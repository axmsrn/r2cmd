// File: MainWindow.Delete.cs | Path: Windows/MainWindow.Delete.cs | Version: 2026-10-19.4
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
            if (IsReparse(File.GetAttributes(path))) return;
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

    // Pace of the row removal during a delete (see RemoveSomeRows)
    private const int RowRemovalIntervalMs = 33;

    // Catch-up share: a backlog is worked off over about this many ticks
    private const int RowRemovalSpread = 6;

    private static bool IsReparse(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;

    // What the delete loops do with a file they could not remove
    private enum FailureAction { Retry, Skip, Abort }

    // Called from background and STA threads; shows the dialog on the UI thread
    private delegate FailureAction FailureHandler(string path, string reason);
    private delegate bool RecycleHandler(string path, out Exception? error);

    // File.Exists/Directory.Exists also return false on access denial. That must
    // never count as successful deletion or remove a protected item from the UI.
    private static bool DeleteEntryExists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch { return true; }
    }

    // The dialog names ten of them; collecting a hundred thousand strings from a
    // locked build folder would cost more than the operation itself
    private const int MaxCollectedErrors = 200;

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
        bool skipAllFailures = false;
        var errors = new List<string>();

        // The list keeps at most MaxCollectedErrors paths for the dialog; the
        // real number of failures is counted separately. The summary used to
        // report errors.Count, so 5000 failures showed up as "Failed: 200".
        int failedCount = 0;
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

        // =====================================================================
        // The folder watcher is paused while this delete owns the list.
        // Otherwise it sees the Recycle Bin take 25 folders in one shell call
        // and removes those rows itself in one chunk, ahead of the paced
        // removal below: that was the remaining jerk. Resumed in finally.
        // =====================================================================
        var watcherPause = sourcePane.SuspendUpdatesScope();

        using var cts = new CancellationTokenSource();
        using var sta = new StaFileOperationWorker();
        var elevatedOperations = new ElevatedFileOperations.Session(cts, request => Dispatcher.Invoke(() =>
        {
            string verb = permanent ? "permanently delete this item" : "move this item to the Recycle Bin";
            var dialog = new ConfirmDialog(
                $"Administrator permission is required for:\n{request.Source}\n\nContinue as administrator to {verb}?",
                "Administrator permission") { Owner = this };
            return dialog.ShowDialog() == true;
        }));

        // The UI remains free while the background delete/STA thread waits.
        void WithDeleteElevation(Action action, string operation, string path) =>
            elevatedOperations.RunAsync(action, new(operation, path, ""), !skipAllFailures).GetAwaiter().GetResult();

        void DeleteLocal(string path, bool directory)
        {
            WithDeleteElevation(() =>
            {
                try
                {
                    if (directory) Directory.Delete(path, recursive: false);
                    else File.Delete(path);
                }
                catch (UnauthorizedAccessException)
                {
                    ClearBlockingAttributes(path, directory);
                    if (directory) Directory.Delete(path, recursive: false);
                    else File.Delete(path);
                }
            }, directory ? "DeleteDirectory" : "DeleteFile", path);
        }

        bool TryRecycleLocal(string path, out Exception? error)
        {
            error = null;
            try
            {
                WithDeleteElevation(() =>
                {
                    if (RecycleHelper.SendToRecycleBin(new[] { path }, silent: true, out var failure)) return;
                    // Read-only attributes can be cleared without elevating.
                    ClearBlockingAttributes(path, Directory.Exists(path));
                    if (!RecycleHelper.SendToRecycleBin(new[] { path }, silent: true, out failure))
                        throw failure ?? new IOException("Cannot move this item to the Recycle Bin.");
                }, "Recycle", path);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { error = ex; return false; }
        }

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
                        "Resolve the problem and press Retry, or skip this item and finish the rest.";

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
            // =====================================================================
            // No counting pass. It walked the whole tree once just to know the
            // total for a progress bar, and on node_modules that walk took longer
            // than the delete itself: a recycle is one shell call per batch, and
            // a permanent delete reads the tree anyway. Progress now counts the
            // selected items; within one big folder the deleted-file counter
            // shows that work is going on.
            // =====================================================================
            int totalItems = items.Count;

            SetStatus(permanent
                ? $"Deleting {totalItems} item(s)... [ESC to cancel]"
                : $"Moving {totalItems} item(s) to Recycle Bin... [ESC to cancel]", forceUpdate: true);

            // Several items: the bar fills item by item. A single folder: its size
            // is unknown without the scan, so the bar just shows activity.
            // A single file is gone before a bar could be seen.
            bool showBar = totalItems > 1 || items[0].IsFolder;
            deleteProgress.IsIndeterminate = totalItems == 1;
            deleteProgress.Value = 0;
            deleteProgressItem.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;

            // ---------------------------------------------------------------------
            // PASS 2: delete
            // ---------------------------------------------------------------------

            // Deleted entries on their way to the UI thread. A plain concurrent
            // queue: its Count is always supported, unlike a single-reader
            // Channel, whose Count throws NotSupportedException
            // ("Specified method is not supported").
            var removedRows = new ConcurrentQueue<FileEntry>();

            // UI DispatcherTimer handles batch updates without flooding the Dispatcher queue
            var uiTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(RowRemovalIntervalMs)
            };

            // =================================================================
            // Rows leave the list at a steady pace, not in bursts.
            //
            // The Recycle Bin gets items in batches of 25 per shell call (one
            // call each would be far slower), so they are reported done 25 at a
            // time. Removing them all in one tick made the list jump every batch.
            // Each tick now removes only a share of what is waiting; the deletion
            // itself is not slowed down at all, only the display is spread out.
            // =================================================================
            // =================================================================
            // Rows leave at the AVERAGE speed of the delete, not when a batch
            // lands. A recycle batch of 25 folders reports nothing while the
            // shell works and then all 25 at once; spreading them over a fixed
            // 200 ms still left "burst, pause, burst". Paced by the measured
            // rate, the list moves continuously, about one batch behind the
            // disk. A growing backlog is caught up so it never lags further.
            // =================================================================
            var paceClock = System.Diagnostics.Stopwatch.StartNew();
            double rowBudget = 0;

            void RemoveSomeRows()
            {
                int waiting = removedRows.Count;
                if (waiting == 0) { rowBudget = 0; return; }

                double elapsedMs = Math.Max(1, paceClock.Elapsed.TotalMilliseconds);
                int done = Volatile.Read(ref okItems);
                bool deleteRunning = !Volatile.Read(ref finished);

                // Rows per tick at the speed the delete has averaged so far
                rowBudget += done * RowRemovalIntervalMs / elapsedMs;

                // Catch up when more than two batches wait, and finish briskly
                // once the delete itself is over
                if (waiting > 50 || !deleteRunning) rowBudget += waiting / (double)RowRemovalSpread;

                int take = Math.Min(waiting, (int)rowBudget);
                if (take == 0) return;
                rowBudget -= take;

                for (int n = 0; n < take && removedRows.TryDequeue(out var rm); n++)
                {
                    if (sourcePane.CurrentPath == startingPath) sourcePane.Items.Remove(rm);
                }
            }

            uiTimer.Tick += (s, e) =>
            {
                // A display problem must never surface as an error dialog in
                // the middle of a delete; it is reported in the status bar with
                // the method that failed, so it can be traced
                try
                {
                    RemoveSomeRows();
                }
                catch (Exception ex)
                {
                    SetStatus($"Row update error: {ex.GetType().Name} in {ex.TargetSite?.DeclaringType?.Name}.{ex.TargetSite?.Name}: {ex.Message}", forceUpdate: true);
                }

                if (!Volatile.Read(ref finished) && !cts.IsCancellationRequested)
                {
                    int currentFiles = Volatile.Read(ref deletedFiles);
                    int currentItems = Volatile.Read(ref okItems);
                    if (totalItems > 1) deleteProgress.Value = Math.Min(100, currentItems * 100.0 / totalItems);
                    string timeElapsed = $"{totalTimeStopwatch.Elapsed.TotalSeconds:0.000}s";
                    bool isForeground = sourcePane.CurrentPath == startingPath && _activePane == sourcePane;
                    string escHint = isForeground ? " [ESC to cancel]" : " (Background)";

                    SetStatus(permanent
                        ? $"Deleting... {currentItems} / {totalItems} item(s), {currentFiles} file(s) — Time: {timeElapsed}{escHint}"
                        : $"Moving to Recycle Bin... {currentItems} / {totalItems} item(s) — Time: {timeElapsed}{escHint}", forceUpdate: true);
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
                    removedRows.Enqueue(entry);
                }

                // Without a pre-scan the files inside a folder removed in one call
                // (a recycle, an SSH rm -r) are not known; the item counts as one
                void CountFilesOf(FileEntry entry) => Interlocked.Increment(ref deletedFiles);

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
                                    OnFailure, cts.Token, DeleteLocal);
                            }
                            catch (OperationCanceledException) { canceled = true; }

                            if (DeleteEntryExists(item.FullPath)) itemSuccess = false;
                        }
                        else
                        {
                            while (true)
                            {
                                try
                                {
                                    DeleteLocal(item.FullPath, false);
                                    Interlocked.Increment(ref deletedFiles);
                                    break;
                                }
                                catch (OperationCanceledException) { canceled = true; itemSuccess = false; break; }
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

                                    if (!DeleteEntryExists(item.FullPath))
                                    {
                                        CountFilesOf(item);
                                        MarkRemoved(item);
                                        continue;
                                    }

                                    bool done = false;

                                    while (!done)
                                    {
                                        if (TryRecycleLocal(item.FullPath, out var recycleError))
                                        {
                                            CountFilesOf(item);
                                            MarkRemoved(item);
                                            break;
                                        }

                                        if (item.IsFolder)
                                        {
                                            RecycleTreeContents(item.FullPath, OnFailure,
                                                () => Interlocked.Increment(ref deletedFiles),
                                                cts.Token, TryRecycleLocal);

                                            if (!DeleteEntryExists(item.FullPath)) MarkRemoved(item);
                                            break;
                                        }

                                        var action = OnFailure(item.FullPath, recycleError?.Message ?? "Cannot move this item to the Recycle Bin.");

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

            // The rows still waiting leave at the same pace instead of all at
            // once at the end; the timer keeps running until they are gone
            while (!removedRows.IsEmpty && sourcePane.CurrentPath == startingPath)
                await Task.Delay(RowRemovalIntervalMs);

            uiTimer.Stop();

            // Whatever is left (the user navigated away meanwhile)
            while (removedRows.TryDequeue(out var rm))
            {
                if (sourcePane.CurrentPath == startingPath) sourcePane.Items.Remove(rm);
            }
        }
        finally
        {
            elevatedOperations.Dispose();
            this.PreviewKeyDown -= cancelHandler;

            deleteProgressItem.Visibility = Visibility.Collapsed;
            deleteProgress.IsIndeterminate = false;   // stops its animation timer

            totalTimeStopwatch.Stop();
            IsStatusLocked = false;
            watcherPause.Dispose();
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
            SetStatus($"Deletion canceled. {okItems} out of {items.Count} item(s) deleted. — Time: {finalTimeElapsed}");
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
    private static void RecycleTreeContents(string path, FailureHandler onFailure, Action onFileRecycled,
        CancellationToken token, RecycleHandler recycle)
    {
        bool RecycleEntry(string entry)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!DeleteEntryExists(entry) || recycle(entry, out var error)) return true;
                var action = onFailure(entry, error?.Message ?? "Cannot move this item to the Recycle Bin.");
                if (action == FailureAction.Retry) continue;
                if (action == FailureAction.Abort) throw new OperationCanceledException(token);
                return false;
            }
        }

        try
        {
            token.ThrowIfCancellationRequested();
            if (!DeleteEntryExists(path)) return;
            // The selected root can itself be a junction, not only a child.
            if (IsReparse(File.GetAttributes(path)))
            {
                RecycleEntry(path);
                return;
            }

            var files = Directory.GetFiles(path, "*", s_flatEnumOptions);
            if (files.Length > 1 && RecycleHelper.SendToRecycleBin(files, silent: true))
            {
                for (int i = 0; i < files.Length; i++) onFileRecycled();
            }
            else
            {
                foreach (var file in files)
                    if (RecycleEntry(file)) onFileRecycled();
            }

            foreach (var dir in Directory.GetDirectories(path, "*", s_flatEnumOptions))
            {
                token.ThrowIfCancellationRequested();
                // Recycle a junction itself without walking into its target.
                if (IsReparse(File.GetAttributes(dir))) RecycleEntry(dir);
                else RecycleTreeContents(dir, onFailure, onFileRecycled, token, recycle);
            }

            if (!Directory.EnumerateFileSystemEntries(path).Any()) RecycleEntry(path);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var action = onFailure(path, ex.Message);
            if (action == FailureAction.Abort) throw new OperationCanceledException(token);
            if (action == FailureAction.Retry) RecycleTreeContents(path, onFailure, onFileRecycled, token, recycle);
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
    private static void Win32FastDeleteDirectory(string path, Action? onFileDeleted, FailureHandler onFailure,
        CancellationToken token, Action<string, bool> deleteLocal)
    {
        bool DeleteEntry(string entry, bool directory, bool reportedChildren = false)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    deleteLocal(entry, directory);
                    if (!directory) onFileDeleted?.Invoke();
                    return true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A skipped child has already been reported by its full path.
                    if (reportedChildren && ex.HResult == unchecked((int)0x80070091)) return false;
                    var action = onFailure(entry, ex.Message);
                    if (action == FailureAction.Retry) continue;
                    if (action == FailureAction.Abort) throw new OperationCanceledException(token);
                    return false;
                }
            }
        }

        try
        {
            token.ThrowIfCancellationRequested();
            if (!DeleteEntryExists(path)) return;
            if (IsReparse(File.GetAttributes(path)))
            {
                DeleteEntry(path, directory: true);
                return;
            }

            var entries = new FileSystemEnumerable<(string Path, FileAttributes Attributes, bool IsDirectory)>(
                path,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.Attributes, e.IsDirectory),
                s_flatEnumOptions).ToArray();
            bool childrenLeft = false;

            foreach (var entry in entries)
            {
                if (entry.IsDirectory) continue;
                if (!DeleteEntry(entry.Path, directory: false)) childrenLeft = true;
            }

            foreach (var entry in entries)
            {
                if (!entry.IsDirectory) continue;
                token.ThrowIfCancellationRequested();
                if (IsReparse(entry.Attributes)) DeleteEntry(entry.Path, directory: true);
                else Win32FastDeleteDirectory(entry.Path, onFileDeleted, onFailure, token, deleteLocal);
                if (DeleteEntryExists(entry.Path)) childrenLeft = true;
            }

            DeleteEntry(path, directory: true, reportedChildren: childrenLeft);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var action = onFailure(path, ex.Message);
            if (action == FailureAction.Abort) throw new OperationCanceledException(token);
            if (action == FailureAction.Retry) Win32FastDeleteDirectory(path, onFileDeleted, onFailure, token, deleteLocal);
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
        // A recycle removes whole folders in one shell call, so only the item
        // count is real there; a permanent delete counts every file it removes
        string done = action == "Recycle"
            ? $"{action}: {items} item(s) done"
            : $"{action}: {files} file(s) in {items} item(s) done";

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
