using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinGetToolbox;

public partial class MainWindow
{
    private Dictionary<string, RetentionConsent> _retentionConsents = new();
    private readonly RetentionClock _retentionClock = new();
    private readonly Stopwatch _retentionElapsed = Stopwatch.StartNew();
    private DispatcherTimer? _retentionTimer;
    private bool _retentionRunning;
    private string _retentionStatus = "";
    private static string KindName(ExportKind kind) => kind == ExportKind.Report ? "Report" : "Backup";
    private static string DefaultBackupFolder => ExportDestinations.DefaultFolder(ExportKind.Backup, ToolboxPaths.Root);

    private string? ChooseExportFolder(ExportKind kind, Window? owner = null)
    {
        RequireInteractiveUi();
        var picker = new OpenFolderDialog { Title = "Choose where to save this Toolbox " + KindName(kind).ToLowerInvariant(), Multiselect = false };
        return picker.ShowDialog(owner ?? this) == true ? picker.FolderName : null;
    }

    private bool ConfirmRetention(ExportKind kind, string folder, bool nested, string rule)
    {
        var dialog = CreatePrototypeDialog("Enable " + KindName(kind).ToLowerInvariant() + " retention",
            folder + "\n\nThis permits permanent deletion of ALL eligible files aged " + rule + " or more in this exact folder, including unrelated personal files." +
            (nested ? " Eligible files in subfolders are included." : " Subfolder contents are excluded.") +
            "\n\nThe parent folder and all subfolders stay. Linked paths, executables, source repositories and protected system locations are skipped. There is no retention undo.", "Cancel");
        dialog.SizeToContent = SizeToContent.Height;
        var grid = (Grid)((Border)dialog.Content).Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var consent = new CheckBox { Content = new TextBlock { Text = "I authorise deletion of all eligible contents at the path above.", TextWrapping = TextWrapping.Wrap },
            Foreground = TextBrush, Margin = new Thickness(15, 4, 15, 12) };
        Grid.SetRow(consent, 2); grid.Children.Add(consent);
        foreach (var button in grid.Children.OfType<Button>()) Grid.SetRow(button, 3);
        var enable = ActionButton("Enable deletion", () => { dialog.Tag = true; dialog.Close(); });
        enable.IsEnabled = false; enable.Margin = new Thickness(15, 0, 0, 10);
        consent.Checked += (_, _) => enable.IsEnabled = true;
        consent.Unchecked += (_, _) => enable.IsEnabled = false;
        Grid.SetRow(enable, 3); grid.Children.Add(enable);
        dialog.ShowDialog();
        return dialog.Tag is true;
    }

    private Dictionary<string, RetentionConsent>? PrepareRetentionConsents()
    {
        var result = new Dictionary<string, RetentionConsent>(_retentionConsents);
        foreach (var kind in new[] { ExportKind.Report, ExportKind.Backup })
        {
            var name = KindName(kind);
            var mode = _draft[name + " destination"];
            if (FolderRetention.Age(_draft[name + " retention"]) is null || mode == "Ask Every Time") { result.Remove(name); continue; }
            var nested = _draft[name + " retention subfolders"] == "Include";
            var rule = _draft[name + " retention"];
            var custom = kind == ExportKind.Report ? _draftReportsFolder : _draftBackupsFolder;
            var folder = ExportDestinations.Resolve(kind, mode, custom, ToolboxPaths.Root, () => null)!;
            SafeDestination.CheckRetentionRoot(folder);
            if (mode == "Default") Directory.CreateDirectory(folder);
            if (!FolderRetention.Matches(result.GetValueOrDefault(name), folder, nested, rule))
            {
                // Capture identity before the modal; a replacement during confirmation is rejected.
                var candidate = FolderRetention.Consent(folder, nested, rule);
                if (mode == "Custom Folder" && !ConfirmRetention(kind, folder, nested, rule)) return null;
                if (!FolderRetention.Matches(candidate, folder, nested, rule)) throw new IOException("The folder changed during confirmation. Choose it again.");
                result[name] = candidate;
            }
        }
        return result;
    }

    private sealed record ExportTarget(ExportKind Kind, string Folder, string Retention, bool Nested, RetentionConsent? Consent);
    private ExportTarget? ResolveExportTarget(ExportKind kind, Window? owner = null, bool resolvePending = true)
    {
        if (resolvePending && HasDraft && !ResolvePending()) return null;
        var name = KindName(kind); var mode = _saved[name + " destination"];
        var custom = kind == ExportKind.Report ? _customReportsFolder : _customBackupsFolder;
        var folder = ExportDestinations.Resolve(kind, mode, custom, ToolboxPaths.Root, () => ChooseExportFolder(kind, owner));
        if (folder is null) return null;
        var nested = _saved[name + " retention subfolders"] == "Include";
        var retention = _saved[name + " retention"];
        var consent = _retentionConsents.GetValueOrDefault(name);
        if (FolderRetention.Age(retention) is not null)
        {
            SafeDestination.CheckRetentionRoot(folder);
            if (mode == "Default") Directory.CreateDirectory(folder);
            if (mode == "Ask Every Time" || !FolderRetention.Matches(consent, folder, nested, retention))
            {
                var candidate = FolderRetention.Consent(folder, nested, retention);
                if (mode != "Default" && !ConfirmRetention(kind, folder, nested, retention)) return null;
                if (!FolderRetention.Matches(candidate, folder, nested, retention)) throw new IOException("The destination changed during confirmation.");
                consent = candidate; // Ask Every Time permission applies to this execution only.
            }
        }
        return new(kind, folder, retention, nested, consent);
    }

    private async Task CleanExportTargetAsync(ExportTarget target)
    {
        if (FolderRetention.Age(target.Retention) is null) return;
        if (_retentionRunning) { _retentionStatus = "Retention is already running. The newly saved file is kept."; return; }
        var now = DateTime.UtcNow;
        if (!_retentionClock.Accept(now, _retentionElapsed.Elapsed))
        { _retentionStatus = "Retention is paused for this session after a clock change; files were preserved."; return; }
        _retentionRunning = true;
        try
        {
            var result = await Task.Run(() => FolderRetention.Clean(target.Folder, target.Retention, target.Nested, target.Consent, now));
            _retentionStatus = KindName(target.Kind) + ": " + result.Message;
        }
        catch (Exception ex) { _retentionStatus = "Retention stopped safely; the saved file is kept. " + ex.Message; }
        finally { _retentionRunning = false; }
    }

    private void StartRetentionTimer()
    {
        _retentionClock.Accept(DateTime.UtcNow, _retentionElapsed.Elapsed);
        // Runs only in a user-launched app. No startup deletion and no remembered Ask destination.
        _retentionTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _retentionTimer.Tick += async (_, _) =>
        {
            if (DataMaintenanceBusy || HasDraft) return;
            foreach (var kind in new[] { ExportKind.Report, ExportKind.Backup })
            {
                var name = KindName(kind);
                if (_saved[name + " destination"] == "Ask Every Time" || FolderRetention.Age(_saved[name + " retention"]) is null) continue;
                var folder = ExportDestinations.DisplayFolder(kind, _saved[name + " destination"], kind == ExportKind.Report ? _customReportsFolder : _customBackupsFolder, ToolboxPaths.Root);
                if (folder is not null)
                    await CleanExportTargetAsync(new(kind, folder, _saved[name + " retention"], _saved[name + " retention subfolders"] == "Include", _retentionConsents.GetValueOrDefault(name)));
            }
        };
        _retentionTimer.Start();
    }

    private void RenderDestinationControls(StackPanel panel, ExportKind kind)
    {
        var name = KindName(kind); var mode = _draft[name + " destination"];
        var folder = ExportDestinations.DisplayFolder(kind, mode, kind == ExportKind.Report ? _draftReportsFolder : _draftBackupsFolder, ToolboxPaths.Root);
        if (mode == "Custom Folder")
            panel.Children.Add(ActionButton("Choose " + name.ToLowerInvariant() + " folder…", () =>
            {
                var chosen = ChooseExportFolder(kind);
                if (chosen is null) return;
                if (kind == ExportKind.Report) _draftReportsFolder = chosen; else _draftBackupsFolder = chosen;
                ApplySearch();
            }));
        panel.Children.Add(Note(mode == "Ask Every Time" ? "A folder chooser opens for every save. Cancel aborts the save; no previous folder is reused."
            : "Destination: " + (folder ?? "Choose a folder.")));
        panel.Children.Add(ActionButton("Open " + (kind == ExportKind.Report ? "Reports" : "Backups") + " folder", () => OpenDestinationFolder(kind, true)));
        panel.Children.Add(Note(ExportDestinations.ScopeNotice + ".", true));
        panel.Children.Add(Note("Age uses the later of file creation and last modification, in UTC. Custom folders require explicit deletion consent. " +
            "Subfolders: " + (_draft[name + " retention subfolders"] == "Include" ? "included" : "excluded") + ". Cleanup runs after a save and every 15 minutes while Toolbox is open; Ask Every Time runs only after its chosen save.", true));
        if (_retentionStatus.Length > 0) panel.Children.Add(Note(_retentionStatus, true));
    }

    private void OpenDestinationFolder(ExportKind kind, bool draft = false)
    {
        var name = KindName(kind); var values = draft ? _draft : _saved;
        var custom = kind == ExportKind.Report ? (draft ? _draftReportsFolder : _customReportsFolder) : (draft ? _draftBackupsFolder : _customBackupsFolder);
        var folder = ExportDestinations.DisplayFolder(kind, values[name + " destination"], custom, ToolboxPaths.Root);
        if (folder is null) { ShowMock("Ask Every Time has no fixed folder. Choose a destination when saving."); return; }
        SafeDestination.CheckAncestors(folder);
        if (!Directory.Exists(folder)) { ShowMock("The destination has not been created yet:\n" + folder); return; }
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { folder } });
    }
}
