using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace R2Cmd;

// =============================================================================
// What to look for inside a file. Built once per search, not per file.
// =============================================================================
public sealed class TextQuery
{
    public string Text { get; }
    public bool UseRegex { get; }
    public bool CaseSensitive { get; }

    private readonly Regex? _regex;
    private readonly StringComparison _comparison;

    public TextQuery(string text, bool useRegex, bool caseSensitive)
    {
        Text = text;
        UseRegex = useRegex;
        CaseSensitive = caseSensitive;

        _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (useRegex)
        {
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (!caseSensitive) options |= RegexOptions.IgnoreCase;

            // Throws on a malformed pattern; the caller reports it before starting
            _regex = new Regex(text, options);
        }
    }

    // Quick check to see if the line contains the text at all
    public bool IsMatch(string line) =>
        _regex?.IsMatch(line) ?? line.Contains(Text, _comparison);

    // Counts the exact number of times the target text or regex appears in the line
    public int CountMatches(string line)
    {
        // Regex.Count walks the matches without building a MatchCollection and a
        // Match object per hit, which Matches(...).Count did for every line
        if (_regex != null)
        {
            return _regex.Count(line);
        }

        if (string.IsNullOrEmpty(Text)) return 0;

        int count = 0;
        int index = 0;

        while ((index = line.IndexOf(Text, index, _comparison)) != -1)
        {
            count++;
            index += Text.Length; // Advance past the current match to prevent overlap counting
        }

        return count;
    }
}

// =============================================================================
// Searches file contents line by line and counts occurrences.
//
// Line based on purpose: it bounds memory regardless of file size, it makes a
// regular expression behave the way people expect from grep, and it removes the
// class of bugs where a match straddles the boundary between two read buffers.
// The trade-off is the same one every grep makes — a phrase split across a line
// break is not found.
// =============================================================================
public static class TextSearcher
{
    // Beyond this a file is assumed not to be something anyone greps
    public const long MaxFileSize = 128L * 1024 * 1024;

    public const int SampleSize = 8192;

    // Returns the total number of matches found inside the file.
    // Returns 0 if the file doesn't exist, is binary, or lacks matches.
    public static int CountMatchesInFile(string path, TextQuery query, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileSize) return 0;

            // bufferSize 1 turns off FileStream's own buffer: the StreamReader in
            // CountMatchesInStream already reads in 32 KB blocks, and two layers
            // of buffering only copied every byte twice
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            return CountMatchesInStream(stream, query, token);
        }
        catch (OperationCanceledException) { throw; }
        catch { return 0; }   // locked, denied, vanished: not a match, not an error
    }

    // Reads the head of a file and reports how it should be decoded. Null means
    // binary — the viewer switches to hex on that.
    public static Encoding? DetectFileEncoding(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var sample = new byte[SampleSize];

            // Read may return less than asked even mid-file; ReadAtLeast does not
            int read = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);

            return read == 0 ? new UTF8Encoding(false) : DetectEncoding(sample, read);
        }
        catch { return null; }
    }

    // Reads the stream line by line until the end to count every occurrence
    public static int CountMatchesInStream(Stream stream, TextQuery query, CancellationToken token)
    {
        if (!stream.CanSeek)
        {
            // An archive entry cannot be rewound after peeking at its head, so it
            // is pulled into memory first. The caller caps the size before asking.
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;
            return CountMatchesInStream(memory, query, token);
        }

        // Pooled: a content search opens thousands of files, and a fresh 8 KB
        // array per file was steady garbage
        byte[] sample = System.Buffers.ArrayPool<byte>.Shared.Rent(SampleSize);
        Encoding? encoding;
        try
        {
            int read = stream.ReadAtLeast(sample.AsSpan(0, SampleSize), SampleSize, throwOnEndOfStream: false);
            if (read == 0) return 0;

            encoding = DetectEncoding(sample, read);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(sample);
        }

        if (encoding == null) return 0;   // binary

        stream.Position = 0;

        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, 32768, leaveOpen: true);

        int totalMatches = 0;
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            token.ThrowIfCancellationRequested();

            // Increment the counter by the number of times the word appears in this specific line
            totalMatches += query.CountMatches(line);
        }

        return totalMatches;
    }

    // =========================================================================
    // Returns null when the content looks binary and should be skipped.
    //
    // Order matters: a byte order mark settles it outright; without one, UTF-16
    // is recognised by the column of zero bytes that latin text produces in it;
    // any zero byte left after that is the classic sign of a binary file; and
    // what remains is UTF-8 if it decodes as such, otherwise the system code page.
    // =========================================================================
    /// <summary>Null means the content looks binary.</summary>
    public static Encoding? DetectEncoding(byte[] sample, int length)
    {
        if (length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
            return new UTF8Encoding(false);

        if (length >= 2 && sample[0] == 0xFF && sample[1] == 0xFE) return Encoding.Unicode;
        if (length >= 2 && sample[0] == 0xFE && sample[1] == 0xFF) return Encoding.BigEndianUnicode;

        int zerosAtEven = 0, zerosAtOdd = 0;
        for (int i = 0; i < length; i++)
        {
            if (sample[i] != 0) continue;
            if ((i & 1) == 0) zerosAtEven++; else zerosAtOdd++;
        }

        int pairs = length / 2;
        if (pairs > 0)
        {
            // Little endian keeps the high byte, at the odd offset, at zero
            if (zerosAtOdd > pairs * 0.3 && zerosAtEven == 0) return Encoding.Unicode;
            if (zerosAtEven > pairs * 0.3 && zerosAtOdd == 0) return Encoding.BigEndianUnicode;
        }

        if (zerosAtEven + zerosAtOdd > 0) return null;

        return IsValidUtf8(sample, length) ? new UTF8Encoding(false) : SystemAnsi();
    }

    private static Encoding? s_ansi;

    // =========================================================================
    // On .NET Core Encoding.Default is UTF-8, so a Windows-1251 file would be
    // decoded as mojibake and never match a Cyrillic query. The real ANSI code
    // page needs the CodePages provider, which may not be referenced — hence the
    // guarded lookup and the Latin1 fallback, which at least keeps byte values
    // intact for ASCII queries.
    // =========================================================================
    public static Encoding SystemAnsi()
    {
        if (s_ansi != null) return s_ansi;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            s_ansi = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch
        {
            s_ansi = Encoding.Latin1;
        }

        return s_ansi;
    }

    private static bool IsValidUtf8(byte[] data, int length)
    {
        int i = 0;

        while (i < length)
        {
            byte b = data[i];

            if (b < 0x80) { i++; continue; }

            int following =
                (b & 0xE0) == 0xC0 ? 1 :
                (b & 0xF0) == 0xE0 ? 2 :
                (b & 0xF8) == 0xF0 ? 3 : -1;

            if (following < 0) return false;

            // A sequence cut off by the end of the sample is not a failure
            if (i + following >= length) return true;

            for (int k = 1; k <= following; k++)
            {
                if ((data[i + k] & 0xC0) != 0x80) return false;
            }

            i += following + 1;
        }

        return true;
    }
}
