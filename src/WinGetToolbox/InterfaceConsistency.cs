using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace WinGetToolbox;

public partial class MainWindow
{
    private static readonly SolidColorBrush AccentFocusBrush = Palette.Create();
    private static readonly SolidColorBrush AccentHoverBrush = Palette.Create();
    private static readonly SolidColorBrush AccentSelectionBrush = Palette.Create();
    private static readonly SolidColorBrush AccentBorderBrush = Palette.Create();
    private static readonly SolidColorBrush TitleBrush = Palette.Create();
    private bool _hasMoreResults;
    private static SolidColorBrush AccentTint(byte alpha) => new(Color.FromArgb(alpha, Accent.Color.R, Accent.Color.G, Accent.Color.B));
    private static string ColourHex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    private static Color ReadableColour(Color colour, double minimum)
    {
        var backgrounds = new[] { BackBrush.Color, SideBrush.Color, SurfaceBrush.Color };
        var towardWhite = SettingsRegistry.Contrast("#FFFFFF", ColourHex(BackBrush.Color)) > SettingsRegistry.Contrast("#000000", ColourHex(BackBrush.Color));
        byte Step(byte channel) => (byte)(towardWhite ? Math.Min(255, channel + 4) : Math.Max(0, channel - 4));
        for (var i = 0; i < 64 && backgrounds.Any(bg => SettingsRegistry.Contrast(ColourHex(colour), ColourHex(bg)) < minimum); i++)
            colour = Color.FromRgb(Step(colour.R), Step(colour.G), Step(colour.B));
        return colour;
    }

    private void UpdateAccentResources()
    {
        // One mapping for chrome, originals, navigation, selections, hover and focus.
        // Artwork has its own resource so a custom artwork colour never changes the chrome.
        Palette.Set(AccentHoverBrush, AccentTint(38).Color);
        Palette.Set(AccentSelectionBrush, AccentTint(55).Color);
        Palette.Set(AccentBorderBrush, AccentTint(66).Color);
        Resources["AccentHover"] = AccentHoverBrush;
        Resources["AccentSelection"] = AccentSelectionBrush;
        Resources["AccentBorder"] = AccentBorderBrush;
        Palette.Set(AccentFocusBrush, ReadableColour(Accent.Color, 3));
        Palette.Set(AccentForeground, ReadableColour(Accent.Color, 4.5));
        Resources["AccentFocus"] = AccentFocusBrush;
        byte Blend(byte accent, byte background) => (byte)Math.Round(accent * .08 + background * .92);
        Palette.Set(TitleBrush, Color.FromRgb(Blend(Accent.Color.R, BackBrush.Color.R), Blend(Accent.Color.G, BackBrush.Color.G), Blend(Accent.Color.B, BackBrush.Color.B)));
        Resources["TitleBackground"] = TitleBrush;
        Resources[SystemColors.HighlightBrushKey] = Accent;
        Resources[SystemColors.HighlightTextBrushKey] = AccentText;
        foreach (var button in new[] { HomeNav, CollectionsNav, InstalledNav, InstallNav, BackupNav, DiagnosticsNav, HelpNav, AboutNav, SettingsNav })
            button.Background = button.Name == NavigationRules.SelectedButton(_route) ? (Brush)Resources["AccentSelection"] : Brushes.Transparent;
    }

    private void UpdatePageHeadings(string query)
    {
        PageTitle.Text = _route switch
        {
            "home" => "Home", "collections" => "Collections", "installed" => "Installed Apps",
            "install" => "Install Centre", "backup" => "Backup / Restore", "diagnostics" => "Diagnostics",
            "help" => "Help", "about" => "About", "settings" => "Settings",
            _ when _route.StartsWith("collection:") => Collections.First(x => x.Key == _route[11..]).Name,
            _ => _selectedPackage?.Name ?? "Application Properties"
        };
        Eyebrow.Text = _route == "settings" ? "Preferences" : "";
        PageIntro.Text = _route switch
        {
            "backup" => "Export Toolbox settings and a reinstall list",
            "diagnostics" => "Read-only checks — no Windows repairs",
            _ => ""
        };
        if (IsPackageSearchRoute() && query.Length >= 2)
        {
            PageTitle.Text = "Results for “" + query + "”";
            PageIntro.Text = _searchData is null ? "Searching packages…" : _searchData.Items.Count == 0 ? "No package results" : _searchData.Items.Count + " package results";
        }
        SettingsSectionHeader.Visibility = Visibility.Collapsed;
        Eyebrow.Visibility = Eyebrow.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PageIntro.Visibility = PageIntro.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSimulationIndicator()
    {
        // Data provenance, never a saved appearance preference or a build-time fixture label.
        var simulated = _captureMode || Discovery.UsesTestProvider || WindowsInventory.Provider is not null;
        CaptureBanner.Visibility = simulated ? Visibility.Visible : Visibility.Collapsed;
        CaptureBanner.Text = simulated ? "  ·  SIMULATED DATA" : "";
    }

    private void CompleteSettingsHelp(DependencyObject root)
    {
        // Accessible descriptions remain available without any mouse/focus popup handlers.
        if (root is FrameworkElement owner)
        {
            if (owner.ToolTip is ToolTip tip) tip.IsOpen = false;
            owner.ToolTip = null;
            ToolTipService.SetIsEnabled(owner, false);
            if (owner is Slider slider) slider.AutoToolTipPlacement = AutoToolTipPlacement.None;
            if (owner is ButtonBase button && string.IsNullOrEmpty(AutomationProperties.GetHelpText(owner)))
                AutomationProperties.SetHelpText(owner, SettingsActionHelp(button.Content as string ?? AutomationProperties.GetName(button)));
        }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToArray())
            CompleteSettingsHelp(child);
    }

    private static string SettingsActionHelp(string label) => label switch
    {
        "Apply" => "Save the current settings and shortcuts. The saved appearance is restored when Toolbox starts again.",
        "Cancel draft" => "Discard unsaved settings and restore the saved appearance, including the accent and tile artwork colours.",
        "Preview draft" => "Show this appearance draft across Toolbox without saving it. Apply saves it; Cancel draft restores the saved appearance.",
        "Stop preview" => "Show the saved appearance while keeping your unsaved draft available to edit or apply.",
        "Remap" => "Choose a new shortcut for this action. Conflicts are checked before you can apply the draft.",
        "Reset" => "Restore this action's default shortcut as a draft. Apply saves it; Cancel draft keeps the saved shortcut.",
        "Restore default shortcuts" or "Reset shortcuts as a draft" => "Restore the default keyboard shortcuts as a draft. Choose Apply to save them.",
        "Save recovery snapshot" => "Save Toolbox preferences, selection and catalogue cache in a local recovery snapshot.",
        "Reset preferences as a draft" => "Load the default preferences as a draft. Nothing is saved until you choose Apply.",
        "Check WinGet sources" => "Read the configured WinGet sources. This does not add or remove a source.",
        "Choose backup folder…" => "Choose an existing local folder for future Toolbox backups. Apply saves the folder preference.",
        "Open Backup folder" => "Open the current local backup folder in File Explorer.",
        "Preview support report" => "Review the local support report before deciding whether to save it. Nothing is uploaded.",
        _ when SettingsRegistry.All.Any(x => x.Section == label) || label == "Keyboard" => "Show the " + label + " settings.",
        _ when SettingsRegistry.AccentPresets.Any(x => x.Name == label) => "Use the " + label + " preset for this colour draft.",
        _ when label.StartsWith("Review ", StringComparison.Ordinal) => label.TrimEnd('…') + ". The next review explains the action before any Windows change.",
        _ => label.TrimEnd('…') + "."
    };

    private static void SetSettingsHelp(FrameworkElement owner, string description)
    {
        AutomationProperties.SetHelpText(owner, description);
        owner.ToolTip = null;
        ToolTipService.SetIsEnabled(owner, false);
        if (owner is Slider slider) slider.AutoToolTipPlacement = AutoToolTipPlacement.None;
    }

    private TextBlock SettingLabel(SettingDefinition definition)
    {
        var label = new TextBlock { Text = SettingsRegistry.Label(definition.Key), Foreground = TextBrush, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,6,0,4), Focusable = true, HorizontalAlignment = HorizontalAlignment.Left };
        KeyboardNavigation.SetIsTabStop(label, true);
        AutomationProperties.SetName(label, SettingsRegistry.Label(definition.Key));
        SetSettingsHelp(label, definition.Help);
        label.MouseLeftButtonDown += (_, _) => label.Focus();
        label.GotKeyboardFocus += (_, _) => label.Background = AccentTint(38);
        label.LostKeyboardFocus += (_, _) => label.Background = Brushes.Transparent;
        return label;
    }

    private void UpdateLoadMoreVisibility()
    {
        // The control is a genuine item in the same virtualized stream as the apps.
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock { Text = _loadingMore ? "Loading…" : "Load More", TextAlignment = TextAlignment.Center });
        content.Children.Add(new TextBlock { Text = _loadingMore ? "Checking packages" : _loadMoreError.Length > 0 ? "Try again" : _route == "installed" ? "More apps" : "Next 99 apps",
            FontSize = 10, FontWeight = FontWeights.Normal, Margin = new Thickness(0, 5, 0, 0), TextAlignment = TextAlignment.Center });
        LoadMoreButton.Content = content;
        AutomationProperties.SetHelpText(LoadMoreButton, _loadMoreError.Length > 0 ? _loadMoreError : "Append the next batch of apps");
        // Keep keyboard focus during loading; the handler prevents repeated requests.
        LoadMoreButton.IsEnabled = true;
    }
}
