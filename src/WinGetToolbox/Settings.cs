using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinGetToolbox;

public partial class MainWindow
{
    private static readonly Dictionary<string, string[]> Choices = SettingsRegistry.Choices;
    private static readonly Dictionary<string, string> Defaults = Choices.ToDictionary(x => x.Key, x => x.Value[0]);
    private Dictionary<string, string> _saved = new(Defaults);
    private Dictionary<string, string> _draft = new(Defaults);
    private Dictionary<string, (string Label, Key Key, ModifierKeys Modifiers)> _draftShortcuts = new();
    private bool _preview;
    private string? _customReportsFolder;
    private string? _draftReportsFolder;
    private string? _customBackupsFolder, _draftBackupsFolder;
    private string _customAccent = "#7F298F", _draftAccent = "#7F298F";
    private string _customTileColour = "#7F298F", _draftTileColour = "#7F298F";
    private string _customSubheadingsColour = "#7F298F", _draftSubheadingsColour = "#7F298F";
    private TextBlock? _draftStatus;
    private string _settingsRecovery = "";
    private string _sourceCheckStatus = "Not checked in this session.";
    private static string NormalizedChoice(string key, Dictionary<string, string> values) =>
        values.TryGetValue(key, out var value) && Choices[key].Contains(value) ? value : Defaults[key];
    private static (Key Key, ModifierKeys Modifiers) NormalizedShortcut(string id, Dictionary<string, (string Label, Key Key, ModifierKeys Modifiers)> values)
    {
        var binding = values.TryGetValue(id, out var value) ? value : DefaultShortcuts()[id];
        return (binding.Key, binding.Modifiers);
    }
    private bool HasDraft => Choices.Keys.Any(key => NormalizedChoice(key, _draft) != NormalizedChoice(key, _saved)) ||
        _shortcuts.Keys.Any(id => NormalizedShortcut(id, _draftShortcuts) != NormalizedShortcut(id, _shortcuts)) || _draftReportsFolder != _customReportsFolder || _draftBackupsFolder != _customBackupsFolder || _draftAccent != _customAccent || _draftTileColour != _customTileColour || _draftSubheadingsColour != _customSubheadingsColour;
    private static string SettingsPath => Path.Combine(ToolboxPaths.Root, "settings-v1.json");

    private void LoadPreferences()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var data = JsonSerializer.Deserialize<SavedPreferences>(File.ReadAllText(SettingsPath));
                if (data?.Version is >= 1 and <= SettingsRegistry.Version)
                {
                    _customReportsFolder = data.CustomReportsFolder;
                    _customBackupsFolder = data.CustomBackupsFolder;
                    _customAccent = System.Text.RegularExpressions.Regex.IsMatch(data.CustomAccent ?? "", "^#[A-Fa-f0-9]{6}$") ? data.CustomAccent! : "#7F298F";
                    _customTileColour = ValidColour(data.CustomTileColour) ? data.CustomTileColour : "#7F298F";
                    _customSubheadingsColour = ValidColour(data.CustomSubheadingsColour) ? data.CustomSubheadingsColour : "#7F298F";
                    _saved = PreferencePolicy.Migrate(data.Values, data.Version);
                    _retentionConsents = data.Version >= 6 ? data.RetentionConsents ?? new() : new();
                    var bindings = ShortcutCatalog.Load(data.Shortcuts, out var warning);
                    foreach (var item in bindings) _shortcuts[item.Key] = item.Value;
                    _settingsRecovery = warning;
                }
                else throw new InvalidDataException("Unsupported settings version.");
            }
        }
        catch (Exception)
        {
            _saved = new(Defaults); SetDefaultShortcuts();
            _settingsRecovery = "Saved preferences were unreadable. Defaults were loaded; the original file is preserved in recovery.";
            try { if (File.Exists(SettingsPath)) DiagnosticsService.Snapshot(ToolboxPaths.Root); } catch { _settingsRecovery += " Recovery copy could not be written."; }
        }
        _collectionListView = _saved["Collection view"] == "Compact List";
        ResetDraft();
        RefreshVisualPreview();
    }

    private void ResetDraft()
    {
        _draft = new(_saved);
        _draftReportsFolder = _customReportsFolder;
        _draftBackupsFolder = _customBackupsFolder;
        _draftAccent = _customAccent;
        _draftTileColour = _customTileColour;
        _draftSubheadingsColour = _customSubheadingsColour;
        _draftShortcuts = _shortcuts.ToDictionary(x => x.Key, x => x.Value);
        _preview = false;
        RefreshVisualPreview();
    }

    private Dictionary<string, string> ActivePreferences => _route == "settings" ? PreferencePolicy.Preview(_saved, _draft, _preview) : _saved;
    private Dictionary<string, (string Label, Key Key, ModifierKeys Modifiers)> ActiveShortcuts => _route == "settings" ? _draftShortcuts : _shortcuts;
    private void RefreshVisualPreview() => ApplyAppearance(ActivePreferences, _preview ? _draftAccent : _customAccent, _preview ? _draftTileColour : _customTileColour, _preview ? _draftSubheadingsColour : _customSubheadingsColour);

    private bool ApplyDraft()
    {
        try
        {
            if (_draft["Report destination"] == "Custom Folder" && (string.IsNullOrWhiteSpace(_draftReportsFolder) || !Directory.Exists(_draftReportsFolder)))
            { ShowMock("Choose an existing custom report folder before applying."); return false; }
            if (_draft["Backup destination"] == "Custom Folder" && (string.IsNullOrWhiteSpace(_draftBackupsFolder) || !Directory.Exists(_draftBackupsFolder)))
            { ShowMock("Choose an existing custom backup folder before applying."); return false; }
            if (!System.Text.RegularExpressions.Regex.IsMatch(_draftAccent, "^#[A-Fa-f0-9]{6}$")) { ShowMock("Use a six-digit colour such as #7F298F."); return false; }
            if (_draft["Accent"] == "Custom" && Math.Max(SettingsRegistry.Contrast(_draftAccent, "#FFFFFF"), SettingsRegistry.Contrast(_draftAccent, "#000000")) < 4.5)
            { ShowMock("This accent needs more contrast for readable controls."); return false; }
            if (!ValidColour(_draftTileColour)) { ShowMock("Tile artwork needs a six-digit colour such as #7F298F."); return false; }
            if (!ValidColour(_draftSubheadingsColour)) { ShowMock("Subheadings need a six-digit colour such as #477ACB."); return false; }
            var retentionConsents = PrepareRetentionConsents();
            if (retentionConsents is null) return false;
            var data = new SavedPreferences
            {
                Version = SettingsRegistry.Version,
                Values = new(_draft),
                CustomReportsFolder = _draftReportsFolder,
                CustomBackupsFolder = _draftBackupsFolder,
                CustomAccent = _draftAccent,
                CustomTileColour = _draftTileColour,
                CustomSubheadingsColour = _draftSubheadingsColour,
                Shortcuts = _draftShortcuts.ToDictionary(x => x.Key, x => $"{x.Value.Key}|{x.Value.Modifiers}"),
                RetentionConsents = retentionConsents
            };
            CommitPreferences(data);
            return true;
        }
        catch (Exception ex)
        {
            ShowMock("Could not save Toolbox preferences: " + ex.Message);
            return false;
        }
    }

    private void CommitPreferences(SavedPreferences data)
    {
        var shortcuts = ShortcutCatalog.Load(data.Shortcuts, out var warning);
        // Replace the saved file before changing live preferences or discarding any draft.
        // A failed write leaves both the old file and all in-memory settings intact.
        ToolboxPaths.AtomicWrite(SettingsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        _saved = new(data.Values);
        _retentionConsents = new(data.RetentionConsents);
        _customReportsFolder = data.CustomReportsFolder;
        _customBackupsFolder = data.CustomBackupsFolder;
        _customAccent = data.CustomAccent;
        _customTileColour = data.CustomTileColour;
        _customSubheadingsColour = data.CustomSubheadingsColour;
        _collectionListView = _saved["Collection view"] == "Compact List";
        foreach (var item in shortcuts) _shortcuts[item.Key] = item.Value;
        _settingsRecovery = warning;
        ResetDraft();
        UpdateTray();
        ApplySearch();
    }

    internal bool RestorePreferences(PortablePreferences preferences, Func<string, string, bool> confirm)
    {
        var candidate = PreferenceRestore.Prepare(preferences, new SavedPreferences
        { Values = new(_saved), CustomReportsFolder = _customReportsFolder, CustomBackupsFolder = _customBackupsFolder });
        var message = "Restore these Toolbox preferences now? They will be saved immediately and kept after restart; no Apply click is needed." +
            (HasDraft ? "\n\nThis replaces your unsaved settings edits and preview. Cancel keeps those edits and your saved preferences." : "") +
            "\n\nThis machine's saved report and backup destinations are kept. Retention is set to Keep all, deletion permissions are cleared and the tray icon stays off. No applications will be installed.";
        if (!confirm("Restore Toolbox settings", message)) return false;
        CommitPreferences(candidate);
        return true;
    }

    private bool ResolvePending()
    {
        if (!HasDraft) { if (_preview) ResetDraft(); return true; }
        string decision = "Stay";
        var dialog = CreatePendingDialog(choice => decision = choice);
        dialog.ShowDialog();
        if (decision == "Apply") return ApplyDraft();
        if (decision == "Discard") { ResetDraft(); return true; }
        return false;
    }

    private Window CreatePendingDialog(Action<string> onChoice)
    {
        var dialog = CreatePrototypeDialog("Pending changes", "You have unsaved Toolbox settings. Apply them, discard them, or stay and keep editing.", "Stay");
        dialog.Width = Math.Min(440, ActualWidth - 24);
        dialog.Height = 190;
        var grid = (Grid)((Border)dialog.Content).Child;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 14, 10) };
        foreach (var label in new[] { "Apply", "Discard", "Stay" })
        {
            var button = new Button { Content = label, Background = label == "Apply" ? Accent : Card, Style = (Style)Resources[typeof(Button)], Margin = new Thickness(5, 0, 0, 0), MinWidth = 68 };
            button.Click += (_, _) => { onChoice(label); dialog.Close(); };
            actions.Children.Add(button);
        }
        var old = grid.Children.OfType<Button>().FirstOrDefault();
        if (old is not null) grid.Children.Remove(old);
        Grid.SetRow(actions, 2); grid.Children.Add(actions);
        return dialog;
    }

    private void RenderSettings(string query)
    {
        var width = Math.Max(1, ContentScroll.ViewportWidth - 18);
        var categories = new WrapPanel { Width = width, Margin = new Thickness(0, 0, 0, 7) };
        var tabs = SettingsRegistry.All.Select(x => x.Section).Where(x => x != "Advanced").Append("Keyboard").Distinct().ToArray();
        foreach (var tab in tabs.Where(x => x.Contains(query, StringComparison.OrdinalIgnoreCase) || SettingsRegistry.All.Any(d => d.Section == x && d.Key.Contains(query, StringComparison.OrdinalIgnoreCase))))
        {
            var button = new Button { Content = tab, Style = (Style)Resources[typeof(Button)], Background = tab == _settingsTab ? Accent : Card, Margin = new Thickness(0, 0, 5, 5), Padding = new Thickness(8, 4, 8, 4), FontSize = 11 };
            button.Click += (_, _) => { _settingsTab = tab; SearchBox.Clear(); ApplySearch(); }; categories.Children.Add(button);
        }
        // Measure the actual labels, then balance full rows instead of stranding
        // Keyboard on its own. The viewport already accounts for text scaling/DPI.
        var categoryButtons = categories.Children.OfType<Button>().ToArray();
        foreach (var button in categoryButtons) button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (categoryButtons.Length > 0)
        {
            var fittingColumns = Math.Max(1, (int)(width / categoryButtons.Max(b => b.DesiredSize.Width)));
            var rows = (int)Math.Ceiling((double)categoryButtons.Length / fittingColumns);
            categories.ItemWidth = width / Math.Ceiling((double)categoryButtons.Length / rows);
        }
        TilesPanel.Children.Add(categories);
        if (query.Length > 0) { FooterNote.Text = "Choose a matching settings section."; return; }
        var panel = new StackPanel { Width = width };
        panel.Children.Add(new TextBlock { Text = _settingsTab, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Subheadings, Margin = new Thickness(0, 0, 0, 8) });
        var tray = new StackPanel();
        var settingsPanel = _settingsTab == "Advanced" ? tray : panel;
        foreach (var definition in SettingsRegistry.All.Where(x => x.Section == _settingsTab))
        {
            settingsPanel.Children.Add(SettingLabel(definition));
            var options = new WrapPanel();
            foreach (var option in definition.Choices)
            {
                var choice = new Button { Content = option, Background = _draft[definition.Key] == option ? Accent : Card, Margin = new Thickness(0, 0, 5, 4), Padding = new Thickness(8, ActivePreferences["Settings spacing"] == "Compact" ? 4 : 8, 8, ActivePreferences["Settings spacing"] == "Compact" ? 4 : 8) };
                System.Windows.Automation.AutomationProperties.SetName(choice, SettingsRegistry.Label(definition.Key) + ": " + option);
                SetSettingsHelp(choice, definition.Help);
                if (_draft[definition.Key] == option) { choice.SetResourceReference(Control.BackgroundProperty, "Accent"); choice.SetResourceReference(Control.ForegroundProperty, "AccentText"); }
                choice.Click += (_, _) => { _draft[definition.Key] = option; RefreshVisualPreview(); ApplySearch(); }; options.Children.Add(choice);
            }
            settingsPanel.Children.Add(options);
            if (definition.Key == "Accent" && _draft["Accent"] == "Custom")
                AddColourEditor(panel, "Custom accent", _draftAccent, value => _draftAccent = value);
            if (definition.Key == "Tile Artwork Colour" && _draft["Tile Artwork Colour"] == "Custom Colour")
                AddColourEditor(panel, "Custom tile artwork colour", _draftTileColour, value => _draftTileColour = value);
            if (definition.Key == "Subheadings Colour" && _draft["Subheadings Colour"] == "Custom")
                AddColourEditor(panel, "Custom subheadings colour", _draftSubheadingsColour, value => _draftSubheadingsColour = value);
        }
        if (_settingsTab == "Appearance")
        {
            panel.Children.Add(ActionButton("Preview Draft", () => { _preview = true; RefreshVisualPreview(); ApplySearch(); }));
        }
        if (_settingsTab == "Keyboard") RenderKeyboard(panel, width);
        if (_settingsTab is not ("Appearance" or "Keyboard" or "Reports" or "Backup")) RenderDraftPreview(settingsPanel);
        if (_settingsTab == "Reports") { RenderReportControls(panel); panel.Children.Add(ActionButton("Preview support report", () => PreviewReport(BuildSupportReport(), "Support"))); }
        if (_settingsTab == "Backup") RenderDestinationControls(panel, ExportKind.Backup);
        if (_settingsTab == "Sources")
        {
            var check = ActionButton("Check WinGet sources", async () => { _sourceCheckStatus = await Discovery.DetectAsync(CancellationToken.None); ApplySearch(); }); panel.Children.Add(check);
            panel.Children.Add(new TextBlock { Text = _sourceCheckStatus + "\nCached collections: " + _collectionData.Count + ". Source queries can contact external services.", Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        }
        if (_settingsTab == "Advanced")
        {
            var integration = new StackPanel(); RenderIntegration(integration);
            panel.Children.Add(Disclosure("advanced-window", "Search timing and tray", tray));
            panel.Children.Add(Disclosure("advanced-windows", "Windows integration", integration));
            var recovery = new StackPanel();
            recovery.Children.Add(Note("Clear Cache refreshes Toolbox's local catalogue and inventory. Clear Data resets Toolbox only: preferences, custom inputs, selection and private reports. Report folders stay in place.", true));
            var clearCache = ActionButton("Clear Cache", async () => await ClearToolboxDataAsync(false));
            var clearData = ActionButton("Clear Data", async () => await ClearToolboxDataAsync(true));
            clearCache.IsEnabled = clearData.IsEnabled = !DataMaintenanceBusy;
            recovery.Children.Add(clearCache); recovery.Children.Add(clearData);
            if (DataMaintenanceBusy) recovery.Children.Add(Note("Wait for the current refresh, report or installation operation to finish before clearing data.", true));
            recovery.Children.Add(Note("Snapshots contain Toolbox preferences, selection and catalogue cache. They do not repair or change Windows."));
            recovery.Children.Add(ActionButton("Save recovery snapshot", () => { _recoveryStatus = "Snapshot saved: " + DiagnosticsService.Snapshot(ToolboxPaths.Root); ApplySearch(); }));
            recovery.Children.Add(ActionButton("Reset preferences as a draft", () => { _draft = new(Defaults); _draftShortcuts = DefaultShortcuts(); _draftAccent = "#7F298F"; _draftTileColour = "#7F298F"; _draftSubheadingsColour = "#7F298F"; _preview = false; RefreshVisualPreview(); ApplySearch(); }));
            recovery.Children.Add(ActionButton("Reset shortcuts as a draft", () => { _draftShortcuts = DefaultShortcuts(); ApplySearch(); }));
            if (!string.IsNullOrWhiteSpace(_settingsRecovery + _recoveryStatus)) recovery.Children.Add(Note((_settingsRecovery + "\n" + _recoveryStatus).Trim()));
            recovery.Children.Add(Note("Ctrl+Shift+F12 always opens Keyboard settings."));
            panel.Children.Add(Disclosure("advanced-recovery", "Toolbox recovery", recovery));
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(ActionButton("Apply", () => ApplyDraft()));
        if (_settingsTab == "Appearance") actions.Children.Add(ActionButton("Cancel Draft", () => { ResetDraft(); ApplySearch(); }));
        panel.Children.Add(actions);
        _draftStatus = new TextBlock { Text = HasDraft ? "Unsaved preview · Apply to save. Leaving Settings offers Apply, Discard or Stay." : "Saved preferences", TextWrapping = TextWrapping.Wrap, Foreground = TextBrush, FontSize = 10, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(_draftStatus);
        var explanations = new StackPanel();
        foreach (var definition in SettingsRegistry.All.Where(x => x.Section == _settingsTab))
            explanations.Children.Add(Note(SettingsRegistry.Label(definition.Key) + " · " + definition.Help, true));
        if (explanations.Children.Count > 0 && _settingsTab != "Advanced") panel.Children.Add(Disclosure("settings-" + _settingsTab, "About these settings", explanations));
        TilesPanel.Children.Add(panel);
        CompleteSettingsHelp(TilesPanel);
        FooterNote.Text = "Saved locally · no telemetry or automatic uploads.";
    }

    private void RenderDraftPreview(StackPanel panel)
    {
        var values = ActivePreferences;
        var text = _settingsTab switch
        {
            "Spacing" => values["Settings spacing"] + " spacing is previewed in these controls now.",
            "Collections" => "Collection view: " + values["Collection view"] + ".",
            "Navigation" => "Start page: " + values["Start page"] + ". Refresh returns " + (values["Home on refresh"] == "On" ? "to Home" : "to the current page") +
                ". Refresh on focus: " + values["Refresh on focus"] + " (" + values["Refresh interval"] + "). Arrow navigation: " + values["Arrow navigation"] + ".",
            "Search" => "Search will use " + values["Search mode"] + " and " + values["Search order"] + ".",
            "Installs" => values["Install review"] == "Condensed overview" ? "Review the exact package IDs before starting. Installers may request permission."
                : "Packages run one at a time. Final approval remains required.",
            "Updates" => "Installed Apps order: " + values["Update display"] + ".",
            "Sources" => "Package source labels: " + values["Source labels"] + ". Full identity remains in Properties.",
            "Language" => "Interface language: " + values["Language"] + ".",
            "Advanced" => "Search waits " + (values["Search delay"] == "Patient" ? "700" : "350") + " milliseconds. Tray icon: " + values["Tray icon"] + " after Apply.",
            _ => ""
        };
        if (text.Length > 0) panel.Children.Add(Note("Preview · " + text, true));
        if (_settingsTab != "Collections") return;
        var sample = _collectionData.Values.SelectMany(x => x.Result.Items).FirstOrDefault();
        if (sample is null) { panel.Children.Add(Note("Collection previews use your verified cached apps when available.", true)); return; }
        var preview = values["Collection view"] == "Compact List" ? BuildPackageRow(sample)
            : (FrameworkElement)CreateTile(sample.Name, sample.Name[..1], null, subtitle: InstallationStatus(sample));
        preview.IsHitTestVisible = false; preview.Focusable = false;
        panel.Children.Add(preview);
    }

    private bool DataMaintenanceBusy => _refreshRunning || _installRunning || _installPreparing || _backupBusy || _diagnosticsRunning || _retentionRunning;
    private async Task ClearToolboxDataAsync(bool allData)
    {
        if (DataMaintenanceBusy) { ShowMock("Wait for the current operation to finish before clearing Toolbox data."); return; }
        try
        {
            var plan = ToolboxDataMaintenance.Prepare(ToolboxPaths.Root, allData);
            var summary = allData
                ? "Reset Toolbox preferences, shortcut mappings, custom inputs, saved selection and search history, and remove " + plan.Entries.Length + " owned data/report files in:\n" + ToolboxPaths.Root +
                  "\n\nReport folders, backups and recovery snapshots stay. Reports outside this private folder stay. Installed apps, personal files, WinGet sources and existing command/Start Menu registrations stay. The optional tray icon switches off. A recovery copy is kept."
                : "Forget Toolbox's local catalogue, search, details and installed-state caches, then refresh. Your settings, selection, reports, installed apps and WinGet sources stay unchanged.";
            if (!ConfirmLocal(allData ? "Clear Toolbox Data" : "Clear Toolbox Cache", summary)) return;
            // The confirmation runs a nested message loop: recheck active operations and file hashes.
            if (DataMaintenanceBusy) { ShowMock("Another Toolbox operation started. Wait for it to finish, then try again."); return; }
            var snapshot = ToolboxDataMaintenance.Execute(plan);
            ResetToolboxMemory(allData);
            _recoveryStatus = allData ? "Toolbox data reset. Private report folders and external reports were preserved." : "Toolbox cache cleared.";
            if (snapshot is not null) _recoveryStatus += " Recovery copy: " + snapshot;
            ApplySearch();
            await RefreshAllAsync(false);
        }
        catch (Exception ex) { ShowMock("Toolbox data could not be cleared: " + ex.Message); }
    }

    private void ResetToolboxMemory(bool allData)
    {
        ++_catalogGeneration; ++_inventoryGeneration;
        CancelSearchRequest(); _routeCancel?.Cancel(); Discovery.InvalidateAll();
        _collectionData.Clear(); _diskCachedCollections.Clear(); _refreshPendingCollections.Clear();
        _installedData = _upgradeData = _searchData = null; _windowsInventory = WindowsInventorySnapshot.Empty;
        _selectedPackage = null; _packageDetailStatus = ""; _sourceCheckStatus = "Not checked in this session.";
        if (allData)
        {
            _installSelection.Clear(); _installReports.Clear(); _activeInstallItems = []; _installStage = _selectionFeedback = ""; _lastReportPath = null; _realInstallOptIn = false;
            _importedBackup = null; _restoreCandidates.Clear(); _restoreChosen.Clear(); _restoreLatest.Clear(); _lastBackupPath = null;
            _backupStatus = "No backup has been opened in this session."; _diagnosticResults = []; _diagnosticStatus = "Choose Quick or Full Check. These checks do not repair Windows.";
            _states.Clear(); _listOffsets.Clear(); _listStateKey = ""; _disclosures.Clear(); _history.Clear(); _history.Add("settings"); _historyIndex = 0;
            _saved = new(Defaults); SetDefaultShortcuts(); _customReportsFolder = _customBackupsFolder = null; _retentionConsents.Clear();
            _customAccent = _customTileColour = _customSubheadingsColour = "#7F298F"; _collectionListView = false;
            _searchScope = "All Apps"; SearchScope.SelectedIndex = 0; _updatesOnly = false; _inventoryLimit = 100;
            _rendering = true; SearchBox.Clear(); _rendering = false; _searchTerm = "";
            _route = "settings"; _settingsTab = "Advanced"; _disclosures["advanced-recovery"] = true;
            ResetDraft(); UpdateTray(); Render();
        }
        UpdateSelectionBadge();
    }

    private void RenderKeyboard(StackPanel panel, double width)
    {
        foreach (var binding in _draftShortcuts.OrderBy(x => x.Value.Label))
        {
            var row = new WrapPanel { Width = width, Margin = new Thickness(0, 0, 0, 5) };
            row.Children.Add(new TextBlock { Text = binding.Value.Label, Width = Math.Max(105, width - 240), Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = FormatShortcut(binding.Value.Key, binding.Value.Modifiers), Width = 105, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center });
            string id = binding.Key;
            var remap = new Button { Content = "Remap", Background = Card, Padding = new Thickness(6, 4, 6, 4) };
            remap.Click += (_, _) => Remap(id); row.Children.Add(remap);
            var reset = new Button { Content = "Reset", Background = Card, Padding = new Thickness(6, 4, 6, 4) };
            reset.Click += (_, _) => ResetShortcut(id); row.Children.Add(reset);
            panel.Children.Add(row);
        }
        var all = new Button { Content = "Restore default shortcuts", Background = Card, HorizontalAlignment = HorizontalAlignment.Left };
        all.Click += (_, _) => { _draftShortcuts = DefaultShortcuts(); ApplySearch(); }; panel.Children.Add(all);
        panel.Children.Add(new TextBlock { Text = "Resets use the 0.5.0 defaults. Draft shortcuts work immediately here; Apply saves them. Leaving Settings offers Discard. D1–D4 are WPF names for the top-row 1–4 keys, distinct from NumPad keys. Text-editing shortcuts stay with text fields.", Foreground = Muted, TextWrapping = TextWrapping.Wrap, FontSize = 10, Margin = new Thickness(0, 5, 0, 0) });
    }

    private static Dictionary<string, (string Label, Key Key, ModifierKeys Modifiers)> DefaultShortcuts() => ShortcutCatalog.Defaults();
}
