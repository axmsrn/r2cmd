using System;
using System.ComponentModel;
using System.Windows.Media;

namespace R2Cmd;

public sealed class FileEntry : INotifyPropertyChanged
{
    // =========================================================================
    // Change notifications use shared, immutable event args. The size spinner
    // raises two notifications per animating row ten times a second, and every
    // one of them used to allocate a fresh PropertyChangedEventArgs.
    // =========================================================================
    private static readonly PropertyChangedEventArgs s_isCutChanged = new(nameof(IsCut));
    private static readonly PropertyChangedEventArgs s_itemOpacityChanged = new(nameof(ItemOpacity));
    private static readonly PropertyChangedEventArgs s_sizeChanged = new(nameof(Size));
    private static readonly PropertyChangedEventArgs s_sizeKnownChanged = new(nameof(SizeKnown));
    private static readonly PropertyChangedEventArgs s_sizeCalculatingChanged = new(nameof(SizeCalculating));
    private static readonly PropertyChangedEventArgs s_sizeAnimationTextChanged = new(nameof(SizeAnimationText));
    private static readonly PropertyChangedEventArgs s_sizeDisplayChanged = new(nameof(SizeDisplay));
    private static readonly PropertyChangedEventArgs s_modifiedChanged = new(nameof(Modified));
    private static readonly PropertyChangedEventArgs s_modifiedDisplayChanged = new(nameof(ModifiedDisplay));
    private static readonly PropertyChangedEventArgs s_isMarkedChanged = new(nameof(IsMarked));
    private static readonly PropertyChangedEventArgs s_iconChanged = new(nameof(Icon));
    private static readonly PropertyChangedEventArgs s_isEditingChanged = new(nameof(IsEditing));

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsFolder { get; set; }

    // Custom vector icons for virtual items (SSH servers, network entries)
    public string IconType { get; set; } = "Default";

    // =========================================================================
    // Last write time. Settable and notifying, not init-only: the directory
    // watcher must be able to update it when a file is saved in place. As an
    // init property the pane kept the old time until the folder was re-read,
    // even though the file on disk had changed.
    // =========================================================================
    private DateTime? _modified;
    public DateTime? Modified
    {
        get => _modified;
        set
        {
            if (_modified == value) return;
            _modified = value;
            OnPropertyChanged(s_modifiedChanged);
            OnPropertyChanged(s_modifiedDisplayChanged);
        }
    }

    // Flag for hidden or system files
    public bool IsHidden { get; init; }

    // Flag for symbolic links (shortcuts)
    public bool IsSymlink { get; init; }

    // Flag for cut operation
    private bool _isCut;
    public bool IsCut
    {
        get => _isCut;
        set
        {
            if (_isCut == value) return;
            _isCut = value;
            OnPropertyChanged(s_isCutChanged);
            OnPropertyChanged(s_itemOpacityChanged);
        }
    }

    // Lowers opacity to make the font look darker for hidden files or cut items
    public double ItemOpacity => IsCut ? 0.5 : (IsHidden ? 0.5 : 1.0);

    // Fast check if the file is an archive (cached after first call to speed up XAML scrolling)
    private bool? _isArchive;
    public bool IsArchive => _isArchive ??= ArchiveService.IsArchiveFile(Name);

    private long _size;
    public long Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value;
            OnPropertyChanged(s_sizeChanged);
            OnPropertyChanged(s_sizeDisplayChanged);
        }
    }

    private bool _sizeKnown;
    public bool SizeKnown
    {
        get => _sizeKnown;
        set
        {
            if (_sizeKnown == value) return;
            _sizeKnown = value;
            OnPropertyChanged(s_sizeKnownChanged);
            OnPropertyChanged(s_sizeDisplayChanged);
        }
    }

    private bool _sizeCalculating;
    public bool SizeCalculating
    {
        get => _sizeCalculating;
        set
        {
            if (_sizeCalculating == value) return;
            _sizeCalculating = value;
            OnPropertyChanged(s_sizeCalculatingChanged);
            OnPropertyChanged(s_sizeDisplayChanged);
        }
    }

    // Animated size text shown while a folder size is being calculated
    private string _sizeAnimationText = "    ⠋    ";
    public string SizeAnimationText
    {
        get => _sizeAnimationText;
        set
        {
            if (_sizeAnimationText == value) return;
            _sizeAnimationText = value;
            OnPropertyChanged(s_sizeAnimationTextChanged);
            OnPropertyChanged(s_sizeDisplayChanged);
        }
    }

    public string SizeDisplay => IsFolder
        ? (SizeCalculating ? SizeAnimationText : SizeKnown ? Helpers.FormatSize(Size) : "<DIR>")
        : Helpers.FormatSize(Size);

    public string ModifiedDisplay => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";

    /// <summary>
    /// Directory part of FullPath (used when showing path of current file in search results).
    /// </summary>
    public string DirectoryDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(FullPath) || Name == "..") return "";

            try
            {
                // SSH paths use forward slash
                if (FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                {
                    int last = FullPath.LastIndexOf('/');
                    return last > 0 ? FullPath.Substring(0, last) : FullPath;
                }

                return System.IO.Path.GetDirectoryName(FullPath) ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    // Marking a file with Insert key for copy/move/delete.
    // Lives INDEPENDENTLY of WPF system selection (SelectedItems), so
    // moving cursor with arrows in Extended mode doesn't clear it.
    private bool _isMarked;
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (_isMarked == value) return;
            _isMarked = value;
            OnPropertyChanged(s_isMarkedChanged);
        }
    }

    // Native Windows icon. Arrives asynchronously after row display,
    // so it's a notify property, not init: binding must update.
    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            OnPropertyChanged(s_iconChanged);
        }
    }

    // Inline rename mode: when true, row shows input field instead of label.
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            OnPropertyChanged(s_isEditingChanged);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(PropertyChangedEventArgs args) => PropertyChanged?.Invoke(this, args);
}
