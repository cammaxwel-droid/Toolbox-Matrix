using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System;

namespace ToolboxMatrix;

public partial class MainWindow : Window
{
    private sealed record Collection(string Key, string Name, string Mark);
    private sealed record PageState(string Search, double Scroll);

    private static readonly Collection[] Collections =
    [
        new("ai", "AI & Automation", "AI"), new("gaming", "Gaming", "G"),
        new("development", "Development", "<>"), new("media", "Media / Creative", "M"),
        new("studying", "Studying", "S"), new("security", "Security / Diagnostics", "+"),
        new("everyday", "Everyday Essentials", "E"),
        new("tools", "Tools", "T"), new("sites", "Sites", "S")
    ];

    private readonly Dictionary<string, PageState> _states = new();
    private readonly List<string> _history = ["home"];
    private int _historyIndex;
    private string _route = "home";
    private bool _rendering;
    private bool _layoutRefreshPending;
    private bool _refreshRunning;
    private DateTime _lastForegroundRefresh = DateTime.MinValue;
    private string _settingsTab = "Appearance";
    private Window? _remapDialog;
    private readonly bool _captureMode = Environment.GetCommandLineArgs().Contains("--capture-screenshots") || Environment.GetCommandLineArgs().Contains("--interactive-fixtures");
    private readonly Dictionary<string, (string Label, Key Key, ModifierKeys Modifiers)> _shortcuts = DefaultShortcuts();
    internal static string ApplicationVersion => typeof(MainWindow).Assembly.GetName().Version!.ToString(3) + ReleaseFeatures.VersionSuffix;
    private static void RequireInteractiveUi()
    {
        if (Environment.GetCommandLineArgs().Contains("--headless-verification"))
            throw new InvalidOperationException("Interactive UI is forbidden during headless verification.");
    }

    public MainWindow()
    {
        if (_captureMode || Environment.GetCommandLineArgs().Contains("--quiet-review") || Environment.GetCommandLineArgs().Contains("--verify-live-appearance")) ShowActivated = false;
        InitializeComponent();
        AppVersionLabel.Text = "v" + ApplicationVersion;
        DpiChanged += (_, _) => UpdateTitleIcon();
        if (ToolboxDataMaintenance.RecoverInterrupted(ToolboxPaths.Root)) _settingsRecovery = "An interrupted Toolbox data reset was rolled back from its recovery snapshot.";
        LoadPreferences();
        LoadCuratedCache();
        LoadSelection();
        Closing += (_, e) => { if (_installRunning) { ShowMock("An installation queue is active. Pause or stop it in Install Centre before closing Toolbox."); e.Cancel = true; return; } if (!ResolvePending()) { e.Cancel = true; return; } PersistSelection(); _retentionTimer?.Stop(); _tray?.Dispose(); };
        Activated += async (_, _) => { if (!_captureMode && !_refreshRunning && _saved["Refresh on focus"] == "On" && DateTime.UtcNow - _lastForegroundRefresh > TimeSpan.FromMinutes(_saved["Refresh interval"] == "5 minutes" ? 5 : _saved["Refresh interval"] == "15 minutes" ? 15 : 2)) { _lastForegroundRefresh = DateTime.UtcNow; await RefreshPackageStatusAsync(); } };
        Loaded += (_, _) =>
        {
            var availableWidth = Math.Max(1, SystemParameters.WorkArea.Width - 16);
            var availableHeight = Math.Max(1, SystemParameters.WorkArea.Height - 16);
            MinWidth = Math.Min(420, availableWidth);
            MinHeight = Math.Min(320, availableHeight);
            Width = Math.Min(900, availableWidth);
            Height = Math.Min(540, availableHeight);
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
            UpdateTitleIcon(); Render(); SearchBox.Focus(); Keyboard.Focus(SearchBox);
            if (!_captureMode)
            {
                var launch = LaunchCommand.Parse(ToolboxPaths.LaunchArgs(), _saved["Start page"]);
                _updatesOnly = launch.UpdatesOnly;
                if (launch.Route != "home") Navigate(launch.Route);
                UpdateTray();
                StartRetentionTimer();
                if (launch.Search.Length > 0) SearchBox.Text = launch.Search;
                if (launch.Refresh) _ = RefreshAllAsync();
                if (launch.Error is not null) ShowMock(launch.Error + " " + LaunchCommand.Usage);
            }
            if (_captureMode)
            {
                UpdateSimulationIndicator();
                if (Environment.GetCommandLineArgs().Contains("--interactive-fixtures")) InitialiseInteractiveFixtures();
                else _ = Dispatcher.BeginInvoke(async () => await CaptureScreenshotsAsync());
            }
            else if (Environment.GetCommandLineArgs().Contains("--verify-live-appearance")) _ = CaptureLiveAppearanceAsync();
            else StartDetection();
        };
    }

    private void Remember() => _states[_route] = new PageState(SearchBox.Text, ContentScroll.VerticalOffset);

    private void Navigate(string route, bool record = true, int historyTarget = -1)
    {
        route = ReleaseFeatures.ResolveRoute(route);
        if (_route == route)
        {
            // A cached hidden-category history entry can resolve to the current page.
            // Still advance Back/Forward so that entry cannot trap navigation.
            if (!record && historyTarget >= 0) { _historyIndex = historyTarget; Render(); }
            return;
        }
        if (_route == "settings" && !ResolvePending()) return;
        CancelSearchRequest();
        Remember();
        if (record)
        {
            if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(route);
            _historyIndex = _history.Count - 1;
        }
        else if (historyTarget >= 0) _historyIndex = historyTarget;
        _route = route;
        Render();
        if (_states.TryGetValue(route, out var state))
        {
            _rendering = true;
            SearchBox.Text = state.Search;
            _rendering = false;
            ApplySearch();
            Dispatcher.BeginInvoke(() => ContentScroll.ScrollToVerticalOffset(state.Scroll));
        }
        else
        {
            _rendering = true;
            SearchBox.Clear();
            _rendering = false;
            ApplySearch();
            ContentScroll.ScrollToTop();
        }
        if (!_captureMode)
        {
            if (SearchBox.Text.Trim().Length >= 2 && IsPackageSearchRoute() && (_searchTerm != SearchBox.Text.Trim() || _searchData is null)) ScheduleSearch();
            RefreshDiscoveryRoute();
            if (route == "collections") _ = PrefetchCollectionsAsync();
        }
    }

    private void Render()
    {
        BackButton.IsEnabled = _historyIndex > 0;
        RefreshButton.IsEnabled = !_refreshRunning;
        ForwardButton.IsEnabled = _historyIndex < _history.Count - 1;
        foreach (var button in new[] { HomeNav, CollectionsNav, InstalledNav, InstallNav, BackupNav, DiagnosticsNav, HelpNav, AboutNav, SettingsNav })
            button.Background = button.Name == NavigationRules.SelectedButton(_route) ? AccentTint(55) : Brushes.Transparent;
        HeroCard.Visibility = Visibility.Visible;
        SectionHint.Visibility = Visibility.Visible;
        FooterNote.Visibility = Visibility.Visible;
        AdvancedButton.Visibility = _route == "settings" ? Visibility.Visible : Visibility.Collapsed;
        HeroCard.Visibility = Visibility.Collapsed;
        SettingsSectionHeader.Visibility = _route is "settings" or "home" or "about" or "help" or "diagnostics" ? Visibility.Collapsed : Visibility.Visible;
        RefreshVisualPreview();

        UpdatePageHeadings(SearchBox.Text.Trim());

        HeroButton.Content = _route switch
        {
            "home" => "Explore Applications  →",
            "collections" => "Focus search  →",
            "backup" => "Backup and Restore  →",
            "diagnostics" => "Quick Check  →",
            "install" => "Review Selection  →",
            "installed" => "Check for Updates  →",
            "properties" => "Back to results  →",
            _ => "All collections  →"
        };
        ApplySearch();
    }

    private void ApplySearch()
    {
        UpdateSimulationIndicator();
        UpdateSelectionBadge();
        RememberListScroll();
        FooterNote.Visibility = Visibility.Visible;
        FooterNote.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        FooterNote.Background = Brushes.Transparent;
        FooterNote.HorizontalAlignment = HorizontalAlignment.Left;
        TilesPanel.Children.Clear();
        _packageList = null;
        PackageHost.Children.Clear();
        PageHeaderRow.Height = new GridLength(1, GridUnitType.Star); PackageResultsRow.Height = new GridLength(0);
        ContentScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _hasMoreResults = false;
        LoadMoreButton = new Button { Visibility = Visibility.Collapsed };
        TilesPanel.ItemWidth = _route is "settings" or "properties" or "backup" or "diagnostics" or "help" or "about" or "install" ? double.NaN : 164;
        TilesPanel.ItemHeight = _route is "settings" or "properties" or "backup" or "diagnostics" or "help" or "about" or "install" ? double.NaN : 139;
        var query = SearchBox.Text.Trim();
        UpdatePageHeadings(query);
        if (query.Length >= 2 && IsPackageSearchRoute()) { AddSearchResults(query); ApplyControlContrast(TilesPanel); ApplyControlContrast(PackageHost); return; }
        if (RenderDiscovery(query)) { }
        else if (_route == "home")
        {
            (string name, string mark, string route)[] links =
            [
                ("Collections", "C", "collections"), ("Installed Apps", "A", "installed"),
                ("Install Centre", "I", "install"), ("Backup / Restore", "B", "backup"),
                ("Diagnostics / System Care", "+", "diagnostics"), ("Settings / Shortcuts", "S", "settings"),
                ("Help / resources", "?", "help")
            ];
            foreach (var link in links.Where(x => x.name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                TilesPanel.Children.Add(CreateTile(link.name, link.mark, () => Navigate(link.route)));
            FooterNote.Text = RefreshStatusBanner.Visibility == Visibility.Visible ? "" : _discoveryStatus;
            FooterNote.Visibility = string.IsNullOrWhiteSpace(FooterNote.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        else if (_route == "collections")
        {
            foreach (var item in Collections.Where(c => CollectionMatches(c, query)))
                TilesPanel.Children.Add(CreateCollectionTile(item));
            FooterNote.Text = ReleaseFeatures.CollectionsFooter;
        }
        else if (_route == "settings") RenderSettings(query);
        else if (_route == "install") RenderInstallCentre();
        else if (_route == "backup") RenderBackup();
        else if (_route == "diagnostics") RenderDiagnostics();
        else if (_route == "about") RenderAbout();
        else if (_route == "help") RenderHelp();
        else { TilesPanel.Children.Add(CreateInfo("This destination is unavailable. Choose a workspace in the sidebar.")); }
        if (TilesPanel.Children.Count == 0)
        {
            var empty = new TextBlock { Text = "No matching items on this screen.", Foreground = Muted, Margin = new Thickness(5, 10, 0, 20) };
            TilesPanel.Children.Add(empty);
        }
        ApplyControlContrast(TilesPanel);
    }

    private static UIElement CreateTile(string name, string mark, System.Action? open, FrameworkElement? logo = null, string? subtitle = null)
    {
        var outer = new Border { Width = 151, Height = 126, Margin = new Thickness(0, 0, 12, 13), Background = Card, CornerRadius = new CornerRadius(10), BorderBrush = new SolidColorBrush(Color.FromArgb(33, 126, 156, 238)), BorderThickness = new Thickness(1) };
        var body = new Grid(); body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(94) }); body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var art = new Border { Background = TileArtworkTint, CornerRadius = new CornerRadius(9, 9, 0, 0), ClipToBounds = true };
        var canvas = new Canvas { ClipToBounds = true, Height = 94, Width = 149 };
        var main = new TextBlock { Text = mark, Foreground = TileArtwork, FontFamily = new FontFamily("Arial Black"), FontWeight = FontWeights.Black, FontSize = mark.Length > 1 ? 79 : 113, LineHeight = 90 };
        Canvas.SetLeft(main, mark.Length > 1 ? -12 : 13); Canvas.SetTop(main, -19);
        if (logo is null)
        {
            canvas.Children.Add(main);
            var bar = new Rectangle { Width = 160, Height = 14, Fill = TileArtwork, RenderTransform = new RotateTransform(-19) };
            Canvas.SetLeft(bar, -19); Canvas.SetTop(bar, 76); canvas.Children.Add(bar);
        }
        else
        {
            Canvas.SetLeft(logo, (149 - logo.Width) / 2); Canvas.SetTop(logo, (94 - logo.Height) / 2);
            canvas.Children.Add(logo);
        }
        art.Child = canvas; body.Children.Add(art);
        var label = new TextBlock { Text = name, Foreground = TextBrush, FontSize = 10, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(9, subtitle is null ? 5 : 2, 9, 0) };
        if (subtitle is null) { Grid.SetRow(label, 1); body.Children.Add(label); }
        else
        {
            var labels = new StackPanel(); labels.Children.Add(label);
            labels.Children.Add(new TextBlock { Text = subtitle, Foreground = Muted, FontSize = 8, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(9, 0, 9, 0) });
            Grid.SetRow(labels, 1); body.Children.Add(labels);
        }
        outer.Child = body;
        if (open is null) return outer;
        var button = new Button { Content = outer, Style = (Style)Application.Current.MainWindow.Resources["TileButtonStyle"] };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.MouseEnter += (_, _) => outer.BorderBrush = AccentFocusBrush;
        button.MouseLeave += (_, _) => outer.BorderBrush = new SolidColorBrush(Color.FromArgb(33, 126, 156, 238));
        button.GotKeyboardFocus += (_, _) => outer.BorderBrush = AccentFocusBrush;
        button.LostKeyboardFocus += (_, _) => outer.BorderBrush = AccentTint(125);
        button.Click += (_, _) => open();
        return button;
    }

    private static UIElement CreateInfo(string text) => new Border
    {
        Width = 310, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12),
        Background = Card, CornerRadius = new CornerRadius(8),
        Child = new TextBlock { Text = text, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap }
    };

    private UIElement CreatePropertyRow(string label, string value)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, Foreground = TextBrush, FontSize = 11, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = new TextBlock { Text = value, Foreground = TextBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(detail, 1);
        row.Children.Add(detail);
        return new Border
        {
            Width = Math.Max(230, ContentScroll.ViewportWidth - 12), Margin = new Thickness(0, 0, 0, 5), Padding = new Thickness(10, 8, 10, 8),
            Background = Card, CornerRadius = new CornerRadius(7), Child = row
        };
    }

    private static string FormatShortcut(Key key, ModifierKeys modifiers) => ShortcutCatalog.Format(key, modifiers);

    private void SetDefaultShortcuts() { _shortcuts.Clear(); foreach (var item in DefaultShortcuts()) _shortcuts[item.Key] = item.Value; }

    private void ResetShortcut(string id)
    {
        var desired = DefaultShortcuts()[id];
        if (_draftShortcuts.Any(x => x.Key != id && x.Value.Key == desired.Key && x.Value.Modifiers == desired.Modifiers))
        {
            ShowMock("That default shortcut is already assigned. Remap the other action first, or reset all shortcuts.");
            return;
        }
        _draftShortcuts[id] = desired;
        ApplySearch();
    }

    private void Remap(string id)
    {
        var prompt = CreatePrototypeDialog("Remap " + _draftShortcuts[id].Label,
            "Press the complete shortcut. Esc cancels.\nUse Ctrl or Alt with a key. Choose Apply afterwards to save. Windows may intercept Win combinations.", "Cancel");
        prompt.SizeToContent = SizeToContent.Height;
        var message = ((Grid)((Border)prompt.Content).Child).Children.OfType<TextBlock>().Single();
        _remapDialog = prompt;
        KeyEventHandler capture = (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            // Modifier key-downs arrive before the final key. They are not a completed chord.
            if (IsModifierKey(key) || e.IsRepeat) return;
            if (key == Key.Escape) { prompt.Close(); return; }
            var modifiers = Keyboard.Modifiers;
            if (!ShortcutAllowed(key, modifiers)) { message.Text = "That combination is reserved or unsupported. Press another shortcut, or Esc to cancel."; return; }
            if (_draftShortcuts.Any(x => x.Key != id && x.Value.Key == key && x.Value.Modifiers == modifiers))
            { message.Text = "Already assigned to " + _draftShortcuts.First(x => x.Key != id && x.Value.Key == key && x.Value.Modifiers == modifiers).Value.Label + ". Press another shortcut, or Esc to cancel."; return; }
            _draftShortcuts[id] = (_draftShortcuts[id].Label, key, modifiers);
            prompt.Close(); ApplySearch();
        };
        // Capture within either of our WPF windows while the modal is open. Keyboard
        // focus can still belong to the owner's editor during activation transitions.
        prompt.PreviewKeyDown += capture;
        PreviewKeyDown += capture;
        prompt.Loaded += (_, _) => ((Grid)((Border)prompt.Content).Child).Children.OfType<Button>().Single().Focus();
        prompt.Closed += (_, _) => { PreviewKeyDown -= capture; _remapDialog = null; };
        prompt.ShowDialog();
    }

    private Window CreatePrototypeDialog(string title, string message, string buttonLabel)
    {
        RequireInteractiveUi();
        var dialog = new Window
        {
            Title = title, Owner = this, Width = 410, Height = 165,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = BackBrush, Foreground = TextBrush,
            ShowInTaskbar = false, Icon = Icon
        };
        var frame = new Border { BorderBrush = Accent, BorderThickness = new Thickness(1), Background = dialog.Background };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titleStrip = new Border { Background = SideBrush, Padding = new Thickness(14, 8, 0, 0), Child = new TextBlock { Text = title, Foreground = TextBrush, FontWeight = FontWeights.SemiBold } };
        titleStrip.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) dialog.DragMove(); };
        layout.Children.Add(titleStrip);
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = TextBrush, Margin = new Thickness(15, 16, 15, 9) };
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var close = new Button { Content = buttonLabel, Background = Accent, Style = (Style)Resources[typeof(Button)], HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 15, 10), MinWidth = 70 };
        close.Click += (_, _) => dialog.Close();
        Grid.SetRow(close, 2); layout.Children.Add(close);
        frame.Child = layout; dialog.Content = frame;
        dialog.Resources = Resources;
        dialog.Loaded += (_, _) => ApplyControlContrast(dialog);
        return dialog;
    }

    private void ShowMock(string message) => CreatePrototypeDialog("Toolbox Matrix", message, "OK").ShowDialog();

    private void TitleStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = RefreshAllAsync();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private static bool IsInputFocused()
    {
        var node = Keyboard.FocusedElement as DependencyObject;
        while (node is not null)
        {
            if (node is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox or ListBox or ListView or Slider or DataGrid or Image { Focusable: true }) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11) { e.Handled = true; return; }
        if (_remapDialog is not null) return;
        if (key == Key.F12 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { Navigate("settings"); _settingsTab = "Keyboard"; ApplySearch(); e.Handled = true; return; }
        if (e.IsRepeat || PreserveFocusedInput(key, Keyboard.Modifiers, IsTextInputFocused(), IsInputFocused())) return;
        var bindings = ActiveShortcuts;
        if (key == bindings["toolbox.refresh.all"].Key && Keyboard.Modifiers == bindings["toolbox.refresh.all"].Modifiers) { e.Handled = true; _ = RefreshAllAsync(); return; }
        if (key == bindings["toolbox.search.focus"].Key && Keyboard.Modifiers == bindings["toolbox.search.focus"].Modifiers) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (ActivePreferences["Arrow navigation"] == "Off" && Keyboard.Modifiers == ModifierKeys.None && key is Key.Left or Key.Right) return;
        foreach (var shortcut in bindings)
        {
            if (shortcut.Value.Key == Key.None) continue;
            if (shortcut.Value.Key != key || shortcut.Value.Modifiers != Keyboard.Modifiers) continue;
            e.Handled = true;
            switch (shortcut.Key)
            {
                case "toolbox.search.focus": SearchBox.Focus(); SearchBox.SelectAll(); break;
                case "toolbox.go.home": Navigate("home"); break;
                case "toolbox.go.settings": Navigate("settings"); break;
                case "toolbox.go.install": Navigate("install"); break;
                case "toolbox.go.installed": Navigate("installed"); break;
                case "toolbox.go.backup": Navigate("backup"); break;
                case "toolbox.go.diagnostics": Navigate("diagnostics"); break;
                case "toolbox.go.about": Navigate("about"); break;
                case "toolbox.go.collections": Navigate("collections"); break;
                case "toolbox.go.back.alt":
                case "toolbox.go.back": if (_historyIndex > 0) BackButton_Click(this, new RoutedEventArgs()); break;
                case "toolbox.go.forward.alt":
                case "toolbox.go.forward": if (_historyIndex < _history.Count - 1) ForwardButton_Click(this, new RoutedEventArgs()); break;
                case "toolbox.refresh.all": _ = RefreshAllAsync(); break;
            }
            break;
        }
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => Navigate("home");
    private void CollectionsNav_Click(object sender, RoutedEventArgs e) => Navigate("collections");
    private void InstalledNav_Click(object sender, RoutedEventArgs e) => Navigate("installed");
    private void InstallNav_Click(object sender, RoutedEventArgs e) => Navigate("install");
    private void BackupNav_Click(object sender, RoutedEventArgs e) => Navigate("backup");
    private void DiagnosticsNav_Click(object sender, RoutedEventArgs e) => Navigate("diagnostics");
    private void HelpNav_Click(object sender, RoutedEventArgs e) => Navigate("help");
    private void AboutNav_Click(object sender, RoutedEventArgs e) => Navigate("about");
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => Navigate("settings");
    private void AdvancedButton_Click(object sender, RoutedEventArgs e) { _settingsTab = "Advanced"; ApplySearch(); }
    private void HeroButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_route)
        {
            case "collections": SearchBox.Focus(); SearchBox.SelectAll(); break;
            case "backup": _ = ExportBackupAsync(); break;
            case "diagnostics": _ = RunDiagnosticsAsync(false); break;
            case "install": CreateInstallConfirmationDialog(_installSelection, true).ShowDialog(); break;
            case "installed": _ = RefreshUpdatesAsync(); break;
            case "properties": BackButton_Click(sender, e); break;
            default: Navigate("collections"); break;
        }
    }
    private void BackButton_Click(object sender, RoutedEventArgs e) { if (_historyIndex > 0) Navigate(_history[_historyIndex - 1], false, _historyIndex - 1); }
    private void ForwardButton_Click(object sender, RoutedEventArgs e) { if (_historyIndex < _history.Count - 1) Navigate(_history[_historyIndex + 1], false, _historyIndex + 1); }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ClearSearchButton is not null) ClearSearchButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_rendering && TilesPanel is not null) ScheduleSearch();
    }
    private void SearchFocus_Click(object sender, RoutedEventArgs e) { SearchBox.Focus(); Keyboard.Focus(SearchBox); }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); Keyboard.Focus(SearchBox); }
    private void UpdateTitleIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/ToolboxMatrix;component/Assets/Toolbox Matrix.ico")).Stream;
        var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var pixels = 20 * VisualTreeHelper.GetDpi(this).DpiScaleX;
        TitleAppIcon.Source = decoder.Frames.OrderBy(frame => frame.PixelWidth >= pixels ? frame.PixelWidth - pixels : 1000 - frame.PixelWidth).First();
    }
    private static bool IsTextInputFocused()
    {
        var node = Keyboard.FocusedElement as DependencyObject;
        while (node is not null)
        {
            if (node is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox || node is ComboBox { IsEditable: true }) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
    internal static bool PreserveFocusedInput(Key key, ModifierKeys modifiers, bool textInput, bool input)
    {
        if (input && (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift) && key is not (>= Key.F1 and <= Key.F24)) return true;
        if (!textInput) return false;
        // AltGr and standard text editing belong to the field, including saved Ctrl+X.
        if (modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Alt)) return true;
        return modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt) &&
            key is Key.A or Key.C or Key.X or Key.V or Key.Z or Key.Y or Key.Back or Key.Delete or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Insert;
    }
    private void SearchScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TilesPanel is null || SearchScope.SelectedItem is not ComboBoxItem selected) return;
        _searchScope = selected.Content?.ToString() ?? "All Apps";
        ScheduleSearch();
        if (_searchScope == "Updates" && _upgradeData is null) _ = RefreshUpdatesAsync();
    }
    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Escape) { SearchBox.Clear(); e.Handled = true; } }
    private ScrollViewer ActiveResultsScroll => _packageList is not null && FindChild<ScrollViewer>(_packageList) is { } scroll ? scroll : ContentScroll;
    private void ContentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateEdgeScroll();
    private void UpdateEdgeScroll()
    {
        var scroll = ActiveResultsScroll;
        EdgeScrollBar.Maximum = scroll.ScrollableHeight;
        EdgeScrollBar.ViewportSize = scroll.ViewportHeight;
        EdgeScrollBar.LargeChange = Math.Max(32, scroll.ViewportHeight * .9);
        EdgeScrollBar.SmallChange = 24;
        EdgeScrollBar.Visibility = scroll.ScrollableHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Math.Abs(EdgeScrollBar.Value - scroll.VerticalOffset) > .1) EdgeScrollBar.Value = scroll.VerticalOffset;
    }
    private void EdgeScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ContentScroll is not null && Math.Abs(ActiveResultsScroll.VerticalOffset - e.NewValue) > .1)
            ActiveResultsScroll.ScrollToVerticalOffset(e.NewValue);
    }

    private void ContentScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) <= 1 || _layoutRefreshPending) return;
        _layoutRefreshPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _layoutRefreshPending = false;
            var offset = ContentScroll.VerticalOffset;
            ApplySearch();
            ContentScroll.ScrollToVerticalOffset(offset);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
}
