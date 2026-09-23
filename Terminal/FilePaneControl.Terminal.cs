using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace R2Cmd.Controls;

public sealed class ConnectionBookmark
{
    public string Title { get; init; } = "";
    public string SessionName { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public bool IsActive { get; init; }
    public bool IsTerminal { get; init; }
}

public partial class FilePaneControl
{
    private TerminalControl _terminal = new();
    private readonly Dictionary<string, TerminalControl> _sshTerminals = new(StringComparer.OrdinalIgnoreCase);
    private bool _terminalReady;
    private bool _isTerminalVisible;
    private string? _sshTerminalPath;
    private string? _lastSshPath;
    private string _lastBookmarkSignature = "";

    public bool IsTerminalRunning => _terminal != null && _terminal.IsRunning;
    public bool IsTerminalVisible => _isTerminalVisible;
    public bool SuppressNextSftpBookmark { get; set; }
    public bool PendingTerminalActivation { get; set; }

    public event EventHandler? ConnectionTabsChanged;
    public event EventHandler<ConnectionBookmark>? ConnectionBookmarkClosed;
    public event EventHandler? TerminalVisibilityChanged;

    #region Lifecycle

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);

        PreviewKeyDown += TerminalKeys;
        Loaded += (s, args) => InitTerminal();
        Unloaded += (s, args) => ShutdownTerminal();
    }

    private bool InitTerminal()
    {
        if (_terminalReady) return true;

        terminalHost.Child = _terminal;
        _terminal.SetResourceReference(FontSizeProperty, "AppFilePaneFontSize");
        _terminal.SessionExited += Terminal_SessionExited;
        _terminalReady = true;

        PathChanged += (s, args) =>
        {
            bool isSshPath = CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);
            if (!isSshPath && _isTerminalVisible)
                HideTerminal(returnFocus: false);
        };

        lvFiles.GotKeyboardFocus += (s, args) =>
        {
            if (!_isTerminalVisible) return;

            _ = Dispatcher.BeginInvoke(new Action(() => _terminal.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        };

        return true;
    }

    private void ShutdownTerminal()
    {
        foreach (var t in _sshTerminals.Values)
            t.Stop();
        _sshTerminals.Clear();
        _terminal.Stop();
    }

    #endregion

    #region Keys

    private void TerminalKeys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            HandleTabKey(e);
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        if (e.Key == Key.Q && CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            if (!_terminal.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                _ = DisconnectSshPaneAsync();
            }
        }
    }

    private void HandleTabKey(KeyEventArgs e)
    {
        if (!_isTerminalVisible) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool inTerminal = _terminal.IsKeyboardFocusWithin;
        if (ctrl || inTerminal) return;

        _terminal.Focus();
        e.Handled = true;
    }

    private async Task DisconnectSshPaneAsync()
    {
        string? sessionName = ExtractSshSessionName(CurrentPath);
        if (sessionName == null) return;

        try
        {
            Providers.SshFileSystemProvider.CloseConnection(sessionName);
            StatusMessage?.Invoke(this, $"Disconnected from SSH session '{sessionName}'.");
            await NavigateAsync(@"\\Network\");
            ConnectionTabsChanged?.Invoke(this, EventArgs.Empty);
            FocusPanel();
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"SSH disconnect error: {ex.Message}");
        }
    }

    #endregion

    #region Show and hide

    public void ToggleSshTerminal()
    {
        if (_isTerminalVisible)
            HideTerminal(returnFocus: true);
        else
            ShowTerminal(takeFocus: true);

        ConnectionTabsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowTerminal(bool takeFocus)
    {
        ClearQuickSearch();

        _isTerminalVisible = true;

        colLeft.Width = new GridLength(1, GridUnitType.Star);
        colMiddle.Width = new GridLength(0);
        colRight.Width = new GridLength(0);

        lvFiles.Visibility = Visibility.Collapsed;
        terminalSplitter.Visibility = Visibility.Collapsed;
        terminalHost.Visibility = Visibility.Visible;

        string? sshSessionName = ExtractSshSessionName(CurrentPath);

        if (!string.IsNullOrEmpty(sshSessionName))
        {
            UpdateBreadcrumbs($"ssh://{sshSessionName}/terminal");

            for (int i = 1; i < spBreadcrumbs.Children.Count; i++)
            {
                if (spBreadcrumbs.Children[i] is UIElement el)
                {
                    el.IsHitTestVisible = false;
                    el.Opacity = 0.5;
                }
            }
        }

        EnsureSshTerminalForCurrentPath();

        if (sshSessionName != null && !_terminal.IsRunning && FindSshSession(sshSessionName) == null)
        {
            StatusMessage?.Invoke(this, $"Terminal: SSH session '{sshSessionName}' not found in settings.");
            return;
        }

        if (takeFocus)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => _terminal.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void HideTerminal(bool returnFocus)
    {
        colLeft.Width = new GridLength(0);
        colMiddle.Width = new GridLength(0);
        colRight.Width = new GridLength(1, GridUnitType.Star);

        terminalHost.Visibility = Visibility.Collapsed;
        terminalSplitter.Visibility = Visibility.Collapsed;

        lvFiles.Visibility = Visibility.Visible;
        _isTerminalVisible = false;

        pnlBreadcrumbs.IsHitTestVisible = true;
        txtPath.IsHitTestVisible = true;
        UpdateBreadcrumbs(CurrentPath);
        txtPath.Text = CurrentPath;

        if (returnFocus) FocusPanel();
    }

    private void EnsureSshTerminalForCurrentPath()
    {
        string? sshSessionName = ExtractSshSessionName(CurrentPath);
        if (sshSessionName == null) return;

        if (!_sshTerminals.TryGetValue(sshSessionName, out var terminal))
        {
            terminal = new TerminalControl();
            terminal.SetResourceReference(FontSizeProperty, "AppFilePaneFontSize");
            terminal.SessionExited += Terminal_SessionExited;
            _sshTerminals[sshSessionName] = terminal;
        }

        if (!ReferenceEquals(_terminal, terminal) || terminalHost.Child != terminal)
        {
            _terminal = terminal;
            terminalHost.Child = terminal;
        }

        _sshTerminalPath = CurrentPath;

        if (!_terminal.IsRunning)
        {
            var session = FindSshSession(sshSessionName);
            if (session != null)
            {
                StatusMessage?.Invoke(this, "");
                _terminal.StartSsh(session);
            }
        }
    }

    private void Terminal_SessionExited(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (sender is TerminalControl dead)
            {
                foreach (var pair in _sshTerminals.ToList())
                {
                    if (ReferenceEquals(pair.Value, dead))
                        _sshTerminals.Remove(pair.Key);
                }
            }

            string msg = _terminal.IsNetworkError
                ? "Terminal closed: connection lost."
                : "Terminal closed.";

            if (IsTerminalVisible && ReferenceEquals(sender, _terminal))
                HideTerminal(returnFocus: true);

            TerminalVisibilityChanged?.Invoke(this, EventArgs.Empty);
            ConnectionTabsChanged?.Invoke(this, EventArgs.Empty);
            StatusMessage?.Invoke(this, msg);
        }));
    }

    private static string? ExtractSshSessionName(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return null;

        string rest = path.Substring(6);
        int slash = rest.IndexOf('/');
        string name = slash < 0 ? rest : rest.Substring(0, slash);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private SshSession? FindSshSession(string name)
    {
        var settings = AppSettings.Load();
        return settings.SshSessions.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            $"{s.Username}@{s.Host}".Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public void CloseSshTerminal()
    {
        CloseSshTerminal(ExtractSshSessionName(CurrentPath));
    }

    public void CloseSshTerminal(string? sessionName)
    {
        TerminalControl? terminal = null;

        if (!string.IsNullOrEmpty(sessionName))
            _sshTerminals.TryGetValue(sessionName, out terminal);

        if (terminal == null)
            terminal = _terminal;

        if (terminal != null && terminal.IsRunning)
            terminal.SendText("\x04");

        if (ReferenceEquals(_terminal, terminal) && IsTerminalVisible)
            HideTerminal(returnFocus: true);
    }

    public void ForgetLastSshPath(string? sessionName)
    {
        if (string.IsNullOrEmpty(sessionName) || string.IsNullOrEmpty(_lastSshPath))
            return;

        if (_lastSshPath.StartsWith($"ssh://{sessionName}", StringComparison.OrdinalIgnoreCase))
            _lastSshPath = null;
    }

    #endregion

    #region Bookmarks

    public void SetConnectionBookmarks(IReadOnlyList<ConnectionBookmark> items)
    {
        string signature = string.Join("|", items.Select(i =>
            $"{i.Title}\u001f{i.SessionName}\u001f{i.TargetPath}\u001f{i.IsActive}\u001f{i.IsTerminal}"));

        if (signature == _lastBookmarkSignature) return;
        _lastBookmarkSignature = signature;

        spConnections.Children.Clear();

        if (items.Count == 0)
        {
            pnlTabStrip.Visibility = Visibility.Collapsed;
            return;
        }

        pnlTabStrip.Visibility = Visibility.Visible;

        foreach (var item in items)
            spConnections.Children.Add(CreateChromeTab(item));
    }

    private Border CreateChromeTab(ConnectionBookmark item)
    {
        // Resolve the exact font size from resources to ensure visual consistency with drive buttons
        double fontSize = 14;
        if (TryFindResource("AppFilePaneFontSize") is double resSize)
        {
            fontSize = resSize;
        }

        // Configure the border container matching the dimensions and layout of drive buttons
        var tab = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(10, 1, 10, 0),
            Margin = new Thickness(0, 1, 4, 1),
            MinWidth = 72,
            MaxWidth = 180,
            MinHeight = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Tag = item,
            SnapsToDevicePixels = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1)
        };

        // Set initial border brush depending on whether the tab is active
        tab.SetResourceReference(Border.BorderBrushProperty,
            item.IsActive ? "Brush.Accent" : "Brush.Border");

        var row = new DockPanel();

        // Create vector path icon matching the style and size of local drive buttons
        var icon = new System.Windows.Shapes.Path
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            StrokeThickness = 0
        };
        // Use Fill instead of Stroke since these geometries are closed shapes
        icon.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "Brush.TextPrimary");

        // Bind path data to the appropriate geometry resource based on connection type
        icon.SetResourceReference(System.Windows.Shapes.Path.DataProperty,
            item.IsTerminal ? "Geom.SshServer" : "Geom.SftpTab");

        DockPanel.SetDock(icon, Dock.Left);

        // Display the connection title using primary text color for optimal readability
        var title = new TextBlock
        {
            Text = item.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Normal,
            FontSize = fontSize
        };

        TextOptions.SetTextFormattingMode(title, TextFormattingMode.Display);
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");

        // Create the close button ("×") placed on the right side
        var close = new TextBlock
        {
            Text = "×",
            FontSize = 15,
            Margin = new Thickness(8, -1, 0, 0),
            Opacity = item.IsActive ? 0.75 : 0.45,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };

        TextOptions.SetTextFormattingMode(close, TextFormattingMode.Display);
        close.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        DockPanel.SetDock(close, Dock.Right);

        // Assemble elements inside the layout panel
        row.Children.Add(close);
        row.Children.Add(icon);
        row.Children.Add(title);
        tab.Child = row;

        // Handle mouse hover effects to match the general app design
        tab.MouseEnter += (_, _) =>
        {
            close.Opacity = 0.95;
            tab.SetResourceReference(Border.BackgroundProperty, "Brush.Hover");

            if (!item.IsActive)
            {
                tab.SetResourceReference(Border.BorderBrushProperty, "Brush.TextSecondary");
            }
        };

        tab.MouseLeave += (_, _) =>
        {
            close.Opacity = item.IsActive ? 0.75 : 0.45;
            tab.Background = Brushes.Transparent;
            tab.SetResourceReference(Border.BorderBrushProperty,
                item.IsActive ? "Brush.Accent" : "Brush.Border");
        };

        // Wire up click event handlers for navigation and closing
        tab.MouseLeftButtonDown += ConnectionTab_MouseLeftButtonDown;
        close.PreviewMouseLeftButtonDown += ConnectionTabClose_MouseLeftButtonDown;

        return tab;
    }

    private async void ConnectionTab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ConnectionBookmark item) return;
        if (string.IsNullOrEmpty(item.SessionName)) return;

        e.Handled = true;
        PaneGotFocus?.Invoke(this, EventArgs.Empty);

        bool openTerminal = item.IsTerminal
            || (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool alreadyOnSession = CurrentPath.StartsWith(
            $"ssh://{item.SessionName}", StringComparison.OrdinalIgnoreCase);

        if (openTerminal)
            PendingTerminalActivation = true;
        else if (IsTerminalVisible)
            HideTerminal(returnFocus: false);

        if (!alreadyOnSession)
        {
            if (openTerminal)
                SuppressNextSftpBookmark = true;
            await NavigateAsync(item.TargetPath);
        }

        if (openTerminal)
        {
            if (!IsTerminalVisible)
                ToggleSshTerminal();
            else
                EnsureSshTerminalForCurrentPath();
        }

        PendingTerminalActivation = false;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (item.IsTerminal && IsTerminalVisible)
                _terminal.Focus();
            else
                FocusPanel();
        }), System.Windows.Threading.DispatcherPriority.Background);

        ConnectionTabsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void ConnectionTabClose_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var tab = FindAncestor<Border>((DependencyObject)sender);
        if (tab?.Tag is not ConnectionBookmark item) return;
        if (string.IsNullOrEmpty(item.SessionName)) return;

        if (item.IsTerminal)
        {
            CloseSshTerminal(item.SessionName);
        }
        else
        {
            ForgetLastSshPath(item.SessionName);

            if (item.IsActive &&
                CurrentPath.StartsWith($"ssh://{item.SessionName}", StringComparison.OrdinalIgnoreCase))
            {
                await NavigateAsync(@"\\Network\");
            }
        }

        ConnectionBookmarkClosed?.Invoke(this, item);
        ConnectionTabsChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion
}
