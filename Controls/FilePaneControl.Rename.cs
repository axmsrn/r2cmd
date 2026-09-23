using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using R2Cmd.Providers;

namespace R2Cmd.Controls;

public partial class FilePaneControl
{
    // EntryPoint is required: kernel32 exports only MoveFileExA and MoveFileExW.
    // DllImport used to find the W version by itself; LibraryImport looks for
    // the exact name "MoveFileEx", which does not exist, and every overwrite
    // on rename failed with "Entry point was not found".
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    private const uint MOVEFILE_REPLACE_EXISTING = 0x00000001;

    // 0 — stem only ("report" in "report.txt"). 1 — whole name including extension.
    // Pressing the rename hotkey again while the box is open switches between them.
    private int _renameSelectionMode;
    private bool _renameScrollHooked;

    private void HandleF2Rename()
    {
        if (lvFiles.SelectedItem is not FileEntry entry || entry.Name == "..") return;

        // Already editing this row: only switch the selection.
        // Must NOT go through SelectRenameText, which resets box.Text.
        if (_renamingEntry != null && ReferenceEquals(_renamingEntry, entry))
        {
            CycleRenameSelection();
            return;
        }

        BeginRename(false);
    }

    private void BeginRename(bool selectAll, bool caretAfterStem = false)
    {
        _renameClickTimer?.Stop();
        if (lvFiles.SelectedItem is not FileEntry entry || entry.Name == "..") return;
        if (_renamingEntry != null) return;
        if (_isBusy) return;

        _renamingEntry = entry;
        lvFiles.ScrollIntoView(entry);

        // Removed synchronous lvFiles.UpdateLayout() which caused UI stutters.
        // Dispatcher guarantees the virtualizing panel will realize the item naturally.
        _ = Dispatcher.BeginInvoke(
            new Action(() => SelectRenameText(entry, selectAll, caretAfterStem)),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    // Opens the pane-level overlay over the name cell. txtName stays visible
    // so the overlay can be re-measured on scroll; the opaque border covers it.
    private void SelectRenameText(FileEntry entry, bool selectAll, bool caretAfterStem = false)
    {
        if (!ReferenceEquals(_renamingEntry, entry)) return;

        if (!TryGetRenameRect(entry, out Rect rect))
        {
            CancelRename();
            return;
        }

        ApplyRenameOverlay(rect);
        pnlRename.UpdateLayout();

        _renameBox = txtRenameOverlay;
        txtRenameOverlay.Text = entry.Name;
        _renameSelectionMode = selectAll ? 1 : 0;

        AttachRenameScrollWatch();

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_renamingEntry, entry) || _renameBox != txtRenameOverlay) return;
            txtRenameOverlay.Focus();
            Keyboard.Focus(txtRenameOverlay);
            if (caretAfterStem)
            {
                // Mouse rename: caret right before the extension ("report|.txt"),
                // since it is almost always the name that gets edited
                string text = txtRenameOverlay.Text ?? "";
                txtRenameOverlay.Select(GetStemLength(text, entry.IsFolder), 0);
            }
            else
                ApplyRenameSelection(txtRenameOverlay, entry);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CycleRenameSelection()
    {
        var box = _renameBox;
        var entry = _renamingEntry;
        if (box == null || entry == null) return;

        // Two modes. For a TC-style three-step cycle (stem -> extension -> all)
        // raise the modulus to 3 and add the extension branch in ApplyRenameSelection.
        _renameSelectionMode = (_renameSelectionMode + 1) % 2;

        box.Focus();
        Keyboard.Focus(box);
        ApplyRenameSelection(box, entry);
    }

    private void ApplyRenameSelection(TextBox box, FileEntry entry)
    {
        string text = box.Text ?? string.Empty;
        if (text.Length == 0)
        {
            box.CaretIndex = 0;
            return;
        }

        int stemLength = GetStemLength(text, entry.IsFolder);
        if (_renameSelectionMode == 0 && stemLength > 0 && stemLength < text.Length)
            box.Select(0, stemLength);
        else
            box.SelectAll();
    }

    // Folders and dotfiles have no stem. "archive.tar.gz" uses the last dot, same as Explorer.
    private static int GetStemLength(string name, bool isFolder)
    {
        if (isFolder) return name.Length;
        int lastDot = name.LastIndexOf('.');
        return lastDot <= 0 ? name.Length : lastDot;
    }

    private bool TryGetRenameRect(FileEntry entry, out Rect rect)
    {
        rect = default;
        if (lvFiles.ItemContainerGenerator.ContainerFromItem(entry) is not ListViewItem container)
            return false;

        Point origin = container.TranslatePoint(new Point(0, 0), grdListArea);
        double x, y, h;

        var nameBlock = FindDescendant<TextBlock>(container, "txtName");
        if (nameBlock != null)
        {
            Point p = nameBlock.TranslatePoint(new Point(-4, -2), grdListArea);
            x = p.X;
            y = p.Y;
            h = Math.Max(nameBlock.ActualHeight + 4, container.ActualHeight > 1 ? container.ActualHeight : 22);
        }
        else
        {
            double iconSlot = 22;
            if (TryFindResource("AppFileIconSize") is double iconSize && iconSize > 0)
                iconSlot = iconSize + 10;

            x = origin.X + container.Padding.Left + iconSlot;
            y = origin.Y;
            h = container.ActualHeight > 1 ? container.ActualHeight : 22;
        }

        double w = 80;
        if (lvFiles.View is GridView gv && gv.Columns.Count > 0 && gv.Columns[0].ActualWidth > 0)
            w = Math.Max(80, origin.X + gv.Columns[0].ActualWidth - x - 8);

        rect = new Rect(x, y, w, h);
        return true;
    }

    private void ApplyRenameOverlay(Rect rect)
    {
        Canvas.SetLeft(pnlRename, rect.X);
        Canvas.SetTop(pnlRename, rect.Y);
        pnlRename.Width = rect.Width;
        pnlRename.Height = Math.Max(rect.Height, 18);
        pnlRename.Visibility = Visibility.Visible;
    }

    private void HideRenameOverlay()
    {
        DetachRenameScrollWatch();
        if (pnlRename.Visibility != Visibility.Collapsed)
            pnlRename.Visibility = Visibility.Collapsed;
    }

    private void AttachRenameScrollWatch()
    {
        if (_renameScrollHooked) return;
        lvFiles.AddHandler(ScrollViewer.ScrollChangedEvent, RenameScrollHandler, true);
        _renameScrollHooked = true;
    }

    private void DetachRenameScrollWatch()
    {
        if (!_renameScrollHooked) return;
        lvFiles.RemoveHandler(ScrollViewer.ScrollChangedEvent, RenameScrollHandler);
        _renameScrollHooked = false;
    }

    private ScrollChangedEventHandler RenameScrollHandler =>
        _renameScrollHandlerField ??= OnRenameScrollChanged;

    private ScrollChangedEventHandler? _renameScrollHandlerField;

    private void OnRenameScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_renamingEntry == null || _renameInProgress) return;
        if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;

        if (lvFiles.ItemContainerGenerator.ContainerFromItem(_renamingEntry) is not ListViewItem)
        {
            CommitActiveRename();
            return;
        }

        if (!TryGetRenameRect(_renamingEntry, out Rect rect) ||
            rect.Y + rect.Height < 0 ||
            rect.Y > grdListArea.ActualHeight)
        {
            CommitActiveRename();
            return;
        }

        ApplyRenameOverlay(rect);
    }

    private async void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (sender is TextBox box) await CommitRenameAsync(box);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelRename();
        }
        else if (e.Key == Key.F2 && !e.Handled)
        {
            e.Handled = true;
            CycleRenameSelection();
        }
    }

    private async void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) await CommitRenameAsync(box);
    }

    private async Task CommitRenameAsync(TextBox box)
    {
        if (_renamingEntry == null || _renameInProgress) return;
        _renameInProgress = true;

        try
        {
            var entry = _renamingEntry;
            string newName = box.Text.Trim();
            bool isCreatingNew = entry.FullPath == ":::NEW:::";
            string oldPath = entry.FullPath;

            // Hide first so LostFocus from collapsing the overlay is a no-op
            // (this method is already in flight via _renameInProgress).
            _renamingEntry = null;
            _renameBox = null;
            entry.IsEditing = false;
            HideRenameOverlay();

            if (string.IsNullOrEmpty(newName) ||
                (!isCreatingNew && string.Equals(newName, entry.Name, StringComparison.Ordinal)))
            {
                AbortRenameUi(entry, isCreatingNew);
                return;
            }

            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageDialog.Show(Window.GetWindow(this), "Invalid file name.", isCreatingNew ? "Create" : "Rename");
                AbortRenameUi(entry, isCreatingNew);
                return;
            }

            if (CurrentPath.Equals(@"\\Network\", StringComparison.OrdinalIgnoreCase) &&
                !isCreatingNew &&
                oldPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                await RenameSshSessionAsync(entry, newName);
                return;
            }

            var provider = FileSystemFactory.GetProvider(CurrentPath);
            string dir = isCreatingNew ? CurrentPath : provider.GetParentPath(oldPath);
            string newPath = provider.CombinePaths(dir, newName);

            bool caseOnlyRename = !isCreatingNew &&
                string.Equals(newName, entry.Name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(newName, entry.Name, StringComparison.Ordinal);

            bool handledAtomicallyByWin32 = false;

            if (!caseOnlyRename && await Task.Run(() => provider.Exists(newPath)))
            {
                var conflict = await ResolveRenameConflict(entry, provider, newName, newPath, isCreatingNew);
                if (conflict == ConflictAction.Abort) return;
                if (conflict == ConflictAction.Reedit) return;
                if (conflict == ConflictAction.AtomicWin32) handledAtomicallyByWin32 = true;
            }

            try
            {
                // On the thread pool: over SSH each of these is a network round
                // trip, and running them here froze the window until it finished
                if (!handledAtomicallyByWin32)
                {
                    if (isCreatingNew)
                    {
                        await Task.Run(() =>
                        {
                            if (entry.IsFolder) provider.CreateDirectory(newPath);
                            else provider.CreateFile(newPath);
                        });
                    }
                    else
                    {
                        await RetryIo(() => Task.Run(() => provider.Rename(oldPath, newPath)));
                    }
                }
            }
            catch (Exception ex)
            {
                MessageDialog.Show(Window.GetWindow(this),
                    $"Cannot {(isCreatingNew ? "create" : "rename")}:\n{ex.Message}",
                    isCreatingNew ? "Create" : "Rename");
                AbortRenameUi(entry, isCreatingNew);
                return;
            }

            await NavigateAsync(CurrentPath, newName);
            DirectoryModified?.Invoke(this, EventArgs.Empty);

            _ = Dispatcher.BeginInvoke(
                new Action(FocusPanel),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        finally { _renameInProgress = false; }
    }

    private enum ConflictAction { Continue, AtomicWin32, Reedit, Abort }

    private async Task<ConflictAction> ResolveRenameConflict(
        FileEntry entry, IFileSystemProvider provider,
        string newName, string newPath, bool isCreatingNew)
    {
        var targetItem = Items.FirstOrDefault(i =>
            string.Equals(i.Name, newName, StringComparison.OrdinalIgnoreCase));

        // Only the local disk provider may be handled with Win32 calls. The old
        // test ("not under \\Network") also took ssh:// paths for local, so an
        // overwrite on SSH went to MoveFileEx with an ssh:// path and always failed.
        bool isLocal = provider is LocalDiskProvider;

        bool isTargetFolder = targetItem != null
            ? targetItem.IsFolder
            : isLocal
                ? Directory.Exists(newPath)
                : await IsRemoteDirectory(provider, newPath);

        bool canOverwrite = !isCreatingNew;
        if (canOverwrite && isTargetFolder)
        {
            var (targetEntries, err) = await provider.ReadDirectoryAsync(newPath);
            canOverwrite = err == null && targetEntries != null && !targetEntries.Any(e => e.Name != "..");
        }

        var conflictDialog = new RenameConflictDialog(newName, isTargetFolder, canOverwrite)
        {
            Owner = Window.GetWindow(this)
        };
        conflictDialog.ShowDialog();

        if (conflictDialog.Result == RenameConflictResult.Rename)
        {
            _renamingEntry = entry;
            lvFiles.UpdateLayout();
            _ = Dispatcher.BeginInvoke(
                new Action(() => SelectRenameText(entry, true)),
                System.Windows.Threading.DispatcherPriority.Input);
            return ConflictAction.Reedit;
        }

        if (conflictDialog.Result != RenameConflictResult.Overwrite)
        {
            AbortRenameUi(entry, isCreatingNew);
            return ConflictAction.Abort;
        }

        if (isLocal && !isTargetFolder && !isCreatingNew)
        {
            if (!MoveFileEx(entry.FullPath, newPath, MOVEFILE_REPLACE_EXISTING))
            {
                int error = Marshal.GetLastWin32Error();
                MessageDialog.Show(Window.GetWindow(this),
                    $"Win32 Kernel atomic overwrite failed.\nError code: {error}", "Overwrite Error");
                RestoreRowFocus(entry);
                return ConflictAction.Abort;
            }
            return ConflictAction.AtomicWin32;
        }

        var (deleted, deleteError) = await TryDeleteForOverwrite(provider, newPath, isLocal);
        if (!deleted)
        {
            MessageDialog.Show(Window.GetWindow(this),
                $"Cannot delete existing item to overwrite:\n{deleteError}", "Overwrite Error");
            AbortRenameUi(entry, isCreatingNew);
            return ConflictAction.Abort;
        }

        return ConflictAction.Continue;
    }

    private async Task RenameSshSessionAsync(FileEntry entry, string newName)
    {
        try
        {
            var settings = (Window.GetWindow(this) as MainWindow)?.AppSettings ?? AppSettings.Load();
            var session = settings.SshSessions.FirstOrDefault(s =>
                s.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) ||
                $"{s.Username}@{s.Host}".Equals(entry.Name, StringComparison.OrdinalIgnoreCase));

            if (session == null)
            {
                RestoreRowFocus(entry);
                return;
            }

            if (settings.SshSessions.Any(s => string.Equals(s.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                MessageDialog.Show(Window.GetWindow(this),
                    $"A session named \"{newName}\" already exists.", "Rename");
                RestoreRowFocus(entry);
                return;
            }

            Providers.SshFileSystemProvider.CloseConnection(entry.Name);
            session.Name = newName;
            settings.Save();
            await NavigateAsync(CurrentPath, newName);
            DirectoryModified?.Invoke(this, EventArgs.Empty);

            _ = Dispatcher.BeginInvoke(
                new Action(FocusPanel),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(Window.GetWindow(this), $"Cannot rename session:\n{ex.Message}", "Rename Error");
            RestoreRowFocus(entry);
        }
    }

    private static async Task<bool> IsRemoteDirectory(IFileSystemProvider provider, string path)
    {
        var (testEntries, testErr) = await provider.ReadDirectoryAsync(path);
        return testErr == null && testEntries != null;
    }

    private static async Task<(bool Ok, string? Error)> TryDeleteForOverwrite(
        IFileSystemProvider provider, string newPath, bool isLocal)
    {
        try
        {
            if (isLocal)
            {
                try
                {
                    if (Directory.Exists(newPath))
                    {
                        var di = new DirectoryInfo(newPath);
                        di.Attributes &= ~FileAttributes.ReadOnly;
                    }
                    else if (File.Exists(newPath))
                    {
                        File.SetAttributes(newPath, FileAttributes.Normal);
                    }
                }
                catch { }
            }

            await RetryIo(async () => await provider.DeleteAsync(newPath));
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task RetryIo(Func<Task> action)
    {
        int retries = 3;
        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when ((ex is UnauthorizedAccessException || ex is IOException) && retries > 0)
            {
                retries--;
                await Task.Delay(150);
            }
        }
    }

    private void AbortRenameUi(FileEntry entry, bool isCreatingNew)
    {
        HideRenameOverlay();
        if (isCreatingNew)
        {
            Items.Remove(entry);
            FocusPanel();
        }
        else
        {
            RestoreRowFocus(entry);
        }
    }

    private void CancelRename()
    {
        if (_renamingEntry == null) return;
        var entry = _renamingEntry;
        _renamingEntry = null;
        _renameBox = null;
        _renameSelectionMode = 0;
        entry.IsEditing = false;
        HideRenameOverlay();

        if (entry.FullPath == ":::NEW:::")
        {
            Items.Remove(entry);
            FocusPanel();
        }
        else
        {
            RestoreRowFocus(entry);
        }
    }

    private void RestoreRowFocus(FileEntry entry)
    {
        lvFiles.UpdateLayout();
        if (lvFiles.ItemContainerGenerator.ContainerFromItem(entry) is ListViewItem container)
            container.Focus();
        else
            lvFiles.Focus();
    }

    private bool ClickInsideRenameBox(MouseButtonEventArgs e)
    {
        return _renameBox != null
            && e.OriginalSource is DependencyObject d
            && FindAncestor<TextBox>(d) is TextBox tb
            && ReferenceEquals(tb, _renameBox);
    }

    private void CommitActiveRename()
    {
        if (_renameBox is TextBox box) _ = CommitRenameAsync(box);
        else CancelRename();
    }

    // Slow second click must not fire on Network root rows: a double-click
    // there opens a session, and a pending timer would drop the user into rename.
    // Files INSIDE an SSH folder are ordinary files: their ssh:// paths used to
    // be blocked here too, so they could only be renamed with F2.
    private bool IsMouseRenameAllowed(FileEntry item)
    {
        if (CurrentPath.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase)) return false;
        if (item.FullPath == ":::ADD_SSH:::") return false;
        return true;
    }

    private void ScheduleMouseRename(FileEntry item)
    {
        _renameClickTimer?.Stop();
        _renameClickTimer = null;

        if (_isBusy || !IsMouseRenameAllowed(item)) return;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime() + 250)
        };
        _renameClickTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_renameClickTimer, timer)) _renameClickTimer = null;
            if (_isBusy || _renamingEntry != null) return;
            if (!ReferenceEquals(lvFiles.SelectedItem, item)) return;
            BeginRename(false, true);
        };
        timer.Start();
    }

    public void StartCreation(bool isFolder)
    {
        if (_isBusy || _renamingEntry != null) return;

        if (string.IsNullOrEmpty(CurrentPath) || CurrentPath.Equals(@"\\Network\", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage?.Invoke(this, "Cannot create items in this location.");
            return;
        }

        var newEntry = new FileEntry
        {
            Name = "",
            IsFolder = isFolder,
            FullPath = ":::NEW:::",
            IsEditing = false
        };

        int insertIdx = Items.Count > 0 && Items[0].Name == ".." ? 1 : 0;
        Items.Insert(insertIdx, newEntry);

        lvFiles.SelectedItem = newEntry;
        lvFiles.ScrollIntoView(newEntry);
        _renamingEntry = newEntry;
        lvFiles.UpdateLayout();

        _ = Dispatcher.BeginInvoke(
            new Action(() => SelectRenameText(newEntry, false)),
            System.Windows.Threading.DispatcherPriority.Input);
    }



    public void RequestRename() => HandleF2Rename();
}
