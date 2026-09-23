using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace R2Cmd;

public class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "About R2cmd";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        // Apply dynamic background and text colors
        SetResourceReference(BackgroundProperty, "Brush.Background");
        SetResourceReference(ForegroundProperty, "Brush.TextPrimary");

        // Click anywhere outside the window closes it. Requires Show(), not
        // ShowDialog() — see the comments in CloseOnFocusLoss.
        CloseOnFocusLoss.Enable(this);

        // Escape handled explicitly instead of IsCancel on the OK button:
        // IsCancel assigns DialogResult, and doing that on a window that was not
        // shown as a dialog throws InvalidOperationException.
        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        var root = new StackPanel { Margin = new Thickness(24) };

        var title = new TextBlock
        {
            Text = "R2cmd",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");

        var version = new TextBlock
        {
            Text = "Version 0.72",
            Margin = new Thickness(0, 2, 0, 0),
            Opacity = 0.7
        };

        var desc = new TextBlock
        {
            Text = "A dual-pane file manager for Windows.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Opacity = 0.85
        };

        // Author + email (clickable mailto)
        var authorPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var authorLabel = new TextBlock { Text = "Author: ", Opacity = 0.7 };

        var link = new Hyperlink { NavigateUri = new Uri("mailto:axmsrn@gmail.com") };
        link.Inlines.Add("axmsrn@gmail.com");

        // Use dynamic accent brush instead of a hardcoded color for better visibility in light theme
        link.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent");

        link.RequestNavigate += (s, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
            }
            catch { }
            e.Handled = true;
        };
        var emailText = new TextBlock();
        emailText.Inlines.Add(link);

        authorPanel.Children.Add(authorLabel);
        authorPanel.Children.Add(emailText);

        var separator = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 20, 0, 0),
            Opacity = 0.15
        };
        separator.SetResourceReference(Border.BackgroundProperty, "Brush.Border");

        var copyright = new TextBlock
        {
            Text = $"© {DateTime.Now.Year} R2cmd. All rights reserved.",
            Margin = new Thickness(0, 12, 0, 0),
            Opacity = 0.5,
            FontSize = 12
        };

        var okButton = new Button
        {
            Content = "OK",
            Width = 88,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0),
            IsDefault = true
        };
        okButton.Click += (s, e) => Close();

        root.Children.Add(title);
        root.Children.Add(version);
        root.Children.Add(desc);
        root.Children.Add(authorPanel);
        root.Children.Add(separator);
        root.Children.Add(copyright);
        root.Children.Add(okButton);

        Content = root;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Apply title bar theme matching the current active theme
        Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme);
    }
}
