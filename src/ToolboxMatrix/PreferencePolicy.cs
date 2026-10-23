using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ToolboxMatrix;

internal sealed class SavedPreferences
{
    public int Version { get; set; } = 1;
    public Dictionary<string, string> Values { get; set; } = new();
    public Dictionary<string, string> Shortcuts { get; set; } = new();
    public string? CustomReportsFolder { get; set; }
    public string? CustomBackupsFolder { get; set; }
    public string CustomAccent { get; set; } = "#7F298F";
    public string CustomTileColour { get; set; } = "#7F298F";
    public string CustomSubheadingsColour { get; set; } = "#7F298F";
    // Deliberately excluded from portable backups. Permission belongs to this directory.
    public Dictionary<string, RetentionConsent> RetentionConsents { get; set; } = new();
}

internal static class PreferencePolicy
{
    internal static Dictionary<string, string> Migrate(Dictionary<string, string>? values, int version, bool portable = false)
    {
        var result = SettingsRegistry.Validate(values);
        // Earlier versions only archived reports; that choice is not permission to delete.
        if (version < 6 || portable)
            result["Report retention"] = result["Backup retention"] = "Keep all";
        return result;
    }

    internal static Dictionary<string, string> Preview(Dictionary<string, string> saved, Dictionary<string, string> draft, bool appearance)
    {
        var result = new Dictionary<string, string>(draft);
        if (!appearance)
            foreach (var setting in SettingsRegistry.All.Where(x => x.Section == "Appearance"))
                result[setting.Key] = saved[setting.Key];
        return result;
    }
}

internal static class ShortcutCatalog
{
    internal static Dictionary<string, (string Label, Key Key, ModifierKeys Modifiers)> Defaults() => new()
    {
        ["toolbox.search.focus"] = ("Focus search", Key.Z, ModifierKeys.Alt),
        ["toolbox.go.back"] = ("Back", Key.Left, ModifierKeys.None),
        ["toolbox.go.forward"] = ("Forward", Key.Right, ModifierKeys.None),
        ["toolbox.go.about"] = ("About", Key.F1, ModifierKeys.None),
        ["toolbox.go.backup"] = ("Backup / Restore", Key.B, ModifierKeys.Control),
        ["toolbox.go.collections"] = ("Collections", Key.D1, ModifierKeys.Control),
        ["toolbox.go.diagnostics"] = ("Diagnostics", Key.D2, ModifierKeys.Control),
        ["toolbox.go.home"] = ("Home", Key.Space, ModifierKeys.Shift),
        ["toolbox.go.install"] = ("Install Centre", Key.D3, ModifierKeys.Control),
        ["toolbox.go.installed"] = ("Installed Apps", Key.D4, ModifierKeys.Control),
        ["toolbox.go.settings"] = ("Settings", Key.A, ModifierKeys.Control),
        ["toolbox.refresh.all"] = ("Refresh all data", Key.F5, ModifierKeys.None)
    };

    internal static bool TryParse(string text, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None; modifiers = ModifierKeys.None;
        var parts = text.Split('|');
        return parts.Length == 2 && Enum.TryParse(parts[0], out key) && Enum.TryParse(parts[1], out modifiers)
            && (key == Key.None && modifiers == ModifierKeys.None || MainWindow.ShortcutAllowed(key, modifiers));
    }

    internal static Dictionary<string, (string Label, Key Key, ModifierKeys Modifiers)> Load(Dictionary<string, string> stored, out string warning)
    {
        var defaults = Defaults();
        var result = defaults.ToDictionary(x => x.Key, x => (x.Value.Label, Key: Key.None, Modifiers: ModifierKeys.None));
        var used = new HashSet<(Key, ModifierKeys)>();
        var conflicts = new List<string>();
        // Valid stored mappings get priority over new defaults. Never reset the whole map.
        foreach (var pair in stored)
        {
            if (!defaults.ContainsKey(pair.Key) || !TryParse(pair.Value, out var key, out var mods)) continue;
            if (key != Key.None && !used.Add((key, mods))) { conflicts.Add(defaults[pair.Key].Label); continue; }
            result[pair.Key] = (defaults[pair.Key].Label, key, mods);
        }
        foreach (var pair in defaults.Where(x => !stored.ContainsKey(x.Key) || !TryParse(stored[x.Key], out _, out _)))
        {
            if (used.Add((pair.Value.Key, pair.Value.Modifiers))) result[pair.Key] = pair.Value;
            else conflicts.Add(pair.Value.Label);
        }
        warning = conflicts.Count == 0 ? "" : "Shortcut conflicts need review in Keyboard: " + string.Join(", ", conflicts) + ". Other saved mappings were preserved.";
        return result;
    }

    internal static string Format(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None) return "Unassigned";
        var name = key is >= Key.D0 and <= Key.D9 ? ((int)key - (int)Key.D0).ToString()
            : key is >= Key.NumPad0 and <= Key.NumPad9 ? "NumPad " + ((int)key - (int)Key.NumPad0) : key.ToString();
        return (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") +
            (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") +
            (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") +
            (modifiers.HasFlag(ModifierKeys.Windows) ? "Win+" : "") + name;
    }
}
