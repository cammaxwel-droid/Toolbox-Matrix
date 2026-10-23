using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading;

namespace ToolboxMatrix;

public partial class MainWindow
{
    private static readonly Dictionary<string, string[]> CuratedIds = new()
    {
        ["ai"] = ["Ollama.Ollama", "Jan.Jan", "Microsoft.PowerToys", "ElementLabs.LMStudio"],
        ["gaming"] = ["Valve.Steam", "EpicGames.EpicGamesLauncher", "Discord.Discord", "GOG.Galaxy"],
        ["development"] = ["Microsoft.VisualStudioCode", "Git.Git", "Python.Python.3.13", "Docker.DockerDesktop"],
        ["media"] = ["VideoLAN.VLC", "GIMP.GIMP", "Audacity.Audacity", "BlenderFoundation.Blender"],
        ["studying"] = ["Anki.Anki", "DigitalScholar.Zotero", "Obsidian.Obsidian", "calibre.calibre"],
        ["security"] = ["Malwarebytes.Malwarebytes", "WiresharkFoundation.Wireshark", "Microsoft.Sysinternals.ProcessExplorer", "CrystalDewWorld.CrystalDiskInfo"],
        ["everyday"] = ["7zip.7zip", "Mozilla.Firefox", "Google.Chrome", "Notepad++.Notepad++"]
    };
    private readonly Dictionary<string, (QueryResult Result, DateTime Time)> _collectionData = new();
    private bool _collectionListView;
    private bool _prefetchStarted;
    private long _catalogGeneration;

    private async Task PrefetchCollectionsAsync()
    {
        if (_prefetchStarted || _captureMode) return;
        _prefetchStarted = true;
        var generation = _catalogGeneration;
        foreach (var collection in Collections)
        {
            if (CuratedExtras.IsExtra(collection.Key)) continue;
            if (generation != _catalogGeneration) break;
            if (_collectionData.TryGetValue(collection.Key, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(20) && !_refreshPendingCollections.Contains(collection.Key)) continue;
            try { await LoadCollectionAsync(collection.Key, CancellationToken.None); }
            catch (Exception ex) { _discoveryStatus = "Collection prefetch paused: " + ex.Message; break; }
            if (_route == "collection:" + collection.Key) ApplySearch();
        }
        _prefetchStarted = false;
    }

    private async Task LoadCollectionAsync(string key, CancellationToken token)
    {
        if (CuratedExtras.IsExtra(key)) return;
        var generation = _catalogGeneration;
        var found = new List<PackageItem>();
        var missing = new List<string>();
        var errors = new List<string>();
        var checks = CuratedIds[key].Select(async id =>
        {
            var result = await Discovery.ExactAsync(id, token);
            var exact = result.Items.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Source));
            return (id, result, detail: exact is null ? null : await Discovery.DetailsAsync(exact, token));
        });
        foreach (var (id, result, detail) in await Task.WhenAll(checks))
        {
            if (detail is null) { missing.Add(id); if (result.Status != "No matching packages found.") errors.Add(result.Status); }
            else if (detail.Publisher == "Unknown") missing.Add(id + " (publisher unconfirmed)");
            else found.Add(detail);
        }
        var status = missing.Count == 0 ? $"{found.Count} verified packages." :
            $"{found.Count} verified; unavailable: {string.Join(", ", missing)}. " + string.Join(" ", errors.Distinct());
        if (!token.IsCancellationRequested && generation == _catalogGeneration)
        {
            if (found.Count > 0)
            {
                _collectionData[key] = (new(found.DistinctBy(x => (x.Id.ToUpperInvariant(), x.Source.ToUpperInvariant())).ToArray(), status), DateTime.UtcNow);
                _diskCachedCollections.Remove(key);
                _refreshPendingCollections.Remove(key);
                SaveCuratedCache();
            }
            else if (!_collectionData.ContainsKey(key)) _collectionData[key] = (new([], status), DateTime.UtcNow);
        }
    }
    private QueryResult? _installedData, _upgradeData, _searchData;
    private string _searchTerm = "", _discoveryStatus = "Checking WinGet…";
    private int _searchLimit = Discovery.SearchBatchSize;
    private long _searchVersion;
    private string _loadMoreError = "";
    private void CancelSearchRequest()
    {
        _searchVersion++;
        _searchCancel?.Cancel(); _searchCancel?.Dispose(); _searchCancel = null;
        _loadingMore = false; _loadMoreError = "";
    }
    private QueryResult OrderSearchPage(QueryResult result, IReadOnlyList<PackageItem>? prior = null)
    {
        if (_saved["Search order"] != "Name" || !result.IsReliable) return result;
        var prefix = prior ?? [];
        return result with { Items = prefix.Concat(result.Items.Skip(prefix.Count).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)).ToArray() };
    }
    private bool _loadingMore;
    private string _searchScope = "All Apps";
    private CancellationTokenSource? _searchCancel, _routeCancel;
    private PackageItem? _selectedPackage;
    private string _packageDetailStatus = "";

    private async Task RefreshUpdatesAsync()
    {
        var generation = _inventoryGeneration;
        _upgradeData = null;
        ApplySearch();
        try
        {
            Discovery.InvalidateInventory();
            var result = await Discovery.UpgradesAsync(CancellationToken.None);
            if (generation != _inventoryGeneration) return;
            _upgradeData = result;
            if (_route == "installed" || _searchScope == "Updates") ApplySearch();
        }
        catch (Exception ex) { _discoveryStatus = "Update check failed: " + ex.Message; ApplySearch(); }
    }

    private async void StartDetection()
    {
        try { _sourceCheckStatus = await Discovery.DetectAsync(CancellationToken.None); await RefreshAllAsync(false); }
        catch (Exception ex) { _discoveryStatus = "Discovery unavailable: " + ex.Message; ApplySearch(); }
    }

    private async void RefreshDiscoveryRoute()
    {
        _routeCancel?.Cancel();
        var cts = new CancellationTokenSource();
        _routeCancel = cts;
        try
        {
            if (_route == "installed")
            {
                if (_installedData is null) await RefreshPackageStatusAsync();
            }
            else if (_route.StartsWith("collection:"))
            {
                var key = _route[11..];
                if (!_collectionData.TryGetValue(key, out var cached) || DateTime.UtcNow - cached.Time > TimeSpan.FromMinutes(20) || _refreshPendingCollections.Contains(key))
                    await LoadCollectionAsync(key, cts.Token);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _discoveryStatus = "WinGet read failed: " + ex.Message; }
        if (!cts.IsCancellationRequested) ApplySearch();
    }

    private void ScheduleSearch()
    {
        CancelSearchRequest();
        var term = SearchBox.Text.Trim();
        _searchTerm = term; _searchData = null; _searchLimit = Discovery.SearchBatchSize;
        ApplySearch();
        if (_captureMode && !Environment.GetCommandLineArgs().Contains("--interactive-fixtures")) return;
        if (_saved["Search mode"] == "Cache only" && IsPackageSearchRoute() && term.Length >= 2)
        {
            var cached = _collectionData.Values.SelectMany(x => x.Result.Items).Where(x => x.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || x.Id.Contains(term, StringComparison.OrdinalIgnoreCase)).DistinctBy(x => (x.Id, x.Source)).ToArray();
            var age = _collectionData.Count == 0 ? "no cache available" : "oldest saved group " + _collectionData.Values.Min(x => x.Time).ToLocalTime().ToString("g");
            _searchData = new(cached, "Offline cached catalogue · " + age + ". Verify live before installing.", false); ApplySearch(); return;
        }
        if (term.Length < 2 || !IsPackageSearchRoute() || _searchScope != "All Apps") return;
        var cts = new CancellationTokenSource(); _searchCancel = cts;
        _ = SearchLaterAsync(term, cts.Token, _searchVersion);
    }

    private async Task SearchLaterAsync(string term, CancellationToken token, long version)
    {
        try
        {
            await Task.Delay(_saved.GetValueOrDefault("Search delay") == "Patient" ? 700 : 350, token);
            var result = await Discovery.SearchPageAsync(term, token);
            if (token.IsCancellationRequested || version != _searchVersion || SearchBox.Text.Trim() != term || !IsPackageSearchRoute()) return;
            _searchData = OrderSearchPage(result);
            ApplySearch();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version != _searchVersion || token.IsCancellationRequested) return;
            _searchData = new([], "Search could not finish: " + ex.Message, false); ApplySearch();
        }
    }

    private void AddPackageTiles(IEnumerable<PackageItem> items, string query = "") => RenderPackageItems(items, query);

    private async void OpenPackage(PackageItem item)
    {
        _selectedPackage = item;
        _packageDetailStatus = "Verifying publisher and package status…";
        Navigate("properties");
        if (string.IsNullOrWhiteSpace(item.Source)) { _packageDetailStatus = "WinGet did not identify a source for this package."; ApplySearch(); return; }
        try
        {
            var detailed = _captureMode ? item : await Discovery.DetailsAsync(item, CancellationToken.None);
            if (_selectedPackage == item && _route == "properties")
            {
                _selectedPackage = detailed;
                _packageDetailStatus = detailed.Publisher == "Unknown" ? "Publisher could not be verified from WinGet." : "Package details verified.";
                Render();
            }
        }
        catch (Exception ex) { _packageDetailStatus = "Package details could not be loaded: " + ex.Message; ApplySearch(); }
    }

    private bool RenderDiscovery(string query)
    {
        if (_route.StartsWith("collection:"))
        {
            var key = _route[11..];
            if (CuratedExtras.IsExtra(key)) { RenderExtraCollection(key, query); return true; }
            if (_collectionData.TryGetValue(key, out var cached))
            {
                var data = cached.Result;
                AddPackageToolbar(data.Items);
                AddPackageTiles(data.Items, query);
                FooterNote.Text = (_diskCachedCollections.Contains(key) || _refreshPendingCollections.Contains(key) || DateTime.UtcNow - cached.Time > TimeSpan.FromMinutes(20) ? "Cached at " + cached.Time.ToLocalTime().ToString("g") + "; live status unverified. " : "Verified this session. ") + data.Status;
            }
            else { TilesPanel.Children.Add(CreateInfo("Loading verified packages…")); FooterNote.Text = "Checking exact package identities in your WinGet sources…"; }
        }
        else if (_route == "installed")
        {
            if (_installedData is null) FooterNote.Text = "Reading installed applications from WinGet…";
            else
            {
                var upgrades = _upgradeData?.Items.ToDictionary(x => x.Id + "|" + x.Source, StringComparer.OrdinalIgnoreCase);
                var inventory = _installedData.Items.Select(x => upgrades is not null && upgrades.TryGetValue(x.Id + "|" + x.Source, out var upgrade) ? x with { Available = upgrade.Available } : x);
                if (_saved["Update display"] == "Needs attention first") inventory = inventory.OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Available));
                if (_updatesOnly) inventory = inventory.Where(x => !string.IsNullOrWhiteSpace(x.Available));
                AddPackageToolbar(inventory);
                var filtered = inventory.Where(x => query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                _hasMoreResults = filtered.Length > _inventoryLimit;
                AddPackageTiles(filtered.Take(_inventoryLimit));
                FooterNote.Text = "Installed: " + _installedData.Status + " Available upgrades: " + (_upgradeData?.Items.Count.ToString() ?? "checking") + ". " + (_upgradeData?.Status ?? "");
                if (_upgradeData is { Items.Count: > 0 })
                {
                    TilesPanel.Children.Add(new Border
                    {
                        Width = Math.Max(240, ContentScroll.ViewportWidth - 18), Background = Card,
                        CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 4, 0, 6),
                        Child = Note("Available updates · " + string.Join(", ", _upgradeData.Items.Select(x => x.Name + " → " + x.Available)))
                    });
                }
            }
        }
        else if (_route == "properties")
        {
            var item = _selectedPackage;
            if (item is null) TilesPanel.Children.Add(CreateInfo("Select a package from search, a collection, or Installed Apps."));
            else
            {
                var logo = LogoRegistry.Create(item.Id, 60);
                if (logo is not null) TilesPanel.Children.Add(new Border { Width = 310, Height = 76, Background = Card,
                    CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 8),
                    Child = logo });
                var status = InstallationStatus(item);
                if (status == "Not Installed" && item.Publisher != "Unknown" && item.Source.Length > 0)
                {
                    var select = new Button { Content = "Add to selection", Background = Accent, Height = 36,
                        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 4) };
                    select.Click += (_, _) => AddToInstallSelection(item);
                    TilesPanel.Children.Add(select);
                }
                else if (status == "Installed" && UpgradeFor(item) is { } update)
                {
                    var addUpdate = new Button { Content = "Select update to " + update.Available, Background = Accent, Margin = new Thickness(0, 6, 6, 6) };
                    addUpdate.Click += (_, _) => SelectUpdate(update); TilesPanel.Children.Add(addUpdate);
                }
                else TilesPanel.Children.Add(CreateInfo(status == "Installed" ?
                    "Already installed. Check Installed Apps for available updates." :
                    status == "Unknown" ? "Selection is unavailable until WinGet confirms the installed inventory and package source. Reopen this page after discovery finishes." :
                    _packageDetailStatus));
                foreach (var field in new[] { ("Display name", item.Name), ("Package ID", item.Id),
                    ("Source", string.IsNullOrWhiteSpace(item.Source) ? "Unknown" : item.Source),
                    ("Publisher", item.Publisher), ("Installed version", InventoryMatcher.Resolve(item, _installedData, _windowsInventory).Version is { Length: > 0 } installedVersion ? installedVersion : "Unknown"),
                    ("Available version", string.IsNullOrWhiteSpace(item.Available) ? (item.IsInstalled ? "Unknown" : item.Version) : item.Available),
                    ("Installation", InstallationStatus(item)),
                    ("Size", "Not provided by WinGet") })
                    if (query.Length == 0 || field.Item1.Contains(query, StringComparison.OrdinalIgnoreCase) || field.Item2.Contains(query, StringComparison.OrdinalIgnoreCase))
                        TilesPanel.Children.Add(CreatePropertyRow(field.Item1, field.Item2));
            }
            FooterNote.Text = _packageDetailStatus + " Package details come from WinGet. Size appears only if verifiable.";
        }
        else return false;
        return true;
    }

    private string InstallationStatus(PackageItem item) => InventoryMatcher.Resolve(item, _installedData, _windowsInventory).State;

    private void AddSearchResults(string query)
    {
        if (query.Length < 2 || !IsPackageSearchRoute()) return;
        if (_searchScope == "Collections")
        {
            if (_route == "collections") return;
            foreach (var item in Collections.Where(c => CollectionMatches(c, query)))
                TilesPanel.Children.Add(CreateCollectionTile(item));
            FooterNote.Text = "Collection matches are local to Toolbox; no package source query is needed.";
            return;
        }
        if (_searchScope is "Installed" or "Updates")
        {
            var data = _searchScope == "Installed" ? _installedData : _upgradeData;
            TilesPanel.Children.Add(CreateInfo(_searchScope == "Updates" ? "WinGet available updates" : "WinGet installed applications"));
            if (data is null) TilesPanel.Children.Add(CreateInfo("Reading WinGet inventory…"));
            else
            {
                AddPackageTiles(data.Items, query);
                if (!data.Items.Any(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Id.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    TilesPanel.Children.Add(CreateInfo("No matching packages in this scope. " + data.Status));
                FooterNote.Text = data.Status;
            }
            return;
        }
        if (_searchData is null) TilesPanel.Children.Add(CreateInfo("Searching WinGet…"));
        else
        {
            _hasMoreResults = _searchData.IsReliable && _searchData.Items.Count > 0 && _searchData.Truncated;
            AddPackageToolbar(_searchData.Items);
            AddPackageTiles(_searchData.Items);
            FooterNote.Text = string.IsNullOrEmpty(_loadMoreError) ?
                (_searchData.IsReliable && !_searchData.Status.Contains("further readable") ? "" : _searchData.Status) : "Load More failed: " + _loadMoreError + " Your results are still here; try again.";
            FooterNote.Visibility = FooterNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await LoadMoreAsync();

    private async Task LoadMoreAsync()
    {
        if (_route == "installed") { _inventoryLimit += 100; ApplySearch(); return; }
        if (_loadingMore || !_hasMoreResults || _searchTerm.Length < 2 || !IsPackageSearchRoute()) return;
        var previous = _searchData;
        if (previous is null) return;
        var term = _searchTerm; var version = _searchVersion;
        var token = (_searchCancel ??= new CancellationTokenSource()).Token;
        var focusAfter = LoadMoreButton.IsKeyboardFocusWithin || _packageList?.IsKeyboardFocusWithin == true;
        bool StillCurrent() => version == _searchVersion && !token.IsCancellationRequested && IsPackageSearchRoute() &&
            _searchTerm == term && SearchBox.Text.Trim() == term && ReferenceEquals(_searchData, previous);
        _loadingMore = true; _loadMoreError = ""; UpdateLoadMoreVisibility();
        try
        {
            var next = await Discovery.SearchPageAsync(term, token, previous.Items);
            if (!StillCurrent()) return;
            if (next.IsReliable)
            {
                _searchData = OrderSearchPage(next, previous.Items);
                _searchLimit = _searchData.Items.Count;
                if (focusAfter) _restorePackageFocus = _searchData.Items.Skip(previous.Items.Count).FirstOrDefault();
            }
            else _loadMoreError = next.Status;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (StillCurrent()) _loadMoreError = ex.Message; }
        finally
        {
            if (version == _searchVersion)
            {
                _loadingMore = false;
                _restoreMoreFocus = focusAfter && _restorePackageFocus is null;
                ApplySearch();
            }
        }
    }

    private static bool CollectionMatches(Collection collection, string query) =>
        ReleaseFeatures.IsCollectionVisible(collection.Key) && (
        collection.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (collection.Key == "gaming" && "game games gaming".Contains(query, StringComparison.OrdinalIgnoreCase)) ||
        (collection.Key == "studying" && "study learning education".Contains(query, StringComparison.OrdinalIgnoreCase)) ||
        (collection.Key == "security" && "diagnostic diagnostics safety".Contains(query, StringComparison.OrdinalIgnoreCase)));

    private UIElement CreateCollectionTile(Collection collection)
    {
        var tile = CreateTile(collection.Name, collection.Mark, () => Navigate("collection:" + collection.Key));
        if (CuratedExtras.IsExtra(collection.Key)) return tile;
        if (tile is FrameworkElement element)
        {
            var menu = CreateDarkMenu();
            // Populate at opening, so a completed background collection fetch is immediately usable.
            menu.Opened += (_, _) =>
            {
                menu.Items.Clear();
                if (_collectionData.TryGetValue(collection.Key, out var cached))
                    foreach (var item in cached.Result.Items)
                    {
                        var state = Selected(item) ? "Selected" : InstallationStatus(item);
                        var entry = new MenuItem { Header = item.Name + (state == "Not Installed" ? "" : " · " + state), IsEnabled = Eligible(item) && !Selected(item), StaysOpenOnClick = false };
                        System.Windows.Automation.AutomationProperties.SetName(entry, item.Name);
                        entry.Click += (_, _) =>
                        {
                            // Recheck current inventory at click time; never navigate or open app Properties.
                            if (Eligible(item) && !Selected(item)) AddToInstallSelection(item, updateFooter: false);
                            menu.IsOpen = false;
                        };
                        menu.Items.Add(entry);
                    }
                if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Package list is loading or unavailable", IsEnabled = false });
            };
            element.ContextMenu = menu;
        }
        return tile;
    }

    private static ContextMenu CreateDarkMenu()
    {
        var menu = new ContextMenu { Background = SurfaceBrush, Foreground = TextBrush, BorderBrush = Accent, BorderThickness = new Thickness(1) };
        var itemStyle = new Style(typeof(MenuItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, SurfaceBrush));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, TextBrush));
        itemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Accent));
        menu.Resources.Add(typeof(MenuItem), itemStyle);
        return menu;
    }
}
