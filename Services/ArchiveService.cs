using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;

namespace R2Cmd;

// A single entry inside the archive (file or folder). Intentionally NOT FileEntry —
// the class shouldn't depend on UI so it can be tested separately.
public sealed record ArchiveNode(string Name, string FullKey, bool IsFolder, long Size, DateTime? Modified);

public static class ArchiveService
{
    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".tar" };

    // =========================================================================
    // SELF-EXTRACTING ARCHIVES
    //
    // An installer is a PE executable with an archive appended to it, so the
    // extension says nothing. Adding ".exe" to ArchiveExtensions is not an
    // option: ParseVirtualPath would then split every path containing "app.exe\"
    // and ordinary navigation would break.
    //
    // Instead an executable is only treated as an archive after it has been
    // opened as one on purpose — Ctrl+PgDn, exactly like Total Commander. The
    // offset of the payload inside it is remembered here, so the scan happens
    // once and ParseVirtualPath stays a pure string operation.
    // =========================================================================
    private static readonly ConcurrentDictionary<string, long> s_selfExtracting =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly byte[][] s_embeddedSignatures =
    {
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },  // 7z
        new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 },  // Rar!
    };

    // The stub in front of the payload is a few hundred kilobytes in practice.
    // Scanning further would mean reading most of a gigabyte for nothing.
    private const int SfxScanLimit = 32 * 1024 * 1024;
    private const int SfxScanChunk = 1 << 20;

    public static bool IsArchiveFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (IsSelfExtracting(path)) return true;
        if (IsCompressedTar(path)) return true;
        return ArchiveExtensions.Contains(Path.GetExtension(path));
    }

    private static bool IsCompressedTar(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);
    }

    // GNU tar stores members as "./file". That would show a folder named "."
    // and break ExtractFiles key lookup against ListChildren keys.
    private static string NormalizeTarKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        string s = key.Replace('\\', '/');
        while (s.StartsWith("./", StringComparison.Ordinal))
            s = s.Substring(2);
        return s.Trim('/');
    }

    private static bool IsTarDirectory(TarEntry entry) =>
        entry.EntryType is TarEntryType.Directory or TarEntryType.DirectoryList;

    /// <summary>True when this executable has already been opened as an archive.</summary>
    public static bool IsSelfExtracting(string path) =>
        s_selfExtracting.TryGetValue(path, out long offset) && offset >= 0;

    // =========================================================================
    // Decides whether an executable can be browsed as an archive, and remembers
    // the answer. Touches the disk — call it off the UI thread.
    // =========================================================================
    public static bool TryOpenSelfExtracting(string path)
    {
        if (s_selfExtracting.TryGetValue(path, out long known)) return known >= 0;

        if (!File.Exists(path)) return false;

        // Whole file first: a zip SFX needs no offset at all
        if (CanOpenAt(path, 0))
        {
            s_selfExtracting[path] = 0;
            return true;
        }

        long offset = FindEmbeddedArchiveOffset(path);
        if (offset > 0 && CanOpenAt(path, offset))
        {
            s_selfExtracting[path] = offset;
            return true;
        }

        s_selfExtracting[path] = -1;
        return false;
    }

    private static bool CanOpenAt(string path, long offset)
    {
        try
        {
            using var stream = OpenAt(path, offset);
            using var archive = ArchiveFactory.OpenArchive(stream);

            // Touching an entry forces the headers to be parsed for real
            return archive.Entries.Any();
        }
        catch { return false; }
    }

    // =========================================================================
    // Finds where the payload starts inside a self-extracting executable.
    // Span.IndexOf is vectorised, which avoids micro-stutters.
    // =========================================================================
    private static long FindEmbeddedArchiveOffset(string path)
    {
        const int maxSignature = 6;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(SfxScanChunk + maxSignature);

        try
        {
            using var file = File.OpenRead(path);
            long limit = Math.Min(file.Length, SfxScanLimit);
            long bufferStart = 0;
            int carry = 0;

            while (bufferStart + carry < limit)
            {
                int toRead = (int)Math.Min(SfxScanChunk, limit - (bufferStart + carry));
                int read = file.Read(buffer, carry, toRead);
                if (read <= 0) break;

                int available = carry + read;
                var window = buffer.AsSpan(0, available);

                long best = -1;
                foreach (var signature in s_embeddedSignatures)
                {
                    int found = window.IndexOf(signature);
                    if (found >= 0 && (best < 0 || found < best)) best = found;
                }

                if (best >= 0) return bufferStart + best;

                carry = Math.Min(maxSignature - 1, available);
                buffer.AsSpan(available - carry, carry).CopyTo(buffer);
                bufferStart += available - carry;
            }
        }
        catch { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return -1;
    }

    private static Stream OpenAt(string path, long offset)
    {
        var file = File.OpenRead(path);
        return offset > 0 ? new OffsetStream(file, offset) : file;
    }

    private static Stream OpenArchiveStream(string archivePath)
    {
        s_selfExtracting.TryGetValue(archivePath, out long offset);
        return OpenAt(archivePath, offset > 0 ? offset : 0);
    }

    private readonly record struct CachedEntry(string Key, bool IsDirectory, long Size, DateTime? Modified);

    private sealed class ArchiveIndex
    {
        public long Length;
        public DateTime LastWriteUtc;
        public CachedEntry[] Entries = Array.Empty<CachedEntry>();
    }

    private static readonly ConcurrentDictionary<string, ArchiveIndex> s_index =
        new(StringComparer.OrdinalIgnoreCase);

    private const int MaxCachedIndexes = 8;

    private static ArchiveIndex GetIndex(string archivePath)
    {
        var info = new FileInfo(archivePath);
        if (s_index.TryGetValue(archivePath, out var cached) &&
            cached.Length == info.Length &&
            cached.LastWriteUtc == info.LastWriteTimeUtc)
            return cached;

        var list = new List<CachedEntry>();
        if (IsCompressedTar(archivePath))
        {
            // Stream the gzip layer and read tar headers only. copyData: false
            // skips each file body so listing a backup does not inflate working set.
            using var gzip = new GZipStream(OpenArchiveStream(archivePath), CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            TarEntry? entry;
            while ((entry = tar.GetNextEntry(copyData: false)) != null)
            {
                string key = NormalizeTarKey(entry.Name);
                if (key.Length == 0 || key == "." || key == "..") continue;
                list.Add(new CachedEntry(
                    key,
                    IsTarDirectory(entry),
                    entry.Length,
                    NullableTime(entry)));
            }
        }
        else
        {
            using var stream = OpenArchiveStream(archivePath);
            using var archive = ArchiveFactory.OpenArchive(stream);
            foreach (var entry in archive.Entries)
            {
                if (entry.Key is null) continue;
                string key = NormalizeTarKey(entry.Key);
                if (key.Length == 0) continue;
                list.Add(new CachedEntry(
                    key,
                    entry.IsDirectory,
                    entry.Size,
                    entry.LastModifiedTime));
            }
        }

        var index = new ArchiveIndex
        {
            Length = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            Entries = list.ToArray()
        };

        // The cache used to keep every archive ever opened for the whole
        // session; a single archive with 100k entries holds megabytes of keys.
        // A handful is plenty for moving around inside the archives in use.
        if (s_index.Count >= MaxCachedIndexes) s_index.Clear();

        s_index[archivePath] = index;
        return index;
    }

    // =========================================================================
    // Read-only view of a file starting at a fixed offset.
    // Overrides Span<byte> reads to ensure high performance on .NET 8+.
    // =========================================================================
    private sealed class OffsetStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _offset;

        public OffsetStream(Stream inner, long offset)
        {
            _inner = inner;
            _offset = offset;
            _inner.Position = offset;
        }

        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length - _offset;

        public override long Position
        {
            get => _inner.Position - _offset;
            set => _inner.Position = _offset + value;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => origin switch
        {
            SeekOrigin.Begin => _inner.Seek(_offset + offset, SeekOrigin.Begin) - _offset,
            SeekOrigin.Current => _inner.Seek(offset, SeekOrigin.Current) - _offset,
            SeekOrigin.End => _inner.Seek(offset, SeekOrigin.End) - _offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    // =========================================================================
    // Counts the files under a folder inside an archive, without extracting.
    //
    // Public because the copy pipeline needs the totals before it starts, and it
    // has to go through OpenArchiveStream to honour the payload offset of a
    // self-extracting executable — reaching for that private method by
    // reflection was the alternative.
    // =========================================================================
    public static (int Files, long Bytes) SumTree(string archivePath, string subPath)
    {
        string prefix = NormalizeDir(subPath);

        int files = 0;
        long bytes = 0;

        foreach (var entry in GetIndex(archivePath).Entries)
        {
            if (entry.IsDirectory) continue;
            if (prefix.Length > 0 && !entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            files++;
            bytes += entry.Size;
        }

        return (files, bytes);
    }

    // =========================================================================
    // Looks for entries inside the archive, by name and optionally by content.
    //
    // One pass over the archive, which is also the only sane access pattern for
    // a solid one. Entries that are not read still have to be drained, otherwise
    // the solid decoder loses its place — and an entry that IS read has to be
    // drained too when the search stops early on a match.
    //
    // contentMatches is given the entry stream and returns whether it matched.
    // Entries larger than maxContentSize are not opened at all: decompressing a
    // few hundred megabytes to grep it is rarely what the user meant.
    // =========================================================================
    public static List<ArchiveNode> FindEntries(
        string archivePath,
        Func<string, bool> nameMatches,
        Func<Stream, bool>? contentMatches,
        long maxContentSize,
        CancellationToken token)
    {
        var found = new List<ArchiveNode>();

        if (IsCompressedTar(archivePath))
        {
            using var gzip = new GZipStream(OpenArchiveStream(archivePath), CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            TarEntry? entry;

            // =================================================================
            // copyData: false, always. With true, TarReader copies the body of
            // EVERY entry into a MemoryStream before returning it, including
            // entries whose name does not match and entries far larger than
            // maxContentSize: one multi-gigabyte file inside a .tar.gz could
            // exhaust memory. Without the copy, DataStream reads straight from
            // the archive and stays valid until the next GetNextEntry, which
            // skips whatever was not read.
            // =================================================================
            while ((entry = tar.GetNextEntry(copyData: false)) != null)
            {
                token.ThrowIfCancellationRequested();
                if (IsTarDirectory(entry)) continue;

                string key = NormalizeTarKey(entry.Name);
                if (key.Length == 0) continue;

                string name = Path.GetFileName(key);
                bool nameOk = nameMatches(name);

                if (contentMatches == null)
                {
                    if (nameOk) found.Add(new ArchiveNode(name, key, false, entry.Length, NullableTime(entry)));
                    continue;
                }

                if (!nameOk || entry.Length > maxContentSize || entry.DataStream == null)
                    continue;

                bool hit = contentMatches(entry.DataStream);
                if (hit) found.Add(new ArchiveNode(name, key, false, entry.Length, NullableTime(entry)));
            }

            return found;
        }

        using var stream = OpenArchiveStream(archivePath);
        using var archive = ArchiveFactory.OpenArchive(stream);

        foreach (var item in archive.Entries)
        {
            token.ThrowIfCancellationRequested();

            if (item.Key is null || item.IsDirectory) continue;

            string key = NormalizeTarKey(item.Key);
            string name = Path.GetFileName(key);

            bool nameOk = nameMatches(name);

            if (contentMatches == null)
            {
                if (nameOk) found.Add(new ArchiveNode(name, key, false, item.Size, item.LastModifiedTime));
                SkipEntryIfSolid(archive, item);
                continue;
            }

            if (!nameOk || item.Size > maxContentSize)
            {
                SkipEntryIfSolid(archive, item);
                continue;
            }

            using var entryStream = item.OpenEntryStream();

            bool matched = contentMatches(entryStream);

            // The search may have stopped on the first matching line
            if (archive.IsSolid) entryStream.CopyTo(Stream.Null);

            if (matched) found.Add(new ArchiveNode(name, key, false, item.Size, item.LastModifiedTime));
        }

        return found;
    }

    private static DateTime? NullableTime(TarEntry entry) =>
        entry.ModificationTime == default ? null : entry.ModificationTime.UtcDateTime;

    public static List<ArchiveNode> ListChildren(string archivePath, string subPath)
    {
        string prefix = NormalizeDir(subPath);
        var folders = new Dictionary<string, ArchiveNode>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ArchiveNode>();

        foreach (var entry in GetIndex(archivePath).Entries)
        {
            string key = entry.Key;
            if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            string rest = key.Substring(prefix.Length).TrimStart('/');
            if (rest.Length == 0 || rest == "." || rest == "..") continue;

            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                if (entry.IsDirectory)
                    folders.TryAdd(rest, new ArchiveNode(rest, prefix + rest, true, 0, entry.Modified));
                else
                    files.Add(new ArchiveNode(rest, prefix + rest, false, entry.Size, entry.Modified));
            }
            else
            {
                string folderName = rest.Substring(0, slash);
                if (folderName.Length == 0 || folderName == "." || folderName == "..") continue;
                folders.TryAdd(folderName, new ArchiveNode(folderName, prefix + folderName, true, 0, null));
            }
        }

        var result = new List<ArchiveNode>(folders.Count + files.Count);
        result.AddRange(folders.Values);
        result.AddRange(files);
        return result;
    }

    // Callback added to track progress during full extraction
    public static void ExtractAll(string archivePath, string destDir, Func<string, bool>? confirmOverwrite = null, Action<string, long, long>? onProgress = null)
        => ExtractFolder(archivePath, "", destDir, confirmOverwrite, onProgress);

    // =========================================================================
    // CRITICAL FIX: Safe Skip for Solid Archives
    // SharpCompress throws "unpacked file size does not match header: expected X found 0"
    // if you skip entries inside a solid block (7z/Rar) without reading their streams.
    // This helper forces the decoder to decompress and discard the unwanted data
    // into Stream.Null, keeping the solid block perfectly in sync without crashing.
    // =========================================================================
    private static void SkipEntryIfSolid(IArchive archive, IArchiveEntry entry)
    {
        if (!entry.IsDirectory && archive.IsSolid)
        {
            using var skipStream = entry.OpenEntryStream();
            skipStream.CopyTo(Stream.Null);
        }
    }

    // onProgress reports byte level progress, so a batch pulled out of one
    // archive drives the progress bars the same way a folder does
    public static int ExtractFiles(
        string archivePath,
        IEnumerable<KeyValuePair<string, string>> keyToDestination,
        Func<string, bool>? confirmOverwrite = null,
        Action<string, long, long>? onProgress = null)
    {
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in keyToDestination)
            wanted[NormalizeTarKey(pair.Key)] = pair.Value;

        if (wanted.Count == 0) return 0;

        int written = 0;

        if (IsCompressedTar(archivePath))
        {
            using var gzip = new GZipStream(OpenArchiveStream(archivePath), CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            TarEntry? entry;
            // CRITICAL: copyData must be true for extraction, otherwise DataStream is null
            while ((entry = tar.GetNextEntry(copyData: true)) != null)
            {
                if (IsTarDirectory(entry)) continue;

                string key = NormalizeTarKey(entry.Name);
                if (!wanted.TryGetValue(key, out string? destPath))
                    continue;

                wanted.Remove(key);

                if (File.Exists(destPath) && confirmOverwrite != null && !confirmOverwrite(destPath))
                {
                    onProgress?.Invoke(Path.GetFileName(destPath), entry.Length, entry.Length);
                    continue;
                }

                WriteTarEntry(entry, destPath, onProgress);
                written++;
            }

            return written;
        }

        using var stream = OpenArchiveStream(archivePath);
        using var archive = ArchiveFactory.OpenArchive(stream);

        foreach (var item in archive.Entries)
        {
            if (item.Key is null || item.IsDirectory) continue;

            string key = NormalizeTarKey(item.Key);

            // If the file is not selected, we MUST safely skip it to keep solid archives in sync
            if (!wanted.TryGetValue(key, out string? destPath))
            {
                SkipEntryIfSolid(archive, item);
                continue;
            }

            wanted.Remove(key);

            if (File.Exists(destPath) && confirmOverwrite != null && !confirmOverwrite(destPath))
            {
                SkipEntryIfSolid(archive, item);

                // The totals were counted with this file in them, so the counters
                // still have to move past it
                onProgress?.Invoke(Path.GetFileName(destPath), item.Size, item.Size);

                // Only abort early if the archive is not solid. If it is solid,
                // aborting is fine because we are destroying the decoder anyway.
                if (wanted.Count == 0) break;
                continue;
            }

            WriteEntry(item, destPath, onProgress);
            written++;

            if (wanted.Count == 0) break;
        }

        return written;
    }

    public static void ExtractFile(string archivePath, string entryKey, string destFilePath)
    {
        int written = ExtractFiles(
            archivePath,
            new[] { new KeyValuePair<string, string>(entryKey, destFilePath) });

        if (written == 0)
            throw new FileNotFoundException("Entry not found in archive: " + entryKey);
    }

    private static string NormalizeDir(string subPath)
    {
        if (string.IsNullOrEmpty(subPath)) return "";
        string s = NormalizeTarKey(subPath);
        return s.Length == 0 ? "" : s + "/";
    }

    private const int ExtractBufferSize = 81920;

    // =========================================================================
    // onProgress reports (file name, bytes written so far, total size) while the
    // entry is being written, not once after it is done.
    //
    // A single CopyTo told the caller nothing until the file was finished, so the
    // per-file progress bar sat at 100% for the whole extraction — and a 300 MB
    // driver payload inside an installer looked frozen.
    // =========================================================================
    private static void WriteEntry(IArchiveEntry entry, string destPath, Action<string, long, long>? onProgress = null)
    {
        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var entryStream = entry.OpenEntryStream();
        using var outFile = File.Create(destPath);

        CopyEntryStream(entryStream, outFile, Path.GetFileName(destPath), entry.Size, onProgress);
    }

    private static void WriteTarEntry(TarEntry entry, string destPath, Action<string, long, long>? onProgress)
    {
        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var outFile = File.Create(destPath);
        if (entry.DataStream != null)
            CopyEntryStream(entry.DataStream, outFile, Path.GetFileName(destPath), entry.Length, onProgress);
    }

    private static void CopyEntryStream(
        Stream entryStream, Stream outFile, string name, long total, Action<string, long, long>? onProgress)
    {
        if (onProgress == null)
        {
            entryStream.CopyTo(outFile);
            return;
        }

        onProgress(name, 0, total);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(ExtractBufferSize);
        try
        {
            long copied = 0;
            int read;

            while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                outFile.Write(buffer, 0, read);
                copied += read;
                onProgress(name, copied, total);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string SafeCombine(string destDir, string relativeKey)
    {
        string full = Path.GetFullPath(Path.Combine(destDir, relativeKey.Replace('/', Path.DirectorySeparatorChar)));
        string root = Path.GetFullPath(destDir);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Archive entry escapes target directory: " + relativeKey);

        return full;
    }

    public static (string? ArchivePath, string InternalPath) ParseVirtualPath(string fullPath)
    {
        string normalized = fullPath.TrimEnd('\\');
        if (normalized.Length == 0) return (null, fullPath);

        int searchFrom = 0;
        while (true)
        {
            int sep = normalized.IndexOf('\\', searchFrom);
            string candidate = sep < 0 ? normalized : normalized.Substring(0, sep);

            if (IsArchiveFile(candidate))
            {
                string internalPath = sep < 0
                    ? ""
                    : normalized.Substring(sep + 1).Replace('\\', '/');

                return (candidate, NormalizeTarKey(internalPath));
            }

            if (sep < 0) return (null, fullPath);
            searchFrom = sep + 1;
        }
    }

    // Callback added to track progress during full extraction
    public static void ExtractFolder(string archivePath, string subPath, string destDir,
        Func<string, bool>? confirmOverwrite = null, Action<string, long, long>? onProgress = null)
    {
        string prefix = NormalizeDir(subPath);

        if (IsCompressedTar(archivePath))
        {
            using var gzip = new GZipStream(OpenArchiveStream(archivePath), CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            TarEntry? entry;
            // CRITICAL: copyData must be true for extraction, otherwise DataStream is null
            while ((entry = tar.GetNextEntry(copyData: true)) != null)
            {
                string key = NormalizeTarKey(entry.Name);
                if (key.Length == 0) continue;
                if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string rest = key.Substring(prefix.Length).TrimStart('/');
                if (rest.Length == 0) continue;

                string destPath = SafeCombine(destDir, rest);

                if (IsTarDirectory(entry))
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                if (File.Exists(destPath) && confirmOverwrite != null && !confirmOverwrite(destPath))
                {
                    onProgress?.Invoke(Path.GetFileName(destPath), entry.Length, entry.Length);
                    continue;
                }

                WriteTarEntry(entry, destPath, onProgress);
            }

            return;
        }

        using var stream = OpenArchiveStream(archivePath);
        using var archive = ArchiveFactory.OpenArchive(stream);

        foreach (var item in archive.Entries)
        {
            if (item.Key is null) continue;

            string key = NormalizeTarKey(item.Key);

            // Skip entries outside the requested folder safely
            if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                SkipEntryIfSolid(archive, item);
                continue;
            }

            string rest = key.Substring(prefix.Length).TrimStart('/');
            if (rest.Length == 0)
            {
                SkipEntryIfSolid(archive, item);
                continue;
            }

            string destPath = SafeCombine(destDir, rest);

            if (item.IsDirectory)
            {
                // Not reported: a folder is not one of the files the totals counted
                Directory.CreateDirectory(destPath);
                continue;
            }

            if (File.Exists(destPath) && confirmOverwrite != null && !confirmOverwrite(destPath))
            {
                SkipEntryIfSolid(archive, item);
                onProgress?.Invoke(Path.GetFileName(destPath), item.Size, item.Size);
                continue;
            }

            WriteEntry(item, destPath, onProgress);
        }
    }
}
