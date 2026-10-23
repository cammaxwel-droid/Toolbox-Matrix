using System.Reflection;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToolboxMatrix;

internal static partial class Program
{
    private static int _checks, _skips;
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: " + message);
        _checks++; Console.WriteLine("PASS: " + message);
    }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        { Check(true, message); return; }
        throw new Exception("FAIL: " + message);
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, value);
    private static object? Call(object owner, string method, params object?[] args) =>
        owner.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.Invoke(owner, args);
    private static IEnumerable<T> Children<T>(DependencyObject owner) where T : DependencyObject
    {
        if (owner is T value) yield return value;
        foreach (var child in LogicalTreeHelper.GetChildren(owner).OfType<DependencyObject>())
            foreach (var match in Children<T>(child)) yield return match;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (!args.Contains("--headless-verification") || !args.Contains("--data-root"))
                throw new InvalidOperationException("Use --headless-verification --data-root <new isolated folder>. This test never shows windows.");
            var profile = ToolboxPaths.Root;
            if (args.Contains("--reload-restored")) return ReloadRestored(profile);
            if (args.Contains("--render-only"))
            {
                if (!File.Exists(Path.Combine(profile, "expected-restored.json")))
                    throw new InvalidOperationException("Render-only uses the already verified isolated restore profile.");
                // The product constructor supplies tooltip suppression. Do not load
                // App.xaml's StartupUri; MainWindow owns all page/layout resources.
                var app = new OffscreenApplication();
                RenderReview(args[Array.IndexOf(args, "--render-review") + 1]);
                return 0;
            }
            if (Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile).Any())
                throw new InvalidOperationException("The headless profile must be empty; refusing to touch existing settings.");
            Directory.CreateDirectory(profile);
            var fixture = Path.Combine(Path.GetTempPath(), "Toolbox-FP050-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            Console.WriteLine("Isolated retention fixtures: " + fixture);
            Destinations(fixture);
            Retention(fixture);
            Shortcuts();
            PreferencesAndBackup(fixture);
            Collections();
            HeadlessControls(profile);
            TargetedFixChecks(profile, fixture);
            Console.WriteLine($"FP0.5.0: {_checks} assertions passed; {_skips} environment-dependent checks skipped. No windows shown, desktop capture, input injection, real installers or registration. Fixtures retained.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Destinations(string root)
    {
        var custom = Path.Combine(root, "custom");
        var next = Path.Combine(root, "next"); Directory.CreateDirectory(custom); Directory.CreateDirectory(next);
        foreach (var kind in new[] { ExportKind.Report, ExportKind.Backup })
        {
            var calls = 0;
            string? Picker() { calls++; return calls == 1 ? custom : calls == 2 ? next : null; }
            Check(ExportDestinations.Resolve(kind, "Default", custom, root, Picker) == Path.Combine(root, kind == ExportKind.Report ? "tblxm Reports" : "tblxm Backups") && calls == 0, kind + ": Default ignores remembered custom destination");
            Check(ExportDestinations.Resolve(kind, "Custom Folder", custom, root, Picker) == custom && calls == 0, kind + ": Custom uses exactly the chosen folder");
            Check(ExportDestinations.Resolve(kind, "Ask Every Time", custom, root, Picker) == custom &&
                ExportDestinations.Resolve(kind, "Ask Every Time", custom, root, Picker) == next && calls == 2, kind + ": each execution requests a fresh folder");
            Check(ExportDestinations.Resolve(kind, "Ask Every Time", custom, root, Picker) is null && calls == 3, kind + ": Cancel returns no destination and cannot reuse custom/default");
            Check(ExportDestinations.DisplayFolder(kind, "Ask Every Time", custom, root) is null, kind + ": Ask mode has no misleading saved-folder path");
            Reject(() => ExportDestinations.Resolve(kind, "Custom Folder", Path.Combine(root, "missing"), root, Picker), kind + ": dangling custom folder does not fall back");
        }
        Reject(() => SafeDestination.Canonical(Path.Combine(root, "..", "outside")), "traversal destination refused");
        Reject(() => SafeDestination.Canonical(@"\\server\share\reports"), "UNC and device destinations fail closed");
        Reject(() => SafeDestination.Canonical("relative"), "relative destination refused");
        var now = DateTime.Now;
        var one = ReportPrivacy.SaveNew(custom, "Support", now, "first password=secret");
        var two = ReportPrivacy.SaveNew(custom, "Support", now, "second");
        Check(one != two && !File.ReadAllText(one).Contains("secret") && File.ReadAllText(two) == "second", "reports redact and preserve same-second files");
        Reject(() => ReportPrivacy.SaveNew(custom, "../escape", now, "bad"), "report kind cannot escape the destination");
        var backup = BackupService.Export(new BackupPayload { Preferences = new() { Values = SettingsRegistry.Defaults } });
        var first = ExportDestinations.SaveBackup(custom, backup, now);
        var second = ExportDestinations.SaveBackup(custom, backup, now);
        Check(first != second && BackupService.Import(File.ReadAllText(first)).SchemaVersion == 1, "backup saves are collision-safe and restorable");
    }

    private static void Retention(string root)
    {
        var folder = Path.Combine(root, "retention"); Directory.CreateDirectory(folder);
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        var now = DateTime.UtcNow;
        string FileAt(string relative, TimeSpan age)
        {
            var path = Path.Combine(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture");
            File.SetCreationTimeUtc(path, now - age); File.SetLastWriteTimeUtc(path, now - age); return path;
        }
        var old = FileAt("unrelated-name.anything", TimeSpan.FromHours(25));
        var exact = FileAt("at-24-hours.txt", TimeSpan.FromHours(24));
        var fresh = FileAt("not-yet.txt", TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        var copied = FileAt("recently-copied.txt", TimeSpan.FromDays(2)); File.SetCreationTimeUtc(copied, now);
        var edited = FileAt("recently-edited.txt", TimeSpan.FromDays(2)); File.SetLastWriteTimeUtc(edited, now);
        var future = FileAt("future.txt", TimeSpan.FromHours(-1));
        var nested = FileAt(@"nested\old.txt", TimeSpan.FromDays(2));
        var executable = FileAt("application.exe", TimeSpan.FromDays(2));
        var windowsScript = FileAt("user-tool.vbs", TimeSpan.FromDays(2));
        var renamedExecutable = FileAt("application.dat", TimeSpan.FromDays(2)); File.WriteAllBytes(renamedExecutable, [77, 90, 0, 0]); File.SetLastWriteTimeUtc(renamedExecutable, now.AddDays(-2));
        var protectedFile = FileAt(@"history\checkpoint.txt", TimeSpan.FromDays(2));
        var gitFile = FileAt(@"source\original.txt", TimeSpan.FromDays(2)); Directory.CreateDirectory(Path.Combine(folder, "source", ".git"));
        var linkedTarget = Path.Combine(outside, "external.txt"); File.WriteAllText(linkedTarget, "preserve outside");
        File.SetCreationTimeUtc(linkedTarget, now.AddDays(-2)); File.SetLastWriteTimeUtc(linkedTarget, now.AddDays(-2));
        var hardlink = Path.Combine(folder, "hardlink.txt");
        var hardlinkMade = CreateHardLink(hardlink, linkedTarget, IntPtr.Zero);
        var linkMade = false;
        try { Directory.CreateSymbolicLink(Path.Combine(folder, "outside-link"), outside); linkMade = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _skips++; Console.WriteLine("UNVERIFIED: directory symbolic-link fixture unavailable: " + ex.Message); }
        var junction = Path.Combine(folder, "junction-to-outside");
        Check(SafeDestination.Within(Path.GetFullPath(junction), root) && SafeDestination.Within(Path.GetFullPath(outside), root), "junction fixture endpoints remain inside the isolated root");
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command",
            "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + junction.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' | Out-Null" }) psi.ArgumentList.Add(arg);
        using (var process = Process.Start(psi)!)
        {
            if (!process.WaitForExit(30000)) throw new IOException("Junction fixture creation has not finished; no retry was started.");
            if (process.ExitCode != 0) throw new IOException("Junction fixture creation failed: " + process.StandardError.ReadToEnd());
        }
        Check(File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint), "junction fixture is an actual Windows reparse point");
        Reject(() => FolderRetention.Consent(junction, true), "junction destination cannot receive cleanup consent");
        Reject(() => ExportDestinations.Resolve(ExportKind.Backup, "Custom Folder", junction, root, () => null), "backup export also rejects junction destinations");
        var consent = FolderRetention.Consent(folder, false);
        var unauthorized = FolderRetention.Clean(folder, "24 hours", false, null, now);
        Check(unauthorized.Deleted == 0 && File.Exists(old), "custom/shared retention requires exact-folder blanket-deletion consent");
        var first = FolderRetention.Clean(folder, "24 hours", false, consent, now, budget: TimeSpan.FromSeconds(10));
        Check(first.Deleted == 2 && !File.Exists(old) && !File.Exists(exact), "24-hour threshold deletes every eligible file type, including unrelated names, only after consent");
        Check(new[] { fresh, copied, edited, future, nested }.All(File.Exists), "fresh, newly copied, edited, future-dated and excluded nested files survive");
        Check(File.Exists(executable) && File.Exists(renamedExecutable) && File.Exists(protectedFile) && File.Exists(gitFile), "executables, executable headers, history and repositories survive");
        Check(File.Exists(windowsScript), "Windows script entry points survive retention even when aged");
        Check(Directory.Exists(folder) && Directory.Exists(Path.Combine(folder, "nested")), "configured parent and all directories survive retention");
        if (hardlinkMade) Check(File.Exists(hardlink) && File.ReadAllText(linkedTarget) == "preserve outside", "hardlinks cannot delete external data");
        else { _skips++; Console.WriteLine("UNVERIFIED: hardlink fixture unavailable."); }
        if (linkMade)
        {
            Check(File.Exists(linkedTarget), "external directory link target survives");
            Reject(() => FolderRetention.Consent(Path.Combine(folder, "outside-link"), true), "linked destination cannot receive retention consent");
            Reject(() => ExportDestinations.Resolve(ExportKind.Report, "Custom Folder", Path.Combine(folder, "outside-link"), root, () => null), "export also refuses a linked destination");
        }
        var recursiveConsent = FolderRetention.Consent(folder, true);
        Check(FolderRetention.Clean(folder, "24 hours", true, consent, now).Deleted == 0, "expanding into subfolders requires fresh consent");
        var recursive = FolderRetention.Clean(folder, "24 hours", true, recursiveConsent, now, budget: TimeSpan.FromSeconds(10));
        Check(recursive.Deleted == 1 && !File.Exists(nested) && Directory.Exists(Path.GetDirectoryName(nested)), "explicit recursive scope deletes aged nested files and keeps empty subfolders");
        Check(File.ReadAllText(linkedTarget) == "preserve outside" && Directory.Exists(junction), "recursive cleanup skips the actual junction and preserves its external target");
        Check(FolderRetention.Clean(folder, "30 days", true, recursiveConsent, now).Deleted == 0, "a changed age rule requires new consent");
        foreach (var kind in new[] { ExportKind.Report, ExportKind.Backup })
        {
            var dedicated = ExportDestinations.DefaultFolder(kind, root); Directory.CreateDirectory(dedicated);
            var aged = Path.Combine(dedicated, "aged.dat"); File.WriteAllText(aged, "fixture");
            File.SetCreationTimeUtc(aged, now.AddDays(-2)); File.SetLastWriteTimeUtc(aged, now.AddDays(-2));
            var cleanup = FolderRetention.Clean(dedicated, "24 hours", false, FolderRetention.Consent(dedicated, false), now);
            Check(cleanup.Deleted == 1 && Directory.Exists(dedicated) && !Directory.EnumerateFileSystemEntries(dedicated).Any(), kind + ": empty default parent survives cleanup");
        }
        var held = FileAt("in-use.txt", TimeSpan.FromDays(2));
        using (var locked = File.Open(held, FileMode.Open, FileAccess.Read, FileShare.None))
            Check(FolderRetention.Clean(folder, "24 hours", true, recursiveConsent, now).Skipped > 0 && File.Exists(held), "access/share failure preserves unavailable files");
        File.SetLastWriteTimeUtc(held, now);
        var budgetFolder = Path.Combine(root, "bounded"); Directory.CreateDirectory(budgetFolder);
        for (var i = 0; i < 6; i++)
        {
            var path = Path.Combine(budgetFolder, i + ".txt"); File.WriteAllText(path, "aged");
            File.SetCreationTimeUtc(path, now.AddDays(-2)); File.SetLastWriteTimeUtc(path, now.AddDays(-2));
        }
        var limited = FolderRetention.Clean(budgetFolder, "24 hours", false, FolderRetention.Consent(budgetFolder, false), now, maxDeletes: 2);
        Check(limited.Deleted == 2 && limited.Limited && Directory.GetFiles(budgetFolder).Length == 4, "cleanup deletion budget is bounded and reports remaining work");
        var oldConsent = FolderRetention.Consent(budgetFolder, false);
        Check(SafeDestination.Within(Path.GetFullPath(budgetFolder), root) &&
            SafeDestination.Within(Path.GetFullPath(budgetFolder + "-original"), root), "replacement fixture move stays inside the isolated test root");
        Directory.Move(budgetFolder, budgetFolder + "-original"); Directory.CreateDirectory(budgetFolder);
        File.WriteAllText(Path.Combine(budgetFolder, "new.txt"), "new directory");
        Check(!FolderRetention.Matches(oldConsent, budgetFolder, false) &&
            FolderRetention.Clean(budgetFolder, "24 hours", false, oldConsent, now).Deleted == 0, "replaced directory identity invalidates old permission");
        Check(FolderRetention.Clean(Path.Combine(root, "missing"), "24 hours", false, oldConsent, now).Deleted == 0, "dangling retention destination fails safely");
        Reject(() => FolderRetention.Consent(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), true), "Documents parent cannot be a retention target");
        Reject(() => FolderRetention.Consent(Environment.GetFolderPath(Environment.SpecialFolder.Windows), true), "Windows cannot be a retention target");
        Reject(() => FolderRetention.Consent(Directory.GetCurrentDirectory(), true), "canonical repository cannot be a retention target");
        var clock = new RetentionClock();
        Check(clock.Accept(now, TimeSpan.Zero) && clock.Accept(now.AddMinutes(1), TimeSpan.FromMinutes(1)), "normal UTC progression is accepted across date boundaries");
        Check(!clock.Accept(now.AddDays(1), TimeSpan.FromMinutes(2)) && !clock.Accept(now.AddMinutes(3), TimeSpan.FromMinutes(3)), "forward and backward wall-clock jumps pause cleanup");
        var midnight = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        Check(!FolderRetention.Eligible(midnight.AddHours(-23), midnight.AddHours(-23), midnight, TimeSpan.FromHours(24)) &&
            FolderRetention.Eligible(midnight.AddDays(-1), midnight.AddDays(-1), midnight, TimeSpan.FromHours(24)), "24-hour expiry uses elapsed UTC time, not calendar midnight");
    }

    private static void Shortcuts()
    {
        var defaults = ShortcutCatalog.Defaults();
        string[] expected = ["Z|Alt", "Left|None", "Right|None", "F1|None", "B|Control", "D1|Control", "D2|Control", "Space|Shift", "D3|Control", "D4|Control", "A|Control", "F5|None"];
        Check(defaults.Values.Select(x => $"{x.Key}|{x.Modifiers}").SequenceEqual(expected), "all twelve defaults match the approved 0.5.0 mappings");
        Check(defaults.Values.Select(x => (x.Key, x.Modifiers)).Distinct().Count() == 12, "default chords do not conflict");
        foreach (var chord in expected) Check(ShortcutCatalog.TryParse(chord, out _, out _), "parser accepts " + chord);
        Check(ShortcutCatalog.Format(Key.D2, ModifierKeys.Control) == "Ctrl+2" &&
            ShortcutCatalog.Format(Key.NumPad2, ModifierKeys.Control) == "Ctrl+NumPad 2", "top-row D2 and NumPad2 have distinct friendly names");
        Check(!ShortcutCatalog.TryParse("D200|Control", out _, out _) && !ShortcutCatalog.TryParse("F4|Alt", out _, out _), "fictitious/reserved chords are rejected");
        var stored = defaults.ToDictionary(x => x.Key, x => $"{x.Value.Key}|{x.Value.Modifiers}");
        stored["toolbox.go.settings"] = "O|Control, Shift";
        var migrated = ShortcutCatalog.Load(stored, out var warning);
        Check(migrated["toolbox.go.settings"].Key == Key.O && warning.Length == 0, "existing valid remapping survives migration");
        stored["toolbox.go.settings"] = "D2|Control";
        var conflict = ShortcutCatalog.Load(stored, out warning);
        Check(conflict["toolbox.go.backup"].Key == Key.B && warning.Contains("Settings") && conflict["toolbox.go.settings"].Key == Key.None, "a conflict does not reset unrelated user mappings");
        Check(MainWindow.PreserveFocusedInput(Key.A, ModifierKeys.Control, true, true) &&
            MainWindow.PreserveFocusedInput(Key.Left, ModifierKeys.None, true, true) &&
            MainWindow.PreserveFocusedInput(Key.Right, ModifierKeys.None, true, true), "Ctrl+A and arrows stay with text editors");
        Check(!MainWindow.PreserveFocusedInput(Key.Z, ModifierKeys.Alt, true, true) &&
            !MainWindow.PreserveFocusedInput(Key.D2, ModifierKeys.Control, true, true), "non-editing navigation chords remain available");
    }

    private static void PreferencesAndBackup(string root)
    {
        var saved = SettingsRegistry.Defaults;
        var draft = new Dictionary<string, string>(saved) { ["Settings spacing"] = "Comfortable", ["Theme"] = "Light" };
        var preview = PreferencePolicy.Preview(saved, draft, false);
        Check(preview["Settings spacing"] == "Comfortable" && preview["Theme"] == saved["Theme"] && saved["Settings spacing"] == "Compact", "non-Appearance draft previews immediately without mutating saved preferences");
        Check(PreferencePolicy.Preview(saved, draft, true)["Theme"] == "Light", "Appearance preview is explicit");
        Check(PreferencePolicy.Migrate(new() { ["Report retention"] = "30 days", ["Text size"] = "Large" }, 5)["Report retention"] == "Keep all", "legacy archive preference never becomes deletion consent");
        var payload = new BackupPayload { Applications = [new("Approved fixture", "Fixture.Tool", "winget", "1")],
            Preferences = new() { Values = draft, Shortcuts = new() { ["toolbox.go.home"] = "H|Control" }, CustomAccent = "#123456" } };
        var restored = BackupService.Import(BackupService.Export(payload));
        Check(restored.Applications.Single().Id == "Fixture.Tool" && restored.Preferences.Values["Settings spacing"] == "Comfortable" &&
            restored.Preferences.Shortcuts["toolbox.go.home"] == "H|Control", "backup roundtrip preserves applications, choices and user shortcuts");
        restored.Preferences.Values["Report retention"] = restored.Preferences.Values["Backup retention"] = "24 hours";
        Check(PreferencePolicy.Migrate(restored.Preferences.Values, 6, true)["Backup retention"] == "Keep all", "portable restore cannot activate destructive retention");
        foreach (var version in Enumerable.Range(1, 5))
        {
            payload.Preferences.Version = version;
            Check(BackupService.Import(BackupService.Export(payload)).Preferences.Version == version, "backup versions remain readable: " + version);
        }
    }

    private static void Collections()
    {
        Check(CuratedExtras.Tools.Count == 0 && CuratedExtras.Sites.Count == 0, "Tools and Sites have no invented/third-party inventory");
        Check(CuratedExtras.TrySiteUri("https://example.test/path", false, out _) &&
            !CuratedExtras.TrySiteUri("http://example.test", false, out _) &&
            CuratedExtras.TrySiteUri("http://example.test", true, out _), "HTTPS is allowed; HTTP requires explicit entry approval");
        foreach (var url in new[] { "javascript:alert(1)", "file:///C:/Windows", "https://user:password@example.test", "https://", "https://example.test/\nunsafe" })
            Check(!CuratedExtras.TrySiteUri(url, true, out _), "unsafe site URI rejected: " + url.Replace("\n", "\\n"));
    }

    private static void HeadlessControls(string profile)
    {
        var app = new App(); app.InitializeComponent();
        // Do not call Application.Run, Window.Show, ShowDialog, Focus, Activate or capture.
        var window = new MainWindow(); app.MainWindow = window;
        Check(!window.IsVisible && PresentationSource.FromVisual(window) is null, "real WPF window remains unshown with no presentation source");
        Set(window, "_route", "settings");
        var draft = Field<Dictionary<string, string>>(window, "_draft");
        draft["Settings spacing"] = "Comfortable";
        Set(window, "_settingsTab", "Spacing"); Call(window, "ApplySearch");
        Check(!File.Exists(Path.Combine(profile, "settings-v1.json")), "editing and rendering a draft writes no settings");
        var panel = (DependencyObject)window.FindName("TilesPanel");
        Check(!Children<Button>(panel).Any(x => x.Content?.ToString() == "Cancel Draft"), "non-Appearance Settings has no Cancel Draft");
        Check(Children<Button>(panel).First(x => x.Content?.ToString() == "Apply").Padding.Top == 9, "Comfortable spacing immediately previews actual control padding");
        foreach (var section in SettingsRegistry.All.Select(x => x.Section).Where(x => x != "Appearance").Append("Keyboard").Distinct())
        {
            Set(window, "_settingsTab", section); Call(window, "ApplySearch");
            Check(!Children<Button>(panel).Any(x => x.Content?.ToString() == "Cancel Draft"), section + ": no redundant Cancel Draft");
        }
        Check((bool)Call(window, "ApplyDraft")!, "Apply succeeds in the isolated profile");
        var path = Path.Combine(profile, "settings-v1.json");
        var savedBytes = File.ReadAllBytes(path);
        Check(JsonSerializer.Deserialize<SavedPreferences>(File.ReadAllText(path))!.Values["Settings spacing"] == "Comfortable", "Apply durably persists the changed setting");
        Field<Dictionary<string, string>>(window, "_draft")["Settings spacing"] = "Compact";
        Call(window, "ResetDraft");
        Check(Field<Dictionary<string, string>>(window, "_draft")["Settings spacing"] == "Comfortable" && savedBytes.SequenceEqual(File.ReadAllBytes(path)), "discard reverts preview and leaves saved bytes unchanged");
        var reload = new MainWindow(); app.MainWindow = reload;
        Check(Field<Dictionary<string, string>>(reload, "_saved")["Settings spacing"] == "Comfortable", "a new unshown window reloads the applied preference");
        Set(reload, "_route", "settings"); Set(reload, "_settingsTab", "Appearance"); Call(reload, "ApplySearch");
        panel = (DependencyObject)reload.FindName("TilesPanel");
        Check(Children<Button>(panel).Any(x => x.Content?.ToString() == "Preview Draft") &&
            Children<Button>(panel).Any(x => x.Content?.ToString() == "Cancel Draft"), "Appearance retains explicit Preview Draft and Cancel Draft");
        var originalBackground = ((SolidColorBrush)reload.Resources["WindowBackground"]).Color;
        Field<Dictionary<string, string>>(reload, "_draft")["Theme"] = "Light"; Call(reload, "RefreshVisualPreview");
        Check(((SolidColorBrush)reload.Resources["WindowBackground"]).Color == originalBackground, "Appearance choice alone does not activate its preview");
        Children<Button>(panel).Single(x => x.Content?.ToString() == "Preview Draft").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((SolidColorBrush)reload.Resources["WindowBackground"]).Color != originalBackground, "explicit Appearance preview changes resources without a visible window");
        Children<Button>(panel).Single(x => x.Content?.ToString() == "Cancel Draft").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((SolidColorBrush)reload.Resources["WindowBackground"]).Color == originalBackground &&
            savedBytes.SequenceEqual(File.ReadAllBytes(path)), "Appearance cancellation restores resources and preserves settings bytes");
        Set(reload, "_route", "collections"); Call(reload, "ApplySearch");
        Check(Children<Button>((DependencyObject)reload.FindName("TilesPanel")).Count() == 9, "two categories are added to the existing seven Collections");
        Set(reload, "_route", "home"); Call(reload, "ApplySearch");
        Check(Children<Button>((DependencyObject)reload.FindName("TilesPanel")).Count() == 7, "Home keeps its existing seven tiles");
        foreach (var route in new[] { "collection:tools", "collection:sites" })
        {
            Set(reload, "_route", route); Call(reload, "ApplySearch");
            panel = (DependencyObject)reload.FindName("TilesPanel");
            Check(!Children<Button>(panel).Any(x => x.Content?.ToString() is "Select" or "Add All") &&
                Children<TextBlock>(panel).Any(x => x.Text.Contains("curated yet")), route + ": functional empty state with no installation controls");
        }
        Set(reload, "_installedData", new QueryResult([], "fixture", true));
        Set(reload, "_windowsInventory", new WindowsInventorySnapshot([], true, true, "fixture", DateTime.UtcNow));
        Set(reload, "_route", "collections");
        var item = new PackageItem("Fixture app", "Fixture.App", "1", "", "winget", "Verified fixture");
        var row = reload.BuildPackageRow(item);
        var select = Children<Button>(row).Single(x => x.Content?.ToString() == "Select");
        var parentClicks = 0;
        row.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, _) => parentClicks++));
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Field<List<PackageItem>>(reload, "_installSelection").Count == 1 && parentClicks == 0 &&
            Field<string>(reload, "_route") == "collections", "direct Select is idempotent, handles its event and never navigates");
        Check(select.Content?.ToString() == "Selected ✓" && ((TextBlock)reload.FindName("InstallSelectionBadge")).Text == "1", "Select immediately updates its state and shared sidebar count");
        var openCount = 0;
        PackageSelectionActions.OpenCard(select, () => openCount++);
        PackageSelectionActions.OpenCard(row, () => openCount++);
        Check(openCount == 1, "button origin is excluded; clicking the rest of a card still invokes its action");
        var unknown = reload.BuildPackageRow(item with { Id = "Other.App", Publisher = "Unknown" });
        var blocked = Children<Button>(unknown).Single(); blocked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!blocked.IsEnabled && Field<List<PackageItem>>(reload, "_installSelection").Count == 1, "unknown publisher guard survives direct routed-event invocation");
        Set(reload, "_installedData", new QueryResult([item], "fixture", true));
        var installed = reload.BuildPackageRow(item);
        Check(!Children<Button>(installed).Single().IsEnabled, "installed app Select remains disabled");
        Check(MainWindow.ApplicationVersion == "0.5.0" && ((TextBlock)reload.FindName("AppVersionLabel")).Text == "v0.5.0", "assembly and sidebar display the actual 0.5.0 version");
        Check(!window.IsVisible && !reload.IsVisible && PresentationSource.FromVisual(reload) is null, "all WPF checks completed without showing or activating a window");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string target, IntPtr security);
}
