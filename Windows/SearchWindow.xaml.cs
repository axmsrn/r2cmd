using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Enumeration;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace R2Cmd;

public partial class SearchWindow : Window
{
    public string? GoToDirectory { get; private set; }
    public string? GoToFile { get; private set; }
    public List<FileEntry>? ResultsToShow { get; private set; }
    public string SearchRoot { get; private set; } = "";

    private readonly ObservableCollection<FileEntry> _results;
    private CancellationTokenSource? _cts;
    private readonly AppSettings _settings;

    // Sorting state
    private string _sortColumn = "Name";
    private bool _sortAscending = true;

    // Selected items to limit the search scope (null = whole folder)
    private readonly List<FileEntry>? _selectedRoots;

    // --- Session persistence (in-memory) ---
    private class SearchSession
    {
        public string Path { get; set; } = "";
        public string Mask { get; set; } = "";
        public string Contains { get; set; } = "";
        public bool CaseSensitive { get; set; }
        public bool Regex { get; set; }
        public bool Archives { get; set; }
        public string Status { get; set; } = "";
        public string Stats { get; set; } = "";
        public string SortColumn { get; set; } = "Name";
        public bool SortAscending { get; set; } = true;
        public List<FileEntry> Results { get; set; } = new();
    }

    private static SearchSession? _lastSession;

    // Limits
    private const int MaxResults = 50_000;

    // Four readers: enough to keep an SSD busy, few enough that a spinning disk
    // is not thrown into constant seeking between files
    private static readonly int ContentSearchThreads = Math.Clamp(Environment.ProcessorCount, 2, 4);
    private const int BatchSize = 256;
    private const long MaxArchiveContentSize = 16L * 1024 * 1024;
    private const int BatchIntervalMs = 100;

    public SearchWindow(string startPath, IList<FileEntry>? selectedItems = null)
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        UpdateHistoryComboBox();
        UpdateContainsHistoryComboBox();

        // Keep only real items (no "..")
        _selectedRoots = selectedItems?
            .Where(e => e.Name != "..")
            .ToList();
        if (_selectedRoots != null && _selectedRoots.Count == 0)
            _selectedRoots = null;

        if (_lastSession != null)
        {
            chkCase.IsChecked = _lastSession.CaseSensitive;
            chkRegex.IsChecked = _lastSession.Regex;
            chkArchives.IsChecked = _lastSession.Archives;
            txtStats.Text = _lastSession.Stats;
            _sortColumn = _lastSession.SortColumn;
            _sortAscending = _lastSession.SortAscending;
            SearchRoot = _lastSession.Path;

            _results = new ObservableCollection<FileEntry>(_lastSession.Results);
            cmbMask.Text = _lastSession.Mask;
            txtPath.Text = startPath;

            if (_selectedRoots != null)
            {
                txtStatus.Text = $"Search limited to {_selectedRoots.Count} selected item(s). Ready.";
            }
            else
            {
                txtStatus.Text = !string.Equals(startPath, _lastSession.Path, StringComparison.OrdinalIgnoreCase)
                    ? "Showing previous results. Ready to search in new path."
                    : _lastSession.Status;
            }
        }
        else
        {
            txtPath.Text = startPath;
            txtStats.Text = "";
            _results = new ObservableCollection<FileEntry>();

            if (_settings.SearchHistory.Count > 0)
                cmbMask.SelectedIndex = 0;

            if (_selectedRoots != null)
                txtStatus.Text = $"Search limited to {_selectedRoots.Count} selected item(s). Ready.";
        }

        // Requirement: "Containing" is always empty on open
        cmbContains.Text = "";

        lvResults.ItemsSource = _results;

        // Helpers to clear text selection after history navigation
        Action fixMaskSelection = () =>
        {
            if (cmbMask.Template?.FindName("PART_EditableTextBox", cmbMask) is TextBox tb)
            {
                tb.SelectionLength = 0;
                tb.CaretIndex = tb.Text.Length;
            }
        };

        Action fixContainsSelection = () =>
        {
            if (cmbContains.Template?.FindName("PART_EditableTextBox", cmbContains) is TextBox tb)
            {
                tb.SelectionLength = 0;
                tb.CaretIndex = tb.Text.Length;
            }
        };

        Loaded += (_, _) =>
        {
            cmbMask.Focus();
            Dispatcher.InvokeAsync(() =>
            {
                if (cmbMask.Template?.FindName("PART_EditableTextBox", cmbMask) is TextBox tb)
                    tb.SelectAll();
            }, DispatcherPriority.ContextIdle);
        };

        // Down key opens dropdown + moves selection
        cmbMask.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && !cmbMask.IsDropDownOpen)
            {
                cmbMask.IsDropDownOpen = true;
                if (cmbMask.SelectedIndex < cmbMask.Items.Count - 1)
                    cmbMask.SelectedIndex++;
                e.Handled = true;
            }
        };

        cmbContains.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && !cmbContains.IsDropDownOpen)
            {
                cmbContains.IsDropDownOpen = true;
                if (cmbContains.SelectedIndex < cmbContains.Items.Count - 1)
                    cmbContains.SelectedIndex++;
                e.Handled = true;
            }
        };

        cmbMask.SelectionChanged += (_, _) => Dispatcher.InvokeAsync(fixMaskSelection, DispatcherPriority.ContextIdle);
        cmbMask.DropDownOpened += (_, _) => Dispatcher.InvokeAsync(fixMaskSelection, DispatcherPriority.ContextIdle);
        cmbContains.SelectionChanged += (_, _) => Dispatcher.InvokeAsync(fixContainsSelection, DispatcherPriority.ContextIdle);
        cmbContains.DropDownOpened += (_, _) => Dispatcher.InvokeAsync(fixContainsSelection, DispatcherPriority.ContextIdle);

        lvResults.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnColumnHeaderClick));
        ApplySort();
        UpdateColumnHeaders();

        // Disable features that don't make sense on remote (SSH)
        UpdateRemoteUiState(startPath, _selectedRoots);
    }
    private void UpdateRemoteUiState(string path, List<FileEntry>? selected)
    {
        bool isRemote = path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
                        || (selected != null && selected.Any(e => e.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)));

        if (isRemote)
        {
            chkArchives.IsChecked = false;
            chkArchives.Visibility = Visibility.Collapsed;   // completely hide
        }
        else
        {
            chkArchives.Visibility = Visibility.Visible;
            chkArchives.ToolTip = "Archives by extension, plus .exe installers already opened as archives";
        }
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme, useSurfaceColor: true);
        // Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme, useSurfaceColor: true);
    }


    protected override void OnClosing(CancelEventArgs e)
    {
        _cts?.Cancel();

        _lastSession = new SearchSession
        {
            Path = txtPath.Text,
            Mask = cmbMask.Text,
            Contains = cmbContains.Text,
            CaseSensitive = chkCase.IsChecked == true,
            Regex = chkRegex.IsChecked == true,
            Archives = chkArchives.IsChecked == true,
            Status = txtStatus.Text,
            Stats = txtStats.Text,
            SortColumn = _sortColumn,
            SortAscending = _sortAscending,
            Results = new List<FileEntry>(_results)
        };

        base.OnClosing(e);
    }

    // ===================== History helpers =====================

    private static void AddToHistory(IList<string> history, string item, int max)
    {
        history.Remove(item);
        history.Insert(0, item);
        while (history.Count > max)
            history.RemoveAt(history.Count - 1);
    }

    private void UpdateHistoryComboBox()
    {
        cmbMask.Items.Clear();
        foreach (var item in _settings.SearchHistory)
            cmbMask.Items.Add(item);
    }

    private void UpdateContainsHistoryComboBox()
    {
        cmbContains.Items.Clear();
        foreach (var item in _settings.SearchContainsHistory)
            cmbContains.Items.Add(item);
    }

    // ===================== Browse =====================

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a folder to search in",
            InitialDirectory = Directory.Exists(txtPath.Text) ? txtPath.Text : ""
        };

        if (dialog.ShowDialog() == true)
        {
            txtPath.Text = dialog.FolderName;
            btnSearch.Focus();
        }
    }

    // ===================== Search =====================

    private async void BtnSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            return;
        }

        string path = txtPath.Text.Trim();
        string inputMask = cmbMask.Text.Trim();

        // When limited to selection we still need a valid base path for display,
        // but the actual roots come from _selectedRoots.
        bool limitedToSelection = _selectedRoots != null && _selectedRoots.Count > 0;

        bool isRemotePath = path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);

        if (!limitedToSelection && !isRemotePath && !Directory.Exists(path))
        {
            MessageBox.Show("Start path does not exist or is not a local physical drive.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Support multiple masks: *.cs;*.xaml   *.cs *.xaml   cs xaml   etc.
        var masks = ParseSearchMasks(inputMask);
        string displayMask = string.IsNullOrEmpty(inputMask) ? "*" : inputMask;

        TextQuery? textQuery = null;
        string contains = cmbContains.Text.Trim();

        if (!string.IsNullOrEmpty(contains))
        {
            try
            {
                textQuery = new TextQuery(contains, chkRegex.IsChecked == true, chkCase.IsChecked == true);
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show($"Invalid regular expression:\n\n{ex.Message}", "Search",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AddToHistory(_settings.SearchContainsHistory, contains, 10);
        }

        bool searchArchives = chkArchives.IsChecked == true;

        bool isRemote = path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
                        || (_selectedRoots != null && _selectedRoots.Any(e => e.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)));

        if (isRemote)
            searchArchives = false;   // archives not supported on remote

        string historyEntry = string.IsNullOrEmpty(inputMask) ? "*" : inputMask;

        AddToHistory(_settings.SearchHistory, historyEntry, 15);
        _settings.Save();

        UpdateHistoryComboBox();
        cmbMask.Text = historyEntry;
        UpdateContainsHistoryComboBox();
        cmbContains.Text = contains;

        _results.Clear();
        SearchRoot = limitedToSelection ? path : path;
        ClearSort();

        _cts = new CancellationTokenSource();
        btnSearch.Content = "Stop";

        if (limitedToSelection)
        {
            txtStatus.Text = textQuery == null
                ? $"Searching in {_selectedRoots!.Count} selected item(s) for {displayMask}..."
                : $"Searching in {_selectedRoots!.Count} selected item(s) for {displayMask} containing \"{contains}\"...";
        }
        else
        {
            txtStatus.Text = textQuery == null
                ? $"Searching for {displayMask}..."
                : $"Searching for {displayMask} containing \"{contains}\"...";
        }

        txtStats.Text = "Scanning...";

        bool hitLimit = false;

        try
        {
            bool caseSensitive = chkCase.IsChecked == true;

            hitLimit = await Task.Run(() =>
                PerformSearch(path, masks, textQuery, searchArchives, _cts.Token, _selectedRoots, caseSensitive));

            txtStatus.Text = hitLimit
                ? $"Stopped at the {MaxResults:N0} result limit. Narrow the mask for a complete list."
                : $"Found {_results.Count} item(s).";
        }
        catch (OperationCanceledException)
        {
            txtStatus.Text = $"Search stopped. Found {_results.Count} item(s).";
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            btnSearch.Content = "Search";
            ApplySort();
        }
    }

    /// <summary>
    /// Parses the mask field into one or more simple expressions.
    /// Supports separators: ; | and space.
    ///
    /// Rules:
    /// - If a token contains no * and no ? → wrap with * *  (cs → *cs*)
    /// - If a token already contains * or ? → leave it exactly as written
    /// </summary>
    private static List<string> ParseSearchMasks(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new List<string> { "*" };

        var parts = input.Split(new[] { ';', '|', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = new List<string>();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            string mask = part;

            // No wildcards → treat as name fragment
            if (!mask.Contains('*') && !mask.Contains('?'))
                mask = "*" + mask + "*";

            result.Add(mask);
        }

        return result.Count > 0 ? result : new List<string> { "*" };
    }

    /// <summary>
    /// Walks the tree(s) once. Returns true if the result limit was hit.
    /// Supports both local paths and SSH (ssh://).
    /// When selectedRoots is provided, only those items are searched.
    /// Archives are only searched on local paths.
    /// </summary>
    private bool PerformSearch(string path, List<string> masks, TextQuery? textQuery,
                               bool searchArchives, CancellationToken token,
                               List<FileEntry>? selectedRoots = null,
                               bool caseSensitive = false)
    {
        var batch = new List<FileEntry>(BatchSize);
        var sinceFlush = System.Diagnostics.Stopwatch.StartNew();
        int total = 0;
        bool hitLimit = false;

        int scannedFiles = 0;
        int scannedFolders = 0;
        int totalTextMatches = 0;
        var searchDuration = System.Diagnostics.Stopwatch.StartNew();

        void UpdateStatsUI()
        {
            if (textQuery != null)
            {
                txtStats.Text =
                    $"Scanned: {scannedFiles:N0} files, {scannedFolders:N0} folders in {searchDuration.Elapsed.TotalSeconds:F2}s\n" +
                    $"Found {totalTextMatches:N0} matches in total.";
            }
            else
            {
                txtStats.Text =
                    $"Scanned: {scannedFiles:N0} files, {scannedFolders:N0} folders in {searchDuration.Elapsed.TotalSeconds:F2}s";
            }
        }

        void Flush(bool waitForUi = false)
        {
            if (batch.Count == 0) return;

            var chunk = batch.ToArray();
            batch.Clear();
            sinceFlush.Restart();

            void AddChunk()
            {
                bool wasEmpty = _results.Count == 0;
                foreach (var item in chunk)
                    _results.Add(item);

                if (wasEmpty)
                    FocusFirstResult();

                UpdateStatsUI();
            }

            if (waitForUi)
                Dispatcher.Invoke(AddChunk, DispatcherPriority.Background);
            else
                Dispatcher.InvokeAsync(AddChunk, DispatcherPriority.Background);
        }

        bool Add(FileEntry item)
        {
            batch.Add(item);
            total++;

            if (total >= MaxResults)
                return false;

            if (batch.Count >= BatchSize || sinceFlush.ElapsedMilliseconds >= BatchIntervalMs)
                Flush();

            return true;
        }

        bool NameMatches(string name)
        {
            foreach (var mask in masks)
            {
                if (FileSystemName.MatchesSimpleExpression(mask, name, ignoreCase: !caseSensitive))
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Process a single file (local or remote)
        // ------------------------------------------------------------------
        bool ProcessFile(FileEntry item)
        {
            if (!NameMatches(item.Name))
                return true; // continue

            if (textQuery != null)
            {
                if (item.IsFolder) return true;

                int matchCount = 0;

                if (item.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                {
                    // Remote file – stream content
                    try
                    {
                        var provider = Providers.FileSystemFactory.GetProvider(item.FullPath);
                        using var stream = provider.OpenReadAsync(item.FullPath, token).GetAwaiter().GetResult();
                        matchCount = TextSearcher.CountMatchesInStream(stream, textQuery, token);
                    }
                    catch
                    {
                        return true; // skip inaccessible remote file
                    }
                }
                else
                {
                    matchCount = TextSearcher.CountMatchesInFile(item.FullPath, textQuery, token);
                }

                if (matchCount == 0) return true;
                totalTextMatches += matchCount;
            }

            return Add(item);
        }

        // ------------------------------------------------------------------
        // Local recursive folder walk
        // ------------------------------------------------------------------
        bool ProcessLocalFolder(string folderPath)
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = 0
            };

            var enumerable = new FileSystemEnumerable<FileEntry>(
                folderPath,
                (ref FileSystemEntry entry) => new FileEntry
                {
                    Name = entry.FileName.ToString(),
                    FullPath = entry.ToFullPath(),
                    IsFolder = entry.IsDirectory,
                    Size = entry.IsDirectory ? 0 : entry.Length,
                    Modified = entry.LastWriteTimeUtc.LocalDateTime
                },
                options)
            {
                ShouldRecursePredicate = static (ref FileSystemEntry e) =>
                    (e.Attributes & FileAttributes.ReparsePoint) == 0,
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                {
                    if (e.IsDirectory) Interlocked.Increment(ref scannedFolders);
                    else Interlocked.Increment(ref scannedFiles);

                    if (searchArchives && !e.IsDirectory &&
                        ArchiveService.IsArchiveFile(e.ToFullPath()))
                        return true;

                    return NameMatches(e.FileName.ToString());
                }
            };

            // Guards the result batch and the limit flag once several threads add
            var sync = new object();

            // false: the result limit was reached
            bool HandleItem(FileEntry item)
            {
                token.ThrowIfCancellationRequested();

                if (searchArchives && !item.IsFolder && ArchiveService.IsArchiveFile(item.FullPath))
                {
                    var inner = SearchArchive(item.FullPath, NameMatches, textQuery, token,
                        count => Interlocked.Add(ref totalTextMatches, count));

                    lock (sync)
                    {
                        foreach (var hit in inner)
                        {
                            scannedFiles++;
                            if (hitLimit || !Add(hit))
                            {
                                hitLimit = true;
                                return false;
                            }
                        }
                    }
                }

                if (!NameMatches(item.Name))
                    return true;

                if (textQuery != null)
                {
                    if (item.IsFolder) return true;

                    // The expensive part, outside the lock: this is what runs in parallel
                    int matchCount = TextSearcher.CountMatchesInFile(item.FullPath, textQuery, token);
                    if (matchCount == 0) return true;

                    Interlocked.Add(ref totalTextMatches, matchCount);
                }

                lock (sync)
                {
                    if (hitLimit || !Add(item))
                    {
                        hitLimit = true;
                        return false;
                    }
                }

                return true;
            }

            try
            {
                if (textQuery == null)
                {
                    // Name search is bound by the directory walk itself; threads
                    // would only wait on each other
                    foreach (var item in enumerable)
                    {
                        if (!HandleItem(item)) return false;
                    }
                }
                else
                {
                    // =========================================================
                    // Content search reads and scans every candidate file, and
                    // that is where the time goes. The walk still runs on one
                    // thread (Parallel.ForEach pulls from the enumerator under
                    // its own lock); the file reading spreads over a few.
                    // Results arrive in completion order, not walk order; the
                    // list can be sorted by any column afterwards.
                    // =========================================================
                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = ContentSearchThreads,
                        CancellationToken = token
                    };

                    Parallel.ForEach(enumerable, parallelOptions, (item, state) =>
                    {
                        if (!HandleItem(item)) state.Stop();
                    });

                    if (hitLimit) return false;
                }
            }
            catch (OperationCanceledException) { throw; }

            // A cancellation observed inside a worker arrives wrapped
            catch (AggregateException) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
            catch { /* inaccessible path – skip */ }

            return true;
        }
        // ------------------------------------------------------------------
        // Fast remote search using the server-side `find` / `rg` commands
        // ------------------------------------------------------------------
        bool ProcessRemoteFind(string sshPath, List<string> masks, TextQuery? textQuery, CancellationToken token)
        {
            try
            {
                // Split ssh://SessionName/remote/path
                string rest = sshPath.Substring(6);
                int slash = rest.IndexOf('/');
                string session = slash < 0 ? rest : rest[..slash];
                string remote = slash < 0 ? "/" : rest[slash..];
                if (string.IsNullOrEmpty(remote)) remote = "/";

                if (textQuery != null)
                {
                    // Content search → fast grep/rg on the server
                    var found = Providers.SshFileSystemProvider.GrepFiles(
                        session,
                        remote,
                        masks,
                        textQuery.Text,
                        textQuery.UseRegex,
                        textQuery.CaseSensitive,
                        token);

                    // One extra find (same as name search) to get sizes without per-file SFTP
                    var sizeByPath = new Dictionary<string, long>(StringComparer.Ordinal);
                    try
                    {
                        foreach (var (path, size) in Providers.SshFileSystemProvider.FindFiles(session, remote, masks, token))
                        {
                            sizeByPath[path] = size;

                            // Also index by normalized form (rg/find sometimes differ by trailing style)
                            string norm = path.Replace('\\', '/').TrimEnd('/');
                            sizeByPath[norm] = size;
                        }
                    }
                    catch
                    {
                        // sizes stay 0 if find fails
                    }

                    foreach (var (remoteFile, matchCount) in found)
                    {
                        token.ThrowIfCancellationRequested();
                        if (hitLimit) return false;

                        scannedFiles++;
                        totalTextMatches += matchCount;

                        string name = System.IO.Path.GetFileName(remoteFile.Replace('\\', '/'));
                        string full = $"ssh://{session}{remoteFile}";

                        long size = 0;
                        if (!sizeByPath.TryGetValue(remoteFile, out size))
                        {
                            string norm = remoteFile.Replace('\\', '/').TrimEnd('/');
                            sizeByPath.TryGetValue(norm, out size);
                        }

                        var entry = new FileEntry
                        {
                            Name = name,
                            FullPath = full,
                            IsFolder = false,
                            Size = size
                        };

                        if (!Add(entry))
                        {
                            hitLimit = true;
                            return false;
                        }
                    }
                }
                else
                {
                    // Name-only search → fast find with sizes (one server command)
                    var foundPaths = Providers.SshFileSystemProvider.FindFiles(session, remote, masks, token);

                    foreach (var (remoteFile, size) in foundPaths)
                    {
                        token.ThrowIfCancellationRequested();
                        if (hitLimit) return false;

                        scannedFiles++;

                        string name = System.IO.Path.GetFileName(remoteFile.Replace('\\', '/'));
                        string full = $"ssh://{session}{remoteFile}";

                        var entry = new FileEntry
                        {
                            Name = name,
                            FullPath = full,
                            IsFolder = false,
                            Size = size
                        };

                        if (!Add(entry))
                        {
                            hitLimit = true;
                            return false;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return true;
            }

            return true;
        }

        // ------------------------------------------------------------------
        // Main logic
        // ------------------------------------------------------------------
        try
        {
            if (selectedRoots != null && selectedRoots.Count > 0)
            {
                // Limited to selection
                foreach (var sel in selectedRoots)
                {
                    token.ThrowIfCancellationRequested();
                    if (hitLimit) break;

                    bool isRemoteItem = sel.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);

                    if (sel.IsFolder)
                    {
                        scannedFolders++;

                        if (isRemoteItem)
                        {
                            // Fast server-side path (find or grep)
                            if (!ProcessRemoteFind(sel.FullPath, masks, textQuery, token))
                                break;
                        }
                        else
                        {
                            if (!ProcessLocalFolder(sel.FullPath))
                                break;
                        }
                    }
                    else
                    {
                        // Same rules as folder search: only files matching the mask
                        if (!NameMatches(sel.Name))
                            continue;

                        scannedFiles++;

                        // Create a clean copy (do not reuse marked instance)
                        var copy = new FileEntry
                        {
                            Name = sel.Name,
                            FullPath = sel.FullPath,
                            IsFolder = false,
                            Size = sel.Size,
                            Modified = sel.Modified
                        };

                        if (!ProcessFile(copy))
                        {
                            hitLimit = true;
                            break;
                        }
                    }
                }
            }
            else
            {
                // Full search from start path
                if (path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                {
                    // Fast server-side path (find or grep)
                    if (!ProcessRemoteFind(path, masks, textQuery, token))
                        hitLimit = true;
                }
                else
                {
                    if (!ProcessLocalFolder(path))
                        hitLimit = true;
                }
            }
        }
        finally
        {
            // Always flush remaining items
            Flush(waitForUi: true);
            searchDuration.Stop();
            Dispatcher.Invoke(UpdateStatsUI, DispatcherPriority.Background);
        }

        return hitLimit;
    }

    private static List<FileEntry> SearchArchive(
        string archivePath,
        Func<string, bool> nameMatches,
        TextQuery? textQuery,
        CancellationToken token,
        Action<int>? onMatchCounted = null)
    {
        var results = new List<FileEntry>();

        try
        {
            Func<Stream, bool>? contentMatches = textQuery == null
                ? null
                : stream =>
                {
                    int count = TextSearcher.CountMatchesInStream(stream, textQuery, token);
                    if (count > 0)
                        onMatchCounted?.Invoke(count);
                    return count > 0;
                };

            var found = ArchiveService.FindEntries(
                archivePath, nameMatches, contentMatches, MaxArchiveContentSize, token);

            foreach (var node in found)
            {
                results.Add(new FileEntry
                {
                    Name = node.Name,
                    FullPath = archivePath + "\\" + node.FullKey.Replace('/', '\\'),
                    IsFolder = false,
                    Size = node.Size,
                    Modified = node.Modified
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Password-protected, truncated, or wrong format — ignore
        }

        return results;
    }

    private void FocusFirstResult()
    {
        lvResults.SelectedIndex = 0;
        lvResults.Focus();

        Dispatcher.InvokeAsync(() =>
        {
            if (lvResults.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem row)
                row.Focus();
        }, DispatcherPriority.ContextIdle);
    }

    // ===================== Actions =====================

    private void LvResults_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ExecuteGoTo();

    private void BtnGoTo_Click(object sender, RoutedEventArgs e) => ExecuteGoTo();

    private void BtnShowInPanel_Click(object sender, RoutedEventArgs e)
    {
        if (_results.Count == 0)
        {
            txtStatus.Text = "Nothing to show yet.";
            return;
        }

        _cts?.Cancel();
        ResultsToShow = new List<FileEntry>(_results);
        DialogResult = true;
    }

    private void ExecuteGoTo()
    {
        if (lvResults.SelectedItem is FileEntry entry)
        {
            if (entry.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                // Do not use Path.GetDirectoryName — it breaks ssh:// paths on Windows
                int last = entry.FullPath.LastIndexOf('/');
                GoToDirectory = last > "ssh://".Length
                    ? entry.FullPath.Substring(0, last)
                    : entry.FullPath;
            }
            else
            {
                GoToDirectory = Path.GetDirectoryName(entry.FullPath);
            }

            GoToFile = entry.Name;
            DialogResult = true;
        }
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter)
        {
            if (lvResults.IsKeyboardFocusWithin && lvResults.SelectedItem != null)
            {
                ExecuteGoTo();
                e.Handled = true;
            }
        }
    }

    // ===================== Sorting =====================

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;

        string baseName = GetBaseColumnName(header.Column?.Header?.ToString() ?? "");
        if (string.IsNullOrEmpty(baseName)) return;

        string propName = baseName switch
        {
            "In Folder" => "FullPath",
            "Size" => "Size",
            _ => "Name"
        };

        if (_sortColumn == propName)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = propName;
            _sortAscending = true;
        }

        UpdateColumnHeaders();
        ApplySort();
    }

    private void UpdateColumnHeaders()
    {
        if (lvResults.View is not GridView gridView) return;

        foreach (var column in gridView.Columns)
        {
            string baseName = GetBaseColumnName(column.Header?.ToString() ?? "");
            string propName = baseName switch
            {
                "In Folder" => "FullPath",
                "Size" => "Size",
                _ => "Name"
            };

            column.Header = propName == _sortColumn
                ? baseName + (_sortAscending ? " ↑" : " ↓")
                : baseName;
        }
    }

    private static string GetBaseColumnName(string header) =>
        header.Replace("↑", "").Replace("↓", "").Replace("\u2007", "").Trim();

    private void ApplySort()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_results);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(
            _sortColumn,
            _sortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending));
    }

    private void ClearSort()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_results);
        view.SortDescriptions.Clear();
    }
}
