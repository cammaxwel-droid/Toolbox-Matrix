using System;
using System.Collections.Generic;
using System.Linq;

namespace ToolboxMatrix;

internal sealed record LaunchCommand(string Route, string Search, bool Refresh, string? Error = null, bool UpdatesOnly = false)
{
    internal static readonly (string Name, string Route, bool Refresh, string Effect)[] Commands =
    [
        ("apps", "installed", false, "Open Installed Apps."),
        ("collections", "collections", false, "Browse collections."),
        ("updates", "installed", true, "Refresh inventory and show available updates; no updates run."),
        ("settings", "settings", false, "Open Settings."),
        ("diagnostics", "diagnostics", false, "Open Diagnostics; checks start when you choose one."),
        ("backup", "backup", false, "Open Backup / Restore; no files are exported or imported automatically."),
        ("refresh", "home", true, "Open Home and refresh read-only inventory."),
        ("help", "help", false, "Open Help and Resources."),
        ("about", "about", false, "Open About."),
        ("install", "install", false, "Open Install Centre; nothing installs automatically.")
    ];
    internal static IEnumerable<string> HelpLines => new[]
    {
        "tlbxm · Open your saved start page.",
        "tlbxm search <name> · Search packages. Example: tlbxm search visual studio"
    }.Concat(Commands.Select(command => "tlbxm " + command.Name + " · " + command.Effect));
    internal static string Usage => "Use: tlbxm, tlbxm search <name>, " + string.Join(", ", Commands.Select(command => "tlbxm " + command.Name)) + ".";

    internal static LaunchCommand Parse(string[] arguments, string startPage = "Home")
    {
        if (arguments.Length == 0) return new(startPage switch
        { "Collections" => "collections", "Installed Apps" => "installed", "Install Centre" => "install", _ => "home" }, "", false);
        var command = arguments[0].ToLowerInvariant();
        if (command == "search") return arguments.Skip(1).Any(x => !string.IsNullOrWhiteSpace(x))
            ? new("home", string.Join(' ', arguments.Skip(1)).Trim(), false)
            : new("home", "", false, "Search needs a package name.");
        var entry = Commands.FirstOrDefault(x => x.Name == command);
        if (entry.Name is null) return new("home", "", false, "Unknown command: " + arguments[0]);
        if (arguments.Length != 1) return new("home", "", false, "The " + entry.Name + " command does not accept extra arguments.");
        return new(entry.Route, "", entry.Refresh, UpdatesOnly: command == "updates");
    }
}
