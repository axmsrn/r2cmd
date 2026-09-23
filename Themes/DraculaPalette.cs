using System.Windows.Media;

namespace R2Cmd;

/// <summary>
/// Canonical colors for the editor and viewer.
/// Dark = classic Dracula, Light = high-contrast colors for light theme.
/// </summary>
internal static class DraculaPalette
{
    // ===== Dark (Dracula) =====
    internal static readonly Color Background = Color.FromRgb(0x28, 0x2A, 0x36);
    internal static readonly Color CurrentLine = Color.FromRgb(0x44, 0x47, 0x5A);
    internal static readonly Color Foreground = Color.FromRgb(0xF8, 0xF8, 0xF2);
    internal static readonly Color Comment = Color.FromRgb(0x62, 0x72, 0xA4);
    internal static readonly Color Cyan = Color.FromRgb(0x8B, 0xE9, 0xFD);
    internal static readonly Color Green = Color.FromRgb(0x50, 0xFA, 0x7B);
    internal static readonly Color Orange = Color.FromRgb(0xFF, 0xB8, 0x6C);
    internal static readonly Color Pink = Color.FromRgb(0xFF, 0x79, 0xC6);
    internal static readonly Color Purple = Color.FromRgb(0xBD, 0x93, 0xF9);
    internal static readonly Color Red = Color.FromRgb(0xFF, 0x55, 0x55);
    internal static readonly Color Yellow = Color.FromRgb(0xF1, 0xFA, 0x8C);

    // ===== Light (high contrast) =====
    internal static readonly Color LightForeground = Color.FromRgb(0x1F, 0x1F, 0x1F);
    internal static readonly Color LightComment = Color.FromRgb(0x6A, 0x73, 0x7D);
    internal static readonly Color LightCyan = Color.FromRgb(0x05, 0x5C, 0x9A);
    internal static readonly Color LightGreen = Color.FromRgb(0x1A, 0x7F, 0x37);
    internal static readonly Color LightOrange = Color.FromRgb(0xB3, 0x5C, 0x00);
    internal static readonly Color LightPink = Color.FromRgb(0xC0, 0x1B, 0x5D);
    internal static readonly Color LightPurple = Color.FromRgb(0x6F, 0x42, 0xC1);
    internal static readonly Color LightRed = Color.FromRgb(0xC0, 0x1B, 0x1B);
    internal static readonly Color LightYellow = Color.FromRgb(0x9A, 0x67, 0x00);

    // ===== Dark brushes =====
    internal static readonly SolidColorBrush BackgroundBrush = CreateBrush(Background);
    internal static readonly SolidColorBrush CurrentLineBrush = CreateBrush(CurrentLine);
    internal static readonly SolidColorBrush ForegroundBrush = CreateBrush(Foreground);
    internal static readonly SolidColorBrush CommentBrush = CreateBrush(Comment);
    internal static readonly SolidColorBrush CyanBrush = CreateBrush(Cyan);
    internal static readonly SolidColorBrush GreenBrush = CreateBrush(Green);
    internal static readonly SolidColorBrush OrangeBrush = CreateBrush(Orange);
    internal static readonly SolidColorBrush PinkBrush = CreateBrush(Pink);
    internal static readonly SolidColorBrush PurpleBrush = CreateBrush(Purple);
    internal static readonly SolidColorBrush RedBrush = CreateBrush(Red);
    internal static readonly SolidColorBrush YellowBrush = CreateBrush(Yellow);

    // ===== Light brushes =====
    internal static readonly SolidColorBrush LightForegroundBrush = CreateBrush(LightForeground);
    internal static readonly SolidColorBrush LightCommentBrush = CreateBrush(LightComment);
    internal static readonly SolidColorBrush LightCyanBrush = CreateBrush(LightCyan);
    internal static readonly SolidColorBrush LightGreenBrush = CreateBrush(LightGreen);
    internal static readonly SolidColorBrush LightOrangeBrush = CreateBrush(LightOrange);
    internal static readonly SolidColorBrush LightPinkBrush = CreateBrush(LightPink);
    internal static readonly SolidColorBrush LightPurpleBrush = CreateBrush(LightPurple);
    internal static readonly SolidColorBrush LightRedBrush = CreateBrush(LightRed);
    internal static readonly SolidColorBrush LightYellowBrush = CreateBrush(LightYellow);

    // ===== Theme-aware helpers =====
    internal static Color GetForeground() => ThemeManager.IsDarkTheme ? Foreground : LightForeground;
    internal static Color GetComment() => ThemeManager.IsDarkTheme ? Comment : LightComment;
    internal static Color GetCyan() => ThemeManager.IsDarkTheme ? Cyan : LightCyan;
    internal static Color GetGreen() => ThemeManager.IsDarkTheme ? Green : LightGreen;
    internal static Color GetOrange() => ThemeManager.IsDarkTheme ? Orange : LightOrange;
    internal static Color GetPink() => ThemeManager.IsDarkTheme ? Pink : LightPink;
    internal static Color GetPurple() => ThemeManager.IsDarkTheme ? Purple : LightPurple;
    internal static Color GetRed() => ThemeManager.IsDarkTheme ? Red : LightRed;
    internal static Color GetYellow() => ThemeManager.IsDarkTheme ? Yellow : LightYellow;

    internal static SolidColorBrush GetForegroundBrush() =>
        ThemeManager.IsDarkTheme ? ForegroundBrush : LightForegroundBrush;

    internal static SolidColorBrush GetCommentBrush() =>
        ThemeManager.IsDarkTheme ? CommentBrush : LightCommentBrush;

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
