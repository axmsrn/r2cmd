using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using R2Cmd.Controls;

namespace R2Cmd;

public partial class MainWindow
{
    // Read-only / hidden / system entries refuse to delete, so strip the flags and retry.
    // Directories keep their Directory flag, which is why they are handled separately.
    private static void ClearBlockingAttributes(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                var di = new DirectoryInfo(path);
                di.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            }
            else
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
        catch { }
    }

    // =========================================================================
    // AttributesToSkip MUST be 0 in every one of these.
    //
    // Its default value is Hidden|System, so desktop.ini, Thumbs.db and any hidden
    // file are invisible to the enumerator. That under-counts sizes and progress,
    // and it makes recursive delete fail outright: the files are never removed and
    // Directory.Delete then throws "The directory is not empty".
    //
    // Reparse points are a separate concern: EnumerationOptions has no way to stop
    // recursion at a junction, so every recursive walk below sets its own
    // ShouldRecursePredicate. Without it a junction is followed, which inflates
    // folder sizes and — far worse — deletes the contents of the link target.
    // =========================================================================
    private static readonly EnumerationOptions s_recursiveEnumOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true
    };

    private static readonly EnumerationOptions s_flatEnumOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true
    };

    private static bool IsReparse(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;

    // =========================================================================
    // Counts how many real files a selected entry represents, so the status bar
    // can report "1234 / 7348 files" instead of "1 item" for a whole folder.
    // Reparse points are never walked; an empty folder still counts as one unit.
    //
    // FileSystemEnumerable rather than DirectoryInfo.EnumerateFiles: the latter
    // builds a full FileInfo per file only to be thrown away one line later.
    // =========================================================================
    private static int CountFilesForDelete(string path, bool isFolder, CancellationToken token)
    {
        if (path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var (files, _) = Providers.SshFileSystemProvider.RemoteSumTree(path);
                return Math.Max(1, files);
            }
            catch { return 1; }
        }

        if (!isFolder) return 1;

        try
        {
            var di = new DirectoryInfo(path);
            if (IsReparse(di.Attributes)) return 1;

            int count = 0;

            var files = new FileSystemEnumerable<byte>(
                path,
                (ref FileSystemEntry entry) => 0,
                s_recursiveEnumOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
                ShouldRecursePredicate = (ref FileSystemEntry e) => !IsReparse(e.Attributes)
            };

            foreach (var _ in files)
            {
                // Instant cancellation so ESC works during huge folder scans
                token.ThrowIfCancellationRequested();
                count++;
            }

            return Math.Max(1, count);
        }
        catch (OperationCanceledException) { throw; }
        catch { return 1; }
    }

    // What the delete loops do with a file they could not remove
    private enum FailureAction { Retry, Skip, Abort }

    // Called from background and STA threads; shows the dialog on the UI thread
    private delegate FailureAction FailureHandler(string path, string reason);

    // The dialog names ten of them; collecting a hundred thousand strings from a
    // locked build folder would cost more than the operation itself
    private const int MaxCollectedErrors = 200;

    // =========================================================================
    // ONE STA THREAD PER OPERATION
    //
    // SHFileOperation needs an STA apartment. Spawning a fresh thread per chunk
    // meant thousands of thread creations plus COM initialisation on a large
    // delete, and the cost showed up as a visible stall between chunks. A single
    // worker for the whole operation does the same job.
    // =========================================================================
    private sealed class StaWorker : IDisposable
    {
        private readonly BlockingCollection<(Action Work, TaskCompletionSource<bool> Done)> _queue = new();
        private readonly Thread _thread;

        public StaWorker()
        {
            _thread = new Thread(() =>
            {
                foreach (var (work, done) in _queue.GetConsumingEnumerable())
                {
                    try { work(); done.TrySetResult(true); }
                    catch (Exception ex) { done.TrySetException(ex); }
                }
            })
            { IsBackground = true };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public Task RunAsync(Action work)
        {
            var done = new TaskCompletionSource<bool>();
            _queue.Add((work, done));
            return done.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
        }
    }

    // =========================================================================
    // DELETE
    //
    // Windows treats a directory as one object. If a single file deep inside is
    // held by another process, SHFileOperation refuses the whole tree, the
    // hundreds of unlocked files around it stay on disk, and the error names the
    // top folder rather than the file actually holding the lock.
    //
    // Both delete paths therefore walk the tree themselves and ask what to do
    // with each file they cannot remove: Retry after closing the program that
    // holds it, Skip this one, Skip all — take everything that is free and leave
    // the rest — or Cancel the whole operation. Whatever is left behind is listed
    // by full path at the end.
    // =========================================================================
    private async Task DoDeleteAsync(bool permanent, bool skipConfirm = false, List<FileEntry>? itemsOverride = null)
    {
        var sourcePane = _activePane;
        var items = itemsOverride ?? sourcePane.SelectedItems.ToList();
        if (items.Count == 0) return;

        bool anySsh = items.Any(i => i.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase));

        if (!skipConfirm && !ConfirmDelete(items.Select(i => i.Name), permanent, anySsh)) return;

        string startingPath = sourcePane.CurrentPath;

        // Sort items properly
        var itemIndices = new Dictionary<FileEntry, int>();
        for (int i = 0; i < sourcePane.Items.Count; i++)
        {
            itemIndices[sourcePane.Items[i]] = i;
        }
        items = items.OrderBy(x => itemIndices.TryGetValue(x, out int idx) ? idx : int.MaxValue).ToList();

        bool finished = false; // set once the delete loop is over, checked by queued UI updates
        int okItems = 0;       // top level entries removed
        int deletedFiles = 0;  // real files removed, shown in the status bar
        int totalFiles = 0;    // result of the recursive pre-scan
        bool skipAllFailures = false;
        var errors = new List<string>();

        // The list keeps at most MaxCollectedErrors paths for the dialog; the
        // real number of failures is counted separately. The summary used to
        // report errors.Count, so 5000 failures showed up as "Failed: 200".
        int failedCount = 0;
        var fileCounts = new Dictionary<FileEntry, int>();
        string? itemToRestore = null;

        var deletedSet = new HashSet<FileEntry>(items);
        var ordered = sourcePane.Items;
        int firstDeletedIndex = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (deletedSet.Contains(ordered[i])) { firstDeletedIndex = i; break; }
        }

        if (firstDeletedIndex > 0)
        {
            itemToRestore = ordered[firstDeletedIndex - 1].Name;
        }

        if (string.IsNullOrEmpty(itemToRestore) && firstDeletedIndex >= 0)
        {
            for (int i = firstDeletedIndex + 1; i < ordered.Count; i++)
            {
                if (!deletedSet.Contains(ordered[i]))
                {
                    itemToRestore = ordered[i].Name;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(itemToRestore) && ordered.Any(x => x.Name == ".."))
        {
            itemToRestore = "..";
        }

        var totalTimeStopwatch = System.Diagnostics.Stopwatch.StartNew();
        IsStatusLocked = true;

        using var cts = new CancellationTokenSource();
        using var sta = new StaWorker();

        KeyEventHandler cancelHandler = (s, e) =>
        {
            if (e.Key == Key.Escape && !cts.IsCancellationRequested)
            {
                if (e.OriginalSource is TextBox) return;

                if (sourcePane.CurrentPath == startingPath && _activePane == sourcePane)
                {
                    cts.Cancel();
                    e.Handled = true;
                    SetStatus("Canceling deletion... Please wait.", forceUpdate: true);
                }
            }
        };

        // Hooked before the scan so ESC can also abort counting
        this.PreviewKeyDown += cancelHandler;

        // =====================================================================
        // The single place that decides what happens to a file that would not go.
        // Runs on whichever thread hit the failure and marshals the dialog to the
        // UI thread, which is free at that moment: the operation is awaited, not
        // blocking it.
        // =====================================================================
        FailureAction OnFailure(string path, string reason)
        {
            if (cts.IsCancellationRequested) return FailureAction.Abort;

            if (!skipAllFailures)
            {
                var choice = ErrorChoice.SkipAll;

                Dispatcher.Invoke(() =>
                {
                    string message =
                        $"{path}{Environment.NewLine}{Environment.NewLine}" +
                        $"{reason}{Environment.NewLine}{Environment.NewLine}" +
                        "It is usually open in another program. Close it and press Retry, " +
                        "or skip it and finish the rest.";

                    var dialog = new ErrorActionDialog(
                        message,
                        permanent ? "Cannot delete" : "Cannot move to Recycle Bin",
                        allowRetry: true,
                        focusSkipAll: true)
                    { Owner = this };

                    dialog.ShowDialog();
                    choice = dialog.Choice;
                });

                switch (choice)
                {
                    case ErrorChoice.Retry:
                        return FailureAction.Retry;

                    case ErrorChoice.SkipAll:
                        skipAllFailures = true;
                        break;

                    case ErrorChoice.Cancel:
                        cts.Cancel();
                        return FailureAction.Abort;
                }
            }

            lock (errors)
            {
                failedCount++;
                if (errors.Count < MaxCollectedErrors) errors.Add($"{path}: {reason}");
            }

            return FailureAction.Skip;
        }

        try
        {
            // ---------------------------------------------------------------------
            // PASS 1: count files so progress can be reported per file
            //
            // Skipped entirely for a selection of plain local files: the count is
            // then the number of items, and the scan machinery costs more than the
            // delete itself.
            // ---------------------------------------------------------------------
            bool needsScan = items.Any(i =>
                i.IsFolder || i.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase));

            if (!needsScan)
            {
                foreach (var item in items) fileCounts[item] = 1;
                totalFiles = items.Count;
            }
            else
            {
                SetStatus("Counting files to delete... [ESC to cancel]", forceUpdate: true);

                try
                {
                    await Task.Run(() =>
                    {
                        foreach (var item in items)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            int n = CountFilesForDelete(item.FullPath, item.IsFolder, cts.Token);
                            fileCounts[item] = n;
                            totalFiles += n;
                        }
                    }, cts.Token);
                }
                catch (OperationCanceledException) { }
            }

            if (cts.IsCancellationRequested)
            {
                SetStatus("Deletion canceled.");
                return;
            }

            SetStatus(permanent
                ? $"Deleting {totalFiles} file(s)... [ESC to cancel]"
                : $"Moving {totalFiles} file(s) to Recycle Bin... [ESC to cancel]", forceUpdate: true);

            // ---------------------------------------------------------------------
            // PASS 2: delete
            // ---------------------------------------------------------------------

            // Thread-safe unbounded channel for passing deleted entries to the UI thread
            var uiChannel = System.Threading.Channels.Channel.CreateUnbounded<FileEntry>(
                new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

            // UI DispatcherTimer handles batch updates without flooding the Dispatcher queue
            var uiTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(66)
            };

            uiTimer.Tick += (s, e) =>
            {
                while (uiChannel.Reader.TryRead(out var rm))
                {
                    if (sourcePane.CurrentPath == startingPath) sourcePane.Items.Remove(rm);
                }

                if (!Volatile.Read(ref finished) && !cts.IsCancellationRequested)
                {
                    int currentFiles = Volatile.Read(ref deletedFiles);
                    string timeElapsed = $"{totalTimeStopwatch.Elapsed.TotalSeconds:0.000}s";
                    bool isForeground = sourcePane.CurrentPath == startingPath && _activePane == sourcePane;
                    string escHint = isForeground ? " [ESC to cancel]" : " (Background)";

                    SetStatus(permanent
                        ? $"Deleting... ({currentFiles} / {totalFiles} files) — Time: {timeElapsed}{escHint}"
                        : $"Moving to Recycle Bin... ({currentFiles} / {totalFiles} files) — Time: {timeElapsed}{escHint}", forceUpdate: true);
                }
            };

            uiTimer.Start();

            await Task.Run(async () =>
            {
                const int ChunkSize = 25;
                bool canceled = false;

                void MarkRemoved(FileEntry entry)
                {
                    Interlocked.Increment(ref okItems);
                    uiChannel.Writer.TryWrite(entry);
                }

                void CountFilesOf(FileEntry entry)
                {
                    Interlocked.Add(ref deletedFiles, fileCounts.TryGetValue(entry, out int n) ? n : 1);
                }

                var recycleBatch = new List<FileEntry>(ChunkSize);

                for (int i = 0; i < items.Count && !canceled;)
                {
                    if (cts.Token.IsCancellationRequested) break;

                    int end = Math.Min(i + ChunkSize, items.Count);
                    recycleBatch.Clear();

                    for (; i < end; i++)
                    {
                        var item = items[i];

                        if (cts.Token.IsCancellationRequested) { canceled = true; break; }

                        bool isSsh = item.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);

                        if (!permanent && !isSsh)
                        {
                            recycleBatch.Add(item);
                            continue;
                        }

                        bool itemSuccess = true;

                        if (isSsh)
                        {
                            while (true)
                            {
                                try
                                {
                                    Providers.SshFileSystemProvider.RemoteDelete(item.FullPath);
                                    CountFilesOf(item);
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    var action = OnFailure(item.FullPath, ex.Message);
                                    if (action == FailureAction.Retry) continue;
                                    if (action == FailureAction.Abort) canceled = true;
                                    itemSuccess = false;
                                    break;
                                }
                            }
                        }
                        else if (item.IsFolder)
                        {
                            try
                            {
                                Win32FastDeleteDirectory(item.FullPath,
                                    () => Interlocked.Increment(ref deletedFiles),
                                    OnFailure, cts.Token);
                            }
                            catch (OperationCanceledException) { canceled = true; }

                            if (Directory.Exists(item.FullPath)) itemSuccess = false;
                        }
                        else
                        {
                            while (true)
                            {
                                try
                                {
                                    File.Delete(item.FullPath);
                                    Interlocked.Increment(ref deletedFiles);
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    ClearBlockingAttributes(item.FullPath, isDirectory: false);

                                    try
                                    {
                                        File.Delete(item.FullPath);
                                        Interlocked.Increment(ref deletedFiles);
                                        break;
                                    }
                                    catch { }

                                    var action = OnFailure(item.FullPath, ex.Message);
                                    if (action == FailureAction.Retry) continue;
                                    if (action == FailureAction.Abort) canceled = true;
                                    itemSuccess = false;
                                    break;
                                }
                            }
                        }

                        if (itemSuccess) MarkRemoved(item);
                        if (canceled) break;
                    }

                    if (recycleBatch.Count > 0)
                    {
                        var batch = recycleBatch;

                        await sta.RunAsync(() =>
                        {
                            try
                            {
                                if (batch.Count > 1 &&
                                    RecycleHelper.SendToRecycleBin(batch.Select(x => x.FullPath), silent: true))
                                {
                                    foreach (var item in batch)
                                    {
                                        CountFilesOf(item);
                                        MarkRemoved(item);
                                    }
                                    return;
                                }

                                foreach (var item in batch)
                                {
                                    if (cts.Token.IsCancellationRequested) { canceled = true; return; }

                                    if (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath))
                                    {
                                        CountFilesOf(item);
                                        MarkRemoved(item);
                                        continue;
                                    }

                                    bool done = false;

                                    while (!done)
                                    {
                                        if (RecycleHelper.SendToRecycleBin(new[] { item.FullPath }, silent: true))
                                        {
                                            CountFilesOf(item);
                                            MarkRemoved(item);
                                            break;
                                        }

                                        if (item.IsFolder)
                                        {
                                            RecycleTreeContents(item.FullPath, OnFailure,
                                                () => Interlocked.Increment(ref deletedFiles),
                                                cts.Token);

                                            if (!Directory.Exists(item.FullPath)) MarkRemoved(item);
                                            break;
                                        }

                                        var action = OnFailure(item.FullPath, "The file is open in another program or access is denied.");

                                        switch (action)
                                        {
                                            case FailureAction.Retry: continue;
                                            case FailureAction.Abort: canceled = true; done = true; break;
                                            default: done = true; break;
                                        }
                                    }

                                    if (canceled) return;
                                }
                            }
                            catch (OperationCanceledException) { canceled = true; }
                        });
                    }
                }

                Volatile.Write(ref finished, true);
            });

            uiTimer.Stop();

            // Final drain to ensure no items are left in the channel after the task finishes
            while (uiChannel.Reader.TryRead(out var rm))
            {
                if (sourcePane.CurrentPath == startingPath) sourcePane.Items.Remove(rm);
            }
        }
        finally
        {
            this.PreviewKeyDown -= cancelHandler;

            totalTimeStopwatch.Stop();
            IsStatusLocked = false;
        }

        if (sourcePane.CurrentPath == startingPath)
        {
            if (!string.IsNullOrEmpty(itemToRestore))
            {
                var target = sourcePane.Items.FirstOrDefault(x => x.Name.Equals(itemToRestore, StringComparison.OrdinalIgnoreCase));
                if (target != null) sourcePane.SetSelectedItem(target);
                else if (sourcePane.Items.Count > 0) sourcePane.SetSelectedItem(sourcePane.Items[0]);
            }

            await SyncPanesIfSamePath(sourcePane);
            _ = Dispatcher.BeginInvoke(new Action(() => sourcePane.FocusPanel()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // Final operation time in the required format
        string finalTimeElapsed = $"{totalTimeStopwatch.Elapsed.TotalSeconds:0.000}s";

        if (cts.IsCancellationRequested)
        {
            SetStatus($"Deletion canceled. {deletedFiles} out of {totalFiles} file(s) deleted. — Time: {finalTimeElapsed}");
        }
        else
        {
            ShowSummary(permanent ? "Delete" : "Recycle", deletedFiles, okItems, errors, failedCount, finalTimeElapsed);
        }
    }

    // =========================================================================
    // Recycles the contents of a directory the shell refused as a whole.
    //
    // One call per directory level, not per file: a shell operation costs
    // milliseconds and creates its own undo record, so a folder with five
    // thousand files would otherwise mean five thousand calls and a Recycle Bin
    // full of individual entries. Only the level that actually fails is retried
    // file by file — which is also what turns "access denied on obj" into the
    // name of the file holding the lock.
    //
    // Must run on an STA thread.
    // =========================================================================
    private static void RecycleTreeContents(string path, FailureHandler onFailure, Action onFileRecycled, CancellationToken token)
    {
        try
        {
            var files = Directory.GetFiles(path, "*", s_flatEnumOptions);

            if (files.Length > 0)
            {
                if (files.Length > 1 && RecycleHelper.SendToRecycleBin(files, silent: true))
                {
                    for (int i = 0; i < files.Length; i++) onFileRecycled();
                }
                else
                {
                    foreach (var file in files)
                    {
                        token.ThrowIfCancellationRequested();

                        while (true)
                        {
                            if (RecycleHelper.SendToRecycleBin(new[] { file }, silent: true))
                            {
                                onFileRecycled();
                                break;
                            }

                            // Read-only or hidden on its own is enough to be refused,
                            // and clearing that needs no question
                            ClearBlockingAttributes(file, isDirectory: false);

                            if (RecycleHelper.SendToRecycleBin(new[] { file }, silent: true))
                            {
                                onFileRecycled();
                                break;
                            }

                            var action = onFailure(file, "The file is open in another program or access is denied.");

                            if (action == FailureAction.Retry) continue;
                            if (action == FailureAction.Abort) throw new OperationCanceledException();
                            break;
                        }
                    }
                }
            }

            foreach (var dir in Directory.GetDirectories(path, "*", s_flatEnumOptions))
            {
                token.ThrowIfCancellationRequested();

                // A junction is recycled as the link it is. Walking into it would
                // empty the folder it points at.
                if (IsReparse(File.GetAttributes(dir)))
                {
                    RecycleHelper.SendToRecycleBin(new[] { dir }, silent: true);
                    continue;
                }

                RecycleTreeContents(dir, onFailure, onFileRecycled, token);
            }

            // Empty by now unless something inside was left behind
            if (!Directory.EnumerateFileSystemEntries(path).Any())
                RecycleHelper.SendToRecycleBin(new[] { path }, silent: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            onFailure(path, ex.Message);
        }
    }

    // =========================================================================
    // HIGH-PERFORMANCE RECURSIVE DELETE
    //
    // Every file is deleted on its own and a failure asks what to do instead of
    // aborting: one locked file used to take the whole directory down with it and
    // leave the hundreds of unlocked files beside it untouched.
    //
    // One directory read per level, into an array. Two reasons: EnumerateFiles
    // followed by EnumerateDirectories opened the same directory twice, and
    // deleting during a lazy enumeration can make FindNextFile skip entries,
    // which then shows up as an unexplained "directory is not empty".
    //
    // s_flatEnumOptions sets AttributesToSkip = 0. With the default value hidden
    // and system files are never enumerated, never deleted, and the final
    // Directory.Delete fails because the folder still has content in it.
    // =========================================================================
    private static void Win32FastDeleteDirectory(string path, Action? onFileDeleted, FailureHandler onFailure, CancellationToken token = default)
    {
        try
        {
            var entries = new FileSystemEnumerable<(string Path, FileAttributes Attributes, bool IsDirectory)>(
                path,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.Attributes, e.IsDirectory),
                s_flatEnumOptions).ToArray();

            foreach (var entry in entries)
            {
                if (entry.IsDirectory) continue;

                token.ThrowIfCancellationRequested();

                while (true)
                {
                    try
                    {
                        File.Delete(entry.Path);
                        onFileDeleted?.Invoke();
                        break;
                    }
                    catch (Exception ex)
                    {
                        ClearBlockingAttributes(entry.Path, isDirectory: false);

                        try
                        {
                            File.Delete(entry.Path);
                            onFileDeleted?.Invoke();
                            break;
                        }
                        catch { }

                        var action = onFailure(entry.Path, ex.Message);

                        if (action == FailureAction.Retry) continue;
                        if (action == FailureAction.Abort) throw new OperationCanceledException();
                        break;
                    }
                }
            }

            foreach (var entry in entries)
            {
                if (!entry.IsDirectory) continue;

                token.ThrowIfCancellationRequested();

                // A junction or symlink is removed as the link itself. Recursing
                // into it would delete the contents of whatever it points at.
                if (IsReparse(entry.Attributes))
                {
                    try { Directory.Delete(entry.Path, false); }
                    catch (Exception ex) { onFailure(entry.Path, ex.Message); }
                    continue;
                }

                Win32FastDeleteDirectory(entry.Path, onFileDeleted, onFailure, token);
            }

            try { Directory.Delete(path, false); }
            catch (UnauthorizedAccessException)
            {
                try
                {
                    ClearBlockingAttributes(path, isDirectory: true);
                    Directory.Delete(path, false);
                }
                catch { /* not empty: something inside was left behind and reported */ }
            }
            catch { /* not empty: something inside was left behind and reported */ }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            onFailure(path, ex.Message);
        }
    }

    private bool ConfirmDelete(IEnumerable<string> names, bool permanent, bool anySsh)
    {
        var list = names.ToList();
        string preview = string.Join(", ", list.Take(5)) + (list.Count > 5 ? $", +{list.Count - 5} more" : "");
        string title = permanent ? "Confirm Permanent Delete" : "Confirm Delete";
        string action = permanent ? $"Permanently delete {list.Count} item(s)?" : $"Delete {list.Count} item(s) to Recycle Bin?";

        if (anySsh)
            action = $"Permanently delete {list.Count} item(s)?\nRemote (SSH) items cannot be sent to the Recycle Bin and will be deleted permanently.";

        var dlg = new ConfirmDialog($"{action}\n{preview}", title) { Owner = this };
        return dlg.ShowDialog() == true;
    }

    // =========================================================================
    // Final line in the status bar, plus a dialog when something was left behind.
    //
    // Naming what stayed on disk is the whole point of the dialog: the shell
    // reports "access denied" on the top folder, this reports the full path of
    // every file that is actually holding it.
    // =========================================================================
    private void ShowSummary(string action, int files, int items, List<string> errors, int failed, string timeElapsed)
    {
        string done = $"{action}: {files} file(s) in {items} item(s) done";

        if (failed == 0)
        {
            SetStatus($"{done}. \u2014 Time: {timeElapsed}");
            return;
        }

        SetStatus($"{done}, {failed} failed. \u2014 Time: {timeElapsed}");

        const int maxShown = 10;

        string list = string.Join(Environment.NewLine, errors.Take(maxShown).Select(e => $"  - {e}"));

        if (failed > maxShown)
            list += $"{Environment.NewLine}{Environment.NewLine}  ...and {failed - maxShown} more.";

        string message =
            $"{action} completed, but some items were left behind.{Environment.NewLine}{Environment.NewLine}" +
            $"Succeeded: {files}{Environment.NewLine}" +
            $"Failed: {failed}{Environment.NewLine}{Environment.NewLine}" +
            $"Still on disk:{Environment.NewLine}{list}";

        MessageDialog.Show(this, message, $"{action} finished with errors");
    }
}
