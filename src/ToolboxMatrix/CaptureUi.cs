using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Media.Imaging;

namespace ToolboxMatrix;
public partial class MainWindow
{
    // Deliberate read-only review with real providers and an isolated data root. No fixture
    // injection, install action or OS registration occurs through this entry point.
    private async Task CaptureLiveAppearanceAsync()
    {
        var args = Environment.GetCommandLineArgs(); var i = Array.IndexOf(args, "--verify-live-appearance");
        if (i < 0 || i + 1 >= args.Length || !args.Contains("--data-root")) { Application.Current.Shutdown(2); return; }
        var folder = Path.GetFullPath(args[i + 1]); Directory.CreateDirectory(folder);
        var checks = new List<string>();
        try
        {
            void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks.Add("PASS: " + message); File.WriteAllLines(Path.Combine(folder, "LIVE-CHECKS.txt"), checks); }
            Check(!_captureMode && !Discovery.UsesTestProvider && WindowsInventory.Provider is null, "normal WinGet and Windows inventory providers are active");
            _sourceCheckStatus = await Discovery.DetectAsync(CancellationToken.None);
            // An activation refresh can already be running. The product deliberately coalesces
            // another Refresh call; a verification harness must wait for that shared work.
            async Task WaitForRefresh()
            {
                var deadline = DateTime.UtcNow.AddSeconds(120);
                while (_refreshRunning && DateTime.UtcNow < deadline) await Task.Delay(100);
                Check(!_refreshRunning, "live inventory refresh finished before captures");
            }
            if (_refreshRunning) await WaitForRefresh();
            await RefreshAllAsync(false);
            await WaitForRefresh();
            File.WriteAllText(Path.Combine(folder, "LIVE-STATUS.txt"), _sourceCheckStatus + "\n" + _discoveryStatus + "\nInventory: " + _installedData?.Status + "\nUpdates: " + _upgradeData?.Status + "\n" + _windowsInventory.Status);
            Check(_installedData?.IsReliable == true, "real WinGet inventory returned " + (_installedData?.Items.Count ?? 0) + " genuine installed rows");
            if (_saved["Accent"] == "Custom")
                Check(ColourHex(Accent.Color).Equals(_customAccent, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(WorkspaceLabel.Foreground, AccentForeground), "separate live process restores the saved custom HEX palette and Workspace resource");
            foreach (var route in new[] { "home", "collections", "installed", "install", "settings", "backup", "diagnostics", "help", "about" })
            {
                Navigate(route); Render(); await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                await Task.Delay(250); UpdateLayout();
                Check(CaptureBanner.Visibility == Visibility.Collapsed && CaptureBanner.Text.Length == 0, route + ": no simulation banner in real-provider mode");
                Check(!VisualDescendants<TextBlock>(RootGrid).Any(t => t.IsVisible && (t.Text.Contains("SIMULATED DATA", StringComparison.OrdinalIgnoreCase) || t.Text.Contains("fixture", StringComparison.OrdinalIgnoreCase))), route + ": no development fixture copy in the visible live UI");
                var dpi = VisualTreeHelper.GetDpi(this);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth*dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight*dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); bitmap.Render(this);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(folder, "live-" + route + ".png")); encoder.Save(stream);
            }
            // Exercise the same category menu against real catalogue and current Windows inventory.
            await LoadCollectionAsync("gaming", CancellationToken.None);
            await LoadCollectionAsync("everyday", CancellationToken.None);
            Navigate("collections"); SearchBox.Clear(); Render();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); UpdateLayout();
            foreach (var name in new[] { "Gaming", "Everyday Essentials" })
            {
                var tile = TilesPanel.Children.OfType<Button>().First(b => VisualDescendants<TextBlock>(b).Any(t => t.Text == name));
                var menu = tile.ContextMenu!; menu.PlacementTarget = tile; menu.IsOpen = true;
                await Task.Delay(200); UpdateLayout();
                var entries = menu.Items.OfType<MenuItem>().ToArray();
                Check(entries.Length > 0 && entries.Any(entry => AutomationProperties.GetName(entry).Length > 0), "real " + name + " category contains verified app names");
                var eligible = entries.FirstOrDefault(entry => entry.IsEnabled);
                if (eligible is not null)
                {
                    var count = _installSelection.Count; var scroll = ContentScroll.VerticalOffset; var appName = AutomationProperties.GetName(eligible);
                    eligible.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Task.Delay(100);
                    Check(_installSelection.Count == count + 1 && _route == "collections" && !menu.IsOpen && Math.Abs(ContentScroll.VerticalOffset-scroll) < .5 && InstallSelectionBadge.Text == _installSelection.Count.ToString(),
                        "real " + name + ": " + appName + " added to isolated selection; popup closes; page/scroll and live badge verified");
                }
                else { Check(entries.All(entry => !entry.IsEnabled), "real " + name + " apps are installed/unknown and remain safely disabled"); menu.IsOpen=false; }
            }
            _installSelection.Clear(); PersistSelection();
            Check(CaptureBanner.Visibility == Visibility.Collapsed, "real-provider review completes without any fixture marker or installer");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "CAPTURE-ERROR.txt"), ex.ToString()); Environment.ExitCode = 2; }
        finally { _tray?.Dispose(); Application.Current.Shutdown(Environment.ExitCode); }
    }

    // Captures actual rendered controls in this running WPF process. All package/installer providers are fixtures.
    // The isolated capture profile prevents changes to the user's preferences, reports or installed applications.
    private async Task CaptureScreenshotsAsync()
    {
        var args = Environment.GetCommandLineArgs(); var index = Array.IndexOf(args, "--capture-screenshots");
        if (index < 0 || index + 1 >= args.Length) { Application.Current.Shutdown(2); return; }
        var folder = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(folder);
        var checks = new List<string>(); var files = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks.Add("PASS: " + message); File.WriteAllLines(Path.Combine(folder, "UI-CHECKS.txt"), checks); }
        if (args.Contains("--verify-refinement-restart"))
        {
            try
            {
                Check(_saved["Accent"] == "Teal" && _saved["Tile Artwork Colour"] == "Custom Colour" && _customTileColour == "#F2BA49", "new WPF process loads the restored independent tile colour and accent");
                Check(((SolidColorBrush)WorkspaceLabel.Foreground).Color == AccentForeground.Color, "restarted process applies the saved Accent Colour to Workspace");
                Check(_saved["Subheadings Colour"] == "Custom" && _customSubheadingsColour == "#477ACB" && SettingsRegistry.Contrast(ColourHex(Subheadings.Color), ColourHex(SurfaceBrush.Color)) >= 4.5, "restarted process loads independently restored blue subheadings");
                Check(TileArtwork.Color == (Color)ColorConverter.ConvertFromString("#F2BA49"), "restarted process applies the saved artwork brush");
                Navigate("collections"); Render(); await Page("restart-restored-artwork");
                File.WriteAllLines(Path.Combine(folder, "UI-CHECKS.txt"), checks);
                File.WriteAllLines(Path.Combine(folder, "SCREENSHOT-INDEX.txt"), files);
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "CAPTURE-ERROR.txt"), ex.ToString()); Environment.ExitCode = 2; }
            finally { Application.Current.Shutdown(Environment.ExitCode); }
            return;
        }
        var firefox = new PackageItem("Mozilla Firefox", "Mozilla.Firefox", "144", "", "winget", "Mozilla");
        var code = new PackageItem("Visual Studio Code", "Microsoft.VisualStudioCode", "1.100", "1.101", "winget", "Microsoft Corporation", true);
        var jan = new PackageItem("Jan", "Jan.Jan", "0.6", "", "winget", "Jan");
        var lm = new PackageItem("LM Studio", "ElementLabs.LMStudio", "0.3", "", "winget", "Element Labs");
        var epic = new PackageItem("Epic Games Launcher", "EpicGames.EpicGamesLauncher", "18", "", "winget", "Epic Games");
        var provider = new CaptureRunner(); Discovery.Runner = provider; Discovery.InvalidateAll();
        WindowsInventory.Provider = _ => Task.FromResult(new WindowsInventorySnapshot([new("Audacity 3.7", "3.7", "Audacity Team")],true,true,"SIMULATED Windows inventory: registry and packaged-app checks succeeded.",DateTime.UtcNow));
        _windowsInventory = await WindowsInventory.ReadAsync(CancellationToken.None);
        _installedData = new([code,firefox with { Version="143", IsInstalled=true }], "2 results from WinGet (SIMULATED DATA).",true);
        _upgradeData = new([code], "1 simulated upgrade.",true);
        _discoveryStatus = "SIMULATED DATA · no installer ran.";
        _saved = new(Defaults); ResetDraft();
        foreach (var collection in Collections)
        {
            var items = CuratedIds[collection.Key].Select(id => id switch { "Jan.Jan"=>jan,"ElementLabs.LMStudio"=>lm,"Mozilla.Firefox"=>firefox,"Microsoft.VisualStudioCode"=>code,"EpicGames.EpicGamesLauncher"=>epic,"Audacity.Audacity"=>new("Audacity","Audacity.Audacity","3.8","","winget","Audacity Team"), _=>new PackageItem(id.Split('.')[^1],id,"1.0","","winget","Simulated publisher") }).ToArray();
            _collectionData[collection.Key] = (new(items,"SIMULATED verified collection.",true),DateTime.UtcNow);
        }
        try
        {
            if (args.Contains("--verify-small-patch-only"))
            {
                await CaptureSmallPatchChecksAsync(Shot, ShotElement, Check);
                File.WriteAllLines(Path.Combine(folder, "SCREENSHOT-INDEX.txt"), files);
                return;
            }
            if (args.Contains("--verify-consistency-only"))
            {
                await CaptureConsistencyChecksAsync(Page, Shot, ShotElement, Check);
                File.WriteAllLines(Path.Combine(folder, "SCREENSHOT-INDEX.txt"), files); return;
            }
            if (args.Contains("--verify-cohesive-only"))
            {
                await CaptureCohesiveChecksAsync(Page, Shot, ShotElement, Check);
                File.WriteAllLines(Path.Combine(folder, "SCREENSHOT-INDEX.txt"), files); return;
            }
            if (args.Contains("--verify-global-only"))
            {
                await CaptureGlobalAccentChecksAsync(Page, Shot, ShotElement, Check);
                File.WriteAllLines(Path.Combine(folder, "SCREENSHOT-INDEX.txt"), files);
                return;
            }
            if (args.Contains("--verify-tooltips-only"))
            {
                await CaptureSettingsTooltipChecksAsync(Shot, ShotElement, Check);
                File.WriteAllLines(Path.Combine(folder, "UI-CHECKS.txt"), checks);
                File.WriteAllLines(Path.Combine(folder, "SCREENSHOT-INDEX.txt"), files);
                return;
            }
            Navigate("home"); Render(); await Page("01-home");
            Navigate("collections"); await Page("02-collections");
            foreach (var collection in Collections) { Navigate("collection:"+collection.Key); await Page("03-collection-"+collection.Key+"-tiles"); }
            Navigate("collection:ai"); _collectionListView=true; ApplySearch(); await Page("04-compact-list");
            Check(_packageList?.Items.Count == 4 && VirtualizingPanel.GetIsVirtualizing(_packageList), "compact list uses a virtualizing items control with four actual rows");
            Check(FindChild<PackageRowControl>(_packageList!)?.ActualHeight > 20, "compact row template renders visible content");
            var toolbar = TilesPanel.Children.OfType<WrapPanel>().First(); var selectAll = toolbar.Children.OfType<Button>().First(x=>Equals(x.Content,"Add All")); selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(_route == "collection:ai" && _installSelection.Count == 4, "Add All adds eligible apps and stays on its page");
            await Page("05-selected-compact-rows");
            var menu = CreatePropertiesContext(jan); menu.PlacementTarget = _packageList; menu.Placement=PlacementMode.Center; menu.IsOpen=true;
            await Task.Delay(150); await ShotElement(menu,"06-white-properties-compact.png"); Check(menu.Background == Brushes.White && menu.BorderThickness.Left == 1, "context Properties is white with a one-pixel black frame"); menu.IsOpen=false;
            _packageList!.SelectedIndex=0; _packageList.Focus();
            var contextKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(_packageList)!, 0, Key.Apps) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            _packageList.RaiseEvent(contextKey); await Task.Delay(100);
            Check(contextKey.Handled && _packageList.ContextMenu?.IsOpen == true, "keyboard context-menu key opens immediate package Properties");
            await ShotElement(_packageList.ContextMenu!, "06a-white-properties-keyboard.png"); _packageList.ContextMenu!.IsOpen=false;
            _collectionListView=false; ApplySearch(); await Page("07-selected-tiles");
            menu = CreatePropertiesContext(jan); menu.PlacementTarget=TilesPanel; menu.Placement=PlacementMode.Center; menu.IsOpen=true; await Task.Delay(150); await ShotElement(menu,"08-white-properties-tiles.png"); menu.IsOpen=false;
            _installSelection.Clear(); AddToInstallSelection(jan); AddToInstallSelection(lm); _installSelection.Remove(jan); Check(_installSelection.Count==1 && _installSelection[0].Id==lm.Id,"selection removal is isolated by package identity");
            Navigate("properties"); _selectedPackage=firefox; Render(); await Page("09-properties-installed");
            _selectedPackage=jan; Render(); await Page("10-properties-not-installed");
            _selectedPackage=new("Unknown example","Example.Unknown","1","","","Unknown"); Render(); await Page("11-properties-unknown");
            _selectedPackage=code; Render(); await Page("12-properties-update");
            Navigate("installed"); await Page("13-installed-apps");
            _updatesOnly=true; ApplySearch(); await Page("14-updates"); _updatesOnly=false;
            Navigate("home"); _searchData=new([jan,lm,epic],"SIMULATED WinGet search.",true); SetCaptureSearch("studio"); await Page("15-search-results");
            _collectionListView=true; _searchLimit=99; _searchTerm="package"; _searchData=new(Enumerable.Range(1,99).Select(i=>new PackageItem("Package "+i,"Test.Package"+i,"1","","winget","Fixture publisher")).ToArray(),"99 simulated search results.",true,true); SetCaptureSearch("package"); await Page("16-search-load-more");
            var realized = Enumerable.Range(0,100).Count(i=>_packageList!.ItemContainerGenerator.ContainerFromIndex(i) is not null); Check(realized > 0 && realized < 50,"100 compact entries realize fewer than 50 containers");
            await LoadMoreAsync(); Check(_searchData.Items.Count == 198 && _searchData.Items.Select(x=>x.Id).Distinct().Count()==198,"Load More expands and deduplicates search results"); await Page("17-search-loaded-198");
            _searchData=new([],"No matching packages found.",true); SetCaptureSearch("no-such-package"); await Page("18-search-empty");
            _searchData=new([],"SIMULATED: source unavailable; results cannot be determined.",false); SetCaptureSearch("offline"); await Page("19-search-error");
            SetCaptureSearch(""); _collectionListView=false; Navigate("collection:ai"); _diskCachedCollections.Add("ai"); _collectionData["ai"] = (_collectionData["ai"].Result, DateTime.UtcNow.AddDays(-3)); ApplySearch(); await Page("20-cached-catalogue-age");
            Navigate("install"); _installSelection.Clear(); ApplySearch(); await Page("21-install-empty"); AddToInstallSelection(jan); AddToInstallSelection(lm); ApplySearch(); await Page("22-install-selection");
            var review=CreateInstallConfirmationDialog(_installSelection,true); review.Show(); await ShotElement(review,"23-review-selection-dialog.png"); review.Close();
            var approval=CreateInstallConfirmationDialog(_installSelection); approval.Show(); await ShotElement(approval,"24-final-real-install-approval-not-submitted.png"); approval.Close();
            await StartInstallQueueAsync(); Check(_installSelection.Count==0 && _installReports.Count>=2 && !_installRunning,"simulation completes with a separate report and empty consumed selection");
            Navigate("home"); Navigate("install"); Check(_route=="install" && _installReports.Count>=2,"Install Centre can be revisited after a completed mock job"); await Page("25-install-completed-report");
            _installRunning=true; _activeInstallItems=[jan]; _installElapsed.Restart(); _installStage="Jan: Download: simulated; percentage unavailable"; ApplySearch(); await Page("26-active-queue-indeterminate");
            _installSelection.Add(lm); ApplySearch(); await Page("27-active-queue-separate-selection"); Check(_activeInstallItems.Count==1 && _installSelection.Count==1,"new selection cannot mutate the active reviewed queue");
            _pauseAfterCurrent=true; _installStage="Paused between apps; active installer was not frozen."; ApplySearch(); await Page("28-queue-paused-between-apps");
            _installStage="Cancellation requested; an elevated installer may continue. Check inventory before retrying."; ApplySearch(); await Page("29-cancellation-state");
            _installRunning=false; _pauseAfterCurrent=false; _activeInstallItems=[]; _installElapsed.Stop();
            var failure=CreateFailureDialog(jan,new(false,false,"Simulated verification failure.","No installation ran.")); failure.Show(); await ShotElement(failure,"30-retry-skip-stop-dialog.png"); failure.Close();
            Navigate("home"); var routeBefore=_route; var selectedBefore=_installSelection.Count; provider.Delay=450; var refresh=RefreshAllAsync(false); Check(_refreshRunning && !RefreshButton.IsEnabled,"refresh acknowledges immediately before provider completion");
            await Task.Delay(50); Navigate("collections"); await Shot("31-refresh-responsive-navigation.png"); await refresh;
            Check(_route=="collections" && _installSelection.Count==selectedBefore,"refresh preserves navigation and selection while allowing interaction"); provider.Delay=0;
            Navigate("backup"); await Page("32-backup-empty");
            var payload=new BackupPayload { Applications=[new("Jan","Jan.Jan","winget","0.6"),new("Missing application","Missing.Package","winget","1"),new("Firefox","Mozilla.Firefox","winget","143"),new("LM Studio","ElementLabs.LMStudio","winget","old")],Preferences=new() { Values=new(_saved) } };
            var backupFile=Path.Combine(ToolboxPaths.Root,"capture-restore.tlbxbackup"); ToolboxPaths.AtomicWrite(backupFile,BackupService.Export(payload)); await ImportBackupFileAsync(backupFile);
            Check(_importedBackup is not null && _restoreCandidates.Count==4 && _restoreCandidates.Any(x=>x.Verified is null) && _restoreCandidates.Any(x=>!x.SavedVersionAvailable && x.Verified is not null),"restore import resolves missing packages and unavailable saved versions without execution"); await Page("33-backup-selective-restore");
            ToolboxPaths.AtomicWrite(backupFile,"{\"Format\":\"wrong\"}"); await ImportBackupFileAsync(backupFile); Check(_importedBackup is null && _backupStatus.StartsWith("Backup rejected"),"invalid restore file is rejected without changing selection"); await Page("34-backup-rejected-file");
            _saved["Backup destination"]="Default"; ResetDraft(); await ExportBackupAsync(); Check(_lastBackupPath is not null && File.Exists(_lastBackupPath),"backup export creates a validated local file"); await Page("35-backup-export-success");
            Navigate("diagnostics"); await RunDiagnosticsAsync(false); Check(_diagnosticResults.Any(x=>x.State=="SKIPPED"),"Quick Check runs with controlled query providers"); await Page("36-diagnostics-quick");
            await RunDiagnosticsAsync(true); Check(_diagnosticResults.Any(x=>x.Area=="Windows inventory"),"Full Check includes Windows inventory"); await Page("37-diagnostics-full");
            await Task.Delay(80); ScrollBar.PageDownCommand.Execute(null, EdgeScrollBar); await Task.Delay(80);
            Check(ContentScroll.VerticalOffset > 100, "themed outer scrollbar page command moves a useful page distance");
            ContentScroll.ScrollToTop();
            _diagnosticResults=[new("PASS","Configuration","Fixture settings passed"),new("WARNING","Source","Fixture source temporarily unavailable"),new("ERROR","Reports","Fixture permission denied"),new("NO DATA","History","No prior reports"),new("SKIPPED","Integration","Registration was not requested")]; ApplySearch(); await Page("38-diagnostic-result-states");
            _recoveryStatus="Saved Toolbox snapshot: "+DiagnosticsService.Snapshot(ToolboxPaths.Root); ApplySearch(); await Page("39-recovery-snapshot");
            var report=CreateReportPreview(BuildSupportReport(),"Support"); report.Show(); await ShotElement(report,"40-support-report-preview-redaction.png"); report.Close();
            Navigate("settings");
            foreach(var section in SettingsRegistry.All.Select(x=>x.Section).Append("Keyboard").Append("Advanced").Distinct()) { _settingsTab=section; ApplySearch(); await Page("41-settings-"+section.ToLowerInvariant()); }
            _ = Dispatcher.BeginInvoke(async () => { await Task.Delay(180); if (_remapDialog is { } dialog) { await ShotElement(dialog,"41-keyboard-remap-dialog.png"); dialog.Close(); } });
            Remap("toolbox.go.home");
            var conflictDialog = CreatePrototypeDialog("Shortcut conflict", "That shortcut is already assigned. Choose another combination.", "OK"); conflictDialog.Show(); await ShotElement(conflictDialog,"41-keyboard-conflict-state.png"); conflictDialog.Close();
            _settingsTab="Reports"; _draft["Report destination"]="Custom Folder"; ApplySearch(); await Page("42-report-custom-folder-draft"); ResetDraft(); _draft["Report destination"]="Ask Every Time"; ApplySearch(); await Page("43-report-ask-each-time-draft"); ResetDraft();
            _draft["Text size"]="Large"; var pending=CreatePendingDialog(_=>{}); pending.Show(); await ShotElement(pending,"44-pending-settings-dialog.png"); pending.Close(); ResetDraft();
            _settingsTab="Appearance";
            foreach(var theme in new[]{"Void Dark","Light","System"}) { _saved["Theme"]=theme; ResetDraft(); ApplySearch(); await Page("45-theme-"+theme.ToLowerInvariant().Replace(' ','-')); }
            _saved["Theme"]="Void Dark"; _saved["Text size"]="Extra Large"; _saved["Settings spacing"]="Comfortable"; ResetDraft(); Navigate("collection:ai"); _collectionListView=true; ApplySearch(); await Page("46-extra-large-comfortable-list");
            Check(RootGrid.LayoutTransform is ScaleTransform transform && transform.ScaleX>1.2,"extra-large scaling applies to the whole interface proportionally");
            Check(_packageList is not null && _packageList.ActualWidth <= ContentScroll.ViewportWidth, "large-scale list fits the available viewport without clipping its controls");
            _saved["Text size"]="Standard"; _saved["Settings spacing"]="Compact"; _saved["Accent"]="Teal"; ResetDraft(); Navigate("settings"); _settingsTab="Appearance"; ApplySearch(); await Page("47-teal-accent-follow-artwork");
            _saved["Accent"]="Purple"; _saved["Tray icon"]="On"; ResetDraft(); UpdateTray(); Check(_tray?.Visible==true,"optional tray icon creates while app is running"); _settingsTab="Advanced"; ApplySearch(); await Page("48-tray-enabled-no-registration"); _saved["Tray icon"]="Off"; ResetDraft(); UpdateTray(); Check(_tray is null,"disabling tray disposes the notification icon");
            _draft["Collection view"]="Compact List"; Check(ApplyDraft(),"settings draft saves successfully"); Check(JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement.GetProperty("Version").GetInt32()==SettingsRegistry.Version,"settings save uses current schema");
            _saved = new(Defaults); LoadPreferences(); Check(_saved["Collection view"] == "Compact List", "saved settings reload with the chosen view");
            var savedJson = File.ReadAllText(SettingsPath);
            var conflicting = new SavedPreferences { Version = 3, Values = new(Defaults), Shortcuts = new() { ["toolbox.go.home"] = "K|Control" } };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(conflicting)); LoadPreferences();
            Check(_shortcuts.Values.GroupBy(x => (x.Key,x.Modifiers)).All(x => x.Count()==1), "conflicting saved shortcuts recover to non-conflicting defaults");
            File.WriteAllText(SettingsPath,"{ damaged settings"); LoadPreferences();
            Check(_settingsRecovery.Contains("unreadable") && Directory.EnumerateFiles(Path.Combine(ToolboxPaths.Root,"recovery"),"settings-v1.json",SearchOption.AllDirectories).Any(), "damaged preferences preserve a recovery copy and load safe defaults");
            File.WriteAllText(SettingsPath,savedJson); LoadPreferences();
            Navigate("about"); await Page("49-about-privacy-language");
            Navigate("help"); await Page("50-help-keyboard");
            await CaptureConsistencyChecksAsync(Page, Shot, ShotElement, Check);
            await CaptureSettingsTooltipChecksAsync(Shot, ShotElement, Check);
            await CaptureGlobalAccentChecksAsync(Page, Shot, ShotElement, Check);
            await CaptureCohesiveChecksAsync(Page, Shot, ShotElement, Check);
            await CaptureRefinementChecksAsync(Page, Shot, Check);
            File.WriteAllLines(Path.Combine(folder,"UI-CHECKS.txt"),checks);
            File.WriteAllLines(Path.Combine(folder,"SCREENSHOT-INDEX.txt"),files);
            File.WriteAllText(Path.Combine(folder,"CAPTURE-NOTES.txt"),"Actual running WPF app, FP2.4. Every image in this set is captured from rendered application controls or an actual WPF dialog/popup. No generated mock-ups. SIMULATED DATA banner marks fixture package and installer states. Quick/Full diagnostics use real local file checks with controlled package and Windows inventory providers. No real installer or Windows registration ran. The capture profile is isolated from user settings. UI-CHECKS.txt contains in-process integration assertions; independent Windows UI smoke evidence is reported separately.\n");
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(folder,"CAPTURE-ERROR.txt"),ex.ToString()); File.WriteAllLines(Path.Combine(folder,"UI-CHECKS.txt"),checks); Environment.ExitCode=2; }
        finally { _installRunning=false; _tray?.Dispose(); Application.Current.Shutdown(Environment.ExitCode); }
        async Task Page(string name)
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            ContentScroll.ScrollToTop(); await Shot(name+"-01.png");
            var height=ContentScroll.ViewportHeight; var max=ContentScroll.ScrollableHeight;
            for(var page=2; height>0 && (page-1)*height*.85 < max+height*.85 && page<=12; page++)
            { var offset=Math.Min(max,(page-1)*height*.85); if(offset<=0) break; ContentScroll.ScrollToVerticalOffset(offset); await Shot(name+"-"+page.ToString("00")+".png"); if(offset>=max) break; }
            ContentScroll.ScrollToTop();
        }
        async Task Shot(string name)=>await ShotElement(this,name);
        async Task ShotElement(FrameworkElement element,string name)
        {
            await Task.Delay(160); element.UpdateLayout();
            if(element.ActualWidth<1 || element.ActualHeight<1) throw new InvalidOperationException("Empty rendered capture: "+name);
            var dpi=VisualTreeHelper.GetDpi(element); var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(element.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream=File.Create(Path.Combine(folder,name)); encoder.Save(stream); files.Add(name);
        }
    }
    private void SetCaptureSearch(string term) { _rendering=true; SearchBox.Text=term; _searchTerm=term; _rendering=false; ApplySearch(); }
    private sealed class CaptureRunner : IWinGetRunner
    {
        internal int Delay;
        internal bool FailUpdates, FailSearch, FailInventory, RecordInteractive;
        internal int Total = 301;
        internal readonly List<string> Calls = new();
        public async Task<CommandResult> RunAsync(string[] args,CancellationToken token)
        {
            Calls.Add(string.Join(" ", args));
            if (RecordInteractive) File.AppendAllText(Path.Combine(ToolboxPaths.Root, "fixture-events.txt"), string.Join(" ", args) + "\n");
            if(Delay>0) await Task.Delay(Delay,token);
            if (FailInventory && args.SequenceEqual(new[] { "list" })) return new(-1, "WinGet timed out after 75 seconds while reading inventory. Retry when ready.");
            if (FailUpdates && args[0] == "list" && args.Contains("--upgrade-available")) return new(-1, "SIMULATED update source unavailable");
            string Row(string name,string id,string version,string available,string source)=>$"{name,-28}{id,-38}{version,-14}{available,-14}{source}";
            var header=Row("Name","Id","Version","Available","Source")+"\n"+new string('-',100)+"\n";
            if(args[0]=="--version") return new(0,"v1.29.380");
            if(args[0]=="source") return new(0,"Name    Argument\n---------------------\nwinget  https://cdn.winget.microsoft.com/cache");
            if(args[0]=="list") return new(0,header+Row("Visual Studio Code","Microsoft.VisualStudioCode","1.100","1.101","winget")+(args.Contains("--upgrade-available")?"":"\n"+Row("Mozilla Firefox","Mozilla.Firefox","143","","winget")));
            var idIndex=Array.IndexOf(args,"--id"); var id=idIndex>=0?args[idIndex+1]:"";
            if(id=="Missing.Package") return new(-1,"No package found matching input criteria.");
            if(args[0]=="show")
            {
                var v=Array.IndexOf(args,"--version"); if(v>=0 && args[v+1]=="old") return new(-1,"No version found");
                var version=v>=0?args[v+1]:"1.0"; var publisher=id=="Jan.Jan"?"Jan":id=="ElementLabs.LMStudio"?"Element Labs":id=="Mozilla.Firefox"?"Mozilla":"Simulated publisher";
                return new(0,"Publisher: "+publisher+"\nVersion: "+version);
            }
            if(id.Length>0) return new(0,header+Row(id.Split('.')[^1],id,"1.0","","winget"));
            if (FailSearch) return new(-1,"SIMULATED package source failed");
            var n=Array.IndexOf(args,"--count"); var count=n>=0?int.Parse(args[n+1]):Total;
            var queryIndex=Array.IndexOf(args,"--query"); var query=queryIndex<0?"":args[queryIndex+1];
            var total=query=="ninety"?90:query=="exactly99"?99:Total;
            return new(0,header+string.Join("\n",Enumerable.Range(1,Math.Min(total,count)).Select(i=>Row("Package "+i,"Test.Package"+i,"1","","winget")))+(total>count?"\nAdditional entries truncated due to count limit.":""));
        }
    }
}
