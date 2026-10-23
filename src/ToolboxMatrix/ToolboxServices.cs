using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ToolboxMatrix;

internal static class ToolboxPaths
{
    internal static string Root
    {
        get
        {
            var args = Environment.GetCommandLineArgs(); var i = Array.IndexOf(args, "--data-root");
            if (i >= 0 && i + 1 < args.Length && Path.IsPathFullyQualified(args[i + 1]) && !args[i + 1].StartsWith(@"\\")) return Path.GetFullPath(args[i + 1]);
            i = Array.IndexOf(args, "--capture-screenshots");
            if (i >= 0 && i + 1 < args.Length) return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[i + 1]))!, "_capture-profile");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToolboxMatrix");
        }
    }
    internal static string[] LaunchArgs()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray(); var result = new List<string>();
        for (var i = 0; i < args.Length; i++) { if (args[i] is "--data-root" or "--verify-live-appearance") { i++; continue; } if (args[i] is "--quiet-review" or "--headless-verification" or "--verify-launch") continue; result.Add(args[i]); }
        return result.ToArray();
    }
    internal static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, content, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
// A finite allowlist: no recursive deletion, custom destinations or integration files.
// Complete, verified copies and a journal precede removal. An interrupted reset rolls back.
internal static class ToolboxDataMaintenance
{
    internal sealed record Entry(string Relative, string Sha256);
    internal sealed record Plan(string Root, bool AllData, Entry[] Entries);
    private sealed record Journal(string Snapshot, bool AllData, Entry[] Entries);
    private const string Pending = "data-reset-pending.json";
    private static readonly string[] CacheFiles = ["curated-v1.json", "curated-v1.json.tmp"];
    private static readonly string[] PreferenceFiles = ["settings-v1.json", "settings-v1.json.tmp", "session-v1.json", "session-v1.json.tmp", "last-report.txt"];
    private static bool Allowed(string relative, bool allData)
    {
        if (CacheFiles.Contains(relative)) return true;
        if (!allData) return false;
        if (PreferenceFiles.Contains(relative)) return true;
        return Regex.IsMatch(relative, @"^reports/(?:Archive/)?Toolbox-(?:Install|Simulation|Support|Diagnostics)-\d{4}-\d{2}-\d{2}-\d{6}(?:-\d+)?\.txt$") ||
            Regex.IsMatch(relative, @"^logs/Toolbox-Crash-\d{8}-\d{6}\.txt$");
    }
    private static string SafePath(string root, string relative)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cleanup path is outside Toolbox data.");
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && File.GetAttributes(part).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Cleanup will not follow a linked file or folder: " + part);
        return path;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static Plan Prepare(string root, bool allData)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        SafePath(root, Pending);
        var names = new List<string>(CacheFiles);
        if (allData)
        {
            names.AddRange(PreferenceFiles);
            foreach (var directory in new[] { "reports", "reports/Archive", "logs" })
            {
                var folder = SafePath(root, directory);
                if (Directory.Exists(folder)) names.AddRange(Directory.EnumerateFiles(folder, "*.txt", SearchOption.TopDirectoryOnly)
                    .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/')).Where(file => Allowed(file, true)));
            }
        }
        return new(root, allData, names.Distinct().Select(name => (name, path: SafePath(root, name))).Where(x => File.Exists(x.path))
            .Select(x => new Entry(x.name, Hash(x.path))).ToArray());
    }
    internal static string? Execute(Plan plan, Action<int>? afterRemoval = null)
    {
        var journalPath = SafePath(plan.Root, Pending);
        if (File.Exists(journalPath)) throw new IOException("An earlier Toolbox reset needs recovery before another reset.");
        var current = Prepare(plan.Root, plan.AllData);
        if (!current.Entries.OrderBy(x => x.Relative).SequenceEqual(plan.Entries.OrderBy(x => x.Relative)))
            throw new IOException("Toolbox data changed during confirmation. Review Clear Data again.");
        if (plan.Entries.Length == 0) return null;
        var snapshot = "recovery/clear-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        foreach (var entry in plan.Entries)
        {
            if (!Allowed(entry.Relative, plan.AllData)) throw new InvalidDataException("Unsupported cleanup entry.");
            var original = SafePath(plan.Root, entry.Relative);
            var copy = SafePath(plan.Root, snapshot + "/" + entry.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(original, copy, false);
            if (Hash(copy) != entry.Sha256) throw new IOException("The recovery copy did not verify; original data is unchanged.");
        }
        ToolboxPaths.AtomicWrite(journalPath, JsonSerializer.Serialize(new Journal(snapshot, plan.AllData, plan.Entries)));
        try
        {
            var removed = 0;
            foreach (var entry in plan.Entries)
            {
                var original = SafePath(plan.Root, entry.Relative);
                if (Hash(original) != entry.Sha256) throw new IOException("Toolbox data changed before cleanup; restoring the prior data.");
                File.Delete(original);
                afterRemoval?.Invoke(++removed);
            }
            File.Delete(journalPath); // Commit: all approved files removed; directories remain.
            return SafePath(plan.Root, snapshot);
        }
        catch { RecoverInterrupted(plan.Root); throw; }
    }
    internal static bool RecoverInterrupted(string root)
    {
        var pending = SafePath(root, Pending);
        if (!File.Exists(pending)) return false;
        if (new FileInfo(pending).Length > 2 * 1024 * 1024) throw new InvalidDataException("Reset recovery journal is too large.");
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(pending)) ?? throw new InvalidDataException("Invalid reset recovery journal.");
        if (!Regex.IsMatch(journal.Snapshot ?? "", @"^recovery/clear-\d{8}-\d{6}-[a-f0-9]{32}$") || journal.Entries is null || journal.Entries.Length > 10000)
            throw new InvalidDataException("Invalid reset recovery location.");
        // Validate every source before restoring any file.
        foreach (var entry in journal.Entries)
        {
            if (!Allowed(entry.Relative, journal.AllData)) throw new InvalidDataException("Recovery entry is outside Toolbox's owned data.");
            SafePath(root, entry.Relative);
            if (Hash(SafePath(root, journal.Snapshot + "/" + entry.Relative)) != entry.Sha256) throw new InvalidDataException("Reset recovery copy failed verification.");
        }
        foreach (var entry in journal.Entries)
        {
            var original = SafePath(root, entry.Relative);
            if (!File.Exists(original))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Copy(SafePath(root, journal.Snapshot + "/" + entry.Relative), original, false);
            }
        }
        File.Delete(pending);
        return true;
    }
}
internal sealed record SettingDefinition(string Section, string Key, string[] Choices, string Help);
internal static class SettingsRegistry
{
    internal const int Version = 6;
    internal static string Label(string key) => key switch { "Theme" => "Background", "Accent" => "Accent Colour", _ => key };
    internal static readonly (string Name, string Hex)[] AccentPresets =
        [("Purple", "#7F298F"), ("Pink", "#B03D83"), ("Teal", "#80C4B8"), ("Blue", "#477ACB"), ("Amber", "#D6A64B"), ("Coral", "#D46B67")];
    internal static readonly SettingDefinition[] All =
    [
        new("Appearance", "Theme", ["Void Dark", "Light", "System"], "System follows the current Windows app theme. Void Dark keeps the established black canvas."),
        new("Appearance", "Accent", AccentPresets.Select(x => x.Name).Append("Custom").ToArray(), "Page headings, Workspace, Advanced and controls use your accent. Tile artwork follows it unless you choose a separate colour. Text is adjusted for readable contrast."),
        new("Appearance", "Tile Artwork Colour", ["Follow Accent", "Custom Colour"], "Follow Accent uses your accent for the oversized artwork. Custom Colour keeps a separate artwork colour; labels stay readable."),
        new("Appearance", "Subheadings Colour", AccentPresets.Select(x => x.Name).Append("Custom").ToArray(), "Independent colour for secondary headings, Preferences and the Install Centre selection badge. The refreshed timestamp always stays signature teal. Text is adjusted for readable contrast."),
        new("Appearance", "Opacity", ["100%", "95%", "90%"], "Window opacity. Lower values let the desktop show through."),
        new("Appearance", "Tile tint", ["10%", "8%", "16%"], "Blue tint behind the existing artwork and content cards."),
        new("Appearance", "Text size", ["Standard", "Large", "Extra Large"], "Scales the complete interface proportionally while preserving text hierarchy."),
        new("Spacing", "Settings spacing", ["Compact", "Comfortable"], "Compact uses tighter rows and controls. Comfortable adds room and package IDs."),
        new("Collections", "Collection view", ["Tiles", "Compact List"], "Compact List uses recycled rows and small bespoke icons."),
        new("Navigation", "Home on refresh", ["Off", "On"], "On returns to Home after a refresh. Off keeps your current page, search and scroll position."),
        new("Navigation", "Refresh on focus", ["On", "Off"], "Refresh read-only inventory when returning after the chosen interval."),
        new("Navigation", "Refresh interval", ["2 minutes", "5 minutes", "15 minutes"], "Minimum time between automatic foreground refreshes."),
        new("Navigation", "Arrow navigation", ["On", "Off"], "Bare Left/Right navigate only outside text and list controls."),
        new("Navigation", "Start page", ["Home", "Collections", "Installed Apps", "Install Centre"], "Used on a normal launch without a command."),
        new("Search", "Search mode", ["Online", "Cache only"], "Online queries configured WinGet sources. Cache only searches saved collections with their age shown."),
        new("Advanced", "Search delay", ["Standard", "Patient"], "Wait 350 or 700 milliseconds after typing to reduce repeated requests."),
        new("Search", "Search order", ["Source order", "Name"], "Sort results by source order or display name."),
        new("Installs", "Install review", ["Show every step", "Condensed overview"], "Both keep the mandatory final approval and exact package identities."),
        new("Updates", "Update display", ["Show all states", "Needs attention first"], "Place available updates first in the installed inventory."),
        new("Sources", "Source labels", ["Always show", "Show in details"], "Controls source labels in comfortable rows. Full identity remains available in right-click Properties."),
        new("Reports", "Report destination", ["Default", "Custom Folder", "Ask Every Time"], "Reports are local. Nothing is uploaded automatically."),
        new("Reports", "Report detail", ["Summary first", "Summary and detail"], "Technical details are redacted and expand only when requested."),
        new("Reports", "Report retention", ["Keep all", "24 hours", "30 days", "90 days"], ExportDestinations.ScopeNotice + ". Custom folders require explicit consent for all eligible contents."),
        new("Reports", "Report retention subfolders", ["Exclude", "Include"], "Choose whether retention also removes eligible files inside subfolders. No folders are deleted."),
        new("Backup", "Backup destination", ["Ask Every Time", "Default", "Custom Folder"], "A reinstall list plus portable Toolbox preferences, without personal files or app data."),
        new("Backup", "Backup retention", ["Keep all", "24 hours", "30 days", "90 days"], ExportDestinations.ScopeNotice + ". Custom folders require explicit consent for all eligible contents."),
        new("Backup", "Backup retention subfolders", ["Exclude", "Include"], "Choose whether retention also removes eligible files inside subfolders. No folders are deleted."),
        new("Advanced", "Tray icon", ["Off", "On"], "Optional app tray icon while Toolbox is running. It does not register startup."),
        new("Language", "Language", ["English"], "The interface language. English is currently available.")
    ];
    internal static Dictionary<string, string[]> Choices => All.ToDictionary(x => x.Key, x => x.Choices);
    internal static Dictionary<string, string> Defaults => All.ToDictionary(x => x.Key, x => x.Choices[0]);
    internal static Dictionary<string, string> Validate(Dictionary<string, string>? values)
    {
        var result = Defaults;
        if (values is null) return result;
        foreach (var definition in All) if (values.TryGetValue(definition.Key, out var value) && definition.Choices.Contains(value)) result[definition.Key] = value;
        return result;
    }
    internal static double Contrast(string first, string second)
    {
        double L(string hex) { var v = Convert.FromHexString(hex.TrimStart('#')); double C(byte c) { var x = c / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); } return .2126*C(v[0])+.7152*C(v[1])+.0722*C(v[2]); }
        var a = L(first); var b = L(second); return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
    }
}
internal sealed class PortablePreferences
{
    public int Version { get; set; } = SettingsRegistry.Version;
    public Dictionary<string, string> Values { get; set; } = new();
    public Dictionary<string, string> Shortcuts { get; set; } = new();
    public string CustomAccent { get; set; } = "#7F298F";
    public string CustomTileColour { get; set; } = "#7F298F";
    public string CustomSubheadingsColour { get; set; } = "#7F298F";
}
internal sealed record BackupApplication(string Name, string Id, string Source, string Version);
internal sealed class BackupPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string Product { get; set; } = "Toolbox Matrix";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public BackupApplication[] Applications { get; set; } = [];
    public PortablePreferences Preferences { get; set; } = new();
}
internal sealed class BackupEnvelope
{
    public string Format { get; set; } = "toolbox-matrix-backup";
    public string Payload { get; set; } = "";
    public string Sha256 { get; set; } = "";
}
internal static class BackupService
{
    private static readonly JsonSerializerOptions Strict = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    internal static string Export(BackupPayload payload)
    {
        Validate(payload);
        var json = JsonSerializer.Serialize(payload);
        return JsonSerializer.Serialize(new BackupEnvelope { Payload = json, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))) }, new JsonSerializerOptions { WriteIndented = true });
    }
    internal static BackupPayload Import(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 5 * 1024 * 1024) throw new InvalidDataException("Backup exceeds the 5 MB safety limit.");
        var envelope = JsonSerializer.Deserialize<BackupEnvelope>(json, Strict) ?? throw new InvalidDataException("Empty backup.");
        if (envelope.Format != "toolbox-matrix-backup" || envelope.Payload is null || envelope.Sha256 is null || !Regex.IsMatch(envelope.Sha256, "^[A-Fa-f0-9]{64}$")) throw new InvalidDataException("This is not a supported Toolbox backup.");
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload));
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(envelope.Sha256))) throw new InvalidDataException("The backup integrity check failed. No settings or packages were changed.");
        var payload = JsonSerializer.Deserialize<BackupPayload>(envelope.Payload, Strict) ?? throw new InvalidDataException("Empty backup data.");
        Validate(payload); return payload;
    }
    private static void Validate(BackupPayload payload)
    {
        if (payload.SchemaVersion != 1 || payload.Product != "Toolbox Matrix" || payload.Preferences is null || payload.Preferences.Version is < 1 or > SettingsRegistry.Version) throw new InvalidDataException("The backup schema or settings version is incompatible.");
        PreferenceRestore.Validate(payload.Preferences);
        if (payload.Applications is null || payload.Applications.Length > 1000 || payload.Preferences.Values is null || payload.Preferences.Shortcuts is null || payload.Preferences.Values.Count > 100 || payload.Preferences.Shortcuts.Count > 100) throw new InvalidDataException("Invalid backup structure or too many entries.");
        foreach (var item in payload.Applications)
        {
            if (item is null || item.Name is null || item.Id is null || item.Source is null || item.Version is null || !Discovery.SafeIdentity(item.Id, item.Source) || item.Name.Length is < 1 or > 200 || item.Name.Any(char.IsControl) || item.Version.Length > 100 || item.Version.Any(char.IsControl)) throw new InvalidDataException("Invalid package data. Imports cannot contain commands or paths.");
        }
        if (payload.Applications.GroupBy(x => x.Source.ToUpperInvariant() + "|" + x.Id.ToUpperInvariant()).Any(x => x.Count() > 1)) throw new InvalidDataException("Duplicate package identities in backup.");
        if (!Regex.IsMatch(payload.Preferences.CustomAccent ?? "", "^#[A-Fa-f0-9]{6}$")) throw new InvalidDataException("Invalid accent colour.");
        if (!Regex.IsMatch(payload.Preferences.CustomTileColour ?? "", "^#[A-Fa-f0-9]{6}$")) throw new InvalidDataException("Invalid tile artwork colour.");
        if (!Regex.IsMatch(payload.Preferences.CustomSubheadingsColour ?? "", "^#[A-Fa-f0-9]{6}$")) throw new InvalidDataException("Invalid subheadings colour.");
        foreach (var pair in payload.Preferences.Values.Concat(payload.Preferences.Shortcuts)) if (pair.Key.Length > 100 || pair.Value is null || pair.Value.Length > 200 || pair.Value.Any(char.IsControl)) throw new InvalidDataException("Invalid preference value.");
    }
}
internal static class ReportPrivacy
{
    internal static string Redact(string text)
    {
        text = Regex.Replace(text, @"(?i)(?:[A-Z]:\\Users\\|/Users/|/home/)[^\\/\s]+", "[user]");
        text = Regex.Replace(text, @"(?i)\b(?:Bearer\s+\S+|(?:token|password|secret|api[_ -]?key|authorization)\s*[:=]\s*[^\s,;]+)", "[credential redacted]");
        text = Regex.Replace(text, @"(?i)\b(?:sk-(?:proj-)?[A-Za-z0-9_-]{12,}|gh[pousr]_[A-Za-z0-9_]{12,})\b", "[credential redacted]");
        text = Regex.Replace(text, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", "[email redacted]");
        text = Regex.Replace(text, @"https?://[^\s<>]+", "[external URL redacted]");
        return text;
    }
    internal static string FileName(string kind, DateTime now) => $"Toolbox-{kind}-{now:yyyy-MM-dd-HHmmss}.txt";
    internal static string SaveNew(string folder, string kind, DateTime now, string content)
    {
        if (!Regex.IsMatch(kind, "^[A-Za-z]+$")) throw new InvalidDataException("Unsupported report kind.");
        folder = SafeDestination.Canonical(folder);
        SafeDestination.CheckAncestors(folder);
        Directory.CreateDirectory(folder);
        using var locked = SafeDestination.LockDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(FileName(kind, now));
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var path = Path.Combine(folder, name + (suffix == 0 ? "" : "-" + suffix) + ".txt");
            FileStream stream;
            try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { continue; }
            using (stream)
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(Redact(content));
            return path;
        }
        throw new IOException("Too many reports share this timestamp. Existing reports were preserved.");
    }
}
internal sealed record DiagnosticResult(string State, string Area, string Detail);
internal static class DiagnosticsService
{
    internal static async Task<IReadOnlyList<DiagnosticResult>> RunAsync(bool full, string root, string? reportFolder, CancellationToken token)
    {
        var results = new List<DiagnosticResult>();
        var detect = await Discovery.DetectAsync(token);
        results.Add(new(detect.StartsWith("WinGet v") && detect.Contains(" · Sources: ") && !detect.Contains("none available") ? "PASS" : "WARNING", "WinGet and sources", detect));
        var settings = Path.Combine(root, "settings-v1.json");
        try
        {
            if (!File.Exists(settings)) results.Add(new("NO DATA", "Configuration", "No saved preferences yet; defaults are in use."));
            else { using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(settings, token)); var version = doc.RootElement.GetProperty("Version").GetInt32(); results.Add(new(version is >= 1 and <= SettingsRegistry.Version ? "PASS" : "ERROR", "Configuration", "Saved settings version " + version + ".")); }
        }
        catch (Exception ex) { results.Add(new("ERROR", "Configuration", "Preferences cannot be read: " + ReportPrivacy.Redact(ex.Message))); }
        foreach (var file in new[] { "curated-v1.json", "session-v1.json" })
        {
            try { var path = Path.Combine(root, file); if (!File.Exists(path)) results.Add(new("NO DATA", file, "Nothing has been saved yet.")); else { using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, token)); results.Add(new("PASS", file, "Local JSON is readable.")); } }
            catch { results.Add(new("WARNING", file, "Damaged local data; Toolbox-only recovery can snapshot and reset it.")); }
        }
        if (reportFolder is null) results.Add(new("SKIPPED", "Report storage", "Ask Every Time has no fixed destination. Choose a folder when saving a report."));
        else try
        {
            SafeDestination.CheckAncestors(reportFolder);
            Directory.CreateDirectory(reportFolder); var probe = Path.Combine(reportFolder, ".toolbox-write-probe-" + Guid.NewGuid().ToString("N"));
            using var pin = SafeDestination.LockDirectory(reportFolder);
            try { await File.WriteAllTextAsync(probe, "permission check", token); } finally { if (File.Exists(probe)) File.Delete(probe); }
            results.Add(new("PASS", "Report storage", "The chosen local report directory is writable."));
            var drive = new DriveInfo(Path.GetPathRoot(reportFolder)!); results.Add(new(drive.AvailableFreeSpace > 100 * 1024 * 1024 ? "PASS" : "WARNING", "Storage", "Report drive has " + (drive.AvailableFreeSpace / 1024 / 1024) + " MB available. Installer requirements vary."));
        }
        catch (Exception ex) { results.Add(new("ERROR", "Report storage", ReportPrivacy.Redact(ex.Message))); }
        var installed = await Discovery.InstalledAsync(token);
        results.Add(new(installed.IsReliable ? installed.Items.Count == 0 ? "NO DATA" : "PASS" : "WARNING", "Package inventory", installed.Status));
        results.Add(new(File.Exists(Path.Combine(AppContext.BaseDirectory, "tlbxm.cmd")) ? "PASS" : "SKIPPED", "Local launcher", "Checks the optional launcher beside this executable; no registration is performed."));
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Toolbox Matrix.lnk");
        results.Add(new(File.Exists(shortcut) ? "PASS" : "SKIPPED", "Start Menu", File.Exists(shortcut) ? "Toolbox shortcut exists; its target can be reviewed in Windows." : "No Toolbox shortcut is registered."));
        if (full)
        {
            var windows = await WindowsInventory.ReadAsync(token);
            results.Add(new(windows.RegistryComplete && windows.PackagedComplete ? "PASS" : "WARNING", "Windows inventory", windows.Status));
            var exact = await Discovery.ExactAsync("Git.Git", token);
            results.Add(new(exact.Items.Any(x => x.Id == "Git.Git") ? "PASS" : "WARNING", "Source query", exact.Status));
            var reports = reportFolder is not null && Directory.Exists(reportFolder) ? Directory.GetFiles(reportFolder, "Toolbox-*.txt") : [];
            results.Add(new(reports.Length == 0 ? "NO DATA" : "PASS", "Installation reports", reports.Length == 0 ? "No Toolbox reports are saved here yet." : reports.Length + " local reports are available for review."));
        }
        else results.Add(new("SKIPPED", "Full checks", "Windows package inventory, source sample and report-history checks run in Full Check."));
        return results;
    }
    internal static string Snapshot(string root)
    {
        var folder = Path.Combine(root, "recovery", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(folder);
        foreach (var name in new[] { "settings-v1.json", "curated-v1.json", "session-v1.json" })
        { var path = Path.Combine(root, name); if (File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) File.Copy(path, Path.Combine(folder, name)); }
        return folder;
    }
}
