using System.IO;
using System.Windows;
using System.Windows.Controls;
using R2Cmd.Controls;

namespace R2Cmd;

public partial class MainWindow
{
    // =========================================================================
    // Blocks two distinct mistakes:
    //   1. dropping a folder into itself or into one of its own subfolders;
    //   2. copying an entry into the directory it already lives in, where the
    //      source and the destination path are literally the same file.
    // =========================================================================
    private FileEntry? FindSelfOperationConflict(List<FileEntry> items, string destPath)
    {
        return items.FirstOrDefault(i => IsSelfOperationConflict(i, destPath));
    }

    // Blocks two mistakes for both local and ssh:// paths:
    // 1) copying/moving an item into the directory it already lives in;
    // 2) dropping a folder into itself or into one of its own subfolders.
    private static bool IsSelfOperationConflict(FileEntry item, string destPath)
    {
        if (item.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
            destPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            // Mixed local/SSH is never "same location"
            if (!item.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
                !destPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                return false;

            string src = item.FullPath.TrimEnd('/');
            string dest = destPath.TrimEnd('/');

            // Different SSH sessions cannot be the same tree
            string srcSession = SshSessionName(src);
            string destSession = SshSessionName(dest);
            if (!string.Equals(srcSession, destSession, StringComparison.OrdinalIgnoreCase))
                return false;

            // Parent of source == destination → copy into its own folder
            int lastSlash = src.LastIndexOf('/');
            if (lastSlash > "ssh://".Length)
            {
                string parent = src.Substring(0, lastSlash);
                if (string.Equals(parent, dest, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Folder into itself or into a child: dest is src or starts with src/
            if (!item.IsFolder) return false;

            return string.Equals(dest, src, StringComparison.OrdinalIgnoreCase) ||
                   dest.StartsWith(src + "/", StringComparison.OrdinalIgnoreCase);
        }

        // ----- Local paths -----
        string localDest = destPath.TrimEnd('\\');
        string localSrc = item.FullPath.TrimEnd('\\');

        string? parentLocal = Path.GetDirectoryName(localSrc);
        if (parentLocal != null &&
            string.Equals(parentLocal.TrimEnd('\\'), localDest, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!item.IsFolder) return false;

        string srcPrefix = localSrc.EndsWith("\\") ? localSrc : localSrc + "\\";
        return (localDest + "\\").StartsWith(srcPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string SshSessionName(string sshPath)
    {
        // ssh://SessionName/rest...
        string rest = sshPath.Length > 6 ? sshPath.Substring(6) : "";
        int slash = rest.IndexOf('/');
        return slash < 0 ? rest : rest.Substring(0, slash);
    }

    // =========================================================================
    // OPENING A FILE WITH THE SHELL
    //
    // ShellExecute is only correct for a real path on disk. An SSH entry carries
    // "ssh://session/dir/server.js", and Windows reads that as a URL: it looks up
    // the registered handler for the ssh: scheme — PuTTY, OpenSSH, Windows
    // Terminal, whatever is installed — and launches a console session. That is
    // why clicking a remote file opened a terminal instead of the file.
    //
    // A path inside an archive fails differently: it looks local but nothing
    // exists there.
    //
    // Both cases are materialised into TEMP first and the local copy is opened.
    // Edits to that copy are NOT sent back to the server or the archive.
    // =========================================================================
    private async Task OpenFileExternallyAsync(FileEntry entry)
    {
        if (File.Exists(entry.FullPath))
        {
            ShellOpen(entry.FullPath);
            return;
        }

        string? localCopy = await MaterializeToTempAsync(entry);
        if (localCopy == null) return;

        ShellOpen(localCopy);
        SetStatus($"Opened a temporary copy of {entry.Name}. Changes are not sent back.");
    }

    private void ShellOpen(string localPath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = localPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, $"Failed to launch file.\n\n{ex.Message}", "Execution Error");
        }
    }

    private async Task<string?> MaterializeToTempAsync(FileEntry entry)
    {
        const long LargeFileThreshold = 64L * 1024 * 1024;

        if (entry.Size > LargeFileThreshold)
        {
            var confirm = new ConfirmDialog(
                $"{entry.Name} is {Helpers.FormatSize(entry.Size)}.\nDownload a temporary copy to open it?",
                "Open file")
            { Owner = this };

            if (confirm.ShowDialog() != true) return null;
        }

        string folder = Path.Combine(Path.GetTempPath(), "R2Cmd", TempFolderFor(entry.FullPath));
        string localPath = Path.Combine(folder, SafeFileName(entry.Name));

        SetBackgroundStatus($"Downloading {entry.Name}...");

        try
        {
            Directory.CreateDirectory(folder);

            var (archivePath, internalPath) = ArchiveService.ParseVirtualPath(entry.FullPath);

            if (archivePath != null && !string.IsNullOrEmpty(internalPath) && File.Exists(archivePath))
            {
                await Task.Run(() => ArchiveService.ExtractFile(archivePath, internalPath, localPath));
            }
            else
            {
                var provider = Providers.FileSystemFactory.GetProvider(entry.FullPath);

                await using var source = await provider.OpenReadAsync(entry.FullPath);
                await using var target = new FileStream(localPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 81920, useAsync: true);

                await source.CopyToAsync(target);
            }

            return localPath;
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, $"Cannot open {entry.Name}:\n{ex.Message}", "Open");
            return null;
        }
        finally
        {
            // Restores whatever the folder size scan is reporting, if anything
            UpdateBackgroundStatus();
        }
    }

    private static string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }

    // One folder per source path, so two server.js from different hosts do not
    // overwrite each other. FNV-1a rather than String.GetHashCode, which is
    // randomised per process — the same remote file would otherwise land in a
    // different temp folder on every run of the application.
    //
    // Case folding happens per character: ToLowerInvariant() on the whole path
    // allocated a throwaway string for nothing.
    private static string TempFolderFor(string fullPath)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in fullPath)
            {
                hash ^= char.ToLowerInvariant(c);
                hash *= 16777619;
            }
            return hash.ToString("x8");
        }
    }

    private async Task RefreshTargetAsync(FilePaneControl targetPane)
    {
        await targetPane.RefreshAsync();
        await SyncPanesIfSamePath(targetPane);
    }

    // =========================================================================
    // SHARED PROGRESS WINDOW HOST
    // Runs a copy/move/pack dialog and mirrors its progress line (including the
    // elapsed timer) into the bottom status bar, exactly like delete does.
    //
    // The summary line is written LAST, after pane refresh and focus callbacks
    // have drained, and the status lock is released only after that. Otherwise
    // the panes would immediately overwrite the result with their own messages
    // and the statistics would vanish the moment the window closes.
    // =========================================================================
    private async Task RunFileOperationAsync(ProgressWindow dialog, Func<Task>? onSuccess = null, FilePaneControl? sourcePane = null)
    {
        this.IsEnabled = false;

        // Lock the status bar so pane messages cannot overwrite operation progress
        IsStatusLocked = true;

        EventHandler<string> onStatus = (s, text) => SetStatus(text, forceUpdate: true);
        dialog.StatusUpdated += onStatus;

        var tcs = new TaskCompletionSource<bool>();
        dialog.Closed += (s, e) => tcs.TrySetResult(true);
        dialog.BackgroundRequested += (s, e) => { this.IsEnabled = true; };

        try
        {
            dialog.Show();
            await tcs.Task;
        }
        finally
        {
            dialog.StatusUpdated -= onStatus;
            if (!this.IsEnabled) this.IsEnabled = true;
        }

        // =========================================================================
        // Everything below runs under the status lock, so the panes cannot clobber
        // the summary. The try/finally matters: without it an exception from
        // onSuccess() (a disconnected network drive is enough) would leave
        // IsStatusLocked stuck at true and the status bar dead until restart.
        // =========================================================================
        string finalStatus = dialog.GetFinalStatus();

        try
        {
            if (!dialog.IsCancelled && dialog.SuccessfullyProcessedFiles > 0 && onSuccess != null)
            {
                await onSuccess();
            }

            sourcePane?.ClearSelection();
        }
        finally
        {
            // ApplicationIdle runs after the panes' own Background/Input priority
            // callbacks, so this is the last thing written to the status bar
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                sourcePane?.FocusPanel();
                SetStatus(finalStatus, forceUpdate: true);

                // Release only now: the line stays until the user does something else
                IsStatusLocked = false;
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    // =========================================================================
    // Single entry point for Copy / Move / drag-and-drop.
    // explicitDestPath allows drag-and-drop to target a specific subfolder.
    // =========================================================================
    private async Task StartTransferAsync(
        List<FileEntry> items,
        FilePaneControl? sourcePane,
        FilePaneControl targetPane,
        FileOperation operation,
        string? explicitDestPath = null)
    {
        if (items.Count == 0) return;

        // Use the explicitly provided path (from a subfolder drop), or fallback to the pane's root
        string destPath = explicitDestPath ?? targetPane.CurrentPath;
        bool isMove = operation == FileOperation.Move;

        var conflict = FindSelfOperationConflict(items, destPath);
        if (conflict != null)
        {
            MessageDialog.Show(this,
                $"Cannot {(isMove ? "move" : "copy")} '{conflict.Name}': the destination is the item's own location or a subfolder of it.\n\n" +
                $"Source: {conflict.FullPath}\nDestination: {destPath}",
                isMove ? "Move Error" : "Copy Error");
            return;
        }

        var dialog = new ProgressWindow(items, destPath, operation) { Owner = this };

        // Move touches both panes, copy only the target one
        Func<Task> onSuccess = isMove
            ? () => DoRefreshAsync()
            : () => RefreshTargetAsync(targetPane);

        // Watchers remain active to show files appearing in real-time.
        // The background navigation fix ensures the UI will not flicker.
        await RunFileOperationAsync(dialog, onSuccess, sourcePane);
    }

    private Task DoCopyAsync() =>
        StartTransferAsync(_activePane.SelectedItems, _activePane, _inactivePane, FileOperation.Copy);

    private Task DoMoveAsync() =>
        StartTransferAsync(_activePane.SelectedItems, _activePane, _inactivePane, FileOperation.Move);

    private Task HandleFilesDroppedAsync(FilePaneControl targetPane, Controls.FilePaneControl.FilesDroppedEventArgs args) =>
        StartTransferAsync(args.Items, null, targetPane, args.IsMove ? FileOperation.Move : FileOperation.Copy, args.TargetPath);

    private async Task DoPackAsync()
    {
        var sourcePane = _activePane;
        var targetPane = _inactivePane;

        var itemsToPack = sourcePane.SelectedItems;
        if (itemsToPack.Count == 0) return;

        string currentDirPath = sourcePane.CurrentPath;
        string destDirPath = targetPane.CurrentPath;

        // =====================================================================
        // The archive must not land inside a folder that is being packed.
        // The packer would pick up the half-written archive itself, and with
        // "Delete packed files after archiving" the new archive would go to
        // the Recycle Bin together with its own contents.
        // =====================================================================
        var container = FindPackedFolderContaining(itemsToPack, destDirPath);
        if (container != null)
        {
            MessageDialog.Show(this,
                $"The archive cannot be created inside \"{container.Name}\", which is being packed.\n\n" +
                "Open a different folder in the other panel.",
                "Pack Error");
            return;
        }

        // The proposed name is already free in the target folder, so accepting
        // the default never ends in "already exists"
        string defaultName = MakeUniqueArchiveName(destDirPath, DefaultArchiveBaseName(itemsToPack, currentDirPath));

        string? zipName = ShowInputBox(
            "Pack files",
            $"Pack {itemsToPack.Count} item(s) to:",
            defaultName,
            out bool deleteAfter,
            "Delete packed files after archiving");
        if (string.IsNullOrWhiteSpace(zipName)) return;

        zipName = zipName.Trim();
        if (!zipName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zipName += ".zip";
        string targetZipPath = Path.Combine(destDirPath, zipName);

        // A name typed by hand that is taken: offer the next free numbered one
        // rather than silently changing what the user explicitly typed
        if (ArchiveNameTaken(targetZipPath))
        {
            string freeName = MakeUniqueArchiveName(destDirPath, Path.GetFileNameWithoutExtension(zipName));

            var confirm = new ConfirmDialog(
                $"\"{zipName}\" already exists.\n\nPack to \"{freeName}\" instead?",
                "Pack")
            { Owner = this };

            if (confirm.ShowDialog() != true) return;

            zipName = freeName;
            targetZipPath = Path.Combine(destDirPath, zipName);
        }

        var dialog = new ProgressWindow(itemsToPack, targetZipPath, FileOperation.Pack) { Owner = this };

        await RunFileOperationAsync(dialog, async () =>
        {
            await targetPane.NavigateAsync(destDirPath, zipName);
            await SyncPanesIfSamePath(targetPane);
        }, sourcePane: null);

        // Everything was skipped, so the packer kept no archive
        if (!dialog.IsCancelled && !File.Exists(targetZipPath))
        {
            MessageDialog.Show(this,
                "Nothing was packed: every selected item was skipped.\n\n" +
                "No archive was created and nothing was deleted.",
                "Pack");
            targetPane.FocusPanel();
            return;
        }

        if (deleteAfter && !dialog.IsCancelled && File.Exists(targetZipPath))
        {
            // Only items that went into the archive in full. A file that was
            // skipped (locked, access denied) is not in the archive, and neither
            // it nor the folder holding it may be deleted.
            var packed = dialog.FullyPackedItems.ToList();
            int kept = itemsToPack.Count - packed.Count;

            if (packed.Count > 0)
            {
                _activePane = sourcePane;
                await DoDeleteAsync(permanent: false, skipConfirm: true, packed);
            }

            if (kept > 0)
            {
                MessageDialog.Show(this,
                    $"{kept} of {itemsToPack.Count} item(s) were NOT deleted: " +
                    "some of their files could not be added to the archive.\n\n" +
                    "They are still in place, and the archive does not contain them in full.",
                    "Pack");
            }
        }

        targetPane.FocusPanel();
    }

    private string? ShowInputBox(string title, string prompt, string defaultText, out bool extraOption, string? extraOptionText = null)
    {
        extraOption = false;

        var window = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = this.Background,
            Foreground = this.Foreground
        };

        var root = new StackPanel { Margin = new Thickness(15) };

        var lbl = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) };

        var txt = new TextBox
        {
            Text = defaultText,
            Height = 25,
            Padding = new Thickness(3),
            Margin = new Thickness(0, 0, 0, 15),
            Background = this.Background,
            Foreground = this.Foreground,
            CaretBrush = this.Foreground
        };

        CheckBox? extraBox = null;
        if (!string.IsNullOrEmpty(extraOptionText))
        {
            extraBox = new CheckBox
            {
                Content = extraOptionText,
                Margin = new Thickness(0, 0, 0, 15),
                Foreground = this.Foreground
            };
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var btnOk = new Button { Content = "OK", Width = 80, Height = 30, IsDefault = true, Margin = new Thickness(0, 0, 10, 0) };
        var btnCancel = new Button { Content = "Cancel", Width = 80, Height = 30, IsCancel = true };

        btnOk.Click += (s, e) => window.DialogResult = true;

        buttons.Children.Add(btnOk);
        buttons.Children.Add(btnCancel);

        root.Children.Add(lbl);
        root.Children.Add(txt);
        if (extraBox != null) root.Children.Add(extraBox);
        root.Children.Add(buttons);

        window.Content = root;
        window.SourceInitialized += (s, e) => Helpers.SetTitleBarTheme(window, ThemeManager.IsDarkTheme);
        window.Loaded += (s, e) => { txt.Focus(); txt.CaretIndex = txt.Text.Length; };

        if (window.ShowDialog() != true) return null;
        extraOption = extraBox?.IsChecked == true;
        return txt.Text;
    }

    // Returns the selected folder that contains destDir (or is destDir), if any.
    // Local paths only: packing works on local files, and an SSH or archive
    // destination cannot be inside a local folder anyway.
    private static FileEntry? FindPackedFolderContaining(IEnumerable<FileEntry> items, string destDir)
    {
        if (destDir.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return null;

        string dest;
        try { dest = Path.GetFullPath(destDir).TrimEnd('\\') + "\\"; }
        catch { return null; }

        foreach (var item in items)
        {
            if (!item.IsFolder || item.Name == "..") continue;

            string folder;
            try { folder = Path.GetFullPath(item.FullPath).TrimEnd('\\') + "\\"; }
            catch { continue; }

            if (dest.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return item;
        }

        return null;
    }

    // =========================================================================
    // Default archive name, as Total Commander proposes it:
    //   - one file selected   -> the file name without its extension;
    //   - one folder selected -> the folder name;
    //   - several items       -> the name of the folder they are in.
    // =========================================================================
    private static string DefaultArchiveBaseName(IReadOnlyList<FileEntry> items, string currentDirPath)
    {
        if (items.Count == 1 && items[0].Name != "..")
        {
            var only = items[0];
            string single = only.IsFolder ? only.Name : Path.GetFileNameWithoutExtension(only.Name);
            if (!string.IsNullOrWhiteSpace(single)) return single;
        }

        string folder = Path.GetFileName(currentDirPath.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(folder) || folder == "Network" ? "archive" : folder;
    }

    // "name.zip", then "name_1.zip", "name_2.zip"... — the first one free.
    // A folder with the same name counts as taken too: a file cannot be
    // created where a directory of that name already is.
    private static string MakeUniqueArchiveName(string directory, string baseName)
    {
        string candidate = baseName + ".zip";

        for (int n = 1; ArchiveNameTaken(Path.Combine(directory, candidate)); n++)
            candidate = $"{baseName}_{n}.zip";

        return candidate;
    }

    private static bool ArchiveNameTaken(string path) => File.Exists(path) || Directory.Exists(path);
}
