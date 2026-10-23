using ToolboxMatrix;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

foreach (var route in new[] { "home", "collections", "collection:gaming", "installed", "install", "backup", "diagnostics", "settings", "help", "about" })
{
    var selected = NavigationRules.SelectedButton(route);
    Check(selected is not null, "Route has no selected navigation item: " + route);
    Check(selected != "InstalledNav" || route == "installed", "Installed Apps highlight leaked into " + route);
    Check(selected != "InstallNav" || route == "install", "Install Centre highlight leaked into " + route);
}
Check(NavigationRules.SelectedButton("properties") is null, "Properties should not claim a sidebar route.");
Check(LaunchCommand.Parse(["search", "Visual Studio", "Code"]).Search == "Visual Studio Code", "Quoted or spaced launch search was not preserved.");
Check(LaunchCommand.Parse(["settings"]).Route == "settings", "Settings launch route failed.");
Check(LaunchCommand.Parse(["refresh"]).Refresh, "Refresh launch action failed.");
Check(LaunchCommand.Parse(["set"]).Error is not null, "Unsupported short command was silently accepted.");

var goodTable = """
Name                 Id                         Version     Available    Source
--------------------------------------------------------------------------
Visual Studio Code   Microsoft.VisualStudioCode  1.100.0     1.101.0      winget
Git                  Git.Git                    2.49.0                   winget
""";
var fake = new FakeRunner((args) => args[0] switch
{
    "list" => new(0, goodTable),
    "search" => new(0, goodTable),
    "show" => new(0, "Publisher: The Git Development Community\nVersion: 2.49.0"),
    _ => new(0, "v1.29.380")
});
Discovery.Runner = fake; Discovery.InvalidateAll();
var inventory = await Discovery.InstalledAsync(CancellationToken.None);
Check(inventory.Items.Count == 2 && inventory.Items.All(x => x.IsInstalled), "Simulated inventory parsing failed.");
await Discovery.InstalledAsync(CancellationToken.None);
Check(fake.Calls == 1, "Inventory cache did not prevent repeated query.");
Discovery.InvalidateInventory();
await Discovery.InstalledAsync(CancellationToken.None);
Check(fake.Calls == 2, "Inventory refresh did not invalidate cache.");
var concurrent = new DelayedFakeRunner(goodTable);
Discovery.Runner = concurrent; Discovery.InvalidateAll();
await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Discovery.InstalledAsync(CancellationToken.None)));
Check(concurrent.Calls == 1, "Concurrent identical inventory queries were not coalesced.");
Discovery.Runner = fake; Discovery.InvalidateAll();
var details = await Discovery.DetailsAsync(new("Git", "Git.Git", "", "", "winget"), CancellationToken.None);
Check(details.Publisher == "The Git Development Community", "Simulated publisher lookup failed.");

foreach (var (label, exitCode, message, expected) in new[]
{
    ("missing", -1, "WinGet is unavailable. Install or repair Windows Package Manager (App Installer) to use discovery.", "unavailable"),
    ("offline", -1, "Network source failed", "network connection error"),
    ("failed", 17, "Failed command with no table", "Failed command")
})
{
    Discovery.Runner = new FakeRunner(_ => new(exitCode, message)); Discovery.InvalidateAll();
    var result = await Discovery.SearchAsync("test", CancellationToken.None);
    Check(result.Items.Count == 0 && result.Status.Contains(expected, StringComparison.OrdinalIgnoreCase), label + " response was not handled: " + result.Status);
}
Discovery.Runner = new FakeRunner(_ => new(0, "Unrecognized output")); Discovery.InvalidateAll();
Check((await Discovery.SearchAsync("test", CancellationToken.None)).Status.Contains("could not be determined"), "Unparseable successful response must not look empty.");

string[]? countArgs = null;
Discovery.Runner = new FakeRunner(args => { countArgs = args; return new(0, goodTable); }); Discovery.InvalidateAll();
await Discovery.SearchAsync("editor", CancellationToken.None, 250);
Check(countArgs is not null && countArgs.Contains("250"), "Incremental WinGet --count was not passed.");
await Discovery.SearchAsync("editor", CancellationToken.None, 1500);
Check(countArgs is not null && countArgs.Contains("1000"), "WinGet --count did not respect documented maximum.");

var packages = new[] { new PackageItem("A", "A.A", "1", "1", "winget", "Publisher"), new PackageItem("B", "B.B", "1", "1", "winget", "Publisher") };
var reports = new List<InstallOutcome>();
var sim = new SimulatedInstallAdapter { ShouldFail = (item, attempt) => item.Id == "A.A" && attempt == 1 };
await new InstallQueue(sim, (_, _) => Task.FromResult(true)).RunAsync(packages,
    new Progress<(PackageItem Item, string Stage)>(), (_, _) => Task.FromResult("Retry"),
    (_, result) => reports.Add(result), CancellationToken.None);
Check(reports.Count == 3 && !reports[0].Success && reports[1].Success && reports[2].Success, "Retry sequence failed.");
reports.Clear();
sim = new SimulatedInstallAdapter { ShouldFail = (item, _) => item.Id == "A.A" };
await new InstallQueue(sim, (_, _) => Task.FromResult(true)).RunAsync(packages,
    new Progress<(PackageItem Item, string Stage)>(), (_, _) => Task.FromResult("Skip"),
    (_, result) => reports.Add(result), CancellationToken.None);
Check(reports.Count == 2 && !reports[0].Success && reports[1].Success, "Skip sequence failed.");
reports.Clear();
await new InstallQueue(sim, (_, _) => Task.FromResult(true)).RunAsync(packages,
    new Progress<(PackageItem Item, string Stage)>(), (_, _) => Task.FromResult("Stop"),
    (_, result) => reports.Add(result), CancellationToken.None);
Check(reports.Count == 1, "Stop sequence failed.");
using var cancel = new CancellationTokenSource(); cancel.Cancel();
reports.Clear();
await new InstallQueue(new SimulatedInstallAdapter(), (_, _) => Task.FromResult(true)).RunAsync(packages,
    new Progress<(PackageItem Item, string Stage)>(), (_, _) => Task.FromResult("Stop"),
    (_, result) => reports.Add(result), cancel.Token);
Check(reports.Count == 0, "Cancelled queue began work.");
using var midFlight = new CancellationTokenSource();
midFlight.CancelAfter(90);
try
{
    await new InstallQueue(new SimulatedInstallAdapter(), (_, _) => Task.FromResult(true)).RunAsync(packages,
        new Progress<(PackageItem Item, string Stage)>(), (_, _) => Task.FromResult("Stop"),
        (_, result) => reports.Add(result), midFlight.Token);
    throw new Exception("Mid-flight simulation cancellation did not interrupt the queue.");
}
catch (OperationCanceledException) { }
Check(reports.Count == 0, "Mid-flight cancellation unexpectedly produced an install result.");

Console.WriteLine("FP2 simulated smoke checks passed: navigation selection, discovery/cache/error states, and install queue success/retry/skip/stop/cancellation. No installs ran.");

sealed class FakeRunner(Func<string[], CommandResult> respond) : IWinGetRunner
{
    public int Calls { get; private set; }
    public Task<CommandResult> RunAsync(string[] args, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(respond(args)); }
}

sealed class DelayedFakeRunner(string output) : IWinGetRunner
{
    public int Calls { get; private set; }
    public async Task<CommandResult> RunAsync(string[] args, CancellationToken token)
    { Calls++; await Task.Delay(100, token); return new(0, output); }
}
