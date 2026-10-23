using System;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace ToolboxMatrix;
public partial class MainWindow
{
    private static string SessionPath => Path.Combine(Path.GetDirectoryName(SettingsPath)!, "session-v1.json");
    private void UpdateSelectionBadge()
    {
        var count = _installSelection.Count(x => x.Operation == "Update"
            ? _installedData?.IsReliable == true && _upgradeData?.IsReliable == true && UpgradeFor(x) is not null && x.Publisher != "Unknown" && Discovery.SafeIdentity(x.Id, x.Source)
            : Eligible(x));
        InstallSelectionBadge.Text = count.ToString();
        System.Windows.Automation.AutomationProperties.SetName(InstallSelectionBadge, count + " selected eligible apps");
    }
    private void PersistSelection()
    {
        UpdateSelectionBadge();
        if (_captureMode) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!); File.WriteAllText(SessionPath + ".tmp", JsonSerializer.Serialize(_installSelection)); File.Move(SessionPath + ".tmp", SessionPath, true); }
        catch (Exception ex) { _selectionFeedback = "Selection remains in this session; could not save it: " + ex.Message; }
    }
    private void LoadSelection()
    {
        if (_captureMode) return;
        try
        {
            var last = Path.Combine(ToolboxPaths.Root, "last-report.txt");
            if (File.Exists(last)) { var value = File.ReadAllText(last); if (Path.GetExtension(value).Equals(".txt", StringComparison.OrdinalIgnoreCase) && File.Exists(value)) _lastReportPath = value; }
            if (!File.Exists(SessionPath) || new FileInfo(SessionPath).Length > 1024 * 1024) return;
            var items = JsonSerializer.Deserialize<PackageItem[]>(File.ReadAllText(SessionPath)) ?? [];
            _installSelection.AddRange(items.Where(x => Discovery.SafeIdentity(x.Id, x.Source) && x.Operation is "Install" or "Update").DistinctBy(x => (x.Id, x.Source)).Take(1000));
        }
        catch { _selectionFeedback = "Saved selection was unreadable; no installation was started."; }
    }
}
