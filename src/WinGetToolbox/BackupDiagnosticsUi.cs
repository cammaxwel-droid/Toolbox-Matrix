using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinGetToolbox;
internal sealed record RestoreCandidate(BackupApplication Saved, PackageItem? Verified, string Status, bool SavedVersionAvailable);
public partial class MainWindow
{
    private BackupPayload? _importedBackup;
    private readonly List<RestoreCandidate> _restoreCandidates = [];
    private readonly HashSet<string> _restoreChosen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _restoreLatest = new(StringComparer.OrdinalIgnoreCase);
    private string _backupStatus = "No backup has been opened in this session.";
    private bool _backupBusy;
    private string? _lastBackupPath;
    private IReadOnlyList<DiagnosticResult> _diagnosticResults = [];
    private bool _diagnosticsRunning;
    private string _diagnosticStatus = "Choose Quick or Full Check. These checks do not repair Windows.";
    private string _recoveryStatus = "";
    private StackPanel PagePanel()
    {
        TilesPanel.ItemWidth = TilesPanel.ItemHeight = double.NaN;
        var panel = new StackPanel { Width = Math.Max(245, ContentScroll.ViewportWidth - 18) }; TilesPanel.Children.Add(panel); return panel;
    }
    private TextBlock Note(string text, bool muted = false) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = muted ? Muted : TextBrush, FontSize = 11, Margin = new Thickness(0, 4, 0, 7) };
    private void RenderBackup()
    {
        var panel = PagePanel();
        panel.Children.Add(Disclosure("backup-contents", "File contents and privacy", Note("Personal files, installed program files, licences and app data are excluded. The file contains app names, package IDs, sources, versions and preferences. Review it before sharing. A checksum detects changes; it does not establish who created the file.", true)));
        var actions = new WrapPanel();
        var export = ActionButton("Export backup…", async () => await ExportBackupAsync()); export.IsEnabled = !_backupBusy; actions.Children.Add(export);
        var import = ActionButton("Open backup…", async () => await OpenBackupAsync()); import.IsEnabled = !_backupBusy; actions.Children.Add(import);
        actions.Children.Add(ActionButton("Open Backup folder", OpenBackupFolder)); panel.Children.Add(actions);
        panel.Children.Add(Note(_saved["Backup destination"] == "Ask Every Time" ? "Destination: choose a folder each time you export." :
            "Destination: " + ExportDestinations.DisplayFolder(ExportKind.Backup, _saved["Backup destination"], _customBackupsFolder, ToolboxPaths.Root), true));
        panel.Children.Add(Note(_backupStatus));
        if (_importedBackup is not null)
        {
            panel.Children.Add(Note("Backup created " + _importedBackup.CreatedUtc.ToLocalTime().ToString("g") + " · " + _importedBackup.Applications.Length + " app entries. Only checked entries enter your review selection."));
            foreach (var candidate in _restoreCandidates)
            {
                var key = candidate.Saved.Source + "|" + candidate.Saved.Id;
                var canSelect = candidate.Verified is not null && InstallationStatus(candidate.Verified) == "Not Installed" && (candidate.SavedVersionAvailable || _restoreLatest.Contains(key));
                var check = new CheckBox { Content = candidate.Saved.Name + " · " + candidate.Saved.Id + " · saved " + candidate.Saved.Version, IsChecked = _restoreChosen.Contains(key), IsEnabled = canSelect, Foreground = TextBrush, Margin = new Thickness(0, 6, 0, 2) };
                check.Checked += (_, _) => _restoreChosen.Add(key); check.Unchecked += (_, _) => _restoreChosen.Remove(key); panel.Children.Add(check);
                panel.Children.Add(Note(candidate.Status, true));
                if (candidate.Verified is not null && !candidate.SavedVersionAvailable && InstallationStatus(candidate.Verified) == "Not Installed")
                {
                    var latest = new CheckBox { Content = "Use the currently available version for " + candidate.Saved.Name, IsChecked = _restoreLatest.Contains(key), Foreground = TextBrush };
                    latest.Checked += (_, _) => { _restoreLatest.Add(key); ApplySearch(); }; latest.Unchecked += (_, _) => { _restoreLatest.Remove(key); _restoreChosen.Remove(key); ApplySearch(); }; panel.Children.Add(latest);
                }
            }
            panel.Children.Add(ActionButton("Add selected apps to installation review", () =>
            {
                var added = 0;
                foreach (var candidate in _restoreCandidates)
                {
                    var key = candidate.Saved.Source + "|" + candidate.Saved.Id;
                    if (!_restoreChosen.Contains(key) || candidate.Verified is null || InstallationStatus(candidate.Verified) != "Not Installed" || (!candidate.SavedVersionAvailable && !_restoreLatest.Contains(key))) continue;
                    var item = candidate.Verified with { RequestedVersion = _restoreLatest.Contains(key) ? "" : candidate.Saved.Version, Operation = "Install" };
                    if (!Selected(item)) { _installSelection.Add(item); added++; }
                }
                PersistSelection(); _backupStatus = added + " app(s) added to the existing installation review. No installer ran."; Navigate("install");
            }));
            panel.Children.Add(ActionButton("Restore settings from this backup…", () =>
            {
                try
                {
                    if (!RestorePreferences(_importedBackup.Preferences, ConfirmLocal)) return;
                    _backupStatus = _settingsRecovery = "Backup settings restored and saved. No Apply click is needed. Retention is Keep all; no applications were installed.";
                    _settingsTab = "Appearance"; Navigate("settings"); ApplySearch();
                }
                catch (Exception ex) { _backupStatus = "Settings were not restored: " + ex.Message; ApplySearch(); }
            }));
        }
        FooterNote.Text = "Export uses saved preferences and keeps drafts unchanged. Opening a backup only reads it; settings restore requires confirmation. Installation requires separate approval.";
    }
    private async Task ExportBackupAsync()
    {
        if (_backupBusy) return; _backupBusy = true; _backupStatus = "Reading the current WinGet inventory for export…"; ApplySearch();
        try
        {
            var target = ResolveExportTarget(ExportKind.Backup, resolvePending: false);
            if (target is null) { _backupStatus = "Export cancelled. No file was written."; return; }
            var inventory = _captureMode ? _installedData! : await Discovery.InstalledAsync(CancellationToken.None);
            if (!inventory.IsReliable) throw new InvalidDataException("WinGet inventory is unavailable. Try Refresh before exporting.");
            var apps = inventory.Items.Where(x => Discovery.SafeIdentity(x.Id, x.Source)).DistinctBy(x => (x.Id, x.Source)).Select(x => new BackupApplication(x.Name, x.Id, x.Source, x.Version)).ToArray();
            var payload = new BackupPayload { Applications = apps, Preferences = new PortablePreferences { Values = new(_saved), Shortcuts = _shortcuts.ToDictionary(x => x.Key, x => $"{x.Value.Key}|{x.Value.Modifiers}"), CustomAccent = _customAccent, CustomTileColour = _customTileColour, CustomSubheadingsColour = _customSubheadingsColour } };
            payload.Preferences.Values["Tray icon"] = "Off"; payload.Preferences.Values["Report destination"] = "Default"; payload.Preferences.Values["Backup destination"] = "Ask Every Time";
            var destination = ExportDestinations.SaveBackup(target.Folder, BackupService.Export(payload), DateTime.Now);
            _lastBackupPath = destination; _backupStatus = $"Saved {apps.Length} recognised app(s) and settings to {destination}. {inventory.Items.Count - apps.Length} entries without a usable package ID/source were excluded. Personal files and app data were not copied.";
            await CleanExportTargetAsync(target);
            if (FolderRetention.Age(target.Retention) is not null) _backupStatus += "\n" + _retentionStatus;
        }
        catch (Exception ex) { _backupStatus = "Backup not saved: " + ex.Message; }
        finally { _backupBusy = false; ApplySearch(); }
    }
    private async Task OpenBackupAsync()
    {
        if (_backupBusy) return;
        RequireInteractiveUi();
        var picker = new OpenFileDialog { Title = "Open a Toolbox backup for validation", Filter = "Toolbox backup|*.tlbxbackup|JSON files|*.json", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        await ImportBackupFileAsync(picker.FileName);
    }
    private async Task ImportBackupFileAsync(string path)
    {
        _backupBusy = true; _importedBackup = null; _restoreCandidates.Clear(); _restoreChosen.Clear(); _restoreLatest.Clear(); _backupStatus = "Validating the file, inventory and package identities…"; ApplySearch();
        try
        {
            if (new FileInfo(path).Length > 5 * 1024 * 1024) throw new InvalidDataException("Backup exceeds the 5 MB safety limit.");
            var payload = BackupService.Import(await File.ReadAllTextAsync(path));
            if (!_captureMode) await RefreshAllAsync(false);
            foreach (var saved in payload.Applications)
            {
                var result = await Discovery.ExactAsync(saved.Id, CancellationToken.None);
                var exact = result.Items.FirstOrDefault(x => x.Id.Equals(saved.Id, StringComparison.OrdinalIgnoreCase) && x.Source.Equals(saved.Source, StringComparison.OrdinalIgnoreCase));
                if (exact is null) { _restoreCandidates.Add(new(saved, null, "Package or saved source is unavailable. No source was added and nothing can be installed.", false)); continue; }
                var detail = await Discovery.DetailsAsync(exact, CancellationToken.None);
                if (detail.Publisher == "Unknown") { _restoreCandidates.Add(new(saved, null, "Publisher could not be verified; this entry is blocked.", false)); continue; }
                var version = saved.Version.Length > 0 && await Discovery.VersionAvailableAsync(detail, saved.Version, CancellationToken.None);
                var state = InstallationStatus(detail);
                _restoreCandidates.Add(new(saved, detail, state == "Installed" ? "Already installed; excluded from reinstall." : state == "Unknown" ? "Installed state unknown; excluded until inventory can be checked." : version ? "Saved version is available. Select to review." : "Saved version is unavailable or unknown. You may explicitly choose the available version instead.", version));
            }
            _importedBackup = payload; _backupStatus = "File format, compatibility and checksum passed. Review each entry below; no installer has run.";
        }
        catch (Exception ex) { _importedBackup = null; _restoreCandidates.Clear(); _backupStatus = "Backup rejected: " + ex.Message; }
        finally { _backupBusy = false; ApplySearch(); }
    }
    private void OpenBackupFolder() => OpenDestinationFolder(ExportKind.Backup);
    private void RenderDiagnostics()
    {
        var panel = PagePanel(); var actions = new WrapPanel();
        foreach (var full in new[] { false, true }) { var button = ActionButton(full ? "Full Check" : "Quick Check", async () => await RunDiagnosticsAsync(full)); button.IsEnabled = !_diagnosticsRunning; actions.Children.Add(button); }
        actions.Children.Add(ActionButton("Preview report", () => PreviewReport(BuildSupportReport(), "Diagnostics"))); panel.Children.Add(actions);
        panel.Children.Add(Note(_diagnosticStatus));
        foreach (var result in _diagnosticResults)
        {
            var detail = Disclosure("diagnostic-" + result.Area, result.State + " · " + result.Area, Note(result.Detail, true), result.State is "WARNING" or "ERROR");
            detail.Foreground = result.State switch { "ERROR" => ErrorBrush, "WARNING" => WarningBrush, "PASS" => SuccessBrush, _ => TextBrush };
            panel.Children.Add(detail);
        }
        panel.Children.Add(new TextBlock { Text = "Recovery Centre", FontSize = 16, Foreground = Subheadings, Margin = new Thickness(0, 12, 0, 6) });
        panel.Children.Add(Note("Recovery affects Toolbox's own settings, selection and cached catalogue only. A snapshot is saved first."));
        panel.Children.Add(ActionButton("Save Toolbox snapshot", () => { _recoveryStatus = "Saved recovery snapshot: " + DiagnosticsService.Snapshot(ToolboxPaths.Root); ApplySearch(); }));
        panel.Children.Add(ActionButton("Snapshot and reset catalogue cache", () =>
        {
            if (!ConfirmLocal("Reset Toolbox cache", "Save a recovery snapshot, then clear only Toolbox's cached catalogue? Installed apps and Windows settings are unaffected.")) return;
            DiagnosticsService.Snapshot(ToolboxPaths.Root);
            if (File.Exists(CuratedCachePath)) File.Move(CuratedCachePath, CuratedCachePath + ".reset-" + DateTime.UtcNow.Ticks);
            _collectionData.Clear(); _diskCachedCollections.Clear(); Discovery.InvalidateAll(); _recoveryStatus = "Catalogue cache reset; previous data remains in the recovery snapshot."; ApplySearch();
        }));
        panel.Children.Add(ActionButton("Restore last settings snapshot…", RestoreSettingsSnapshot));
        panel.Children.Add(Note(_recoveryStatus, true)); FooterNote.Text = "Expand a result for details · reports stay local.";
    }
    private async Task RunDiagnosticsAsync(bool full)
    {
        if (_diagnosticsRunning) return; _diagnosticsRunning = true; _diagnosticStatus = (full ? "Full" : "Quick") + " Check running…"; ApplySearch();
        try { var folder = ExportDestinations.DisplayFolder(ExportKind.Report, _saved["Report destination"], _customReportsFolder, ToolboxPaths.Root); _diagnosticResults = await DiagnosticsService.RunAsync(full, ToolboxPaths.Root, folder, CancellationToken.None); _diagnosticStatus = (full ? "Full" : "Quick") + " Check finished at " + DateTime.Now.ToString("t") + ". Read each result below."; }
        catch (Exception ex) { _diagnosticStatus = "Check could not finish: " + ex.Message; }
        finally { _diagnosticsRunning = false; if (_route == "diagnostics") ApplySearch(); }
    }
    private void RestoreSettingsSnapshot()
    {
        try
        {
            var root = Path.Combine(ToolboxPaths.Root, "recovery");
            var path = Directory.Exists(root) ? Directory.GetDirectories(root).OrderByDescending(x => x).Select(x => Path.Combine(x, "settings-v1.json")).FirstOrDefault(File.Exists) : null;
            if (path is null) { _recoveryStatus = "No saved settings snapshot is available."; ApplySearch(); return; }
            if (!RestorePreferences(PreferenceRestore.ReadSnapshot(path), ConfirmLocal)) return;
            _settingsTab = "Advanced"; Navigate("settings");
            _recoveryStatus = "Snapshot settings restored and saved. No Apply click is needed. Retention is Keep all and needs a fresh choice.";
        }
        catch (Exception ex) { _recoveryStatus = "Settings were not restored: " + ex.Message; }
        ApplySearch();
    }
    private string BuildSupportReport()
    {
        var text = "Toolbox Matrix " + ApplicationVersion + " (FP0.5.0)\nLocal support report preview\n" + DateTime.Now.ToString("u") + "\n\n" +
            "No telemetry or automatic uploads. Inventory names, user paths, credentials and raw installer logs are omitted. Review this text before sharing.\n\n" +
            "Settings schema: " + SettingsRegistry.Version + "\nTheme: " + _saved["Theme"] + "\nCollection view: " + _saved["Collection view"] + "\n" +
            "WinGet inventory available: " + (_installedData?.IsReliable == true) + "\nWindows inventory: " + _windowsInventory.Status + "\n" +
            "Selected: " + _installSelection.Count + " · Active queue: " + _activeInstallItems.Count + " · Completed results: " + _installReports.Count + "\n\n" +
            string.Join("\n", _diagnosticResults.Select(x => x.State + " · " + x.Area + " · " + x.Detail));
        return ReportPrivacy.Redact(text);
    }
    private Window CreateReportPreview(string text, string kind)
    {
        RequireInteractiveUi();
        var dialog = new Window { Owner = this, Title = "Preview local " + kind.ToLowerInvariant() + " report", Width = 620, Height = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = BackBrush, Foreground = TextBrush, ResizeMode = ResizeMode.NoResize, Resources = Resources };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var note = Note("Review and edit the redacted text below. Save creates a local file only; nothing is uploaded."); DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
        var actions = new WrapPanel(); DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        var editor = new TextBox { Text = ReportPrivacy.Redact(text), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Card, Foreground = TextBrush, Padding = new Thickness(8) };
        actions.Children.Add(ActionButton("Save reviewed report…", async () =>
        {
            try
            {
                var target = ResolveExportTarget(ExportKind.Report, dialog);
                if (target is null) { note.Text = "Save cancelled. No report was written."; return; }
                _lastReportPath = ReportPrivacy.SaveNew(target.Folder, kind, DateTime.Now, editor.Text);
                SaveLastReportReference();
                await CleanExportTargetAsync(target);
                note.Text = "Saved: " + _lastReportPath + (FolderRetention.Age(target.Retention) is not null ? "\n" + _retentionStatus : "");
            }
            catch (Exception ex) { note.Text = "Report was not saved: " + ex.Message; }
        }));
        actions.Children.Add(ActionButton("Close", dialog.Close)); panel.Children.Add(editor); dialog.Content = panel; return dialog;
    }
    private void PreviewReport(string text, string kind) => CreateReportPreview(text, kind).ShowDialog();
    private bool ConfirmLocal(string title, string message)
    {
        var dialog = CreatePrototypeDialog(title, message, "Cancel"); dialog.SizeToContent = SizeToContent.Height; dialog.MaxHeight = SystemParameters.WorkArea.Height - 24;
        var grid = (Grid)((Border)dialog.Content).Child;
        var approve = ActionButton("Confirm", () => { dialog.Tag = true; dialog.Close(); }); approve.HorizontalAlignment = HorizontalAlignment.Left; approve.Margin = new Thickness(15, 0, 0, 10); Grid.SetRow(approve, 2); grid.Children.Add(approve);
        dialog.ShowDialog(); return dialog.Tag is true;
    }
    private void SaveLastReportReference()
    {
        if (_captureMode || _lastReportPath is null) return;
        try { ToolboxPaths.AtomicWrite(Path.Combine(ToolboxPaths.Root, "last-report.txt"), _lastReportPath); } catch { }
    }
    private void RenderAbout()
    {
        var panel = PagePanel();
        panel.Children.Add(Note("Application version " + ApplicationVersion + " · FP0.5.0 · local functional prototype"));
        panel.Children.Add(Disclosure("about-privacy", "Privacy and online services", PrivacyDetails()));
        panel.Children.Add(Disclosure("about-credits", "Licensing and independence", Note("Independent of Microsoft and package publishers. No telemetry or automatic report uploads.\n\nToolbox Matrix uses original artwork and Cam's supplied application icon. No third-party product logos are displayed. No trademark clearance is claimed. Windows, WinGet, .NET and third-party packages retain their respective owners' licences and terms. A reinstall list does not preserve application licences.")));
        var actions = new WrapPanel();
        actions.Children.Add(ActionButton("Help & Resources", () => Navigate("help")));
        actions.Children.Add(ActionButton("Preview support report", () => PreviewReport(BuildSupportReport(), "Support")));
        panel.Children.Add(actions);
        FooterNote.Text = "";
    }
}
