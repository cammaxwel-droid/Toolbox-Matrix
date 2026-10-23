using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;

namespace ToolboxMatrix;

internal sealed class MoreResults
{
    internal static readonly MoreResults Item = new();
    private MoreResults() { }
}
internal sealed class PackageRowControl : ContentControl
{
    public PackageRowControl() { DataContextChanged += (_, _) => { if (Application.Current.MainWindow is MainWindow main) Content = DataContext is PackageItem item ? main.BuildPackageRow(item) : DataContext is MoreResults ? main.BuildLoadMore(false) : null; }; }
}
internal sealed class PackageTileRowControl : ContentControl
{
    public PackageTileRowControl() { DataContextChanged += (_, _) => { if (DataContext is object[] items && Application.Current.MainWindow is MainWindow main) Content = main.BuildPackageTileRow(items); }; }
}
public partial class MainWindow
{
    private int _inventoryLimit = 100;
    private bool _updatesOnly;
    private ListBox? _packageList;
    private Button LoadMoreButton = new() { Visibility = Visibility.Collapsed };
    private PackageItem? _restorePackageFocus;
    private bool _restoreMoreFocus;
    private readonly Dictionary<string, double> _listOffsets = new();
    private string _listStateKey = "";
    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T result) return result; var nested = FindChild<T>(child); if (nested is not null) return nested; } return null;
    }
    private void RememberListScroll() { if (_packageList is not null && FindChild<ScrollViewer>(_packageList) is { } scroll) _listOffsets[_listStateKey] = scroll.VerticalOffset; }
    private bool IsPackageSearchRoute() => _route is "home" or "collections" || _route.StartsWith("collection:") && !CuratedExtras.IsExtra(_route[11..]);
    private bool Selected(PackageItem item) => _installSelection.Any(x => x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) && x.Source.Equals(item.Source, StringComparison.OrdinalIgnoreCase));
    private bool Eligible(PackageItem item) => InstallationStatus(item) == "Not Installed" && Discovery.SafeIdentity(item.Id, item.Source) && item.Publisher != "Unknown";
    private PackageItem? UpgradeFor(PackageItem item) => _upgradeData?.Items.FirstOrDefault(x => x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) && x.Source.Equals(item.Source, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Available));
    private async void SelectUpdate(PackageItem item)
    {
        var generation = _catalogGeneration;
        try
        {
            var detail = _captureMode ? item : await Discovery.DetailsAsync(item, CancellationToken.None);
            if (generation != _catalogGeneration) return;
            if (detail.Publisher == "Unknown") { ShowMock("The update publisher could not be confirmed."); return; }
            if (!Selected(item)) _installSelection.Add(detail with { Operation = "Update", RequestedVersion = item.Available });
            PersistSelection(); ApplySearch();
        }
        catch (Exception ex) { ShowMock("Could not select this update: " + ex.Message); }
    }
    private void ToggleSelection(PackageItem item)
    {
        if (Selected(item)) _installSelection.RemoveAll(x => x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) && x.Source.Equals(item.Source, StringComparison.OrdinalIgnoreCase));
        else if (Eligible(item)) AddToInstallSelection(item);
        PersistSelection(); ApplySearch();
    }
    private void AddPackageToolbar(IEnumerable<PackageItem> source)
    {
        TilesPanel.ItemWidth = double.NaN; TilesPanel.ItemHeight = double.NaN;
        var items = source.ToArray();
        var toolbar = new WrapPanel { Width = Math.Max(240, ContentScroll.ViewportWidth - 18), Margin = new Thickness(0, 2, 0, 7) };
        void Action(string label, Action action)
        {
            var button = new Button { Content = label, Background = Card, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 5, 4), FontSize = 11 };
            button.Click += (_, _) => action(); toolbar.Children.Add(button);
        }
        void SelectAll() { foreach (var item in items.Where(Eligible)) if (!Selected(item)) _installSelection.Add(item); PersistSelection(); ApplySearch(); }
        Action(_collectionListView ? "Tiles" : "Compact List", () => { _collectionListView = !_collectionListView; _saved["Collection view"] = _draft["Collection view"] = _collectionListView ? "Compact List" : "Tiles"; PersistCurrentPreferences(); ApplySearch(); });
        if (_route != "installed") Action("Add All", SelectAll);
        if (_route != "installed") Action("Review selection (" + _installSelection.Count + ")", () => Navigate("install"));
        if (_route == "installed") Action(_updatesOnly ? "Show all installed" : "Show updates", () => { _updatesOnly = !_updatesOnly; ApplySearch(); });
        TilesPanel.Children.Add(toolbar);
    }
    private void RenderPackageItems(IEnumerable<PackageItem> source, string query)
    {
        var items = source.Where(x => query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(x => (x.Id.ToUpperInvariant(), x.Source.ToUpperInvariant())).ToArray();
        if (_saved["Search order"] == "Name" && !(IsPackageSearchRoute() && SearchBox.Text.Trim().Length >= 2))
            items = items.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (items.Length == 0) return;
        TilesPanel.ItemWidth = double.NaN; TilesPanel.ItemHeight = double.NaN;
        // The header cannot scroll. Only this bounded, recycled results viewport owns scrolling.
        PageHeaderRow.Height = GridLength.Auto; PackageResultsRow.Height = new GridLength(1, GridUnitType.Star);
        ContentScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var entries = items.Cast<object>().Concat(_hasMoreResults ? new object[] { MoreResults.Item } : []).ToArray();
        var columns = Math.Max(1, (int)Math.Floor(Math.Max(240, ContentScroll.ViewportWidth - 18) / 164));
        var list = new ListBox { ItemsSource = _collectionListView ? entries : entries.Chunk(columns).Select(row => row.ToArray()).ToArray(),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = TextBrush, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(list, true); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
        list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(_collectionListView ? typeof(PackageRowControl) : typeof(PackageTileRowControl)) };
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }));
        list.ItemContainerStyle = style;
        _listStateKey = _route + "|" + SearchBox.Text + (_collectionListView ? "|list" : "|tiles");
        var offset = _listOffsets.GetValueOrDefault(_listStateKey);
        var focus = _restorePackageFocus; var focusMore = _restoreMoreFocus; _restorePackageFocus = null; _restoreMoreFocus = false;
        list.Loaded += (_, _) =>
        {
            if (FindChild<ScrollViewer>(list) is { } scroll)
            {
                scroll.ScrollToVerticalOffset(offset);
                scroll.ScrollChanged += (_, _) => { if (ReferenceEquals(_packageList, list)) UpdateEdgeScroll(); };
            }
            ApplyControlContrast(list); UpdateEdgeScroll();
            Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_packageList, list)) return;
                list.UpdateLayout();
                if (focus is not null) VisualDescendants<FrameworkElement>(list).FirstOrDefault(x => x.Focusable && Equals(x.Tag, focus))?.Focus();
                else if (focusMore && LoadMoreButton.IsVisible) LoadMoreButton.Focus();
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        };
        list.PreviewKeyDown += (_, e) =>
        {
            if (PackageSelectionActions.InsideButton(e.OriginalSource as DependencyObject) && e.Key is Key.Space or Key.Enter && !LoadMoreButton.IsKeyboardFocusWithin) return;
            if (e.Key == Key.End && Keyboard.Modifiers == ModifierKeys.Control)
            {
                list.ScrollIntoView(list.Items[list.Items.Count - 1]);
                Dispatcher.BeginInvoke(() => { list.UpdateLayout(); if (LoadMoreButton.IsVisible) LoadMoreButton.Focus(); });
                e.Handled = true; return;
            }
            if (LoadMoreButton.IsKeyboardFocusWithin)
            {
                if (e.Key is Key.Enter or Key.Space) { _ = LoadMoreAsync(); e.Handled = true; }
                if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) e.Handled = true;
                return;
            }
            if (list.SelectedItem is MoreResults && e.Key is Key.Enter or Key.Space)
            { _ = LoadMoreAsync(); e.Handled = true; return; }
            if (list.SelectedItem is not PackageItem item) return;
            if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)
            { var context = CreatePropertiesContext(item); list.ContextMenu = context; context.PlacementTarget = list; context.Placement = PlacementMode.Center; context.IsOpen = true; e.Handled = true; }
            else if (e.Key == Key.Enter) { OpenPackage(item); e.Handled = true; }
            else if (e.Key == Key.Space && (Eligible(item) || Selected(item))) { ToggleSelection(item); e.Handled = true; }
        };
        list.SelectionChanged += (_, _) =>
        {
            list.ContextMenu = null;
            foreach (var border in VisualDescendants<PackageRowControl>(list).Select(x => x.Content).OfType<Border>())
                if (border.Tag is PackageItem package) border.BorderBrush = Selected(package) || Equals(list.SelectedItem, package) ? AccentFocusBrush : Brushes.Transparent;
        };
        _packageList = list; PackageHost.Children.Add(list);
    }

    internal FrameworkElement BuildLoadMore(bool tile)
    {
        var button = new Button { Background = Accent, Foreground = AccentText, MinHeight = tile ? 126 : 56,
            HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = tile ? new Thickness(0, 0, 12, 13) : new Thickness(0, 5, 0, 8), Padding = new Thickness(8),
            FontSize = 14, FontWeight = FontWeights.SemiBold, Tag = MoreResults.Item };
        if (tile) button.Width = 151;
        button.SetResourceReference(Control.FocusVisualStyleProperty, "AccentFocusVisual");
        System.Windows.Automation.AutomationProperties.SetName(button, "Load More");
        System.Windows.Automation.AutomationProperties.SetHelpText(button, "Append up to 99 more apps. This is a command, not an app.");
        button.ContextMenuOpening += (_, e) => e.Handled = true;
        button.GotKeyboardFocus += (_, _) => { if (_packageList is not null) { if (_packageList.ContextMenu is { } menu) menu.IsOpen = false; _packageList.ContextMenu = null; } };
        button.Click += LoadMore_Click;
        LoadMoreButton = button; UpdateLoadMoreVisibility();
        return button;
    }

    internal FrameworkElement BuildPackageTileRow(IReadOnlyList<object> items)
    {
        var row = new WrapPanel { Width = Math.Max(240, ContentScroll.ViewportWidth - 18) };
        foreach (var entry in items)
        {
            if (entry is MoreResults) { row.Children.Add(BuildLoadMore(true)); continue; }
            var item = (PackageItem)entry;
            var status = InstallationStatus(item); var mark = item.Name.Length == 0 ? "?" : item.Name[..1].ToUpperInvariant();
            var tile = (FrameworkElement)CreateTile(item.Name, mark, () => OpenPackage(item), subtitle: (Selected(item) ? "Selected · " : "") + status);
            if (tile is Button { Content: Border outer })
            {
                outer.BorderBrush = Selected(item) ? AccentFocusBrush : AccentTint(125);
                outer.BorderThickness = new Thickness(Selected(item) ? 2 : 1);
                tile.MouseLeave += (_, _) => { outer.BorderBrush = Selected(item) ? AccentFocusBrush : AccentTint(125); };
            }
            if (status == "Installed") tile.Opacity = .72;
            AttachPackageContext(tile, item); row.Children.Add(tile);
        }
        return row;
    }
    internal FrameworkElement BuildPackageRow(PackageItem item)
    {
        var selected = Selected(item); var compact = ActivePreferences["Settings spacing"] == "Compact";
        var grid = new Grid { Margin = new Thickness(3, compact ? 2 : 7, 3, compact ? 2 : 7) };
        foreach (var width in new[] { new GridLength(27), new GridLength(1, GridUnitType.Star), new GridLength(92), GridLength.Auto }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        grid.Children.Add(new TextBlock { Text = "▰", Foreground = TileArtwork, FontSize = 21, VerticalAlignment = VerticalAlignment.Center });
        var names = new StackPanel { Margin = new Thickness(3, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = item.Name, FontSize = 12, Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!compact) names.Children.Add(new TextBlock { Text = item.Id + (ActivePreferences["Source labels"] == "Always show" ? " · " + item.Source : ""), FontSize = 10, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(names, 1); grid.Children.Add(names);
        var status = new TextBlock { Text = InstallationStatus(item), Foreground = TextBrush, FontSize = 10, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(status, 2); grid.Children.Add(status);
        var select = new Button { Content = selected ? "Selected ✓" : "Select", IsEnabled = Eligible(item) && !selected, Padding = new Thickness(6, 3, 6, 3), Background = selected ? Accent : Card, FontSize = 10 };
        Border? row = null;
        System.Windows.Automation.AutomationProperties.SetName(select, (selected ? "Selected " : "Select ") + item.Name);
        PackageSelectionActions.BindSelect(select, () => Eligible(item) && !Selected(item), () => AddToInstallSelection(item), () =>
        {
            // Update this control in place so pointer-up cannot hit a newly-built parent row.
            select.Content = Selected(item) ? "Selected ✓" : "Select";
            select.IsEnabled = Eligible(item) && !Selected(item);
            select.Background = Selected(item) ? Accent : Card;
            select.Foreground = Selected(item) ? AccentText : TextBrush;
            System.Windows.Automation.AutomationProperties.SetName(select, (Selected(item) ? "Selected " : "Select ") + item.Name);
            if (row is not null) row.BorderBrush = Selected(item) ? AccentFocusBrush : Brushes.Transparent;
            UpdateSelectionBadge();
        });
        Grid.SetColumn(select, 3); grid.Children.Add(select);
        row = new Border { Child = grid, Tag = item, Background = Card, BorderBrush = selected ? AccentFocusBrush : Brushes.Transparent, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, compact ? 1 : 4), Focusable = true };
        row.MouseLeftButtonUp += (_, e) => PackageSelectionActions.OpenCard(e.OriginalSource as DependencyObject, () => OpenPackage(item));
        row.GotKeyboardFocus += (_, _) => row.BorderBrush = AccentFocusBrush;
        row.LostKeyboardFocus += (_, _) => row.BorderBrush = Selected(item) || Equals(_packageList?.SelectedItem, item) ? AccentFocusBrush : Brushes.Transparent;
        row.KeyDown += (_, e) => { if (e.Key == Key.Enter && !PackageSelectionActions.InsideButton(e.OriginalSource as DependencyObject)) { OpenPackage(item); e.Handled = true; } };
        row.Loaded += (_, _) => ApplyControlContrast(row);
        AttachPackageContext(row, item); return row;
    }
    private void AttachPackageContext(FrameworkElement target, PackageItem item)
    {
        target.Tag = item;
        System.Windows.Automation.AutomationProperties.SetName(target, item.Name);
        System.Windows.Automation.AutomationProperties.SetHelpText(target, item.Id + " · " + InstallationStatus(item) + ". Right-click for Properties.");
        target.ContextMenu = CreatePropertiesContext(item);
    }
    private ContextMenu CreatePropertiesContext(PackageItem item)
    {
        var menu = new ContextMenu { Background = Brushes.White, Foreground = Brushes.Black, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Padding = new Thickness(0), HasDropShadow = false };
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.BackgroundProperty, Brushes.White); border.SetValue(Border.BorderBrushProperty, Brushes.Black); border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter))); menu.Template = new ControlTemplate(typeof(ContextMenu)) { VisualTree = border };
        var panel = new StackPanel { Width = 340, Margin = new Thickness(11) };
        panel.Children.Add(new TextBlock { Text = item.Name + " — Properties", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) });
        var match = InventoryMatcher.Resolve(item, _installedData, _windowsInventory);
        foreach (var (label, value) in new[] { ("Package ID", item.Id), ("Publisher", item.Publisher), ("Source", item.Source), ("Installed", match.State + (match.Version.Length > 0 ? " · " + match.Version : "")), ("Available", item.Available.Length > 0 ? item.Available : item.Version), ("Evidence", match.Evidence) })
            panel.Children.Add(new TextBlock { Text = label + ": " + (value.Length == 0 ? "Unknown" : value), Foreground = Brushes.Black, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Add(string label, Action action)
        {
            var b = new Button { Content = label, Foreground = Brushes.Black, Background = Brushes.White, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 6, 0), Style = null };
            b.Click += (_, _) => { menu.IsOpen = false; action(); }; actions.Children.Add(b);
        }
        Add("Full Properties", () => OpenPackage(item));
        if (Eligible(item) || Selected(item)) Add(Selected(item) ? "Remove selection" : "Add selection", () => ToggleSelection(item));
        if (UpgradeFor(item) is { } update) Add("Select update", () => SelectUpdate(update));
        panel.Children.Add(actions);
        var entry = new MenuItem { Header = panel, StaysOpenOnClick = true, Focusable = false, Padding = new Thickness(0) };
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("Header") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        entry.Template = new ControlTemplate(typeof(MenuItem)) { VisualTree = presenter }; menu.Items.Add(entry);
        return menu;
    }
}
