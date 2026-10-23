namespace WinGetToolbox;

internal static class NavigationRules
{
    internal static string? SelectedButton(string route) => route.Split(':')[0] switch
    {
        "home" => "HomeNav",
        "collections" or "collection" => "CollectionsNav",
        "installed" => "InstalledNav",
        "install" => "InstallNav",
        "backup" => "BackupNav",
        "diagnostics" => "DiagnosticsNav",
        "settings" => "SettingsNav",
        "about" => "AboutNav",
        "help" => "HelpNav",
        _ => null
    };
}
