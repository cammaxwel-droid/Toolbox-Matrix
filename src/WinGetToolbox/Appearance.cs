using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinGetToolbox;
public partial class MainWindow
{
    private static class Palette
    {
        // WPF seals plain brushes placed in resource dictionaries. A colour expression keeps
        // the shared brush live; both DynamicResource and code-held references then update.
        private static readonly DependencyProperty ColourProperty = DependencyProperty.RegisterAttached("PaletteColour", typeof(Color), typeof(MainWindow), new PropertyMetadata(Colors.Transparent));
        internal static SolidColorBrush Create()
        {
            var brush = new SolidColorBrush();
            System.Windows.Data.BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty,
                new System.Windows.Data.Binding { Source = brush, Path = new PropertyPath(ColourProperty) });
            return brush;
        }
        internal static void Set(SolidColorBrush brush, Color colour) => brush.SetValue(ColourProperty, colour);
    }
    // Stable brush identities keep code-built controls and open popups in sync with XAML.
    // ApplyAppearance is the only palette writer; custom artwork remains independent.
    private static readonly SolidColorBrush TextBrush = Palette.Create();
    private static readonly SolidColorBrush Muted = Palette.Create();
    private static readonly SolidColorBrush Card = Palette.Create();
    private static readonly SolidColorBrush Accent = Palette.Create();
    private static readonly SolidColorBrush BackBrush = Palette.Create();
    private static readonly SolidColorBrush SideBrush = Palette.Create();
    private static readonly SolidColorBrush AccentText = Palette.Create();
    private static readonly SolidColorBrush TileArtwork = Palette.Create();
    private static readonly SolidColorBrush TileArtworkTint = Palette.Create();
    private static readonly SolidColorBrush AccentForeground = Palette.Create();
    private static readonly SolidColorBrush SurfaceBrush = Palette.Create();
    private static readonly SolidColorBrush SuccessBrush = Palette.Create();
    private static readonly SolidColorBrush WarningBrush = Palette.Create();
    private static readonly SolidColorBrush ErrorBrush = Palette.Create();
    private static readonly SolidColorBrush Subheadings = Palette.Create();
    private static readonly SolidColorBrush RefreshTimestamp = new(Color.FromRgb(128, 196, 184));
    private double ScaleFactor => (_preview ? _draft : _saved)["Text size"] switch { "Large" => 1.12, "Extra Large" => 1.22, _ => 1 };
    private void ApplyAppearance(Dictionary<string,string> values, string custom, string customTile, string customSubheadings)
    {
        if (RootGrid is null) return;
        var light = values["Theme"] == "Light";
        if (values["Theme"] == "System")
        { try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false); light = key?.GetValue("AppsUseLightTheme") is int value && value == 1; } catch { light = false; } }
        Palette.Set(TextBrush, light ? Color.FromRgb(22,21,34) : Color.FromRgb(244,241,248));
        Palette.Set(Muted, light ? Color.FromRgb(79,78,94) : Color.FromRgb(166,163,178));
        Palette.Set(BackBrush, light ? Color.FromRgb(245,245,250) : Color.FromRgb(0,0,5));
        Palette.Set(SideBrush, light ? Color.FromRgb(231,232,242) : Color.FromRgb(9,9,20));
        Palette.Set(SurfaceBrush, light ? Color.FromRgb(245,245,250) : Color.FromRgb(25,24,38));
        Palette.Set(SuccessBrush, light ? Color.FromRgb(25,105,55) : Color.FromRgb(123,211,151));
        Palette.Set(WarningBrush, light ? Color.FromRgb(133,76,0) : Color.FromRgb(255,207,138));
        Palette.Set(ErrorBrush, light ? Color.FromRgb(172,35,50) : Color.FromRgb(255,146,154));
        var hex = values["Accent"] == "Custom" && ValidColour(custom) ? custom : SettingsRegistry.AccentPresets.FirstOrDefault(x => x.Name == values["Accent"]).Hex ?? "#7F298F";
        Palette.Set(Accent, (Color)ColorConverter.ConvertFromString(hex));
        Palette.Set(AccentText, SettingsRegistry.Contrast(hex, "#FFFFFF") >= 4.5 ? Colors.White : Colors.Black);
        var artworkHex = values.GetValueOrDefault("Tile Artwork Colour") == "Custom Colour" ? customTile : hex;
        Palette.Set(TileArtwork, (Color)ColorConverter.ConvertFromString(ValidColour(artworkHex) ? artworkHex : hex));
        Palette.Set(TileArtworkTint, Color.FromArgb(11, TileArtwork.Color.R, TileArtwork.Color.G, TileArtwork.Color.B));
        Resources["TileArtwork"] = TileArtwork;
        var alpha = values["Tile tint"] switch { "8%" => (byte)20, "16%" => (byte)41, _ => (byte)25 };
        Palette.Set(Card, Color.FromArgb(alpha,126,156,238));
        Resources["WindowText"] = TextBrush; Resources["TextMuted"] = Muted; Resources["Card"] = Card;
        Resources["Surface"] = SurfaceBrush; Resources["AccentForeground"] = AccentForeground;
        Resources["StatusSuccess"] = SuccessBrush; Resources["StatusWarning"] = WarningBrush; Resources["StatusError"] = ErrorBrush;
        var subheadingHex = values.GetValueOrDefault("Subheadings Colour") == "Custom" && ValidColour(customSubheadings)
            ? customSubheadings : SettingsRegistry.AccentPresets.FirstOrDefault(x => x.Name == values.GetValueOrDefault("Subheadings Colour")).Hex ?? "#7F298F";
        Palette.Set(Subheadings, ReadableColour((Color)ColorConverter.ConvertFromString(subheadingHex), 4.5));
        Resources["Subheadings"] = Subheadings;
        Resources["RefreshTimestamp"] = RefreshTimestamp;
        // Keep the signature fill exact. Where its contrast is low, outline only the
        // glyphs using the central neutral text colour; the status surface stays themed.
        Resources["RefreshTimestampEdge"] = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = TextBrush.Color, ShadowDepth = 0, BlurRadius = 1,
            Opacity = SettingsRegistry.Contrast("#80C4B8", ColourHex(BackBrush.Color)) < 4.5 ? 1 : 0
        };
        Resources["WindowBackground"] = BackBrush; Resources["SidebarBackground"] = SideBrush; Resources["Accent"] = Accent; Resources["AccentText"] = AccentText;
        UpdateAccentResources();
        Background = BackBrush; Foreground = TextBrush;
        Opacity = values["Opacity"] switch { "95%" => .95, "90%" => .90, _ => 1 };
        RootGrid.LayoutTransform = new ScaleTransform(ScaleFactor, ScaleFactor);
        TilesPanel.Margin = new Thickness(0, values["Settings spacing"] == "Comfortable" ? 7 : 0, 0, 0);
    }
    private Button ActionButton(string label, Action action)
    {
        var b = new Button { Content = label, Background = Card, Foreground = TextBrush, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 6, 4), Padding = new Thickness(9, ActivePreferences["Settings spacing"] == "Compact" ? 5 : 9, 9, ActivePreferences["Settings spacing"] == "Compact" ? 5 : 9) };
        b.Click += (_, _) => { try { action(); } catch (Exception ex) { ShowMock("This action could not finish: " + ex.Message); } }; return b;
    }
    private static void ApplyControlContrast(DependencyObject root)
    {
        if (root is Button button && ReferenceEquals(button.Background, Accent)) button.Foreground = AccentText;
        if (root is not Visual) return;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) ApplyControlContrast(VisualTreeHelper.GetChild(root, i));
    }
    internal static bool IsModifierKey(Key key) => key is Key.LWin or Key.RWin or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift;
    internal static bool ShortcutAllowed(Key key, ModifierKeys modifiers)
    {
        if (!Enum.IsDefined(key) || (modifiers & ~(ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Windows)) != 0) return false;
        if (key is Key.None or Key.F11 or Key.Escape or Key.Tab or Key.System or Key.ImeProcessed or Key.DeadCharProcessed || IsModifierKey(key)) return false;
        if (key == Key.Delete && modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Alt)) return false;
        if (key == Key.F4 && modifiers.HasFlag(ModifierKeys.Alt) || key == Key.F12 && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) return false;
        if (modifiers.HasFlag(ModifierKeys.Windows) && key is Key.L or Key.D or Key.E or Key.R or Key.I or Key.U or Key.Tab) return false;
        return modifiers != ModifierKeys.None || key is Key.Left or Key.Right or Key.F5 or Key.F1;
    }
}
