using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace R2Cmd;

public partial class FavoriteWindow : Window
{
    private readonly AppSettings _settings;

    public ObservableCollection<FavoriteEntry> Items { get; } = new();
    public FavoriteEntry? SelectedResult { get; private set; }

    private Point _dragStart;
    private FavoriteEntry? _draggedItem;
    private int _insertIndex = -1;
    private InsertionLineAdorner? _insertionAdorner;

    public FavoriteWindow(AppSettings settings, string? currentPath = null)
    {
        InitializeComponent();
        _settings = settings;

        foreach (var f in settings.Favorites)
            Items.Add(f);

        lstFavorites.ItemsSource = Items;

        int idx = 0;
        if (!string.IsNullOrEmpty(currentPath))
        {
            string cur = currentPath.TrimEnd('\\', '/');
            for (int i = 0; i < Items.Count; i++)
            {
                if (string.Equals(Items[i].Path.TrimEnd('\\', '/'), cur, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }
        }

        if (Items.Count > 0)
            lstFavorites.SelectedIndex = idx;

        Loaded += (s, e) =>
        {
            lstFavorites.Focus();
            if (lstFavorites.SelectedItem != null)
            {
                var item = (ListBoxItem)lstFavorites.ItemContainerGenerator.ContainerFromItem(lstFavorites.SelectedItem);
                item?.Focus();
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme);
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (lstFavorites.SelectedItem is FavoriteEntry entry)
        {
            int idx = lstFavorites.SelectedIndex;
            Items.Remove(entry);
            if (Items.Count > 0)
                lstFavorites.SelectedIndex = Math.Min(idx, Items.Count - 1);

            lstFavorites.Focus();
        }
    }

    private void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        if (lstFavorites.SelectedItem is not FavoriteEntry entry) return;

        int idx = lstFavorites.SelectedIndex;
        var result = ShowEditBox("Edit favorite", entry.Name, entry.Path);
        if (result == null) return;

        string newName = result.Value.Name.Trim();
        string newPath = result.Value.Path.Trim();

        if (string.IsNullOrEmpty(newName)) newName = entry.Name;
        if (string.IsNullOrEmpty(newPath)) newPath = entry.Path;

        if (string.Equals(newName, entry.Name, StringComparison.Ordinal) &&
            string.Equals(newPath, entry.Path, StringComparison.Ordinal))
        { lstFavorites.Focus(); return; }

        if (Items.Where((_, i) => i != idx)
                 .Any(f => string.Equals(f.Path, newPath, StringComparison.OrdinalIgnoreCase)))
        {
            MessageDialog.Show(this, "This directory is already in favorites.", "Info");
            lstFavorites.Focus();
            return;
        }

        Items[idx] = new FavoriteEntry { Name = newName, Path = newPath };
        lstFavorites.SelectedIndex = idx;
        lstFavorites.Focus();
    }

    private void BtnUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void BtnDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        int idx = lstFavorites.SelectedIndex;
        int newIdx = idx + delta;
        if (idx < 0 || newIdx < 0 || newIdx >= Items.Count) return;

        Items.Move(idx, newIdx);
        lstFavorites.SelectedIndex = newIdx;
        lstFavorites.Focus();
    }

    private void BtnGo_Click(object sender, RoutedEventArgs e)
    {
        if (lstFavorites.SelectedItem is FavoriteEntry entry)
        {
            SelectedResult = entry;
            SaveAndClose(true);
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        SaveAndClose(false);
    }

    private void OnFavoriteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (lstFavorites.SelectedItem is FavoriteEntry entry)
        {
            SelectedResult = entry;
            SaveAndClose(true);
        }
    }

    private void LstFavorites_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _draggedItem = GetEntryAt(e.GetPosition(lstFavorites));
    }

    private void LstFavorites_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null)
            return;

        Point pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var dragged = _draggedItem;
        DragDrop.DoDragDrop(lstFavorites, dragged, DragDropEffects.Move);
        HideInsertionLine();
        _draggedItem = null;
        if (dragged != null && Items.Contains(dragged))
            lstFavorites.SelectedItem = dragged;
    }

    private void LstFavorites_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(FavoriteEntry)))
        {
            e.Effects = DragDropEffects.None;
            HideInsertionLine();
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        UpdateInsertionLine(e.GetPosition(lstFavorites));
    }

    private void LstFavorites_DragLeave(object sender, DragEventArgs e)
    {
        Point pos = e.GetPosition(lstFavorites);
        var bounds = new Rect(0, 0, lstFavorites.ActualWidth, lstFavorites.ActualHeight);
        if (!bounds.Contains(pos))
            HideInsertionLine();
    }

    private void LstFavorites_Drop(object sender, DragEventArgs e)
    {
        HideInsertionLine();

        if (e.Data.GetData(typeof(FavoriteEntry)) is not FavoriteEntry dragged)
            return;

        int oldIndex = Items.IndexOf(dragged);
        if (oldIndex < 0) return;

        int insertIndex = _insertIndex >= 0
            ? _insertIndex
            : GetInsertIndex(e.GetPosition(lstFavorites));

        int newIndex = insertIndex;
        if (oldIndex < insertIndex)
            newIndex--;

        if (newIndex < 0 || newIndex >= Items.Count || newIndex == oldIndex)
        {
            lstFavorites.SelectedItem = dragged;
            lstFavorites.Focus();
            return;
        }

        Items.Move(oldIndex, newIndex);
        lstFavorites.SelectedItem = dragged;
        lstFavorites.Focus();
    }

    private void UpdateInsertionLine(Point pos)
    {
        _insertIndex = GetInsertIndex(pos);
        EnsureInsertionAdorner();
        _insertionAdorner?.SetY(GetInsertY(_insertIndex));
    }

    private int GetInsertIndex(Point pos)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (lstFavorites.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
                continue;

            double top = container.TranslatePoint(new Point(0, 0), lstFavorites).Y;
            if (pos.Y < top + container.ActualHeight / 2)
                return i;
        }

        return Items.Count;
    }

    private double GetInsertY(int insertIndex)
    {
        if (Items.Count == 0)
            return 2;

        if (insertIndex >= Items.Count)
        {
            if (lstFavorites.ItemContainerGenerator.ContainerFromIndex(Items.Count - 1) is ListBoxItem last)
                return last.TranslatePoint(new Point(0, last.ActualHeight), lstFavorites).Y;
            return Math.Max(2, lstFavorites.ActualHeight - 2);
        }

        if (lstFavorites.ItemContainerGenerator.ContainerFromIndex(insertIndex) is ListBoxItem item)
            return item.TranslatePoint(new Point(0, 0), lstFavorites).Y;

        return 2;
    }

    private void EnsureInsertionAdorner()
    {
        if (_insertionAdorner != null)
            return;

        var layer = AdornerLayer.GetAdornerLayer(lstFavorites);
        if (layer == null)
            return;

        var brush = TryFindResource("Brush.Selection") as Brush
                    ?? TryFindResource("Brush.TextPrimary") as Brush
                    ?? Brushes.Gray;
        _insertionAdorner = new InsertionLineAdorner(lstFavorites, brush);
        layer.Add(_insertionAdorner);
    }

    private void HideInsertionLine()
    {
        _insertIndex = -1;
        if (_insertionAdorner == null)
            return;

        var layer = AdornerLayer.GetAdornerLayer(lstFavorites);
        layer?.Remove(_insertionAdorner);
        _insertionAdorner = null;
    }

    private FavoriteEntry? GetEntryAt(Point pos)
    {
        DependencyObject? el = lstFavorites.InputHitTest(pos) as DependencyObject;
        while (el != null && el != lstFavorites)
        {
            if (el is ListBoxItem item)
                return item.DataContext as FavoriteEntry;
            el = VisualTreeHelper.GetParent(el);
        }
        return null;
    }

    private void SaveAndClose(bool result)
    {
        if (!result) SelectedResult = null;

        _settings.Favorites = Items.ToList();
        try { _settings.Save(); } catch { }

        DialogResult = result;
    }

    private (string Name, string Path)? ShowEditBox(string title, string name, string path)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };

        window.SetResourceReference(Control.BackgroundProperty, "Brush.Background");
        window.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");

        var grid = new Grid { Margin = new Thickness(15) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lblName = new TextBlock { Text = "Display name:", Margin = new Thickness(0, 0, 0, 4) };
        lblName.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        Grid.SetRow(lblName, 0);

        var txtName = new TextBox
        {
            Text = name,
            Height = 26,
            Padding = new Thickness(3),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        txtName.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        txtName.SetResourceReference(Control.BackgroundProperty, "Brush.Background");
        Grid.SetRow(txtName, 1);

        var lblPath = new TextBlock { Text = "Folder path:", Margin = new Thickness(0, 12, 0, 4) };
        lblPath.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        Grid.SetRow(lblPath, 2);

        var txtPath = new TextBox
        {
            Text = path,
            Height = 26,
            Padding = new Thickness(3),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        txtPath.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        txtPath.SetResourceReference(Control.BackgroundProperty, "Brush.Background");
        Grid.SetRow(txtPath, 3);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnOk = new Button { Content = "OK", Width = 80, Height = 30, IsDefault = true, Margin = new Thickness(0, 0, 10, 0) };
        var btnCancel = new Button { Content = "Cancel", Width = 80, Height = 30, IsCancel = true };
        btnPanel.Children.Add(btnOk);
        btnPanel.Children.Add(btnCancel);
        Grid.SetRow(btnPanel, 5);

        btnOk.Click += (s, e) => window.DialogResult = true;

        grid.Children.Add(lblName);
        grid.Children.Add(txtName);
        grid.Children.Add(lblPath);
        grid.Children.Add(txtPath);
        grid.Children.Add(btnPanel);

        window.Content = grid;

        window.SourceInitialized += (s, e) => Helpers.SetTitleBarTheme(window, ThemeManager.IsDarkTheme);
        window.Loaded += (s, e) => { txtName.Focus(); txtName.CaretIndex = txtName.Text.Length; };

        return window.ShowDialog() == true ? (txtName.Text, txtPath.Text) : null;
    }

    // Drawn over the list; does not participate in layout, so row height does not jump.
    private sealed class InsertionLineAdorner : Adorner
    {
        private readonly Pen _pen;
        private double _y;

        public InsertionLineAdorner(UIElement adorned, Brush brush) : base(adorned)
        {
            IsHitTestVisible = false;
            _pen = new Pen(brush, 2);
            if (_pen.CanFreeze) _pen.Freeze();
        }

        public void SetY(double y)
        {
            _y = y;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double y = Math.Round(_y) + 0.5;
            double w = AdornedElement.RenderSize.Width;
            dc.DrawLine(_pen, new Point(6, y), new Point(Math.Max(6, w - 6), y));
        }
    }
}
