using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace R2Cmd;

// =============================================================================
// Pieces shared by the viewer (F3) and the built-in editor (F4). Both windows
// used to carry their own identical copies.
// =============================================================================

// The encodings the status-bar button cycles through
public static class EncodingChoices
{
    public static readonly string[] Names =
    {
        "Auto", "UTF-8", "UTF-8 BOM", "Windows-1251", "CP866", "UTF-16 LE", "UTF-16 BE"
    };

    // null for "Auto": the encoding is detected from the file
    public static Encoding? Get(int index)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return index switch
            {
                1 => new UTF8Encoding(false),
                2 => new UTF8Encoding(true),
                3 => Encoding.GetEncoding(1251),
                4 => Encoding.GetEncoding(866),
                5 => Encoding.Unicode,
                6 => Encoding.BigEndianUnicode,
                _ => null
            };
        }
        catch
        {
            return new UTF8Encoding(false);
        }
    }
}

// =============================================================================
// Smooth mouse-wheel scrolling for any element that hosts a ScrollViewer.
// Each wheel notch moves a target position; a 60 Hz timer eases the view
// towards it. Ctrl+wheel is left alone: both windows use it for font size.
// =============================================================================
public sealed class SmoothWheelScroller
{
    // About five standard lines per wheel notch
    private const double PixelsPerNotch = 120;

    // Share of the remaining distance covered per frame: larger responds
    // faster, the easing keeps it from jumping
    private const double Easing = 0.38;

    private readonly DispatcherTimer _timer;
    private ScrollViewer? _viewer;
    private double _target;

    public SmoothWheelScroller(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTick;
    }

    public void Attach(UIElement element) => element.PreviewMouseWheel += OnWheel;

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return;
        if (sender is not DependencyObject source) return;

        ScrollViewer? viewer = FindScrollViewer(source);
        if (viewer == null || viewer.ScrollableHeight <= 0) return;

        e.Handled = true;

        // A new target starts from where the view is now, not from a target
        // left over from another element or an animation that already ended
        if (!ReferenceEquals(_viewer, viewer) || !_timer.IsEnabled)
        {
            _viewer = viewer;
            _target = viewer.VerticalOffset;
        }

        _target = Math.Clamp(_target - e.Delta / 120.0 * PixelsPerNotch, 0, viewer.ScrollableHeight);

        if (!_timer.IsEnabled) _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_viewer == null)
        {
            _timer.Stop();
            return;
        }

        double current = _viewer.VerticalOffset;
        double remaining = _target - current;

        if (Math.Abs(remaining) < 0.5)
        {
            _viewer.ScrollToVerticalOffset(_target);
            _timer.Stop();
            return;
        }

        _viewer.ScrollToVerticalOffset(current + remaining * Easing);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root == null) return null;
        if (root is ScrollViewer viewer) return viewer;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }

        return null;
    }
}
