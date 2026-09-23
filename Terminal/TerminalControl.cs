using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using R2Cmd.Terminal;

namespace R2Cmd.Controls;

// Draws the pseudo console with WPF primitives, so the terminal picks up the
// application theme instead of looking like a pasted in console window.
public sealed class TerminalControl : Control, IDisposable
{
    private const double ScrollbarWidth = 8;
    private const double PadLeft = 4;
    private const double PadTop = 2;

    private readonly VtScreen _screen;
    // Using a tuple to store the rented buffer and the actual length of the payload
    private readonly ConcurrentQueue<(char[] Buffer, int Length)> _pending = new();
    private readonly DispatcherTimer _timer;

    private ITerminalSession? _session;

    // --- FONT & RENDERING INFRASTRUCTURE ---
    private Typeface _typefaceNormal = null!;
    private Typeface _typefaceBold = null!;
    private GlyphTypeface? _glyphTypefaceNormal;
    private GlyphTypeface? _glyphTypefaceBold;

    private double _cellWidth = 8, _cellHeight = 16, _baseline;
    private double _pixelsPerDip = 1.0;
    private long _renderedVersion = -1;
    private bool _caretOn = true;
    private int _caretTicks;

    // Reusable buffer for character processing (safe to mutate, not retained by WPF)
    private char[] _charBuffer = Array.Empty<char>();

    // Caches for read-only typography structures. GlyphRun retains these, but since they
    // contain identical data for any text run of length N, we can share them safely.
    private double[][] _zeroAdvancesCache = Array.Empty<double[]>();
    private Point[][] _glyphOffsetsCache = Array.Empty<Point[]>();
    private double _cachedCellWidth = -1;

    // --- SLEEP & TIMEOUT CLOCKS ---
    private DateTime _lastTickTime = DateTime.UtcNow;
    private DateTime _lastActivityTime = DateTime.UtcNow;
    // --- RESIZE DEBOUNCING ---
    private DateTime _lastResizeTime = DateTime.UtcNow;
    private (int cols, int rows)? _pendingResize;

    // Start parameters are held until the control has a real size
    private bool _startPending;
    private string? _pendingWorkingDirectory;
    private string? _pendingShellPath;
    private SshSession? _pendingSshSession;

    private int _scrollOffset;
    private volatile bool _scrollPending;
    private bool _thumbDragging;
    private double _thumbGrabOffset;

    private bool _hasSelection;
    private bool _selecting;
    private (int Line, int Col) _selStart;
    private (int Line, int Col) _selEnd;

    public event EventHandler? SessionExited;

    public bool IsRunning => _startPending || _session?.IsRunning == true;

    public bool IsSshSession => _session is SshShellSession;

    // True if the terminal was killed due to sleep mode, timeout, or network loss
    public bool IsNetworkError { get; private set; }

    private static readonly Color[] FallbackPalette =
    {
        Color.FromRgb(0x00, 0x00, 0x00), Color.FromRgb(0xCD, 0x00, 0x00),
        Color.FromRgb(0x00, 0xCD, 0x00), Color.FromRgb(0xCD, 0xCD, 0x00),
        Color.FromRgb(0x00, 0x00, 0xEE), Color.FromRgb(0xCD, 0x00, 0xCD),
        Color.FromRgb(0x00, 0xCD, 0xCD), Color.FromRgb(0xE5, 0xE5, 0xE5),

        Color.FromRgb(0x7F, 0x7F, 0x7F), Color.FromRgb(0xFF, 0x00, 0x00),
        Color.FromRgb(0x00, 0xFF, 0x00), Color.FromRgb(0xFF, 0xFF, 0x00),
        Color.FromRgb(0x5C, 0x5C, 0xFF), Color.FromRgb(0xFF, 0x00, 0xFF),
        Color.FromRgb(0x00, 0xFF, 0xFF), Color.FromRgb(0xFF, 0xFF, 0xFF)
    };

    private static readonly string[] PaletteKeys = BuildPaletteKeys();

    private static string[] BuildPaletteKeys()
    {
        var keys = new string[16];
        for (int i = 0; i < keys.Length; i++) keys[i] = "Color.Terminal.Ansi" + i;
        return keys;
    }

    private readonly Color[] _palette = new Color[16];

    private readonly Dictionary<Color, SolidColorBrush> _brushCache = new();

    private Brush _backgroundBrush = Brushes.Black;
    private Brush _foregroundBrush = Brushes.White;
    private Brush _caretBrush = Brushes.Gray;
    private Color _defaultFg = Colors.White;
    private Color _defaultBg = Colors.Black;
    private Color _selectionColor = Color.FromRgb(0x55, 0x55, 0x55);

    public TerminalControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;

        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New");

        // Bind the terminal's font size to the global application font size metric.
        // ZoomManager dynamically updates this resource.
        SetResourceReference(FontSizeProperty, "AppFontSize");

        _screen = new VtScreen(80, 25);
        _screen.ClipboardCopyRequested += OnClipboardCopyRequested;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;

        // Force a theme refresh on first render
        RefreshTheme();

        // Listen for OS level theme changes
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
        {
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.General ||
                e.Category == Microsoft.Win32.UserPreferenceCategory.Color)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshTheme();
                    InvalidateVisual();
                }));
            }
        };

        // Listen for internal app theme toggles
        ThemeManager.ThemeChanged += (s, e) =>
        {
            RefreshTheme();
            InvalidateVisual();
        };
    }

    private void OnClipboardCopyRequested(string text)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { Clipboard.SetText(text); } catch { }
        }));
    }

    public void Start(string? workingDirectory, string? shellPath = null)
    {
        if (_session != null || _startPending) return;

        _pendingWorkingDirectory = workingDirectory;
        _pendingShellPath = shellPath;
        _startPending = true;

        TryStartSession();
    }

    private void TryStartSession()
    {
        if (!_startPending || _session != null) return;
        if (_pendingSshSession != null) return;
        if (ActualWidth < 1 || ActualHeight < 1) return;

        _startPending = false;

        string shell = string.IsNullOrWhiteSpace(_pendingShellPath)
            ? ConPtySession.ResolveDefaultShell()
            : _pendingShellPath!;

        MeasureCell();
        var (cols, rows) = MeasureGrid();
        _screen.Resize(cols, rows);

        IsNetworkError = false;

        // Reset scroll position and fully clear the screen and scrollback history
        // \u001bc resets the terminal state, \x1b[3J clears the scrollback buffer
        _scrollOffset = 0;
        WriteLocal("\u001bc\x1b[3J");

        _lastTickTime = DateTime.UtcNow;
        _lastActivityTime = DateTime.UtcNow;

        try
        {
            _session = new ConPtySession(shell, _pendingWorkingDirectory, cols, rows);
        }
        catch (Exception ex)
        {
            WriteLocal($"\r\n Cannot start terminal: {ex.Message}\r\n");
            InvalidateVisual();
            return;
        }

        WireSessionEvents();
    }

    public void StartSsh(SshSession session)
    {
        if (_session != null || _startPending) return;
        _pendingSshSession = session;
        _pendingWorkingDirectory = null;
        _pendingShellPath = null;
        _startPending = true;
        TryStartSshSession();
    }

    private void TryStartSshSession()
    {
        if (!_startPending || _session != null) return;
        if (_pendingSshSession == null) return;
        if (ActualWidth < 1 || ActualHeight < 1) return;

        _startPending = false;
        MeasureCell();
        var (cols, rows) = MeasureGrid();
        _screen.Resize(cols, rows);

        IsNetworkError = false;
        WriteLocal("\u001bc");
        WriteLocal("Connecting to SSH...\r\n");
        InvalidateVisual();

        _lastTickTime = DateTime.UtcNow;
        _lastActivityTime = DateTime.UtcNow;

        var sessionInfo = _pendingSshSession;

        // Connect off the UI thread so the window stays responsive
        System.Threading.Tasks.Task.Run(() =>
        {
            ITerminalSession? session = null;
            Exception? error = null;
            int maxRetries = 3;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    error = null;
                    if (attempt > 1)
                    {
                        // Notify the user about the reconnection attempt on the UI thread
                        Dispatcher.BeginInvoke(new Action(() =>
                            WriteLocal($"\r\n[Attempt {attempt}/{maxRetries}] Reconnecting to SSH...\r\n")));

                        // Wait for 2 seconds before the next attempt to allow network stabilization
                        System.Threading.Thread.Sleep(2000);
                    }

                    // Attempt to establish the SSH connection
                    session = new SshShellSession(sessionInfo, cols, rows);

                    // If no exception occurred, the connection was successful, break the retry loop
                    break;
                }
                catch (Exception ex)
                {
                    error = ex;

                    // If the user closed the terminal window during the attempts, abort immediately
                    if (_pendingSshSession == null && _session == null) break;
                }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                // User closed the terminal while connecting
                if (_pendingSshSession == null && _session == null)
                {
                    try { session?.Dispose(); } catch { }
                    return;
                }

                if (error != null || session == null)
                {
                    WriteLocal($"\r\n Cannot connect to SSH: {error?.Message ?? "unknown error"}\r\n");
                    InvalidateVisual();
                    try { session?.Dispose(); } catch { }
                    return;
                }

                if (_session != null)
                {
                    try { session.Dispose(); } catch { }
                    return;
                }

                _session = session;
                _pendingSshSession = null;

                // Reset scroll and wipe the entire history of connection errors/attempts
                _scrollOffset = 0;
                WriteLocal("\u001bc\x1b[3J");

                WireSessionEvents();
            }));
        });
    }

    private void WireSessionEvents()
    {
        if (_session == null) return;

        _session.Output += (buffer, count) =>
                {
                    _lastActivityTime = DateTime.UtcNow;

                    // Rent an array from the shared pool to avoid allocating new memory on the heap
                    var rented = System.Buffers.ArrayPool<char>.Shared.Rent(count);
                    Array.Copy(buffer, rented, count);

                    _pending.Enqueue((rented, count));

                    // Replaces Dispatcher.BeginInvoke flooding with a lightweight flag
                    _scrollPending = true;
                };

        _session.Exited += () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_session == null) return; // Already handled by OnTick or manual Stop

                    if (IsSshSession && !_session.IsRunning)
                    {
                        IsNetworkError = true;
                        WriteLocal("\r\n\r\n[Network error: Connection lost]\r\n");
                    }
                    else
                    {
                        WriteLocal("\r\n\r\n[Session closed by remote host]\r\n");
                    }

                    Stop();
                    SessionExited?.Invoke(this, EventArgs.Empty);
                    Keyboard.ClearFocus();
                }));

        _timer.Start();
        InvalidateVisual();
    }

    private void AutoScrollToBottom()
    {
        if (_scrollOffset > 0)
        {
            _scrollOffset = 0;
            InvalidateVisual();
        }
    }

    public void Stop()
    {
        _startPending = false;
        _pendingSshSession = null;

        _timer.Stop();

        // Clear stale output from the dead session to prevent it from bleeding into the next one
        while (_pending.TryDequeue(out var chunk))
        {
            System.Buffers.ArrayPool<char>.Shared.Return(chunk.Buffer);
        }

        var sessionToDispose = _session;
        _session = null;

        if (sessionToDispose != null)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try { sessionToDispose.Dispose(); } catch { }
            });
        }
    }

    public void Dispose() => Stop();

    public void SendCommand(string command) => SafeWrite(command + "\r");

    // Allows sending raw text/sequences without an automatic newline
    public void SendText(string text) => SafeWrite(text);

    public void ChangeDirectory(string path)
    {
        if (_session == null || string.IsNullOrWhiteSpace(path)) return;
        if (_session is SshShellSession) return;

        try
        {
            // Escape first so PSReadLine drops the current edit line, then cd.
            _session.Write("\x1b");

            string cmd = $"cd \"{path.TrimEnd('\\')}\"";

            // Notify the VT parser that this specific string should be blanked out.
            // This removes visual noise without desyncing cursor coordinates with ConPTY.
            _screen.SuppressNextEcho(cmd);

            _session.Write(cmd + "\r");
        }
        catch
        {
            IsNetworkError = true;
            WriteLocal("\r\n\r\n[Network error: Connection timed out or lost]\r\n");
            Stop();
            SessionExited?.Invoke(this, EventArgs.Empty);
            Keyboard.ClearFocus();
        }
    }

    private void WriteLocal(string text)
    {
        // Implicitly converts string to ReadOnlySpan<char> without heap allocations
        _screen.Write(text);
    }

    private void FlushPendingResize()
    {
        if (!_pendingResize.HasValue || _session == null) return;

        var (cols, rows) = _pendingResize.Value;
        _pendingResize = null;
        _session.Resize(cols, rows);
    }

    private void SafeWrite(string text)
    {
        if (_session == null) return;

        try
        {
            _session.Write(text);

            if (text == "\x04" && IsSshSession)
            {
                System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_session != null)
                        {
                            IsNetworkError = true;
                            WriteLocal("\r\n\r\n[Network error: Server did not respond to EOF]\r\n");
                            Stop();
                            SessionExited?.Invoke(this, EventArgs.Empty);
                            Keyboard.ClearFocus();
                        }
                    }));
                });
            }
        }
        catch
        {
            IsNetworkError = true;
            WriteLocal("\r\n\r\n[Network error: Connection timed out or lost]\r\n");
            Stop();
            SessionExited?.Invoke(this, EventArgs.Empty);
            Keyboard.ClearFocus();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.UtcNow;

        bool wokeFromSleep = (now - _lastTickTime).TotalSeconds > 10;

        // Connection already dead (server reboot, network drop)
        if (_session != null && IsSshSession && !_session.IsRunning)
        {
            IsNetworkError = true;
            WriteLocal("\r\n\r\n[Network error: Connection lost]\r\n");
            Stop();
            SessionExited?.Invoke(this, EventArgs.Empty);
            Keyboard.ClearFocus();
            _lastTickTime = now;
            return;
        }

        // Dropped idle timeout check. Users might run long compilations or watch logs.
        // We only drop the connection on system wake-up, as the network interface usually resets.
        if (_session != null && IsSshSession && wokeFromSleep)
        {
            IsNetworkError = true;
            WriteLocal("\r\n\r\n[System wake-up detected. Dropping stale SSH connection...]\r\n");

            Stop();
            SessionExited?.Invoke(this, EventArgs.Empty);
            Keyboard.ClearFocus();

            _lastTickTime = now;
            return;
        }

        _lastTickTime = now;

        // Execute pending resize if 150ms has passed since the last window size change
        if (_pendingResize.HasValue && (now - _lastResizeTime).TotalMilliseconds > 150)
        {
            var (cols, rows) = _pendingResize.Value;
            _pendingResize = null;

            _session?.Resize(cols, rows);
        }

        int scrollbackBefore = _screen.Scrollback.Count;

        bool wrote = false;
        while (_pending.TryDequeue(out var chunk))
        {
            // Pass the exact rented length as a Span
            _screen.Write(chunk.Buffer.AsSpan(0, chunk.Length));
            wrote = true;

            // Return the array back to the pool immediately after writing to the screen
            System.Buffers.ArrayPool<char>.Shared.Return(chunk.Buffer);
        }

        // Once per batch, before the frame is drawn
        if (wrote) _screen.MaskSuppressedCommands();

        // Handle auto-scroll efficiently on the render tick
        if (_scrollPending)
        {
            _scrollPending = false;
            AutoScrollToBottom();
        }

        int added = _screen.Scrollback.Count - scrollbackBefore;
        if (_scrollOffset > 0 && added > 0)
            _scrollOffset = Math.Min(_scrollOffset + added, _screen.Scrollback.Count);

        // =====================================================================
        // The blink redraws the whole terminal, so it only happens when the
        // caret is actually drawn (see OnRender). It used to fire twice a
        // second regardless: a visible terminal that did not have focus, which
        // is most of the time while working in the panes, was fully repainted
        // for a caret it never shows. With CPU rendering that was steady load
        // for nothing. Focus changes repaint on their own (OnGot/LostKeyboardFocus).
        // =====================================================================
        if (++_caretTicks >= 15)
        {
            _caretTicks = 0;
            if (_screen.CursorVisible && IsFocused && _scrollOffset == 0)
            {
                _caretOn = !_caretOn;
                InvalidateVisual();
                return;
            }
        }

        if (_screen.Version != _renderedVersion) InvalidateVisual();

        Cursor = _screen.MouseTrackingMode != MouseMode.None ? Cursors.Arrow : Cursors.IBeam;
    }

    private void MeasureCell()
    {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        _typefaceNormal = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _typefaceBold = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        _typefaceNormal.TryGetGlyphTypeface(out _glyphTypefaceNormal);
        _typefaceBold.TryGetGlyphTypeface(out _glyphTypefaceBold);

        var probe = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typefaceNormal, FontSize, Brushes.Black, _pixelsPerDip);

        _cellWidth = probe.WidthIncludingTrailingWhitespace / 10.0;
        _cellHeight = Math.Ceiling(probe.Height);
        _baseline = Math.Round(probe.Baseline);

        if (_cellWidth <= 0) _cellWidth = FontSize * 0.6;
        if (_cellHeight <= 0) _cellHeight = FontSize * 1.4;
    }

    private (int cols, int rows) MeasureGrid()
    {
        double textWidth = ActualWidth - PadLeft * 2 - ScrollbarWidth;

        int cols = (int)Math.Max(8, Math.Floor(textWidth / _cellWidth));
        int rows = (int)Math.Max(2, Math.Floor((ActualHeight - PadTop * 2) / _cellHeight));
        return (cols, rows);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_startPending)
        {
            if (_pendingSshSession != null) TryStartSshSession();
            else TryStartSession();
            return;
        }

        var (cols, rows) = MeasureGrid();

        if (cols != _screen.Cols || rows != _screen.Rows)
        {
            _screen.Resize(cols, rows);
            if (_scrollOffset > 0) _scrollOffset = Math.Min(_scrollOffset, _screen.Scrollback.Count);
        }

        _pendingResize = (cols, rows);
        _lastResizeTime = DateTime.UtcNow;
    }
    private bool TryGetLine(int absolute, out Cell[] source, out int start, out int width)
    {
        var scrollback = _screen.Scrollback;

        if (absolute >= 0 && absolute < scrollback.Count)
        {
            source = scrollback[absolute];
            start = 0;
            width = source.Length;
            return true;
        }

        int row = absolute - scrollback.Count;
        if (row >= 0 && row < _screen.Rows)
        {
            source = _screen.Buffer;
            start = row * _screen.Cols;
            width = _screen.Cols;
            return true;
        }

        source = Array.Empty<Cell>();
        start = width = 0;
        return false;
    }

    private int TopVisibleLine => Math.Max(0, _screen.Scrollback.Count - _scrollOffset);

    private int MaxScrollOffset => _screen.Scrollback.Count;

    private void ScrollBy(int lines)
    {
        int next = Math.Min(Math.Max(_scrollOffset + lines, 0), MaxScrollOffset);
        if (next == _scrollOffset) return;

        _scrollOffset = next;
        InvalidateVisual();
    }

    private void ScrollToBottom()
    {
        if (_scrollOffset == 0) return;

        _scrollOffset = 0;
        InvalidateVisual();
    }

    private (int Line, int Col) PointToCell(Point p)
    {
        int row = (int)Math.Floor((p.Y - PadTop) / _cellHeight);
        row = Math.Min(Math.Max(row, 0), _screen.Rows - 1);

        int col = (int)Math.Round((p.X - PadLeft) / _cellWidth);
        col = Math.Min(Math.Max(col, 0), _screen.Cols);

        return (TopVisibleLine + row, col);
    }

    private ((int Line, int Col) start, (int Line, int Col) end) OrderedSelection()
    {
        bool forward = _selStart.Line < _selEnd.Line ||
                       (_selStart.Line == _selEnd.Line && _selStart.Col <= _selEnd.Col);

        return forward ? (_selStart, _selEnd) : (_selEnd, _selStart);
    }

    private bool IsSelected(int line, int col)
    {
        if (!_hasSelection) return false;

        var (start, end) = OrderedSelection();
        if (line < start.Line || line > end.Line) return false;
        if (line == start.Line && col < start.Col) return false;
        if (line == end.Line && col >= end.Col) return false;

        return true;
    }

    private void ClearSelection()
    {
        if (!_hasSelection) return;

        _hasSelection = false;
        InvalidateVisual();
    }

    private string GetSelectedText()
    {
        if (!_hasSelection) return "";

        var (start, end) = OrderedSelection();
        var text = new StringBuilder();

        for (int line = start.Line; line <= end.Line; line++)
        {
            if (!TryGetLine(line, out var source, out int offset, out int width)) continue;

            int from = line == start.Line ? Math.Min(start.Col, width) : 0;
            int to = line == end.Line ? Math.Min(end.Col, width) : width;

            var lineText = new StringBuilder();
            for (int x = from; x < to; x++)
            {
                char ch = source[offset + x].Ch;
                lineText.Append(ch == '\0' ? ' ' : ch);
            }

            if (line > start.Line) text.Append(Environment.NewLine);
            text.Append(lineText.ToString().TrimEnd());
        }

        return text.ToString();
    }

    private void CopySelection()
    {
        string text = GetSelectedText();
        if (text.Length == 0) return;

        try { Clipboard.SetText(text); } catch { }
        ClearSelection();
    }

    private void PasteClipboard()
    {
        if (_session == null) return;

        string text;
        try { text = Clipboard.GetText(); } catch { return; }
        if (string.IsNullOrEmpty(text)) return;

        text = text.Replace("\r\n", "\r").Replace('\n', '\r');

        // Honor Bracketed Paste mode to prevent unintended multi-line execution
        if (_screen.BracketedPasteMode)
        {
            SafeWrite("\x1b[200~");
            SafeWrite(text);
            SafeWrite("\x1b[201~");
        }
        else
        {
            SafeWrite(text);
        }

        ScrollToBottom();
    }

    private Brush ThemeBrush(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private SolidColorBrush BrushFor(Color color)
    {
        if (_brushCache.TryGetValue(color, out var cached)) return cached;

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushCache[color] = brush;
        return brush;
    }

    private Color Resolve(int color, Color themeDefault)
    {
        if (color < 0) return themeDefault;
        if (color >= VtScreen.TrueColorBase)
            return Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);

        if (color < 16) return _palette[color];

        if (color < 232)
        {
            int c = color - 16;
            return Color.FromRgb(CubeSteps[c / 36], CubeSteps[(c / 6) % 6], CubeSteps[c % 6]);
        }

        byte grey = (byte)(8 + (color - 232) * 10);
        return Color.FromRgb(grey, grey, grey);
    }

    private static readonly byte[] CubeSteps = { 0, 95, 135, 175, 215, 255 };

    public void RefreshTheme()
    {
        bool changed = false;

        for (int i = 0; i < _palette.Length; i++)
        {
            Color color = TryFindResource(PaletteKeys[i]) is Color themed ? themed : FallbackPalette[i];
            if (_palette[i] != color) { _palette[i] = color; changed = true; }
        }

        _backgroundBrush = ThemeBrush("Brush.Terminal.Background", Colors.Black);
        _foregroundBrush = ThemeBrush("Brush.Terminal.Foreground", Colors.White);
        _caretBrush = ThemeBrush("Brush.Terminal.Cursor", Colors.Gray);
        Brush selection = ThemeBrush("Brush.Selection", Color.FromRgb(0x55, 0x55, 0x55));

        Color fg = (_foregroundBrush as SolidColorBrush)?.Color ?? Colors.White;
        Color bg = (_backgroundBrush as SolidColorBrush)?.Color ?? Colors.Black;
        Color sel = (selection as SolidColorBrush)?.Color ?? Color.FromRgb(0x55, 0x55, 0x55);

        if (fg != _defaultFg || bg != _defaultBg || sel != _selectionColor) changed = true;

        _defaultFg = fg;
        _defaultBg = bg;
        _selectionColor = sel;

        if (changed) _brushCache.Clear();
    }

    protected override void OnRender(DrawingContext dc)
    {
        _renderedVersion = _screen.Version;

        // Rebuild structural caches if the grid grows or the font zoom (cell width) changes
        if (_charBuffer.Length < _screen.Cols || Math.Abs(_cachedCellWidth - _cellWidth) > 0.01)
        {
            _charBuffer = new char[_screen.Cols];
            _cachedCellWidth = _cellWidth;

            _zeroAdvancesCache = new double[_screen.Cols + 1][];
            _glyphOffsetsCache = new Point[_screen.Cols + 1][];

            for (int i = 0; i <= _screen.Cols; i++)
            {
                _zeroAdvancesCache[i] = new double[i]; // By default, double arrays are filled with 0.0

                var offsets = new Point[i];
                for (int j = 0; j < i; j++) offsets[j] = new Point(j * _cellWidth, 0);
                _glyphOffsetsCache[i] = offsets;
            }
        }

        dc.DrawRectangle(_backgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

        int top = TopVisibleLine;

        for (int y = 0; y < _screen.Rows; y++)
        {
            int absolute = top + y;
            if (!TryGetLine(absolute, out var cells, out int offset, out int width)) continue;

            double py = PadTop + y * _cellHeight;
            int x = 0;

            while (x < width)
            {
                var first = cells[offset + x];
                bool firstSelected = IsSelected(absolute, x);
                int runStart = x;

                while (x < width && SameStyle(cells[offset + x], first) &&
                       IsSelected(absolute, x) == firstSelected) x++;

                int length = x - runStart;
                bool blank = true;

                bool isBold = (first.Flags & CellFlags.Bold) != 0;
                var glyphTypeface = isBold && _glyphTypefaceBold != null ? _glyphTypefaceBold : _glyphTypefaceNormal;

                for (int i = 0; i < length; i++)
                {
                    char ch = cells[offset + runStart + i].Ch;
                    _charBuffer[i] = ch == '\0' ? ' ' : ch;

                    if (_charBuffer[i] != ' ') blank = false;
                }

                Color fg = Resolve(first.Fg, _defaultFg);
                Color bg = Resolve(first.Bg, _defaultBg);
                if ((first.Flags & CellFlags.Inverse) != 0) (fg, bg) = (bg, fg);
                if (firstSelected) bg = _selectionColor;

                double px = PadLeft + runStart * _cellWidth;

                // Background block
                if (first.Bg >= 0 || firstSelected || (first.Flags & CellFlags.Inverse) != 0)
                {
                    dc.DrawRectangle(BrushFor(bg), null, new Rect(px, py, (length * _cellWidth) + 1.0, _cellHeight + 1.0));
                }

                if (!blank)
                {
                    var fgBrush = BrushFor(fg);

                    // We MUST allocate a new array for glyphs because WPF retains this reference.
                    ushort[] glyphIndices = new ushort[length];

                    // We can safely reuse these arrays because their contents are identical
                    // for ANY text run of this exact length, preventing memory allocation spam.
                    double[] zeroAdvanceWidths = _zeroAdvancesCache[length];
                    Point[] glyphOffsets = _glyphOffsetsCache[length];

                    // Fallback glyph for missing characters to prevent KeyNotFoundException crashes
                    ushort fallbackGlyph = 0;
                    if (glyphTypeface != null)
                    {
                        glyphTypeface.CharacterToGlyphMap.TryGetValue('?', out fallbackGlyph);
                    }

                    for (int i = 0; i < length; i++)
                    {
                        char ch = _charBuffer[i];

                        // Fast path: try to get the exact glyph, fallback to '?' if missing
                        if (glyphTypeface == null || !glyphTypeface.CharacterToGlyphMap.TryGetValue(ch, out glyphIndices[i]))
                        {
                            glyphIndices[i] = fallbackGlyph;
                        }
                    }

                    var origin = new Point(px, py + _baseline);
                    var glyphRun = new GlyphRun(
                        glyphTypeface, 0, false, FontSize, (float)_pixelsPerDip,
                        glyphIndices, origin, zeroAdvanceWidths,
                        glyphOffsets, null, null, null, null, null);

                    dc.DrawGlyphRun(fgBrush, glyphRun);

                    // Reconstruct underline manually
                    if ((first.Flags & CellFlags.Underline) != 0)
                    {
                        double lineThickness = Math.Max(1.0, FontSize / 15.0);
                        double lineY = py + _baseline + lineThickness + 1;
                        dc.DrawRectangle(fgBrush, null, new Rect(px, lineY, length * _cellWidth, lineThickness));
                    }
                }
            }
        }

        if (_screen.CursorVisible && IsFocused && _caretOn && _scrollOffset == 0)
        {
            var caret = new Rect(PadLeft + _screen.CursorX * _cellWidth,
                PadTop + _screen.CursorY * _cellHeight, _cellWidth, _cellHeight);
            dc.DrawRectangle(_caretBrush, null, caret);
        }

        DrawScrollbar(dc);
    }

    private void DrawScrollbar(DrawingContext dc)
    {
        int total = _screen.Scrollback.Count + _screen.Rows;
        if (total <= _screen.Rows) return;

        double trackX = ActualWidth - ScrollbarWidth;

        double thumbHeight = Math.Max(20, ActualHeight * _screen.Rows / total);
        double travel = ActualHeight - thumbHeight;

        double progress = MaxScrollOffset == 0 ? 1 : 1.0 - (double)_scrollOffset / MaxScrollOffset;

        var thumb = new Rect(trackX + 2, travel * progress, ScrollbarWidth - 4, thumbHeight);
        var thumbBrush = ThemeBrush(_thumbDragging ? "Brush.ScrollThumbHover" : "Brush.ScrollThumb",
            Color.FromRgb(0x4A, 0x4A, 0x4A));

        dc.DrawRoundedRectangle(thumbBrush, null, thumb, 2, 2);
    }

    private static bool SameStyle(Cell a, Cell b) => a.Fg == b.Fg && a.Bg == b.Bg && a.Flags == b.Flags;

    // =========================================================================
    // MOUSE TRACKING & INTERACTION LOGIC
    // =========================================================================

    private void SendMouseEvent(MouseButton? button, bool isDown, Point p, bool isMotion)
    {
        if (_screen.MouseTrackingMode == MouseMode.None) return;
        if (isMotion && _screen.MouseTrackingMode < MouseMode.ButtonEvent) return;

        int cb = 0;
        if (isMotion) cb += 32;

        if (button == MouseButton.Left) cb += 0;
        else if (button == MouseButton.Middle) cb += 1;
        else if (button == MouseButton.Right) cb += 2;
        else if (!isDown) cb += 3; // Release event

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) cb += 4;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) cb += 8;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) cb += 16;

        var (line, col) = PointToCell(p);

        // VT sequences use 1-based coordinates
        int x = col + 1;
        int y = (line - TopVisibleLine) + 1;

        WriteMouseEvent(cb, x, y, isDown);
    }

    private void WriteMouseEvent(int cb, int x, int y, bool isDown)
    {
        x = Math.Max(1, Math.Min(x, _screen.Cols));
        y = Math.Max(1, Math.Min(y, _screen.Rows));

        if (_screen.MouseTrackingEncoding == MouseEncoding.SGR)
        {
            char state = isDown ? 'M' : 'm';
            SafeWrite($"\x1b[<{cb};{x};{y}{state}");
        }
        else if (_screen.MouseTrackingEncoding == MouseEncoding.URXVT)
        {
            SafeWrite($"\x1b[{cb};{x};{y}M");
        }
        else
        {
            if (!isDown) cb = 3;
            if (x > 223) x = 223;
            if (y > 223) y = 223;
            SafeWrite($"\x1b[M{(char)(32 + cb)}{(char)(32 + x)}{(char)(32 + y)}");
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (_screen.MouseTrackingMode != MouseMode.None)
        {
            int cb = e.Delta > 0 ? 64 : 65; // Scroll Up : Scroll Down
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) cb += 4;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) cb += 8;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) cb += 16;

            var (line, col) = PointToCell(e.GetPosition(this));
            int x = col + 1;
            int y = (line - TopVisibleLine) + 1;

            WriteMouseEvent(cb, x, y, true);
            e.Handled = true;
            return;
        }

        ScrollBy(e.Delta / 120 * 3);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        if (_screen.MouseTrackingMode != MouseMode.None)
        {
            CaptureMouse();
            SendMouseEvent(MouseButton.Left, true, e.GetPosition(this), false);
            e.Handled = true;
            return;
        }

        Point p = e.GetPosition(this);

        if (p.X >= ActualWidth - ScrollbarWidth - 2)
        {
            StartThumbDrag(p);
            e.Handled = true;
            return;
        }

        _selStart = _selEnd = PointToCell(p);
        _hasSelection = false;
        _selecting = true;

        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_screen.MouseTrackingMode != MouseMode.None)
        {
            SendMouseEvent(MouseButton.Left, false, e.GetPosition(this), false);
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        _selecting = false;
        _thumbDragging = false;

        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();

        // In apps with mouse tracking (MC, htop) — forward right-click to the server
        if (_screen.MouseTrackingMode != MouseMode.None)
        {
            CaptureMouse();
            SendMouseEvent(MouseButton.Right, true, e.GetPosition(this), false);
            e.Handled = true;
            return;
        }

        // Normal shell: right-click pastes clipboard (like most terminals)
        PasteClipboard();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);

        if (_screen.MouseTrackingMode != MouseMode.None)
        {
            SendMouseEvent(MouseButton.Right, false, e.GetPosition(this), false);
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        Point p = e.GetPosition(this);

        if (_screen.MouseTrackingMode != MouseMode.None)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                SendMouseEvent(MouseButton.Left, true, p, true);
            else if (e.RightButton == MouseButtonState.Pressed)
                SendMouseEvent(MouseButton.Right, true, p, true);
            else if (_screen.MouseTrackingMode == MouseMode.AnyEvent)
                SendMouseEvent(null, false, p, true); // Send pure hover events

            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        if (_thumbDragging) { UpdateThumbDrag(p); return; }
        if (!_selecting) return;

        if (p.Y < 0) ScrollBy(1);
        else if (p.Y > ActualHeight) ScrollBy(-1);

        _selEnd = PointToCell(p);
        _hasSelection = _selEnd != _selStart;
        InvalidateVisual();
    }

    private void StartThumbDrag(Point p)
    {
        int total = _screen.Scrollback.Count + _screen.Rows;
        if (total <= _screen.Rows) return;

        double thumbHeight = Math.Max(24, ActualHeight * _screen.Rows / total);
        double travel = ActualHeight - thumbHeight;
        double progress = MaxScrollOffset == 0 ? 1 : 1.0 - (double)_scrollOffset / MaxScrollOffset;
        double thumbTop = travel * progress;

        _thumbGrabOffset = p.Y >= thumbTop && p.Y <= thumbTop + thumbHeight
            ? p.Y - thumbTop
            : thumbHeight / 2;

        _thumbDragging = true;
        CaptureMouse();
        UpdateThumbDrag(p);
    }

    private void UpdateThumbDrag(Point p)
    {
        int total = _screen.Scrollback.Count + _screen.Rows;
        double thumbHeight = Math.Max(24, ActualHeight * _screen.Rows / total);
        double travel = ActualHeight - thumbHeight;
        if (travel <= 0) return;

        double position = Math.Min(Math.Max(p.Y - _thumbGrabOffset, 0), travel) / travel;

        _scrollOffset = (int)Math.Round(MaxScrollOffset * (1.0 - position));
        InvalidateVisual();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _caretOn = true;
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (_session == null || string.IsNullOrEmpty(e.Text)) return;

        _lastActivityTime = DateTime.UtcNow;

        // Switch to manual mode: stop hiding auto-commands so recalled history stays visible
        _screen.ClearSuppressedCommands();

        ClearSelection();
        ScrollToBottom();
        SafeWrite(e.Text);
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_session == null) return;

        _lastActivityTime = DateTime.UtcNow;

        // Switch to manual mode on any physical key press (like Up/Down arrows)
        _screen.ClearSuppressedCommands();

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && key == Key.C && _hasSelection) { CopySelection(); e.Handled = true; return; }
        if (ctrl && shift && key == Key.C) { CopySelection(); e.Handled = true; return; }
        if (ctrl && key == Key.V) { PasteClipboard(); e.Handled = true; return; }

        if (ctrl && key == Key.Q)
        {
            Stop();
            SessionExited?.Invoke(this, EventArgs.Empty);
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        // --- NEW: Clear Terminal (Ctrl + Shift + K) ---
        // Use Ctrl+Shift+K to avoid breaking Linux apps like nano where Ctrl+K cuts text
        if (ctrl && shift && key == Key.K)
        {
            WriteLocal("\x1bc\x1b[3J");
            FlushPendingResize();

            if (IsSshSession)
            {
                // In Bash, Escape is the Meta key. ESC + 'c' triggers 'capitalize-word'.
                // To avoid swallowing the 'c' in 'clear', we use Ctrl+U (\x15) instead.
                // Ctrl+U is the standard Linux shortcut to clear the current input line.
                _session?.Write("\x15");
                SafeWrite("clear\r");
            }
            else
            {
                // In PowerShell/Windows, Escape successfully clears the input line.
                _session?.Write("\x1b");
                SafeWrite("cls\r");
            }

            e.Handled = true;
            return;
        }

        if (shift && key == Key.Insert) { PasteClipboard(); e.Handled = true; return; }
        if (shift && key == Key.PageUp) { ScrollBy(_screen.Rows - 1); e.Handled = true; return; }
        if (shift && key == Key.PageDown) { ScrollBy(-_screen.Rows + 1); e.Handled = true; return; }
        if (key == Key.Escape && _hasSelection) { ClearSelection(); e.Handled = true; return; }

        string? sequence = key switch
        {
            Key.Enter => "\r",
            Key.Space => " ",
            Key.Tab => "\t",
            Key.Back => "\x7f",
            Key.Escape => "\x1b",

            Key.Up => _screen.ApplicationCursorKeys ? "\x1bOA" : "\x1b[A",
            Key.Down => _screen.ApplicationCursorKeys ? "\x1bOB" : "\x1b[B",
            Key.Right => _screen.ApplicationCursorKeys ? "\x1bOC" : "\x1b[C",
            Key.Left => _screen.ApplicationCursorKeys ? "\x1bOD" : "\x1b[D",

            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",

            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~",
            Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~",
            Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~",
            Key.F12 => "\x1b[24~",
            _ => null
        };

        if (sequence == null && ctrl && key is >= Key.A and <= Key.Z)
            sequence = ((char)(key - Key.A + 1)).ToString();

        if (sequence == null) return;

        ClearSelection();
        ScrollToBottom();

        SafeWrite(sequence);
        e.Handled = true;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // React to global font size scaling triggered by ZoomManager
        if (e.Property == FontSizeProperty && _session != null)
        {
            // Recalculate character cell dimensions based on the new font size
            MeasureCell();

            // Recalculate how many rows and columns fit in the current physical window size
            var (cols, rows) = MeasureGrid();

            // Resize the internal VT screen buffer and reflow the text
            _screen.Resize(cols, rows);

            // Queue the resize for the backend process (ConPTY / SSH)
            _pendingResize = (cols, rows);
            _lastResizeTime = DateTime.UtcNow;

            InvalidateVisual();
        }
    }
}
