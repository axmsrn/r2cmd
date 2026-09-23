using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace R2Cmd;

// Important: partial class means this is a continuation of MainWindow class
public partial class MainWindow
{
    private void OpenFavorites()
    {
        var targetElement = _activePane.FindName("lvFiles") as UIElement ?? _activePane;

        var menu = new ContextMenu
        {
            PlacementTarget = targetElement,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            HorizontalOffset = 0,
            VerticalOffset = 0
        };

        menu.SetResourceReference(Control.FontSizeProperty, "AppFontSize");

        // Clean current path from slashes for accurate comparison
        string currentActivePath = _activePane.CurrentPath.TrimEnd('\\', '/');
        foreach (var fav in _settings.Favorites)
        {
            var item = new MenuItem();
            string path = fav.Path;

            // Wrap text in TextBlock for strict color control
            var headerText = new TextBlock { Text = fav.Name };
            item.Header = headerText;

            // If bookmark path matches current open path
            if (string.Equals(path.TrimEnd('\\', '/'), currentActivePath, StringComparison.OrdinalIgnoreCase))
            {
                // Apply yellow color (marked files brush) directly to text
                headerText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.MarkedText");

                item.IsChecked = true;

                // Reliably transfer focus when menu loads
                item.Loaded += (s, e) =>
                {
                    Dispatcher.BeginInvoke(new Action(() => item.Focus()), System.Windows.Threading.DispatcherPriority.Input);
                };
            }

            item.Click += async (s, e) => await NavigateToFavoriteAsync(path);
            menu.Items.Add(item);
        }

        if (_settings.Favorites.Count > 0) menu.Items.Add(new Separator());

        var addItem = new MenuItem { Header = "Add current directory..." };
        addItem.Click += (s, e) => AddCurrentToFavorites();
        menu.Items.Add(addItem);

        var editItem = new MenuItem { Header = "Configure..." };
        editItem.Click += async (s, e) => await OpenFavoritesEditorAsync();
        menu.Items.Add(editItem);

        menu.IsOpen = true;
    }

    private async Task NavigateToFavoriteAsync(string path)
    {
        if (_busy) return;

        // Only a local path can be checked cheaply. A UNC share is left to the
        // provider, and an ssh:// bookmark has no directory on this machine at
        // all — Directory.Exists said no and the favourite simply never opened.
        bool isLocalPath = !path.StartsWith(@"\\") &&
                           !path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);

        if (isLocalPath && !await Task.Run(() => Directory.Exists(path)))
        {
            MessageDialog.Show(this, $"Directory not found:\n{path}", "Favorites Error");
            return;
        }

        await _activePane.NavigateAsync(path);
    }

    private void AddCurrentToFavorites()
    {
        string currentPath = _activePane.CurrentPath;
        if (_settings.Favorites.Any(f => string.Equals(f.Path, currentPath, StringComparison.OrdinalIgnoreCase)))
        {
            MessageDialog.Show(this, "This directory is already in favorites.", "Info");
            return;
        }

        string name = Path.GetFileName(currentPath.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = currentPath;

        _settings.Favorites.Add(new FavoriteEntry { Name = name, Path = currentPath });
        try { _settings.Save(); } catch { }
        SetStatus($"Added to favorites: {name}");
    }

    private async Task OpenFavoritesEditorAsync()
    {
        var dlg = new FavoriteWindow(_settings, _activePane.CurrentPath) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.SelectedResult != null)
            await NavigateToFavoriteAsync(dlg.SelectedResult.Path);
    }
}
