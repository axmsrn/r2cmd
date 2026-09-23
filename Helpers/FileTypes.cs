using System;
using System.Collections.Generic;

namespace R2Cmd;

// =============================================================================
// Which files are text, and how they are shown. The single list used by the
// viewer (F3), the built-in editor (F4) and the choice between them.
//
// There used to be three copies (viewer, editor launcher, editor window) that
// had drifted apart: the viewer knew .svg, .jsx and .tsx and the editor did
// not, and .log was code in one place and plain text in another.
// =============================================================================
public static class FileTypes
{
    // Source code and configuration: syntax highlighting and line numbers
    public static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".c", ".cc", ".cpp", ".h", ".hpp", ".java", ".py",
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".ejs",
        ".html", ".css", ".xml", ".json", ".yaml", ".yml",
        ".sh", ".bat", ".cmd", ".ps1", ".php", ".rb", ".go",
        ".rs", ".swift", ".sql", ".ini", ".cfg", ".conf",
        ".xaml", ".fs", ".vb", ".lua", ".kt", ".csproj", ".svg",
        ".env", ".gitignore", ".gitattributes", ".gitconfig", ".gitmodules",
        ".dockerignore"
    };

    // Plain text: no highlighting, no line numbers
    public static readonly HashSet<string> PlainText = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".tsv"
    };

    // Rendered as a document by the viewer
    public static readonly HashSet<string> Markdown = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd"
    };

    // Known to be text without looking inside the file
    public static bool IsKnownText(string extension) =>
        Code.Contains(extension) || PlainText.Contains(extension) || Markdown.Contains(extension);
}
