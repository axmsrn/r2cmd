using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using R2Cmd.Providers;

namespace R2Cmd.Controls;

public partial class FilePaneControl : UserControl
{
    #region Properties and fields

    public string CurrentPath { get; private set; } = "";
    public string SortColumn { get; set; } = "Name";
    public bool SortAscending { get; set; } = true;
    public BulkObservableCollection<FileEntry> Items { get; } = new();

    public FileEntry? SelectedItem => lvFiles.SelectedItem as FileEntry;

    public List<FileEntry> SelectedItems
    {
        get
        {
            var marked = Items.Where(e => e.IsMarked && e.Name != "..").ToList();
            if (marked.Count > 0) return marked;

            var cursor = SelectedItem;
            if (cursor != null && cursor.Name != "..") return new List<FileEntry> { cursor };
            return new List<FileEntry>();
        }
    }

    private CancellationTokenSource? _navCts;
    private System.Windows.Threading.DispatcherTimer? _loadingAnimTimer;
    private int _requestId;
    private bool _isBusy;

    /// <summary>True while this pane is reading a directory. The host uses it for the wait cursor.</summary>
    public bool IsBusy => _isBusy;

    private bool _suppressDriveSelectionChanged;
    private FileSystemWatcher? _watcher;
    private string _quickSearchText = "";

    private FileEntry? _renamingEntry;
    private bool _renameInProgress;
    private FileEntry? _selectionAtMouseDown;
    private System.Windows.Threading.DispatcherTimer? _renameClickTimer;
    private TextBox? _renameBox;

    private Point? _dragStartPoint;
    private FileEntry? _dragStartItem;
    private bool _isDragging;
    private DateTime _dragStartTime;
    private const int DragDelayMs = 180;
    private const double DragThresholdPx = 12;

    private bool _isRightDragSelecting;
    private bool _rightDragTargetState;
    private bool _rightDragMarkApplied;
    private FileEntry? _lastRightDragItem;
    private Point _rightDragStartPoint;
    private Point _lastRightDragHitPoint;
    private bool _isSearchResults;

    private readonly Stack<string> _forwardHistory = new();
    private List<(string Text, string Path)> _crumbParts = new();
    private readonly List<(FrameworkElement Hit, FrameworkElement? Sep, double Width)> _crumbVisuals = new();
    private FrameworkElement? _crumbEllipsis;
    private double _crumbEllipsisWidth;
    private double _crumbPrefixWidth;
    private double _lastCrumbAvail = -1;

    private static readonly ConcurrentDictionary<string, long> s_driveTotals =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Brush s_breadcrumbHoverBrush = CreateFrozen(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));

    public event EventHandler? PathChanged;
    public event EventHandler<FileEntry>? ItemExecuted;
    public event EventHandler<string>? StatusMessage;
    public event EventHandler? PaneGotFocus;
    public event EventHandler? BusyStateChanged;
    public event EventHandler? DirectoryModified;
    public event EventHandler<FileEntry>? SizeCalculationRequested;

    public Func<string, string>? SyncPathResolver;

    // Use .NET 10 source generator for zero-allocation interop
    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    #endregion

    #region Initialization

    public FilePaneControl()
    {
        InitializeComponent();
        lvFiles.ItemsSource = Items;
        lvFiles.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnColumnHeaderClick));

        lvFiles.PreviewMouseMove += LvFiles_MouseMoveRouter;
        lstDrives.PreviewMouseLeftButtonDown += LstDrives_PreviewMouseLeftButtonDown;
        lvFiles.PreviewMouseRightButtonDown += LvFiles_PreviewMouseRightButtonDown;
        lvFiles.PreviewMouseRightButtonUp += LvFiles_PreviewMouseRightButtonUp;
        lvFiles.PreviewMouseLeftButtonDown += LvFiles_LoadingParentClick;
        lstDrives.PreviewMouseRightButtonDown += LstDrives_PreviewMouseRightButtonDown;
        pnlBreadcrumbs.MouseRightButtonDown += PnlBreadcrumbs_MouseRightButtonDown;
        pnlBreadcrumbs.SizeChanged += OnBreadcrumbsSizeChanged;
        lvFiles.SizeChanged += (s, e) => AutoSizeNameColumn();

        Unloaded += (s, e) =>
        {
            StopWatcher();
            StopLoadingAnimation();
        };
    }

    private static Brush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    #endregion

    #region Loading animation

    private void StartLoadingAnimation(string parentPath)
    {
        StopLoadingAnimation();

        var loadingEntry = new FileEntry
        {
            Name = "..",
            FullPath = "",
            IsFolder = true
        };

        Items.ReplaceAll(new List<FileEntry> { loadingEntry });
        SetSelectedItem(loadingEntry, takeFocus: true);

        bool toggle = false;
        _loadingAnimTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(380)
        };
        _loadingAnimTimer.Tick += (s, e) =>
        {
            toggle = !toggle;
            string newName = toggle ? ". ." : "..";

            if (lvFiles.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem lvi)
            {
                // SetCurrentValue keeps the {Binding Name} alive. A plain
                // assignment replaced it, and since rows are recycled, the
                // container later showed ". ." instead of another file's name.
                _loadingTextBlock = FindVisualTextBlock(lvi);
                _loadingTextBlock?.SetCurrentValue(TextBlock.TextProperty, newName);
            }
        };
        _loadingAnimTimer.Start();
    }

    private TextBlock? _loadingTextBlock;

    private void StopLoadingAnimation()
    {
        _loadingAnimTimer?.Stop();
        _loadingAnimTimer = null;

        // Show the bound name again
        _loadingTextBlock?.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        _loadingTextBlock = null;
    }

    private DateTime _loadingParentClickTime;

    private void LvFiles_LoadingParentClick(object sender, MouseButtonEventArgs e)
    {
        if (_loadingAnimTimer == null) return;

        DateTime now = DateTime.UtcNow;
        int elapsed = (int)(now - _loadingParentClickTime).TotalMilliseconds;
        _loadingParentClickTime = now;

        if (elapsed <= 0 || elapsed > GetDoubleClickTime())
            return;

        var entry = Items.FirstOrDefault(i => i.Name == "..");
        if (entry == null) return;

        e.Handled = true;
        ItemExecuted?.Invoke(this, entry);
    }
    #endregion

    #region Drive bar

    public static List<DriveItem> ScanDrives()
    {
        var list = new List<DriveItem>();

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady) list.Add(new DriveItem { Name = d.Name.TrimEnd('\\'), Type = d.DriveType });
            }
            catch { }
        }

        list.Add(new DriveItem { Name = "NET", Type = DriveType.Network });
        return list;
    }

    public void ApplyDrives(IEnumerable<DriveItem> drives)
    {
        lstDrives.ItemsSource = drives;
        UpdateDriveSelection();
    }

    private void UpdateDriveSelection()
    {
        _suppressDriveSelectionChanged = true;
        try
        {
            string root;
            if (CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                root = "";
            else if (CurrentPath.StartsWith(@"\\"))
                root = "NET";
            else
                root = (Path.GetPathRoot(CurrentPath) ?? "").TrimEnd('\\');

            if (string.IsNullOrEmpty(root))
            {
                lstDrives.SelectedItem = null;
                return;
            }

            foreach (var obj in lstDrives.Items)
            {
                if (obj is DriveItem di && string.Equals(di.Name, root, StringComparison.OrdinalIgnoreCase))
                {
                    lstDrives.SelectedItem = di;
                    return;
                }
            }
            lstDrives.SelectedItem = null;
        }
        finally { _suppressDriveSelectionChanged = false; }
    }

    private async void OnDriveSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDriveSelectionChanged) return;
        if (lstDrives.SelectedItem is DriveItem di) await NavigateToDrive(di);
    }

    private async void LstDrives_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is DriveItem di &&
            ReferenceEquals(lstDrives.SelectedItem, di))
        {
            await NavigateToDrive(di);
        }
    }

    #endregion

    #region Column widths

    private const double NameColumnMin = 160;
    private bool _columnWidthsPinned;
    private double _lastAutoNameWidth = -1;

    public List<double> GetColumnWidths()
    {
        var widths = new List<double>();
        if (lvFiles.View is not GridView grid) return widths;

        foreach (var column in grid.Columns)
        {
            double width = column.ActualWidth > 0 ? column.ActualWidth : column.Width;
            widths.Add(double.IsNaN(width) ? 0 : width);
        }

        return widths;
    }

    public void ApplyColumnWidths(IReadOnlyList<double>? widths)
    {
        if (lvFiles.View is not GridView grid) return;
        if (widths == null || widths.Count != grid.Columns.Count) return;

        foreach (double width in widths)
        {
            if (double.IsNaN(width) || width < 20 || width > 4000) return;
        }

        for (int i = 0; i < widths.Count; i++) grid.Columns[i].Width = widths[i];

        _columnWidthsPinned = true;
    }

    private void AutoSizeNameColumn()
    {
        if (_columnWidthsPinned) return;
        if (lvFiles.View is not GridView grid || grid.Columns.Count == 0) return;
        if (lvFiles.ActualWidth <= 0) return;

        var nameColumn = grid.Columns[0];

        if (_lastAutoNameWidth >= 0 && !double.IsNaN(nameColumn.Width) &&
            Math.Abs(nameColumn.Width - _lastAutoNameWidth) > 0.5)
        {
            _columnWidthsPinned = true;
            return;
        }

        double otherColumns = 0;
        for (int i = 1; i < grid.Columns.Count; i++) otherColumns += grid.Columns[i].ActualWidth;

        double width = lvFiles.ActualWidth - otherColumns - SystemParameters.VerticalScrollBarWidth - 8;
        if (width < NameColumnMin) width = NameColumnMin;

        if (Math.Abs(width - _lastAutoNameWidth) < 1) return;

        nameColumn.Width = width;
        _lastAutoNameWidth = width;
    }

    #endregion

    #region Selection

    private void LvFiles_MouseMoveRouter(object sender, MouseEventArgs e)
    {
        if (_isRightDragSelecting)
        {
            LvFiles_PreviewRightMouseMove(sender, e);
            return;
        }

        LvFiles_PreviewMouseMove(sender, e);
    }

    private void LvFiles_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var entry = EntryFromSource(e.OriginalSource);

        if (entry != null && entry.Name != "..")
        {
            _rightDragStartPoint = e.GetPosition(lvFiles);
            _lastRightDragHitPoint = _rightDragStartPoint;

            _rightDragTargetState = !entry.IsMarked;
            _rightDragMarkApplied = false;
            _isRightDragSelecting = true;
            _lastRightDragItem = entry;

            lvFiles.CaptureMouse();
            lvFiles.SelectedItem = entry;

            e.Handled = true;
        }
    }

    private void LvFiles_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRightDragSelecting) return;

        _isRightDragSelecting = false;
        lvFiles.ReleaseMouseCapture();

        Point currentPos = e.GetPosition(lvFiles);
        bool wasDragging = Math.Abs(currentPos.X - _rightDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                           Math.Abs(currentPos.Y - _rightDragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance;

        if (wasDragging)
        {
            e.Handled = true;
            return;
        }

        if (_lastRightDragItem != null && _lastRightDragItem.Name != "..")
        {
            var owner = Window.GetWindow(this);
            var paths = SelectedItems.Select(i => i.FullPath).ToList();
            if (paths.Count == 0 && _lastRightDragItem != null)
                paths.Add(_lastRightDragItem.FullPath);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                WindowsContextMenu.Show(paths, owner);
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);

            e.Handled = true;
        }
    }

    private void LvFiles_PreviewRightMouseMove(object sender, MouseEventArgs e)
    {
        if (e.RightButton == MouseButtonState.Released)
        {
            _isRightDragSelecting = false;
            lvFiles.ReleaseMouseCapture();
            return;
        }

        Point currentPosition = e.GetPosition(lvFiles);

        bool hasMoved = Math.Abs(currentPosition.X - _rightDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(currentPosition.Y - _rightDragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance;

        if (!hasMoved) return;

        if (!_rightDragMarkApplied && _lastRightDragItem != null)
        {
            _lastRightDragItem.IsMarked = _rightDragTargetState;
            _rightDragMarkApplied = true;
            UpdateMarkedStatus();
        }

        if (Math.Abs(currentPosition.Y - _lastRightDragHitPoint.Y) < 2) return;
        _lastRightDragHitPoint = currentPosition;

        var hit = lvFiles.InputHitTest(currentPosition) as DependencyObject;
        var entry = EntryFromSource(hit);

        if (entry == null || entry.Name == "..") return;

        if (entry != _lastRightDragItem)
        {
            entry.IsMarked = _rightDragTargetState;
            _lastRightDragItem = entry;
            UpdateMarkedStatus();
        }
    }

    private void RestoreSelection(string? itemToSelect)
    {
        FileEntry? match = null;
        if (!string.IsNullOrEmpty(itemToSelect))
            match = Items.FirstOrDefault(e => string.Equals(e.Name, itemToSelect, StringComparison.OrdinalIgnoreCase));

        if (match != null || Items.Count > 0)
        {
            var target = match ?? Items[0];
            SetSelectedItem(target, takeFocus: lvFiles.IsKeyboardFocusWithin);
        }
    }

    public void SetSelectedItem(FileEntry item) => SetSelectedItem(item, takeFocus: true);

    public void SetSelectedItem(FileEntry item, bool takeFocus)
    {
        lvFiles.SelectedItem = item;
        lvFiles.ScrollIntoView(item);

        if (!takeFocus) return;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (lvFiles.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
                container.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void FocusPanel()
    {
        if (lvFiles.SelectedItem != null && lvFiles.ItemContainerGenerator.ContainerFromItem(lvFiles.SelectedItem) is ListViewItem container)
            container.Focus();
        else
            lvFiles.Focus();
    }

    #endregion

    #region Sorting

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        string columnName = GetBaseColumnName(header.Column?.Header?.ToString() ?? "");
        ApplySort(columnName);
    }

    public void UpdateColumnHeaders()
    {
        if (lvFiles.View is not GridView gridView) return;
        foreach (var column in gridView.Columns)
        {
            string baseName = GetBaseColumnName(column.Header?.ToString() ?? "");
            column.Header = baseName == SortColumn ? baseName + (SortAscending ? " ↑" : " ↓") : baseName;
        }
    }

    private static string GetBaseColumnName(string header)
    {
        return header.Replace("↑", "").Replace("↓", "").Replace("\u2007", "").Trim();
    }

    private List<FileEntry> SortEntries(List<FileEntry> entries)
    {
        if (entries.Count < 2) return entries;

        FileEntry? parent = null;

        if (entries[0].Name == "..")
        {
            parent = entries[0];
            entries.RemoveAt(0);
        }

        entries.Sort(new FileEntryComparer(SortColumn, SortAscending));

        if (parent != null) entries.Insert(0, parent);

        return entries;
    }

    public void ApplySort(string columnName)
    {
        if (string.IsNullOrEmpty(columnName)) return;

        if (SortColumn == columnName) SortAscending = !SortAscending;
        else { SortColumn = columnName; SortAscending = true; }

        var selected = SelectedItem;

        var sorted = SortEntries(Items.ToList());
        Items.ReplaceAll(sorted);

        if (selected != null) lvFiles.SelectedItem = selected;

        UpdateColumnHeaders();
    }

    public class FileEntryComparer : IComparer<FileEntry>
    {
        private readonly string _sortColumn;
        private readonly bool _sortAscending;

        public FileEntryComparer(string sortColumn, bool sortAscending)
        {
            _sortColumn = sortColumn;
            _sortAscending = sortAscending;
        }

        public int Compare(FileEntry? x, FileEntry? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            if (x.IsFolder != y.IsFolder)
                return x.IsFolder ? -1 : 1;

            int result = _sortColumn switch
            {
                "Size" => x.Size.CompareTo(y.Size),
                "Modified" => CompareDates(x.Modified, y.Modified),
                "Extension" => MemoryExtensions.CompareTo(
                                    Path.GetExtension(x.Name.AsSpan()),
                                    Path.GetExtension(y.Name.AsSpan()),
                                    StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
            };

            if (result == 0 && _sortColumn != "Name")
                result = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

            return _sortAscending ? result : -result;
        }

        private static int CompareDates(DateTime? a, DateTime? b)
        {
            if (a.HasValue) return b.HasValue ? a.Value.CompareTo(b.Value) : 1;
            return b.HasValue ? -1 : 0;
        }
    }

    #endregion

    #region Visual tree helpers

    private static TextBlock? FindVisualTextBlock(DependencyObject root)
    {
        if (root is TextBlock tb)
            return tb;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindVisualTextBlock(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        DependencyObject? d = start;
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T fe && fe.Name == name) return fe;
            if (FindDescendant<T>(child, name) is T found) return found;
        }
        return null;
    }

    #endregion
}
