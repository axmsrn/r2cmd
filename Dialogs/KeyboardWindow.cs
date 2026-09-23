using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace R2Cmd;

public sealed class KeyboardWindow : Window
{
    public KeyboardWindow()
    {
        Title = "Shortcuts";

        // Automatically adjust the window size to fit the content
        SizeToContent = SizeToContent.WidthAndHeight;

        // Hard limits prevent the window from expanding off-screen.
        MaxWidth = 1150;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;

        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        FontSize = 11;

        Background = (System.Windows.Media.Brush)Application.Current.FindResource("Brush.Background");
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Brush.TextPrimary");

        InputBindings.Add(new KeyBinding(new RelayCommand(() => Close()), Key.Escape, ModifierKeys.None));

        // Click anywhere outside the window closes it. Requires Show(), not
        // ShowDialog() — see the comments in CloseOnFocusLoss.
        CloseOnFocusLoss.Enable(this);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Background
        };

        var grid = new Grid { Margin = new Thickness(20, 12, 12, 12) };

        // 3-column layout separated by 16px margins
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Content is dynamically balanced to ~32-34 lines per column
        string col1Text =
@"📁 FILE AND FOLDER MANAGEMENT
• F7 — Create a new folder
• Shift + F4 — Create a new text file
• F2 or Shift + F6 — Rename selected file or folder
• F2 again while renaming — switch the selection between
  the name and the name with its extension
• F5 — Copy file(s)
• F6 — Move file(s)
• Alt + F5 — Pack into archive
• F8 or Delete — Move to Recycle Bin
• Shift + F8 or Shift + Delete — Delete permanently

🔄 NAVIGATION AND PANELS
• Tab — Switch between left and right panels
• Ctrl + U — Swap left and right panels
• Ctrl + R — Refresh both panels
• Ctrl + PageUp or Ctrl + Up — Go up one level (same as '..')
• Ctrl + PageDown — Return to the folder Ctrl + PageUp came from,
  or open the .exe installer under the cursor as an archive
• Ctrl + Down — Go forward in history
• Enter — Open folder, file, or archive (also confirms rename)
• Esc — Cancel rename/create, or clear quick search

🔀 SELECTION
• Insert — Toggle selection and move cursor down
• Space — Toggle selection and calculate folder size
• Ctrl + A or * (Shift + 8) — Select all / Deselect all
• Ctrl + Click — Toggle the mark on a single row
• Shift + Click — Mark a range from the cursor to the clicked file

📋 CLIPBOARD
• Ctrl + Shift + C — Copy full paths of selected files
• Ctrl + Alt + C — Copy only names of selected files
• Right click a breadcrumb — Copy that part of the path";

        string col2Text =
        @"💻 INTEGRATED TERMINAL
• Ctrl + ~ — Toggle the global bottom terminal
• Ctrl + Tab — Focus the terminal / back to the file list
• Up / Down — The shell's own command history
• Shift + PageUp / PageDown — Scroll terminal output
• Ctrl + C / Ctrl + V — Copy selected text / Paste
• Shift + Insert — Paste text from clipboard
• Ctrl + Shift + K — Clear terminal screen and scrollback buffer
• Ctrl + D — Send EOF / Close SSH session
• Ctrl + Q — Force close terminal session
• Esc — Clear terminal selection or line buffer
• Drag the horizontal divider to resize the terminal


🔍 SEARCH, FAVORITES, AND FILTERS
• Type any letter — Quick search filter in current folder
• Backspace — Erase the last character of the quick search
• Alt + F7 — Open advanced file search window
• Ctrl + D — Open Favorites menu (and add current folder)
• Alt + D — Open Favorites manager
• Enter in the manager — Go to the selected folder
• Drag a row in the manager — Reorder

🖱️ MOUSE
• Right click on a file — Windows context menu
• Right button drag — Mark or unmark several rows at once
• Second click on a selected name — Rename in place
• Drag files onto the other panel — Copy them
• Drag files with Shift — Move them instead of copying
• Drag files onto a folder — Drop them directly inside
• Double click the divider — Reset panels to 50% / 50%
• Drag the header gripper — Set a column width, kept between runs
• Click the path bar — Edit the path as text
• Right click a path segment — Copy that path to clipboard";

        string col3Text =
        @"👁️ VIEW, SIZES AND SCALE
• F3 — Built in viewer: text, Markdown, hex, images and code
• F4 — Open file in the external editor
• Alt + Shift + Enter — Calculate sizes of all folders in panel
• Ctrl + Plus / Ctrl + Minus — Scale fonts, icons and rows
• Ctrl + 0 — Reset the scale back to 100%
• Ctrl + Mouse Wheel — Same as the two keys above

🔎 VIEWER (F3) SHORTCUTS
• Ctrl + F — Focus the search box
• Esc — Clear search / cancel Go-to-line / Close viewer
• Enter or F3 — Next search match
• Shift + Enter or Shift + F3 — Previous search match
• 1 — Force Text mode (with line numbers)
• 2 — Force Hex mode
• W — Toggle Word Wrap (Text mode only)
• Ctrl + Shift + V — Toggle Code highlighting ↔ plain Text
• Type a number (e.g. 142) — Go to that line (after a short pause)
• Backspace — Edit the typed line number
• Click mode in status bar (Text / Code / Hex / Image) — cycle modes
• Click encoding in status bar — cycle encoding
  (Auto → UTF-8 → UTF-8 BOM → Windows-1251 → CP866 → UTF-16)

📊 SORTING
• Ctrl + F3 — Sort by Name
• Ctrl + F4 — Sort by Extension
• Ctrl + F5 — Sort by Modified Date
• Ctrl + F6 — Sort by Size
• Click a column header — Sort by that column
  (Pressing the shortcut again toggles Ascending/Descending)

🌐 NETWORK & SSH
• \\Network — Type in path bar to open SSH & LAN manager
• Alt + Enter — File Properties (or edit SSH session)
• F2 or Shift + F6 — Rename over SSH
• F5 / F6 — Copy / Move over SSH
• F8 or Delete — Delete over SSH";

        var tb1 = new TextBlock
        {
            Text = col1Text,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            VerticalAlignment = VerticalAlignment.Top,
            TextAlignment = TextAlignment.Left
        };

        var tb2 = new TextBlock
        {
            Text = col2Text,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            VerticalAlignment = VerticalAlignment.Top,
            TextAlignment = TextAlignment.Left
        };

        var tb3 = new TextBlock
        {
            Text = col3Text,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            VerticalAlignment = VerticalAlignment.Top,
            TextAlignment = TextAlignment.Left
        };

        Grid.SetColumn(tb1, 0);
        Grid.SetColumn(tb2, 2);
        Grid.SetColumn(tb3, 4);

        grid.Children.Add(tb1);
        grid.Children.Add(tb2);
        grid.Children.Add(tb3);
        scroll.Content = grid;

        Content = scroll;
        SourceInitialized += (s, ev) => Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme);
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}
