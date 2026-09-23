using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace R2Cmd;

public partial class ViewerWindow : Window
{
    private enum ViewMode { Text, Markdown, Hex, Image }

    private const int MaxTextBytes = 8 * 1024 * 1024;
    private const int MaxHexBytes = 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp" };

    private readonly string _path;
    private readonly string _displayName;
    private readonly bool _isCodeFile;

    private ViewMode _mode = ViewMode.Text;

    // The mode the file was opened in. Alt+1 returns to it: for source code
    // that is Code (highlighting and line numbers), not plain Text.
    private readonly ViewMode _startMode;
    private bool _truncated;
    private long _fileSize;
    private string _encodingName = "";
    private bool _loading;
    private bool _isWrapped;
    private double _imageZoom;

    // Encoding
    private Encoding? _forcedEncoding;
    private int _encodingIndex;

    // Go to line
    private readonly StringBuilder _gotoBuffer = new();
    private DispatcherTimer? _gotoTimer;
    private const int GotoDebounceMs = 480;

    // Smooth mouse-wheel scrolling
    private readonly SmoothWheelScroller _scroller;

    public ViewerWindow(string path, string displayName)
    {
        InitializeComponent();

        _path = path;
        _displayName = displayName;
        Title = $"View: {displayName}";

        string extension = Path.GetExtension(path);
        string highlightingExtension = GetHighlightingExtension(path);

        _isCodeFile = FileTypes.Code.Contains(highlightingExtension);

        if (ImageExtensions.Contains(extension)) _mode = ViewMode.Image;
        else if (FileTypes.Markdown.Contains(extension)) _mode = ViewMode.Markdown;
        else if (_isCodeFile) _mode = ViewMode.Markdown;

        _startMode = _mode;

        if (TryFindResource("Brush.Background") is Brush background)
            Resources[SystemColors.ControlBrushKey] = background;

        docViewer.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnLinkNavigate));

        imageScroll.SizeChanged += (_, _) =>
        {
            if (_mode == ViewMode.Image && _imageZoom == 0)
                UpdateImageScale();
        };

        SourceInitialized += (_, _) => Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme, useSurfaceColor: true);

        _scroller = new SmoothWheelScroller(Dispatcher);
        _scroller.Attach(avalonCodeViewer);
        _scroller.Attach(docViewer);

        _gotoTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(GotoDebounceMs)
        };

        _gotoTimer.Tick += (_, _) =>
        {
            _gotoTimer.Stop();
            ExecuteGotoLine();
        };

        ContentRendered += (_, _) => LoadFile();
    }

    private static string GetHighlightingExtension(string path) =>
    AvalonEditDraculaTheme.GetHighlightingExtension(path);

    private void LoadFile()
    {
        if (_loading) return;
        _loading = true;

        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                ShowText("File not found: " + _path);
                UpdateStatus();
                return;
            }

            _fileSize = info.Length;

            if (_mode == ViewMode.Image) { LoadImage(); return; }
            if (_mode == ViewMode.Hex) { LoadHex(); return; }
            if (_mode == ViewMode.Markdown)
            {
                if (_isCodeFile) LoadHighlighted();
                else LoadMarkdown();
                return;
            }

            LoadText();
        }
        catch (Exception ex)
        {
            ShowText($"Cannot read the file.\r\n\r\n{ex.Message}");
            UpdateStatus();
        }
        finally { _loading = false; }
    }

    private bool TryReadText(out string content)
    {
        content = "";

        Encoding? encoding = _forcedEncoding ?? TextSearcher.DetectFileEncoding(_path);
        if (encoding == null) return false;

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        int toRead = (int)Math.Min(_fileSize, MaxTextBytes);
        _truncated = _fileSize > MaxTextBytes;

        var bytes = new byte[toRead];
        int read = stream.ReadAtLeast(bytes, toRead, throwOnEndOfStream: false);
        int skip = PreambleLength(encoding, bytes, read);

        _encodingName = _forcedEncoding != null
            ? EncodingChoices.Names[_encodingIndex]
            : EncodingName(encoding);

        content = encoding.GetString(bytes, skip, read - skip);
        return true;
    }

    private void LoadText()
    {
        if (!TryReadText(out string content))
        {
            if (LooksLikeImage(_path))
            {
                _mode = ViewMode.Image;
                LoadImage();
                return;
            }
            _mode = ViewMode.Hex;
            LoadHex();
            return;
        }

        ResetMatches();
        avalonCodeViewer.Text = content;
        avalonCodeViewer.FontSize = Math.Max(13, FontSize);
        avalonCodeViewer.Visibility = Visibility.Visible;
        docViewer.Visibility = Visibility.Collapsed;
        AvalonEditDraculaTheme.Apply(avalonCodeViewer, _path, showLineNumbers: false);
        ShowSurface(codeHost);
        UpdateStatus();
    }

    private void LoadHighlighted()
    {
        if (!TryReadText(out string content))
        {
            _mode = ViewMode.Hex;
            LoadHex();
            return;
        }

        ResetMatches();
        avalonCodeViewer.Text = content;
        avalonCodeViewer.FontSize = Math.Max(13, FontSize);
        avalonCodeViewer.Visibility = Visibility.Visible;
        docViewer.Visibility = Visibility.Collapsed;
        AvalonEditDraculaTheme.Apply(avalonCodeViewer, _path, showLineNumbers: true);

        ShowSurface(codeHost);
        UpdateStatus();
    }

    private void LoadMarkdown()
    {
        if (!TryReadText(out string content))
        {
            _mode = ViewMode.Hex;
            LoadHex();
            return;
        }

        ResetMatches();
        docViewer.Document = MarkdownRenderer.Render(content, Math.Max(13, FontSize));
        avalonCodeViewer.Visibility = Visibility.Collapsed;
        docViewer.Visibility = Visibility.Visible;
        ShowSurface(codeHost);
        UpdateStatus();
    }

    private void OnLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static readonly byte[][] ImageSignatures =
    {
        new byte[] { 0x89, 0x50, 0x4E, 0x47 },
        new byte[] { 0xFF, 0xD8, 0xFF },
        new byte[] { 0x47, 0x49, 0x46, 0x38 },
        new byte[] { 0x42, 0x4D },
        new byte[] { 0x49, 0x49, 0x2A, 0x00 },
        new byte[] { 0x4D, 0x4D, 0x00, 0x2A },
        new byte[] { 0x00, 0x00, 0x01, 0x00 },
    };

    private static bool LooksLikeImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[16];
            int read = stream.Read(head, 0, head.Length);
            if (read < 4) return false;

            foreach (var signature in ImageSignatures)
            {
                if (read < signature.Length) continue;
                bool match = true;
                for (int i = 0; i < signature.Length; i++)
                    if (head[i] != signature[i]) { match = false; break; }
                if (match) return true;
            }

            return read >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
                              && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50;
        }
        catch { return false; }
    }

    // notice: a line shown above the dump, e.g. why an image fell back to hex
    private void LoadHex(string? notice = null)
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        int toRead = (int)Math.Min(_fileSize, MaxHexBytes);
        _truncated = _fileSize > MaxHexBytes;

        var bytes = new byte[toRead];
        int read = stream.ReadAtLeast(bytes, toRead, throwOnEndOfStream: false);

        _encodingName = "binary";
        ResetMatches();
        avalonCodeViewer.Text = notice + FormatHex(bytes, read);
        avalonCodeViewer.FontSize = Math.Max(13, FontSize);
        avalonCodeViewer.Visibility = Visibility.Visible;
        docViewer.Visibility = Visibility.Collapsed;
        AvalonEditDraculaTheme.ApplyHex(avalonCodeViewer);
        ShowSurface(codeHost);
        UpdateStatus();
    }

    private static string FormatHex(byte[] data, int length)
    {
        var text = new StringBuilder(length / 16 * 78 + 64);
        var ascii = new char[16];

        for (int offset = 0; offset < length; offset += 16)
        {
            text.Append(offset.ToString("X8")).Append("  ");
            int count = Math.Min(16, length - offset);

            for (int i = 0; i < 16; i++)
            {
                if (i < count)
                {
                    byte b = data[offset + i];
                    text.Append(b.ToString("X2")).Append(' ');
                    ascii[i] = b >= 0x20 && b < 0x7F ? (char)b : '.';
                }
                else
                {
                    text.Append("   ");
                    ascii[i] = ' ';
                }
                if (i == 7) text.Append(' ');
            }
            text.Append(' ').Append(ascii, 0, 16).Append("\r\n");
        }
        return text.ToString();
    }

    // =========================================================================
    // A photo is decoded no larger than the screen.
    //
    // Decoded pixels take 4 bytes each: a 50-megapixel photo held 200 MB while
    // being shown "fit to window" on a screen that can display a fraction of
    // it. It is now decoded to the screen size, and only when the user zooms
    // in is it reloaded once at full resolution, so detail at 100 %+ is kept.
    // =========================================================================
    private bool _imageReduced;

    private void LoadImage(bool fullResolution = false)
    {
        try
        {
            var bitmap = new BitmapImage();
            int width, height;

            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // Header only, no pixels: the original size decides the decode size
                var header = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                width = header.PixelWidth;
                height = header.PixelHeight;
                stream.Position = 0;

                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                int limit = (int)(Math.Max(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight) * dpi);

                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;

                // One side only: the other follows and the aspect ratio is kept
                if (!fullResolution && Math.Max(width, height) > limit)
                {
                    if (width >= height) bitmap.DecodePixelWidth = limit;
                    else bitmap.DecodePixelHeight = limit;
                }

                bitmap.EndInit();
            }

            bitmap.Freeze();
            _imageReduced = bitmap.PixelWidth < width;

            imgContent.Source = bitmap;
            _encodingName = $"{width} x {height}";
            _truncated = false;
            _imageZoom = 0;
            UpdateImageScale();
            ShowSurface(imageScroll);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            _mode = ViewMode.Hex;
            LoadHex($"Cannot decode the image: {ex.Message}\r\n\r\n");
        }
    }

    private void UpdateImageScale()
    {
        if (imgContent.Source == null) return;

        if (_imageZoom == 0)
        {
            double availableWidth = imageScroll.ViewportWidth > 0 ? imageScroll.ViewportWidth : imageScroll.ActualWidth;
            double availableHeight = imageScroll.ViewportHeight > 0 ? imageScroll.ViewportHeight : imageScroll.ActualHeight;

            if (availableWidth > 0 && availableHeight > 0)
            {
                double scale = Math.Min(availableWidth / imgContent.Source.Width, availableHeight / imgContent.Source.Height);
                if (scale > 1.0) scale = 1.0;
                imgContent.Width = imgContent.Source.Width * scale;
                imgContent.Height = imgContent.Source.Height * scale;
            }
            else
            {
                imgContent.Width = double.NaN;
                imgContent.Height = double.NaN;
            }
        }
        else
        {
            imgContent.Width = imgContent.Source.Width * _imageZoom;
            imgContent.Height = imgContent.Source.Height * _imageZoom;
        }
    }

    private void ZoomImage(bool zoomIn)
    {
        if (imgContent.Source == null) return;

        // The first zoom swaps the screen-sized copy for the full image
        if (_imageReduced)
        {
            LoadImage(fullResolution: true);
            imageScroll.UpdateLayout();
        }

        double viewWidth = imageScroll.ViewportWidth > 0 ? imageScroll.ViewportWidth : imageScroll.ActualWidth;
        double viewHeight = imageScroll.ViewportHeight > 0 ? imageScroll.ViewportHeight : imageScroll.ActualHeight;
        Point viewportCenter = new(viewWidth / 2, viewHeight / 2);

        Point centerOnImage = new(0, 0);
        try { centerOnImage = imageScroll.TranslatePoint(viewportCenter, imgContent); } catch { }

        double oldZoom = _imageZoom;
        if (oldZoom == 0)
        {
            double scale = Math.Min(viewWidth / imgContent.Source.Width, viewHeight / imgContent.Source.Height);
            oldZoom = Math.Min(1.0, scale);
            _imageZoom = oldZoom;
        }

        _imageZoom = zoomIn ? _imageZoom * 1.15 : _imageZoom / 1.15;
        if (_imageZoom < 0.05) _imageZoom = 0.05;
        if (_imageZoom > 40.0) _imageZoom = 40.0;

        double ratio = _imageZoom / oldZoom;
        UpdateImageScale();
        imageScroll.UpdateLayout();

        try
        {
            Point newCenterOnImage = new(centerOnImage.X * ratio, centerOnImage.Y * ratio);
            Point newCenterInViewport = imgContent.TranslatePoint(newCenterOnImage, imageScroll);
            imageScroll.ScrollToHorizontalOffset(imageScroll.HorizontalOffset + (newCenterInViewport.X - viewportCenter.X));
            imageScroll.ScrollToVerticalOffset(imageScroll.VerticalOffset + (newCenterInViewport.Y - viewportCenter.Y));
        }
        catch { }

        UpdateStatus();
    }

    private void ShowSurface(FrameworkElement surface)
    {
        codeHost.Visibility = ReferenceEquals(surface, codeHost) ? Visibility.Visible : Visibility.Collapsed;
        imageScroll.Visibility = ReferenceEquals(surface, imageScroll) ? Visibility.Visible : Visibility.Collapsed;
        FocusActiveSurface();
    }

    private void FocusActiveSurface()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            FrameworkElement target = codeHost.Visibility == Visibility.Visible
                ? (avalonCodeViewer.Visibility == Visibility.Visible ? avalonCodeViewer : docViewer)
                : imageScroll;
            target.Focus();
            Keyboard.Focus(target);
        }, DispatcherPriority.Input);
    }

    // Plain messages (file not found, read errors) in the same editor as text
    private void ShowText(string content)
    {
        ResetMatches();
        avalonCodeViewer.Text = content;
        avalonCodeViewer.Visibility = Visibility.Visible;
        docViewer.Visibility = Visibility.Collapsed;
        AvalonEditDraculaTheme.Apply(avalonCodeViewer, _path, showLineNumbers: false);
        ShowSurface(codeHost);
    }

    private void UpdateStatus()
    {
        string size = Helpers.FormatSize(_fileSize);
        string cut = _truncated
            ? _mode == ViewMode.Hex
                ? $"  •  showing the first {Helpers.FormatSize(MaxHexBytes)}"
                : $"  •  showing the first {Helpers.FormatSize(MaxTextBytes)}"
            : "";

        string wrapState = _isWrapped && _mode != ViewMode.Image && avalonCodeViewer.Visibility == Visibility.Visible
            ? "  •  Wrap: On"
            : "";
        string zoomState = "";
        if (_mode == ViewMode.Image && imgContent.Source != null)
            zoomState = _imageZoom == 0 ? "  •  Fit to window" : $"  •  {Math.Round(_imageZoom * 100)}%";

        txtMode.Text = (_mode == ViewMode.Markdown && _isCodeFile) ? "Code" : _mode.ToString();

        txtEncoding.Text = _forcedEncoding == null
            ? (string.IsNullOrEmpty(_encodingName) ? "Auto" : _encodingName)
            : EncodingChoices.Names[_encodingIndex];

        txtStatusLeft.Text = $"{_displayName}  •  {size}";

        string extra = zoomState + cut + wrapState;
        if (_gotoBuffer.Length > 0) extra += $"  →  {_gotoBuffer}";
        txtStatusRight.Text = extra;

        Visibility searchVisibility = _mode == ViewMode.Image ? Visibility.Collapsed : Visibility.Visible;
        txtFind.Visibility = searchVisibility;
        btnPrev.Visibility = searchVisibility;
        btnNext.Visibility = searchVisibility;
        txtMatches.Visibility = searchVisibility;
    }

    private static int PreambleLength(Encoding encoding, byte[] data, int length)
    {
        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0 || length < preamble.Length) return 0;
        for (int i = 0; i < preamble.Length; i++)
            if (data[i] != preamble[i]) return 0;
        return preamble.Length;
    }

    private static string EncodingName(Encoding encoding) => encoding.CodePage switch
    {
        65001 => "UTF-8",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        _ => encoding.WebName
    };

    // ===== Search =====
    private readonly List<int> _textMatches = new();
    private readonly List<TextRange> _documentMatches = new();
    private string _matchQuery = "";
    private int _matchIndex = -1;
    private TextRange? _highlighted;

    private void ToggleWrap()
    {
        _isWrapped = !_isWrapped;
        avalonCodeViewer.WordWrap = _isWrapped;
        UpdateStatus();
    }

    private void TxtFind_TextChanged(object sender, TextChangedEventArgs e) =>
        ExecuteSearch(txtFind.Text, 1, isNewSearch: true);

    private void TxtFind_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            int direction = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
            ExecuteSearch(txtFind.Text, direction, isNewSearch: false);
        }
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e) =>
        ExecuteSearch(txtFind.Text, -1, isNewSearch: false);

    private void BtnNext_Click(object sender, RoutedEventArgs e) =>
        ExecuteSearch(txtFind.Text, 1, isNewSearch: false);

    private void ExecuteSearch(string needle, int direction, bool isNewSearch)
    {
        if (string.IsNullOrEmpty(needle))
        {
            ResetMatches();
            UpdateSearchCounter();
            return;
        }

        if (isNewSearch || _matchQuery != needle)
        {
            RebuildMatches(needle);
            if (MatchCount > 0)
            {
                _matchIndex = 0;
                ShowCurrentMatch();
            }
            UpdateSearchCounter();
            return;
        }

        int count = MatchCount;
        if (count == 0) return;

        _matchIndex = ((_matchIndex + direction) % count + count) % count;
        ShowCurrentMatch();
        UpdateSearchCounter();
    }

    private int MatchCount =>
        _mode == ViewMode.Markdown && !_isCodeFile
            ? _documentMatches.Count
            : _textMatches.Count;

    private void UpdateSearchCounter()
    {
        int count = MatchCount;
        txtMatches.Text = count == 0
            ? (string.IsNullOrEmpty(_matchQuery) ? "" : "0 / 0")
            : $"{_matchIndex + 1} / {count}";
    }

    private void ResetMatches()
    {
        ClearHighlight();
        _textMatches.Clear();
        _documentMatches.Clear();
        _matchQuery = "";
        _matchIndex = -1;
    }

    private void RebuildMatches(string needle)
    {
        ResetMatches();
        _matchQuery = needle;

        if (_mode == ViewMode.Markdown && !_isCodeFile)
        {
            CollectDocumentMatches(needle);
            return;
        }

        string haystack = avalonCodeViewer.Text;
        int from = 0;
        while (from <= haystack.Length - needle.Length)
        {
            int index = haystack.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0) break;
            _textMatches.Add(index);
            from = index + 1;
        }
    }

    private void CollectDocumentMatches(string needle)
    {
        if (docViewer.Document == null) return;
        var position = docViewer.Document.ContentStart;

        while (position != null)
        {
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                string run = position.GetTextInRun(LogicalDirection.Forward);
                int from = 0;
                while (from <= run.Length - needle.Length)
                {
                    int index = run.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;

                    var start = position.GetPositionAtOffset(index);
                    var end = start?.GetPositionAtOffset(needle.Length);
                    if (start != null && end != null)
                        _documentMatches.Add(new TextRange(start, end));
                    from = index + 1;
                }
            }
            position = position.GetNextContextPosition(LogicalDirection.Forward);
        }
    }

    private void ShowCurrentMatch()
    {
        if (_mode == ViewMode.Markdown && !_isCodeFile)
        {
            ClearHighlight();
            var range = _documentMatches[_matchIndex];
            range.ApplyPropertyValue(TextElement.BackgroundProperty, DraculaPalette.CurrentLineBrush);
            range.ApplyPropertyValue(TextElement.ForegroundProperty, DraculaPalette.ForegroundBrush);
            _highlighted = range;
            (range.Start.Parent as FrameworkContentElement)?.BringIntoView();
            return;
        }

        int offset = _textMatches[_matchIndex];
        avalonCodeViewer.Select(offset, _matchQuery.Length);
        avalonCodeViewer.ScrollToLine(avalonCodeViewer.Document.GetLineByOffset(offset).LineNumber);
    }

    private void ClearHighlight()
    {
        if (_highlighted == null) return;
        try
        {
            _highlighted.ApplyPropertyValue(TextElement.BackgroundProperty, null);
            _highlighted.ApplyPropertyValue(TextElement.ForegroundProperty, null);
        }
        catch { }
        _highlighted = null;
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        e.Handled = true;
        if (_mode == ViewMode.Image)
        {
            ZoomImage(e.Delta > 0);
        }
        else
        {
            SetTextFontSize(e.Delta > 0
                ? Math.Min(72, FontSize + 2)
                : Math.Max(8, FontSize - 2));
        }
    }

    private void SetTextFontSize(double fontSize)
    {
        FontSize = fontSize;
        avalonCodeViewer.FontSize = Math.Max(13, fontSize);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // Go to line by typing digits
        if (!txtFind.IsFocused && !ctrl)
        {
            if ((e.Key >= Key.D0 && e.Key <= Key.D9) || (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9))
            {
                e.Handled = true;
                int digit = e.Key >= Key.NumPad0 ? e.Key - Key.NumPad0 : e.Key - Key.D0;
                _gotoBuffer.Append(digit);
                _gotoTimer?.Stop();
                _gotoTimer?.Start();
                UpdateStatus();
                return;
            }

            if (e.Key == Key.Enter && _gotoBuffer.Length > 0)
            {
                e.Handled = true;
                _gotoTimer?.Stop();
                ExecuteGotoLine();
                return;
            }

            if (e.Key == Key.Back && _gotoBuffer.Length > 0)
            {
                e.Handled = true;
                _gotoBuffer.Length--;
                _gotoTimer?.Stop();
                if (_gotoBuffer.Length > 0) _gotoTimer?.Start();
                UpdateStatus();
                return;
            }

            if (_gotoBuffer.Length > 0 && e.Key is not (Key.LeftShift or Key.RightShift))
            {
                _gotoBuffer.Clear();
                _gotoTimer?.Stop();
                UpdateStatus();
            }
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_gotoBuffer.Length > 0)
            {
                _gotoBuffer.Clear();
                _gotoTimer?.Stop();
                UpdateStatus();
                return;
            }
            if (!string.IsNullOrEmpty(txtFind.Text) || txtFind.IsFocused)
            {
                txtFind.Text = "";
                FocusActiveSurface();
            }
            else Close();
            return;
        }

        if (ctrl && e.Key == Key.F)
        {
            e.Handled = true;
            txtFind.Focus();
            txtFind.SelectAll();
            return;
        }

        if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            e.Handled = true;
            if (_mode == ViewMode.Image)
            {
                _imageZoom = 0;
                UpdateImageScale();
                UpdateStatus();
            }
            else if (Application.Current.TryFindResource("AppFontSize") is double defaultSize)
                SetTextFontSize(defaultSize);
            return;
        }

        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            e.Handled = true;
            if (_mode == ViewMode.Image) ZoomImage(true);
            else SetTextFontSize(Math.Min(72, FontSize + 2));
            return;
        }

        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            e.Handled = true;
            if (_mode == ViewMode.Image) ZoomImage(false);
            else SetTextFontSize(Math.Max(8, FontSize - 2));
            return;
        }

        if (ctrl && (Keyboard.Modifiers & ModifierKeys.Shift) != 0 && e.Key == Key.V)
        {
            e.Handled = true;
            if (!txtFind.IsFocused)
            {
                var modes = GetAvailableModes();
                if (modes.Contains(ViewMode.Markdown) && modes.Contains(ViewMode.Text))
                    _mode = _mode == ViewMode.Markdown ? ViewMode.Text : ViewMode.Markdown;
                else
                {
                    CycleViewMode();
                    return;
                }
                LoadFile();
            }
            return;
        }

        if (e.Key == Key.F3)
        {
            e.Handled = true;
            int dir = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
            ExecuteSearch(txtFind.Text, dir, isNewSearch: false);
            return;
        }

        // Plain 1 and 2 are digits of a line number, so the modes live on Alt
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.System)
        {
            if (e.SystemKey is Key.D1 or Key.NumPad1) { e.Handled = true; _mode = _startMode; LoadFile(); }
            else if (e.SystemKey is Key.D2 or Key.NumPad2) { e.Handled = true; _mode = ViewMode.Hex; LoadFile(); }
            return;
        }

        if (!ctrl && !txtFind.IsFocused && e.Key == Key.W)
        {
            e.Handled = true;
            ToggleWrap();
        }
    }

    private void TxtMode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CycleViewMode();
    }

    private void TxtEncoding_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _encodingIndex = (_encodingIndex + 1) % EncodingChoices.Names.Length;
        _forcedEncoding = EncodingChoices.Get(_encodingIndex);
        LoadFile();
    }

    private ViewMode[] GetAvailableModes()
    {
        string ext = Path.GetExtension(_path);
        if (ImageExtensions.Contains(ext)) return [ViewMode.Image, ViewMode.Hex];
        if (FileTypes.Markdown.Contains(ext)) return [ViewMode.Text, ViewMode.Markdown, ViewMode.Hex];
        if (_isCodeFile) return [ViewMode.Text, ViewMode.Markdown, ViewMode.Hex];
        return [ViewMode.Text, ViewMode.Hex];
    }

    private void CycleViewMode()
    {
        var modes = GetAvailableModes();
        int index = Array.IndexOf(modes, _mode);
        if (index < 0) index = 0;
        _mode = modes[(index + 1) % modes.Length];
        LoadFile();
    }

    private void ExecuteGotoLine()
    {
        if (_gotoBuffer.Length == 0) return;

        if (!int.TryParse(_gotoBuffer.ToString(), out int line) || line < 1)
        {
            _gotoBuffer.Clear();
            UpdateStatus();
            return;
        }

        _gotoBuffer.Clear();
        UpdateStatus();

        // Rendered Markdown and images have no lines
        if (avalonCodeViewer.Visibility != Visibility.Visible || codeHost.Visibility != Visibility.Visible)
            return;

        line = Math.Min(line, avalonCodeViewer.Document.LineCount);
        avalonCodeViewer.ScrollToLine(line);
        avalonCodeViewer.CaretOffset = avalonCodeViewer.Document.GetLineByNumber(line).Offset;
        avalonCodeViewer.Focus();
    }
}
