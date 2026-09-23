using System.IO;
using System.IO.Enumeration;
using R2Cmd.Controls;

namespace R2Cmd;

public partial class MainWindow
{
    // =========================================================================
    // FOLDER SIZES — background work, never a modal wait
    //
    // Counting a node_modules tree over SSH takes minutes. It used to run under
    // SetBusy, which put the wait cursor on the whole desktop and made _busy
    // swallow every hotkey, so the only option was to sit and watch.
    //
    // Now it runs detached: no busy flag, no cursor, progress in its own status
    // field. A scan keeps running after the user leaves the folder, so that on
    // coming back (typically to an SSH folder) the result is already there.
    //
    // Finished sizes are kept by path for the session. The rows of a folder are
    // new objects after every listing, so the result is written to whichever
    // rows show that path when the scan ends, and put on the rows again each
    // time a pane lists a folder. Ctrl+R forgets the sizes inside the folder
    // being refreshed, which is the way to get fresh numbers.
    //
    // Scans in flight are remembered too, so a second Space on the same folder,
    // or the same folder open in both panes, does not walk the tree twice at
    // once. Touched from the UI thread only.
    // =========================================================================
    private readonly HashSet<string> _sizeScansRunning = new(StringComparer.OrdinalIgnoreCase);

    // Esc stops every scan in flight: they all share this token, and a fresh
    // source replaces it for the next scans
    private CancellationTokenSource _sizeCts = new();

    // =========================================================================
    // Esc answers at once. A stopped SSH scan still needs a few seconds to
    // close its connection; the spinners used to keep turning until then.
    // Now the rows are reset right here, and the scans that are still winding
    // down end silently: a cancelled scan publishes nothing (see the finally
    // blocks), so it cannot touch a row that a new Space has started again.
    // =========================================================================
    private void CancelFolderSizes()
    {
        _sizeCts.Cancel();
        _sizeCts = new CancellationTokenSource();

        foreach (var path in _sizeScansRunning.ToList())
            PublishFolderSize(path, null);
        _sizeScansRunning.Clear();

        _sizeJobsRunning = 0;
        SetBackgroundStatus(null);
        SetStatus("Folder size calculation stopped.");
    }

    // Exact comparison: on an SSH server "Src" and "src" are two folders
    private readonly Dictionary<string, FolderSize> _folderSizes = new(StringComparer.Ordinal);

    // =========================================================================
    // Files AND bytes from one walk. Space on a folder used to start two scans
    // of the same tree at once: this one for the Size column and a second one
    // in the pane for "Selected: N files". On a server whose disk is the
    // bottleneck they slowed each other down. The pane now waits for this
    // result instead of walking the tree itself (FilePaneControl.SetFolderTotals).
    // =========================================================================
    private readonly record struct FolderSize(long Files, long Bytes);
    private int _sizeJobsRunning;

    // Collection to track items that are currently animating their size calculation
    private readonly HashSet<FileEntry> _sizeAnimatingEntries = new();
    private System.Windows.Threading.DispatcherTimer? _sizeAnimTimer;

    // Braille spinner frames for modern technical UI feedback
    // Padded with four spaces on the left to visually center it under the 'I' in "<DIR>" (proportional fonts require more spaces)
    private static readonly string[] _brailleFrames = { "    ⠋    ", "    ⠙    ", "    ⠹    ", "    ⠸    ", "    ⠼    ", "    ⠴    ", "    ⠦    ", "    ⠧    ", "    ⠇    ", "    ⠏    " };
    private int _sizeAnimFrame;

    // Starts the size calculation animation timer if it is not already running
    private void EnsureSizeAnimationRunning()
    {
        if (_sizeAnimTimer != null) return;

        // Braille spinner requires a faster update rate for smooth rotation
        _sizeAnimTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };

        _sizeAnimTimer.Tick += (s, e) =>
        {
            // Auto-clean entries that finished calculating (SizeCalculating became false)
            _sizeAnimatingEntries.RemoveWhere(entry => !entry.SizeCalculating);

            if (_sizeAnimatingEntries.Count == 0)
            {
                _sizeAnimTimer.Stop();
                _sizeAnimTimer = null;
                _sizeAnimFrame = 0;
                return;
            }

            _sizeAnimFrame = (_sizeAnimFrame + 1) % _brailleFrames.Length;
            string text = _brailleFrames[_sizeAnimFrame];

            foreach (var entry in _sizeAnimatingEntries)
            {
                entry.SizeAnimationText = text;
            }
        };

        _sizeAnimTimer.Start();
    }

    private void QueueFolderSize(FileEntry entry)
    {
        if (!entry.IsFolder || entry.Name == "..") return;

        string path = entry.FullPath;

        if (!_sizeScansRunning.Add(path))
        {
            entry.SizeCalculating = true;
            _sizeAnimatingEntries.Add(entry);
            EnsureSizeAnimationRunning();
            return;
        }

        _ = RunFolderSizeAsync(entry, path);
    }

    private async Task RunFolderSizeAsync(FileEntry entry, string path)
    {
        entry.SizeCalculating = true;
        _sizeAnimatingEntries.Add(entry);
        EnsureSizeAnimationRunning();

        _sizeJobsRunning++;
        UpdateBackgroundStatus();

        var token = _sizeCts.Token;
        FolderSize? size = null;
        try
        {
            size = await Task.Run(() => CalculateFolderSize(path, token));
        }
        catch { }
        finally
        {
            // A cancelled scan was already reset by CancelFolderSizes, job
            // counter included; touching them now would undo that
            if (!token.IsCancellationRequested)
            {
                _sizeScansRunning.Remove(path);
                PublishFolderSize(path, size);

                _sizeJobsRunning--;
                UpdateBackgroundStatus();
            }
        }
    }

    private void QueueAllFolderSizes(FilePaneControl pane)
    {
        var targets = pane.Items
            .Where(e => e.IsFolder && e.Name != ".." && !e.SizeKnown && !e.SizeCalculating)
            .ToList();

        if (targets.Count == 0) return;

        _ = RunAllFolderSizesAsync(targets);
    }

    private async Task RunAllFolderSizesAsync(List<FileEntry> targets)
    {
        int done = 0;
        var token = _sizeCts.Token;

        foreach (var entry in targets)
        {
            if (token.IsCancellationRequested) break;

            string path = entry.FullPath;
            done++;

            if (!_sizeScansRunning.Add(path))
            {
                entry.SizeCalculating = true;
                _sizeAnimatingEntries.Add(entry);
                EnsureSizeAnimationRunning();
                continue;
            }

            entry.SizeCalculating = true;
            _sizeAnimatingEntries.Add(entry);
            EnsureSizeAnimationRunning();

            SetBackgroundStatus($"Folder sizes: {done} / {targets.Count}");

            FolderSize? size = null;
            try
            {
                size = await Task.Run(() => CalculateFolderSize(path, token));
            }
            catch { }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    _sizeScansRunning.Remove(path);
                    PublishFolderSize(path, size);
                }
            }
        }

        UpdateBackgroundStatus();
    }

    // =========================================================================
    // The result goes to every row that shows this folder NOW, in both panes,
    // not to the row the scan started from: after a reload or a return to the
    // folder that row is gone. Writing only to it also left a row started by
    // a second Space on a running scan spinning forever.
    // =========================================================================
    // null: unknown (failed, or stopped with Esc)
    private void PublishFolderSize(string path, FolderSize? size)
    {
        if (size is FolderSize known) _folderSizes[path] = known;

        foreach (var pane in new[] { leftPane, rightPane })
        {
            foreach (var entry in pane.Items)
            {
                if (!entry.IsFolder || !string.Equals(entry.FullPath, path, StringComparison.Ordinal)) continue;

                if (size is FolderSize result)
                {
                    entry.Size = result.Bytes;
                    pane.SetFolderTotals(entry, result.Files, result.Bytes);
                }

                entry.SizeKnown = size != null;
                entry.SizeCalculating = false;
            }
        }
    }

    // Called when a pane has listed a folder: sizes counted earlier reappear
    private void ApplyKnownFolderSizes(FilePaneControl pane)
    {
        if (_folderSizes.Count == 0) return;

        foreach (var entry in pane.Items)
        {
            if (entry.IsFolder && !entry.SizeKnown && _folderSizes.TryGetValue(entry.FullPath, out var known))
            {
                entry.Size = known.Bytes;
                entry.SizeKnown = true;
                pane.SetFolderTotals(entry, known.Files, known.Bytes);
            }
        }
    }

    // Ctrl+R: everything below the refreshed folder may have changed
    private void ForgetFolderSizesUnder(string folder)
    {
        string prefix = folder.TrimEnd('\\', '/');

        var stale = _folderSizes.Keys
            .Where(k => k.Length > prefix.Length &&
                        k.StartsWith(prefix, StringComparison.Ordinal) &&
                        k[prefix.Length] is '\\' or '/')
            .ToList();

        foreach (var key in stale) _folderSizes.Remove(key);
    }

    private void UpdateBackgroundStatus()
    {
        SetBackgroundStatus(_sizeJobsRunning switch
        {
            <= 0 => null,
            1 => "Calculating folder size...",
            _ => $"Calculating {_sizeJobsRunning} folder sizes..."
        });
    }

    // null means unknown: failed or stopped with Esc
    private static FolderSize? CalculateFolderSize(string path, CancellationToken token)
    {
        try
        {
            if (path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                var (remoteFiles, remoteBytes) = Providers.SshFileSystemProvider.RemoteSumTree(path, token);
                return new FolderSize(remoteFiles, remoteBytes);
            }

            long files = 0;
            long bytes = 0;
            var sizes = new FileSystemEnumerable<long>(
                path,
                (ref FileSystemEntry entry) => entry.Length,
                s_recursiveEnumOptions)
            {
                // Files only: folders are walked, not counted
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,

                // A junction belongs to its target, not to this folder
                ShouldRecursePredicate = (ref FileSystemEntry e) => !IsReparse(e.Attributes)
            };

            foreach (var size in sizes)
            {
                token.ThrowIfCancellationRequested();
                files++;
                bytes += size;
            }
            return new FolderSize(files, bytes);
        }
        catch { return null; }
    }

}
