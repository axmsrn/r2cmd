using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace R2Cmd;

internal static class AvalonEditDraculaTheme
{
    private enum ColorCategory
    {
        Comment,
        String,
        Number,
        Keyword,
        Type,
        Function,
        Attribute,
        Error,
        Plain
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> HighlightingResources =
        new(BuildHighlightingResourceMap);

    internal static void Apply(TextEditor editor, string path, bool showLineNumbers)
    {
        editor.Background = (Brush)Application.Current.FindResource("Brush.Background");
        editor.Foreground = DraculaPalette.ForegroundBrush;
        editor.LineNumbersForeground = (Brush)Application.Current.FindResource("Brush.TextSecondary");
        editor.ShowLineNumbers = showLineNumbers;
        editor.TextArea.Background = (Brush)Application.Current.FindResource("Brush.Background");
        editor.TextArea.Foreground = DraculaPalette.GetForegroundBrush();
        // Same gray as the focused row in the file panel
        editor.TextArea.SelectionBrush = (Brush)Application.Current.FindResource("Brush.Selection");
        editor.TextArea.SelectionForeground = null;   // keep syntax colors when selected
        editor.TextArea.SelectionBorder = null;
        editor.TextArea.Caret.CaretBrush = DraculaPalette.ForegroundBrush;
        editor.TextArea.TextView.LinkTextForegroundBrush = ThemeManager.IsDarkTheme
            ? DraculaPalette.CyanBrush
            : DraculaPalette.LightCyanBrush;
        editor.TextArea.TextView.LinkTextBackgroundBrush = Brushes.Transparent;

        // Full-width selection
        var textView = editor.TextArea.TextView;
        bool hasRenderer = false;
        foreach (var renderer in textView.BackgroundRenderers)
        {
            if (renderer is FullWidthSelectionRenderer)
            {
                hasRenderer = true;
                break;
            }
        }
        if (!hasRenderer)
            textView.BackgroundRenderers.Add(new FullWidthSelectionRenderer(editor));
        editor.TextArea.TextView.Margin = new Thickness(6, 0, 0, 0);
        editor.Padding = new Thickness(4, 0, 0, 0);

        foreach (var margin in editor.TextArea.LeftMargins)
        {
            if (margin is FrameworkElement element &&
                margin.GetType().Name == "LineNumberMargin")
            {
                element.Margin = new Thickness(0, 0, 4, 0);
            }
        }

        editor.Options.HighlightCurrentLine = true;
        editor.TextArea.TextView.CurrentLineBackground = (Brush)Application.Current.FindResource("Brush.Surface");
        editor.TextArea.TextView.CurrentLineBorder = new Pen((Brush)Application.Current.FindResource("Brush.Border"), 1);

        // Hide current-line highlight while there is a selection
        editor.TextArea.SelectionChanged += (_, _) =>
        {
            editor.Options.HighlightCurrentLine = editor.TextArea.Selection.IsEmpty;
        };
        editor.SyntaxHighlighting = CreateDefinition(path);
        MakeLineNumberSeparatorSolid(editor);
        editor.Dispatcher.BeginInvoke(() => MakeLineNumberSeparatorSolid(editor),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        editor.TextArea.TextView.Redraw();
    }

    private static void MakeLineNumberSeparatorSolid(TextEditor editor)
    {
        var stroke = (Brush)Application.Current.FindResource("Brush.Border");

        foreach (var margin in editor.TextArea.LeftMargins)
        {
            if (margin is System.Windows.Shapes.Line line)
            {
                line.StrokeDashArray = new DoubleCollection(); // solid
                line.Stroke = stroke;
                line.StrokeThickness = 1;
            }
        }
    }
    internal static string GetHighlightingExtension(string path)
    {
        string fileName = Path.GetFileName(path);

        if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            return ".env";

        if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
        {
            return ".sh";
        }

        return Path.GetExtension(path);
    }

    private static IHighlightingDefinition? CreateDefinition(string path)
    {
        string extension = GetHighlightingExtension(path);
        var definition = HighlightingManager.Instance.GetDefinitionByExtension(extension);

        if (definition == null && extension is ".ts" or ".tsx" or ".jsx" or ".mjs" or ".ejs" or ".env")
            definition = HighlightingManager.Instance.GetDefinition("JavaScript");

        if (definition == null)
            return null;

        var freshDefinition = LoadFreshDefinition(definition.Name);
        if (freshDefinition == null)
            return definition;

        if (extension is ".css" or ".html" or ".htm")
        {
            AddRule(freshDefinition, "DraculaHexColor",
                @"#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})(?![0-9a-fA-F])",
                DraculaPalette.GetPurple());
            AddRule(freshDefinition, "DraculaCssProperty",
                @"\b[a-zA-Z_-][a-zA-Z0-9_-]*\s*(?=:)",
                DraculaPalette.GetGreen());
        }

        foreach (var color in freshDefinition.NamedHighlightingColors)
        {
            if (!color.IsFrozen)
                ApplyColor(color);
        }

        return freshDefinition;
    }

    private static void AddRule(
        IHighlightingDefinition definition,
        string name,
        string pattern,
        Color color)
    {
        var highlightingColor = new HighlightingColor
        {
            Name = name,
            Foreground = new SimpleHighlightingBrush(color)
        };

        if (definition.NamedHighlightingColors is IList<HighlightingColor> colors)
            colors.Add(highlightingColor);

        var rule = new HighlightingRule
        {
            Regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant),
            Color = highlightingColor
        };

        InsertRuleRecursive(definition.MainRuleSet, rule, new HashSet<HighlightingRuleSet>());
    }

    private static void InsertRuleRecursive(
        HighlightingRuleSet? ruleSet,
        HighlightingRule rule,
        HashSet<HighlightingRuleSet> visited)
    {
        if (ruleSet == null || !visited.Add(ruleSet))
            return;

        ruleSet.Rules.Insert(0, rule);

        foreach (var span in ruleSet.Spans)
        {
            string name = span.SpanColor?.Name ?? "";
            if (!Contains(name, "string") &&
                !Contains(name, "char") &&
                !Contains(name, "comment"))
            {
                InsertRuleRecursive(span.RuleSet, rule, visited);
            }
        }
    }

    private static void ApplyColor(HighlightingColor color)
    {
        if (string.IsNullOrEmpty(color.Name))
            return;

        string name = color.Name.ToLowerInvariant();
        if (name is "draculahexcolor" or "draculacssproperty")
            return;

        ColorCategory category = ResolveCategory(name);
        Color resolved = category switch
        {
            ColorCategory.Comment => DraculaPalette.GetComment(),
            ColorCategory.String => DraculaPalette.GetYellow(),
            ColorCategory.Number => DraculaPalette.GetPurple(),
            ColorCategory.Keyword => DraculaPalette.GetPink(),
            ColorCategory.Type => DraculaPalette.GetCyan(),
            ColorCategory.Function => DraculaPalette.GetGreen(),
            ColorCategory.Attribute => DraculaPalette.GetOrange(),
            ColorCategory.Error => DraculaPalette.GetRed(),
            _ => DraculaPalette.GetForeground()
        };

        color.Foreground = new SimpleHighlightingBrush(resolved);
    }

    private static ColorCategory ResolveCategory(string name)
    {
        if (Contains(name, "comment") || name is "header" or "filename" or "position" or "blockquote")
            return ColorCategory.Comment;
        if (Contains(name, "string") || Contains(name, "char") || Contains(name, "regex") || name is "value" or "code")
            return ColorCategory.String;
        if (Contains(name, "number") || Contains(name, "digit") || Contains(name, "boolean") ||
            Contains(name, "constant") || Contains(name, "literal") || name is "null" or "bool" or "entities" or "entityreference")
            return ColorCategory.Number;
        if (Contains(name, "keyword") || Contains(name, "modifier") || Contains(name, "visibility") ||
            Contains(name, "operator") || Contains(name, "statement") || Contains(name, "control") ||
            Contains(name, "namespace") || name is "assignment" or "package" or "this" or "friend" or "heading")
            return ColorCategory.Keyword;
        if (Contains(name, "type") || Contains(name, "tag") || Contains(name, "selector") ||
            name is "class" or "void" or "link" or "image" or "javascriptintrinsics")
            return ColorCategory.Type;
        if (Contains(name, "method") || Contains(name, "function") || Contains(name, "property") ||
            name is "command" or "addedtext" or "javascriptglobalfunctions")
            return ColorCategory.Function;
        if (Contains(name, "attribute") || Contains(name, "decorator") || Contains(name, "annotation") ||
            Contains(name, "preprocessor") || Contains(name, "directive") || name is "fieldname" or "variable" or "strongemphasis")
            return ColorCategory.Attribute;
        if (Contains(name, "error") || Contains(name, "removed"))
            return ColorCategory.Error;

        return ColorCategory.Plain;
    }

    private static bool Contains(string value, string part) =>
        value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

    private static IReadOnlyDictionary<string, string> BuildHighlightingResourceMap()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(HighlightingManager).Assembly;

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(".xshd", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) continue;
                using var reader = XmlReader.Create(stream);
                var definition = HighlightingLoader.LoadXshd(reader);
                if (!string.IsNullOrWhiteSpace(definition.Name))
                    result[definition.Name] = resourceName;
            }
            catch
            {
                // Ignore incompatible embedded definitions.
            }
        }

        return result;
    }

    private static IHighlightingDefinition? LoadFreshDefinition(string name)
    {
        try
        {
            if (!HighlightingResources.Value.TryGetValue(name, out string? resourceName))
                return null;

            var assembly = typeof(HighlightingManager).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var reader = XmlReader.Create(stream);
            var definition = HighlightingLoader.LoadXshd(reader);
            return HighlightingLoader.Load(definition, HighlightingManager.Instance);
        }
        catch
        {
            return null;
        }
    }

    private sealed class FullWidthSelectionRenderer : ICSharpCode.AvalonEdit.Rendering.IBackgroundRenderer
    {
        private readonly TextEditor _editor;

        public FullWidthSelectionRenderer(TextEditor editor)
        {
            _editor = editor;
        }

        public ICSharpCode.AvalonEdit.Rendering.KnownLayer Layer =>
            ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection;

        public void Draw(ICSharpCode.AvalonEdit.Rendering.TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid) return;
            if (_editor.TextArea.Selection.IsEmpty) return;

            var brush = _editor.TextArea.SelectionBrush;
            if (brush == null) return;

            foreach (var segment in _editor.TextArea.Selection.Segments)
            {
                var startLine = textView.Document.GetLineByOffset(segment.StartOffset);
                var endLine = textView.Document.GetLineByOffset(segment.EndOffset);

                for (int lineNumber = startLine.LineNumber; lineNumber <= endLine.LineNumber; lineNumber++)
                {
                    var docLine = textView.Document.GetLineByNumber(lineNumber);
                    var visualLine = textView.GetVisualLine(lineNumber);
                    if (visualLine == null) continue;

                    // Full-width only when the entire line is selected
                    bool fullySelected =
                        segment.StartOffset <= docLine.Offset &&
                        segment.EndOffset >= docLine.EndOffset;

                    if (!fullySelected)
                        continue;

                    double y = visualLine.VisualTop - textView.ScrollOffset.Y;
                    drawingContext.DrawRectangle(
                        brush,
                        null,
                        new Rect(0, y, textView.ActualWidth, visualLine.Height));
                }
            }
        }
    }

    internal static void ApplyHex(TextEditor editor)
    {
        Apply(editor, "dump.txt", showLineNumbers: false);

        var textView = editor.TextArea.TextView;
        foreach (var transformer in textView.LineTransformers)
        {
            if (transformer is HexColorizer)
                return;
        }

        textView.LineTransformers.Add(new HexColorizer());
    }

    private sealed class HexColorizer : ICSharpCode.AvalonEdit.Rendering.DocumentColorizingTransformer
    {
        private static readonly Regex HexWord =
            new(@"\b[0-9A-Fa-f]+\b", RegexOptions.Compiled);

        protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
        {
            string text = CurrentContext.Document.GetText(line);
            if (text.Length == 0) return;

            int hexEnd = Math.Min(60, text.Length);

            var hexBrush = new SolidColorBrush(DraculaPalette.GetPurple());
            hexBrush.Freeze();

            foreach (Match match in HexWord.Matches(text.Substring(0, hexEnd)))
            {
                ChangeLinePart(
                    line.Offset + match.Index,
                    line.Offset + match.Index + match.Length,
                    element => element.TextRunProperties.SetForegroundBrush(hexBrush));
            }

            if (text.Length > 60)
            {
                var asciiBrush = new SolidColorBrush(DraculaPalette.GetComment());
                asciiBrush.Freeze();
                ChangeLinePart(
                    line.Offset + 60,
                    line.Offset + text.Length,
                    element => element.TextRunProperties.SetForegroundBrush(asciiBrush));
            }
        }
    }
}
