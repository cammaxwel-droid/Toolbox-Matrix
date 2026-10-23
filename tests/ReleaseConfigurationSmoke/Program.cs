using System.Reflection;
using System.Text.RegularExpressions;
using WinGetToolbox;

// Console-only: exercises pure production policy/CLI code and inspects UI source.
// Never constructs App, MainWindow, a WPF control, a provider or a user profile.
try
{
    if (args.Length != 2 || args[0] is not ("V1" or "Release" or "Debug"))
        throw new ArgumentException("Usage: ReleaseConfigurationSmoke <V1|Release|Debug> <repository-root>");
    var configuration = args[0];
    var v1 = configuration == "V1";
    var root = Path.GetFullPath(args[1]);
    var checks = 0;
    void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        checks++;
    }
    string Source(string name) => File.ReadAllText(Path.Combine(root, "src", "WinGetToolbox", name));
    var assembly = typeof(ReleaseFeatures).Assembly;
    Check(assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration == configuration,
        "Tests must load the requested production configuration.");
    Check(ReleaseFeatures.IncludeUnfinishedCollections == !v1, "Only V1 hides unfinished collections.");
    Check(ReleaseFeatures.VersionSuffix == (v1 ? " candidate" : ""), "Candidate display identity is V1-only.");
    Check(assembly.GetName().Version == new Version(v1 ? "1.0.0.0" : "0.5.0.0"), "Assembly identity matches the profile.");
    Check(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion
        .StartsWith(v1 ? "1.0.0-candidate" : "0.5.0-FP0.5.0", StringComparison.Ordinal),
        "File metadata distinguishes the candidate from development.");

    var main = Source("MainWindow.xaml.cs");
    var discovery = Source("DiscoveryUi.cs");
    var catalog = Regex.Match(main, @"Collection\[\] Collections\s*=\s*\[(.*?)\];", RegexOptions.Singleline);
    Check(catalog.Success, "Inspect the actual UI collection catalogue.");
    var entries = Regex.Matches(catalog.Groups[1].Value, "new\\(\"([^\"]+)\", \"([^\"]+)\", \"([^\"]+)\"\\)")
        .Select(match => (Key: match.Groups[1].Value, Name: match.Groups[2].Value, Mark: match.Groups[3].Value)).ToArray();
    (string Key, string Name, string Mark)[] expected =
    [
        ("ai", "AI & Automation", "AI"), ("gaming", "Gaming", "G"),
        ("development", "Development", "<>"), ("media", "Media / Creative", "M"),
        ("studying", "Studying", "S"), ("security", "Security / Diagnostics", "+"),
        ("everyday", "Everyday Essentials", "E"), ("tools", "Tools", "T"), ("sites", "Sites", "S")
    ];
    Check(entries.SequenceEqual(expected), "All nine source categories retain their order, names and artwork marks.");
    var visible = entries.Where(entry => ReleaseFeatures.IsCollectionVisible(entry.Key)).ToArray();
    Check(visible.SequenceEqual(v1 ? expected[..7] : expected), "V1 exposes exactly seven categories; development exposes nine.");
    foreach (var entry in expected[..7])
    {
        Check(ReleaseFeatures.IsCollectionVisible(entry.Key), entry.Key + " remains visible.");
        Check(ReleaseFeatures.ResolveRoute("collection:" + entry.Key) == "collection:" + entry.Key,
            entry.Key + " navigation is unchanged.");
        Check(NavigationRules.SelectedButton("collection:" + entry.Key) == "CollectionsNav",
            entry.Key + " keeps its sidebar selection.");
    }
    foreach (var key in new[] { "tools", "sites", "TOOLS", "SITES" })
    {
        Check(ReleaseFeatures.IsCollectionVisible(key) == !v1, key + " follows the active configuration.");
        var route = "collection:" + key;
        var resolved = ReleaseFeatures.ResolveRoute(route);
        Check(resolved == (v1 ? "collections" : route), route + " is safely redirected only in V1.");
        Check(ReleaseFeatures.ResolveRoute(resolved) == resolved, "Resolved history/cache routes are stable.");
        Check(NavigationRules.SelectedButton(resolved) == "CollectionsNav", "Hidden-category fallback selects Collections.");
    }
    foreach (var route in new[] { "home", "collections", "installed", "install", "backup", "diagnostics",
        "settings", "about", "help", "properties", "collection:unknown", "" })
        Check(ReleaseFeatures.ResolveRoute(route) == route, "Unrelated route is untouched: " + route);
    foreach (var command in LaunchCommand.Commands)
    {
        var parsed = LaunchCommand.Parse([command.Name]);
        Check(parsed.Error is null && ReleaseFeatures.ResolveRoute(parsed.Route) == command.Route &&
            parsed.Refresh == command.Refresh, "Existing tlbxm command is unchanged: " + command.Name);
    }
    Check(LaunchCommand.Parse([], "Collections").Route == "collections", "Saved Collections start page remains valid.");
    Check(LaunchCommand.Parse(["search", "visual", "studio"]).Search == "visual studio", "Existing CLI search remains valid.");
    Check(CuratedExtras.Tools.Count == 0 && CuratedExtras.Sites.Count == 0,
        "Both deliberately empty source registries remain compiled into each profile.");
    Check(ReleaseFeatures.CollectionsFooter.Contains("Tools and Sites", StringComparison.Ordinal) == !v1,
        "Collection guidance describes only enabled categories.");

    // Static integration checks complement policy tests without starting WPF.
    Check(Regex.IsMatch(discovery, @"CollectionMatches\(Collection collection, string query\)\s*=>\s*ReleaseFeatures\.IsCollectionVisible\(collection\.Key\)\s*&&"),
        "The shared UI matching path applies the visibility policy before name/alias matching.");
    Check(main.Contains("Collections.Where(c => CollectionMatches(c, query))", StringComparison.Ordinal) &&
        discovery.Contains("Collections.Where(c => CollectionMatches(c, query))", StringComparison.Ordinal),
        "Collection browsing and collection-scoped search both use the shared matching path.");
    var navigateStart = main.IndexOf("private void Navigate(", StringComparison.Ordinal);
    var navigateEnd = main.IndexOf("private void Render()", navigateStart, StringComparison.Ordinal);
    var navigate = main[navigateStart..navigateEnd];
    Check(navigate.IndexOf("route = ReleaseFeatures.ResolveRoute(route);", StringComparison.Ordinal) is var guard &&
        guard >= 0 && guard < navigate.IndexOf("if (_route == route)", StringComparison.Ordinal) &&
        guard < navigate.IndexOf("Remember();", StringComparison.Ordinal),
        "Navigation resolves hidden routes before history/state or rendering.");
    Check(navigate.Contains("if (!record && historyTarget >= 0) { _historyIndex = historyTarget; Render(); }", StringComparison.Ordinal),
        "A history entry resolving to the current page still advances Back/Forward.");
    Check(main.Contains("Navigate(_history[_historyIndex - 1], false, _historyIndex - 1)", StringComparison.Ordinal) &&
        main.Contains("Navigate(_history[_historyIndex + 1], false, _historyIndex + 1)", StringComparison.Ordinal),
        "Back and Forward retain the guarded navigation path.");
    Check(main.Contains("FooterNote.Text = ReleaseFeatures.CollectionsFooter;", StringComparison.Ordinal) &&
        main.Contains("ToString(3) + ReleaseFeatures.VersionSuffix;", StringComparison.Ordinal),
        "Runtime guidance and version label use the compiled profile.");

    Console.WriteLine($"ReleaseConfigurationSmoke {configuration}: {checks} checks passed; {visible.Length} visible categories. No app/UI/provider launched.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("ReleaseConfigurationSmoke FAILED: " + error.Message);
    return 1;
}
