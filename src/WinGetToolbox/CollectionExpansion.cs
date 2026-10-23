using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace WinGetToolbox;

internal sealed record CuratedTool(string Name, string Description, string Executable, string VerifiedSha256);
internal sealed record CuratedSite(string Name, string Address, bool AllowHttp = false);
internal static class CuratedExtras
{
    // Source registry, deliberately empty. Add entries only after Cam approves their
    // identity, destination and (for a local tool) the exact verified executable hash.
    internal static IReadOnlyList<CuratedTool> Tools { get; } = Array.Empty<CuratedTool>();
    internal static IReadOnlyList<CuratedSite> Sites { get; } = Array.Empty<CuratedSite>();
    internal static bool IsExtra(string key) => key is "tools" or "sites";
    internal static bool TrySiteUri(string address, bool allowHttp, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace) || address.Any(char.IsControl) ||
            !Uri.TryCreate(address, UriKind.Absolute, out var parsed) || parsed.UserInfo.Length > 0 ||
            parsed.HostNameType == UriHostNameType.Unknown || parsed.Host.Length == 0 ||
            !(parsed.Scheme == Uri.UriSchemeHttps || allowHttp && parsed.Scheme == Uri.UriSchemeHttp)) return false;
        uri = parsed; return true;
    }
    internal static void VerifyTool(CuratedTool tool)
    {
        var path = SafeDestination.Canonical(tool.Executable);
        SafeDestination.CheckAncestors(path);
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) ||
            !System.Text.RegularExpressions.Regex.IsMatch(tool.VerifiedSha256, "^[a-fA-F0-9]{64}$"))
            throw new IOException("The approved local tool is unavailable or has no verified identity.");
        using var file = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(tool.VerifiedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The local tool changed since verification. Review it before opening.");
    }
}

public partial class MainWindow
{
    private void RenderExtraCollection(string key, string query)
    {
        var name = key == "tools" ? "Tools" : "Sites";
        var count = key == "tools" ? CuratedExtras.Tools.Count : CuratedExtras.Sites.Count;
        if (count == 0)
        {
            var panel = PagePanel();
            panel.Children.Add(Note(key == "tools"
                ? "No tools have been curated yet. Approved Windows utilities will appear here."
                : "No sites have been curated yet. Approved website links will appear here with an Open Site action."));
            FooterNote.Text = name + " · ready for a carefully reviewed collection.";
            return;
        }
        // The existing view preference and tile artwork are shared. These entries never
        // become PackageItems and cannot enter the installation selection.
        if (key == "sites")
            foreach (var site in CuratedExtras.Sites.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                AddExtraEntry(site.Name, "Open Site", () =>
                {
                    if (!CuratedExtras.TrySiteUri(site.Address, site.AllowHttp, out var uri)) { ShowMock("This site needs a valid approved HTTPS address."); return; }
                    if (uri!.Scheme == Uri.UriSchemeHttp && !ConfirmLocal("Open an HTTP site", uri.AbsoluteUri + "\nThis approved link uses unencrypted HTTP. Open it in your browser?")) return;
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                });
        else
            foreach (var tool in CuratedExtras.Tools.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                AddExtraEntry(tool.Name, "Open Tool", () =>
                {
                    CuratedExtras.VerifyTool(tool);
                    Process.Start(new ProcessStartInfo(tool.Executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(tool.Executable) });
                });
        FooterNote.Text = name + " · " + count + " curated entries.";
    }

    private void AddExtraEntry(string name, string label, Action action)
    {
        if (_collectionListView)
        {
            TilesPanel.ItemWidth = TilesPanel.ItemHeight = double.NaN;
            var row = new DockPanel { Width = Math.Max(240, ContentScroll.ViewportWidth - 18), Background = Card, Margin = new Thickness(0, 0, 0, 4) };
            var open = ActionButton(label, action); DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
            row.Children.Add(Note(name)); TilesPanel.Children.Add(row);
        }
        else
        {
            var panel = new StackPanel { Width = 164 };
            panel.Children.Add(CreateTile(name, name.Length == 0 ? "?" : name[..1], null));
            panel.Children.Add(ActionButton(label, action)); TilesPanel.Children.Add(panel);
        }
    }
}
