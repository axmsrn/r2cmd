using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace R2Cmd.Controls;

public partial class FilePaneControl
{
    public class FilesDroppedEventArgs : EventArgs
    {
        public List<FileEntry> Items { get; }
        public bool IsMove { get; }
        public string TargetPath { get; }

        public FilesDroppedEventArgs(List<FileEntry> items, bool isMove, string targetPath)
        {
            Items = items;
            IsMove = isMove;
            TargetPath = targetPath;
        }
    }

    public event EventHandler<FilesDroppedEventArgs>? FilesDropped;

    private static readonly string s_dragSourceFormat = "R2Cmd_Source";

    public static readonly DependencyProperty IsDropTargetProperty =
        DependencyProperty.RegisterAttached("IsDropTarget", typeof(bool), typeof(FilePaneControl), new PropertyMetadata(false));

    public static void SetIsDropTarget(UIElement element, bool value) => element.SetValue(IsDropTargetProperty, value);
    public static bool GetIsDropTarget(UIElement element) => (bool)element.GetValue(IsDropTargetProperty);

    private ListViewItem? _currentDropTarget;

    // The entry the pointer was over on the previous DragOver. That event fires
    // continuously, and looking the container up again for the same row is work
    // for nothing.
    private FileEntry? _lastDragOverEntry;

    private void ClearDropTarget()
    {
        if (_currentDropTarget != null)
        {
            SetIsDropTarget(_currentDropTarget, false);
            _currentDropTarget = null;
        }

        _lastDragOverEntry = null;
    }

    static FilePaneControl()
    {
        EventManager.RegisterClassHandler(typeof(FilePaneControl), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnPaneLoaded));
    }

    private static void OnPaneLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FilePaneControl pane && pane.lvFiles != null && !pane.lvFiles.AllowDrop)
        {
            pane.lvFiles.AllowDrop = true;
            pane.lvFiles.DragEnter += pane.LvFiles_DragEnter;
            pane.lvFiles.DragOver += pane.LvFiles_DragOver;
            pane.lvFiles.DragLeave += pane.LvFiles_DragLeave;
            pane.lvFiles.Drop += pane.LvFiles_Drop;
        }
    }

    private void LvFiles_DragLeave(object sender, DragEventArgs e)
    {
        ClearDropTarget();
    }

    private bool IsSameSource(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(s_dragSourceFormat)) return false;
        var source = e.Data.GetData(s_dragSourceFormat) as FilePaneControl;
        return ReferenceEquals(source, this);
    }

    // Quick search is on, so part of the listing is hidden behind a view filter.
    // The window level hotkey handler needs this to leave Space to the filter.
    public bool IsQuickSearchActive => !string.IsNullOrEmpty(_quickSearchText);

    // Resolves the currently focused or selected ListViewItem.
    private ListViewItem? GetFocusedOrSelectedContainer()
    {
        if (Keyboard.FocusedElement is DependencyObject focusedElement &&
            FindAncestor<ListViewItem>(focusedElement) is ListViewItem container)
        {
            return container;
        }

        object? targetItem = lvFiles.SelectedItem ?? (lvFiles.Items.Count > 0 ? lvFiles.Items[0] : null);
        if (targetItem == null) return null;

        // Return the container if it is already realized.
        // We drop the forced UpdateLayout() here to prevent UI thread stalling.
        return lvFiles.ItemContainerGenerator.ContainerFromItem(targetItem) as ListViewItem;
    }

    // Scrolls an item into view and gives it the keyboard focus asynchronously.
    // This avoids forcing a synchronous layout pass (UpdateLayout),
    // eliminating micro-freezes during rapid keyboard navigation.
    private void FocusListItem(object item)
    {
        lvFiles.ScrollIntoView(item);

        // Yield to the UI thread to allow the virtualizing panel to realize the new container
        // before we attempt to focus it
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (lvFiles.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
            {
                container.Focus();
            }
        }), DispatcherPriority.Loaded);
    }

    private void MoveFocusToNextItem(int currentIndex)
    {
        if (currentIndex < 0 || currentIndex >= lvFiles.Items.Count - 1) return;
        FocusListItem(lvFiles.Items[currentIndex + 1]);
    }

    public void ClearSelection()
    {
        if (_renamingEntry != null) return; // Block clearing selection during rename

        // Clearing covers everything, including rows hidden by a quick search
        // filter, because those marks would otherwise survive invisibly
        foreach (var item in Items)
        {
            if (item.IsMarked) item.IsMarked = false;
        }
        UpdateMarkedStatus();

        GetFocusedOrSelectedContainer()?.Focus();
    }

    public void SelectAllFiles()
    {
        // Redirect Ctrl+A to the text box if renaming is in progress
        if (_renamingEntry != null)
        {
            if (_renameBox != null)
            {
                _renameBox.Focus();
                _renameBox.SelectAll();
            }
            return;
        }

        var container = GetFocusedOrSelectedContainer();

        // Avoid LINQ OfType() and ToList() overhead on the UI collection
        var selectable = new List<FileEntry>(lvFiles.Items.Count);
        foreach (var item in lvFiles.Items)
        {
            if (item is FileEntry entry && entry.Name != "..")
                selectable.Add(entry);
        }

        if (selectable.Count == 0) return;

        bool targetState = !selectable.All(i => i.IsMarked);

        // Assign only where the value actually changes: a PropertyChanged per row
        // across a few thousand files is enough to stall the UI thread
        foreach (var item in selectable)
        {
            if (item.IsMarked != targetState) item.IsMarked = targetState;
        }

        UpdateMarkedStatus();

        container?.Focus();
    }

    private void UserControl_GotFocus(object sender, RoutedEventArgs e) => PaneGotFocus?.Invoke(this, EventArgs.Empty);

    private void LvFiles_GotFocus(object sender, RoutedEventArgs e) => PaneGotFocus?.Invoke(this, EventArgs.Empty);

    private void LstDrives_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is DriveItem di)
        {
            string drivePath = di.Name == "NET" ? @"\\Network\" : di.Name + "\\";
            var owner = Window.GetWindow(this);
            WindowsContextMenu.Show(drivePath, owner);
            e.Handled = true;
        }
    }

    private bool _ctrlClickAddedMark;
    private FileEntry? _ctrlClickItem;

    private void LvFiles_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        PaneGotFocus?.Invoke(this, EventArgs.Empty);
        _renameClickTimer?.Stop();

        _dragStartPoint = null;
        _dragStartItem = null;
        _ctrlClickItem = null;

        var itemUnderMouse = EntryFromSource(e.OriginalSource);
        if (itemUnderMouse == null) FocusPanel();

        if (_renamingEntry != null)
        {
            if (!ClickInsideRenameBox(e)) CommitActiveRename();
            return;
        }

        if (e.ClickCount == 2)
        {
            if (itemUnderMouse != null)
            {
                e.Handled = true;
                if (!_isBusy) ItemExecuted?.Invoke(this, itemUnderMouse);
            }
            return;
        }

        _selectionAtMouseDown = lvFiles.SelectedItem as FileEntry;

        // Record drag origin before any modifier checks. This allows starting a drag
        // immediately after a Shift-click or Ctrl-click, just like in Total Commander.
        if (itemUnderMouse != null && itemUnderMouse.Name != "..")
        {
            _dragStartPoint = e.GetPosition(null);
            _dragStartItem = itemUnderMouse;
            _dragStartTime = DateTime.UtcNow;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control &&
            itemUnderMouse != null && itemUnderMouse.Name != "..")
        {
            e.Handled = true;
            _ctrlClickItem = itemUnderMouse;

            // Do not unmark an already marked item on MouseDown. If we do, starting a drag
            // on a marked item with Ctrl held will deselect it and drag only that single file.
            // Unmarking is deferred to MouseUp if no drag occurred.
            if (!itemUnderMouse.IsMarked)
            {
                itemUnderMouse.IsMarked = true;
                _ctrlClickAddedMark = true;
                UpdateMarkedStatus();
            }
            else
            {
                _ctrlClickAddedMark = false;
            }

            lvFiles.SelectedItem = itemUnderMouse;
            FocusListItem(itemUnderMouse);
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Shift && itemUnderMouse != null)
        {
            e.Handled = true;

            var from = lvFiles.SelectedItem as FileEntry ?? itemUnderMouse;
            int a = lvFiles.Items.IndexOf(from);
            int b = lvFiles.Items.IndexOf(itemUnderMouse);
            if (a < 0) a = b;

            if (b >= 0)
            {
                int fromIdx = Math.Min(a, b);
                int toIdx = Math.Max(a, b);
                for (int i = fromIdx; i <= toIdx; i++)
                {
                    if (lvFiles.Items[i] is FileEntry entry && entry.Name != "..")
                        entry.IsMarked = true;
                }

                UpdateMarkedStatus();
            }

            lvFiles.SelectedItem = itemUnderMouse;
            FocusListItem(itemUnderMouse);
            return;
        }
    }

    private void LvFiles_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = null;
        _dragStartItem = null;

        if (_renamingEntry != null || e.ClickCount != 1) return;

        var mouseItem = EntryFromSource(e.OriginalSource);
        if (mouseItem == null || mouseItem.Name == "..") return;

        // If Ctrl was held and we clicked an already marked item without dragging, unmark it now.
        // This ensures unmarking happens correctly without breaking drag-and-drop.
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (!_ctrlClickAddedMark && mouseItem.IsMarked && ReferenceEquals(mouseItem, _ctrlClickItem))
            {
                mouseItem.IsMarked = false;
                UpdateMarkedStatus();
            }
            e.Handled = true;
            return;
        }

        if (!ReferenceEquals(mouseItem, _selectionAtMouseDown)) return;

        ScheduleMouseRename(mouseItem);
    }

    private void LvFiles_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging || _isBusy || _renamingEntry != null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_dragStartPoint == null || _dragStartItem == null) return;

        // Ignore tiny accidental moves and very short clicks
        if ((DateTime.UtcNow - _dragStartTime).TotalMilliseconds < DragDelayMs)
            return;

        Point pos = e.GetPosition(null);
        Vector diff = _dragStartPoint.Value - pos;

        double thresholdX = Math.Max(SystemParameters.MinimumHorizontalDragDistance, DragThresholdPx);
        double thresholdY = Math.Max(SystemParameters.MinimumVerticalDragDistance, DragThresholdPx);

        if (Math.Abs(diff.X) < thresholdX && Math.Abs(diff.Y) < thresholdY)
            return;

        StartFileDrag(_dragStartItem);
    }

    private void StartFileDrag(FileEntry startItem)
    {
        var items = SelectedItems;
        if (!items.Any(i => ReferenceEquals(i, startItem)))
            items = new List<FileEntry> { startItem };

        var paths = items
            .Where(f => f.Name != ".." && !string.IsNullOrEmpty(f.FullPath) && f.FullPath != ":::NEW:::")
            .Where(f => File.Exists(f.FullPath) || Directory.Exists(f.FullPath))
            .Select(f => f.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (paths.Length == 0) return;

        _renameClickTimer?.Stop();
        _renameClickTimer = null;
        _dragStartPoint = null;
        _dragStartItem = null;
        _ctrlClickItem = null;

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        data.SetData(DataFormats.Text, string.Join(Environment.NewLine, paths));
        data.SetData(s_dragSourceFormat, this);

        _isDragging = true;
        try
        {
            // Link is deliberately absent: nothing in the app creates shortcuts,
            // and offering it means Ctrl+Shift+drag into Explorer produces a .lnk
            // where the user expected a copy
            DragDrop.DoDragDrop(lvFiles, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"Drag error: {ex.Message}");
        }
        finally
        {
            _isDragging = false;
        }
    }

    private void LvFiles_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_renamingEntry != null) return; // Block arrow navigation during rename

        if ((e.Key == Key.Up || e.Key == Key.Down) && Keyboard.Modifiers == ModifierKeys.None)
        {
            var focusedContainer = GetFocusedOrSelectedContainer();
            if (focusedContainer == null) return;

            e.Handled = true;

            int idx = lvFiles.ItemContainerGenerator.IndexFromContainer(focusedContainer);
            int newIdx = e.Key == Key.Up ? idx - 1 : idx + 1;

            if (newIdx >= 0 && newIdx < lvFiles.Items.Count)
            {
                FocusListItem(lvFiles.Items[newIdx]);
            }
        }
    }

    private void LvFiles_KeyDown(object sender, KeyEventArgs e)
    {
        if (_renamingEntry != null) return;

        if (e.Key == Key.Enter && lvFiles.SelectedItem is FileEntry entry)
        {
            e.Handled = true;

            if (!string.IsNullOrEmpty(_quickSearchText))
                ClearQuickSearch();

            ItemExecuted?.Invoke(this, entry);
        }
    }

    private CancellationTokenSource? _selectionCountCts;
    private string _lastMarkedStatus = "";

    private void LvFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Update path display when in search results mode
        if (_isSearchResults)
        {
            if (lvFiles.SelectedItem is FileEntry entry && entry.Name != "..")
            {
                string dir = entry.DirectoryDisplay;
                if (string.IsNullOrEmpty(dir))
                    dir = entry.FullPath;

                UpdateBreadcrumbs(dir);
            }
            else
            {
                UpdateBreadcrumbs(CurrentPath);
            }
        }
    }

    // The recount is queued rather than run immediately; this flag collapses a
    // burst of requests into one.
    private bool _markedStatusQueued;

    // =========================================================================
    // Reports how many items are marked, and asynchronously calculates the
    // total number of files and total size for marked directories.
    //
    // The count itself is deferred to Background priority. It walks the whole
    // listing, and it is asked for on every arrow key through SelectionChanged,
    // on every Space, and once per row crossed during a right button drag — a
    // held arrow key or a drag across a large folder used to mean one full pass
    // per event. Background priority runs once the input queue has drained, so
    // a burst of five hundred requests costs a single pass.
    // =========================================================================
    private void UpdateMarkedStatus()
    {
        if (_isBusy || _markedStatusQueued) return;

        _markedStatusQueued = true;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _markedStatusQueued = false;
            if (!_isBusy) RecomputeMarkedStatus();
        }), DispatcherPriority.Background);
    }

    // Hidden and system files must be visible to the enumerator, otherwise the
    // reported selection is smaller than what a copy or a delete will actually
    // touch. Reparse points are never followed: a junction belongs to its target.
    private static readonly EnumerationOptions s_selectionEnumOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true
    };

    // =========================================================================
    // Totals of marked folders that have already been counted.
    //
    // Every mark used to recount ALL marked folders from scratch: marking ten
    // folders one by one walked 1+2+...+10 = 55 trees, and over SSH each walk is
    // a remote command that cannot be cancelled. Now each folder is counted
    // once and its result reused while it stays marked.
    //
    // Keyed by the FileEntry object, not the path: a new listing (navigation or
    // reload) creates new entries, so stale totals are never looked up again,
    // and the garbage collector drops them without any clearing code.
    // =========================================================================
    private readonly ConditionalWeakTable<FileEntry, FolderTotals> _folderTotals = new();

    private sealed record FolderTotals(long Files, long Bytes);

    /// <summary>
    /// Takes the totals of a folder counted by the host's folder size scan, so
    /// the selection status does not walk the same tree a second time.
    /// </summary>
    public void SetFolderTotals(FileEntry entry, long files, long bytes)
    {
        _folderTotals.AddOrUpdate(entry, new FolderTotals(files, bytes));
        if (entry.IsMarked) UpdateMarkedStatus();
    }

    private void RecomputeMarkedStatus()
    {
        // We only count explicitly marked items here.
        // We do not count the currently focused item to prevent aggressive
        // disk scanning during normal keyboard navigation (Up/Down arrows).
        long directFilesCount = 0;
        long directFoldersCount = 0;
        long directSize = 0;
        int foldersBeingSized = 0;
        List<FileEntry>? foldersToCount = null;

        foreach (var item in Items)
        {
            if (!item.IsMarked || item.Name == "..") continue;

            if (item.IsFolder)
            {
                directFoldersCount++;

                // Already counted: only the folders never seen before are scanned
                if (_folderTotals.TryGetValue(item, out var known))
                {
                    directFilesCount += known.Files;
                    directSize += known.Bytes;
                }
                // The folder size scan (Space) is walking this tree right now and
                // hands its file count over when done: a second walk of the same
                // tree here only slowed both down
                else if (item.SizeCalculating)
                {
                    foldersBeingSized++;
                }
                else
                {
                    (foldersToCount ??= new List<FileEntry>()).Add(item);
                }
            }
            else
            {
                directFilesCount++;
                directSize += item.Size;
            }
        }

        if (directFilesCount == 0 && directFoldersCount == 0)
        {
            SetStatusMessage("Ready.");
            return;
        }

        // Cancel any pending background size calculation to avoid disk I/O flooding
        _selectionCountCts?.Cancel();
        _selectionCountCts = new CancellationTokenSource();
        var token = _selectionCountCts.Token;

        // Only files, or folders that are all counted already: report instantly
        if (foldersToCount == null && foldersBeingSized == 0)
        {
            SetStatusMessage($"Selected: {directFilesCount} file(s) ({Helpers.FormatSize(directSize)})");
            return;
        }

        // If folders are marked, show an intermediate message and start the background scanner
        SetStatusMessage($"Selected: {directFilesCount} file(s), {directFoldersCount} folder(s) ... calculating...");

        // Nothing to walk here: the size scan reports back through SetFolderTotals
        if (foldersToCount == null) return;

        var folders = foldersToCount;

        Task.Run(() =>
        {
            long totalFiles = directFilesCount;
            long totalSize = directSize;

            foreach (var folderEntry in folders)
            {
                if (token.IsCancellationRequested) return;

                string folder = folderEntry.FullPath;
                long folderFiles = 0;
                long folderSize = 0;

                try
                {
                    if (folder.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                    {
                        (folderFiles, folderSize) = Providers.SshFileSystemProvider.RemoteSumTree(folder, token);
                    }
                    else if (Directory.Exists(folder))
                    {
                        var di = new DirectoryInfo(folder);

                        // Skip symbolic links to avoid infinite recursion loops
                        if ((di.Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            // FileSystemEnumerable is highly optimized as it reads directly from the OS kernel
                            // and does not allocate strings or FileInfo objects in memory.
                            var enumerable = new FileSystemEnumerable<long>(
                                folder,
                                (ref FileSystemEntry entry) => entry.Length,
                                s_selectionEnumOptions)
                            {
                                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,

                                // A nested junction points outside this tree; walking
                                // into it counted somebody else's files as ours
                                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                                    (entry.Attributes & FileAttributes.ReparsePoint) == 0
                            };

                            foreach (var size in enumerable)
                            {
                                // A folder abandoned halfway is not stored
                                if (token.IsCancellationRequested) return;
                                folderFiles++;
                                folderSize += size;
                            }
                        }
                    }

                    // Stored even if this pass gets cancelled right after: the
                    // next mark reuses the finished folders instead of redoing them
                    _folderTotals.AddOrUpdate(folderEntry, new FolderTotals(folderFiles, folderSize));
                }
                catch { /* Ignore inaccessible folders (Access Denied) */ }

                totalFiles += folderFiles;
                totalSize += folderSize;
            }

            if (token.IsCancellationRequested) return;

            Dispatcher.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    SetStatusMessage($"Selected: {totalFiles} file(s) ({Helpers.FormatSize(totalSize)})");
                }
            }, DispatcherPriority.Background);
        }, token);
    }

    // Helper method to prevent UI flashing by suppressing identical string updates
    private void SetStatusMessage(string message)
    {
        if (message == _lastMarkedStatus) return;
        _lastMarkedStatus = message;
        StatusMessage?.Invoke(this, message);
    }

    // O(1) resolver for mouse and drag events.
    // Reads DataContext directly instead of walking the visual tree via FindAncestor.
    // This entirely prevents CPU spikes and UI micro-freezes during rapid mouse movements.
    private static FileEntry? EntryFromSource(object? originalSource)
    {
        if (originalSource is FrameworkElement fe)
            return fe.DataContext as FileEntry;

        if (originalSource is FrameworkContentElement fce)
            return fce.DataContext as FileEntry;

        return null;
    }

    private void UserControl_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (_isBusy || _renamingEntry != null || string.IsNullOrWhiteSpace(e.Text) || char.IsControl(e.Text[0])) return;
        _quickSearchText += e.Text;
        UpdateQuickSearch();
        e.Handled = true;
    }

    private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isBusy || _renamingEntry != null) return;

        if (!string.IsNullOrEmpty(_quickSearchText))
        {
            if (e.Key == Key.Escape) { e.Handled = true; ClearQuickSearch(); return; }
            if (e.Key == Key.Back) { e.Handled = true; _quickSearchText = _quickSearchText.Substring(0, _quickSearchText.Length - 1); UpdateQuickSearch(); return; }

            // NOTE: this branch only runs if the window level handler lets Space
            // through. See IsQuickSearchActive and the note in MainWindow.
            if (e.Key == Key.Space) { e.Handled = true; _quickSearchText += " "; UpdateQuickSearch(); return; }
        }
    }

    private void UpdateQuickSearch()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(lvFiles.ItemsSource);
        if (string.IsNullOrEmpty(_quickSearchText))
        {
            pnlSearch.Visibility = Visibility.Collapsed;
            view.Filter = null;
        }
        else
        {
            pnlSearch.Visibility = Visibility.Visible;
            txtSearch.Text = $"Filter: {_quickSearchText}";

            // The filter text is captured by value. Reading the field from inside
            // the predicate meant every row of every later refresh was matched
            // against whatever the field happened to hold at that moment.
            string needle = _quickSearchText;
            view.Filter = obj => (obj is FileEntry entry) && (entry.Name == ".." || entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));

            if (lvFiles.Items.Count > 0) { lvFiles.SelectedIndex = 0; lvFiles.ScrollIntoView(lvFiles.Items[0]); }
        }
    }

    public void ClearQuickSearch()
    {
        if (string.IsNullOrEmpty(_quickSearchText)) return;
        _quickSearchText = "";
        UpdateQuickSearch();
    }

    public void HandleInsertSelection()
    {
        if (_renamingEntry != null) return; // Block Insert during rename

        var focusedContainer = GetFocusedOrSelectedContainer();

        if (focusedContainer != null && focusedContainer.DataContext is FileEntry entry)
        {
            if (entry.Name != "..") entry.IsMarked = !entry.IsMarked;
            UpdateMarkedStatus();

            int idx = lvFiles.ItemContainerGenerator.IndexFromContainer(focusedContainer);
            MoveFocusToNextItem(idx);
        }
    }

    public void HandleSpaceSelection()
    {
        if (_renamingEntry != null) return; // Block Space during rename

        var focusedContainer = GetFocusedOrSelectedContainer();

        if (focusedContainer != null && focusedContainer.DataContext is FileEntry entry)
        {
            if (entry.Name != "..")
            {
                entry.IsMarked = !entry.IsMarked;
                UpdateMarkedStatus();

                // Unmarking a folder with a known size must not walk it again
                if (entry.IsFolder && !entry.SizeKnown) SizeCalculationRequested?.Invoke(this, entry);
            }

            int idx = lvFiles.ItemContainerGenerator.IndexFromContainer(focusedContainer);
            MoveFocusToNextItem(idx);
        }
    }

    // Drop target is refused for the same reasons in all three handlers
    private bool IsDropRefused(DragEventArgs e, FileEntry? dropItem)
    {
        if (_isBusy || CurrentPath.Equals(@"\\Network\", StringComparison.OrdinalIgnoreCase))
            return true;

        // If the drag originated from this exact same panel, it is only allowed
        // if dropping directly into a subfolder. Dropping into the panel's background
        // would mean copying a file to its own current location.
        bool isSameSource = IsSameSource(e);
        bool isDropOnSubfolder = dropItem != null && dropItem.IsFolder && dropItem.Name != "..";

        if (isSameSource && !isDropOnSubfolder)
            return true;

        return false;
    }

    // Shift means move, exactly as in DragOver. Without this the very first frame
    // of the drag showed a copy cursor and then flipped.
    private static DragDropEffects EffectFor(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;

        bool isShift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        return isShift ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    private void LvFiles_DragEnter(object sender, DragEventArgs e)
    {
        var dropItem = EntryFromSource(e.OriginalSource);
        e.Effects = IsDropRefused(e, dropItem) ? DragDropEffects.None : EffectFor(e);
        e.Handled = true;
    }

    private void LvFiles_DragOver(object sender, DragEventArgs e)
    {
        var entry = EntryFromSource(e.OriginalSource);
        e.Effects = IsDropRefused(e, entry) ? DragDropEffects.None : EffectFor(e);
        e.Handled = true;

        if (e.Effects == DragDropEffects.None)
        {
            ClearDropTarget();
            return;
        }

        // DragOver fires on every mouse move. Staying on the same row cannot
        // change the highlight, and the container lookup below is not free.
        if (ReferenceEquals(entry, _lastDragOverEntry)) return;
        _lastDragOverEntry = entry;

        ListViewItem? newTarget = null;

        if (entry != null && entry.IsFolder && entry.Name != "..")
        {
            newTarget = lvFiles.ItemContainerGenerator.ContainerFromItem(entry) as ListViewItem;
        }

        if (_currentDropTarget != newTarget)
        {
            if (_currentDropTarget != null) SetIsDropTarget(_currentDropTarget, false);

            _currentDropTarget = newTarget;

            if (_currentDropTarget != null)
            {
                SetIsDropTarget(_currentDropTarget, true);
            }
        }
    }

    private void LvFiles_Drop(object sender, DragEventArgs e)
    {
        var dropItem = EntryFromSource(e.OriginalSource);
        ClearDropTarget();

        // The Network root check was missing here, so a drop could still be
        // reported for a location that refuses it during the drag
        if (IsDropRefused(e, dropItem))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && paths.Length > 0)
            {
                var items = new List<FileEntry>(paths.Length);
                foreach (var path in paths)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            var fi = new FileInfo(path);
                            items.Add(new FileEntry
                            {
                                Name = fi.Name,
                                FullPath = fi.FullName,
                                IsFolder = false,
                                Size = fi.Length,
                                Modified = fi.LastWriteTime
                            });
                        }
                        else if (Directory.Exists(path))
                        {
                            var di = new DirectoryInfo(path);
                            items.Add(new FileEntry
                            {
                                Name = di.Name,
                                FullPath = di.FullName,
                                IsFolder = true
                            });
                        }
                    }
                    catch { }
                }

                if (items.Count > 0)
                {
                    // Resolve the exact destination path based on cursor position
                    string dropTargetPath = CurrentPath;

                    if (dropItem != null && dropItem.IsFolder && dropItem.Name != "..")
                    {
                        dropTargetPath = dropItem.FullPath;
                    }

                    // Evaluate the key states (Shift) at the exact moment of the drop
                    // instead of relying on e.Effects, which the OS may overwrite
                    bool isMove = EffectFor(e) == DragDropEffects.Move;
                    FilesDropped?.Invoke(this, new FilesDroppedEventArgs(items, isMove, dropTargetPath));
                }
            }
        }

        // Inform the source (e.g., Windows Explorer) that the operation is fully
        // handled here. This prevents the OS from attempting to delete the source
        // file itself after we have already processed the move operation.
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }
}
