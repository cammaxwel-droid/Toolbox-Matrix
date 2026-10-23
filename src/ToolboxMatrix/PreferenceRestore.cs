using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Input;

namespace ToolboxMatrix;

// Restores are prepared without touching the current draft, disk or Windows integration.
internal static class PreferenceRestore
{
    internal static void Validate(PortablePreferences preferences)
    {
        if (preferences.Version is < 1 or > SettingsRegistry.Version || preferences.Values is null || preferences.Shortcuts is null ||
            preferences.Values.Count > 100 || preferences.Shortcuts.Count > 100)
            throw new InvalidDataException("The settings version or structure is unsupported.");
        foreach (var colour in new[] { preferences.CustomAccent, preferences.CustomTileColour, preferences.CustomSubheadingsColour })
            if (!Regex.IsMatch(colour ?? "", "^#[A-Fa-f0-9]{6}$")) throw new InvalidDataException("Invalid settings colour.");
        var choices = SettingsRegistry.Choices;
        foreach (var pair in preferences.Values.Concat(preferences.Shortcuts))
            if (pair.Key.Length > 100 || pair.Value is null || pair.Value.Length > 200 || pair.Value.Any(char.IsControl))
                throw new InvalidDataException("Invalid preference value.");
        foreach (var pair in preferences.Values)
            if (choices.TryGetValue(pair.Key, out var options) && !options.Contains(pair.Value))
                throw new InvalidDataException("Unsupported choice for " + SettingsRegistry.Label(pair.Key) + ".");
        var shortcuts = ShortcutCatalog.Defaults();
        var used = new HashSet<(Key, ModifierKeys)>();
        foreach (var pair in preferences.Shortcuts.Where(x => shortcuts.ContainsKey(x.Key)))
        {
            if (!ShortcutCatalog.TryParse(pair.Value, out var key, out var modifiers) ||
                key != Key.None && !used.Add((key, modifiers)))
                throw new InvalidDataException("Invalid or conflicting shortcut for " + shortcuts[pair.Key].Label + ".");
        }
    }

    internal static SavedPreferences Prepare(PortablePreferences preferences, SavedPreferences current)
    {
        Validate(preferences);
        var values = PreferencePolicy.Migrate(preferences.Values, preferences.Version, portable: true);
        // Neither a portable backup nor an old local snapshot grants deletion authority or
        // activates integration. Keep this machine's saved destinations, never a pending draft.
        values["Report destination"] = current.Values["Report destination"];
        values["Backup destination"] = current.Values["Backup destination"];
        values["Report retention subfolders"] = values["Backup retention subfolders"] = "Exclude";
        values["Tray icon"] = "Off";
        var shortcuts = ShortcutCatalog.Load(preferences.Shortcuts, out _);
        return new SavedPreferences
        {
            Version = SettingsRegistry.Version, Values = values,
            Shortcuts = shortcuts.ToDictionary(x => x.Key, x => $"{x.Value.Key}|{x.Value.Modifiers}"),
            CustomAccent = preferences.CustomAccent, CustomTileColour = preferences.CustomTileColour,
            CustomSubheadingsColour = preferences.CustomSubheadingsColour,
            CustomReportsFolder = current.CustomReportsFolder, CustomBackupsFolder = current.CustomBackupsFolder,
            RetentionConsents = new()
        };
    }

    internal static PortablePreferences ReadSnapshot(string path)
    {
        if (new FileInfo(path).Length > 5 * 1024 * 1024) throw new InvalidDataException("Settings snapshot exceeds the 5 MB limit.");
        var data = JsonSerializer.Deserialize<SavedPreferences>(File.ReadAllText(path), new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 })
            ?? throw new InvalidDataException("Empty settings snapshot.");
        var preferences = new PortablePreferences
        {
            Version = data.Version, Values = data.Values, Shortcuts = data.Shortcuts,
            CustomAccent = data.CustomAccent, CustomTileColour = data.CustomTileColour,
            CustomSubheadingsColour = data.CustomSubheadingsColour
        };
        Validate(preferences);
        return preferences;
    }
}
