using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace WinGetToolbox;

public partial class MainWindow
{
    private readonly HashSet<string> _diskCachedCollections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _refreshPendingCollections = new(StringComparer.OrdinalIgnoreCase);
    private WindowsInventorySnapshot _windowsInventory = WindowsInventorySnapshot.Empty;
    private long _inventoryGeneration;
    private async Task RefreshPackageStatusAsync() => await RefreshAllAsync(false);

    private async Task RefreshAllAsync(bool includeSearch = true)
    {
        if (_refreshRunning) return;
        _refreshRunning = true;
        var generation = ++_inventoryGeneration;
        var route = _route; var query = SearchBox.Text.Trim();
        RefreshButton.IsEnabled = false;
        System.Windows.Automation.AutomationProperties.SetHelpText(RefreshButton, "Refreshing read-only inventory; you can keep using Toolbox");
        RetryRefreshButton.Visibility = Visibility.Collapsed;
        RefreshStatusBanner.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        RefreshStatusBanner.Background = Brushes.Transparent;
        RefreshStatusBanner.Effect = null;
        _discoveryStatus = "Refresh started · reading WinGet and Windows inventory…";
        var refreshSearch = includeSearch && query.Length >= 2 && IsPackageSearchRoute() && _saved["Search mode"] == "Online";
        var refreshCollection = route.StartsWith("collection:") && !refreshSearch;
        var pending = new List<string> { "WinGet inventory", "updates", "Windows records" };
        if (refreshSearch) pending.Add("search results");
        if (refreshCollection) pending.Add("collection");
        var totalChecks = pending.Count;
        var elapsed = Stopwatch.StartNew();
        void ShowStage() => RefreshStatusBanner.Text = $"Refreshing · {totalChecks - pending.Count}/{totalChecks} complete · {string.Join(", ", pending)} · {elapsed.Elapsed.TotalSeconds:0}s";
        RefreshStatusBanner.Visibility = Visibility.Visible;
        ShowStage();
        var statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statusTimer.Tick += (_, _) => ShowStage(); statusTimer.Start();
        void CheckFinished(string name) { pending.Remove(name); ShowStage(); }
        void RedrawData() { if (_route is not ("settings" or "help" or "about" or "backup" or "diagnostics")) ApplySearch(); }
        StartRefreshAnimation();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        try
        {
            Discovery.InvalidateAll();
            foreach (var key in CuratedIds.Keys) _refreshPendingCollections.Add(key);
            async Task Inventory()
            {
                var result = await Discovery.InstalledAsync(CancellationToken.None);
                if (generation != _inventoryGeneration) return;
                _installedData = result; ReconcileSelection(); CheckFinished("WinGet inventory"); RedrawData();
            }
            async Task Upgrades()
            {
                var result = await Discovery.UpgradesAsync(CancellationToken.None);
                if (generation != _inventoryGeneration) return;
                _upgradeData = result; UpdateSelectionBadge(); CheckFinished("updates"); RedrawData();
            }
            async Task Windows()
            {
                var result = await WindowsInventory.ReadAsync(CancellationToken.None);
                if (generation != _inventoryGeneration) return;
                _windowsInventory = result; ReconcileSelection(); CheckFinished("Windows records"); RedrawData();
            }
            var work = new List<Task> { Inventory(), Upgrades(), Windows() };
            async Task Search() { await RefreshSearchAsync(query); CheckFinished("search results"); }
            if (refreshSearch) work.Add(Search());
            await Task.WhenAll(work);
            if (refreshCollection) { await LoadCollectionAsync(route[11..], CancellationToken.None); CheckFinished("collection"); }
            var incomplete = new List<string>();
            if (_installedData?.IsReliable != true) incomplete.Add("WinGet inventory: " + (_installedData?.Status ?? "no result returned"));
            if (_upgradeData?.IsReliable != true) incomplete.Add("Updates: " + (_upgradeData?.Status ?? "no result returned"));
            if (!_windowsInventory.RegistryComplete || !_windowsInventory.PackagedComplete) incomplete.Add("Windows records: " + _windowsInventory.Status);
            if (refreshSearch && _searchData?.IsReliable != true) incomplete.Add("Search: " + (_searchData?.Status ?? "query changed or was cancelled"));
            if (refreshCollection && _refreshPendingCollections.Contains(route[11..])) incomplete.Add("Collection: fresh verification unavailable; cached entries may remain");
            _discoveryStatus = incomplete.Count == 0 ? "Refreshed at " + DateTime.Now.ToString("t") : "Refresh incomplete — " + string.Join(" · ", incomplete);
            _lastForegroundRefresh = DateTime.UtcNow;
        }
        catch (Exception ex) { _discoveryStatus = "Refresh incomplete: " + ex.Message; }
        finally
        {
            statusTimer.Stop(); elapsed.Stop();
            _refreshRunning = false; StopRefreshAnimation();
            RefreshStatusBanner.Text = _discoveryStatus;
            RefreshStatusBanner.SetResourceReference(TextBlock.ForegroundProperty, _discoveryStatus.StartsWith("Refreshed at ") ? "RefreshTimestamp" : "StatusWarning");
            if (_discoveryStatus.StartsWith("Refreshed at ")) RefreshStatusBanner.SetResourceReference(UIElement.EffectProperty, "RefreshTimestampEdge");
            RetryRefreshButton.Visibility = _discoveryStatus.StartsWith("Refreshed at ") ? Visibility.Collapsed : Visibility.Visible;
            RefreshButton.IsEnabled = true;
            System.Windows.Automation.AutomationProperties.SetHelpText(RefreshButton, "Read-only refresh (" + FormatShortcut(_shortcuts["toolbox.refresh.all"].Key, _shortcuts["toolbox.refresh.all"].Modifiers) + ")");
            if (_saved.GetValueOrDefault("Home on refresh") == "On" && includeSearch) Navigate("home");
            RedrawData();
            if (_route == "settings" && _settingsTab == "Advanced") ApplySearch();
        }
    }

    private async Task RefreshSearchAsync(string query)
    {
        CancelSearchRequest();
        var version = _searchVersion;
        var token = (_searchCancel = new CancellationTokenSource()).Token;
        QueryResult result;
        try { result = await Discovery.SearchPageAsync(query, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        if (version == _searchVersion && IsPackageSearchRoute() && _searchTerm == query && SearchBox.Text.Trim() == query)
        { _searchLimit = Discovery.SearchBatchSize; _searchData = OrderSearchPage(result); ApplySearch(); }
    }
    private async Task RefreshVisibleCollectionAsync(string key)
    {
        try { await LoadCollectionAsync(key, CancellationToken.None); if (_route == "collection:" + key) ApplySearch(); }
        catch (Exception ex) { _discoveryStatus = "Collection refresh incomplete: " + ex.Message; }
    }
    private void ReconcileSelection()
    {
        _installSelection.RemoveAll(x => x.Operation == "Install" && InstallationStatus(x) == "Installed");
        PersistSelection();
    }
    private void StartRefreshAnimation()
    {
        var rotation = new RotateTransform(); RefreshGlyph.RenderTransform = rotation;
        rotation.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(650)) { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
    }
    private void StopRefreshAnimation() { RefreshGlyph.RenderTransform = System.Windows.Media.Transform.Identity; }

    private static string DefaultReportFolder => ExportDestinations.DefaultFolder(ExportKind.Report, ToolboxPaths.Root);

    private sealed class CachedCollection
    {
        public DateTime Time { get; set; }
        public PackageItem[] Items { get; set; } = [];
        public string Status { get; set; } = "";
    }

    private static string CuratedCachePath => Path.Combine(ToolboxPaths.Root, "curated-v1.json");

    private void LoadCuratedCache()
    {
        try
        {
            if (_captureMode) return;
            if (!File.Exists(CuratedCachePath)) return;
            var cache = JsonSerializer.Deserialize<Dictionary<string, CachedCollection>>(File.ReadAllText(CuratedCachePath));
            if (cache is null) return;
            foreach (var entry in cache)
                if (CuratedIds.ContainsKey(entry.Key) && entry.Value.Items.Length > 0)
                {
                    _collectionData[entry.Key] = (new(entry.Value.Items, entry.Value.Status), entry.Value.Time);
                    _diskCachedCollections.Add(entry.Key);
                }
        }
        catch (Exception) { /* A damaged cache never prevents startup. */ }
    }

    private void SaveCuratedCache()
    {
        if (_captureMode || Discovery.UsesTestProvider || WindowsInventory.Provider is not null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CuratedCachePath)!);
            var cache = _collectionData.Where(x => x.Value.Result.Items.Count > 0).ToDictionary(x => x.Key,
                x => new CachedCollection { Time = x.Value.Time, Items = x.Value.Result.Items.ToArray(), Status = x.Value.Result.Status });
            var temp = CuratedCachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache));
            File.Move(temp, CuratedCachePath, true);
        }
        catch (Exception) { /* Live discovery continues when cache storage is unavailable. */ }
    }

    private void PersistCurrentPreferences()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new SavedPreferences
            {
                Version = SettingsRegistry.Version, Values = new(_saved), CustomReportsFolder = _customReportsFolder, CustomBackupsFolder = _customBackupsFolder, CustomAccent = _customAccent, CustomTileColour = _customTileColour, CustomSubheadingsColour = _customSubheadingsColour,
                Shortcuts = _shortcuts.ToDictionary(x => x.Key, x => $"{x.Value.Key}|{x.Value.Modifiers}"), RetentionConsents = new(_retentionConsents)
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, SettingsPath, true);
        }
        catch (Exception ex) { ShowMock("Could not save collection view: " + ex.Message); }
    }

    private void RenderReportControls(StackPanel panel)
    {
        RenderDestinationControls(panel, ExportKind.Report);
        var openReport = new Button { Content = "Open Last Report", Background = Card, IsEnabled = _lastReportPath is not null && File.Exists(_lastReportPath), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0) };
        openReport.Click += (_, _) => { if (_lastReportPath is not null && File.Exists(_lastReportPath)) Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true, ArgumentList = { _lastReportPath } }); };
        panel.Children.Add(openReport);
    }
}
