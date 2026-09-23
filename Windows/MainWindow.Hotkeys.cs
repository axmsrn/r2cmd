using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using R2Cmd.Controls;

namespace R2Cmd;

public partial class MainWindow
{
    // =========================================================================
    // Keyboard focus is inside a text editor (rename box, path box, dialogs)
    // OR inside the integrated Terminal.
    //
    // Without this guard the window-level handlers steal keys the editor/terminal needs:
    // Window_PreviewKeyDown swallowed every Space and every Ctrl+A, so a space
    // could not be typed into a file name or a path at all.
    // Preview handlers tunnel from the window down, so the window always sees the
    // key first — the editor/terminal never gets a chance to claim it.
    // =========================================================================
    private static bool IsTextEditorFocused() =>
        Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is TerminalControl;

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_busy || IsTextEditorFocused()) return;

        try
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            // Pattern matching provides a clean, table-like routing for global hotkeys
            switch (key, Keyboard.Modifiers)
            {
                case (Key.F3, ModifierKeys.Control): e.Handled = true; _activePane.ApplySort("Name"); break;
                case (Key.F4, ModifierKeys.Control): e.Handled = true; _activePane.ApplySort("Extension"); break;
                case (Key.F5, ModifierKeys.Control): e.Handled = true; _activePane.ApplySort("Modified"); break;
                case (Key.F6, ModifierKeys.Control): e.Handled = true; _activePane.ApplySort("Size"); break;

                case (Key.F3, ModifierKeys.None): e.Handled = true; OpenInViewer(); break;

                case (Key.F4, ModifierKeys.Shift): e.Handled = true; _activePane.StartCreation(isFolder: false); break;
                case (Key.F4, ModifierKeys.None): e.Handled = true; OpenFileInEditor(readOnly: false); break;

                case (Key.F5, ModifierKeys.Alt): e.Handled = true; await DoPackAsync(); break;
                case (Key.F5, ModifierKeys.None): e.Handled = true; await DoCopyAsync(); break;

                case (Key.F6, ModifierKeys.None): e.Handled = true; await DoMoveAsync(); break;
                case (Key.F6, ModifierKeys.Shift): e.Handled = true; _activePane.RequestRename(); break;

                // Only the two combinations that mean something. The wildcard also
                // caught Ctrl+Delete and Alt+Delete, which belong to the shell and
                // to other shortcuts, and turned them into a delete prompt.
                case (Key.F8, ModifierKeys.None):
                case (Key.F8, ModifierKeys.Shift):
                case (Key.Delete, ModifierKeys.None):
                case (Key.Delete, ModifierKeys.Shift):
                    e.Handled = true;
                    bool permanent = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                    if (_activePane.CurrentPath.Equals(@"\\Network\", StringComparison.OrdinalIgnoreCase))
                    {
                        await DeleteSshSessionsAsync();
                    }
                    else
                    {
                        await DoDeleteAsync(permanent);
                    }
                    break;

                case (Key.F7, ModifierKeys.None): e.Handled = true; _activePane.StartCreation(isFolder: true); break;
                case (Key.F7, ModifierKeys.Alt): e.Handled = true; await OpenSearchAsync(); break;

                case (Key.C, ModifierKeys.Control): e.Handled = true; CopyFilesToClipboard(cut: false); break;
                case (Key.X, ModifierKeys.Control): e.Handled = true; CopyFilesToClipboard(cut: true); break;
                case (Key.V, ModifierKeys.Control): e.Handled = true; await PasteFilesFromClipboardAsync(); break;

                case (Key.C, ModifierKeys.Control | ModifierKeys.Shift): e.Handled = true; CopySelectedNamesToClipboard(fullPath: true); break;
                case (Key.C, ModifierKeys.Control | ModifierKeys.Alt): e.Handled = true; CopySelectedNamesToClipboard(fullPath: false); break;

                case (Key.F2, ModifierKeys.None): e.Handled = true; _activePane.RequestRename(); break;
                case (Key.Insert, ModifierKeys.None): e.Handled = true; _activePane.HandleInsertSelection(); break;
                case (Key.R, ModifierKeys.Control): e.Handled = true; ForgetFolderSizesUnder(_activePane.CurrentPath); await DoRefreshAsync(reloadIcons: true); break;
                case (Key.D, ModifierKeys.Control): e.Handled = true; OpenFavorites(); break;
                case (Key.D, ModifierKeys.Alt): e.Handled = true; await OpenFavoritesEditorAsync(); break;
                case (Key.U, ModifierKeys.Control): e.Handled = true; await SwapPanelsAsync(); break;
                case (Key.Tab, ModifierKeys.None): e.Handled = true; _inactivePane.FocusPanel(); break;
                case (Key.Escape, ModifierKeys.None):
                    // Folder size scans first: they are the long-running thing
                    // the user most likely wants to stop
                    if (_sizeScansRunning.Count > 0)
                    {
                        e.Handled = true;
                        CancelFolderSizes();
                    }
                    // Clear the cut state and restore normal opacity on Escape
                    else if (_clipboardCut)
                    {
                        e.Handled = true;
                        _clipboardCut = false;
                        Clipboard.Clear();
                        foreach (var item in _activePane.Items) item.IsCut = false;
                        foreach (var item in _inactivePane.Items) item.IsCut = false;
                        SetStatus("Ready.");
                    }
                    break;

                case (Key.Enter, ModifierKeys.Alt):
                    e.Handled = true;
                    if (_activePane.CurrentPath.Equals(@"\\Network\", StringComparison.OrdinalIgnoreCase))
                    {
                        // Edit session if it's a valid SSH connection item
                        if (_activePane.SelectedItem?.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            EditSshSession(_activePane.SelectedItem);
                        }
                        // Safely ignore Alt+Enter for "Add SSH connection" or other virtual network items
                    }
                    else if (_activePane.SelectedItem != null && _activePane.SelectedItem.Name != "..")
                    {
                        WindowsContextMenu.ShowProperties(_activePane.SelectedItem.FullPath, this);
                    }
                    break;

                case (Key.Enter, ModifierKeys.Alt | ModifierKeys.Shift):
                    e.Handled = true;
                    QueueAllFolderSizes(_activePane);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 1. Global hotkeys evaluated first. These must trigger even if focus
        // is inside the terminal or a text editor.
        if ((e.Key == Key.Oem3 || e.Key == Key.Oem8) && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ToggleGlobalTerminal();
            e.Handled = true;
            return;
        }

        // 2. Block the rest of the file manager navigation hotkeys if the application
        // is busy, or if focus is inside a text editor/terminal.
        if (_busy || IsTextEditorFocused()) return;

        try
        {
            switch (e.Key, Keyboard.Modifiers)
            {
                case (Key.A, ModifierKeys.Control):
                case (Key.Multiply, ModifierKeys.None):
                case (Key.D8, ModifierKeys.Shift):
                    e.Handled = true;
                    _activePane.SelectAllFiles();
                    break;

                case (Key.Space, ModifierKeys.None):
                    if (_activePane.IsQuickSearchActive) return;
                    e.Handled = true;
                    _activePane.HandleSpaceSelection();
                    break;

                case (Key.PageUp, ModifierKeys.Control):
                case (Key.Up, ModifierKeys.Control):
                    e.Handled = true;
                    await NavigateUpAsync(_activePane);
                    break;

                case (Key.PageDown, ModifierKeys.Control):
                    e.Handled = true;
                    await EnterItemOrGoForwardAsync(_activePane);
                    break;

                case (Key.Down, ModifierKeys.Control):
                    e.Handled = true;
                    await _activePane.NavigateForwardAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
    }

    // ===================== SSH sessions in the Network root =====================

    private SshSession? FindSshSession(string name) =>
        _settings.SshSessions.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            $"{s.Username}@{s.Host}".Equals(name, StringComparison.OrdinalIgnoreCase));

    private async Task DeleteSshSessionsAsync()
    {
        // SelectedItems already falls back to the row under the cursor, so the
        // extra fallback that used to sit here could never run
        var sessionsToDelete = _activePane.SelectedItems
            .Where(i => i.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sessionsToDelete.Count == 0) return;

        // Uses the themed dialog like every other confirmation in the app
        var dlg = new ConfirmDialog($"Are you sure you want to delete {sessionsToDelete.Count} SSH session(s)?", "Confirm Delete") { Owner = this };
        if (dlg.ShowDialog() != true) return;

        foreach (var item in sessionsToDelete)
        {
            var sessionToRemove = FindSshSession(item.Name);
            if (sessionToRemove != null) _settings.SshSessions.Remove(sessionToRemove);
        }

        _settings.Save();
        await _activePane.RefreshAsync();
        SetStatus($"Deleted {sessionsToDelete.Count} SSH session(s).");
    }

    private void EditSshSession(FileEntry item)
    {
        var sessionToEdit = FindSshSession(item.Name);
        if (sessionToEdit == null) return;

        var dlg = new SshConnectionWindow(sessionToEdit) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        Providers.SshFileSystemProvider.CloseConnection(item.Name);
        _settings.SshSessions.Remove(sessionToEdit);
        _settings.SshSessions.Add(dlg.Result);
        _settings.Save();
        _ = _activePane.RefreshAsync();
    }

    private bool _clipboardCut;

    private void CopyFilesToClipboard(bool cut)
    {
        var paths = new StringCollection();
        var itemsToCut = new List<FileEntry>();

        // Reset cut state for all items in both panes before applying new cut states
        foreach (var item in _activePane.Items) item.IsCut = false;
        foreach (var item in _inactivePane.Items) item.IsCut = false;

        foreach (var item in _activePane.SelectedItems)
        {
            if (item.Name != ".." && !string.IsNullOrEmpty(item.FullPath))
            {
                paths.Add(item.FullPath);
                itemsToCut.Add(item);
            }
        }

        if (paths.Count == 0) return;

        var data = new DataObject();
        data.SetFileDropList(paths);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 1)));
        Clipboard.SetDataObject(data, true);

        _clipboardCut = cut;

        if (cut)
        {
            // Dim the cut items to provide visual feedback
            foreach (var item in itemsToCut) item.IsCut = true;
        }

        SetStatus(cut ? $"Cut {paths.Count} item(s)." : $"Copied {paths.Count} item(s).");
    }

    private async Task PasteFilesFromClipboardAsync()
    {
        if (!Clipboard.ContainsFileDropList()) return;

        var items = new List<FileEntry>();
        foreach (string? path in Clipboard.GetFileDropList())
        {
            if (string.IsNullOrEmpty(path)) continue;
            items.Add(new FileEntry
            {
                Name = Path.GetFileName(path.TrimEnd('\\', '/')),
                FullPath = path,
                IsFolder = Directory.Exists(path)
            });
        }

        if (items.Count == 0) return;

        bool move = _clipboardCut;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data != null &&
                data.GetDataPresent("Preferred DropEffect") &&
                data.GetData("Preferred DropEffect") is MemoryStream stream)
            {
                byte[] buffer = new byte[4];
                stream.Position = 0;
                if (stream.Read(buffer, 0, 4) >= 4)
                    move = (BitConverter.ToInt32(buffer, 0) & 2) != 0;
            }
        }
        catch
        {
        }

        await StartTransferAsync(
            items,
            null,
            _activePane,
            move ? FileOperation.Move : FileOperation.Copy,
            _activePane.CurrentPath);

        // A cut is used up by its paste, as in Explorer: the clipboard still
        // pointed at the old locations, and a second Ctrl+V tried to move files
        // that were no longer there. Cleared only if something really moved, so
        // a paste refused up front (e.g. into the same folder) keeps the cut.
        if (move && items.Any(i => !File.Exists(i.FullPath) && !Directory.Exists(i.FullPath)))
        {
            _clipboardCut = false;
            try { Clipboard.Clear(); } catch { }
        }
    }
}
