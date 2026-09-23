using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using R2Cmd.Providers;

namespace R2Cmd.Controls;

public partial class FilePaneControl
{
    private bool _lastSshTerminalState;

    #region Drive navigation

    private async Task NavigateToDrive(DriveItem di)
    {
        if (CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            _lastSshTerminalState = IsTerminalVisible;

        string newRoot = di.Name == "NET"
            ? @"\\Network\"
            : di.Name + "\\";

        string targetPath = SyncPathResolver?.Invoke(newRoot) ?? newRoot;

        PaneGotFocus?.Invoke(this, EventArgs.Empty);

        if (!string.Equals(CurrentPath, targetPath, StringComparison.OrdinalIgnoreCase))
            await NavigateAsync(targetPath);

        if (targetPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) &&
            IsTerminalVisible != _lastSshTerminalState)
        {
            ToggleSshTerminal();
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsTerminalVisible)
                _terminal.Focus();
            else
                FocusPanel();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    #endregion

    #region Navigation

    public bool CanNavigateForward => _forwardHistory.Count > 0;

    public async Task NavigateForwardAsync()
    {
        if (_forwardHistory.Count == 0) return;

        string nextPath = _forwardHistory.Pop();
        await NavigateAsync(nextPath, null, isForward: true);
        FocusPanel();
    }

    public async Task NavigateAsync(string newPath, string? itemToSelect = null, bool isForward = false)
    {
        ClearQuickSearch();

        _isSearchResults = false;
        txtPath.Visibility = Visibility.Collapsed;
        pnlSearchPath.Visibility = Visibility.Collapsed;

        _renameClickTimer?.Stop();
        _renameClickTimer = null;

        _navCts?.Cancel();
        _navCts = new CancellationTokenSource();
        var token = _navCts.Token;
        int currentRequestId = ++_requestId;

        StopWatcher();

        // Prevent the list from flashing empty when just reloading the current directory.
        // Old items remain visible until the background read completes and replaces them smoothly.
        bool isReload = string.Equals(CurrentPath.TrimEnd('\\', '/'), newPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        if (!isReload)
        {
            StartLoadingAnimation(newPath);
        }

        SetBusy(true);

        bool isSshPath = newPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);
        string? sessionName = null;

        try
        {
            if (!isForward)
            {
                if (IsAncestorPath(CurrentPath, newPath))
                    _forwardHistory.Push(CurrentPath);
                else if (!string.Equals(CurrentPath.TrimEnd('\\', '/'), newPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    _forwardHistory.Clear();
            }

            string previousPath = CurrentPath;
            CurrentPath = newPath;
            if (newPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
                newPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                _lastSshPath = newPath;
            }

            txtPath.Text = CurrentPath;
            UpdateBreadcrumbs(CurrentPath);
            pnlBreadcrumbs.Visibility = Visibility.Visible;
            UpdateDriveSelection();

            if (isSshPath)
            {
                sessionName = newPath.Substring(6).TrimEnd('/');
                int firstSlash = sessionName.IndexOf('/');
                if (firstSlash > 0) sessionName = sessionName.Substring(0, firstSlash);

                bool sessionOpen = SshFileSystemProvider.IsSessionOpen(sessionName);

                StatusMessage?.Invoke(this,
                    sessionOpen
                        ? "Reading remote folder..."
                        : $"Connecting to SSH session '{sessionName}'...");
            }
            else if (newPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                StatusMessage?.Invoke(this, "Reading network folder...");
            }

            var provider = FileSystemFactory.GetProvider(CurrentPath);
            var (entries, error) = await provider.ReadDirectoryAsync(CurrentPath, token);

            if (token.IsCancellationRequested || currentRequestId != _requestId)
                return;

            StopLoadingAnimation();

            if (isSshPath && error != null)
            {
                // Shown again once the pane is back in the network root: the
                // "Ready." written after that listing used to erase the reason
                _statusAfterReturn = $"SSH '{sessionName}': {error}";
                StatusMessage?.Invoke(this, _statusAfterReturn);

                if (sessionName != null)
                {
                    try { SshFileSystemProvider.CloseConnection(sessionName); } catch { }
                }

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    _ = NavigateAsync(@"\\Network\");
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                return;
            }

            // =================================================================
            // The folder could not be opened at all (does not exist, access
            // denied, share offline). The pane used to stay on that path with
            // an empty list, and PathChanged had already told the host (drive
            // history, terminal) about a place that was never entered. Now the
            // error is shown and the pane goes back to where it was, with the
            // folder it failed to open under the cursor.
            // =================================================================
            if (error != null && entries.Count == 0 && !isReload && !string.IsNullOrEmpty(previousPath))
            {
                MessageDialog.Show(Window.GetWindow(this)!, $"Cannot open folder:\n{newPath}\n\n{error}", "Error");

                string failedName = Path.GetFileName(newPath.TrimEnd('\\', '/'));
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    _ = NavigateAsync(previousPath, failedName, isForward: true);
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                return;
            }

            bool atDriveRoot = string.Equals(
                Path.GetPathRoot(CurrentPath)?.TrimEnd('\\'),
                CurrentPath.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

            bool hasParentEntry = entries.Count > 0 && entries[0].Name == "..";

            if (!atDriveRoot && !hasParentEntry && provider.CanHandle(CurrentPath))
                entries.Insert(0, new FileEntry { Name = "..", IsFolder = true });

            var sorted = SortEntries(entries);

            // A reload of the same folder (Ctrl+R, a copy into this pane, a
            // rename) builds new entries; marks are carried over by name
            if (isReload)
            {
                var marked = Items.Where(e => e.IsMarked).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var entry in sorted)
                    if (marked.Contains(entry.Name)) entry.IsMarked = true;
            }

            Items.ReplaceAll(sorted);
            AutoSizeNameColumn();

            bool virtualEntries = provider is not LocalDiskProvider;
            IconService.QueueLoad(sorted, virtualEntries, currentRequestId, () => _requestId, Dispatcher);

            StartWatcher(CurrentPath, isLocal: !virtualEntries);

            txtSpace.Text = "";
            _ = UpdateFreeSpaceAsync(provider, CurrentPath, token);

            RestoreSelection(itemToSelect);

            // Only now, once the folder has really been entered
            PathChanged?.Invoke(this, EventArgs.Empty);

            if (error != null)
                StatusMessage?.Invoke(this, $"Error: {error}");
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!token.IsCancellationRequested)
                SetBusy(false);
        }
    }

    public async Task RefreshAsync() => await NavigateAsync(CurrentPath, SelectedItem?.Name);

    public void ShowSearchResults(string searchRoot, IReadOnlyList<FileEntry> results)
    {
        _navCts?.Cancel();
        StopLoadingAnimation();
        StopWatcher();
        ClearQuickSearch();

        _isSearchResults = true;
        CurrentPath = searchRoot;

        txtPath.Visibility = Visibility.Collapsed;
        pnlSearchPath.Visibility = Visibility.Collapsed;

        pnlBreadcrumbs.Visibility = Visibility.Visible;
        UpdateBreadcrumbs(searchRoot);

        UpdateDriveSelection();
        PathChanged?.Invoke(this, EventArgs.Empty);

        int requestId = ++_requestId;

        var items = new List<FileEntry>(results.Count + 1)
        {
            new FileEntry
            {
                Name = "..",
                IsFolder = true,
                FullPath = searchRoot
            }
        };
        items.AddRange(results);

        Items.ReplaceAll(items);

        IconService.QueueLoad(items, virtualEntries: false, requestId, () => _requestId, Dispatcher);

        if (items.Count > 1) SetSelectedItem(items[1], takeFocus: true);
        else if (items.Count > 0) SetSelectedItem(items[0], takeFocus: true);

        StatusMessage?.Invoke(this, $"Search results: {results.Count} item(s). Ctrl+R reloads the folder.");
    }

    public string GetPersistentPath()
    {
        if (CurrentPath.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase) ||
            CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            return @"\\Network\";

        var (archivePath, _) = ArchiveService.ParseVirtualPath(CurrentPath);
        if (archivePath != null)
        {
            string? dir = Path.GetDirectoryName(archivePath);
            return !string.IsNullOrEmpty(dir) ? dir : archivePath;
        }
        return CurrentPath;
    }

    private void SetBusy(bool busy)
    {
        if (_isBusy == busy) return;

        _isBusy = busy;
        BusyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task UpdateFreeSpaceAsync(IFileSystemProvider provider, string path, CancellationToken token)
    {
        string text = await Task.Run(() =>
        {
            try
            {
                var freeSpace = provider.GetFreeSpace(path);
                if (!freeSpace.HasValue) return "";

                string free = Helpers.FormatSize((long)freeSpace.Value);

                string? root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                    return $"{free} free";

                if (!s_driveTotals.TryGetValue(root, out long total))
                {
                    try { total = new DriveInfo(root).TotalSize; }
                    catch { total = 0; }

                    s_driveTotals[root] = total;
                }

                string letter = root.TrimEnd('\\');
                return total > 0
                    ? $"{letter}  {free} of {Helpers.FormatSize(total)} free"
                    : $"{letter}  {free} free";
            }
            catch { return ""; }
        });

        if (token.IsCancellationRequested) return;

        txtSpace.Text = "";

        string status = _statusAfterReturn ?? (string.IsNullOrEmpty(text) ? "Ready." : text);
        _statusAfterReturn = null;
        StatusMessage?.Invoke(this, status);
    }

    // Why the pane was sent back to the network root, kept until that listing
    // has finished so it is the last thing in the status bar
    private string? _statusAfterReturn;

    private static bool IsAncestorPath(string current, string target)
    {
        if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(target)) return false;

        int currentLength = TrimmedLength(current);
        int targetLength = TrimmedLength(target);

        if (targetLength == 0 || currentLength <= targetLength) return false;

        for (int i = 0; i < targetLength; i++)
        {
            if (Normalize(current[i]) != Normalize(target[i])) return false;
        }

        return IsSeparator(current[targetLength]);

        static bool IsSeparator(char c) => c == '\\' || c == '/';
        static char Normalize(char c) => char.ToUpperInvariant(c == '/' ? '\\' : c);
        static int TrimmedLength(string path)
        {
            int length = path.Length;
            while (length > 0 && IsSeparator(path[length - 1])) length--;
            return length;
        }
    }

    #endregion

    #region Breadcrumbs and path box

    private void OnBreadcrumbsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || _crumbVisuals.Count == 0) return;
        RelayoutCrumbs();
    }

    private void UpdateBreadcrumbs(string path)
    {
        spBreadcrumbs.Children.Clear();
        _crumbVisuals.Clear();
        _crumbEllipsis = null;
        _crumbEllipsisWidth = 0;
        _crumbPrefixWidth = 0;
        _lastCrumbAvail = -1;
        _crumbParts = new List<(string Text, string Path)>();

        if (string.IsNullOrEmpty(path)) return;

        if (_isSearchResults)
        {
            var searchLabel = new TextBlock
            {
                Text = "Search results › ",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0),
                FontWeight = FontWeights.SemiBold
            };
            searchLabel.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            searchLabel.SetResourceReference(TextBlock.FontSizeProperty, "AppFilePaneFontSize");
            spBreadcrumbs.Children.Add(searchLabel);
            _crumbPrefixWidth = MeasureCrumb(searchLabel);
        }

        var parts = new List<(string Text, string Path)>();

        if (path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(("ssh://", @"\\Network\"));

            string rest = path.Substring(6);
            var segments = rest.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            string currentBuiltPath = "ssh://";
            for (int i = 0; i < segments.Length; i++)
            {
                currentBuiltPath += segments[i] + "/";
                parts.Add((segments[i] + "/", currentBuiltPath));
            }
        }
        else
        {
            string separator = path.Contains("/") ? "/" : "\\";
            string prefix;
            string remainingPath;

            if (path.StartsWith(@"\\"))
            {
                int nextSlash = path.IndexOf('\\', 2);
                if (nextSlash > 0)
                {
                    prefix = path.Substring(0, nextSlash + 1);
                    remainingPath = path.Substring(nextSlash + 1);
                }
                else
                {
                    prefix = path;
                    remainingPath = "";
                }
            }
            else if (path.Contains(":\\"))
            {
                prefix = path.Substring(0, 3);
                remainingPath = path.Substring(3);
            }
            else
            {
                prefix = path;
                remainingPath = "";
            }

            string currentBuiltPath = prefix;
            parts.Add((prefix, currentBuiltPath));

            if (!string.IsNullOrEmpty(remainingPath))
            {
                var segments = remainingPath.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in segments)
                {
                    currentBuiltPath += part + separator;
                    parts.Add((part + separator, currentBuiltPath));
                }
            }
        }

        _crumbParts = parts;

        for (int i = 0; i < parts.Count; i++)
        {
            var (hit, sep) = CreateCrumbItem(parts[i].Text, parts[i].Path);
            spBreadcrumbs.Children.Add(hit);
            if (sep != null) spBreadcrumbs.Children.Add(sep);

            double width = MeasureCrumb(hit) + (sep != null ? MeasureCrumb(sep) : 0);
            _crumbVisuals.Add((hit, sep, width));

            if (i == 0)
            {
                var ellipsis = CreateCrumbEllipsis();
                spBreadcrumbs.Children.Add(ellipsis);
                _crumbEllipsisWidth = MeasureCrumb(ellipsis);
                ellipsis.Visibility = Visibility.Collapsed;
                _crumbEllipsis = ellipsis;
            }
        }

        RelayoutCrumbs();
    }

    private void RelayoutCrumbs()
    {
        int n = _crumbVisuals.Count;
        if (n == 0) return;

        double available = pnlBreadcrumbs.ActualWidth;
        if (available <= 1) return;
        if (Math.Abs(available - _lastCrumbAvail) < 0.5) return;
        _lastCrumbAvail = available;

        double budget = available - 8;

        SetCrumbsVisible(0, n - 1, true);
        if (_crumbEllipsis != null) _crumbEllipsis.Visibility = Visibility.Collapsed;

        if (n <= 3) return;

        double total = _crumbPrefixWidth;
        for (int i = 0; i < n; i++) total += _crumbVisuals[i].Width;
        if (total <= budget) return;

        double head = _crumbPrefixWidth + _crumbVisuals[0].Width + _crumbEllipsisWidth;
        int hideTo = n - 3;
        for (int h = 1; h <= n - 3; h++)
        {
            double tail = 0;
            for (int i = h + 1; i < n; i++) tail += _crumbVisuals[i].Width;
            if (head + tail <= budget)
            {
                hideTo = h;
                break;
            }
        }

        SetCrumbsVisible(1, hideTo, false);
        if (_crumbEllipsis != null) _crumbEllipsis.Visibility = Visibility.Visible;
    }

    private void SetCrumbsVisible(int from, int to, bool visible)
    {
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        for (int i = from; i <= to; i++)
        {
            _crumbVisuals[i].Hit.Visibility = vis;
            if (_crumbVisuals[i].Sep != null)
                _crumbVisuals[i].Sep!.Visibility = vis;
        }
    }

    private static double MeasureCrumb(FrameworkElement el)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return el.DesiredSize.Width;
    }

    private (FrameworkElement Hit, FrameworkElement? Sep) CreateCrumbItem(string text, string targetPath)
    {
        string displayText = text;
        string trailingSeparator = "";

        if (text.Equals("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            displayText = "ssh://";
            trailingSeparator = "";
        }
        else if (text.Length > 1 && (text.EndsWith("\\") || text.EndsWith("/")))
        {
            displayText = text.Substring(0, text.Length - 1);
            trailingSeparator = text.Substring(text.Length - 1);
        }

        var border = new Border
        {
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0),
            Padding = new Thickness(1, 2, 1, 2),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Tag = "crumb"
        };

        var tb = new TextBlock
        {
            Text = displayText,
            VerticalAlignment = VerticalAlignment.Center
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        tb.SetResourceReference(TextBlock.FontSizeProperty, "AppFilePaneFontSize");
        border.Child = tb;

        border.MouseEnter += (_, _) => border.Background = s_breadcrumbHoverBrush;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;

        border.MouseLeftButtonDown += async (_, e) =>
        {
            e.Handled = true;
            string? itemToSelect = null;

            if (text == "ssh://")
            {
                if (CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                    itemToSelect = CurrentPath.Substring(6).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }
            else if (CurrentPath.StartsWith(targetPath, StringComparison.OrdinalIgnoreCase))
            {
                itemToSelect = CurrentPath.Substring(targetPath.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (itemToSelect == "LAN") itemToSelect = "Windows Network";
            }

            await NavigateAsync(targetPath, itemToSelect);
            FocusPanel();
        };

        border.MouseRightButtonDown += (_, e) =>
        {
            try
            {
                Clipboard.SetText(targetPath);
                StatusMessage?.Invoke(this, $"Path copied: {targetPath}");
            }
            catch { }
            e.Handled = true;
        };

        FrameworkElement? sep = null;
        if (!string.IsNullOrEmpty(trailingSeparator))
        {
            var sepTb = new TextBlock
            {
                Text = trailingSeparator,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 1, 0),
                Tag = "crumb"
            };
            sepTb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
            sepTb.SetResourceReference(TextBlock.FontSizeProperty, "AppFilePaneFontSize");
            sep = sepTb;
        }

        return (border, sep);
    }

    private static TextBlock CreateCrumbEllipsis()
    {
        var tb = new TextBlock
        {
            Text = " … ",
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "crumb"
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        tb.SetResourceReference(TextBlock.FontSizeProperty, "AppFilePaneFontSize");
        return tb;
    }

    private void PnlBreadcrumbs_MouseLeftButtonDown(object sender, MouseButtonEventArgs? e)
    {
        if (IsTerminalVisible) return;

        pnlBreadcrumbs.Visibility = Visibility.Collapsed;
        txtPath.Visibility = Visibility.Visible;
        txtPath.Focus();
        Dispatcher.BeginInvoke(new Action(() => txtPath.SelectAll()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PnlBreadcrumbs_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Clipboard.SetText(CurrentPath);
            StatusMessage?.Invoke(this, "Full path copied to clipboard.");
        }
        catch { }
        e.Handled = true;
    }

    private void PnlSearchPath_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            string textToCopy = txtSearchPath.Text ?? "";

            const string prefix = "Search results: ";
            if (textToCopy.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                textToCopy = textToCopy.Substring(prefix.Length);

            if (!string.IsNullOrWhiteSpace(textToCopy))
            {
                Clipboard.SetText(textToCopy);
                StatusMessage?.Invoke(this, "Path copied to clipboard.");
            }
        }
        catch { }

        e.Handled = true;
    }

    private void TxtPath_LostFocus(object sender, RoutedEventArgs e)
    {
        pnlBreadcrumbs.Visibility = Visibility.Visible;
        txtPath.Visibility = Visibility.Collapsed;
        txtPath.Text = CurrentPath;
    }

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        string newPath = txtPath.Text;

        if (!newPath.StartsWith(@"\\") &&
            !newPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) &&
            !newPath.EndsWith("\\"))
        {
            newPath += "\\";
        }

        await NavigateAsync(newPath);
        FocusPanel();
    }

    #endregion
}
