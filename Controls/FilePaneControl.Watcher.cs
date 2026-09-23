using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace R2Cmd.Controls;

// =============================================================================
// Auto-update of the current folder via FileSystemWatcher.
//
// Changes are STREAMED into the list, never dumped into it.
//
//   1. One pending operation per path. Raw events (a single copied file fires
//      Created plus several Changed) are folded into one entry per path, kept
//      in arrival order.
//
//   2. The queue is drained in small chunks, one chunk every ~30 ms, each
//      applied as ordinary single-item Insert / Remove notifications. Rows
//      appear and disappear one after another, the way they did when every
//      burst triggered a reload, but without re-reading the directory, without
//      Reset, and without the cursor jumping.
//
//      An earlier version turned any burst into a full reload rate limited to
//      once per second. It was cheap, but the list moved in visible jerks.
//
//   3. A full reload is the last resort: only when the watcher reports a
//      buffer overflow (events were lost), or when the backlog grows past what
//      streaming can reasonably catch up with. Those reloads are rate limited.
//
//   4. Nothing is thrown away by accident. Changes that arrive while a row is
//      being renamed are kept and applied once the rename box closes.
// =============================================================================
public partial class FilePaneControl
{
    #region Suspension (called by the host around its own operations)

    // While suspended, the operation that writes into this folder owns the list
    // and watcher events are discarded rather than queued.
    private int _updatesSuspended;

    public void SuspendUpdates()
    {
        _updatesSuspended++;
        ClearPendingWatch();
    }

    public void ResumeUpdates()
    {
        if (_updatesSuspended > 0) _updatesSuspended--;
    }

    /// <summary>
    /// Suspends watcher updates until the returned object is disposed.
    /// Intended for <c>using var _ = pane.SuspendUpdatesScope();</c> so an
    /// exception can never leave the pane suspended forever.
    /// Must be created and disposed on the UI thread.
    /// </summary>
    public IDisposable SuspendUpdatesScope()
    {
        SuspendUpdates();
        return new UpdatesSuspension(this);
    }

    private sealed class UpdatesSuspension(FilePaneControl owner) : IDisposable
    {
        private FilePaneControl? _owner = owner;

        public void Dispose()
        {
            // Idempotent: a double Dispose must not resume someone else's suspension
            _owner?.ResumeUpdates();
            _owner = null;
        }
    }

    // Kept for existing callers. Each blocker is handled separately in FlushWatch.
    private bool UpdatesBlocked => _updatesSuspended > 0 || _renamingEntry != null || _isBusy;

    #endregion

    #region State

    private enum WatchOp : byte { Add, Remove, Update }

    // Path carries the most recent spelling seen for the key. The dictionary is
    // case-insensitive, so the key itself keeps the FIRST spelling; a case-only
    // rename would otherwise never reach the list.
    private readonly record struct PendingWatch(string Path, WatchOp Op);

    private readonly System.Threading.Lock _watchLock = new();

    // Guarded by _watchLock. The queue holds keys in arrival order and is kept
    // one-to-one with the dictionary: a key is enqueued when it first enters
    // the dictionary and leaves both at the same time.
    private readonly Dictionary<string, PendingWatch> _pendingWatch = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _pendingOrder = new();
    private bool _watchReload;
    private bool _watchScheduled;

    // UI thread only
    private DispatcherTimer? _watchTimer;
    private long _lastWatchReloadMs;

    // Delay between the first event of a burst and the first visible change
    private const int WatchDebounceMs = 60;

    // Pace of the stream: one chunk per interval
    private const int WatchChunkIntervalMs = 30;

    // Minimum chunk. With a growing backlog the chunk grows too (a tenth of the
    // queue), so a fast copy of tiny files does not leave the list far behind.
    private const int WatchChunkMin = 48;
    private const int WatchChunkMax = 400;

    // Backlog beyond which streaming is abandoned for one full reload
    private const int WatchOverflowLimit = 20000;

    // Full reloads are never closer together than this
    private const int WatchReloadMinIntervalMs = 1000;

    // How often a queue held back by an open rename box is looked at again
    private const int WatchRenamePollMs = 250;

    // Below these sizes a linear scan per event beats building an index
    private const int WatchIndexMinItems = 512;
    private const int WatchIndexMinBatch = 4;

    #endregion

    #region Watcher lifetime

    private void StartWatcher(string path, bool isLocal)
    {
        StopWatcher();
        if (!isLocal || string.IsNullOrEmpty(path)) return;

        // No Directory.Exists pre-check: the FileSystemWatcher constructor already
        // validates the path and throws for a missing one.
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(path)
            {
                // LastWrite is required: a file saved in place with the same
                // size (one letter fixed) raises no Size event, so its date
                // never updated. It used to be left out because a large copy
                // pulses it continuously; with events folded to one entry per
                // path and one stat per chunk, that no longer costs anything.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                InternalBufferSize = 65536
            };

            // Handlers first, then enabling: enabling inside the object
            // initializer left a window where events fired with no handler.
            watcher.Created += OnFsCreated;
            watcher.Deleted += OnFsDeleted;
            watcher.Renamed += OnFsRenamed;
            watcher.Changed += OnFsChanged;
            watcher.Error += OnFsError;

            _watcher = watcher;
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            if (ReferenceEquals(_watcher, watcher)) _watcher = null;
            try { watcher?.Dispose(); } catch { }
        }
    }

    private void StopWatcher()
    {
        ClearPendingWatch();

        var watcher = _watcher;
        if (watcher == null) return;

        _watcher = null;
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch { }
    }

    #endregion

    #region Event intake (thread pool)

    // A callback already in flight when the watcher was replaced still arrives
    // afterwards. Its paths belong to the previous folder, so it is ignored.
    private bool IsCurrentWatcher(object sender) =>
        ReferenceEquals(sender, Volatile.Read(ref _watcher));

    private void OnFsCreated(object sender, FileSystemEventArgs e)
    {
        if (IsCurrentWatcher(sender)) QueueWatch(e.FullPath, WatchOp.Add);
    }

    private void OnFsDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsCurrentWatcher(sender)) QueueWatch(e.FullPath, WatchOp.Remove);
    }

    private void OnFsChanged(object sender, FileSystemEventArgs e)
    {
        if (IsCurrentWatcher(sender)) QueueWatch(e.FullPath, WatchOp.Update);
    }

    private void OnFsRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsCurrentWatcher(sender)) return;

        QueueWatch(e.OldFullPath, WatchOp.Remove);
        QueueWatch(e.FullPath, WatchOp.Add);
    }

    // Buffer overflow: individual events were lost, only a full read is reliable
    private void OnFsError(object sender, ErrorEventArgs e)
    {
        if (IsCurrentWatcher(sender)) QueueReload();
    }

    private void QueueWatch(string path, WatchOp op)
    {
        lock (_watchLock)
        {
            // A full reload is already pending and will cover this change
            if (!_watchReload) CoalesceWatch(path, op);

            if (_watchScheduled) return;
            _watchScheduled = true;
        }

        PostFirstFlush();
    }

    private void QueueReload()
    {
        lock (_watchLock)
        {
            _watchReload = true;
            _pendingWatch.Clear();
            _pendingOrder.Clear();

            if (_watchScheduled) return;
            _watchScheduled = true;
        }

        PostFirstFlush();
    }

    private void PostFirstFlush() =>
        Dispatcher.InvokeAsync(() => ScheduleFlush(WatchDebounceMs), DispatcherPriority.Background);

    // Caller holds _watchLock
    private void CoalesceWatch(string path, WatchOp op)
    {
        if (_pendingWatch.TryGetValue(path, out var prev))
        {
            WatchOp merged = op switch
            {
                WatchOp.Remove => WatchOp.Remove,
                WatchOp.Add => WatchOp.Add,

                // Update after Add stays Add; Update after Remove means the file
                // came back, which is an Add too
                _ => prev.Op == WatchOp.Update ? WatchOp.Update : WatchOp.Add
            };

            // Same key, same place in the queue; only the spelling and the
            // operation are refreshed
            _pendingWatch[path] = new PendingWatch(path, merged);
            return;
        }

        _pendingWatch[path] = new PendingWatch(path, op);
        _pendingOrder.Enqueue(path);

        // Streaming cannot catch up with a backlog this large in reasonable
        // time; one full read is the better deal
        if (_pendingWatch.Count > WatchOverflowLimit)
        {
            _watchReload = true;
            _pendingWatch.Clear();
            _pendingOrder.Clear();
        }
    }

    #endregion

    #region Flush (UI thread)

    private void ScheduleFlush(int delayMs)
    {
        if (_watchTimer == null)
        {
            // Background priority: applying file changes must never delay input
            _watchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _watchTimer.Tick += (_, _) =>
            {
                _watchTimer!.Stop();
                FlushWatch();
            };
        }

        _watchTimer.Stop();
        _watchTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
        _watchTimer.Start();
    }

    private void ClearPendingWatch()
    {
        lock (_watchLock)
        {
            _pendingWatch.Clear();
            _pendingOrder.Clear();
            _watchReload = false;
            _watchScheduled = false;
        }

        _watchTimer?.Stop();
    }

    private void FlushWatch()
    {
        // An operation owns the list: whatever it did is its own business
        if (_updatesSuspended > 0)
        {
            ClearPendingWatch();
            return;
        }

        // A navigation is replacing the list right now, and its directory read
        // already sees everything these events describe
        if (_isBusy)
        {
            ClearPendingWatch();
            return;
        }

        // Mutating the list under an open rename box would move the row out from
        // under it. The queue is kept (_watchScheduled stays true, so new events
        // keep folding into it) and looked at again shortly.
        if (_renamingEntry != null)
        {
            ScheduleFlush(WatchRenamePollMs);
            return;
        }

        List<PendingWatch>? chunk = null;
        bool reload;
        bool more = false;
        long reloadDelayMs = 0;

        lock (_watchLock)
        {
            reload = _watchReload;

            if (reload)
            {
                long sinceLast = Environment.TickCount64 - _lastWatchReloadMs;
                reloadDelayMs = WatchReloadMinIntervalMs - sinceLast;

                // When deferring, the flags stay set: new events fold into the
                // pending reload instead of scheduling flushes of their own
                if (reloadDelayMs <= 0)
                {
                    _watchReload = false;
                    _watchScheduled = false;
                }
            }
            else
            {
                int pending = _pendingWatch.Count;
                if (pending == 0)
                {
                    _pendingOrder.Clear();
                    _watchScheduled = false;
                    return;
                }

                // A tenth of the backlog per chunk, within limits: small bursts
                // trickle in row by row, a flood is still worked off quickly
                int take = Math.Clamp(pending / 10, WatchChunkMin, WatchChunkMax);
                chunk = new List<PendingWatch>(Math.Min(take, pending));

                while (chunk.Count < take && _pendingOrder.TryDequeue(out var key))
                {
                    if (_pendingWatch.Remove(key, out var item)) chunk.Add(item);
                }

                more = _pendingWatch.Count > 0;

                // Still scheduled while anything is left: new events must not
                // post a second, competing flush
                if (!more)
                {
                    _pendingOrder.Clear();
                    _watchScheduled = false;
                }
            }
        }

        if (reload)
        {
            if (reloadDelayMs > 0) ScheduleFlush((int)reloadDelayMs);
            else _ = ReloadFromWatcherAsync();
            return;
        }

        ApplyWatchChunk(chunk!);

        if (more) ScheduleFlush(WatchChunkIntervalMs);
    }

    private async Task ReloadFromWatcherAsync()
    {
        _lastWatchReloadMs = Environment.TickCount64;

        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            // Fire-and-forget: without this the exception would go unobserved
            StatusMessage?.Invoke(this, $"Auto-refresh failed: {ex.Message}");
        }
    }

    // =========================================================================
    // Every change is applied as a single-item Insert or Remove, never as a
    // Reset. A Reset regenerates all visible rows, drops keyboard focus and can
    // clear the selection, which is exactly the jerk this file exists to avoid.
    // The chunk size keeps the number of notifications per frame small.
    // =========================================================================
    private void ApplyWatchChunk(List<PendingWatch> chunk)
    {
        string currentDir = CurrentPath.TrimEnd('\\', '/');
        var index = BuildWatchIndex(chunk.Count);

        var selected = lvFiles.SelectedItem as FileEntry;
        bool hadFocus = lvFiles.IsKeyboardFocusWithin;

        FileEntryComparer? comparer = null;
        List<FileEntry>? added = null;
        bool marksChanged = false;
        bool selectionMoved = false;

        foreach (var (path, op) in chunk)
        {
            switch (op)
            {
                case WatchOp.Remove:
                {
                    var gone = FindWatchEntry(path, index);
                    if (gone == null) break;

                    int slot = Items.IndexOf(gone);
                    if (slot < 0) break;

                    Items.RemoveAt(slot);
                    index?.Remove(path);
                    if (gone.IsMarked) marksChanged = true;

                    // The cursor row itself went away: land on its neighbour,
                    // which is what Explorer and Total Commander do
                    if (ReferenceEquals(gone, selected))
                    {
                        selected = Items.Count > 0 ? Items[Math.Min(slot, Items.Count - 1)] : null;
                        selectionMoved = true;
                    }
                    break;
                }

                case WatchOp.Add:
                case WatchOp.Update:
                {
                    if (!BelongsToDir(path, currentDir)) break;

                    var existing = FindWatchEntry(path, index);

                    if (existing == null)
                    {
                        var entry = BuildLocalEntry(path);
                        if (entry == null) break;

                        comparer ??= new FileEntryComparer(SortColumn, SortAscending);
                        InsertSorted(entry, comparer);

                        if (index != null) index[path] = entry;
                        (added ??= new List<FileEntry>()).Add(entry);
                        break;
                    }

                    // The lookup is case-insensitive, so a case-only rename lands
                    // here with the old spelling still on the row. Name is not a
                    // notifying property, so the row is replaced, not edited.
                    if (!string.Equals(existing.Name, Path.GetFileName(path), StringComparison.Ordinal))
                    {
                        var replacement = ReplaceWatchEntry(existing, path);
                        if (replacement == null) break;

                        if (index != null) index[path] = replacement;
                        if (ReferenceEquals(selected, existing))
                        {
                            selected = replacement;
                            selectionMoved = true;
                        }
                        if (replacement.Icon == null) (added ??= new List<FileEntry>()).Add(replacement);
                        break;
                    }

                    if (TryReadLocalStat(path, out long? size, out DateTime modified))
                        ApplyStat(existing, size, modified, ref comparer);
                    break;
                }
            }
        }

        if (selectionMoved && selected != null)
            SetSelectedItem(selected, takeFocus: hadFocus);

        if (marksChanged) UpdateMarkedStatus();

        if (added != null)
            IconService.QueueLoad(added, virtualEntries: false, _requestId, () => _requestId, Dispatcher);
    }

    #endregion

    #region Helpers

    // Full path -> row, built only when both the folder and the chunk are big
    // enough for per-event linear scans to add up
    private Dictionary<string, FileEntry>? BuildWatchIndex(int chunkCount)
    {
        if (Items.Count < WatchIndexMinItems || chunkCount < WatchIndexMinBatch) return null;

        var index = new Dictionary<string, FileEntry>(Items.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Items.Count; i++)
        {
            var entry = Items[i];
            if (entry.Name != "..") index[entry.FullPath] = entry;
        }
        return index;
    }

    private FileEntry? FindWatchEntry(string path, Dictionary<string, FileEntry>? index)
    {
        if (index != null) return index.TryGetValue(path, out var hit) ? hit : null;

        for (int i = 0; i < Items.Count; i++)
        {
            var entry = Items[i];
            if (entry.Name != ".." &&
                string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    // Swaps a row for a freshly read one in the same slot, keeping everything
    // the user or the app attached to the old row
    private FileEntry? ReplaceWatchEntry(FileEntry existing, string path)
    {
        var replacement = BuildLocalEntry(path);
        if (replacement == null) return null;

        replacement.IsMarked = existing.IsMarked;
        replacement.IsCut = existing.IsCut;
        replacement.Icon = existing.Icon;

        if (replacement.IsFolder && existing.SizeKnown)
        {
            replacement.Size = existing.Size;
            replacement.SizeKnown = true;
        }

        int slot = Items.IndexOf(existing);
        if (slot < 0) return null;

        Items[slot] = replacement;
        return replacement;
    }

    /// <summary>
    /// Updates the size and date of the row showing <paramref name="fullPath"/>,
    /// if this pane shows it. For changes the directory watcher cannot see:
    /// remote folders have no watcher, so after the application itself uploads
    /// a file over SSH the host passes the fresh values in here.
    /// Must be called on the UI thread.
    /// </summary>
    public void ApplyEntryStat(string fullPath, long size, DateTime modified)
    {
        // A navigation in flight is about to replace the list anyway
        if (_isBusy || string.IsNullOrEmpty(fullPath)) return;

        // Remote file systems are case-sensitive: "a.txt" and "A.txt" are two files
        var comparison = fullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        for (int i = 0; i < Items.Count; i++)
        {
            var entry = Items[i];
            if (entry.Name == ".." || !string.Equals(entry.FullPath, fullPath, comparison)) continue;

            FileEntryComparer? comparer = null;
            ApplyStat(entry, size, modified, ref comparer);
            return;
        }
    }

    // =========================================================================
    // The one place where a row takes a new size and date — from a watcher
    // event (local) or from ApplyEntryStat (SSH).
    //
    // size is null for folders: their size is only ever set by the folder size
    // calculation, never by a stat. Their date is refreshed, though: creating
    // or deleting something inside a subfolder changes its last write time.
    //
    // When the list is sorted by size or date, the row may no longer be in its
    // slot and is moved. The comparer is passed by ref so a chunk of watcher
    // updates builds it at most once, and only if a move is actually needed.
    // =========================================================================
    private void ApplyStat(FileEntry entry, long? size, DateTime modified, ref FileEntryComparer? comparer)
    {
        bool changed = false;

        if (size is long length && !entry.IsFolder && entry.Size != length)
        {
            entry.Size = length;
            changed = true;
        }

        if (entry.Modified != modified)
        {
            entry.Modified = modified;
            changed = true;
        }

        // Name and Extension ordering cannot be affected by a size or date change
        if (!changed || SortColumn is not ("Size" or "Modified")) return;

        comparer ??= new FileEntryComparer(SortColumn, SortAscending);
        RepositionIfUnsorted(entry, comparer);
    }

    // One stat call. FileInfo works for directories as well; only Length
    // refuses them, which is why size comes back null for a folder.
    // The time is local, the same kind the directory listing produces.
    private static bool TryReadLocalStat(string path, out long? size, out DateTime modified)
    {
        size = null;
        modified = default;

        try
        {
            var info = new FileInfo(path);
            FileAttributes attrs = info.Attributes;
            if ((int)attrs == -1) return false;

            if ((attrs & FileAttributes.Directory) == 0) size = info.Length;
            modified = info.LastWriteTime;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // =========================================================================
    // Moves a row whose sort key changed to its correct slot.
    //
    // ObservableCollection.Move raises a single Move notification: the row
    // container is kept, and so are the selection and the keyboard focus if the
    // row is the cursor row. A Remove + Insert would have dropped both.
    // =========================================================================
    private void RepositionIfUnsorted(FileEntry entry, FileEntryComparer comparer)
    {
        int index = Items.IndexOf(entry);
        if (index < 0) return;

        int first = (Items.Count > 0 && Items[0].Name == "..") ? 1 : 0;

        bool beforeOk = index <= first || comparer.Compare(Items[index - 1], entry) <= 0;
        bool afterOk = index >= Items.Count - 1 || comparer.Compare(entry, Items[index + 1]) <= 0;
        if (beforeOk && afterOk) return;

        // Binary search over the list as if the row had been taken out of it.
        // The result is an index in that shortened list, which is exactly what
        // Move expects as its target.
        int lo = first;
        int hi = Items.Count - 1;

        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            var probe = Items[mid < index ? mid : mid + 1];

            if (comparer.Compare(probe, entry) <= 0) lo = mid + 1;
            else hi = mid;
        }

        if (lo != index) Items.Move(index, lo);
    }

    // Kept for any caller outside this file
    private bool BelongsToCurrentDir(string fullPath) =>
        BelongsToDir(fullPath, CurrentPath.TrimEnd('\\', '/'));

    // Span based: GetDirectoryName on a string allocated a new string per event
    private static bool BelongsToDir(string fullPath, string normalizedDir)
    {
        var parent = Path.GetDirectoryName(fullPath.AsSpan()).TrimEnd('\\');
        return parent.Equals(normalizedDir.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    // Kept for any caller outside this file
    private void InsertSorted(FileEntry entry) =>
        InsertSorted(entry, new FileEntryComparer(SortColumn, SortAscending));

    // Binary insert with the pane comparer. ".." stays at index 0.
    // The comparer is passed in so a chunk of inserts shares one instance.
    private void InsertSorted(FileEntry entry, FileEntryComparer comparer)
    {
        int lo = (Items.Count > 0 && Items[0].Name == "..") ? 1 : 0;
        int hi = Items.Count;

        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (comparer.Compare(Items[mid], entry) <= 0) lo = mid + 1;
            else hi = mid;
        }

        Items.Insert(lo, entry);
    }

    // =========================================================================
    // One GetFileAttributesEx call per entry.
    //
    // The previous version asked Directory.Exists, then File.Exists, then built a
    // DirectoryInfo or FileInfo that read the attributes a third time. A FileInfo
    // reads the attribute data once, works for directories too (only Length
    // refuses them), and reports a missing path as attributes of -1.
    // =========================================================================
    private static FileEntry? BuildLocalEntry(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            FileAttributes attrs = info.Attributes;
            if ((int)attrs == -1) return null;

            bool isDir = (attrs & FileAttributes.Directory) != 0;

            return new FileEntry
            {
                Name = info.Name,
                FullPath = fullPath,
                IsFolder = isDir,
                Size = isDir ? 0 : info.Length,
                Modified = info.LastWriteTime,
                IsHidden = (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0,
                IsSymlink = (attrs & FileAttributes.ReparsePoint) != 0
            };
        }
        catch
        {
            // Gone between the event and now, or no access: nothing to show
            return null;
        }
    }

    #endregion
}
