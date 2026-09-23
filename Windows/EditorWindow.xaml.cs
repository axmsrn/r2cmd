using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace R2Cmd;

public partial class EditorWindow : Window
{
    private readonly string _path;
    private readonly string? _remotePath;
    private readonly string _displayName;

    private bool _dirty;
    private bool _suppressTextChanged;
    private string _encodingName = "UTF-8";
    private Encoding? _forcedEncoding;
    private int _encodingIndex;

    private readonly List<int> _textMatches = new();
    private string _matchQuery = "";
    private int _matchIndex = -1;

    private readonly SmoothWheelScroller _scroller;

    public EditorWindow(string localPath, string displayName, string? remotePath = null)
    {
        InitializeComponent();

        _path = localPath;
        _remotePath = remotePath;
        _displayName = displayName;
        Title = $"Edit: {displayName}";

        if (TryFindResource("Brush.Background") is Brush background)
            Resources[SystemColors.ControlBrushKey] = background;

        SourceInitialized += (_, _) =>
            Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme, useSurfaceColor: true);

        _scroller = new SmoothWheelScroller(Dispatcher);
        _scroller.Attach(txtContent);

        ContentRendered += (_, _) => LoadFile();
    }

    private void LoadFile()
    {
        try
        {
            Encoding encoding = _forcedEncoding
                ?? TextSearcher.DetectFileEncoding(_path)
                ?? new UTF8Encoding(false);

            string text = File.ReadAllText(_path, encoding);

            _encodingName = _forcedEncoding != null
                ? EncodingChoices.Names[_encodingIndex]
                : encoding.CodePage == 65001 ? "UTF-8" : encoding.WebName;

            _suppressTextChanged = true;
            txtContent.Text = text;
            _suppressTextChanged = false;

            _dirty = false;
            Title = $"Edit: {_displayName}";

            string extension = AvalonEditDraculaTheme.GetHighlightingExtension(_path);
            bool plainText = FileTypes.PlainText.Contains(extension);
            AvalonEditDraculaTheme.Apply(txtContent, _path, showLineNumbers: !plainText);

            UpdateStatus();
            txtContent.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Cannot open file:\n" + ex.Message, "Editor");
            Close();
        }
        finally
        {
            _suppressTextChanged = false;
        }
    }

    // Bumped on every edit, so a save can tell whether the text changed while
    // its upload was still running
    private int _editVersion;

    // An upload is in flight: a second save or a close waits for it
    private bool _saving;

    private void TxtContent_TextChanged(object? sender, EventArgs e)
    {
        if (_suppressTextChanged) return;

        _editVersion++;

        // Only the first edit changes anything in the status bar. Calling
        // UpdateStatus on every keystroke also read the file size from disk
        // (new FileInfo) once per key press.
        if (_dirty) return;

        _dirty = true;
        Title = $"Edit: {_displayName} *";
        UpdateStatus();
    }

    // =========================================================================
    // The local copy is written right here (fast); the SSH upload runs on the
    // thread pool. It used to run on the UI thread, and on a slow link the
    // editor froze for the whole upload after every Ctrl+S.
    // =========================================================================
    private async Task<bool> SaveAsync()
    {
        if (_saving) return false;
        _saving = true;

        int versionAtSave = _editVersion;

        try
        {
            Encoding encoding = _forcedEncoding
                ?? TextSearcher.DetectFileEncoding(_path)
                ?? new UTF8Encoding(false);

            File.WriteAllText(_path, txtContent.Text, encoding);

            if (!string.IsNullOrEmpty(_remotePath) &&
                _remotePath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                string localPath = _path;
                string remotePath = _remotePath;

                txtStatusRight.Text = "•  Uploading...";

                await Task.Run(() =>
                {
                    using var stream = new FileStream(
                        localPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);
                    Providers.SshFileSystemProvider.UploadFromStream(
                        stream,
                        remotePath,
                        CancellationToken.None,
                        _ => { });
                });

                // Remote folders have no watcher: the pane would keep the old
                // size and date until the folder was re-read
                if (Owner is MainWindow main)
                    main.RefreshRemoteEntry(remotePath);
            }

            // Text typed while the upload ran is not in this save
            if (_editVersion == versionAtSave)
            {
                _dirty = false;
                Title = $"Edit: {_displayName}";
            }
            UpdateStatus();

            txtStatusRight.Text = "•  Saved";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                txtStatusRight.Text = "";
                timer.Stop();
            };
            timer.Start();

            return true;
        }
        catch (Exception ex)
        {
            UpdateStatus();
            MessageBox.Show(this, "Save failed:\n" + ex.Message, "Editor");
            return false;
        }
        finally
        {
            _saving = false;
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        // An upload is still running; closing now would lose its error report
        if (_saving)
        {
            e.Cancel = true;
            return;
        }

        if (!_dirty) return;

        var dialog = new ConfirmDialog(
            message: "Do you want to save the changes?",
            title: "Unsaved Changes",
            yesText: "Save",
            noText: "Don't Save",
            cancelText: "Cancel")
        {
            Owner = this
        };

        dialog.ShowDialog();

        switch (dialog.Result)
        {
            case ConfirmDialog.ConfirmResult.Yes:
                // The close is held while the upload runs; once saved, the
                // document is clean and the second Close() goes straight through
                e.Cancel = true;
                if (await SaveAsync() && !_dirty) Close();
                return;
            case ConfirmDialog.ConfirmResult.Cancel:
                e.Cancel = true;
                break;
        }

        if (e.Cancel) return;

        if (Owner != null)
            Owner.Activate();
        else if (Application.Current.MainWindow != null &&
                 Application.Current.MainWindow != this)
            Application.Current.MainWindow.Activate();
    }

    private void UpdateStatus()
    {
        txtMode.Text = "Edit";
        txtEncoding.Text = _forcedEncoding == null
            ? string.IsNullOrEmpty(_encodingName) ? "Auto" : _encodingName
            : EncodingChoices.Names[_encodingIndex];

        string size = "";
        try
        {
            var info = new FileInfo(_path);
            if (info.Exists)
                size = "  •  " + Helpers.FormatSize(info.Length);
        }
        catch { }

        txtStatusLeft.Text = _displayName + size;
        txtStatusRight.Text = _dirty ? "  •  modified" : "";
    }

    private void TxtMode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    private void TxtEncoding_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _encodingIndex = (_encodingIndex + 1) % EncodingChoices.Names.Length;
        _forcedEncoding = EncodingChoices.Get(_encodingIndex);

        if (!_dirty)
            LoadFile();
        else
            UpdateStatus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (control && e.Key == Key.S)
        {
            e.Handled = true;
            _ = SaveAsync();
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(txtFind.Text) || txtFind.IsFocused)
            {
                txtFind.Text = "";
                txtContent.Focus();
            }
            else
            {
                Close();
            }
            return;
        }

        if (control && e.Key == Key.F)
        {
            e.Handled = true;
            txtFind.Focus();
            txtFind.SelectAll();
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        e.Handled = true;
        txtContent.FontSize = e.Delta > 0
            ? Math.Min(72, txtContent.FontSize + 2)
            : Math.Max(8, txtContent.FontSize - 2);
    }

    private void TxtFind_TextChanged(object sender, TextChangedEventArgs e) =>
        ExecuteSearch(txtFind.Text, isNew: true);

    private void TxtFind_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        int direction = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
        ExecuteSearch(txtFind.Text, isNew: false, direction);
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e) =>
        ExecuteSearch(txtFind.Text, isNew: false, direction: -1);

    private void BtnNext_Click(object sender, RoutedEventArgs e) =>
        ExecuteSearch(txtFind.Text, isNew: false, direction: 1);

    private void ExecuteSearch(string needle, bool isNew, int direction = 1)
    {
        if (string.IsNullOrEmpty(needle))
        {
            _textMatches.Clear();
            _matchQuery = "";
            _matchIndex = -1;
            txtMatches.Text = "";
            return;
        }

        if (isNew || _matchQuery != needle)
        {
            _matchQuery = needle;
            _textMatches.Clear();

            string text = txtContent.Text;
            int from = 0;
            while (from <= text.Length - needle.Length)
            {
                int index = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                _textMatches.Add(index);
                from = index + 1;
            }

            _matchIndex = _textMatches.Count > 0 ? 0 : -1;
        }
        else if (_textMatches.Count > 0)
        {
            _matchIndex = ((_matchIndex + direction) % _textMatches.Count + _textMatches.Count) %
                          _textMatches.Count;
        }

        if (_matchIndex < 0 || _matchIndex >= _textMatches.Count)
        {
            txtMatches.Text = "0 / 0";
            return;
        }

        int offset = _textMatches[_matchIndex];
        txtContent.Select(offset, needle.Length);
        txtContent.ScrollToLine(txtContent.Document.GetLineByOffset(offset).LineNumber);
        txtMatches.Text = $"{_matchIndex + 1} / {_textMatches.Count}";
    }
}
