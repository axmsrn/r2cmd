using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using R2Cmd.Controls;

namespace R2Cmd;

public partial class MainWindow
{
    // Sentinel stored in CustomEditorPath when the built-in Avalon editor is selected
    private const string InternalEditorId = "__internal__";

    private const string Npp64Path = @"C:\Program Files\Notepad++\notepad++.exe";
    private const string Npp32Path = @"C:\Program Files (x86)\Notepad++\notepad++.exe";
    private const string Np3Path = @"C:\Program Files\Notepad3\Notepad3.exe";

    #region Editor choice (status bar button)

    // =========================================================================
    // The list costs four or five File.Exists calls to build. Nothing in it
    // changes while the application runs except the custom entry, so the chosen
    // path is all the cache has to be keyed on: a different choice rebuilds it
    // by itself, no manual invalidation needed.
    // =========================================================================
    private List<(string ButtonName, string MenuName, string Path)>? _editorsCache;
    private string? _editorsCacheKey;

    private static string SelectedEditorKey(string? customPath) =>
        string.IsNullOrEmpty(customPath) ? InternalEditorId : customPath;

    private List<(string ButtonName, string MenuName, string Path)> GetAvailableEditors()
    {
        if (_editorsCache != null &&
            string.Equals(_editorsCacheKey, _settings.CustomEditorPath, StringComparison.OrdinalIgnoreCase))
        {
            return _editorsCache;
        }

        var list = new List<(string ButtonName, string MenuName, string Path)>
        {
            // Built-in Avalon editor (always available)
            ("Internal Editor", "Internal Editor", InternalEditorId)
        };

        string winNotepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

        if (File.Exists(Npp64Path)) list.Add(("Notepad++", "Notepad++", Npp64Path));
        else if (File.Exists(Npp32Path)) list.Add(("Notepad++", "Notepad++", Npp32Path));

        if (File.Exists(Np3Path)) list.Add(("Notepad3", "Notepad3", Np3Path));

        list.Add(("Win Notepad", "Windows Notepad", winNotepad));

        string custom = _settings.CustomEditorPath;
        bool isStandard = string.IsNullOrEmpty(custom) ||
                          custom.Equals(InternalEditorId, StringComparison.OrdinalIgnoreCase) ||
                          custom.Equals(Npp64Path, StringComparison.OrdinalIgnoreCase) ||
                          custom.Equals(Npp32Path, StringComparison.OrdinalIgnoreCase) ||
                          custom.Equals(Np3Path, StringComparison.OrdinalIgnoreCase) ||
                          custom.Equals(winNotepad, StringComparison.OrdinalIgnoreCase);

        if (!isStandard && File.Exists(custom))
        {
            string fileName = Path.GetFileName(custom);
            list.Add(($"Editor: {fileName}", $"Custom: {fileName}", custom));
        }

        _editorsCacheKey = custom;
        _editorsCache = list;

        return list;
    }

    private void UpdateEditorButton()
    {
        var editors = GetAvailableEditors();
        string selected = SelectedEditorKey(_settings.CustomEditorPath);

        var current = editors.FirstOrDefault(x =>
            string.Equals(x.Path, selected, StringComparison.OrdinalIgnoreCase));

        if (current.ButtonName == null)
            current = editors[0];

        btnSettings.Content = current.ButtonName;
    }

    // The same four lines used to sit in three click handlers
    private void SelectEditor(string path, string label)
    {
        _settings.CustomEditorPath = path;

        // A locked settings file must not turn a button click into a crash
        try { _settings.Save(); } catch { }

        UpdateEditorButton();
        SetStatus($"Editor set to: {label}");
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var editors = GetAvailableEditors();
        string selected = SelectedEditorKey(_settings.CustomEditorPath);

        int currentIndex = editors.FindIndex(x =>
            string.Equals(x.Path, selected, StringComparison.OrdinalIgnoreCase));

        if (currentIndex == -1) currentIndex = 0;

        var next = editors[(currentIndex + 1) % editors.Count];
        SelectEditor(next.Path, next.ButtonName);
    }

    private void OnSettingsRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var parentPanel = (FrameworkElement)btnSettings.Parent;
        var menu = new ContextMenu
        {
            PlacementTarget = parentPanel,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Custom
        };

        // Align the popup flush against the right-most edge of the containing panel
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, offset) => new[]
        {
            new System.Windows.Controls.Primitives.CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width + 5, -popupSize.Height - 5),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.None)
        };

        menu.Items.Add(new MenuItem { Header = "Editor Settings (F3/F4)", IsEnabled = false });
        menu.Items.Add(new Separator());

        var editors = GetAvailableEditors();
        string selected = SelectedEditorKey(_settings.CustomEditorPath);

        foreach (var ed in editors)
        {
            if (ed.MenuName.StartsWith("Custom:", StringComparison.Ordinal)) continue;

            bool active = string.Equals(selected, ed.Path, StringComparison.OrdinalIgnoreCase);

            // The themed MenuItem template has no check mark area, so IsChecked
            // would be invisible. A marker in the header is what actually shows.
            var item = new MenuItem { Header = active ? "● " + ed.MenuName : "    " + ed.MenuName };
            item.Click += (s, ev) => SelectEditor(ed.Path, ed.ButtonName);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        bool hasCustom = editors.Any(x => x.MenuName.StartsWith("Custom:", StringComparison.Ordinal));

        var customItem = new MenuItem
        {
            Header = hasCustom
                ? $"● Browse... (Current: {Path.GetFileName(_settings.CustomEditorPath)})"
                : "    Browse for custom editor (.exe)..."
        };

        customItem.Click += (s, ev) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
                Title = "Select Editor Executable"
            };

            if (dlg.ShowDialog() == true)
                SelectEditor(dlg.FileName, Path.GetFileName(dlg.FileName));
        };
        menu.Items.Add(customItem);

        menu.IsOpen = true;
    }

    #endregion

    #region F3 — built-in viewer

    // =========================================================================
    // F3 — the built in viewer, in a window of its own.
    //
    // Non-modal on purpose: several files can be open at once and the manager
    // stays usable behind them, which is what F3 does everywhere else.
    //
    // async void is unavoidable for a hotkey entry point, so everything runs
    // inside a try: an exception escaping an async void method terminates the
    // process.
    // =========================================================================
    private async void OpenInViewer()
    {
        try
        {
            await OpenInViewerAsync();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, $"Cannot view the file.\n\n{ex.Message}", "F3");
        }
    }

    private async Task OpenInViewerAsync()
    {
        var items = _activePane.SelectedItems;
        if (items.Count == 0) return;

        var item = items[0];
        if (item.Name == "..") return;

        bool isSsh = item.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);

        // Resolve a local symlink to the real file: F4 works because external
        // editors let Windows follow the link, F3 reads through FileStream
        string path = item.FullPath;
        if (item.IsSymlink && !isSsh) path = ResolveLinkTarget(path);

        // After resolving, skip real directories
        if (Directory.Exists(path) || (item.IsFolder && !item.IsSymlink))
            return;

        string? pathToView;

        if (File.Exists(path))
        {
            pathToView = path;
        }
        else if (isSsh && TryGetWatchedCopy(item, out string watchedCopy))
        {
            // The file is open in an external editor right now. Its local copy
            // holds the latest saved edits, possibly not uploaded yet;
            // downloading again would overwrite them with the server version.
            pathToView = watchedCopy;
        }
        else
        {
            pathToView = await MaterializeToTempAsync(item);
        }

        if (pathToView == null) return;

        var viewer = new ViewerWindow(pathToView, item.Name) { Owner = this };
        viewer.Show();
    }

    private static string ResolveLinkTarget(string path)
    {
        try
        {
            FileSystemInfo? target = null;

            try { target = new FileInfo(path).ResolveLinkTarget(true); } catch { }
            if (target == null)
            {
                try { target = new DirectoryInfo(path).ResolveLinkTarget(true); } catch { }
            }

            return target?.FullName ?? path;
        }
        catch
        {
            return path;
        }
    }

    #endregion

    #region F4 — editor

    // =========================================================================
    // F4
    //
    // Remote files and files inside archives go through MaterializeToTempAsync,
    // which puts each copy in its own subfolder keyed by the source path, so two
    // files with the same name from two servers never share a local copy.
    // =========================================================================
    private async void OpenFileInEditor(bool readOnly)
    {
        try
        {
            await OpenFileInEditorAsync(readOnly);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, $"Cannot open the file.\n\n{ex.Message}", "F4");
        }
    }

    private async Task OpenFileInEditorAsync(bool readOnly)
    {
        var items = _activePane.SelectedItems;
        if (items.Count == 0) return;

        var item = items[0];
        if (item.IsFolder || item.Name == "..") return;

        bool isLocal = File.Exists(item.FullPath);
        bool isSsh = item.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);

        // =====================================================================
        // Reopening a remote file that is already being watched.
        //
        // The download below rewrites the local copy. With the old watcher still
        // attached, the half-written download itself was taken for an edit and
        // uploaded over the server file. And any saved edit not uploaded yet
        // would be overwritten by the download. So: send pending edits first,
        // then detach, then download.
        // =====================================================================
        if (isSsh) await FlushAndStopRemoteEditAsync(GetTempCopyPath(item));

        // No SetBusy here: the wait cursor and the hotkey lock are not worth it
        // for a download that reports itself in the background status line
        string? pathToOpen = isLocal ? item.FullPath : await MaterializeToTempAsync(item);
        if (pathToOpen == null) return;

        // A copy pulled out of an archive has nowhere to be written back to
        bool isArchive = !isLocal && !isSsh;
        if (isArchive) readOnly = true;

        // Built-in editor only when Internal is selected (the default if unset)
        bool useInternal =
            string.IsNullOrEmpty(_settings.CustomEditorPath) ||
            string.Equals(_settings.CustomEditorPath, InternalEditorId, StringComparison.OrdinalIgnoreCase);

        // Encoding detection reads the file, so it stays off the UI thread
        if (useInternal && !readOnly &&
            await Task.Run(() => IsInternalEditable(item.Name, pathToOpen)))
        {
            string? remotePath = isSsh ? item.FullPath : null;
            var editor = new EditorWindow(pathToOpen, item.Name, remotePath) { Owner = this };
            editor.Show();
            return;
        }

        string editorPath = ResolveEditorPath();

        try
        {
            string args = $"\"{pathToOpen}\"";

            if (readOnly && editorPath.EndsWith("notepad++.exe", StringComparison.OrdinalIgnoreCase))
                args = $"-ro \"{pathToOpen}\"";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = editorPath,
                Arguments = args,
                UseShellExecute = true
            });

            if (isSsh && !readOnly) StartRemoteEdit(pathToOpen, item.FullPath);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, $"Failed to find or launch the editor.\n\n{ex.Message}", "F3/F4 Error");
        }
    }

    private static bool IsInternalEditable(string name, string path)
    {
        if (FileTypes.IsKnownText(Path.GetExtension(name)))
            return true;

        // Fallback: treat as text if encoding detection succeeds
        return TextSearcher.DetectFileEncoding(path) != null;
    }

    // The fallback order lives in GetAvailableEditors and is read from there
    // rather than duplicated: the first external entry is the best one installed.
    private string ResolveEditorPath()
    {
        string editorPath = _settings.CustomEditorPath;
        if (!string.IsNullOrWhiteSpace(editorPath) &&
            !string.Equals(editorPath, InternalEditorId, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(editorPath))
        {
            return editorPath;
        }

        var external = GetAvailableEditors().FirstOrDefault(e =>
            !string.Equals(e.Path, InternalEditorId, StringComparison.OrdinalIgnoreCase));

        return external.Path ?? "notepad.exe";
    }

    #endregion

    #region Remote edit: upload the local copy back to the server on save

    // =========================================================================
    // WATCHING THE LOCAL COPY OF A REMOTE FILE
    //
    // The previous version uploaded on the FIRST change event and ignored every
    // event for the next 1.5 s. Editors save in several steps (truncate, write,
    // flush, sometimes rename), so the upload could read a half-written file,
    // and the event of the final write fell inside the ignored window: the
    // server kept the truncated version. Editors that save atomically (write a
    // temporary file, then rename it over the original) raised no Changed event
    // for the watched name at all and were never uploaded.
    //
    // Now:
    //   - trailing debounce: the upload starts only after the file has been
    //     quiet for UploadQuietMs, so the editor has finished writing;
    //   - Created and Renamed are watched too, which covers atomic saves;
    //   - uploads of one file never overlap (a gate per file);
    //   - a save that did not change the file (same size and write time as the
    //     last upload) is not sent;
    //   - edits saved just before the application closes are still sent.
    // =========================================================================

    private const int UploadQuietMs = 700;
    private const int UploadRetryAttempts = 10;
    private const int UploadRetryDelayMs = 500;
    private static readonly TimeSpan UploadOnExitTimeout = TimeSpan.FromSeconds(15);

    private sealed class RemoteEditWatch
    {
        public required string LocalPath { get; init; }
        public required string RemotePath { get; init; }
        public required FileSystemWatcher Watcher { get; init; }

        // Assigned right after construction: its callback needs the watch itself
        public System.Threading.Timer? Debounce { get; set; }

        // One upload at a time for this file
        public SemaphoreSlim Gate { get; } = new(1, 1);

        // Size and write time of the content the server has. Starts as the
        // freshly downloaded copy, so opening a file uploads nothing.
        // Touched only while holding Gate.
        public long SyncedLength { get; set; }
        public DateTime SyncedWriteUtc { get; set; }

        public volatile bool Stopped;
    }

    // Local copy path -> watch. UI thread only.
    private readonly Dictionary<string, RemoteEditWatch> _remoteEdits =
        new(StringComparer.OrdinalIgnoreCase);

    // Same folder and file name MaterializeToTempAsync produces
    private static string GetTempCopyPath(FileEntry entry) =>
        Path.Combine(Path.GetTempPath(), "R2Cmd", TempFolderFor(entry.FullPath), SafeFileName(entry.Name));

    private bool TryGetWatchedCopy(FileEntry entry, out string localPath)
    {
        localPath = GetTempCopyPath(entry);
        return _remoteEdits.ContainsKey(localPath) && File.Exists(localPath);
    }

    private void StartRemoteEdit(string localPath, string remotePath)
    {
        string? directory = Path.GetDirectoryName(localPath);
        if (string.IsNullOrEmpty(directory)) return;

        // FlushAndStopRemoteEditAsync ran before the download, so normally there
        // is nothing here. Guard anyway: two watches on one file would race.
        if (_remoteEdits.Remove(localPath, out var stale)) StopWatch(stale);

        var watcher = new FileSystemWatcher
        {
            Path = directory,
            Filter = Path.GetFileName(localPath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };

        var watch = new RemoteEditWatch
        {
            LocalPath = localPath,
            RemotePath = remotePath,
            Watcher = watcher
        };

        watch.Debounce = new System.Threading.Timer(
            state => _ = UploadIfChangedAsync(watch, quiet: false),
            null, Timeout.Infinite, Timeout.Infinite);

        // The downloaded content is what the server already has
        try
        {
            var info = new FileInfo(localPath);
            if (info.Exists)
            {
                watch.SyncedLength = info.Length;
                watch.SyncedWriteUtc = info.LastWriteTimeUtc;
            }
        }
        catch { }

        void Kick(object sender, FileSystemEventArgs e)
        {
            if (watch.Stopped) return;
            if (!string.Equals(e.FullPath, localPath, StringComparison.OrdinalIgnoreCase)) return;

            // Every event restarts the quiet period
            try { watch.Debounce?.Change(UploadQuietMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        watcher.Changed += Kick;
        watcher.Created += Kick;
        watcher.Renamed += (s, e) => Kick(s, e);

        // Buffer overflow: an event may have been lost. The content check in
        // the upload makes a spurious attempt harmless.
        watcher.Error += (s, e) =>
        {
            if (watch.Stopped) return;
            try { watch.Debounce?.Change(UploadQuietMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        };

        _remoteEdits[localPath] = watch;
        watcher.EnableRaisingEvents = true;
    }

    // Runs on the thread pool (timer callback) or from the exit flush
    private async Task UploadIfChangedAsync(RemoteEditWatch watch, bool quiet)
    {
        if (watch.Stopped) return;

        await watch.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (watch.Stopped) return;

            // Mid atomic-save the file can be briefly missing; the Created or
            // Renamed event of the new file restarts the timer
            var info = new FileInfo(watch.LocalPath);
            if (!info.Exists) return;

            // Captured BEFORE reading: if the editor saves again during the
            // upload, the stamp no longer matches and the next event sends it
            long length = info.Length;
            DateTime writeUtc = info.LastWriteTimeUtc;

            if (length == watch.SyncedLength && writeUtc == watch.SyncedWriteUtc) return;

            string name = Path.GetFileName(watch.RemotePath);
            if (!quiet) PostStatus($"Uploading changes to {name}...");

            await UploadWithRetryAsync(watch.LocalPath, watch.RemotePath).ConfigureAwait(false);

            watch.SyncedLength = length;
            watch.SyncedWriteUtc = writeUtc;

            if (!quiet)
            {
                PostStatus($"Saved {name} to SSH server.");

                // Remote folders have no watcher: without this the row kept the
                // old size and date until the folder was re-read
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.InvokeAsync(() => RefreshRemoteEntry(watch.RemotePath));
            }
        }
        catch (Exception ex)
        {
            // The synced stamp is left alone, so the next save retries
            if (!quiet && !Dispatcher.HasShutdownStarted)
            {
                _ = Dispatcher.InvokeAsync(() => MessageDialog.Show(this,
                    $"Failed to upload changes to {watch.RemotePath}:\n{ex.Message}\n\n" +
                    "Save the file again in the editor to retry.",
                    "SSH Upload Error"));
            }
        }
        finally
        {
            watch.Gate.Release();
        }
    }

    // The editor may still hold the file for a moment after the quiet period;
    // IOException (sharing violation, file briefly gone) is retried
    private static async Task UploadWithRetryAsync(string localPath, string remotePath)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                Providers.SshFileSystemProvider.UploadFromStream(stream, remotePath, CancellationToken.None, _ => { });
                return;
            }
            catch (IOException) when (attempt < UploadRetryAttempts)
            {
                await Task.Delay(UploadRetryDelayMs).ConfigureAwait(false);
            }
        }
    }

    // Detaches the watch for a local copy, sending any saved-but-unsent edit first
    private async Task FlushAndStopRemoteEditAsync(string localPath)
    {
        if (!_remoteEdits.Remove(localPath, out var watch)) return;

        DetachEvents(watch);
        await UploadIfChangedAsync(watch, quiet: false);
        StopWatch(watch);
    }

    // Stops events and the pending timer; no further upload can be scheduled
    private static void DetachEvents(RemoteEditWatch watch)
    {
        try { watch.Watcher.EnableRaisingEvents = false; } catch { }
        try { watch.Debounce?.Dispose(); } catch { }
    }

    private static void StopWatch(RemoteEditWatch watch)
    {
        watch.Stopped = true;
        DetachEvents(watch);
        try { watch.Watcher.Dispose(); } catch { }
    }

    private void PostStatus(string text)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() => SetStatus(text));
    }

    /// <summary>
    /// Re-reads the size and date of a remote file the application has just
    /// written, and updates its row in whichever pane shows it. Remote folders
    /// have no directory watcher, so this is the only way such a row learns
    /// about the change without a full refresh.
    /// Called on the UI thread by the external-editor watch and by EditorWindow.
    /// </summary>
    internal void RefreshRemoteEntry(string sshPath) => _ = RefreshRemoteEntryAsync(sshPath);

    private async Task RefreshRemoteEntryAsync(string sshPath)
    {
        try
        {
            // One SFTP stat, off the UI thread: it is a network round trip
            var stat = await Task.Run(() => Providers.SshFileSystemProvider.RemoteStat(sshPath));
            if (stat == null) return;

            leftPane.ApplyEntryStat(sshPath, stat.Value.Size, stat.Value.Modified);
            rightPane.ApplyEntryStat(sshPath, stat.Value.Size, stat.Value.Modified);
        }
        catch
        {
            // Cosmetic only: the next refresh of the folder shows the values anyway
        }
    }

    /// <summary>
    /// Called when the window closes. An edit saved in the last moment before
    /// closing is still sent (bounded by a timeout, so a dead connection cannot
    /// hang the exit); nothing is watched after that. Must run BEFORE the SSH
    /// connections are closed.
    /// </summary>
    private void StopEditorWatchers()
    {
        if (_remoteEdits.Count == 0) return;

        var watches = _remoteEdits.Values.ToList();
        _remoteEdits.Clear();

        foreach (var watch in watches) DetachEvents(watch);

        try
        {
            // Quiet: no status line or dialog while the window is going away.
            // Task.Run keeps the awaits off the UI thread, so Wait cannot deadlock.
            var flush = Task.Run(() => Task.WhenAll(
                watches.Select(w => UploadIfChangedAsync(w, quiet: true))));

            flush.Wait(UploadOnExitTimeout);
        }
        catch { }

        foreach (var watch in watches) StopWatch(watch);
    }

    #endregion
}
