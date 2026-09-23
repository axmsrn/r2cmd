using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using R2Cmd.Controls;

namespace R2Cmd;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    public AppSettings AppSettings => _settings;

    // Long running operation started by this window (copy, refresh, size scan).
    // Gates the hotkeys, so it must never latch on.
    private bool _busy;

    // A pane is capturing the mouse (right button drag selection). Cursor only.
    private bool _panePointerBusy;

    public enum StatusPriority { Default = 0, Zoom = 1, Operation = 2 }
    private readonly string?[] _statusState = new string?[3];

    // =========================================================================
    // CRITICAL FIX: Global Status Lock
    // Mapped to the priority queue for backward compatibility with older operation code.
    // Setting this to false automatically clears the operation-level message.
    // =========================================================================
    public bool IsStatusLocked
    {
        get => _statusState[(int)StatusPriority.Operation] != null;
        set
        {
            if (!value) UpdateStatus(null, StatusPriority.Operation);
        }
    }

    private System.Windows.Threading.DispatcherTimer? _zoomSaveTimer;

    // Global memory to store the last deeply navigated path for each root drive letter
    private readonly Dictionary<string, string> _driveHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FilePaneControl, HashSet<string>> _paneOpenedSessions = new();
    private readonly Dictionary<FilePaneControl, HashSet<string>> _paneOpenedTerminals = new();
    private FilePaneControl _activePane;
    // Dynamically returns the pane that is currently NOT active
    private FilePaneControl _inactivePane => _activePane == leftPane ? rightPane : leftPane;

    // =========================================================================
    // WHITE FLASH FIX: Win32 constants for WM_ERASEBKGND interception
    //
    // Windows sends WM_ERASEBKGND before WPF renders its first frame.
    // By default, the OS fills the client area with white. We intercept
    // this message and paint the dark theme background color instead,
    // eliminating the white flash at startup.
    // =========================================================================
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    // Cached GDI brush for WM_ERASEBKGND painting.
    // Recreated only when theme changes to avoid GDI object leaks.
    private IntPtr _backgroundBrush = IntPtr.Zero;
    private uint _lastBrushColor = 0;

    // Windows sends several WM_DEVICECHANGE messages for one physical insertion.
    // Rebuilding both drive lists per message means several DriveInfo.GetDrives()
    // calls in a row, and a stale network mapping makes each one block the UI
    // thread until the SMB timeout expires. One rebuild per burst is enough.
    private System.Windows.Threading.DispatcherTimer? _driveRefreshTimer;

    // =========================================================================
    // P/Invoke declarations for WM_ERASEBKGND handling
    // =========================================================================
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FillRect(IntPtr hDC, [System.Runtime.InteropServices.In] ref RECT lprc, IntPtr hbr);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public MainWindow()
    {
        // Settings first: the render mode must be chosen before the window exists
        _settings = AppSettings.Load();

        if (!_settings.HardwareRendering)
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        InitializeComponent();

        // Placement is applied before the window is shown, otherwise it would
        // visibly jump from the XAML position to the restored one
        RestoreWindowPlacement();

        // APPLY THEME FROM SETTINGS IMMEDIATELY ON STARTUP
        ThemeManager.ApplyTheme(_settings.IsDarkTheme);

        UpdateEditorButton();

        IconService.Enabled = _settings.UseSystemIcons;

        _activePane = _settings.LastActivePane == "Right" ? rightPane : leftPane;

        SetupPane(leftPane, _settings.LastLeftSortColumn, _settings.LastLeftSortAscending);
        SetupPane(rightPane, _settings.LastRightSortColumn, _settings.LastRightSortAscending);

        // Restored before the first listing, so the panes never flash a default
        // layout on their way to the saved one
        leftPane.ApplyColumnWidths(_settings.LeftColumnWidths);
        rightPane.ApplyColumnWidths(_settings.RightColumnWidths);

        // How long an SSH session survives after the last pane leaves it
        Providers.SshFileSystemProvider.IdleTimeout =
            TimeSpan.FromMinutes(Math.Max(1, _settings.SshIdleMinutes));

        InitZoom();

        _ = InitializeAsync();
    }

    // =========================================================================
    // WINDOW PLACEMENT
    //
    // Two cases that go wrong if handled naively:
    //
    // 1. A monitor that is no longer there. Coordinates saved on a second screen
    //    put the window somewhere invisible, and the only way back is the
    //    keyboard. The stored rectangle is therefore checked against the virtual
    //    desktop, and anything that would leave the window unreachable is dropped
    //    in favour of the XAML defaults.
    //
    // 2. Closing while maximized. Left/Top/Width/Height then describe the
    //    maximized frame, which extends past the work area by the invisible
    //    border, so restoring it produces an oversized window. RestoreBounds
    //    holds the normal-state rectangle and is what gets stored.
    // =========================================================================
    private void RestoreWindowPlacement()
    {
        var s = _settings;

        if (s.WindowWidth > 0 && s.WindowHeight > 0)
        {
            double width = Math.Max(MinWidth > 0 ? MinWidth : 400, s.WindowWidth);
            double height = Math.Max(MinHeight > 0 ? MinHeight : 300, s.WindowHeight);

            if (IsPlacementReachable(s.WindowLeft, s.WindowTop, width, height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WindowLeft;
                Top = s.WindowTop;
                Width = width;
                Height = height;
            }
        }

        // Applied even when the rectangle was rejected: the user still asked for
        // a maximized window last time
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private static bool IsPlacementReachable(double left, double top, double width, double height)
    {
        double screenLeft = SystemParameters.VirtualScreenLeft;
        double screenTop = SystemParameters.VirtualScreenTop;
        double screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        double screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        // Require a usable strip of the title bar to remain grabbable: enough
        // width to aim at, and the caption itself inside the desktop
        const double MinVisibleWidth = 120;
        const double MinVisibleHeight = 40;

        double overlapLeft = Math.Max(left, screenLeft);
        double overlapTop = Math.Max(top, screenTop);
        double overlapRight = Math.Min(left + width, screenRight);
        double overlapBottom = Math.Min(top + height, screenBottom);

        return overlapRight - overlapLeft >= MinVisibleWidth
            && overlapBottom - overlapTop >= MinVisibleHeight
            && top >= screenTop - 1;   // a title bar above the desktop cannot be dragged
    }

    private void SaveWindowPlacement()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;

        // Minimized counts as "not normal" too: its Left/Top are meaningless
        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds.Width <= 0 || bounds.Height <= 0 || double.IsNaN(bounds.Width)) return;

        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
    }

    // =========================================================================
    // UI ZOOM — Ctrl+Plus / Ctrl+Minus / Ctrl+0 / Ctrl+MouseWheel
    //
    // ZoomManager rewrites the metrics in Application.Resources; every pane reads
    // them through DynamicResource, so the panes re-measure themselves.
    //
    // The saved level is applied before the panes are populated, so the first
    // listing is already drawn at the right size instead of being re-laid out.
    // =========================================================================
    private void InitZoom()
    {
        ZoomManager.SetZoom(_settings.UiZoom, silent: true);

        ZoomManager.ZoomChanged += OnZoomChanged;

        ZoomManager.Attach(this, text =>
        {
            // The priority queue naturally handles hiding the zoom overlay during
            // an active operation, and restoring the background default message when done.
            UpdateStatus(text, StatusPriority.Zoom);
        });
    }

    private void OnZoomChanged(double zoom)
    {
        _settings.UiZoom = zoom;

        // Holding Ctrl+Plus walks through the levels quickly, and each step would
        // otherwise rewrite settings.json. One write once the user stops.
        if (_zoomSaveTimer == null)
        {
            _zoomSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            _zoomSaveTimer.Tick += (s, e) =>
            {
                _zoomSaveTimer!.Stop();
                try { _settings.Save(); } catch { }
            };
        }

        _zoomSaveTimer.Stop();
        _zoomSaveTimer.Start();
    }

    // =========================================================================
    // SPLITTER: double click restores the 50 / 50 layout
    //
    // PreviewMouseLeftButtonDown with ClickCount == 2 rather than MouseDoubleClick:
    // GridSplitter derives from Thumb and captures the mouse on button down, so
    // catching the second click before the drag begins is the reliable point.
    // Marking it handled also stops a one pixel drag from sneaking in.
    // =========================================================================
    private void HookSplitterReset()
    {
        paneSplitter.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (e.ClickCount != 2) return;

            ResetPaneSplit(paneSplitter);
            e.Handled = true;
        };

        paneSplitter.DragDelta += (s, e) =>
        {
            double total = leftPane.ActualWidth + rightPane.ActualWidth;
            if (total > 0)
            {
                int percent = (int)(leftPane.ActualWidth / total * 100);
                SetStatus($"Panels: {percent}% / {100 - percent}%");
            }
        };
    }

    private void HookTerminalSplitter()
    {
        bottomTerminalSplitter.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (e.ClickCount != 2) return;

            // Double click resets the terminal to the default 250px height
            if (rowTerminal.Height.Value > 0)
            {
                rowTerminal.Height = new GridLength(250);
                SetStatus("Terminal height: 250px (Default)");
            }
            e.Handled = true;
        };

        bottomTerminalSplitter.DragDelta += (s, e) =>
        {
            if (rowTerminal.Height.Value > 0)
            {
                // Show current height in pixels during drag
                int height = (int)rowTerminal.Height.Value;
                SetStatus($"Terminal height: {height}px");
            }
        };
    }

    private void ResetPaneSplit(GridSplitter splitter)
    {
        if (VisualTreeHelper.GetParent(splitter) is not Grid grid) return;

        // Auto resolves by shape: a vertical divider is tall and narrow
        bool splitsColumns = splitter.ResizeDirection == GridResizeDirection.Columns
            || (splitter.ResizeDirection == GridResizeDirection.Auto
                && splitter.ActualWidth <= splitter.ActualHeight);

        if (splitsColumns && grid.ColumnDefinitions.Count > 1)
        {
            int ownColumn = Grid.GetColumn(splitter);

            for (int i = 0; i < grid.ColumnDefinitions.Count; i++)
            {
                if (i == ownColumn) continue;

                // Auto columns are toolbars and the divider itself, not panes
                var column = grid.ColumnDefinitions[i];
                if (column.Width.IsAuto) continue;

                column.Width = new GridLength(1, GridUnitType.Star);
            }
        }
        else if (grid.RowDefinitions.Count > 1)
        {
            int ownRow = Grid.GetRow(splitter);

            for (int i = 0; i < grid.RowDefinitions.Count; i++)
            {
                if (i == ownRow) continue;

                var row = grid.RowDefinitions[i];
                if (row.Height.IsAuto) continue;

                row.Height = new GridLength(1, GridUnitType.Star);
            }
        }

        SetStatus("Panels: 50% / 50%");
    }

    private void SetupPane(FilePaneControl pane, string sortCol, bool sortAsc)
    {
        pane.SortColumn = sortCol;
        pane.SortAscending = sortAsc;

        pane.PaneGotFocus += (s, e) =>
        {
            _activePane = pane;
            // Sync terminal when switching focus between Left and Right panes (e.g. via Tab)
            SyncGlobalTerminalDirectory();
        };

        // Only update the global status bar if the message comes from the active pane
        pane.StatusMessage += (s, msg) =>
        {
            if (pane == _activePane) SetStatus(msg);
        };

        pane.ItemExecuted += async (s, entry) => await HandleItemExecutionAsync(pane, entry);

        // =========================================================================
        // FIXED: this used to read its own output (`... || _busy`), so once the flag
        // went up it could never come back down and every hotkey stayed dead.
        // Pointer state and operation state are separate concerns now.
        // =========================================================================
        pane.BusyStateChanged += (s, e) =>
        {
            _panePointerBusy = leftPane.IsMouseCaptured || rightPane.IsMouseCaptured;
            UpdateBusyCursor();
        };

        pane.SizeCalculationRequested += (s, entry) => QueueFolderSize(entry);
        pane.DirectoryModified += async (s, e) => { if (!_busy) await SyncPanesIfSamePath(pane); };
        pane.FilesDropped += async (s, args) => await HandleFilesDroppedAsync(pane, args);

        // Track navigation history to restore the last opened folder when a drive letter is clicked
        pane.PathChanged += (s, e) =>
        {
            string current = pane.CurrentPath;

            ApplyKnownFolderSizes(pane);

            // TRACK SSH ROOTS: Save deep paths for SSH sessions so we can return to them.
            if (current.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                string? sessionName = SshSessionOf(current);
                if (sessionName != null)
                {
                    _driveHistory[$"ssh://{sessionName}"] = current;

                    if (pane.SuppressNextSftpBookmark)
                    {
                        pane.SuppressNextSftpBookmark = false;
                    }
                    else
                    {
                        if (!_paneOpenedSessions.TryGetValue(pane, out var opened))
                        {
                            opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _paneOpenedSessions[pane] = opened;
                        }
                        opened.Add(sessionName);
                    }
                }
            }
            else if (!current.StartsWith(@"\\"))
            {
                string root = System.IO.Path.GetPathRoot(current) ?? "";
                if (root != "") _driveHistory[root] = current;
            }

            UpdatePinnedSshSessions();
            RefreshConnectionTabs();

            // Sync terminal when navigating into a new folder (only if this pane is active)
            if (pane == _activePane)
            {
                SyncGlobalTerminalDirectory();
            }
        };

        pane.ConnectionTabsChanged += (s, e) =>
        {
            RememberPaneTerminal(pane);
            RefreshConnectionTabs();
        };

        pane.ConnectionBookmarkClosed += (s, item) =>
        {
            HandleBookmarkClosed(pane, item);
        };

        // Resolves target path when a drive root (e.g. "D:\") is clicked in the UI
        pane.SyncPathResolver = (driveRoot) =>
        {
            var otherPane = pane == leftPane ? rightPane : leftPane;

            // Sync with adjacent pane if it's already exploring the requested drive
            if (otherPane.CurrentPath.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase))
            {
                return otherPane.CurrentPath;
            }

            // Restore the last visited deep path on this drive if available
            if (_driveHistory.TryGetValue(driveRoot, out string? savedPath) && System.IO.Directory.Exists(savedPath))
            {
                return savedPath;
            }

            return driveRoot;
        };
    }

    // =========================================================================
    // Sessions shown in a pane are pinned: the idle sweeper in the provider
    // never touches them, no matter how long they sit untouched.
    //
    // Everything else is left to the sweeper, which closes a session after five
    // minutes without use. Leaving for a local drive and coming back a moment
    // later therefore costs nothing, while a session forgotten for the evening
    // does not keep an sshd slot busy.
    // =========================================================================
    private void UpdatePinnedSshSessions()
    {
        var inUse = new List<string>(2);

        foreach (string path in new[] { leftPane.CurrentPath, rightPane.CurrentPath })
        {
            string? name = SshSessionOf(path);
            if (name != null) inUse.Add(name);
        }

        Providers.SshFileSystemProvider.SetPinnedSessions(inUse);
    }

    private static string? SshSessionOf(string path)
    {
        if (!path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return null;

        string rest = path.Substring(6);
        int slash = rest.IndexOf('/');
        string name = slash < 0 ? rest : rest.Substring(0, slash);

        return string.IsNullOrEmpty(name) ? null : name;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // FIXED: Title bar now requests color from current theme
        Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme, useSurfaceColor: true);

        // Hook into the Windows message loop to listen for OS-level events (like USB insertion)
        if (System.Windows.PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
        {
            source.AddHook(WndProc);
        }

        // Update switcher icon to the correct one on startup
        btnTheme.ApplyTemplate();
        UpdateThemeIcon(btnTheme);

        HookSplitterReset();
        HookTerminalSplitter();
    }

    // The same three lines used to sit in both OnSourceInitialized and the click handler
    private static void UpdateThemeIcon(Button button)
    {
        if (button.Template?.FindName("Icon", button) is TextBlock iconBlock)
        {
            iconBlock.Text = ThemeManager.IsDarkTheme ? "🌙" : "☀️";
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // =========================================================================
        // WHITE FLASH FIX: Intercept WM_ERASEBKGND and paint dark background
        //
        // Windows sends WM_ERASEBKGND before WPF renders its first frame.
        // By default, the OS fills the client area with white. We intercept
        // this message and paint the dark theme background color instead,
        // eliminating the white flash at startup.
        //
        // wParam contains the HDC (device context) for painting.
        // We cache the GDI brush and only recreate it when the theme changes.
        // =========================================================================
        if (msg == WM_ERASEBKGND)
        {
            // CreateSolidBrush expects COLORREF in BGR format
            // Dark theme background #FF252526 -> BGR: 0x00262525
            // Light theme background #FFF3F3F3 -> BGR: 0x00F3F3F3
            uint colorRef = ThemeManager.IsDarkTheme ? 0x00262525u : 0x00F3F3F3u;

            // Cache the brush: recreate only when theme changes
            if (_backgroundBrush == IntPtr.Zero || _lastBrushColor != colorRef)
            {
                if (_backgroundBrush != IntPtr.Zero) DeleteObject(_backgroundBrush);
                _backgroundBrush = CreateSolidBrush(colorRef);
                _lastBrushColor = colorRef;
            }

            RECT rect;
            GetClientRect(hwnd, out rect);
            FillRect(wParam, ref rect, _backgroundBrush);

            handled = true;
            return new IntPtr(1); // Return non-zero to indicate we handled it
        }

        // Only react to media physically appearing or leaving. WM_DEVICECHANGE also
        // fires for node changes, which arrive in bursts and would rebuild both
        // drive lists for nothing.
        if (msg == WM_DEVICECHANGE)
        {
            int eventType = wParam.ToInt32();
            if (eventType == DBT_DEVICEARRIVAL || eventType == DBT_DEVICEREMOVECOMPLETE)
            {
                ScheduleDriveRefresh();
            }
        }
        return IntPtr.Zero;
    }

    private void ScheduleDriveRefresh()
    {
        if (_driveRefreshTimer == null)
        {
            _driveRefreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            _driveRefreshTimer.Tick += async (s, e) =>
            {
                _driveRefreshTimer!.Stop();
                await RefreshDrivesAsync();
            };
        }

        _driveRefreshTimer.Stop();
        _driveRefreshTimer.Start();
    }

    private async Task InitializeAsync()
    {
        // =========================================================================
        // STARTUP OPTIMIZATION: Yield control back to the UI thread
        //
        // This allows the UI thread to render the first frame (the dark empty
        // window) before we start loading files. Without this, InitDrives() and
        // NavigateAsync() would block the UI thread, delaying the first visible
        // frame by 100-200ms.
        //
        // With Task.Yield(), the window appears immediately (empty), then files
        // load asynchronously. The perceived startup time is faster.
        // =========================================================================
        await Task.Yield();

        try
        {
            Task drives = RefreshDrivesAsync();

            // Off the UI thread: Directory.Exists on a disconnected network
            // share blocks until the SMB timeout
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var (startLeft, startRight) = await Task.Run(() => (
                IsValidStartPath(_settings.LastLeftPath) ? _settings.LastLeftPath : home,
                IsValidStartPath(_settings.LastRightPath) ? _settings.LastRightPath : home));

            await Task.WhenAll(leftPane.NavigateAsync(startLeft), rightPane.NavigateAsync(startRight));

            leftPane.UpdateColumnHeaders();
            rightPane.UpdateColumnHeaders();
            RefreshConnectionTabs();

            await drives;
        }
        catch (Exception ex)
        {
            SetStatus($"Initialization error: {ex.Message}");
        }
    }

    // DriveInfo.IsReady blocks on a dead network drive, so the scan runs in
    // the background, once, and both panes get the same list
    private async Task RefreshDrivesAsync()
    {
        var drives = await Task.Run(FilePaneControl.ScanDrives);
        leftPane.ApplyDrives(drives);
        rightPane.ApplyDrives(drives);
    }

    static bool IsValidStartPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return true;
        return Directory.Exists(path);
    }

    private async Task HandleItemExecutionAsync(FilePaneControl pane, FileEntry entry)
    {
        if (entry.Name == "..")
        {
            // Special case: ".." coming from search results has FullPath set
            // to the original folder. Navigate back to it instead of going up.
            if (!string.IsNullOrEmpty(entry.FullPath))
            {
                await pane.NavigateAsync(entry.FullPath);
            }
            else
            {
                await NavigateUpAsync(pane);
            }
            return;
        }

        // Intercept execution for the virtual "Add SSH" entry located in the Network root
        if (entry.FullPath == ":::ADD_SSH:::")
        {
            var dlg = new SshConnectionWindow { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                _settings.SshSessions.Add(dlg.Result);
                _settings.Save();
                await pane.RefreshAsync();
                RefreshConnectionTabs();
                SetStatus($"SSH Session '{dlg.Result.Name}' saved.");
            }
            return;
        }

        if (entry.IsFolder || Providers.FileSystemFactory.GetProvider(entry.FullPath) is Providers.ArchiveProvider)
        {
            string pathToNavigate = entry.FullPath;

            // Attempt to resolve the real path if the folder is a Symlink
            if (entry.IsSymlink)
            {
                try
                {
                    var dirInfo = new System.IO.DirectoryInfo(entry.FullPath);
                    var target = dirInfo.ResolveLinkTarget(true);
                    if (target != null)
                    {
                        pathToNavigate = target.FullName;
                    }
                }
                catch
                {
                    // Fallback to original path if target resolution fails due to permissions/network
                }
            }

            // SMART SSH RESTORE: If opening an SSH session from the Network root,
            // check if we have a deeper saved path in history and restore it.
            if (pathToNavigate.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) &&
                pane.CurrentPath.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase))
            {
                string? sessionName = SshSessionOf(pathToNavigate);
                if (sessionName != null && _driveHistory.TryGetValue($"ssh://{sessionName}", out string? savedPath))
                {
                    pathToNavigate = savedPath;
                }
            }

            await pane.NavigateAsync(pathToNavigate);
        }
        else
        {
            // Not Process.Start on entry.FullPath: for an ssh:// entry the shell
            // resolves it as a URL scheme and opens a terminal
            await OpenFileExternallyAsync(entry);
        }
    }

    private async Task NavigateUpAsync(FilePaneControl pane)
    {
        string path = pane.CurrentPath;

        if (path.Equals(@"\\Network\", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(@"\\Network", StringComparison.OrdinalIgnoreCase))
            return;

        // SSH paths
        if (path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            string clean = path.TrimEnd('/');
            int firstSlash = clean.IndexOf('/', 6); // after "ssh://"
            string sessionName = firstSlash > 0 ? clean.Substring(6, firstSlash - 6) : clean.Substring(6);

            // If the session is not connected yet — escape immediately to Network
            if (!Providers.SshFileSystemProvider.IsSessionOpen(sessionName))
            {
                await pane.NavigateAsync(@"\\Network\", sessionName);
                return;
            }

            int lastSlash = clean.LastIndexOf('/');

            // From SSH root → go to Network and select the session
            if (lastSlash <= 5)
            {
                await pane.NavigateAsync(@"\\Network\", sessionName);
                return;
            }

            // Inside an already connected SSH folder → normal up
            string sshParentPath = clean.Substring(0, lastSlash) + "/";
            string folder = clean.Substring(lastSlash + 1);
            await pane.NavigateAsync(sshParentPath, folder);
            return;
        }

        // From Windows Network (\\Network\LAN) → Network root, select "Windows Network"
        if (path.Equals(@"\\Network\LAN", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(@"\\Network\LAN\", StringComparison.OrdinalIgnoreCase))
        {
            await pane.NavigateAsync(@"\\Network\", "Windows Network");
            return;
        }

        // Normal case (including \\MI → \\Network\LAN)
        string trimmedPath = path.TrimEnd('\\', '/');
        string folderToSelect;

        if (trimmedPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNC: \\MI or \\MI\share → take the last segment ("MI" / "share")
            int lastSlash = trimmedPath.LastIndexOf('\\');
            folderToSelect = lastSlash >= 0 ? trimmedPath.Substring(lastSlash + 1) : trimmedPath;
        }
        else
        {
            folderToSelect = Path.GetFileName(trimmedPath) ?? "";
        }

        var provider = Providers.FileSystemFactory.GetProvider(path);
        string newPath = provider.GetParentPath(path);

        if (string.IsNullOrEmpty(newPath)) return;

        await pane.NavigateAsync(newPath, folderToSelect);
    }

    // reloadIcons is only true for the user pressing Ctrl+R. The same method is
    // the completion callback of a move, and clearing the cache there threw away
    // the icons of the very files that had just been moved — they were already
    // loaded in the source pane and would otherwise appear instantly.
    // =========================================================================
    // Ctrl+PgDn does two jobs, in this order.
    //
    // 1. An .exe under the cursor is checked for an archive inside it. This is
    //    what the key gained a second meaning for: driver and setup packages are
    //    archives behind a PE stub, and looking inside beats running them.
    //
    // 2. Otherwise it means what it always meant — back to the folder Ctrl+PgUp
    //    came from. Putting "enter the item under the cursor" ahead of this was
    //    wrong: after going up, the cursor sits on the folder just left, so the
    //    key walked back into it one level at a time instead of returning to the
    //    full path.
    //
    // 3. With nothing to return to, it falls back to opening the folder or
    //    archive under the cursor, so the key is never simply dead.
    // =========================================================================
    private async Task EnterItemOrGoForwardAsync(FilePaneControl pane)
    {
        var item = pane.SelectedItem;
        bool haveItem = item != null && item.Name != "..";

        if (haveItem && !item!.IsFolder && await TryEnterSelfExtractingAsync(pane, item))
            return;

        if (pane.CanNavigateForward)
        {
            await pane.NavigateForwardAsync();
            return;
        }

        if (haveItem && (item!.IsFolder || item.IsArchive))
            await HandleItemExecutionAsync(pane, item);
    }

    private async Task<bool> TryEnterSelfExtractingAsync(FilePaneControl pane, FileEntry item)
    {
        if (!item.FullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;

        // Finding the payload means reading the head of the file, which on a
        // several hundred megabyte installer is not instant
        SetBackgroundStatus($"Looking inside {item.Name}...");

        bool opened;
        try
        {
            opened = await Task.Run(() => ArchiveService.TryOpenSelfExtracting(item.FullPath));
        }
        finally
        {
            UpdateBackgroundStatus();
        }

        if (!opened)
        {
            SetStatus($"{item.Name} is not a self-extracting archive.");
            return false;
        }

        await pane.NavigateAsync(item.FullPath);
        return true;
    }

    private async Task DoRefreshAsync(bool reloadIcons = false)
    {
        SetBusy(true);
        try
        {
            // Ctrl+R is the user saying the listing is wrong. Icons are part of
            // that: an executable whose icon the shell had not produced yet gets
            // another chance here instead of waiting for a restart.
            if (reloadIcons) IconService.Clear();

            // Force a fresh network scan (clears the 60-second cache)
            Providers.HybridNetworkScanner.InvalidateCache();

            await Task.WhenAll(leftPane.RefreshAsync(), rightPane.RefreshAsync());
        }
        finally { SetBusy(false); }
    }

    private async Task SyncPanesIfSamePath(FilePaneControl changedPane)
    {
        var otherPane = changedPane == leftPane ? rightPane : leftPane;
        if (string.Equals(changedPane.CurrentPath, otherPane.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            await otherPane.RefreshAsync();
            // Restoring focus to the originating pane via Background priority ensures it executes
            // only after the other pane's internal Input priority bindings finish settling.
            _ = changedPane.Dispatcher.BeginInvoke(
                new Action(() => changedPane.FocusPanel()),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private async Task SwapPanelsAsync()
    {
        SetBusy(true);
        try
        {
            string tempPath = leftPane.CurrentPath;
            await Task.WhenAll(leftPane.NavigateAsync(rightPane.CurrentPath), rightPane.NavigateAsync(tempPath));
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateBusyCursor();
    }

    private void UpdateBusyCursor() =>
        Mouse.OverrideCursor = (_busy || _panePointerBusy) ? Cursors.Wait : null;

    // =========================================================================
    // Priority-based status machine. Replaces the brittle flag architecture.
    // New code should call UpdateStatus directly. SetStatus is kept for
    // backward compatibility with existing pane callbacks.
    // =========================================================================
    public void SetStatus(string text, bool forceUpdate = false)
    {
        UpdateStatus(text, forceUpdate ? StatusPriority.Operation : StatusPriority.Default);
    }

    public void UpdateStatus(string? text, StatusPriority priority)
    {
        _statusState[(int)priority] = text;

        // Render highest priority message that currently exists
        for (int i = _statusState.Length - 1; i >= 0; i--)
        {
            if (_statusState[i] != null)
            {
                statusText.Text = _statusState[i];
                return;
            }
        }

        statusText.Text = "Ready.";
    }

    // Same reasoning as the shortcuts window: shown non-modally so that a click
    // on the main window closes it, and kept in a field so the button cannot
    // stack copies of it
    private AboutWindow? _aboutWindow;

    // Independent of SetStatus: never suppressed by the status lock or the zoom
    // indicator, and never overwritten by a pane message
    public void SetBackgroundStatus(string? text)
    {
        backgroundText.Text = text ?? "";
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        if (_aboutWindow != null)
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow { Owner = this };
        _aboutWindow.Closed += (s, args) => _aboutWindow = null;
        _aboutWindow.Show();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.ToggleTheme();

        // Update OS-level title bar color for main window and any open child dialogs
        foreach (Window window in Application.Current.Windows)
        {
            Helpers.SetTitleBarTheme(window, ThemeManager.IsDarkTheme, useSurfaceColor: true);
        }

        if (sender is Button btn) UpdateThemeIcon(btn);
    }

    // Kept so a second click on the button focuses the open window instead of
    // stacking copies of it
    private KeyboardWindow? _keyboardWindow;

    private void OnKeyboardClick(object sender, RoutedEventArgs e)
    {
        // Show() and not ShowDialog(): a modal dialog disables its owner, so a
        // click on the main window is swallowed by the OS and the shortcuts
        // window never sees a Deactivated event. It would then only close when
        // the whole application lost focus, which is exactly what it used to do.
        if (_keyboardWindow != null)
        {
            _keyboardWindow.Activate();
            return;
        }

        _keyboardWindow = new KeyboardWindow { Owner = this };
        _keyboardWindow.Closed += (s, args) => _keyboardWindow = null;
        _keyboardWindow.Show();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _driveRefreshTimer?.Stop();

        // A pending debounced write would never fire after this point
        _zoomSaveTimer?.Stop();

        // Clean up the cached GDI brush to avoid resource leaks
        if (_backgroundBrush != IntPtr.Zero)
        {
            DeleteObject(_backgroundBrush);
            _backgroundBrush = IntPtr.Zero;
        }

        // Watchers over the temporary copies of remote files
        StopEditorWatchers();

        Providers.SshFileSystemProvider.CloseAll();

        // Temporary copies of remote and in-archive files opened this session.
        // Anything still held by an editor simply refuses to delete.
        try
        {
            string temp = Path.Combine(Path.GetTempPath(), "R2Cmd");
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
        catch { }

        _settings.IsDarkTheme = ThemeManager.IsDarkTheme;
        _settings.UiZoom = ZoomManager.Zoom;

        SaveWindowPlacement();

        _settings.LastLeftPath = leftPane.GetPersistentPath();
        _settings.LastRightPath = rightPane.GetPersistentPath();

        // FIXED: this used to store the opposite pane, so the focus landed on the
        // wrong side after every restart
        _settings.LastActivePane = _activePane == leftPane ? "Left" : "Right";

        _settings.LeftColumnWidths = leftPane.GetColumnWidths();
        _settings.RightColumnWidths = rightPane.GetColumnWidths();

        _settings.LastLeftSortColumn = leftPane.SortColumn;
        _settings.LastLeftSortAscending = leftPane.SortAscending;
        _settings.LastRightSortColumn = rightPane.SortColumn;
        _settings.LastRightSortAscending = rightPane.SortAscending;

        // Save the terminal height only if it is currently visible.
        // If it's hidden (Height == 0), we keep the previously stored value intact.
        if (bottomTerminalHost.Visibility == Visibility.Visible && rowTerminal.Height.Value > 0)
        {
            _settings.TerminalHeight = rowTerminal.Height.Value;
        }

        try { _settings.Save(); } catch { }
    }
    // ===================== Search (Alt+F7) =====================
    private async Task OpenSearchAsync()
    {
        // If there are marked items, limit the search to those only
        var marked = _activePane.Items
            .Where(e => e.IsMarked && e.Name != "..")
            .ToList();

        var dlg = marked.Count > 0
            ? new SearchWindow(_activePane.CurrentPath, marked) { Owner = this }
            : new SearchWindow(_activePane.CurrentPath) { Owner = this };

        if (dlg.ShowDialog() != true) return;

        // The whole result set goes into the pane, so the matches can be worked
        // with as a group instead of one jump per file
        if (dlg.ResultsToShow != null)
        {
            _activePane.ShowSearchResults(
                string.IsNullOrEmpty(dlg.SearchRoot) ? _activePane.CurrentPath : dlg.SearchRoot,
                dlg.ResultsToShow);

            _activePane.FocusPanel();
            return;
        }

        if (dlg.GoToDirectory != null)
        {
            await _activePane.NavigateAsync(dlg.GoToDirectory, dlg.GoToFile);
        }
    }

    // ===================== Copy to clipboard =====================
    private void CopySelectedNamesToClipboard(bool fullPath)
    {
        // SelectedItems already returns the cursor row when nothing is marked
        var items = _activePane.SelectedItems;
        if (items.Count == 0) return;

        string text = string.Join(Environment.NewLine, items.Select(i => fullPath ? i.FullPath : i.Name));

        try
        {
            Clipboard.SetText(text);
            SetStatus($"Copied {items.Count} {(fullPath ? "path(s)" : "name(s)")} to clipboard.");
        }
        catch (Exception ex)
        {
            SetStatus($"Clipboard error: {ex.Message}");
        }
    }

    // =========================================================================
    // GLOBAL WINDOWS TERMINAL LOGIC
    // =========================================================================
    private void ToggleGlobalTerminal()
    {
        // If the active pane is currently showing an SSH path, toggle the embedded SSH terminal instead
        if (_activePane != null && _activePane.CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            _activePane.ToggleSshTerminal();
            RefreshConnectionTabs();
            return;
        }

        if (bottomTerminalHost.Visibility == Visibility.Visible)
        {
            // Save the dynamically adjusted height to settings before collapsing the panel,
            // so it is remembered if the user reopens the terminal within the same session.
            if (rowTerminal.Height.Value > 0)
            {
                _settings.TerminalHeight = rowTerminal.Height.Value;
            }

            // Hide the terminal
            bottomTerminalHost.Visibility = Visibility.Collapsed;
            bottomTerminalSplitter.Visibility = Visibility.Collapsed;
            rowTerminal.Height = new GridLength(0);

            // Return focus to the active file pane
            _activePane?.FocusPanel();
        }
        else
        {
            // Show the terminal
            bottomTerminalHost.Visibility = Visibility.Visible;
            bottomTerminalSplitter.Visibility = Visibility.Visible;

            // Apply saved height, clamped to a maximum of 70% of the window height
            // to prevent the terminal from hiding the file panels entirely on smaller screens.
            if (rowTerminal.Height.Value <= 0)
            {
                double targetHeight = _settings.TerminalHeight > 0 ? _settings.TerminalHeight : 250;
                double maxAllowed = this.ActualHeight * 0.7;

                rowTerminal.Height = new GridLength(Math.Min(targetHeight, maxAllowed));
            }

            if (!globalTerminal.IsRunning)
            {
                // Start local terminal using the active pane's path
                string? startPath = GetActiveLocalPath();

                // Initialize the cache so it doesn't instantly send 'cd' again
                _lastSyncedTerminalPath = startPath;

                // Pass the path and null for the default shell (PowerShell)
                globalTerminal.Start(startPath, null);

                // Auto-hide when 'exit' is typed
                globalTerminal.SessionExited -= GlobalTerminal_SessionExited;
                globalTerminal.SessionExited += GlobalTerminal_SessionExited;
            }
            else
            {
                // If terminal is already running but was hidden, force a sync
                // in case the user changed directories while it was closed
                _lastSyncedTerminalPath = null;
                SyncGlobalTerminalDirectory();
            }

            // Focus the terminal
            _ = Dispatcher.BeginInvoke(new Action(() => globalTerminal.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void GlobalTerminal_SessionExited(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (bottomTerminalHost.Visibility == Visibility.Visible)
            {
                ToggleGlobalTerminal();
            }
        }));
    }

    private string? GetActiveLocalPath()
    {
        if (_activePane == null) return null;
        string path = _activePane.CurrentPath;

        // Ignore network roots and SSH paths (Windows Terminal cannot operate there)
        if (string.IsNullOrEmpty(path) ||
            path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return System.IO.Directory.Exists(path) ? path : null;
    }

    // Track the last synced path to avoid spamming 'cd' when simply clicking files
    private string? _lastSyncedTerminalPath;

    private void SyncGlobalTerminalDirectory()
    {
        // Do not spam commands if the terminal is hidden or not running
        if (bottomTerminalHost.Visibility != Visibility.Visible || !globalTerminal.IsRunning)
            return;

        string? localPath = GetActiveLocalPath();

        // Only send the 'cd' command if the path has actually changed
        if (localPath != null && !string.Equals(localPath, _lastSyncedTerminalPath, StringComparison.OrdinalIgnoreCase))
        {
            _lastSyncedTerminalPath = localPath;

            // ChangeDirectory sends Escape and cd as two writes, after flushing
            // any pending ConPTY resize. A single "\x1bcd ..." string is swallowed
            // by PSReadLine as one CSI-like token.
            globalTerminal.ChangeDirectory(localPath);
        }
    }

    private void RefreshConnectionTabs()
    {
        leftPane.SetConnectionBookmarks(BuildPaneBookmarks(leftPane));
        rightPane.SetConnectionBookmarks(BuildPaneBookmarks(rightPane));
    }

    private void RememberPaneTerminal(FilePaneControl pane)
    {
        string? name = SshSessionOf(pane.CurrentPath);
        if (name == null) return;

        if (!_paneOpenedTerminals.TryGetValue(pane, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _paneOpenedTerminals[pane] = set;
        }

        if (pane.IsTerminalVisible)
            set.Add(name);
        else if (!pane.IsTerminalRunning)
            set.Remove(name);
    }

    private void HandleBookmarkClosed(FilePaneControl pane, ConnectionBookmark item)
    {
        if (item.IsTerminal)
        {
            if (_paneOpenedTerminals.TryGetValue(pane, out var terminals))
                terminals.Remove(item.SessionName);
        }
        else if (_paneOpenedSessions.TryGetValue(pane, out var sessions))
        {
            sessions.Remove(item.SessionName);
            pane.ForgetLastSshPath(item.SessionName);
        }

        bool paneHasSftp = _paneOpenedSessions.TryGetValue(pane, out var paneSftp)
            && paneSftp.Count > 0;
        bool paneHasSsh = _paneOpenedTerminals.TryGetValue(pane, out var paneSsh)
            && paneSsh.Count > 0;

        if (!paneHasSftp && !paneHasSsh &&
            pane.CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            _ = pane.NavigateAsync(@"\\Network\");
        }

        bool stillUsed = false;
        foreach (var other in new[] { leftPane, rightPane })
        {
            if (_paneOpenedSessions.TryGetValue(other, out var s) && s.Contains(item.SessionName))
                stillUsed = true;
            if (_paneOpenedTerminals.TryGetValue(other, out var t) && t.Contains(item.SessionName))
                stillUsed = true;
            if (other != pane &&
                string.Equals(SshSessionOf(other.CurrentPath), item.SessionName, StringComparison.OrdinalIgnoreCase))
                stillUsed = true;
        }

        if (!stillUsed)
        {
            try { Providers.SshFileSystemProvider.CloseConnection(item.SessionName); } catch { }
            _driveHistory.Remove($"ssh://{item.SessionName}");
        }

        RefreshConnectionTabs();
    }

    private List<ConnectionBookmark> BuildPaneBookmarks(FilePaneControl pane)
    {
        var list = new List<ConnectionBookmark>();

        if (!_paneOpenedSessions.TryGetValue(pane, out var opened))
            opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? paneSession = SshSessionOf(pane.CurrentPath);

        opened.RemoveWhere(name =>
            !Providers.SshFileSystemProvider.IsSessionOpen(name) &&
            !string.Equals(paneSession, name, StringComparison.OrdinalIgnoreCase));

        foreach (string name in opened.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            bool onThisPane = string.Equals(paneSession, name, StringComparison.OrdinalIgnoreCase);
            if (!Providers.SshFileSystemProvider.IsSessionOpen(name) && !onThisPane)
                continue;

            string target = $"ssh://{name}/";
            if (_driveHistory.TryGetValue($"ssh://{name}", out string? saved) &&
                !string.IsNullOrWhiteSpace(saved))
            {
                target = saved;
            }

            list.Add(new ConnectionBookmark
            {
                Title = name,
                SessionName = name,
                TargetPath = target,
                IsActive = onThisPane && !pane.IsTerminalVisible && !pane.PendingTerminalActivation,
                IsTerminal = false
            });
        }

        if (_paneOpenedTerminals.TryGetValue(pane, out var terminals) && terminals.Count > 0)
        {
            foreach (string sshName in terminals)
            {
                string target = $"ssh://{sshName}/";
                if (_driveHistory.TryGetValue($"ssh://{sshName}", out string? saved) &&
                    !string.IsNullOrWhiteSpace(saved))
                {
                    target = saved;
                }

                list.Add(new ConnectionBookmark
                {
                    Title = sshName,
                    SessionName = sshName,
                    TargetPath = target,
                    IsActive = (pane.IsTerminalVisible || pane.PendingTerminalActivation) &&
                        string.Equals(paneSession, sshName, StringComparison.OrdinalIgnoreCase),
                    IsTerminal = true
                });
            }
        }

        return list;
    }
}
