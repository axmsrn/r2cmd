using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace R2Cmd;

// =============================================================================
// FILE ICONS THROUGH THE WINDOWS SYSTEM IMAGE LIST
//
// Windows keeps every distinct file icon exactly once, in the system image list
// owned by shell32, and identifies it by an integer index. Hundreds of
// executables with the stock application icon all map to the same index. This
// is what Total Commander and Explorer are built on.
//
// The previous version asked the shell for an HICON per file, copied its pixels
// into a new BitmapSource and cached that bitmap per FILE. A folder like
// System32 therefore produced hundreds of identical bitmaps, each one alive in
// three places (managed wrapper, WIC pixel buffer, render-thread copy), at
// 32x32 although the list draws them at 16x16.
//
// Now:
//   1. The shell is asked for the icon INDEX (SHGFI_SYSICONINDEX). That is an
//      int, and it is what gets cached per file and per extension.
//   2. A bitmap is created once per distinct index, and shared by every row
//      that shows that icon.
//   3. The bitmap is taken at the size the list actually needs: the small
//      system icon (16 px at 100 % DPI) unless the UI zoom or DPI calls for more.
//
// Why a WPF bitmap at all instead of drawing straight from the image list the
// way Total Commander does: ImageList_Draw paints onto a GDI device context,
// and WPF rows have none. Converting each distinct icon once is the closest a
// WPF list can get. The flat ImageList_* functions are deliberately not used
// either: without a comctl32 v6 activation context they bind to comctl32 v5,
// which must not be handed the shell's v6 image list. Asking SHGetFileInfo for
// the icon of the file that produced the index gives the same pixels safely.
// =============================================================================
public static partial class IconService
{
    public static bool Enabled = true;

    // ---------------------------------------------------------------------
    // Size classes: the two system image lists SHGetFileInfo can hand out
    // ---------------------------------------------------------------------
    private const int SizeSmall = 0;    // SM_CXSMICON, 16 px at 100 % DPI
    private const int SizeLarge = 1;    // SM_CXICON, 32 px at 100 % DPI
    private const int SizeClassCount = 2;

    // Cached index meaning "the shell has no icon for this", so it is not asked again
    private const int NoIcon = -1;

    private const string NoExtKey = "<none>";

    // A plain array, matched as a span: no string is allocated per file to test it
    private static readonly string[] s_perFileExtensions =
        { ".exe", ".scr", ".lnk", ".ico", ".cur", ".ani", ".msc", ".cpl" };

    // ---------------------------------------------------------------------
    // Caches. All of them hold ints except the last one, which holds one
    // bitmap per distinct icon and size class.
    // ---------------------------------------------------------------------

    // Extension -> system image list index. Deterministic: SHGFI_USEFILEATTRIBUTES
    // never touches the file, so a miss is remembered as NoIcon.
    private static readonly ConcurrentDictionary<string, int> s_extIndex =
        new(StringComparer.OrdinalIgnoreCase);

    // Span view over the same dictionary: the UI-thread fast path looks an
    // extension up without allocating a substring for it
    private static readonly ConcurrentDictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> s_extIndexBySpan =
        s_extIndex.GetAlternateLookup<ReadOnlySpan<char>>();

    // =========================================================================
    // Per-file icons are keyed by name, size and timestamp — deliberately NOT by
    // path. A move changes only the path, so the index learned while the file
    // was still in the source folder is a hit the moment it appears in the
    // destination. The stamp keeps the key honest the other way: a different
    // build dropped over an old file differs in size or time and misses.
    // =========================================================================
    private readonly record struct IconKey(string Name, long Ticks, long Size);

    private static readonly ConcurrentDictionary<IconKey, int> s_fileIndex = new();

    // Insertion order, used to evict the oldest entries instead of wiping all
    private static readonly ConcurrentQueue<IconKey> s_fileOrder = new();

    // Tracked separately: ConcurrentDictionary.Count takes every lock in the table
    private static int s_fileIndexCount;

    // Entries are ints now, so the cap can be generous
    private const int FileIndexLimit = 16384;

    // Index -> bitmap, one dictionary per size class. Bounded by the number of
    // distinct icons on the machine, not by the number of files seen.
    private static readonly ConcurrentDictionary<int, ImageSource>[] s_indexBitmaps =
        CreateIndexBitmapCaches();

    private static ConcurrentDictionary<int, ImageSource>[] CreateIndexBitmapCaches()
    {
        var caches = new ConcurrentDictionary<int, ImageSource>[SizeClassCount];
        for (int i = 0; i < caches.Length; i++) caches[i] = new ConcurrentDictionary<int, ImageSource>();
        return caches;
    }

    private const int BatchSize = 128;

    // =========================================================================
    // Delays before asking the shell again about a file it could not answer yet.
    //
    // A file written a moment ago often has no icon: the icon handler has not
    // been loaded, and on a large executable Defender is still scanning it.
    // With index lookups the shell usually answers with the GENERIC index for
    // the extension instead of failing, so a generic answer for a per-file type
    // is shown immediately but not remembered, and asked again later. After the
    // last retry it is accepted: plenty of executables really have no icon.
    // =========================================================================
    private static readonly int[] RetryDelaysMs = { 1200, 3500, 8000 };

    // =========================================================================
    // ONE QUEUE, TWO CONSUMERS
    //
    // The watcher calls QueueLoad once per created file, so a thread per call
    // turned a folder move into hundreds of threads. Work goes through one queue
    // drained by at most two ThreadPool tasks: two, because a shell call on an
    // executable that Defender is scanning blocks for seconds and must not stall
    // everything queued behind it. The pool is MTA, which SHGetFileInfo handles;
    // an STA worker would have to pump messages.
    // =========================================================================
    private sealed class IconJob
    {
        public required List<FileEntry> Items { get; init; }
        public required bool VirtualEntries { get; init; }
        public required int RequestId { get; init; }
        public required Func<int> CurrentRequestId { get; init; }
        public required Dispatcher Dispatcher { get; init; }
        public required int Attempt { get; init; }
        public required int SizeClass { get; init; }
    }

    private static readonly ConcurrentQueue<IconJob> s_queue = new();

    private const int MaxWorkers = 2;
    private static int s_workers;

    // =========================================================================
    // PUBLIC API (unchanged signatures)
    // =========================================================================

    public static void QueueLoad(
        IReadOnlyList<FileEntry> items,
        bool virtualEntries,
        int requestId,
        Func<int> currentRequestId,
        Dispatcher dispatcher)
    {
        if (!Enabled || items.Count == 0) return;

        int sizeClass = CurrentSizeClass();

        // Anything already known is applied right here, synchronously, before the
        // list is rendered. Going through a worker for known icons drew a frame
        // with no icons at all, which looked like the pane blinking on refresh.
        List<FileEntry>? uncached = null;

        foreach (var entry in items)
        {
            if (!TryApplyCachedIcon(entry, virtualEntries, sizeClass))
                (uncached ??= new List<FileEntry>()).Add(entry);
        }

        if (uncached == null) return;

        Enqueue(new IconJob
        {
            Items = uncached,
            VirtualEntries = virtualEntries,
            RequestId = requestId,
            CurrentRequestId = currentRequestId,
            Dispatcher = dispatcher,
            Attempt = 0,
            SizeClass = sizeClass
        });
    }

    /// <summary>Drops every cached icon, so the next listing asks the shell again.</summary>
    public static void Clear()
    {
        s_extIndex.Clear();
        s_fileIndex.Clear();
        foreach (var cache in s_indexBitmaps) cache.Clear();

        while (s_fileOrder.TryDequeue(out _)) { }

        Volatile.Write(ref s_fileIndexCount, 0);
    }

    // =========================================================================
    // SIZE SELECTION (UI thread)
    //
    // The pane draws icons at AppFileIconSize device-independent pixels, which
    // ZoomManager rescales. Multiplied by the DPI scale this is the number of
    // physical pixels needed. The small system list is used whenever it is big
    // enough; downscaling a 32 px icon to 16 px with LowQuality scaling is what
    // made icons look blurry, and it cost four times the memory.
    // =========================================================================
    private static readonly int s_smallIconPx = Math.Max(16, GetSystemMetrics(SM_CXSMICON));

    private static int CurrentSizeClass()
    {
        double logical = 16;
        double scale = 1.0;

        var app = Application.Current;
        if (app != null)
        {
            if (app.TryFindResource("AppFileIconSize") is double size && size > 0)
                logical = size;

            if (app.MainWindow is Visual main)
            {
                try { scale = VisualTreeHelper.GetDpi(main).DpiScaleX; }
                catch { }
            }
        }

        return logical * scale <= s_smallIconPx + 0.5 ? SizeSmall : SizeLarge;
    }

    // =========================================================================
    // UI THREAD FAST PATH
    //
    // Returns true when the entry needs no background work: it already has an
    // icon, it is drawn by XAML as a vector, or the caches already have the
    // answer. Pure dictionary lookups, no shell calls.
    // =========================================================================
    private static bool TryApplyCachedIcon(FileEntry entry, bool virtualEntries, int sizeClass)
    {
        if (entry.Icon != null) return true;
        if (IsVectorDrawn(entry)) return true;

        int index;

        if (!virtualEntries && NeedsPerFileLookup(entry.Name))
        {
            if (!s_fileIndex.TryGetValue(FileIdentityKey(entry), out index)) return false;
        }
        else
        {
            if (!s_extIndexBySpan.TryGetValue(ExtensionSpan(entry.Name), out index)) return false;
            if (index == NoIcon) return true;
        }

        if (!s_indexBitmaps[sizeClass].TryGetValue(index, out var bitmap)) return false;

        entry.Icon = bitmap;
        return true;
    }

    // Folders, archives, ".." and custom network items are vectors in XAML.
    // Asking Windows for their icons would waste both time and memory.
    private static bool IsVectorDrawn(FileEntry entry) =>
        entry.Name == ".." ||
        (!string.IsNullOrEmpty(entry.IconType) && entry.IconType != "Default") ||
        entry.IsFolder ||
        entry.IsArchive;

    // =========================================================================
    // WORKERS
    // =========================================================================

    private static void Enqueue(IconJob job)
    {
        s_queue.Enqueue(job);
        TryStartWorker();
    }

    private static void TryStartWorker()
    {
        while (true)
        {
            int running = Volatile.Read(ref s_workers);
            if (running >= MaxWorkers) return;

            if (Interlocked.CompareExchange(ref s_workers, running + 1, running) == running)
            {
                Task.Run(Drain);
                return;
            }
        }
    }

    private static void Drain()
    {
        try
        {
            while (s_queue.TryDequeue(out var job)) Process(job);
        }
        finally
        {
            Interlocked.Decrement(ref s_workers);

            // A job enqueued between the last dequeue and the decrement would
            // otherwise sit there with nobody to pick it up
            if (!s_queue.IsEmpty) TryStartWorker();
        }
    }

    private static void Process(IconJob job)
    {
        var pending = new List<KeyValuePair<FileEntry, ImageSource>>(BatchSize);
        List<FileEntry>? retry = null;

        foreach (var entry in job.Items)
        {
            if (job.CurrentRequestId() != job.RequestId) return;

            var (icon, askAgain) = Resolve(entry, job);

            if (askAgain) (retry ??= new List<FileEntry>()).Add(entry);

            if (icon == null) continue;

            pending.Add(new(entry, icon));
            if (pending.Count >= BatchSize) Flush(pending, job);
        }

        if (pending.Count > 0) Flush(pending, job);

        if (retry != null) ScheduleRetry(job, retry);
    }

    private static void Flush(List<KeyValuePair<FileEntry, ImageSource>> batch, IconJob job)
    {
        var snapshot = batch.ToArray();
        batch.Clear();

        job.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (job.CurrentRequestId() != job.RequestId) return;

            // A retry may replace the generic icon shown earlier; ReferenceEquals
            // in FileEntry.Icon keeps an unchanged bitmap from notifying again
            foreach (var pair in snapshot)
                pair.Key.Icon = pair.Value;
        }), DispatcherPriority.Background);
    }

    private static void ScheduleRetry(IconJob job, List<FileEntry> entries)
    {
        var retry = new IconJob
        {
            Items = entries,
            VirtualEntries = job.VirtualEntries,
            RequestId = job.RequestId,
            CurrentRequestId = job.CurrentRequestId,
            Dispatcher = job.Dispatcher,
            Attempt = job.Attempt + 1,
            SizeClass = job.SizeClass
        };

        int delay = RetryDelaysMs[job.Attempt];

        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            if (retry.CurrentRequestId() == retry.RequestId) Enqueue(retry);
        }, null, delay, Timeout.Infinite);
    }

    // =========================================================================
    // RESOLUTION (worker threads)
    // =========================================================================

    private static (ImageSource? Icon, bool AskAgain) Resolve(FileEntry entry, IconJob job)
    {
        if (IsVectorDrawn(entry)) return (null, false);

        if (!job.VirtualEntries && NeedsPerFileLookup(entry.Name))
            return ResolvePerFile(entry, job);

        // By extension. No File.Exists guard anywhere: the entry came out of a
        // directory listing, and on a network share that check is a round trip.
        int index = GetExtensionIndex(entry.Name);
        if (index == NoIcon) return (null, false);

        return (GetBitmap(index, job.SizeClass, ProbeName(entry.Name), byAttributes: true), false);
    }

    private static (ImageSource? Icon, bool AskAgain) ResolvePerFile(FileEntry entry, IconJob job)
    {
        var key = FileIdentityKey(entry);

        if (s_fileIndex.TryGetValue(key, out int known))
            return (GetBitmap(known, job.SizeClass, entry.FullPath, byAttributes: false), false);

        bool canRetry = job.Attempt < RetryDelaysMs.Length;
        int genericIndex = GetExtensionIndex(entry.Name);
        int index = QueryIndex(entry.FullPath, byAttributes: false);

        if (index == NoIcon)
        {
            // The shell could not answer at all (file gone, handler not loaded).
            // Show the generic icon for now; a failure is never remembered.
            var generic = genericIndex == NoIcon
                ? null
                : GetBitmap(genericIndex, job.SizeClass, ProbeName(entry.Name), byAttributes: true);

            return (generic, canRetry);
        }

        // A generic answer for a type that normally carries its own icon may
        // mean "not ready yet". Show it, do not remember it, ask again later.
        if (index == genericIndex && canRetry)
            return (GetBitmap(index, job.SizeClass, entry.FullPath, byAttributes: false), true);

        RememberFileIndex(key, index);
        return (GetBitmap(index, job.SizeClass, entry.FullPath, byAttributes: false), false);
    }

    private static int GetExtensionIndex(string name)
    {
        var ext = ExtensionSpan(name);
        if (s_extIndexBySpan.TryGetValue(ext, out int cached)) return cached;

        int index = QueryIndex(ProbeName(name), byAttributes: true);
        s_extIndex.TryAdd(ext.ToString(), index);
        return index;
    }

    // One bitmap per distinct icon and size. The pixels are taken from the file
    // (or extension) that produced the index, which is the same icon the system
    // image list holds for it.
    private static ImageSource? GetBitmap(int index, int sizeClass, string source, bool byAttributes)
    {
        var cache = s_indexBitmaps[sizeClass];
        if (cache.TryGetValue(index, out var cached)) return cached;

        var loaded = LoadIconBitmap(source, byAttributes, sizeClass);
        if (loaded == null) return null;

        // Two workers may race on the same index; both get the stored instance
        return cache.GetOrAdd(index, loaded);
    }

    private static void RememberFileIndex(IconKey key, int index)
    {
        if (!s_fileIndex.TryAdd(key, index)) return;

        s_fileOrder.Enqueue(key);
        if (Interlocked.Increment(ref s_fileIndexCount) > FileIndexLimit) TrimFileIndex();
    }

    // Evicts the oldest quarter rather than clearing everything: a full wipe
    // dropped the entries of the folder on screen along with the rest.
    private static void TrimFileIndex()
    {
        int toEvict = FileIndexLimit / 4;

        for (int i = 0; i < toEvict && s_fileOrder.TryDequeue(out var old); i++)
        {
            if (s_fileIndex.TryRemove(old, out _)) Interlocked.Decrement(ref s_fileIndexCount);
        }
    }

    // =========================================================================
    // NAME HELPERS
    // =========================================================================

    private static bool NeedsPerFileLookup(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0) return false;

        var ext = name.AsSpan(dot);

        foreach (string candidate in s_perFileExtensions)
        {
            if (ext.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static ReadOnlySpan<char> ExtensionSpan(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? NoExtKey.AsSpan() : name.AsSpan(dot);
    }

    // With SHGFI_USEFILEATTRIBUTES the shell only looks at the extension. A short
    // synthetic name avoids handing it an arbitrary long or odd real one.
    private static string ProbeName(string name)
    {
        var ext = ExtensionSpan(name);
        return ext.SequenceEqual(NoExtKey.AsSpan()) ? "file" : string.Concat("file", ext);
    }

    private static IconKey FileIdentityKey(FileEntry entry) =>
        new(entry.Name, entry.Modified?.Ticks ?? 0, entry.Size);

    // =========================================================================
    // WIN32
    //
    // LibraryImport with a blittable struct: the old DllImport marshalled two
    // ByValTStr strings (display name and type name) on every single call only
    // to throw them away.
    // =========================================================================

    private static readonly uint s_infoSize = (uint)Unsafe.SizeOf<SHFILEINFOW>();

    // Returns the index of the icon in the system image list, or NoIcon.
    // The HIMAGELIST returned here belongs to the shell and must not be freed.
    private static int QueryIndex(string path, bool byAttributes)
    {
        var info = default(SHFILEINFOW);

        uint flags = SHGFI_SYSICONINDEX | SHGFI_SMALLICON;
        uint attributes = 0;

        if (byAttributes)
        {
            flags |= SHGFI_USEFILEATTRIBUTES;
            attributes = FILE_ATTRIBUTE_NORMAL;
        }

        IntPtr imageList = SHGetFileInfo(path, attributes, ref info, s_infoSize, flags);
        return imageList == IntPtr.Zero ? NoIcon : info.iIcon;
    }

    private static ImageSource? LoadIconBitmap(string path, bool byAttributes, int sizeClass)
    {
        var info = default(SHFILEINFOW);

        uint flags = SHGFI_ICON | (sizeClass == SizeSmall ? SHGFI_SMALLICON : SHGFI_LARGEICON);
        uint attributes = 0;

        if (byAttributes)
        {
            flags |= SHGFI_USEFILEATTRIBUTES;
            attributes = FILE_ATTRIBUTE_NORMAL;
        }

        IntPtr result = SHGetFileInfo(path, attributes, ref info, s_infoSize, flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // Frozen: shareable across threads and rows, no change tracking
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const int SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        public fixed char szDisplayName[260];
        public fixed char szTypeName[80];
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);
}
