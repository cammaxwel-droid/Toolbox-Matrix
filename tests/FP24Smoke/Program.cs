using System.IO;
using System.Text.Json;
using System.Windows.Input;
using ToolboxMatrix;

if (args.Contains("--live-inventory"))
{
    var live = await WindowsInventory.ReadAsync(CancellationToken.None);
    Console.WriteLine($"Read-only Windows inventory: {live.Apps.Count} records; registry complete={live.RegistryComplete}; packaged apps complete={live.PackagedComplete}. {live.Status}");
    if (!live.RegistryComplete || !live.PackagedComplete) Environment.ExitCode = 2;
    return;
}
var count = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
void Reject(Action action, string name) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException) { Check(true, name); return; } throw new Exception("Accepted unsafe input: " + name); }
string Row(string name, string id, string version, string source) => $"{name,-28}{id,-38}{version,-18}{source}";
var table = Row("Name","Id","Version","Source") + "\n" + new string('-',95) + "\n" + Row("Firefox","Mozilla.Firefox","144","winget");
Check(Discovery.ParseTable(table + "\n" + Row("Firefox","Mozilla.Firefox","144","winget") + "\nAdditional entries truncated due to count limit.\nMore packages have unknown versions.").Count == 1, "parser rejects footers and duplicate rows");
Check(Discovery.ParseTable("Nom  Identifiant  Version\n----------------------\nNom  Test.App  1").Count == 0, "unsupported table language remains unknown");
Check(!Discovery.SafeIdentity("--override", "winget") && !Discovery.SafeIdentity("A;calc", "winget") && !Discovery.SafeIdentity("A.A", "https://bad"), "package identities cannot become switches, shell commands or source URLs");
var empty = new QueryResult([], "No installed packages found.", true);
var windows = new WindowsInventorySnapshot([],true,true,"fixture",DateTime.UtcNow);
var vlc = new PackageItem("VLC media player","VideoLAN.VLC","3.0.21","","winget","VideoLAN");
var match = InventoryMatcher.Resolve(vlc, empty, windows with { Apps = [new("VLC media player 3.0.20","3.0.20","VideoLAN")] });
Check(match.State == "Installed" && match.Version == "3.0.20", "Windows fallback matches VLC name and publisher independently of latest version");
Check(InventoryMatcher.Resolve(vlc, empty, windows with { Apps = [new("VLC media player","3","Unrelated publisher")] }).State == "Unknown", "ambiguous Windows name does not claim installed or allow reinstall");
Check(InventoryMatcher.Resolve(vlc, empty, windows).State == "Not Installed", "reliable empty inventories allow a new installation");
Check(InventoryMatcher.Resolve(vlc, empty, WindowsInventorySnapshot.Empty).State == "Unknown", "incomplete Windows inventory blocks blind reinstall");
Check(InventoryMatcher.Resolve(vlc with { IsInstalled = true }, empty, windows).State == "Not Installed", "stale package object cannot override fresh inventory");
Check(InventoryMatcher.Resolve(vlc, empty with { Items = [vlc with { Version = "3.0.20" }] }, windows).Version == "3.0.20", "exact inventory version wins over catalogue version");
var payload = new BackupPayload { Applications = [new("Firefox","Mozilla.Firefox","winget","144")], Preferences = new() { Values = SettingsRegistry.Defaults } };
var exported = BackupService.Export(payload);
Check(BackupService.Import(exported).Applications[0].Id == "Mozilla.Firefox", "backup round-trip preserves identities and versioned preferences");
var legacyPreferences = JsonSerializer.Deserialize<PortablePreferences>("{\"Version\":3,\"Values\":{\"Accent\":\"Pink\"}}")!;
Check(legacyPreferences.CustomTileColour == "#7F298F" && SettingsRegistry.Validate(legacyPreferences.Values)["Tile Artwork Colour"] == "Follow Accent", "older preferences default to Follow Accent with the original custom purple");
payload.Preferences.Values["Tile Artwork Colour"] = "Custom Colour";
payload.Preferences.CustomTileColour = "#F2BA49";
var colourRoundtrip = BackupService.Import(BackupService.Export(payload));
Check(colourRoundtrip.Preferences.CustomTileColour == "#F2BA49" && colourRoundtrip.Preferences.Values["Tile Artwork Colour"] == "Custom Colour", "backup round-trip preserves independent tile artwork preference");
Reject(() => BackupService.Export(new BackupPayload { Preferences = new() { CustomTileColour = "not-a-colour" } }), "invalid portable tile artwork colour rejected");
Check(SettingsRegistry.Validate(new() { ["Tile Artwork Colour"] = "Unknown option" })["Tile Artwork Colour"] == "Follow Accent", "unknown artwork mode recovers to Follow Accent");
Check(NavigationRules.SelectedButton("help") == "HelpNav" && LaunchCommand.Parse(["help"]).Route == "help", "Help sidebar and launch command share the Help route");
Check(legacyPreferences.CustomSubheadingsColour == "#7F298F" && SettingsRegistry.Validate(legacyPreferences.Values)["Subheadings Colour"] == "Purple", "old preferences keep existing options and gain safe subheadings defaults");
payload.Preferences.Values["Subheadings Colour"] = "Custom"; payload.Preferences.CustomSubheadingsColour = "#477ACB";
var subheadingsRoundtrip = BackupService.Import(BackupService.Export(payload));
Check(subheadingsRoundtrip.Preferences.CustomSubheadingsColour == "#477ACB" && subheadingsRoundtrip.Preferences.Values["Subheadings Colour"] == "Custom" && subheadingsRoundtrip.Preferences.CustomTileColour == "#F2BA49", "backup round-trip preserves independent subheadings and tile colours");
Reject(() => BackupService.Export(new BackupPayload { Preferences = new() { CustomSubheadingsColour = "bad" } }), "invalid portable subheadings colour rejected");
Check(SettingsRegistry.Label("Theme") == "Background" && SettingsRegistry.Validate(new() { ["Theme"] = "Light" })["Theme"] == "Light", "Background is a label change preserving existing saved themes");
var envelope = JsonSerializer.Deserialize<BackupEnvelope>(exported)!; envelope.Payload += " ";
Reject(() => BackupService.Import(JsonSerializer.Serialize(envelope)), "backup checksum mismatch rejected");
Reject(() => BackupService.Import(exported.Replace("\"Format\":", "\"Command\":\"calc.exe\",\"Format\":")), "unknown executable backup field rejected");
Reject(() => BackupService.Export(new BackupPayload { SchemaVersion = 999 }), "future backup schema rejected");
Reject(() => BackupService.Export(new BackupPayload { Applications = [new("Bad","A.A --force","winget","1")] }), "command-like package ID rejected");
Reject(() => BackupService.Export(new BackupPayload { Applications = [new("A","A.A","winget","1"),new("a","a.a","WinGet","2")] }), "duplicate restore identity rejected");
Reject(() => BackupService.Import(new string('x', 5*1024*1024+1)), "oversized backup rejected before parsing");
var argsInstall = WinGetInstallAdapter.BuildArguments(vlc);
Check(argsInstall[0] == "install" && argsInstall.Contains("--no-upgrade") && argsInstall.Contains("--exact") && argsInstall.Contains("--disable-interactivity"), "normal install uses exact identity and prevents automatic upgrade");
var argsUpdate = WinGetInstallAdapter.BuildArguments(vlc with { Operation = "Update", RequestedVersion = "3.0.22" });
Check(argsUpdate[0] == "upgrade" && argsUpdate.Contains("--version") && !argsUpdate.Contains("--all"), "reviewed update is individually scoped and version constrained");
Check(!argsInstall.Any(x => x.Contains("ignore-") || x == "--force" || x == "--override" || x == "--allow-reboot"), "installer-integrity safeguards are retained");
Reject(() => WinGetInstallAdapter.BuildArguments(vlc with { RequestedVersion = "--force" }), "unsafe version rejected");
Check(MainWindow.ShortcutAllowed(Key.X, ModifierKeys.Control), "Ctrl+X settings shortcut accepted");
Check(!MainWindow.ShortcutAllowed(Key.F11, ModifierKeys.None) && !MainWindow.ShortcutAllowed(Key.F4, ModifierKeys.Alt) && !MainWindow.ShortcutAllowed(Key.F12, ModifierKeys.Control | ModifierKeys.Shift), "window and recovery shortcuts reserved");
Check(!MainWindow.ShortcutAllowed(Key.L, ModifierKeys.Windows), "Windows lock shortcut cannot be remapped");
var normalized = SettingsRegistry.Validate(new() { ["Theme"]="Invented", ["Language"]="Mandarin", ["Text size"]="Large" });
Check(normalized["Theme"] == "Void Dark" && normalized["Language"] == "English" && normalized["Text size"] == "Large", "settings migration rejects unsupported options and keeps valid ones");
Check(SettingsRegistry.Contrast("#000000","#FFFFFF") == 21 && SettingsRegistry.Contrast("#7F298F","#FFFFFF") > 4.5, "contrast computation protects readable controls");
var redacted = ReportPrivacy.Redact(@"C:\Users\PrivatePerson\logs password=abc token=xyz test@example.com https://private.example/path?q=secret");
Check(!redacted.Contains("PrivatePerson") && !redacted.Contains("abc") && !redacted.Contains("xyz") && !redacted.Contains("example.com") && !redacted.Contains("private.example"), "report redaction removes user paths, credentials, emails and URLs");
var root = Path.Combine(AppContext.BaseDirectory, "Toolbox-FP24-Tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
ToolboxPaths.AtomicWrite(Path.Combine(root, "settings-v1.json"), "{\"Version\":3,\"Values\":{}}");
ToolboxPaths.AtomicWrite(Path.Combine(root, "curated-v1.json"), "{}");
ToolboxPaths.AtomicWrite(Path.Combine(root, "session-v1.json"), "[]");
var reportTime = new DateTime(2026, 1, 1, 12, 0, 0);
var firstReport = ReportPrivacy.SaveNew(root, "Simulation", reportTime, "original report");
var secondReport = ReportPrivacy.SaveNew(root, "Simulation", reportTime, "second report");
Check(firstReport != secondReport && File.ReadAllText(firstReport) == "original report" && File.ReadAllText(secondReport) == "second report", "same-second reports preserve both files without overwriting");
var snapshot = DiagnosticsService.Snapshot(root);
Check(File.ReadAllText(Path.Combine(snapshot,"settings-v1.json")) == File.ReadAllText(Path.Combine(root,"settings-v1.json")), "Toolbox recovery snapshot preserves original settings bytes");
var old = Path.Combine(root,"Toolbox-Install-2020-01-01.txt"); File.WriteAllText(old,"owned report"); File.SetLastWriteTimeUtc(old,DateTime.UtcNow.AddDays(-100));
var unrelated = Path.Combine(root,"personal.txt"); File.WriteAllText(unrelated,"untouched"); File.SetLastWriteTimeUtc(unrelated,DateTime.UtcNow.AddDays(-100));
var migratedRetention = PreferencePolicy.Migrate(new() { ["Report retention"] = "30 days" }, 5);
Check(migratedRetention["Report retention"] == "Keep all" && File.Exists(old) && File.Exists(unrelated), "old archive-only preference migrates without authorising deletion");
Discovery.Runner = new TestRunner(a => a[0] switch { "--version" => new(0,"v1.29.380"), "source" => new(0,"Name    Argument\n----------------\nwinget  https://cdn.winget.microsoft.com/cache"), "list" => new(0,table), "search" => new(0,table.Replace("Firefox", "Git").Replace("Mozilla.Firefox","Git.Git")), _ => new(0,"Publisher: Mozilla\nVersion: 144") });
var originalRunner = Discovery.Runner;
Discovery.Runner = new TestRunner(a => a[0] == "--version" ? new(0, "v1.29.380") : new(0, "Name        Argument\n--------------------\nwinget      https://cdn.winget.microsoft.com/cache\nwinget-font https://cdn.winget.microsoft.com/fonts"));
Check(await Discovery.DetectAsync(CancellationToken.None) == "WinGet v1.29.380 · Sources: winget, winget-font", "source names use fixed table columns even with a single-space separator");
Discovery.Runner = originalRunner;
Discovery.InvalidateAll(); WindowsInventory.Provider = _ => Task.FromResult(windows);
var quick = await DiagnosticsService.RunAsync(false,root,root,CancellationToken.None);
var full = await DiagnosticsService.RunAsync(true,root,root,CancellationToken.None);
Check(quick.Any(x => x.State == "SKIPPED" && x.Area == "Full checks") && quick.Any(x => x.State == "PASS" && x.Area == "Configuration"), "Quick diagnostics execute real local checks and identify skipped checks");
Check(full.Any(x => x.Area == "Windows inventory" && x.State == "PASS") && full.Any(x => x.Area == "Installation reports"), "Full diagnostics add inventory and report checks using controlled providers");
var adapter = new CountingAdapter(); var reports = new List<InstallOutcome>();
await new InstallQueue(adapter,(_,_)=>Task.FromResult(true),(_,_)=>Task.FromResult<string?>("Inventory changed")).RunAsync([vlc], new Progress<(PackageItem,string)>(),(_,_)=>Task.FromResult("Stop"),(_,r)=>reports.Add(r),CancellationToken.None);
Check(adapter.Calls == 0 && reports.Count == 1 && !reports[0].Success, "queue safety gate blocks an installer after inventory changes");
var gate = new TaskCompletionSource<bool>(); adapter = new CountingAdapter();
var queue = new InstallQueue(adapter,(_,_)=>Task.FromResult(true), async (_,token)=>{ await gate.Task.WaitAsync(token); return null; });
var run = queue.RunAsync([vlc],new Progress<(PackageItem,string)>(),(_,_)=>Task.FromResult("Stop"),(_,_)=>{},CancellationToken.None);
await Task.Delay(80); Check(adapter.Calls == 0 && !run.IsCompleted, "queue can pause safely before an app"); gate.SetResult(true); await run;
Check(adapter.Calls == 1, "resuming a paused queue starts exactly one adapter call");
Check(LaunchCommand.Parse(["search", "Visual", "Studio"]).Search == "Visual Studio" && LaunchCommand.Parse(["install"]).Route == "install", "local launch commands route without installing anything");
Console.WriteLine($"FP2.4: {count} assertions passed. Package queries and installation adapters were simulated; local temporary files were used. No installer or OS registration ran.");
foreach (var command in LaunchCommand.Commands)
{
    var parsed = LaunchCommand.Parse([command.Name]);
    Check(parsed.Error is null && parsed.Route == command.Route && parsed.Refresh == command.Refresh, "Help command is implemented by the actual parser: tlbxm " + command.Name);
}
Check(LaunchCommand.Parse([]).Route == "home" && LaunchCommand.Parse(["search", "Visual Studio"]).Search == "Visual Studio" && LaunchCommand.Parse(["search"]).Error is not null && LaunchCommand.Parse(["uninstall"]).Error is not null, "bare launch and search are supported; missing query and invented uninstall command are rejected");
Check(SettingsRegistry.All.Single(x => x.Key == "Search delay").Section == "Advanced" && SettingsRegistry.Validate(new() { ["Search delay"] = "Patient" })["Search delay"] == "Patient", "search delay moves to Advanced without changing saved preference keys");
Check(MainWindow.IsModifierKey(Key.LeftCtrl) && MainWindow.IsModifierKey(Key.RightAlt) && !MainWindow.IsModifierKey(Key.K), "remap capture can distinguish unfinished modifier presses from complete chords");
Check(MainWindow.PreserveFocusedInput(Key.X, ModifierKeys.Control, true, true) && MainWindow.PreserveFocusedInput(Key.Left, ModifierKeys.Control | ModifierKeys.Shift, true, true) && MainWindow.PreserveFocusedInput(Key.Right, ModifierKeys.None, false, true), "cut, word selection and list arrows stay with focused controls");
Check(!MainWindow.PreserveFocusedInput(Key.H, ModifierKeys.Control, true, true) && !MainWindow.PreserveFocusedInput(Key.F5, ModifierKeys.None, true, true) && !MainWindow.PreserveFocusedInput(Key.O, ModifierKeys.Control | ModifierKeys.Shift, true, true), "navigation, refresh and a remapped chord work from a focused text field");
Check(!MainWindow.ShortcutAllowed(Key.Tab, ModifierKeys.Alt) && !MainWindow.ShortcutAllowed(Key.Delete, ModifierKeys.Control | ModifierKeys.Alt), "reserved Windows switching/security chords cannot be remapped");
Check(Discovery.CommandTimeout(["list"]).TotalSeconds == 75 && Discovery.CommandTimeout(["list", "--upgrade-available"]).TotalSeconds == 75 && Discovery.CommandTimeout(["search", "editor"]).TotalSeconds == 35, "only slow inventory reads receive the bounded 75-second allowance");
Discovery.InvalidateAll(); Discovery.Runner = new TestRunner(_ => new(-1, "WinGet timed out after 75 seconds while reading inventory. Retry when ready."));
var timedOut = await Discovery.InstalledAsync(CancellationToken.None);
Check(!timedOut.IsReliable && timedOut.Status.Contains("75 seconds") && timedOut.Status.Contains("timed out") && Discovery.CacheCounts.QueryEntries == 0, "inventory timeout reason is preserved and never cached as reliable inventory");
Discovery.Runner = new TestRunner(_ => new(-1, "network unavailable"));
Check((await Discovery.InstalledAsync(CancellationToken.None)).Status.Contains("network connection error"), "network failure is distinguished from the app timeout");
Discovery.Runner = originalRunner; Discovery.InvalidateAll();

var clearRoot = Path.Combine(root, "clear-boundaries"); Directory.CreateDirectory(clearRoot);
void Fixture(string relative, string text) => ToolboxPaths.AtomicWrite(Path.Combine(clearRoot, relative), text);
Fixture("settings-v1.json", "settings-before"); Fixture("session-v1.json", "selection-before"); Fixture("curated-v1.json", "cache-before"); Fixture("last-report.txt", Path.Combine(root, "outside-report.txt"));
Fixture("reports/Toolbox-Install-2026-01-01-120000.txt", "Toolbox install report");
Fixture("reports/Archive/Toolbox-Simulation-2026-01-01-120000-1.txt", "Toolbox archived report");
Fixture("reports/personal.txt", "personal"); Fixture("reports/Toolbox-personal-notes.txt", "unrelated");
Fixture("logs/Toolbox-Crash-20260101-120000.txt", "Toolbox crash report");
Fixture("backups/Toolbox-Backup.tlbxbackup", "backup"); Fixture("recovery/earlier/settings-v1.json", "earlier recovery");
Fixture("history/original.txt", "history"); Fixture("launcher/tlbxm.cmd", "existing launcher"); Fixture("ToolboxMatrix.exe", "binary sentinel");
ToolboxPaths.AtomicWrite(Path.Combine(root, "outside-report.txt"), "outside report stays");
var cachePlan = ToolboxDataMaintenance.Prepare(clearRoot, false);
Check(cachePlan.Entries.Select(x => x.Relative).SequenceEqual(["curated-v1.json"]), "Clear Cache selects only Toolbox's on-disk catalogue cache");
var cacheRecovery = ToolboxDataMaintenance.Execute(cachePlan)!;
Check(!File.Exists(Path.Combine(clearRoot, "curated-v1.json")) && File.ReadAllText(Path.Combine(clearRoot, "settings-v1.json")) == "settings-before" && File.ReadAllText(Path.Combine(clearRoot, "session-v1.json")) == "selection-before" && File.Exists(Path.Combine(clearRoot, "reports/Toolbox-Install-2026-01-01-120000.txt")), "Clear Cache preserves settings, selection and reports");
Check(File.ReadAllText(Path.Combine(cacheRecovery, "curated-v1.json")) == "cache-before", "cache recovery preserves the exact original bytes");
Fixture("curated-v1.json", "fresh cache");
var reset = ToolboxDataMaintenance.Prepare(clearRoot, true);
Check(reset.Entries.Length == 7 && reset.Entries.All(x => !x.Relative.StartsWith("launcher/") && !x.Relative.StartsWith("history/") && !x.Relative.StartsWith("recovery/") && !x.Relative.StartsWith("backups/")), "Clear Data has exactly the expected private inputs, caches and owned reports");
var before = reset.Entries.ToDictionary(x => x.Relative, x => File.ReadAllText(Path.Combine(clearRoot, x.Relative)));
try { ToolboxDataMaintenance.Execute(reset, n => { if (n == 3) throw new IOException("injected write failure"); }); throw new Exception("Expected rollback failure"); }
catch (IOException) { }
Check(before.All(x => File.ReadAllText(Path.Combine(clearRoot, x.Key)) == x.Value) && !File.Exists(Path.Combine(clearRoot, "data-reset-pending.json")), "failure after partial removal rolls back every original file and clears the journal");
Fixture("settings-v1.json", "changed while confirmation was open");
try { ToolboxDataMaintenance.Execute(reset); throw new Exception("Stale plan accepted"); } catch (IOException) { }
Check(File.Exists(Path.Combine(clearRoot, "curated-v1.json")) && File.ReadAllText(Path.Combine(clearRoot, "settings-v1.json")) == "changed while confirmation was open", "changed data invalidates the review plan before deletion");
reset = ToolboxDataMaintenance.Prepare(clearRoot, true);
var dataRecovery = ToolboxDataMaintenance.Execute(reset)!;
Check(reset.Entries.All(x => !File.Exists(Path.Combine(clearRoot, x.Relative))) && Directory.Exists(Path.Combine(clearRoot, "reports/Archive")) && Directory.Exists(Path.Combine(clearRoot, "logs")), "successful Clear Data removes owned files and retains report/archive/log directories");
Check(new[] { "reports/personal.txt", "reports/Toolbox-personal-notes.txt", "backups/Toolbox-Backup.tlbxbackup", "recovery/earlier/settings-v1.json", "history/original.txt", "launcher/tlbxm.cmd", "ToolboxMatrix.exe" }.All(x => File.Exists(Path.Combine(clearRoot, x))) && File.ReadAllText(Path.Combine(root, "outside-report.txt")) == "outside report stays", "personal files, external reports, backups, history, binaries and launcher survive reset");
// Simulate process interruption after removals with a fully verified recovery journal.
ToolboxPaths.AtomicWrite(Path.Combine(clearRoot, "data-reset-pending.json"), JsonSerializer.Serialize(new { Snapshot = Path.GetRelativePath(clearRoot, dataRecovery).Replace('\\', '/'), AllData = true, Entries = reset.Entries }));
Check(ToolboxDataMaintenance.RecoverInterrupted(clearRoot) && reset.Entries.All(x => File.Exists(Path.Combine(clearRoot, x.Relative))) && !ToolboxDataMaintenance.RecoverInterrupted(clearRoot), "next-start recovery restores interrupted reset once and is idempotent");
var malicious = new ToolboxDataMaintenance.Plan(clearRoot, true, [new("../outside-report.txt", "fake")]);
try { ToolboxDataMaintenance.Execute(malicious); throw new Exception("Unsafe plan accepted"); } catch (IOException) { }
Check(File.ReadAllText(Path.Combine(root, "outside-report.txt")) == "outside report stays", "forged out-of-root cleanup plan cannot remove external files");
Console.WriteLine($"FP2.4 cohesive update: {count} assertions passed. Isolated file fixtures only; no real installer or OS registration ran.");
WindowsInventory.Provider = null;
// Retain fixture files in this named temporary directory for investigation; no user files are deleted.
sealed class TestRunner(Func<string[], CommandResult> response) : IWinGetRunner { public Task<CommandResult> RunAsync(string[] args, CancellationToken token) => Task.FromResult(response(args)); }
sealed class CountingAdapter : IInstallAdapter { public int Calls; public Task<InstallOutcome> InstallAsync(PackageItem item,IProgress<string> progress,CancellationToken token) { Calls++; return Task.FromResult(new InstallOutcome(true,false,"Simulated","No installer ran")); } }
