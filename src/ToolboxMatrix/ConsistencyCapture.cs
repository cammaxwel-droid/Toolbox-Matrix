using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ToolboxMatrix;

public partial class MainWindow
{
    private async Task CaptureSmallPatchChecksAsync(Func<string, Task> shot, Func<FrameworkElement, string, Task> elementShot, Action<bool, string> check)
    {
        async Task Idle()
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            UpdateLayout(); await Task.Delay(180);
        }
        var runner = new CaptureRunner(); Discovery.Runner = runner;
        var shortcutsBefore = _shortcuts.ToDictionary(x => x.Key, x => x.Value);
        var dpi = VisualTreeHelper.GetDpi(this);
        check(true, $"Actual monitor DPI: {dpi.PixelsPerInchX:0}x{dpi.PixelsPerInchY:0}; size/text stress cases are in-process layout checks, not OS DPI changes");
        Navigate("home"); await RefreshAllAsync(false); await Idle();
        var homeTiles = TilesPanel.Children.Count;
        var liveStamp = RefreshStatusBanner;
        foreach (var theme in new[] { "Void Dark", "Light", "System", "Void Dark" })
        {
            _draft["Theme"] = theme; _preview = true; RefreshVisualPreview(); await Idle();
            check(ReferenceEquals(RefreshStatusBanner, liveStamp) && ReferenceEquals(RefreshStatusHost.Background, BackBrush), theme + ": existing stamp surface follows Background without a refresh, rerender or restart");
            check(ColourHex(((SolidColorBrush)RefreshStatusBanner.Foreground).Color) == "#80C4B8" && RefreshStatusBanner.Background == Brushes.Transparent, theme + ": exact signature teal lettering has no separate dark rectangle");
            check(VisualDescendants<TextBlock>(RootGrid).Count(t => t.IsVisible && t.Text.StartsWith("Refreshed at ")) == 1 && TilesPanel.Children.Count == homeTiles, theme + ": exactly one timestamp; all seven Home dashboard tiles retained");
            var edge = (System.Windows.Media.Effects.DropShadowEffect)RefreshStatusBanner.Effect;
            check(SettingsRegistry.Contrast("#80C4B8", ColourHex(BackBrush.Color)) >= 4.5 || edge.Opacity == 1 && SettingsRegistry.Contrast("#80C4B8", ColourHex(edge.Color)) >= 4.5, theme + ": contrast comes from the fill or neutral glyph edge, not the container");
            if (theme != "Void Dark" || _draft["Accent"] != "Blue")
                await shot("patch-home-" + theme.Replace(" ", "").ToLowerInvariant() + ".png");
            _draft["Accent"] = "Blue"; RefreshVisualPreview();
        }
        ResetDraft();
        RefreshStatusBanner.Text = "Refreshed at previous value";
        await RefreshAllAsync(false); await Idle();
        check(RefreshStatusBanner.Text == "Refreshed at " + DateTime.Now.ToString("t"), "another refresh replaces the timestamp with the current value");
        runner.FailInventory = true; await RefreshAllAsync(false); await Idle();
        check(RetryRefreshButton.IsVisible && RefreshStatusBanner.Effect is null && !RefreshStatusBanner.Text.StartsWith("Refreshed at "), "incomplete refresh retains readable status and Retry");
        runner.FailInventory = false; await RefreshAllAsync(false);

        foreach (var layout in new[] { (900d, 540d, "Standard"), (900d, 540d, "Extra Large"), (640d, 420d, "Extra Large"), (420d, 360d, "Extra Large") })
        {
            Width = layout.Item1; Height = layout.Item2; _saved["Text size"] = layout.Item3; ResetDraft();
            Navigate("settings"); _settingsTab = "Keyboard"; ApplySearch(); await Idle();
            ContentScroll.ScrollToTop(); await Idle();
            var categories = TilesPanel.Children.OfType<WrapPanel>().First();
            var buttons = categories.Children.OfType<Button>().ToArray();
            var rows = buttons.GroupBy(b => Math.Round(b.TranslatePoint(new Point(), categories).Y, 1)).ToArray();
            var label = $"{layout.Item1:0}x{layout.Item2:0}-{layout.Item3.Replace(" ", "")}";
            check(buttons.Length == 12 && buttons.Any(b => Equals(b.Content, "Keyboard")) && buttons.All(b => b.Focusable && KeyboardNavigation.GetIsTabStop(b)), label + ": all twelve categories present and keyboard accessible");
            check(rows.Max(r => r.Count()) - rows.Min(r => r.Count()) <= 1 && (rows.Length == buttons.Length || rows.All(r => r.Count() > 1)), label + ": balanced category rows with no orphan Keyboard category");
            check(categories.ActualWidth <= ContentScroll.ViewportWidth && buttons.All(b =>
            {
                var text = VisualDescendants<TextBlock>(b).First();
                var p = b.TranslatePoint(new Point(), categories);
                return p.X >= 0 && p.X + b.ActualWidth <= categories.ActualWidth + .5 && text.ActualWidth <= b.ActualWidth - b.Padding.Left - b.Padding.Right + .5;
            }), label + ": category labels fit the viewport without horizontal scrolling or clipping");
            if (layout.Item1 >= 640) await shot("patch-keyboard-" + label + ".png");
            _settingsTab = "Advanced";
            foreach (var key in new[] { "advanced-window", "advanced-windows", "advanced-recovery" }) _disclosures[key] = true;
            ApplySearch(); await Idle();
            foreach (var fraction in new[] { 0d, .5, 1d })
            {
                ContentScroll.ScrollToVerticalOffset(ContentScroll.ScrollableHeight * fraction); await Idle();
                var stampBottom = RefreshStatusBanner.TranslatePoint(new Point(0, RefreshStatusBanner.ActualHeight), this).Y;
                var bodyTop = ContentScroll.TranslatePoint(new Point(), this).Y;
                check(bodyTop - stampBottom >= 14 * ScaleFactor && PageBody.ClipToBounds && ContentScroll.ClipToBounds, label + $" Advanced scroll {fraction:0.0}: fixed status has a clear gap and body clips below it");
                if (layout.Item1 == 900 && layout.Item3 == "Standard" || layout.Item1 == 640 && fraction == .5)
                    await shot($"patch-advanced-{label}-{fraction:0.0}.png");
            }
        }
        Width = 900; Height = 540; _saved["Text size"] = "Standard"; ResetDraft();
        Navigate("settings"); _settingsTab = "Appearance"; ApplySearch(); await Idle();
        var accentBefore = Accent.Color; var artBefore = TileArtwork.Color; var subBefore = Subheadings.Color;
        _draft["Theme"] = "Light"; _preview = true; RefreshVisualPreview(); await Idle();
        check(Accent.Color == accentBefore && TileArtwork.Color == artBefore && ReferenceEquals(RefreshStatusHost.Background, BackBrush), "Background preview preserves independent accent/artwork choices and updates the existing status surface");
        ResetDraft(); ApplySearch(); await Idle();
        check(Subheadings.Color == subBefore && Accent.Color == accentBefore && TileArtwork.Color == artBefore, "Cancel preview restores all colour roles");
        check(VisualDescendants<FrameworkElement>(RootGrid).All(e => e.ToolTip is null && !ToolTipService.GetIsEnabled(e)), "Settings and template controls retain global passive-tooltip suppression");
        Navigate("help"); _disclosures["help-keyboard"] = true; _disclosures["help-commands"] = true; ApplySearch(); await Idle();
        check(shortcutsBefore.All(x => _shortcuts[x.Key] == x.Value) && LaunchCommand.Commands.All(c => VisualDescendants<TextBlock>(TilesPanel).Any(t => t.Text.StartsWith("tlbxm " + c.Name + " · "))), "keyboard bindings unchanged and Help still lists parser-backed commands");
        var package = new PackageItem("Steam", "Valve.Steam", "1", "", "winget", "Valve");
        foreach (var compact in new[] { false, true })
        {
            _collectionListView = compact; Navigate("collection:gaming"); Render(); await Idle();
            var menu = CreatePropertiesContext(package); menu.PlacementTarget = this; menu.IsOpen = true; await Idle();
            check(menu.Background == Brushes.White && menu.BorderBrush == Brushes.Black && menu.BorderThickness == new Thickness(1) && !menu.HasDropShadow && VisualDescendants<Border>(menu).All(b => b.CornerRadius == new CornerRadius(0)), "Properties stays square, white and black-bordered in " + (compact ? "Compact List" : "Tiles"));
            await elementShot(menu, "patch-properties-" + (compact ? "compact" : "tiles") + ".png"); menu.IsOpen = false;
        }
        _saved = new(Defaults); ResetDraft();
    }

    private async Task CaptureCohesiveChecksAsync(Func<string, Task> page, Func<string, Task> shot, Func<FrameworkElement, string, Task> elementShot, Action<bool, string> check)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Idle() { await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); UpdateLayout(); await Task.Delay(120); }
        bool NoTips() => !PresentationSource.CurrentSources.Cast<PresentationSource>().Any(source => source.RootVisual is not null && VisualDescendants<ToolTip>(source.RootVisual).Any(t => t.IsOpen));
        _saved = new(Defaults); _customAccent = _customTileColour = _customSubheadingsColour = "#7F298F"; SetDefaultShortcuts(); ResetDraft();
        var runner = new CaptureRunner(); Discovery.Runner = runner;
        WindowsInventory.Provider = _ => Task.FromResult(new WindowsInventorySnapshot([], true, true, "SIMULATED inventory", DateTime.UtcNow));
        var jan = new PackageItem("Jan", "Jan.Jan", "1", "", "winget", "Jan");
        _installedData = new([], "SIMULATED reliable", true); _windowsInventory = new([], true, true, "SIMULATED", DateTime.UtcNow);
        _installSelection.Clear(); _installSelection.Add(jan); _installStage = "";
        Navigate("install"); SetCaptureSearch(""); await Idle();
        var heading = VisualDescendants<TextBlock>(TilesPanel).First(t => t.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Text == "Current Selection"));
        check(ReferenceEquals(heading.Foreground, Subheadings) && heading.Inlines.OfType<System.Windows.Documents.Run>().Last().Foreground == Muted && heading.Inlines.OfType<System.Windows.Documents.Run>().Last().Text.EndsWith("(1 package)"), "Current Selection is a subheading and singular package count is neutral metadata");
        check(!VisualDescendants<TextBlock>(TilesPanel).Any(t => t.Text.Contains("Select verified packages") || t.Text.Contains("Add apps from")), "selected Install Centre has no stale empty-state box");
        await page("cohesive-install-one-selected");
        Navigate("home"); SetCaptureSearch("kept query"); SearchBox.CaretIndex = 3; await Idle();
        var historyCount = _history.Count; var setting = _saved["Accent"];
        Click(SearchFocusButton); await Idle();
        check(SearchBox.IsKeyboardFocused && SearchBox.CaretIndex == 3 && SearchBox.Text == "kept query" && _history.Count == historyCount, "magnifier focuses search without changing query, caret, route or history");
        check(ClearSearchButton.IsVisible, "search X is visible only for nonempty text");
        await shot("cohesive-search-with-clear.png");
        Click(ClearSearchButton); await Idle();
        check(SearchBox.Text == "" && SearchBox.IsKeyboardFocused && ClearSearchButton.Visibility == Visibility.Collapsed && _installSelection.Count == 1 && _saved["Accent"] == setting, "search X clears only query and keeps focus, selection and settings");
        await shot("cohesive-search-cleared-focused.png");
        check(TitleAppIcon.Source is System.Windows.Media.Imaging.BitmapFrame frame && frame.PixelWidth >= 20 * VisualTreeHelper.GetDpi(this).DpiScaleX && Icon is not null, "toolbar chooses an original ICO frame for the current monitor DPI and window icon is set");
        check(VisualDescendants<TextBlock>(RootGrid).Count(t => t.Text == "Toolbox Matrix") == 1 && !VisualDescendants<TextBlock>(RootGrid).Any(t => t.Text == "T"), "single top-toolbar title/icon; no sidebar brand duplicate");
        check(HelpNav.FontWeight == FontWeights.Light && AboutNav.FontWeight == FontWeights.Light && SettingsNav.FontWeight == FontWeights.Light && PageTitle.FontWeight == FontWeights.SemiBold, "only the three bottom sidebar tabs use lighter type");

        foreach (var route in new[] { "home", "collections", "collection:gaming", "installed", "install", "backup", "diagnostics", "help", "about", "settings" })
        {
            Navigate(route); SetCaptureSearch(""); Render(); await Idle();
            check(VisualDescendants<FrameworkElement>(RootGrid).All(e => e.ToolTip is null && !ToolTipService.GetIsEnabled(e)), route + ": all visible and template controls have tooltip service disabled and no passive content");
            foreach (var owner in VisualDescendants<FrameworkElement>(RootGrid).Where(e => e.IsVisible && e.Focusable).Take(6))
            { owner.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent }); owner.Focus(); }
            await Task.Delay(800); check(NoTips(), route + ": mouse/focus dwell leaves no WPF tooltip popup");
        }
        typeof(Window).GetMethod("OnDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(this, [EventArgs.Empty]);
        await Task.Delay(1000); check(NoTips(), "global tooltip suppression survives deactivation");
        _settingsTab = "Search"; ApplySearch(); await Idle();
        check(!VisualDescendants<TextBlock>(TilesPanel).Any(t => t.Text == "Search delay"), "ordinary Search settings omit delay");
        Click(AdvancedButton); await Idle();
        foreach (var expander in VisualDescendants<Expander>(TilesPanel)) expander.IsExpanded = true;
        await Idle();
        check(VisualDescendants<TextBlock>(TilesPanel).Any(t => t.Text == "Search delay") && VisualDescendants<Button>(TilesPanel).Any(b => Equals(b.Content, "Clear Cache")) && VisualDescendants<Button>(TilesPanel).Any(b => Equals(b.Content, "Clear Data")), "single Advanced cog exposes search timing and both Toolbox cleanup actions");
        await page("cohesive-advanced-operations");

        _draftShortcuts["toolbox.go.home"] = ("Go to Home", Key.O, ModifierKeys.Control | ModifierKeys.Shift);
        check(ApplyDraft(), "remapped shortcut draft saves atomically");
        SetDefaultShortcuts(); LoadPreferences();
        check(_shortcuts["toolbox.go.home"].Key == Key.O && _shortcuts["toolbox.go.home"].Modifiers == (ModifierKeys.Control | ModifierKeys.Shift), "saved custom shortcut reloads through the real preference parser");
        Navigate("help"); _disclosures["help-keyboard"] = true; _disclosures["help-commands"] = true; ApplySearch(); await Idle();
        check(VisualDescendants<TextBlock>(TilesPanel).Any(t => t.Text == "Ctrl+Shift+O · Go to Home") && !VisualDescendants<TextBlock>(TilesPanel).Any(t => t.Text == "Ctrl+H · Go to Home"), "Help shows the saved remap instead of its default");
        check(LaunchCommand.Commands.All(c => VisualDescendants<TextBlock>(TilesPanel).Any(t => t.Text.StartsWith("tlbxm " + c.Name + " · "))) && VisualDescendants<TextBlock>(TilesPanel).Any(t => t.Text.StartsWith("tlbxm search <name>")), "Toolbox Commands lists the complete parser-backed command set");
        await page("cohesive-help-saved-shortcuts-and-commands");

        foreach (var theme in new[] { "Void Dark", "Light" })
        {
            _saved["Theme"] = theme; _saved["Accent"] = "Blue"; _saved["Subheadings Colour"] = "Amber"; ResetDraft();
            Navigate("home"); SetCaptureSearch(""); await RefreshAllAsync(false); await Idle();
            check(RefreshStatusBanner.Text.StartsWith("Refreshed at ") && ReferenceEquals(RefreshStatusBanner.Foreground, RefreshTimestamp) && FooterNote.Visibility == Visibility.Collapsed && ColourHex(RefreshTimestamp.Color) == "#80C4B8", theme + ": one successful timestamp remains exact signature teal independently of both colour settings");
            check(ReferenceEquals(RefreshStatusHost.Background, BackBrush) && (SettingsRegistry.Contrast("#80C4B8", ColourHex(BackBrush.Color)) >= 4.5 || RefreshStatusBanner.Effect is System.Windows.Media.Effects.DropShadowEffect { Opacity: 1 }), theme + ": signature timestamp uses the themed surface and contrast-aware glyph edge");
            await shot("cohesive-teal-timestamp-" + theme.Replace(" ", "").ToLowerInvariant() + ".png");
        }
        runner.FailInventory = true; await RefreshAllAsync(false); await Idle();
        check(RefreshStatusBanner.Text.Contains("timed out after 75 seconds") && RetryRefreshButton.IsVisible && !ReferenceEquals(RefreshStatusBanner.Foreground, RefreshTimestamp), "failed refresh names its actual timeout and exposes Retry instead of claiming a refreshed timestamp");
        await shot("cohesive-refresh-timeout-and-retry.png"); runner.FailInventory = false;

        Navigate("settings"); _settingsTab = "Advanced"; ResetDraft(); ApplySearch();
        var preferencesBefore = System.IO.File.ReadAllText(SettingsPath); var selectionBefore = _installSelection.ToArray();
        var cancel = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        Task? confirmationShot = null;
        cancel.Tick += async (_, _) =>
        {
            cancel.Stop(); var dialog = Application.Current.Windows.OfType<Window>().First(w => w.Title == "Clear Toolbox Data");
            confirmationShot = elementShot(dialog, "cohesive-clear-data-confirmation.png"); await confirmationShot; dialog.Close();
        };
        cancel.Start(); await ClearToolboxDataAsync(true); if (confirmationShot is not null) await confirmationShot;
        check(System.IO.File.ReadAllText(SettingsPath) == preferencesBefore && _installSelection.SequenceEqual(selectionBefore), "Cancel Clear Data keeps the saved profile and selection unchanged");
        runner.Delay = 250; var lateCatalogue = LoadCollectionAsync("gaming", CancellationToken.None);
        ResetToolboxMemory(false); await lateCatalogue;
        check(_collectionData.Count == 0 && _installedData is null && _searchData is null && _installSelection.SequenceEqual(selectionBefore) && System.IO.File.ReadAllText(SettingsPath) == preferencesBefore, "Clear Cache invalidates memory, rejects late catalogue writes and preserves saved settings/selection");
        runner.Delay = 0;
        _states["home"] = new("private old search", 50); _lastReportPath = "old report";
        ResetToolboxMemory(true);
        check(_installSelection.Count == 0 && _states.Count == 0 && _lastReportPath is null && _customReportsFolder is null && _saved.SequenceEqual(Defaults) && _shortcuts["toolbox.go.home"].Key == Key.H && _tray is null, "Clear Data also resets in-memory custom inputs, navigation, selection, shortcuts and optional tray");
        await page("cohesive-clean-data-state");
        _disclosures.Clear(); _saved = new(Defaults); ResetDraft(); Navigate("home"); Render();
    }

    private async Task CaptureConsistencyChecksAsync(Func<string, Task> page, Func<string, Task> shot, Func<FrameworkElement, string, Task> elementShot, Action<bool, string> check)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Idle() { await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); UpdateLayout(); await Task.Delay(100); }
        string Hex(Brush brush) => ColourHex(((SolidColorBrush)brush).Color);
        _saved = new(Defaults); _customAccent = _customTileColour = "#7F298F"; ResetDraft();
        _collectionListView = false; _installSelection.Clear(); SetCaptureSearch(""); Navigate("home"); Render();
        check(PageTitle.Text == "Home" && PageIntro.Visibility == Visibility.Collapsed && Eyebrow.Visibility == Visibility.Collapsed, "Home has one concise heading");
        await page("consistency-01-home");
        Navigate("collections"); check(PageIntro.Visibility == Visibility.Collapsed, "Collections has one heading without duplicate copy");
        await page("consistency-02-collections");
        Navigate("install"); check(PageTitle.Text == "Install Centre" && PageIntro.Visibility == Visibility.Collapsed, "Install Centre has its exact page heading");
        await page("consistency-03-install-centre");
        Navigate("installed"); await Idle();
        check(!VisualDescendants<Button>(TilesPanel).Any(b => Equals(b.Content,"Add All") || Equals(b.Content,"Install All")), "Installed Apps has no Select All or Install All");
        await page("consistency-04-installed-inventory");
        Navigate("about"); await Idle();
        check(!VisualDescendants<TextBlock>(TilesPanel).Any(t => t.Text.Contains("Concept and direction") || t.Text.Contains("built with .NET")), "About omits the unwanted creator and technology credits");
        check(VisualDescendants<Expander>(TilesPanel).Any(e => Equals(e.Header,"Licensing and independence") && !e.IsExpanded), "essential licensing and independence remain in a collapsed disclosure");
        await page("consistency-05-about");

        foreach (var item in new[] { ("Purple","#7F298F"),("Pink","#B03D83"),("Teal","#80C4B8"),("Custom","#336699") })
        {
            _saved["Accent"] = item.Item1; _customAccent = item.Item2; ResetDraft(); Navigate("home"); Render(); await Idle();
            check(Hex(WorkspaceLabel.Foreground)==Hex(AccentForeground),item.Item1+" preserves the independent Workspace colour");
            var logos = VisualDescendants<TextBlock>(RootGrid).Where(t => t.Text == "Toolbox Matrix").ToArray();
            check(logos.Length == 1 && TitleAppIcon.Source is not null && Icon is not null, item.Item1 + " keeps the supplied icon and single top-toolbar brand");
            check(((SolidColorBrush)TitleStrip.BorderBrush).Color.R == Accent.Color.R && ((SolidColorBrush)HomeNav.Background).Color.G == Accent.Color.G, item.Item1 + " updates title border and current sidebar highlight");
            check(Hex(Accent) == item.Item2 && Hex(TileArtwork) == item.Item2 && Hex(BackBrush) == "#000005", item.Item1 + " preserves Follow Accent and the black baseline");
            check(SettingsRegistry.Contrast(Hex(Accent),Hex(AccentText)) >= 4.5 && SettingsRegistry.Contrast(Hex(AccentFocusBrush),Hex(BackBrush)) >= 3, item.Item1 + " keeps control text and focus-frame contrast");
            var tile = TilesPanel.Children.OfType<Button>().First(); tile.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0) { RoutedEvent = Mouse.MouseEnterEvent });
            check(tile.Content is Border frame && Hex(frame.BorderBrush) == Hex(AccentFocusBrush), item.Item1 + " tile hover uses the accent focus frame");
            tile.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0) { RoutedEvent = Mouse.MouseLeaveEvent });
            await page("consistency-06-" + item.Item1.ToLowerInvariant() + "-home-chrome");
            Navigate("settings"); _settingsTab = "Appearance"; ApplySearch(); await page("consistency-07-" + item.Item1.ToLowerInvariant() + "-settings");
            EdgeScrollBar.ApplyTemplate();
            check(VisualDescendants<Border>(EdgeScrollBar).Any(b => b.Background is SolidColorBrush brush && Hex(brush) == Hex(AccentFocusBrush)), item.Item1 + " recolours the rendered scrollbar thumb");
            Navigate("collection:ai"); _collectionListView = true; ApplySearch(); await page("consistency-08-" + item.Item1.ToLowerInvariant() + "-compact");
            _collectionListView = false;
        }
        foreach (var colour in new[] { "#000000", "#FFFFFF", "#FFFF00" })
        {
            _saved["Accent"] = "Custom"; _customAccent = colour; ResetDraft();
            check(SettingsRegistry.Contrast(Hex(Accent),Hex(AccentText)) >= 4.5 && SettingsRegistry.Contrast(Hex(AccentFocusBrush),Hex(BackBrush)) >= 3, colour + " extreme custom accent retains text and focus contrast");
        }
        _saved["Accent"] = "Teal"; _saved["Tile Artwork Colour"] = "Custom Colour"; _customTileColour = "#F2BA49"; ResetDraft(); Navigate("collections"); Render();
        check(Hex(TileArtwork) == "#F2BA49" && Hex(Accent) == "#80C4B8", "custom tile override leaves the global accent independent");
        await page("consistency-09-teal-chrome-gold-artwork");

        Navigate("settings"); _settingsTab = "Navigation"; ApplySearch(); await Idle();
        var homeLabel = VisualDescendants<TextBlock>(TilesPanel).First(t => t.Text == "Home on refresh");
        check(AutomationProperties.GetHelpText(homeLabel).Contains("On returns to Home") && homeLabel.Focusable && homeLabel.ToolTip is null,
            "Home on refresh retains accessible help and focus without a tooltip");
        homeLabel.Focus(); await Idle(); await shot("consistency-11-navigation-keyboard-no-popup.png");
        var explanations = VisualDescendants<Expander>(TilesPanel).First(e => Equals(e.Header,"About these settings")); explanations.IsExpanded = true;
        await page("consistency-12-navigation-descriptions-retained");
        Click(AdvancedButton); await Idle();
        var advancedSections=VisualDescendants<Expander>(TilesPanel).ToArray();
        check(advancedSections.Length == 3 && advancedSections.All(x=>!x.IsExpanded),"Advanced starts with three collapsed coherent sections");
        await page("improvement-advanced-collapsed");
        // Screenshot/layout passes may rebuild Settings. Always act on current controls.
        foreach(var section in VisualDescendants<Expander>(TilesPanel).ToArray()) section.IsExpanded=true;
        await Idle();
        check(_settingsTab == "Advanced" && VisualDescendants<Button>(TilesPanel).Any(b => Equals(b.Content,"Review alias registration…")), "accent gear opens Advanced with the existing Windows Integration actions");
        var categoryButtons = TilesPanel.Children.OfType<WrapPanel>().First().Children.OfType<Button>().ToArray();
        check(!categoryButtons.Any(b => Equals(b.Content,"Integration") || Equals(b.Content,"Advanced")), "main Settings categories do not duplicate Advanced or Integration");
        check(VisualDescendants<TextBlock>(TilesPanel).Any(t => t.Text == "Tray icon" && t.ToolTip is null && AutomationProperties.GetHelpText(t).Length > 0), "Advanced retains the tray preference and its explanation");
        await page("consistency-13-advanced-integration");

        var runner = new CaptureRunner(); Discovery.Runner = runner;
        _installedData = new([], "No installed packages found.", true);
        _windowsInventory = new([], true, true, "fixture", DateTime.UtcNow);
        _installSelection.Clear(); _installReports.Clear();
        foreach (var compact in new[]{false,true})
        {
            _collectionListView=compact; var layout=compact?"compact":"tiles";
            Navigate("home"); SetCaptureSearch("package"); Discovery.InvalidateAll();
            _searchData=await Discovery.SearchPageAsync("package",CancellationToken.None); ApplySearch(); await Idle();
            object[] Entries() => compact ? _packageList!.Items.Cast<object>().ToArray() : _packageList!.Items.Cast<object[]>().SelectMany(x=>x).ToArray();
            void VisibleLast(string state)
            {
                var entries=Entries();
                check(entries.Length==_searchData.Items.Count+1 && entries[^1] is MoreResults && entries.Take(entries.Length-1).All(x=>x is PackageItem),layout+" "+state+" ends with a command after genuine packages");
                var origin=LoadMoreButton.TranslatePoint(new Point(),PackageHost);
                check(LoadMoreButton.IsVisible && LoadMoreButton.ActualHeight>=50 && origin.Y>=-1 && origin.Y+LoadMoreButton.ActualHeight<=PackageHost.ActualHeight+1,layout+" "+state+" Load More is wholly visible inside the only results viewport");
                check(LoadMoreButton.ContextMenu is null && LoadMoreButton.Tag is not PackageItem && LoadMoreButton.Focusable,layout+" command has no Properties or package selection and is keyboard accessible");
            }
            check(_searchData.Items.Count==99 && _hasMoreResults,layout+" begins with exactly 99 genuine apps and known more");
            check(ContentScroll.ScrollableHeight==0 && VirtualizingPanel.GetIsVirtualizing(_packageList!),layout+" has no nested page scrolling and keeps virtualization");
            var realized=Enumerable.Range(0,_packageList!.Items.Count).Count(i=>_packageList.ItemContainerGenerator.ContainerFromIndex(i)!=null);
            check(realized>0 && realized<_packageList.Items.Count,layout+" realizes only viewport rows");
            await shot("improvement-"+layout+"-99-top.png");
            FindChild<ScrollViewer>(_packageList)!.ScrollToEnd(); await Idle(); VisibleLast("99+more");
            await shot("improvement-"+layout+"-99-position-100-load-more.png");
            var scroll=FindChild<ScrollViewer>(_packageList)!; var offset=scroll.VerticalOffset;
            LoadMoreButton.Focus(); runner.Delay=350; Click(LoadMoreButton);
            check(_loadingMore && LoadMoreButton.IsKeyboardFocused,layout+" loading retains keyboard focus");
            await shot("improvement-"+layout+"-loading.png");
            var until=DateTime.UtcNow.AddSeconds(15); while(_loadingMore && DateTime.UtcNow<until) await Task.Delay(30); await Idle();
            check(_searchData.Items.Count==198 && _hasMoreResults && _searchData.Items.Select(x=>x.Id).Distinct().Count()==198,layout+" appends another 99 unique packages and retains known continuation");
            check(Math.Abs(FindChild<ScrollViewer>(_packageList!)!.VerticalOffset-offset)<2,layout+" append preserves the prior scroll offset (before "+offset+", after "+FindChild<ScrollViewer>(_packageList!)!.VerticalOffset+")");
            check(Keyboard.FocusedElement is FrameworkElement focused && focused.Tag is PackageItem focusedPackage && focusedPackage.Id=="Test.Package100",layout+" focus continues to the first appended app");
            await shot("improvement-"+layout+"-198-preserved-position.png");
            FindChild<ScrollViewer>(_packageList!)!.ScrollToEnd(); await Idle(); VisibleLast("198+more");
            await shot("improvement-"+layout+"-198-position-199-load-more.png");
            runner.Delay=0; runner.FailSearch=true; Discovery.InvalidateAll(); var before=_searchData.Items.ToArray();
            await LoadMoreAsync(); await Idle();
            check(_loadMoreError.Length>0 && _searchData.Items.SequenceEqual(before) && _hasMoreResults,layout+" source failure preserves results and the retry command");
            FindChild<ScrollViewer>(_packageList!)!.ScrollToEnd(); await Idle(); await shot("improvement-"+layout+"-source-failure.png");
            runner.FailSearch=false; Discovery.InvalidateAll(); await LoadMoreAsync(); await LoadMoreAsync(); await Idle();
            check(_searchData.Items.Count==301 && !_hasMoreResults && !Entries().Any(x=>x is MoreResults),layout+" repeated batches stop at actual exhaustion");
            foreach(var total in new[]{90,99})
            {
                runner.Total=total; Discovery.InvalidateAll(); _searchData=await Discovery.SearchPageAsync("package",CancellationToken.None); ApplySearch(); await Idle();
                FindChild<ScrollViewer>(_packageList!)!.ScrollToEnd(); await Idle();
                check(_searchData.Items.Count==total && !_hasMoreResults && Entries().Length==total && !Entries().Any(x=>x is MoreResults),layout+" "+total+" genuine packages have no extra Load More");
                await shot("improvement-"+layout+"-"+total+"-no-more.png");
            }
            runner.Total=301; Discovery.InvalidateAll(); _searchData=await Discovery.SearchPageAsync("package",CancellationToken.None); ApplySearch(); await Idle();
            runner.Delay=350; var pending=LoadMoreAsync(); Navigate("help"); await pending; await Idle();
            check(_route=="help" && _searchData.Items.Count==99 && !_loadingMore,layout+" navigation cancels the request and rejects its late result");
            runner.Delay=0;
        }
        _collectionListView=false; Navigate("home"); SetCaptureSearch("package");
        var packages=Enumerable.Range(1,99).Select(i=>new PackageItem("Package "+i,"Test.Package"+i,"1","","winget","Fixture publisher")).ToArray();
        _searchData=new(packages,"99 simulated search results.",true,true); ApplySearch(); await Idle();
        var selectAll=VisualDescendants<Button>(TilesPanel).First(b=>Equals(b.Content,"Add All")); Click(selectAll);
        check(_route=="home" && _installSelection.Count==99,"Add All selects only the 99 genuine apps and stays on the page");
        Click(VisualDescendants<Button>(TilesPanel).First(b=>Equals(b.Content,"Add All")));
        check(_route=="home" && _installSelection.Count==99 && !_installRunning, "Repeated Add All stays on page, avoids duplicates and never starts an installer");
        Click(VisualDescendants<Button>(TilesPanel).First(b=>Equals(b.Content,"Review selection (99)")));
        check(_route=="install"&&!_installRunning&&!_installPreparing&&_installReports.Count==0, "Explicit Review selection opens Install Centre without executing");
        SetCaptureSearch(""); _installSelection.Clear(); _disclosures.Clear(); _saved=new(Defaults); _customAccent=_customTileColour="#7F298F"; ResetDraft();
        Navigate("settings"); _settingsTab="Appearance"; ApplySearch();
        foreach(var preset in SettingsRegistry.AccentPresets)
        {
            _draft["Accent"]=preset.Name; _preview=true; RefreshVisualPreview(); ApplySearch(); await Idle();
            check(Hex(WorkspaceLabel.Foreground)==Hex(AccentForeground) && SettingsRegistry.Contrast(preset.Hex,Hex(AccentText))>=4.5,preset.Name+" preview preserves Workspace with readable control text");
        }
        await page("improvement-accent-presets");
        _draft["Accent"]="Custom"; _draftAccent="#336699"; _preview=true; RefreshVisualPreview(); ApplySearch(); await Idle();
        var hexEditor=VisualDescendants<TextBox>(TilesPanel).First(x=>AutomationProperties.GetName(x)=="Custom accent");
        hexEditor.Text="#E0A54A"; await Idle();
        check(Hex(WorkspaceLabel.Foreground)==Hex(AccentForeground) && _draftAccent=="#E0A54A","valid HEX changes update the live preview without Apply");
        check(VisualDescendants<Button>(TilesPanel).Where(b=>ReferenceEquals(b.Background,Accent)).All(b=>SettingsRegistry.Contrast(Hex(b.Background),Hex(b.Foreground))>=4.5),"live colour editing updates the actual selected-control text contrast");
        var wheel=VisualDescendants<Image>(TilesPanel).First(x=>AutomationProperties.GetName(x)=="Custom accent colour wheel");
        wheel.BringIntoView(); wheel.Focus(); await Idle();
        var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(wheel)!,0,Key.Right){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        wheel.RaiseEvent(key); check(key.Handled && _draftAccent!="#E0A54A","colour wheel supports keyboard hue changes");
        await page("improvement-colour-wheel-and-hex");
        var validAccent=Hex(Accent); hexEditor.Text="#QQQQQQ"; await Idle();
        check(Hex(Accent)==validAccent,"invalid HEX leaves the last valid preview intact");
        await shot("improvement-invalid-hex.png");
        ResetDraft(); ApplySearch();
        check(Hex(WorkspaceLabel.Foreground)==Hex(AccentForeground)&&!HasDraft,"Cancel restores the saved Workspace accent colour");
        Navigate("home"); Render();
    }

    private async Task CaptureSettingsTooltipChecksAsync(Func<string, Task> shot, Func<FrameworkElement, string, Task> elementShot, Action<bool, string> check)
    {
        async Task Idle() { await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); UpdateLayout(); }
        bool NoTips() => !PresentationSource.CurrentSources.Cast<PresentationSource>().Any(source => source.RootVisual is not null && VisualDescendants<ToolTip>(source.RootVisual).Any(t => t.IsOpen));
        ResetDraft(); Navigate("settings"); SetCaptureSearch("");
        foreach (var section in SettingsRegistry.All.Select(d => d.Section).Append("Keyboard").Distinct())
        {
            _settingsTab = section;
            _draft["Accent"] = "Custom"; _draft["Tile Artwork Colour"] = "Custom Colour"; _draft["Subheadings Colour"] = "Custom";
            ApplySearch(); await Idle();
            if (section == "Advanced") { foreach (var expander in VisualDescendants<Expander>(TilesPanel)) expander.IsExpanded = true; await Idle(); }
            var controls = VisualDescendants<FrameworkElement>(TilesPanel).Where(e => e.IsVisible && (e is Button or TextBox or Slider or Image || e is TextBlock { Focusable: true })).ToArray();
            check(controls.Length > 0 && controls.All(e => e.ToolTip is null && !ToolTipService.GetIsEnabled(e)), section + ": every authored title/control has no tooltip and disables the tooltip service");
            foreach (var owner in controls)
            {
                owner.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                if (owner.Focusable) owner.Focus();
                await Idle();
                check(NoTips(), section + ": mouse/focus opens no tooltip for " + (AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : owner.GetType().Name));
                owner.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            }
            check(VisualDescendants<Slider>(TilesPanel).All(x => x.AutoToolTipPlacement == System.Windows.Controls.Primitives.AutoToolTipPlacement.None), section + ": native slider value tooltips are disabled");
            if (section is "Appearance" or "Navigation" or "Advanced") { ContentScroll.ScrollToTop(); await shot("settings-no-popup-" + section.ToLowerInvariant() + ".png"); }
        }
        _settingsTab = "Appearance"; ApplySearch(); await Idle();
        var label = VisualDescendants<TextBlock>(TilesPanel).First(t => t.Text == "Background");
        check(label.Focusable && AutomationProperties.GetHelpText(label).Length > 0, "Background label retains keyboard focus and accessible help");
        check(VisualDescendants<Expander>(TilesPanel).Any(e => Equals(e.Header, "About these settings")), "permanent About these settings instructions remain available");
        label.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0) { RoutedEvent = Mouse.MouseEnterEvent }); label.Focus();
        typeof(Window).GetMethod("OnDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(this, [EventArgs.Empty]);
        await Task.Delay(1200); await Idle(); check(NoTips(), "deactivation and delayed focus callbacks leave no Settings tooltip popup");
        await shot("settings-no-popup-after-deactivation.png");
        ResetDraft(); ApplySearch();
    }

    private async Task CaptureGlobalAccentChecksAsync(Func<string, Task> page, Func<string, Task> shot, Func<FrameworkElement, string, Task> elementShot, Action<bool, string> check)
    {
        async Task Idle() { await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); UpdateLayout(); await Task.Delay(70); }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        string Hex(Brush brush) => ColourHex(((SolidColorBrush)brush).Color);
        var routes = new[] { ("home", "Home"), ("collections", "Collections"), ("installed", "Installed Apps"), ("install", "Install Centre"), ("backup", "Backup / Restore"), ("diagnostics", "Diagnostics"), ("help", "Help"), ("about", "About"), ("settings", "Settings") };
        _saved = new(Defaults); _customAccent = "#7F298F"; _customTileColour = "#D46B67"; _installSelection.Clear(); ResetDraft(); SetCaptureSearch("");
        foreach (var theme in new[] { "Void Dark", "Light" })
        foreach (var size in new[] { "Standard", "Large" })
        foreach (var choice in new[] { (Name:"purple", Accent:"Purple", Hex:"#7F298F", Sub:"Blue"), (Name:"blue", Accent:"Blue", Hex:"#477ACB", Sub:"Amber"), (Name:"yellow", Accent:"Custom", Hex:"#FFFF00", Sub:"Purple"), (Name:"custom", Accent:"Custom", Hex:"#337FA5", Sub:"Amber") })
        {
            _saved["Theme"] = theme; _saved["Text size"] = size; _saved["Accent"] = choice.Accent; _customAccent=choice.Hex; _saved["Tile Artwork Colour"] = "Custom Colour"; _saved["Subheadings Colour"] = choice.Sub; var colour=choice.Name; ResetDraft();
            Point? headerPosition = null;
            foreach (var (route, title) in routes)
            {
                Navigate(route); SearchBox.Clear(); Render(); await Idle(); ContentScroll.ScrollToTop(); await Idle();
                var position = PageTitle.TranslatePoint(new Point(), RootGrid);
                headerPosition ??= position;
                check(PageTitle.Text == title && PageTitle.FontFamily.Source == "Segoe UI" && PageTitle.FontSize == 21 && PageTitle.FontWeight == FontWeights.SemiBold &&
                    (position-headerPosition.Value).Length < .5 && ReferenceEquals(PageTitle.Foreground, AccentForeground), theme + "/" + size + "/" + colour + ": " + title + " shares exact font, scale, position and colour");
                if (route == "collections") check(PageIntro.Visibility == Visibility.Collapsed && Eyebrow.Visibility == Visibility.Collapsed && SettingsSectionHeader.Visibility == Visibility.Collapsed, "Collections has no duplicate headline or Browse copy");
                if (route == "installed") check(!VisualDescendants<Button>(TilesPanel).Any(b => b.Content is string label && label.StartsWith("Review selection", StringComparison.OrdinalIgnoreCase)), "Installed Apps has no Review Selection action");
                await shot("focused-" + theme.Replace(" ", "").ToLowerInvariant() + "-" + size.ToLowerInvariant() + "-" + colour.ToLowerInvariant() + "-" + route + ".png");
            }
            check(Hex(Accent) == choice.Hex && Hex(TileArtwork) == "#D46B67", "Subheadings " + choice.Sub + " leaves Accent and artwork independent");
            check(new[] { BackBrush, SideBrush, SurfaceBrush }.All(bg => SettingsRegistry.Contrast(Hex(AccentForeground), Hex(bg)) >= 4.5), theme + " " + colour + " main headings keep readable accent contrast");
            check(new[] { Eyebrow, PageIntro, InstallSelectionBadge }.All(t => ReferenceEquals(t.Foreground, Subheadings)) && ReferenceEquals(WorkspaceLabel.Foreground, AccentForeground) && ReferenceEquals(AdvancedButton.Foreground, AccentForeground) &&
                VisualDescendants<TextBlock>(AdvancedButton).All(t => ReferenceEquals(t.Foreground, AccentForeground)), "secondary headings and live count use Subheadings; Workspace and Advanced text/cog use Accent");
            check(VisualDescendants<System.Windows.Shapes.Path>(RefreshGlyph).All(path => ReferenceEquals(path.Stroke, AccentForeground)), "refresh control stays on Accent independently of the timestamp");
            check(new[] { BackBrush, SideBrush, SurfaceBrush }.All(bg => SettingsRegistry.Contrast(Hex(Subheadings), Hex(bg)) >= 4.5), theme + " " + colour + " subheading text has at least 4.5:1 contrast");
        }
        foreach (var theme in new[] { "Void Dark", "Light" })
        foreach (var colour in new[] { "#000000", "#FFFFFF", "#FFFF00", "#000005" })
        {
            _saved["Theme"] = theme; _saved["Subheadings Colour"] = "Custom"; _customSubheadingsColour = colour; ResetDraft();
            check(new[] { BackBrush, SideBrush, SurfaceBrush }.All(bg => SettingsRegistry.Contrast(Hex(Subheadings), Hex(bg)) >= 4.5), theme + " extreme " + colour + " adjusts for accessible text contrast");
        }
        _saved = new(Defaults); _customSubheadingsColour = "#7F298F"; ResetDraft(); Navigate("settings"); _settingsTab="Appearance"; ApplySearch(); await Idle();
        var priorAccent=Hex(Accent); var priorTile=Hex(TileArtwork); var priorSub=Hex(Subheadings); var priorStatus=Hex(WarningBrush);
        _draft["Subheadings Colour"]="Custom"; _draftSubheadingsColour="#477ACB"; _preview=true; RefreshVisualPreview(); ApplySearch(); await Idle();
        var editor=VisualDescendants<TextBox>(TilesPanel).First(t=>AutomationProperties.GetName(t)=="Custom subheadings colour");
        editor.Text="#D6A64B"; await Idle();
        check(Hex(Subheadings)=="#D6A64B" && Hex(Accent)==priorAccent && Hex(TileArtwork)==priorTile && Hex(WarningBrush)==priorStatus, "Subheadings HEX preview changes only its text role; semantic status colours stay independent");
        var last=Hex(Subheadings); editor.Text="#QQQQQQ"; await Idle(); check(Hex(Subheadings)==last,"invalid subheadings HEX keeps the last valid preview");
        editor.Text="#477ACB";
        var wheel=VisualDescendants<Image>(TilesPanel).First(t=>AutomationProperties.GetName(t)=="Custom subheadings colour colour wheel");
        wheel.BringIntoView(); wheel.Focus(); await Idle();
        check(Keyboard.FocusedElement==wheel, "Subheadings colour wheel receives keyboard focus before arrow input");
        var key=new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!,0,Key.Right) { RoutedEvent=Keyboard.PreviewKeyDownEvent }; wheel.RaiseEvent(key);
        check(key.Handled && _draftSubheadingsColour!="#477ACB", "Subheadings uses the existing functional visual/keyboard colour picker");
        await page("focused-subheadings-custom-picker");
        Click(VisualDescendants<Button>(TilesPanel).First(b=>Equals(b.Content,"Cancel draft")));
        check(!HasDraft && Hex(Subheadings)==priorSub && Hex(Accent)==priorAccent, "Cancel restores the saved subheadings colour without changing Accent");
        check(AppVersionLabel.Text=="v"+typeof(MainWindow).Assembly.GetName().Version!.ToString(3), "sidebar version comes from the actual running assembly");

        _installedData=new([],"SIMULATED empty reliable inventory",true); _upgradeData=new([],"SIMULATED no updates",true); _windowsInventory=new([],true,true,"SIMULATED",DateTime.UtcNow); _installSelection.Clear();
        var steam=new PackageItem("Steam","Valve.Steam","1.0","","winget","Valve Corporation");
        var zip=new PackageItem("7-Zip","7zip.7zip","1.0","","winget","Igor Pavlov");
        _collectionData["gaming"]=(new([steam],"SIMULATED verified identity",true),DateTime.UtcNow);
        _collectionData["everyday"]=(new([zip],"SIMULATED verified identity",true),DateTime.UtcNow);
        _saved["Text size"]="Large"; ResetDraft(); Navigate("collections"); SetCaptureSearch(""); ApplySearch(); await Idle(); ContentScroll.ScrollToVerticalOffset(70); await Idle();
        foreach(var pair in new[]{("Gaming",steam),("Everyday Essentials",zip)})
        {
            var tile=TilesPanel.Children.OfType<Button>().First(b=>VisualDescendants<TextBlock>(b).Any(t=>t.Text==pair.Item1));
            var menu=tile.ContextMenu!; menu.PlacementTarget=tile; menu.IsOpen=true; await Idle();
            var entry=menu.Items.OfType<MenuItem>().Single(); var offset=ContentScroll.VerticalOffset; var history=_history.Count;
            check(entry.IsEnabled && AutomationProperties.GetName(entry)==pair.Item2.Name,"category popup exposes eligible app name: "+pair.Item2.Name);
            await elementShot(menu,"focused-category-"+pair.Item2.Id+"-popup.png");
            entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Idle();
            check(Selected(pair.Item2) && !menu.IsOpen && _route=="collections" && _history.Count==history && Math.Abs(ContentScroll.VerticalOffset-offset)<.5,"click "+pair.Item2.Name+" adds once, closes popup and preserves exact page/scroll/history");
            check(InstallSelectionBadge.Text==_installSelection.Count.ToString(),"live badge updates immediately after "+pair.Item2.Name);
            menu.IsOpen=true; await Idle(); check(!menu.Items.OfType<MenuItem>().Single().IsEnabled,"already selected "+pair.Item2.Name+" cannot be added twice"); menu.IsOpen=false;
        }
        await shot("focused-collections-two-selected.png");
        var gaming=TilesPanel.Children.OfType<Button>().First(b=>VisualDescendants<TextBlock>(b).Any(t=>t.Text=="Gaming"));
        var context=gaming.ContextMenu!; _installSelection.Clear(); _installedData=new([steam],"SIMULATED installed Steam",true); UpdateSelectionBadge(); context.PlacementTarget=gaming; context.IsOpen=true; await Idle();
        check(!context.Items.OfType<MenuItem>().Single().IsEnabled && InstallSelectionBadge.Text=="0","installed app is disabled and never counted as eligible"); context.IsOpen=false;
        _installedData=new([],"SIMULATED unavailable",false); context.IsOpen=true; await Idle(); check(!context.Items.OfType<MenuItem>().Single().IsEnabled,"unknown inventory blocks category quick-add"); context.IsOpen=false;
        _installedData=new([],"SIMULATED reliable",true); context.IsOpen=true; await Idle(); var stale=context.Items.OfType<MenuItem>().Single(); _windowsInventory=WindowsInventorySnapshot.Empty;
        stale.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); check(!Selected(steam)&&!context.IsOpen,"click rechecks eligibility if inventory changes after opening");
        _windowsInventory=new([],true,true,"SIMULATED",DateTime.UtcNow);
        foreach(var compact in new[]{false,true})
        {
            _collectionListView=compact; Navigate("collection:gaming"); Render(); await Idle();
            var menu=CreatePropertiesContext(steam); menu.PlacementTarget=this; menu.IsOpen=true; await Idle();
            check(menu.Background==Brushes.White && menu.BorderBrush==Brushes.Black && menu.BorderThickness==new Thickness(1) && !menu.HasDropShadow && VisualDescendants<Border>(menu).All(b=>b.CornerRadius==new CornerRadius(0)),"individual app Properties remains white, square, thin black in "+(compact?"Compact List":"Tiles"));
            await elementShot(menu,"focused-white-properties-"+(compact?"compact":"tiles")+".png"); menu.IsOpen=false;
        }
        _collectionListView=false; _saved=new(Defaults); ResetDraft(); Navigate("home"); Render();
    }
}
