using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinGetToolbox;

internal static partial class Program
{
    private static bool HasDraft(MainWindow window) => (bool)typeof(MainWindow).GetProperty("HasDraft", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static string SettingsState(MainWindow window, string profile) => JsonSerializer.Serialize(new
    {
        Saved = Field<Dictionary<string, string>>(window, "_saved"), Draft = Field<Dictionary<string, string>>(window, "_draft"),
        Shortcuts = Field<Dictionary<string, (string, Key, ModifierKeys)>>(window, "_shortcuts").Select(x => x.Key + ":" + x.Value),
        DraftShortcuts = Field<Dictionary<string, (string, Key, ModifierKeys)>>(window, "_draftShortcuts").Select(x => x.Key + ":" + x.Value),
        Fields = new[] { "_customAccent", "_draftAccent", "_customTileColour", "_draftTileColour", "_customSubheadingsColour", "_draftSubheadingsColour",
            "_customReportsFolder", "_draftReportsFolder", "_customBackupsFolder", "_draftBackupsFolder" }.Select(x => Field<string?>(window, x)),
        Preview = Field<bool>(window, "_preview"), Consents = Field<Dictionary<string, RetentionConsent>>(window, "_retentionConsents"),
        Background = ((SolidColorBrush)window.Resources["WindowBackground"]).Color.ToString(),
        File = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(profile, "settings-v1.json"))))
    });

    private static void TargetedFixChecks(string profile, string fixture)
    {
        var settingsPath = Path.Combine(profile, "settings-v1.json");
        var reportFolder = Path.Combine(fixture, "machine reports"); Directory.CreateDirectory(reportFolder);
        var backupFolder = Path.Combine(fixture, "machine backups"); Directory.CreateDirectory(backupFolder);
        var current = new SavedPreferences
        {
            Version = 6, Values = SettingsRegistry.Defaults,
            CustomReportsFolder = reportFolder, CustomBackupsFolder = backupFolder,
            RetentionConsents = new() { ["Report"] = FolderRetention.Consent(reportFolder, true, "24 hours"), ["Backup"] = FolderRetention.Consent(backupFolder, false, "24 hours") }
        };
        current.Values["Report destination"] = "Custom Folder"; current.Values["Backup destination"] = "Default";
        current.Values["Report retention"] = current.Values["Backup retention"] = "24 hours";
        ToolboxPaths.AtomicWrite(settingsPath, JsonSerializer.Serialize(current));
        var window = new MainWindow();
        Set(window, "_captureMode", true); Set(window, "_route", "settings");
        Field<Dictionary<string, string>>(window, "_draft")["Theme"] = "Light";
        Field<Dictionary<string, string>>(window, "_draft")["Report destination"] = "Ask Every Time";
        Set(window, "_draftReportsFolder", Path.Combine(fixture, "unsaved folder"));
        Set(window, "_draftAccent", "#ABCDEF"); Set(window, "_preview", true); Call(window, "RefreshVisualPreview");
        var preferences = new PortablePreferences
        {
            Values = new(SettingsRegistry.Defaults) { ["Theme"] = "Light", ["Accent"] = "Custom", ["Tile Artwork Colour"] = "Custom Colour",
                ["Subheadings Colour"] = "Custom", ["Settings spacing"] = "Comfortable", ["Collection view"] = "Compact List",
                ["Start page"] = "Collections", ["Report destination"] = "Default", ["Backup destination"] = "Custom Folder",
                ["Report retention"] = "24 hours", ["Backup retention"] = "30 days", ["Report retention subfolders"] = "Include", ["Tray icon"] = "On" },
            CustomAccent = "#123456", CustomTileColour = "#F2BA49", CustomSubheadingsColour = "#477ACB",
            Shortcuts = new() { ["toolbox.go.home"] = "H|Control, Shift" }
        };
        var before = SettingsState(window, profile); var prompts = 0;
        Check(!window.RestorePreferences(preferences, (_, message) => { prompts++; return !message.Contains("unsaved settings edits"); }) && prompts == 1,
            "restore confirmation explicitly includes replacement of pending settings drafts");
        Check(before == SettingsState(window, profile), "cancelled restore preserves saved bytes, drafts, preview, shortcuts, folders and consent");
        var invalid = JsonSerializer.Deserialize<PortablePreferences>(JsonSerializer.Serialize(preferences))!;
        invalid.CustomAccent = "bad";
        Reject(() => window.RestorePreferences(invalid, (_, _) => throw new Exception("Validation must precede confirmation")), "invalid colour is rejected before confirmation");
        invalid.CustomAccent = "#123456"; invalid.Values["Theme"] = "invented";
        Reject(() => window.RestorePreferences(invalid, (_, _) => throw new Exception("Validation must precede confirmation")), "unsupported settings choice is rejected before confirmation");
        invalid.Values["Theme"] = "Light"; invalid.Shortcuts["toolbox.go.home"] = "F4|Alt";
        Reject(() => window.RestorePreferences(invalid, (_, _) => true), "reserved shortcut is rejected");
        invalid.Shortcuts["toolbox.go.home"] = "A|Control"; invalid.Shortcuts["toolbox.go.settings"] = "A|Control";
        Reject(() => window.RestorePreferences(invalid, (_, _) => true), "explicit duplicate shortcuts are rejected");
        Check(before == SettingsState(window, profile), "invalid restores leave all saved and draft state unchanged");
        using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Reject(() => window.RestorePreferences(preferences, (_, _) => true), "a failed settings-file replacement propagates without committing live state");
        Check(before == SettingsState(window, profile) && !Directory.EnumerateFiles(profile, "settings-v1.json.*.tmp").Any(), "failed save preserves prior settings and draft; temporary file is cleaned up");
        var selectionBefore = Field<List<PackageItem>>(window, "_installSelection").ToArray();
        Check(window.RestorePreferences(preferences, (_, _) => true), "confirmed valid restore commits without invoking Apply");
        var restored = JsonSerializer.Deserialize<SavedPreferences>(File.ReadAllText(settingsPath))!;
        Check(restored.Values["Theme"] == "Light" && restored.Values["Collection view"] == "Compact List" && restored.CustomAccent == "#123456" &&
            restored.CustomTileColour == "#F2BA49" && restored.CustomSubheadingsColour == "#477ACB", "appearance, layout and custom colours are durably restored");
        Check(!HasDraft(window) && !Field<bool>(window, "_preview") && Field<bool>(window, "_collectionListView"), "successful restore synchronises current UI and draft with saved settings");
        Check(((SolidColorBrush)window.Resources["WindowBackground"]).Color == Color.FromRgb(245, 245, 250), "restored Light theme is applied to live WPF resources immediately");
        Check(restored.Values["Report destination"] == "Custom Folder" && restored.Values["Backup destination"] == "Default" &&
            restored.CustomReportsFolder == reportFolder && restored.CustomBackupsFolder == backupFolder, "restore preserves this machine's saved folders and destination modes, excluding the pending folder draft");
        Check(restored.Values["Report retention"] == "Keep all" && restored.Values["Backup retention"] == "Keep all" && restored.RetentionConsents.Count == 0 &&
            restored.Values["Report retention subfolders"] == "Exclude" && restored.Values["Tray icon"] == "Off", "restore cannot import deletion consent, nested cleanup scope or activate the tray");
        Check(selectionBefore.SequenceEqual(Field<List<PackageItem>>(window, "_installSelection")) && !Field<bool>(window, "_installRunning"), "settings restore does not alter installation selection or start an installer");
        Check(Field<Dictionary<string, (string, Key, ModifierKeys)>>(window, "_shortcuts")["toolbox.go.home"].Item2 == Key.H,
            "restored shortcut mappings apply immediately");

        // Exercise the actual export handler with saved preferences and a conflicting draft.
        Field<Dictionary<string, string>>(window, "_draft")["Theme"] = "Void Dark";
        Set(window, "_draftAccent", "#ABCDEF");
        Set(window, "_installedData", new QueryResult([new("Fixture app", "Fixture.App", "1", "", "winget", "Fixture publisher")], "SIMULATED DATA", true));
        var exportBefore = SettingsState(window, profile);
        ((Task)Call(window, "ExportBackupAsync")!).GetAwaiter().GetResult();
        var backupPath = Field<string?>(window, "_lastBackupPath");
        Check(backupPath is not null && File.Exists(backupPath), "export handler writes a valid backup without applying or discarding a pending draft");
        Check(exportBefore == SettingsState(window, profile), "export is non-mutating for saved settings, draft, preview, shortcuts and deletion consent");
        var exported = BackupService.Import(File.ReadAllText(backupPath!));
        Check(exported.Preferences.Values["Theme"] == "Light" && exported.Preferences.CustomAccent == "#123456", "export contains saved preferences, not unsaved preview edits");
        var envelope = JsonSerializer.Deserialize<BackupEnvelope>(File.ReadAllText(backupPath!))!;
        envelope.Payload += " ";
        Reject(() => BackupService.Import(JsonSerializer.Serialize(envelope)), "corrupted backup checksum cannot reach restore");
        Check(exportBefore == SettingsState(window, profile), "rejected backup leaves saved settings and pending draft intact");

        foreach (var version in Enumerable.Range(1, SettingsRegistry.Version))
        {
            var legacy = new PortablePreferences { Version = version, Values = new() { ["Theme"] = "Light", ["Report retention"] = "30 days" } };
            var migrated = PreferenceRestore.Prepare(BackupService.Import(BackupService.Export(new() { Preferences = legacy })).Preferences, restored);
            Check(migrated.Values["Theme"] == "Light" && migrated.Values["Report retention"] == "Keep all" && migrated.Values["Tile Artwork Colour"] == "Follow Accent",
                "settings backup version " + version + " migrates missing fields without acquiring deletion permission");
        }
        var snapshot = Path.Combine(fixture, "snapshot.json");
        var savedSnapshot = new SavedPreferences
        {
            Version = 6, Values = new(preferences.Values), Shortcuts = new(preferences.Shortcuts),
            CustomAccent = preferences.CustomAccent, CustomTileColour = preferences.CustomTileColour,
            CustomSubheadingsColour = preferences.CustomSubheadingsColour,
            CustomReportsFolder = @"Z:\another-machine\reports", CustomBackupsFolder = @"Z:\another-machine\backups",
            RetentionConsents = new(current.RetentionConsents)
        };
        File.WriteAllText(snapshot, JsonSerializer.Serialize(savedSnapshot));
        Check(window.RestorePreferences(PreferenceRestore.ReadSnapshot(snapshot), (_, _) => true), "local recovery snapshot uses the same confirmed immediate commit path");
        restored = JsonSerializer.Deserialize<SavedPreferences>(File.ReadAllText(settingsPath))!;
        Check(restored.CustomReportsFolder == reportFolder && restored.CustomBackupsFolder == backupFolder && restored.RetentionConsents.Count == 0,
            "snapshot cannot reintroduce old machine paths or deletion consent");
        File.WriteAllText(snapshot, "{broken");
        try { PreferenceRestore.ReadSnapshot(snapshot); throw new Exception("Malformed snapshot was accepted"); }
        catch (JsonException) { Check(true, "malformed snapshot is safely rejected before confirmation or mutation"); }
        File.WriteAllText(Path.Combine(profile, "expected-restored.json"), File.ReadAllText(settingsPath));

        foreach (var command in LaunchCommand.Commands)
        {
            var parsed = LaunchCommand.Parse([command.Name], "Collections");
            Check(parsed.Error is null && parsed.Route == command.Route && parsed.Refresh == command.Refresh && parsed.UpdatesOnly == (command.Name == "updates"),
                "command resolves route/action without executing it: tlbxm " + command.Name);
            Check(LaunchCommand.Parse([command.Name.ToUpperInvariant()]).Route == command.Route &&
                LaunchCommand.Parse([command.Name, "unexpected"]).Error is not null, command.Name + ": casing accepted and extra arguments rejected");
        }
        foreach (var pair in new[] { ("Home", "home"), ("Collections", "collections"), ("Installed Apps", "installed"), ("Install Centre", "install") })
            Check(LaunchCommand.Parse([], pair.Item1).Route == pair.Item2, "bare tlbxm respects saved start page: " + pair.Item1);
        Check(LaunchCommand.Parse(["search", "Visual Studio", "Code"]).Search == "Visual Studio Code" && LaunchCommand.Parse(["search"]).Error is not null &&
            LaunchCommand.Parse(["search", " "]).Error is not null && LaunchCommand.Parse(["tblxm"]).Error is not null && LaunchCommand.Parse(["set"]).Error is not null,
            "search preserves its query and missing queries, misspellings and invented aliases are rejected");
        Set(window, "_route", "help"); Call(window, "Render");
        var helpText = Children<TextBlock>((DependencyObject)window.FindName("TilesPanel")).Select(x => x.Text).ToArray();
        Check(LaunchCommand.HelpLines.All(line => helpText.Contains(line)), "actual Help controls contain every command from the shared parser guide");
        VerifyIcon(window);
        Check(!window.IsVisible && PresentationSource.FromVisual(window) is null, "all targeted checks used an unshown WPF window with no presentation source");
    }

    private static int ReloadRestored(string profile)
    {
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow();
        var expected = JsonSerializer.Deserialize<SavedPreferences>(File.ReadAllText(Path.Combine(profile, "expected-restored.json")))!;
        Check(expected.Values.All(x => Field<Dictionary<string, string>>(window, "_saved").GetValueOrDefault(x.Key) == x.Value), "fresh process reloads every restored preference");
        Check(Field<string>(window, "_customAccent") == expected.CustomAccent && Field<string>(window, "_customTileColour") == expected.CustomTileColour &&
            Field<string>(window, "_customSubheadingsColour") == expected.CustomSubheadingsColour, "fresh process reloads all restored custom colours");
        Check(!HasDraft(window) && Field<Dictionary<string, RetentionConsent>>(window, "_retentionConsents").Count == 0, "fresh process has no pending Apply or imported deletion consent");
        Check(Field<Dictionary<string, (string, Key, ModifierKeys)>>(window, "_shortcuts")["toolbox.go.home"].Item2 == Key.H, "fresh process reloads restored keyboard mapping");
        Check(File.ReadAllText(Path.Combine(profile, "expected-restored.json")) == File.ReadAllText(Path.Combine(profile, "settings-v1.json")), "restart leaves the restored settings file unchanged");
        Check(!window.IsVisible && PresentationSource.FromVisual(window) is null, "restart persistence check creates no native window");
        Console.WriteLine($"Fresh-process restore: {_checks} assertions passed.");
        return 0;
    }
}
