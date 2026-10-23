using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinGetToolbox;

public partial class MainWindow
{
    // Explicit fixture-only entry point for independent keyboard/mouse tests of the published WPF build.
    private void InitialiseInteractiveFixtures()
    {
        Directory.CreateDirectory(ToolboxPaths.Root);
        Title += " · SIMULATED REVIEW";
        Discovery.Runner = new CaptureRunner { Delay = 1800, RecordInteractive = true };
        Discovery.InvalidateAll();
        WindowsInventory.Provider = async token =>
        {
            File.AppendAllText(Path.Combine(ToolboxPaths.Root, "fixture-events.txt"), "windows-inventory\n");
            await Task.Delay(2600, token);
            return new([], true, true, "SIMULATED Windows inventory", DateTime.UtcNow);
        };
        _windowsInventory = new([], true, true, "SIMULATED Windows inventory", DateTime.UtcNow);
        _installedData = new([], "SIMULATED empty inventory", true);
        _upgradeData = new([], "SIMULATED no updates", true);
        foreach (var collection in Collections)
            _collectionData[collection.Key] = (new(CuratedIds[collection.Key].Select(id => new PackageItem(id.Split('.')[^1], id, "1.0", "", "winget", "Simulated publisher")).ToArray(), "SIMULATED collection", true), DateTime.UtcNow);
        _discoveryStatus = "SIMULATED DATA · no installer runs in this review.";
        Navigate("home"); Render();
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        if (root is not Visual) yield break;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisualDescendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private async Task CaptureRefinementChecksAsync(Func<string, Task> page, Func<string, Task> shot, Action<bool, string> check)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Button Named(string name) { UpdateLayout(); return VisualDescendants<Button>(TilesPanel).First(x => AutomationProperties.GetName(x) == name); }
        Button Label(string label) { UpdateLayout(); return VisualDescendants<Button>(TilesPanel).First(x => Equals(x.Content, label)); }
        TextBox Editor(string name) { UpdateLayout(); return VisualDescendants<TextBox>(TilesPanel).First(x => AutomationProperties.GetName(x) == name); }
        KeyEventArgs KeyOn(UIElement target, Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(args); return args;
        }
        async Task FinishRefresh()
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (_refreshRunning && DateTime.UtcNow < until) await Task.Delay(30);
            check(!_refreshRunning && RefreshButton.IsEnabled, "refresh finishes and re-enables its control");
        }
        void Settings()
        {
            Navigate("settings"); _settingsTab = "Appearance"; SetCaptureSearch(""); ApplySearch();
        }
        string Hex(SolidColorBrush brush) => "#" + brush.Color.R.ToString("X2") + brush.Color.G.ToString("X2") + brush.Color.B.ToString("X2");

        _saved = new(Defaults); _customAccent = _customTileColour = "#7F298F"; SetDefaultShortcuts(); ResetDraft();
        // The preceding cleanup test deliberately clears inventory; establish this suite's own fixture.
        _installedData = new([], "SIMULATED reliable inventory", true);
        _windowsInventory = new([], true, true, "SIMULATED Windows inventory", DateTime.UtcNow);
        _collectionListView = false; SetCaptureSearch(""); Navigate("home"); Render();
        await page("ref-01-home-purple");
        check(Hex(TileArtwork) == "#7F298F" && Hex(BackBrush) == "#000005", "default artwork and Void Dark preserve the exact original purple and background");
        check(RefreshButton.ActualWidth >= 34 && RefreshButton.ActualHeight >= 30 && RefreshGlyph.ActualWidth == 20, "refresh has a visible 20-DIP glyph and at least a 34-by-30-DIP target");
        check(AutomationProperties.GetName(RefreshButton) == "Refresh all data" && RefreshButton.ToolTip is null && AutomationProperties.GetHelpText(RefreshButton).Length>0, "refresh has a meaningful accessible name and static help without a tooltip");
        check(KeyOn(SearchBox, Key.F11).Handled && ResizeMode == ResizeMode.NoResize && WindowState == WindowState.Normal, "F11 and custom nonmaximizable chrome remain unchanged");
        var sidebar = (StackPanel)HelpNav.Parent;
        check(sidebar.Children.IndexOf(HelpNav) + 1 == sidebar.Children.IndexOf(AboutNav) && sidebar.Children.IndexOf(AboutNav) + 1 == sidebar.Children.IndexOf(SettingsNav), "Help is immediately above About and Settings");

        Navigate("collections"); SetCaptureSearch("dev"); Click(HelpNav);
        check(_route == "help" && PageTitle.Text == "Help" && NavigationRules.SelectedButton(_route) == "HelpNav", "sidebar Help opens its own correctly highlighted destination");
        await page("ref-02-help-sidebar");
        UpdateLayout(); VisualDescendants<Expander>(TilesPanel).First(x => Equals(x.Header, "Keyboard shortcuts")).IsExpanded = true;
        await page("ref-03-help-keyboard-expanded");
        ContentScroll.ScrollToVerticalOffset(150); await Task.Delay(100); var helpScroll = ContentScroll.VerticalOffset;
        Click(BackButton); check(_route == "collections" && SearchBox.Text == "dev", "Back from Help restores the previous collection search");
        Click(ForwardButton); await Task.Delay(100);
        check(_route == "help" && Math.Abs(ContentScroll.VerticalOffset - helpScroll) < 2 && _disclosures["help-keyboard"], "Forward restores Help scroll and expanded guidance");
        await shot("ref-04-help-forward-restored.png");
        Navigate("about"); await page("ref-05-about-concise");
        UpdateLayout(); foreach (var item in VisualDescendants<Expander>(TilesPanel)) item.IsExpanded = true;
        await page("ref-06-about-notices-expanded");

        foreach (var item in new[] { ("Purple", "#7F298F"), ("Pink", "#B03D83"), ("Teal", "#80C4B8"), ("Custom", "#336699") })
        {
            Settings(); Click(Named("Accent Colour: " + item.Item1));
            if (item.Item1 == "Custom") Editor("Custom accent").Text = item.Item2;
            Click(Label("Apply"));
            check(!HasDraft && Hex(Accent) == item.Item2 && Hex(TileArtwork) == item.Item2, "Apply persists " + item.Item1 + " accent and matching tile artwork");
            check(SettingsRegistry.Contrast(Hex(Accent), Hex(AccentText)) >= 4.5, item.Item1 + " accent controls keep readable text contrast");
            Navigate("collections"); SetCaptureSearch(""); await page("ref-07-accent-" + item.Item1.ToLowerInvariant());
            UpdateLayout();
            check(VisualDescendants<TextBlock>(TilesPanel).Where(x => x.FontFamily.Source == "Arial Black").All(x => x.Foreground is SolidColorBrush b && Hex(b) == item.Item2), item.Item1 + " is rendered on the oversized collection artwork");
            Navigate("collection:ai"); await page("ref-08-app-artwork-" + item.Item1.ToLowerInvariant());
        }

        Settings(); var originalFile = File.ReadAllText(SettingsPath);
        Click(Named("Tile Artwork Colour: Custom Colour")); Editor("Custom tile artwork colour").Text = "#F2BA49";
        Click(Label("Preview draft"));
        check(_preview && Hex(TileArtwork) == "#F2BA49" && Hex(Accent) == "#336699", "tile override previews independently of the accent");
        await page("ref-09-custom-tile-preview-settings");
        Click(Label("Cancel draft"));
        check(!HasDraft && !_preview && Hex(TileArtwork) == "#336699" && File.ReadAllText(SettingsPath) == originalFile, "Cancel restores saved artwork and leaves the settings file unchanged");
        Click(Named("Accent Colour: Teal")); Click(Named("Tile Artwork Colour: Custom Colour")); Editor("Custom tile artwork colour").Text = "#F2BA49"; _draft["Subheadings Colour"]="Custom"; _draftSubheadingsColour="#477ACB"; Click(Label("Apply"));
        check(_saved["Tile Artwork Colour"] == "Custom Colour" && _customTileColour == "#F2BA49" && Hex(Accent) == "#80C4B8", "custom tile colour saves independently alongside a teal accent");
        await page("ref-10-custom-tile-saved-settings");
        Navigate("collections"); SetCaptureSearch(""); await page("ref-11-teal-accent-gold-tile-override");
        _saved = new(Defaults); _customTileColour = "#7F298F"; LoadPreferences();
        check(_saved["Tile Artwork Colour"] == "Custom Colour" && _customTileColour == "#F2BA49" && Hex(TileArtwork) == "#F2BA49", "saved artwork mode and colour reload from disk");
        _collectionListView = true; Navigate("collection:ai"); ApplySearch(); await page("ref-12-override-compact-list");
        _collectionListView = false;

        _saved["Backup destination"] = "Default"; ResetDraft(); Navigate("backup"); await ExportBackupAsync();
        check(_lastBackupPath is not null && File.Exists(_lastBackupPath), "backup export creates a file after the cleanup regression: " + _backupStatus);
        var backup = _lastBackupPath!; var imported = BackupService.Import(File.ReadAllText(backup));
        check(imported.Preferences.CustomSubheadingsColour == "#477ACB" && imported.Preferences.Values["Subheadings Colour"] == "Custom", "actual backup export preserves independent subheadings mode and HEX");
        check(imported.Preferences.CustomTileColour == "#F2BA49" && imported.Preferences.Values["Tile Artwork Colour"] == "Custom Colour", "actual backup export includes the independent tile colour and mode");
        Settings(); Click(Named("Tile Artwork Colour: Follow Accent")); Click(Named("Accent Colour: Pink")); Click(Label("Apply"));
        Navigate("backup"); await ImportBackupFileAsync(backup);
        check(!RestorePreferences(imported.Preferences, (_, _) => false) && _saved["Accent"] == "Pink" && Hex(TileArtwork) == "#B03D83", "cancelled restore preserves saved appearance");
        check(RestorePreferences(imported.Preferences, (_, _) => true) && !HasDraft, "confirmed restore saves immediately without Apply");
        Navigate("settings"); _settingsTab = "Appearance"; ApplySearch();
        check(_customSubheadingsColour == "#477ACB" && _saved["Subheadings Colour"] == "Custom", "restore commits the backup subheadings colour independently");
        check(Hex(TileArtwork) == "#F2BA49" && _saved["Accent"] == "Teal", "restore commits the backup artwork and independent accent");
        await page("ref-13-backup-restored-appearance");

        // Both real event entry points use the same controlled provider and produce the same requests.
        var runner = new CaptureRunner { Delay = 450 }; Discovery.Runner = runner; var windowsCalls = 0;
        WindowsInventory.Provider = async token => { windowsCalls++; await Task.Delay(700, token); return new([], true, true, "SIMULATED Windows inventory", DateTime.UtcNow); };
        ResetDraft(); Navigate("home"); SetCaptureSearch("studio"); _searchData = new([], "SIMULATED search", true);
        var generation = _inventoryGeneration; Click(RefreshButton);
        check(_refreshRunning && _inventoryGeneration == generation + 1 && RefreshStatusBanner.Text.Contains("0/4") && RefreshGlyph.RenderTransform.HasAnimatedProperties, "button starts the global refresh immediately with named stages and a brief glyph animation");
        check(KeyOn(SearchBox, Key.F5).Handled && _inventoryGeneration == generation + 1, "F5 during an active refresh is handled without starting duplicate work");
        await shot("ref-14-refresh-icon-spinning.png"); Click(HelpNav); await shot("ref-15-refresh-navigation-to-help.png");
        await FinishRefresh();
        var buttonCalls = runner.Calls.OrderBy(x => x).ToArray();
        check(_route == "help" && windowsCalls == 1, "refresh completes once while Help navigation stays responsive");
        Navigate("home"); SetCaptureSearch("studio"); runner.Calls.Clear(); SearchBox.Focus(); KeyOn(SearchBox, Key.F5);
        check(_refreshRunning, "F5 starts refresh while the search editor has focus");
        await FinishRefresh();
        check(buttonCalls.SequenceEqual(runner.Calls.OrderBy(x => x)) && windowsCalls == 2, "button and F5 issue identical inventory, updates and search requests plus one Windows inventory call each");
        await page("ref-16-refresh-f5-completed");

        SetCaptureSearch(""); Settings(); Editor("Custom tile artwork colour").Text = "#AABBCC";
        var focused = Editor("Custom tile artwork colour"); focused.Focus(); focused.CaretIndex = 4;
        KeyOn(focused, Key.F5); await FinishRefresh();
        check(ReferenceEquals(focused, Editor("Custom tile artwork colour")) && _draftTileColour == "#AABBCC" && focused.CaretIndex == 4 && Keyboard.FocusedElement == focused, "refresh preserves the settings draft, editor instance, focus and caret");
        ResetDraft(); Navigate("home"); SetCaptureSearch("");
        runner.FailUpdates = true; Click(RefreshButton); await FinishRefresh();
        check(RefreshStatusBanner.Text.Contains("incomplete") && RefreshStatusBanner.Text.Contains("Updates"), "partial refresh names unavailable updates instead of claiming completion");
        await page("ref-17-refresh-partial-result"); runner.FailUpdates = false; runner.Delay = 0;

        _installSelection.Clear();
        var jan = new PackageItem("Jan", "Jan.Jan", "1", "", "winget", "Jan");
        var lm = new PackageItem("LM Studio", "ElementLabs.LMStudio", "1", "", "winget", "Element Labs");
        _installSelection.Add(jan); Navigate("install");
        var queue = StartInstallQueueAsync();
        check(_installRunning, "a real asynchronous simulated queue starts for the refresh regression");
        _installSelection.Add(lm); Click(RefreshButton);
        check(_activeInstallItems.Count == 1 && _activeInstallItems[0].Id == jan.Id, "refresh leaves the active reviewed queue intact");
        await queue; await FinishRefresh();
        check(_installReports.Count(x => x.Name == jan.Name && x.Result.Success) == 1 && _installSelection.Count == 1 && _installSelection[0].Id == lm.Id, "simulated job completes during refresh and the separate selection survives");
        await page("ref-18-refresh-simulated-job-completed");

        Navigate("diagnostics"); await RunDiagnosticsAsync(false); await page("ref-19-diagnostics-quick-concise");
        await RunDiagnosticsAsync(true); await page("ref-20-diagnostics-full-concise");
        _diagnosticResults = [new("WARNING", "Source", "SIMULATED source unavailable; retry after checking connectivity."), new("ERROR", "Reports", "SIMULATED folder unavailable; choose a writable report folder.")]; ApplySearch(); UpdateLayout();
        check(VisualDescendants<Expander>(TilesPanel).Where(x => x.Header is string h && (h.StartsWith("WARNING") || h.StartsWith("ERROR"))).All(x => x.IsExpanded), "diagnostic warnings and errors remain expanded by default");
        await page("ref-21-diagnostic-warnings-visible");
        Settings(); _draft["Text size"] = "Extra Large"; _draft["Theme"] = "Light"; _preview = true; RefreshVisualPreview(); ApplySearch(); await page("ref-22-light-extra-large-settings");
        check(SettingsRegistry.Contrast(Hex(TextBrush), Hex(BackBrush)) >= 4.5, "light-theme labels retain readable contrast with custom tile artwork");
        ResetDraft(); _saved["Backup destination"] = "Default"; ResetDraft(); ApplyDraft();
        Navigate("collections"); SetCaptureSearch(""); await page("ref-23-final-override-for-restart");
        check(!HasDraft && _saved["Accent"] == "Teal" && _saved["Tile Artwork Colour"] == "Custom Colour" && _customTileColour == "#F2BA49", "saved final state is ready for an independent process-restart test");
    }
}
