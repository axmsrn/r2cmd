using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace R2Cmd;

// =============================================================================
// Markdown → FlowDocument
//
// Badges written as ![…](…/badge/Label-Message-color) are drawn as local WPF
// chips (no network). Body text follows the app theme; headings, links and code
// use a fixed readable palette. Emoji are drawn with the Segoe UI Emoji font.
// Relative image paths resolve against the folder of the Markdown file.
// =============================================================================
public static class MarkdownRenderer
{
    private static Brush BrushOr(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static readonly Color FallbackPrimary = Color.FromRgb(0xD4, 0xD4, 0xD4);
    private static readonly Color FallbackSecondary = Color.FromRgb(0x9D, 0x9D, 0x9D);
    private static readonly Color FallbackSurface = Color.FromRgb(0x2D, 0x2D, 0x30);
    private static readonly Color FallbackSurfaceAlt = Color.FromRgb(0x33, 0x33, 0x37);
    private static readonly Color FallbackBorder = Color.FromRgb(0x45, 0x45, 0x45);
    private static readonly Color FallbackBackground = Color.FromRgb(0x25, 0x25, 0x26);

    // Created once and frozen. A new brush per heading, link, code span and
    // badge, and a new FontFamily per code span, added up to thousands of
    // objects for a long document.
    private static readonly SolidColorBrush MdHeading = Solid(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly SolidColorBrush MdLink = Solid(Color.FromRgb(0x6C, 0xB6, 0xFF));
    private static readonly SolidColorBrush MdCode = Solid(Color.FromRgb(0xCE, 0x91, 0x78));
    private static readonly SolidColorBrush MdQuote = Solid(Color.FromRgb(0x6A, 0x99, 0x55));
    private static readonly SolidColorBrush BadgeLabel = Solid(Color.FromRgb(0x55, 0x55, 0x55));

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");
    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji, Segoe UI Symbol, Segoe UI");
    private static readonly FontFamily BadgeFont = new("Segoe UI, Arial");

    private static readonly Regex ImgTagRegex = new(
        @"<img\s+[^>]*src\s*=\s*[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AltAttrRegex = new(
        @"alt\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EmojiRegex = new(
        @"(" +
        @"\ud83c[\ud000-\udfff]|" +
        @"\ud83d[\ud000-\udfff]|" +
        @"\ud83e[\ud000-\udfff]|" +
        @"[\u2600-\u27bf]|" +
        @"[\u2300-\u23ff]|" +
        @"[\u2b50]|" +
        @"[\u2100-\u214f]|" +
        @"[\u25a0-\u25ff]|" +
        @"[\u2b00-\u2bff]" +
        @")\ufe0f?",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, Color> BadgeNamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = Color.FromRgb(0xE0, 0x5D, 0x44),
        ["blue"] = Color.FromRgb(0x00, 0x7E, 0xC6),
        ["green"] = Color.FromRgb(0x97, 0xCA, 0x00),
        ["brightgreen"] = Color.FromRgb(0x44, 0xCC, 0x11),
        ["yellow"] = Color.FromRgb(0xDF, 0xB3, 0x17),
        ["yellowgreen"] = Color.FromRgb(0xA4, 0xA6, 0x1D),
        ["orange"] = Color.FromRgb(0xFE, 0x7D, 0x37),
        ["lightgrey"] = Color.FromRgb(0x9F, 0x9F, 0x9F),
        ["lightgray"] = Color.FromRgb(0x9F, 0x9F, 0x9F),
        ["grey"] = Color.FromRgb(0x55, 0x55, 0x55),
        ["gray"] = Color.FromRgb(0x55, 0x55, 0x55),
        ["blueviolet"] = Color.FromRgb(0x8B, 0x00, 0xCE),
        ["success"] = Color.FromRgb(0x44, 0xCC, 0x11),
        ["important"] = Color.FromRgb(0xFE, 0x7D, 0x37),
        ["critical"] = Color.FromRgb(0xE0, 0x5D, 0x44),
        ["informational"] = Color.FromRgb(0x00, 0x7E, 0xC6),
        ["inactive"] = Color.FromRgb(0x9F, 0x9F, 0x9F),
    };

    public static FlowDocument Render(string markdown, double baseFontSize, string? basePath = null)
    {
        var document = new FlowDocument
        {
            FontSize = baseFontSize,
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI, Segoe UI Symbol"),
            PagePadding = new Thickness(24, 16, 24, 24),
            LineHeight = baseFontSize * 1.5,
            Foreground = BrushOr("Brush.TextPrimary", FallbackPrimary),
            Background = BrushOr("Brush.Background", FallbackBackground)
        };

        TextOptions.SetTextFormattingMode(document, TextFormattingMode.Ideal);

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<Block>();
        var paragraph = new List<string>();
        var lists = new List<(int Indent, List List)>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var block = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, baseFontSize * 0.7),
                Foreground = BrushOr("Brush.TextPrimary", FallbackPrimary)
            };
            AppendInline(block.Inlines, string.Join(" ", paragraph), baseFontSize, basePath);
            blocks.Add(block);
            paragraph.Clear();
        }

        void CloseLists()
        {
            FlushParagraph();
            lists.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                CloseLists();
                string fence = trimmed.Substring(0, 3);
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fence))
                {
                    code.AppendLine(lines[i]);
                    i++;
                }
                blocks.Add(CodeBlock(code.ToString().TrimEnd('\n', '\r'), baseFontSize));
                continue;
            }

            if (trimmed.Length == 0)
            {
                CloseLists();
                continue;
            }

            if (trimmed.StartsWith("<") && trimmed.Contains(">"))
            {
                CloseLists();
                var imgMatches = ImgTagRegex.Matches(trimmed);
                if (imgMatches.Count > 0)
                {
                    var p = new Paragraph
                    {
                        Margin = new Thickness(0, baseFontSize * 0.7, 0, baseFontSize * 0.7),
                        TextAlignment = TextAlignment.Center
                    };
                    foreach (Match imgMatch in imgMatches)
                    {
                        string src = imgMatch.Groups[1].Value;
                        var altMatch = AltAttrRegex.Match(imgMatch.Value);
                        string alt = altMatch.Success ? altMatch.Groups[1].Value : "";
                        p.Inlines.Add(BuildImage(alt, src, baseFontSize, basePath));
                        p.Inlines.Add(new Run(" "));
                    }
                    blocks.Add(p);
                }
                continue;
            }

            if (IsHorizontalRule(trimmed))
            {
                CloseLists();
                blocks.Add(HorizontalRule(baseFontSize));
                continue;
            }

            int hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
            if (hashes is > 0 and <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ')
            {
                CloseLists();
                blocks.Add(Heading(trimmed.Substring(hashes + 1).Trim(), hashes, baseFontSize, basePath));
                continue;
            }

            if (trimmed.StartsWith("|") && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                CloseLists();
                var rows = new List<string> { trimmed };
                i += 2;
                while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                {
                    rows.Add(lines[i].Trim());
                    i++;
                }
                i--;
                blocks.Add(BuildTable(rows, baseFontSize, basePath));
                continue;
            }

            if (trimmed.StartsWith("> "))
            {
                CloseLists();
                var quote = new Paragraph
                {
                    Margin = new Thickness(0, 0, 0, baseFontSize * 0.7),
                    Padding = new Thickness(12, 4, 0, 4),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = MdQuote,
                    Foreground = BrushOr("Brush.TextSecondary", FallbackSecondary)
                };
                AppendInline(quote.Inlines, trimmed.Substring(2).Trim(), baseFontSize, basePath);
                blocks.Add(quote);
                continue;
            }

            int indent = line.Length - trimmed.Length;
            string? itemText = null;
            bool ordered = false;

            if (trimmed.Length > 1 && (trimmed[0] is '-' or '*' or '+') && trimmed[1] == ' ')
            {
                itemText = trimmed.Substring(2);
            }
            else
            {
                int digits = 0;
                while (digits < trimmed.Length && char.IsDigit(trimmed[digits])) digits++;
                if (digits > 0 && digits + 1 < trimmed.Length &&
                    (trimmed[digits] == '.' || trimmed[digits] == ')') && trimmed[digits + 1] == ' ')
                {
                    itemText = trimmed.Substring(digits + 2);
                    ordered = true;
                }
            }

            if (itemText != null)
            {
                FlushParagraph();
                AddListItem(blocks, lists, indent, ordered, itemText, baseFontSize, basePath);
                continue;
            }

            if (indent >= 4 && lists.Count == 0)
            {
                FlushParagraph();
                blocks.Add(CodeBlock(line.Substring(4), baseFontSize));
                continue;
            }

            if (lists.Count > 0 && indent > 0)
            {
                var lastList = lists[^1].List;
                if (lastList.ListItems.LastListItem?.Blocks.LastBlock is Paragraph last)
                {
                    last.Inlines.Add(new Run(" "));
                    AppendInline(last.Inlines, trimmed, baseFontSize, basePath);
                    continue;
                }
            }

            if (lists.Count > 0) CloseLists();
            paragraph.Add(trimmed);
        }

        FlushParagraph();
        foreach (var block in blocks)
            document.Blocks.Add(block);
        return document;
    }

    private static void AddListItem(List<Block> blocks, List<(int Indent, List List)> lists,
        int indent, bool ordered, string text, double baseFontSize, string? basePath)
    {
        while (lists.Count > 0 && lists[^1].Indent > indent)
            lists.RemoveAt(lists.Count - 1);

        if (lists.Count == 0 || lists[^1].Indent < indent)
        {
            var list = new List
            {
                MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(baseFontSize * 1.4, 0, 0, baseFontSize * 0.5),
                Padding = new Thickness(0),
                Foreground = BrushOr("Brush.TextPrimary", FallbackPrimary)
            };
            if (lists.Count == 0) blocks.Add(list);
            else lists[^1].List.ListItems.LastListItem?.Blocks.Add(list);
            lists.Add((indent, list));
        }

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 2),
            Foreground = BrushOr("Brush.TextPrimary", FallbackPrimary)
        };
        AppendInline(paragraph.Inlines, text, baseFontSize, basePath);
        lists[^1].List.ListItems.Add(new ListItem(paragraph));
    }

    private static Block Heading(string text, int level, double baseFontSize, string? basePath)
    {
        double[] scale = { 1.9, 1.6, 1.35, 1.2, 1.08, 1.0 };
        var heading = new Paragraph
        {
            FontSize = baseFontSize * scale[level - 1],
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, baseFontSize * 0.9, 0, baseFontSize * 0.4),
            Foreground = level <= 2 ? MdHeading : BrushOr("Brush.TextPrimary", FallbackPrimary)
        };
        AppendInline(heading.Inlines, text, baseFontSize, basePath);
        if (level <= 2)
        {
            heading.BorderThickness = new Thickness(0, 0, 0, 1);
            heading.Padding = new Thickness(0, 0, 0, 4);
            heading.BorderBrush = BrushOr("Brush.Border", FallbackBorder);
        }
        return heading;
    }

    private static Block CodeBlock(string code, double baseFontSize)
    {
        var codeBox = new TextBox
        {
            Text = code,
            FontFamily = MonoFont,
            FontSize = baseFontSize * 0.92,
            Padding = new Thickness(10, 8, 26, 8),
            BorderThickness = new Thickness(0),
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            AcceptsReturn = true,
            Foreground = MdCode,
            CaretBrush = BrushOr("Brush.TextPrimary", FallbackPrimary)
        };

        var copyBtn = new Button
        {
            Content = "Copy",
            FontSize = 10,
            Padding = new Thickness(6, 0, 6, 0),  // no extra gap under the text
            Margin = new Thickness(0, 8, 8, 0),   // same gap on top and right
            MinWidth = 0,
            MinHeight = 0,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            Opacity = 0,
            Focusable = false,
            Background = BrushOr("Brush.SurfaceAlt", FallbackSurfaceAlt),
            Foreground = BrushOr("Brush.TextPrimary", FallbackPrimary),
            BorderBrush = BrushOr("Brush.Border", FallbackBorder)
        };

        copyBtn.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(code);
                copyBtn.Content = "Copied";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    copyBtn.Content = "Copy";
                    if (!copyBtn.IsMouseOver) copyBtn.Opacity = 0;
                };
                timer.Start();
            }
            catch { }
        };

        var grid = new Grid();
        grid.Children.Add(codeBox);
        grid.Children.Add(copyBtn);
        grid.MouseEnter += (_, _) => copyBtn.Opacity = 1;
        grid.MouseLeave += (_, _) =>
        {
            if (copyBtn.Content as string != "Copied") copyBtn.Opacity = 0;
        };

        var border = new Border
        {
            Child = grid,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, baseFontSize * 0.7),
            SnapsToDevicePixels = true,
            Background = BrushOr("Brush.Surface", FallbackSurface),
            BorderBrush = BrushOr("Brush.Border", FallbackBorder)
        };
        return new BlockUIContainer(border);
    }

    private static Block HorizontalRule(double baseFontSize) =>
        new Paragraph
        {
            Margin = new Thickness(0, baseFontSize * 0.4, 0, baseFontSize * 0.8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = BrushOr("Brush.Border", FallbackBorder)
        };

    private static bool IsHorizontalRule(string line)
    {
        if (line.Length < 3) return false;
        char c = line[0];
        if (c is not ('-' or '*' or '_')) return false;
        foreach (char ch in line)
            if (ch != c && ch != ' ') return false;
        return true;
    }

    private static bool IsTableSeparator(string line)
    {
        string trimmed = line.Trim();
        if (!trimmed.StartsWith("|")) return false;
        foreach (char c in trimmed)
            if (c is not ('|' or '-' or ':' or ' ')) return false;
        return trimmed.Contains('-');
    }

    private static Block BuildTable(List<string> rows, double baseFontSize, string? basePath)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, baseFontSize * 0.7),
            Foreground = BrushOr("Brush.TextPrimary", FallbackPrimary)
        };
        var cellsPerRow = new List<string[]>();
        int columns = 0;
        foreach (string row in rows)
        {
            var cells = row.Trim().Trim('|').Split('|');
            cellsPerRow.Add(cells);
            columns = Math.Max(columns, cells.Length);
        }
        for (int i = 0; i < columns; i++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (int r = 0; r < cellsPerRow.Count; r++)
        {
            var row = new TableRow();
            bool header = r == 0;
            for (int c = 0; c < columns; c++)
            {
                string text = c < cellsPerRow[r].Length ? cellsPerRow[r][c].Trim() : "";
                var paragraph = new Paragraph
                {
                    Margin = new Thickness(0),
                    Foreground = BrushOr("Brush.TextPrimary", FallbackPrimary)
                };
                AppendInline(paragraph.Inlines, text, baseFontSize, basePath);
                var cell = new TableCell(paragraph)
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                    BorderBrush = BrushOr("Brush.Border", FallbackBorder)
                };
                if (header) cell.Background = BrushOr("Brush.Surface", FallbackSurface);
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        return table;
    }

    private static void AppendInline(InlineCollection target, string text, double baseFontSize, string? basePath)
    {
        int i = 0;
        var plain = new StringBuilder();
        var primary = BrushOr("Brush.TextPrimary", FallbackPrimary);

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            string raw = plain.ToString();
            var matches = EmojiRegex.Matches(raw);
            if (matches.Count == 0)
            {
                target.Add(new Run(raw) { Foreground = primary });
            }
            else
            {
                int lastIdx = 0;
                foreach (Match m in matches)
                {
                    if (m.Index > lastIdx)
                        target.Add(new Run(raw.Substring(lastIdx, m.Index - lastIdx)) { Foreground = primary });

                    string emoji = m.Value;
                    if (!emoji.EndsWith("\ufe0f", StringComparison.Ordinal))
                        emoji += "\ufe0f";
                    target.Add(BuildEmojiImage(emoji, baseFontSize));
                    lastIdx = m.Index + m.Length;
                }
                if (lastIdx < raw.Length)
                    target.Add(new Run(raw.Substring(lastIdx)) { Foreground = primary });
            }
            plain.Clear();
        }

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < text.Length)
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '[' && i + 2 < text.Length && text[i + 1] == '!' && text[i + 2] == '[')
            {
                if (TryParseLinkedImage(text, i, out string alt, out string imgUrl, out string href, out int next))
                {
                    FlushPlain();
                    target.Add(BuildImageLink(alt, imgUrl, href, baseFontSize, basePath));
                    i = next;
                    continue;
                }
            }

            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                if (TryParseImage(text, i, out string alt, out string imgUrl, out int next))
                {
                    FlushPlain();
                    target.Add(BuildImage(alt, imgUrl, baseFontSize, basePath));
                    i = next;
                    continue;
                }
            }

            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    FlushPlain();
                    target.Add(new Run(text.Substring(i + 1, end - i - 1))
                    {
                        FontFamily = MonoFont,
                        FontSize = baseFontSize * 0.92,
                        Foreground = MdCode,
                        Background = BrushOr("Brush.Surface", FallbackSurface)
                    });
                    i = end + 1;
                    continue;
                }
            }

            if (c == '[')
            {
                int close = text.IndexOf(']', i + 1);
                if (close > i && close + 1 < text.Length && text[close + 1] == '(')
                {
                    int urlEnd = text.IndexOf(')', close + 2);
                    if (urlEnd > close)
                    {
                        FlushPlain();
                        string label = text.Substring(i + 1, close - i - 1);
                        string url = text.Substring(close + 2, urlEnd - close - 2).Trim();
                        target.Add(BuildLink(label, url));
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }

            if (Matched(text, i, "**", out int boldEnd))
            {
                FlushPlain();
                var bold = new Bold();
                AppendInline(bold.Inlines, text.Substring(i + 2, boldEnd - i - 2), baseFontSize, basePath);
                target.Add(bold);
                i = boldEnd + 2;
                continue;
            }

            if (Matched(text, i, "~~", out int strikeEnd))
            {
                FlushPlain();
                var span = new Span
                {
                    TextDecorations = TextDecorations.Strikethrough,
                    Foreground = BrushOr("Brush.TextSecondary", FallbackSecondary)
                };
                AppendInline(span.Inlines, text.Substring(i + 2, strikeEnd - i - 2), baseFontSize, basePath);
                target.Add(span);
                i = strikeEnd + 2;
                continue;
            }

            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] != c)
            {
                int end = text.IndexOf(c, i + 1);
                if (end > i + 1)
                {
                    FlushPlain();
                    var italic = new Italic();
                    AppendInline(italic.Inlines, text.Substring(i + 1, end - i - 1), baseFontSize, basePath);
                    target.Add(italic);
                    i = end + 1;
                    continue;
                }
            }

            plain.Append(c);
            i++;
        }

        FlushPlain();
    }

    private static bool Matched(string text, int index, string marker, out int end)
    {
        end = -1;
        if (index + marker.Length >= text.Length) return false;
        if (string.CompareOrdinal(text, index, marker, 0, marker.Length) != 0) return false;
        end = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
        return end > index + marker.Length;
    }

    private static Inline BuildLink(string label, string url)
    {
        var link = new Hyperlink(new Run(string.IsNullOrEmpty(label) ? url : label))
        {
            ToolTip = url,
            Foreground = MdLink
        };
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            link.NavigateUri = uri;
        return link;
    }

    private static bool TryParseImage(string text, int index, out string alt, out string url, out int next)
    {
        alt = "";
        url = "";
        next = index;
        if (index + 1 >= text.Length || text[index] != '!' || text[index + 1] != '[')
            return false;
        int close = text.IndexOf(']', index + 2);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
            return false;
        int urlEnd = text.IndexOf(')', close + 2);
        if (urlEnd < 0) return false;
        alt = text.Substring(index + 2, close - (index + 2));
        url = text.Substring(close + 2, urlEnd - (close + 2)).Trim();
        next = urlEnd + 1;
        return url.Length > 0;
    }

    private static bool TryParseLinkedImage(string text, int index,
        out string alt, out string imgUrl, out string href, out int next)
    {
        alt = "";
        imgUrl = "";
        href = "";
        next = index;
        if (index + 2 >= text.Length || text[index] != '[' || text[index + 1] != '!')
            return false;
        if (!TryParseImage(text, index + 1, out alt, out imgUrl, out int afterImg))
            return false;
        if (afterImg + 1 >= text.Length || text[afterImg] != ']' || text[afterImg + 1] != '(')
            return false;
        int hrefEnd = text.IndexOf(')', afterImg + 2);
        if (hrefEnd < 0) return false;
        href = text.Substring(afterImg + 2, hrefEnd - (afterImg + 2)).Trim();
        next = hrefEnd + 1;
        return imgUrl.Length > 0;
    }

    private static Inline BuildImage(string alt, string url, double baseFontSize, string? basePath)
    {
        if (TryBuildLocalBadge(url, alt, null, baseFontSize, out var badge))
            return badge;

        return new InlineUIContainer(CreateMarkdownImage(url, alt, baseFontSize, basePath))
        {
            BaselineAlignment = BaselineAlignment.Center
        };
    }

    private static Inline BuildImageLink(string alt, string imgUrl, string href, double baseFontSize, string? basePath)
    {
        if (TryBuildLocalBadge(imgUrl, alt, href, baseFontSize, out var badge))
            return badge;

        var image = CreateMarkdownImage(imgUrl, alt, baseFontSize, basePath);
        var link = new Hyperlink(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center })
        {
            ToolTip = string.IsNullOrEmpty(href) ? imgUrl : href,
            TextDecorations = null
        };
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
            link.NavigateUri = uri;
        else if (Uri.TryCreate(imgUrl, UriKind.Absolute, out var imgUri))
            link.NavigateUri = imgUri;
        return link;
    }

    private static bool IsBadgeUrl(string url) =>
        url.Contains("/badge/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Draws a two-tone badge in pure WPF from a /badge/Label-Message-color URL.
    /// No network access.
    /// </summary>
    private static bool TryBuildLocalBadge(string url, string alt, string? href, double baseFontSize, out Inline result)
    {
        result = null!;
        if (!TryParseBadgeParts(url, out string label, out string message, out Color color))
            return false;

        double fontSize = Math.Max(10.5, baseFontSize * 0.82);

        var leftText = new TextBlock
        {
            Text = label,
            FontSize = fontSize,
            FontFamily = BadgeFont,
            Foreground = Brushes.White,
            Padding = new Thickness(0),
            Margin = new Thickness(0)
        };
        var rightText = new TextBlock
        {
            Text = message,
            FontSize = fontSize,
            FontFamily = BadgeFont,
            Foreground = Brushes.White,
            Padding = new Thickness(0),
            Margin = new Thickness(0)
        };

        var left = new Border
        {
            Background = BadgeLabel,
            Padding = new Thickness(7, 2, 7, 2),
            Child = leftText
        };
        var right = new Border
        {
            Background = Solid(color),
            Padding = new Thickness(7, 2, 7, 2),
            Child = rightText
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (!string.IsNullOrEmpty(label)) row.Children.Add(left);
        if (!string.IsNullOrEmpty(message)) row.Children.Add(right);
        if (row.Children.Count == 0)
        {
            rightText.Text = string.IsNullOrEmpty(alt) ? "badge" : alt;
            row.Children.Add(right);
        }

        var chrome = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 1, 6, 1),
            SnapsToDevicePixels = true,
            ClipToBounds = true
        };

        FrameworkElement content = chrome;
        if (!string.IsNullOrWhiteSpace(href))
        {
            var btn = new Button
            {
                Content = chrome,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = href,
                Focusable = false
            };
            btn.Click += (_, _) =>
            {
                try
                {
                    if (Uri.TryCreate(href, UriKind.Absolute, out var abs))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = abs.AbsoluteUri,
                            UseShellExecute = true
                        });
                    }
                }
                catch { }
            };
            content = btn;
        }

        result = new InlineUIContainer(content) { BaselineAlignment = BaselineAlignment.Center };
        return true;
    }

    private static bool TryParseBadgeParts(string url, out string label, out string message, out Color color)
    {
        label = "";
        message = "";
        color = Color.FromRgb(0x00, 0x7E, 0xC6);

        int badgeIdx = url.IndexOf("/badge/", StringComparison.OrdinalIgnoreCase);
        if (badgeIdx < 0)
            return false;

        string rest = url.Substring(badgeIdx + 7);
        int q = rest.IndexOfAny(['?', '#']);
        if (q >= 0) rest = rest.Substring(0, q);
        if (rest.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) rest = rest[..^4];
        if (rest.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) rest = rest[..^4];

        try { rest = Uri.UnescapeDataString(rest); }
        catch { }

        // Field separator is '-'; a literal dash inside a field is encoded as '--'
        string normalized = rest.Replace("--", "\u001e", StringComparison.Ordinal);
        int last = normalized.LastIndexOf('-');
        if (last <= 0) return false;

        string colorToken = normalized[(last + 1)..].Replace("\u001e", "-", StringComparison.Ordinal);
        string left = normalized[..last];
        int mid = left.LastIndexOf('-');
        if (mid < 0)
        {
            label = left.Replace("\u001e", "-", StringComparison.Ordinal).Replace('_', ' ');
            message = "";
        }
        else
        {
            label = left[..mid].Replace("\u001e", "-", StringComparison.Ordinal).Replace('_', ' ');
            message = left[(mid + 1)..].Replace("\u001e", "-", StringComparison.Ordinal).Replace('_', ' ');
        }

        color = ResolveBadgeColor(colorToken);
        return label.Length > 0 || message.Length > 0;
    }

    private static Color ResolveBadgeColor(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Color.FromRgb(0x00, 0x7E, 0xC6);

        if (BadgeNamedColors.TryGetValue(token, out var named))
            return named;

        string hex = token.TrimStart('#');
        if (hex.Length == 3)
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        else if (hex.Length == 8)
            hex = hex[^6..];

        if (hex.Length == 6 &&
            int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            return Color.FromRgb(
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF));
        }

        return Color.FromRgb(0x00, 0x7E, 0xC6);
    }

    private static Image CreateMarkdownImage(string url, string alt, double baseFontSize, string? basePath)
    {
        var image = new Image
        {
            MaxHeight = 500,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 2, 6, 2),
            ToolTip = string.IsNullOrEmpty(alt) ? url : alt,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };

        // /badge/… URLs are drawn as local chips — never downloaded
        if (IsBadgeUrl(url))
            return image;

        Uri? uri = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri) &&
            (absoluteUri.Scheme == Uri.UriSchemeHttp ||
             absoluteUri.Scheme == Uri.UriSchemeHttps ||
             absoluteUri.Scheme == Uri.UriSchemeFile))
        {
            uri = absoluteUri;
        }
        else
        {
            try
            {
                string safeUrl = url.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
                string currentSearchDir = basePath ?? AppContext.BaseDirectory;
                var dirInfo = new DirectoryInfo(currentSearchDir);
                while (dirInfo != null)
                {
                    string testPath = Path.Combine(dirInfo.FullName, safeUrl);
                    if (File.Exists(testPath))
                    {
                        uri = new Uri(testPath, UriKind.Absolute);
                        break;
                    }
                    dirInfo = dirInfo.Parent;
                }
            }
            catch { }
        }

        if (uri != null)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

                // A local screenshot is decoded no wider than the screen: the
                // document never shows it larger, and a 4K screenshot decoded
                // in full takes about 33 MB. Remote images download
                // asynchronously and their size is not known up front.
                if (uri.IsFile) bitmap.DecodePixelWidth = DecodeWidthFor(uri.LocalPath);

                bitmap.EndInit();
                if (bitmap.CanFreeze) bitmap.Freeze();
                image.Source = bitmap;
            }
            catch { }
        }

        return image;
    }

    // 0 keeps the original width. Only images wider than the screen are
    // reduced; setting a decode width above the original would enlarge it.
    private static int DecodeWidthFor(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // Header only, no pixels
            var header = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

            double dpi = Application.Current?.MainWindow is Visual window
                ? VisualTreeHelper.GetDpi(window).DpiScaleX
                : 1.0;
            int limit = (int)(SystemParameters.PrimaryScreenWidth * dpi);

            return header.PixelWidth > limit ? limit : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static Inline BuildEmojiImage(string emoji, double fontSize)
    {
        // Offline-first: no CDN. WPF may tint emoji with the theme color
        // (monochrome), but it always works without network.
        return new Run(emoji)
        {
            FontFamily = EmojiFont,
            FontSize = fontSize,
            Foreground = BrushOr("Brush.TextPrimary", FallbackPrimary)
        };
    }
}
