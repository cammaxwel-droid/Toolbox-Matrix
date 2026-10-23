using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinGetToolbox;

internal sealed record WindowsApp(string Name, string Version, string Publisher, string PackageId = "", string Family = "");
internal sealed record WindowsInventorySnapshot(IReadOnlyList<WindowsApp> Apps, bool RegistryComplete, bool PackagedComplete, string Status, DateTime CheckedAt)
{
    internal static WindowsInventorySnapshot Empty => new([], false, false, "Windows inventory has not been read.", DateTime.MinValue);
}
internal sealed record InstalledMatch(string State, string Version, string Evidence);

internal static class InventoryMatcher
{
    private static string Normalize(string value) => Regex.Replace(Regex.Replace(value.ToLowerInvariant(), @"\b(v?\d[\w.\-]*|x64|x86|64-bit|32-bit)\b", ""), @"[^a-z0-9]", "");
    internal static InstalledMatch Resolve(PackageItem item, QueryResult? winget, WindowsInventorySnapshot windows)
    {
        var exact = winget?.Items.FirstOrDefault(x => x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(x.Source) || x.Source.Equals(item.Source, StringComparison.OrdinalIgnoreCase)));
        if (exact is not null) return new("Installed", exact.Version, "Exact WinGet inventory identity");
        var direct = windows.Apps.Where(x => x.PackageId.Equals(item.Id, StringComparison.OrdinalIgnoreCase) && x.PackageId.Length > 0).ToArray();
        if (direct.Length == 1) return new("Installed", direct[0].Version, "Exact Windows package identity");
        var name = Normalize(item.Name);
        var aliases = item.Id.ToLowerInvariant() switch
        {
            "videolan.vlc" => new[] { "vlc", "vlcmediaplayer" },
            "audacity.audacity" => new[] { "audacity" },
            "mozilla.firefox" => new[] { "mozillafirefox", "firefox" },
            _ => new[] { name }
        };
        var candidates = windows.Apps.Where(x => aliases.Contains(Normalize(x.Name))).ToArray();
        bool PublisherMatches(WindowsApp app)
        {
            var a = Normalize(app.Publisher); var b = Normalize(item.Publisher);
            if (a.Length < 3 || b.Length < 3 || b == "unknown") return false;
            if (a == b || (Math.Min(a.Length, b.Length) >= 6 && (a.Contains(b) || b.Contains(a)))) return true;
            return item.Id == "VideoLAN.VLC" && a.Contains("videolan") || item.Id == "Audacity.Audacity" && (a.Contains("audacity") || a.Contains("muse"));
        }
        var matched = candidates.Where(PublisherMatches).ToArray();
        if (matched.Length == 1) return new("Installed", matched[0].Version, "Windows name and publisher match");
        if (candidates.Length > 0 || direct.Length > 1) return new("Unknown", "", "A possible Windows match needs review; installation is blocked");
        // A loose name hit never marks an app installed, but prevents a risky automatic reinstall.
        if (name.Length >= 5 && windows.Apps.Any(x => Normalize(x.Name).Contains(name) || name.Contains(Normalize(x.Name)) && Normalize(x.Name).Length >= 5))
            return new("Unknown", "", "A similar Windows entry exists; inspect Properties before proceeding");
        if (winget?.IsReliable != true || !windows.RegistryComplete || !windows.PackagedComplete || !Discovery.SafeIdentity(item.Id, item.Source))
            return new("Unknown", "", "One or more inventory checks are unavailable");
        return new("Not Installed", "", "No match in the current WinGet and Windows inventories");
    }
}

internal static class WindowsInventory
{
    internal static Func<CancellationToken, Task<WindowsInventorySnapshot>>? Provider { get; set; }
    internal static Task<WindowsInventorySnapshot> ReadAsync(CancellationToken token) => Provider is null ? ReadRealAsync(token) : Provider(token);
    private static Task<WindowsInventorySnapshot> ReadRealAsync(CancellationToken token) => Task.Run(async () =>
    {
        var apps = new List<WindowsApp>(); var registryOk = true; var packagedOk = false;
        if (!OperatingSystem.IsWindows()) return WindowsInventorySnapshot.Empty;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false);
                if (uninstall is null) continue;
                foreach (var key in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(key, false);
                    string Read(string name) => entry?.GetValue(name) as string ?? "";
                    if (Read("DisplayName") is { Length: > 0 } name)
                        apps.Add(new(name, Read("DisplayVersion"), Read("Publisher"), Read("WinGetPackageIdentifier")));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { registryOk = false; }
        }
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(token); timed.CancelAfter(TimeSpan.FromSeconds(40));
        using var process = new Process { StartInfo = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; @(Get-AppxPackage | Select-Object Name,Version,Publisher,PackageFamilyName) | ConvertTo-Json -Compress" }) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timed.Token); var error = process.StandardError.ReadToEndAsync(timed.Token);
            await process.WaitForExitAsync(timed.Token);
            var json = await output; await error;
            if (process.ExitCode == 0)
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
                var entries = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : new[] { document.RootElement };
                foreach (var entry in entries)
                {
                    string Read(string name) => entry.TryGetProperty(name, out var value) ? value.ToString() : "";
                    apps.Add(new(Read("Name"), Read("Version"), Read("Publisher"), Family: Read("PackageFamilyName")));
                }
                packagedOk = true;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or JsonException)
        { try { if (!process.HasExited) process.Kill(); } catch { } }
        token.ThrowIfCancellationRequested();
        return new WindowsInventorySnapshot(apps.DistinctBy(x => (x.Name, x.Version, x.Publisher, x.Family)).ToArray(), registryOk, packagedOk,
            $"Uninstall records: {(registryOk ? "read" : "partially unavailable")}; packaged apps: {(packagedOk ? "read" : "unavailable")}. Read-only, current user scope.", DateTime.UtcNow);
    }, token);
}
