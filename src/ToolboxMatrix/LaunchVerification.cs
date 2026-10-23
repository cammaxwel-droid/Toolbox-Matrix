using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ToolboxMatrix;

// Internal verification entry point. Runs before StartupUri can construct a window;
// it resolves a command without executing its refresh, navigation or install actions.
internal static class LaunchVerification
{
    internal static int Run(string[] arguments)
    {
        var rootIndex = Array.IndexOf(arguments, "--data-root");
        if (!arguments.Contains("--headless-verification") || !arguments.Contains("--verify-launch") ||
            rootIndex < 0 || rootIndex + 1 >= arguments.Length || !Path.IsPathFullyQualified(arguments[rootIndex + 1]) ||
            arguments[rootIndex + 1].StartsWith(@"\\")) return 3;
        var root = Path.GetFullPath(arguments[rootIndex + 1]);
        var normalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToolboxMatrix");
        if (SafeDestination.Within(root, normalRoot)) return 3;
        var startPage = "Home";
        var settingsPath = Path.Combine(root, "settings-v1.json");
        if (File.Exists(settingsPath))
        {
            var settings = JsonSerializer.Deserialize<SavedPreferences>(File.ReadAllText(settingsPath));
            if (settings?.Version is >= 1 and <= SettingsRegistry.Version)
                startPage = PreferencePolicy.Migrate(settings.Values, settings.Version)["Start page"];
        }
        var forwarded = ToolboxPaths.LaunchArgs();
        var parsed = LaunchCommand.Parse(forwarded, startPage);
        ToolboxPaths.AtomicWrite(Path.Combine(root, "launch-verification.json"), JsonSerializer.Serialize(new
        {
            Arguments = forwarded, parsed.Route, parsed.Search, parsed.Refresh, parsed.Error, parsed.UpdatesOnly,
            WorkingDirectory = Environment.CurrentDirectory, BaseDirectory = AppContext.BaseDirectory,
            Executable = Environment.ProcessPath, Version = MainWindow.ApplicationVersion,
            WindowCreated = System.Windows.Application.Current.MainWindow is not null,
            ActionsExecuted = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return parsed.Error is null ? 0 : 2;
    }
}
