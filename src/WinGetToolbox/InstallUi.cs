using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinGetToolbox;

public partial class MainWindow
{
    private readonly List<PackageItem> _installSelection = [];
    private string _selectionFeedback = "";
    private string? _lastReportPath;
    private readonly List<(string Name, InstallOutcome Result)> _installReports = [];
    private DateTime _installReportAt = DateTime.Now;
    private IReadOnlyList<PackageItem> _activeInstallItems = [];
    private CancellationTokenSource? _installCancellation;
    private bool _installRunning;
    private bool _installPreparing;
    private bool _queueStopped;
    private bool _pauseAfterCurrent;
    private TaskCompletionSource<bool>? _resumeQueue;
    private bool _realInstallOptIn;
    private string _installStage = "";
    private DateTime _installStartedAt;
    private readonly System.Diagnostics.Stopwatch _installElapsed = new();
    private TextBlock? _elapsedLabel;
    private DispatcherTimer? _installClock;

    private void AddToInstallSelection(PackageItem item, bool updateFooter = true)
    {
        if (!Discovery.SafeIdentity(item.Id, item.Source) || item.Publisher == "Unknown")
        {
            ShowMock("The package needs a confirmed source and publisher before it can be selected.");
            return;
        }
        if (InstallationStatus(item) != "Not Installed")
        {
            ShowMock("Installation status is installed or cannot be determined. Refresh the inventory before selecting it.");
            return;
        }
        if (_installSelection.Any(x => x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) &&
                                      x.Source.Equals(item.Source, StringComparison.OrdinalIgnoreCase))) return;
        _installSelection.Add(item);
        PersistSelection();
        _selectionFeedback = $"{item.Name} added · {_installSelection.Count} selected";
        if (_route == "install") ApplySearch(); else if (updateFooter) FooterNote.Text = _selectionFeedback;
    }

    private void RenderInstallCentre()
    {
        TilesPanel.ItemWidth = double.NaN;
        TilesPanel.ItemHeight = double.NaN;
        var installPanel = new StackPanel { Width = Math.Max(310, ContentScroll.ViewportWidth - 20) };
        TilesPanel.Children.Add(installPanel);
        var progressGroup = new StackPanel { Width = Math.Max(310, ContentScroll.ViewportWidth - 20) };
        var elapsed = _installRunning ? $" · elapsed {_installElapsed.Elapsed:hh\\:mm\\:ss}" : "";
        var stageNote = CreateInfo(_installStage + elapsed);
        if (stageNote is FrameworkElement stageElement) stageElement.HorizontalAlignment = HorizontalAlignment.Left;
        if (_installStage.Length > 0) progressGroup.Children.Add(stageNote);
        if (_installRunning)
        {
            var percentage = System.Text.RegularExpressions.Regex.Match(_installStage, @"(?:Download|Install): WinGet reports (\d{1,3})%");
            progressGroup.Children.Add(new ProgressBar { Width = Math.Max(300, ContentScroll.ViewportWidth - 35), Height = 11, Minimum = 0, Maximum = 100,
                IsIndeterminate = !percentage.Success, Value = percentage.Success ? int.Parse(percentage.Groups[1].Value) : 0,
                Foreground = Accent, Background = Card, Margin = new Thickness(0, 0, 10, 10) });
            if (!percentage.Success) progressGroup.Children.Add(new TextBlock { Text = "Working · percentage unavailable from WinGet",
                Foreground = Muted, FontSize = 10, Margin = new Thickness(0, 0, 0, 9) });
        }
        _elapsedLabel = new TextBlock { Text = _installRunning ? "Elapsed " + _installElapsed.Elapsed.ToString(@"hh\:mm\:ss") : "", Foreground = Muted, FontSize = 10 };
        progressGroup.Children.Add(_elapsedLabel);
        installPanel.Children.Add(progressGroup);
        var selectionHeading = new TextBlock { Foreground = Subheadings, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 8) };
        selectionHeading.Inlines.Add(new System.Windows.Documents.Run("Current Selection"));
        selectionHeading.Inlines.Add(new System.Windows.Documents.Run($"  ({_installSelection.Count} {(_installSelection.Count == 1 ? "package" : "packages")})") { Foreground = Muted, FontWeight = FontWeights.Normal });
        installPanel.Children.Add(selectionHeading);
        if (_installSelection.Count == 0 && !_installRunning) installPanel.Children.Add(Note("Add apps from Collections or search to build your selection.", true));
        foreach (var item in _installSelection)
        {
            var logo = LogoRegistry.Create(item.Id, 32);
            var line = CreatePropertyRow(item.Name, item.Id + " · " + item.Source + " · " + item.Publisher);
            if (logo is null) installPanel.Children.Add(line);
            else
            {
                if (line is Border row) row.Width = Math.Max(230, ContentScroll.ViewportWidth - 58);
                var packageLine = new StackPanel { Orientation = Orientation.Horizontal };
                packageLine.Children.Add(new Border { Width = 40, Height = 40, Background = Card,
                    CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 5), Child = logo });
                packageLine.Children.Add(line);
                installPanel.Children.Add(packageLine);
            }
            var remove = new Button { Content = "Remove " + item.Name, Background = Accent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 10, 8) };
            remove.Click += (_, _) => { _installSelection.Remove(item); PersistSelection(); ApplySearch(); };
            installPanel.Children.Add(remove);
        }
        if (_installRunning)
        {
            installPanel.Children.Add(new TextBlock { Text = "Active reviewed queue · " + string.Join(", ", _activeInstallItems.Select(x => x.Name)), Foreground = TextBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
            var stop = new Button { Content = "Stop current queue", Background = Accent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 12, 10) };
            stop.Click += (_, _) => { if (_realInstallOptIn && !ConfirmLocal("Stop the current queue", "Request cancellation of WinGet and stop later packages? An elevated installer may continue, and completed changes cannot be undone.")) return; _queueStopped = true; _resumeQueue?.TrySetResult(true); _installStage = "Stopping requested. Waiting for the current process to exit; earlier installer changes cannot be undone."; _installCancellation?.Cancel(); ApplySearch(); };
            installPanel.Children.Add(stop);
            installPanel.Children.Add(ActionButton(_pauseAfterCurrent ? "Resume queue" : "Pause after current app", () =>
            {
                _pauseAfterCurrent = !_pauseAfterCurrent;
                if (!_pauseAfterCurrent) _resumeQueue?.TrySetResult(true);
                ApplySearch();
            }));
            installPanel.Children.Add(new TextBlock { Text = "Pause waits between apps; an active installer or Windows permission dialog cannot be safely frozen.", Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        }
        if (!_installRunning && !_installPreparing && _installSelection.Count > 0)
        {
            installPanel.Children.Add(new TextBlock { Text = _saved["Install review"] == "Condensed overview" ? "Review the exact package IDs before starting. Installers may request permission." : "Packages run one at a time. Windows or an installer may ask for permission. Stopping cannot undo changes already made.", Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 8, 8) });
            var mode = new CheckBox { Content = _captureMode ? "Real installation is disabled for simulated data" : "Allow WinGet installation after final confirmation", IsChecked = _realInstallOptIn, IsEnabled = !_captureMode,
                Foreground = TextBrush, Margin = new Thickness(6, 5, 6, 10) };
            mode.Checked += (_, _) => { _realInstallOptIn = true; ApplySearch(); };
            mode.Unchecked += (_, _) => { _realInstallOptIn = false; ApplySearch(); };
            installPanel.Children.Add(mode);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var review = new Button { Content = "Review selection", Background = Card, Margin = new Thickness(0, 4, 12, 10) };
            review.Click += (_, _) => CreateInstallConfirmationDialog(_installSelection, true).ShowDialog();
            actions.Children.Add(review);
            var start = new Button { Content = _realInstallOptIn ? "Review and install" : "Run simulated queue", Background = Accent,
                Margin = new Thickness(0, 4, 12, 10) };
            start.Click += async (_, _) => await StartInstallQueueAsync();
            if (_captureMode || _realInstallOptIn) actions.Children.Add(start);
            installPanel.Children.Add(actions);
        }
        if (_installReports.Count > 0)
        {
            installPanel.Children.Add(ActionButton("Preview completed report", () => PreviewReport(string.Join("\n\n", _installReports.Select(x => x.Name + ": " + x.Result.Summary + "\n" + x.Result.Details)), "Install")));
            RenderReportControls(installPanel);
            installPanel.Children.Add(new TextBlock { Text = "Completed report · " + _installReportAt.ToString("g"), Foreground = TextBrush, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 8) });
            foreach (var (name, result) in _installReports)
            {
                var reportGroup = new StackPanel { Width = Math.Max(310, ContentScroll.ViewportWidth - 20) };
                reportGroup.Children.Add(new TextBlock { Text = name + ": " + result.Summary, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
                if (!string.IsNullOrWhiteSpace(result.Details))
                    reportGroup.Children.Add(new Expander { Header = "Technical details for " + name, IsExpanded = _saved["Report detail"] == "Summary and detail", Foreground = TextBrush,
                        Background = Card, Margin = new Thickness(0, 0, 10, 8),
                        Content = new TextBlock { Text = ReportPrivacy.Redact(result.Details), Foreground = TextBrush,
                            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) } });
                installPanel.Children.Add(reportGroup);
            }
        }
        FooterNote.Text = _installRunning ? "The queue continues while you visit another page. Current progress is shown when you return." :
            "";
    }

    private async Task StartInstallQueueAsync()
    {
        if (_installRunning || _installPreparing || _installSelection.Count == 0 || (!_captureMode && !_realInstallOptIn)) return;
        _installPreparing = true;
        var queueItems = _installSelection.ToArray();
        var real = _realInstallOptIn && !_captureMode;
        try
        {
        if (real)
        {
            _installStage = "Checking WinGet and reviewed package identities…";
            ApplySearch();
            var detection = await Discovery.DetectAsync(CancellationToken.None);
            if (!detection.StartsWith("WinGet v", StringComparison.OrdinalIgnoreCase))
            {
                _installStage = "WinGet is unavailable: " + detection; _installPreparing = false; ApplySearch(); return;
            }
            Discovery.InvalidateInventory();
            _installedData = await Discovery.InstalledAsync(CancellationToken.None);
            _upgradeData = await Discovery.UpgradesAsync(CancellationToken.None);
            _windowsInventory = await WindowsInventory.ReadAsync(CancellationToken.None);
            foreach (var item in queueItems)
            {
                if (item.Operation == "Install" && InstallationStatus(item) != "Not Installed" || item.Operation == "Update" && UpgradeFor(item) is null)
                { _installStage = "Queue requires review: " + item.Name + " is no longer eligible. Refresh and select again."; return; }
                Discovery.InvalidatePackage(item.Id, item.Source);
                var exact = await Discovery.ExactAsync(item.Id, CancellationToken.None);
                if (!exact.Items.Any(x => x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) &&
                    x.Source.Equals(item.Source, StringComparison.OrdinalIgnoreCase)))
                {
                    _installStage = "Package identity could not be confirmed: " + item.Id + " / " + item.Source + ". " + exact.Status;
                    _installPreparing = false; ApplySearch(); return;
                }
                if (item.RequestedVersion.Length > 0 && !await Discovery.VersionAvailableAsync(item, item.RequestedVersion, CancellationToken.None))
                { _installStage = "The selected version is no longer available: " + item.Name; return; }
                var fresh = await Discovery.DetailsAsync(item, CancellationToken.None);
                if (!fresh.Publisher.Equals(item.Publisher, StringComparison.OrdinalIgnoreCase))
                {
                    _installStage = "Package publisher changed or could not be confirmed: " + item.Id;
                    _installPreparing = false; ApplySearch(); return;
                }
            }
            var confirmation = CreateInstallConfirmationDialog(queueItems);
            confirmation.ShowDialog();
            if (confirmation.Tag is not true) { _installPreparing = false; ApplySearch(); return; }
        }
        _installPreparing = false;
        _installRunning = true;
        _installStartedAt = DateTime.Now;
        _installElapsed.Restart();
        _installClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _installClock.Tick += (_, _) => { if (_route == "install" && _installRunning && _elapsedLabel is not null) _elapsedLabel.Text = "Elapsed " + _installElapsed.Elapsed.ToString(@"hh\:mm\:ss"); };
        _installClock.Start();
        _activeInstallItems = queueItems;
        foreach (var item in queueItems) _installSelection.Remove(item);
        PersistSelection();
        _queueStopped = false;
        _pauseAfterCurrent = false;
        _installReports.Clear();
        _installCancellation = new CancellationTokenSource();
        _installStage = real ? "Starting reviewed WinGet queue…" : "Running simulated queue…";
        ApplySearch();
        var adapter = real ? (IInstallAdapter)new WinGetInstallAdapter() : new SimulatedInstallAdapter();
        var queue = new InstallQueue(adapter, async (item, token) =>
        {
            Discovery.InvalidateInventory();
            var inventory = await Discovery.InstalledAsync(token);
            return inventory.Items.Any(x => x.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) &&
                x.Source.Equals(item.Source, StringComparison.OrdinalIgnoreCase) && (item.RequestedVersion.Length == 0 || x.Version.Equals(item.RequestedVersion, StringComparison.OrdinalIgnoreCase)));
        }, async (item, token) =>
        {
            if (_pauseAfterCurrent)
            {
                _resumeQueue = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _installStage = "Queue paused between apps. Resume or stop when ready.";
                if (_route == "install") ApplySearch();
                await _resumeQueue.Task.WaitAsync(token);
            }
            token.ThrowIfCancellationRequested();
            if (!real) return null;
            Discovery.InvalidateInventory();
            _installedData = await Discovery.InstalledAsync(token);
            _windowsInventory = await WindowsInventory.ReadAsync(token);
            if (item.Operation == "Install" && InstallationStatus(item) != "Not Installed") return "Installation stopped for review: " + item.Name + " is installed or its state is unknown.";
            if (item.Operation == "Update")
            {
                _upgradeData = await Discovery.UpgradesAsync(token);
                if (UpgradeFor(item) is not { } update || update.Available != item.RequestedVersion) return "Update eligibility changed. Review the package again.";
            }
            return null;
        });
        var progress = new Progress<(PackageItem Item, string Stage)>(p =>
        {
            _installStage = p.Item.Name + ": " + p.Stage;
            if (_route == "install") ApplySearch();
        });
        try
        {
            await queue.RunAsync(queueItems, progress, async (item, result) =>
            {
                _installStage = item.Name + ": paused after a problem.";
                if (_route == "install") ApplySearch();
                var decision = ShowInstallFailureDecision(item, result);
                if (decision == "Stop") _queueStopped = true;
                return await Task.FromResult(decision);
            }, (item, result) =>
            {
                _installReportAt = DateTime.Now;
                _installReports.Add((item.Name, result));
                if (_route == "install") ApplySearch();
            }, _installCancellation.Token);
            _installStage = _queueStopped ? "Queue stopped. Earlier installer changes were not rolled back." : "Queue finished. Review the report below.";
        }
        catch (OperationCanceledException) { _installStage = "Queue stopped. Earlier installer changes were not rolled back."; }
        finally
        {
            await SaveInstallReportAsync(real);
            _installRunning = false;
            _installClock?.Stop();
            _installElapsed.Stop();
            _activeInstallItems = [];
            _pauseAfterCurrent = false; _resumeQueue = null;
            _realInstallOptIn = false;
            _installCancellation.Dispose();
            _installCancellation = null;
            if (real) { Discovery.InvalidateInventory(); _installedData = null; _upgradeData = null; _ = RefreshPackageStatusAsync(); }
            if (_route == "install") ApplySearch();
        }
        }
        catch (Exception ex)
        {
            _installPreparing = false;
            _installRunning = false;
            _installClock?.Stop();
            _installElapsed.Stop();
            _activeInstallItems = [];
            _pauseAfterCurrent = false; _resumeQueue = null;
            _installStage = "The queue could not start or finish: " + ex.Message;
            if (_route == "install") ApplySearch();
        }
        finally { _installPreparing = false; if (_route == "install") ApplySearch(); }
    }

    private string ShowInstallFailureDecision(PackageItem item, InstallOutcome result)
    {
        var dialog = CreateFailureDialog(item, result);
        dialog.ShowDialog();
        return dialog.Tag as string ?? "Stop";
    }

    private Window CreateInstallConfirmationDialog(IReadOnlyList<PackageItem> items, bool reviewOnly = false)
    {
        var dialog = new Window { Title = reviewOnly ? "Review selection" : "Confirm real installation", Owner = this, Width = 510, Height = 300,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = BackBrush, Foreground = TextBrush,
            Tag = false };
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = reviewOnly ? "Review current selection" : "Confirm real WinGet installation", FontSize = 17,
            FontWeight = FontWeights.SemiBold, Foreground = TextBrush, Margin = new Thickness(0, 0, 0, 9) });
        content.Children.Add(new TextBlock { Text = "Review the exact packages and sources:", Foreground = TextBrush,
            Margin = new Thickness(0, 0, 0, 7) });
        var summary = string.Join("\n", items.Select(x => $"• {x.Operation}: {x.Name} — {x.Id} ({x.Source}) {x.RequestedVersion}"));
        content.Children.Add(new ScrollViewer { Height = 89, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = summary, Foreground = TextBrush, TextWrapping = TextWrapping.Wrap } });
        content.Children.Add(new TextBlock { Text = "Windows or an installer may ask for administrator permission. Cancellation cannot undo changes an installer has already made.",
            TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 8, 0, 14) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var choice in reviewOnly ? new[] { "Close" } : new[] { "Cancel", "Install" })
        {
            var button = new Button { Content = choice, Background = choice == "Install" ? Accent : Card,
                Foreground = TextBrush, BorderBrush = Accent, MinWidth = 85,
                Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 8, 12, 8) };
            button.Click += (_, _) => { dialog.Tag = choice == "Install"; dialog.Close(); };
            actions.Children.Add(button);
        }
        content.Children.Add(actions);
        dialog.Content = new Border { BorderBrush = Accent, BorderThickness = new Thickness(1),
            Background = dialog.Background, Child = content };
        dialog.Resources = Resources;
        dialog.Loaded += (_, _) => ApplyControlContrast(dialog);
        return dialog;
    }

    private Window CreateFailureDialog(PackageItem item, InstallOutcome result)
    {
        var dialog = new Window { Title = "Installation paused", Owner = this, Width = 480, Height = 232,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = BackBrush, Foreground = TextBrush,
            Tag = "Stop" };
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = "Installation paused", FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush, Margin = new Thickness(0, 0, 0, 10) });
        content.Children.Add(new TextBlock { Text = item.Name + " · " + item.Id + "\n" + result.Summary,
            TextWrapping = TextWrapping.Wrap, Foreground = TextBrush, Margin = new Thickness(0, 0, 0, 7) });
        content.Children.Add(new TextBlock { Text = "Retry may run the installer again. Stop does not undo earlier installer changes.",
            TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 14) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var choice in new[] { "Retry", "Skip", "Stop" })
        {
            var button = new Button { Content = choice, Background = choice == "Retry" ? Accent : Card,
                Foreground = TextBrush, BorderBrush = Accent, MinWidth = 77,
                Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 8, 12, 8) };
            button.Click += (_, _) => { dialog.Tag = choice; dialog.Close(); };
            actions.Children.Add(button);
        }
        content.Children.Add(actions);
        dialog.Content = new Border { BorderBrush = Accent, BorderThickness = new Thickness(1),
            Background = dialog.Background, Child = content };
        dialog.Resources = Resources;
        dialog.Loaded += (_, _) => ApplyControlContrast(dialog);
        return dialog;
    }

    private async Task SaveInstallReportAsync(bool real)
    {
        if (_installReports.Count == 0) return;
        try
        {
            var target = ResolveExportTarget(ExportKind.Report);
            if (target is null) { _installReports.Add(("Report storage", new InstallOutcome(false, false, "Report save cancelled; no file was written.", ""))); return; }
            var lines = new List<string> { "Toolbox Matrix " + ApplicationVersion + " (FP0.5.0)", real ? "Real WinGet queue" : "Simulation only", DateTime.Now.ToString("u"), _installStage, "" };
            foreach (var (name, result) in _installReports)
            {
                lines.Add(name + ": " + result.Summary);
                if (!string.IsNullOrWhiteSpace(result.Details)) lines.Add(result.Details);
                lines.Add("");
            }
            var path = ReportPrivacy.SaveNew(target.Folder, real ? "Install" : "Simulation", DateTime.Now, string.Join(Environment.NewLine, lines));
            _lastReportPath = path;
            SaveLastReportReference();
            _installReports.Add(("Saved report", new InstallOutcome(true, false, path, "")));
            await CleanExportTargetAsync(target);
            if (FolderRetention.Age(target.Retention) is not null) _installReports.Add(("Retention", new InstallOutcome(true, false, _retentionStatus, "")));
        }
        catch (Exception ex) { _installReports.Add(("Report storage", new InstallOutcome(false, false, "Could not save the report: " + ex.Message, ""))); }
    }
}
