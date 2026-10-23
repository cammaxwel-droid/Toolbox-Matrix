using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ToolboxMatrix;

public partial class MainWindow
{
    private readonly Dictionary<string, bool> _disclosures = new();
    private static bool ValidColour(string? value) => Regex.IsMatch(value ?? "", "^#[A-Fa-f0-9]{6}$");

    private Expander Disclosure(string id, string label, UIElement content, bool initiallyOpen = false)
    {
        var expander = new Expander
        {
            Header = label, HeaderTemplate = (DataTemplate)Resources["SubheadingTemplate"], Content = content, Foreground = TextBrush,
            IsExpanded = _disclosures.GetValueOrDefault(id, initiallyOpen),
            Margin = new Thickness(0, 4, 0, 6), Padding = new Thickness(0, 3, 0, 3),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(expander, label);
        expander.Expanded += (_, _) => _disclosures[id] = true;
        expander.Collapsed += (_, _) => _disclosures[id] = false;
        return expander;
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation; var x = c * (1 - Math.Abs(hue / 60 % 2 - 1)); var m = value - c;
        var (r, g, b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r+m)*255), (byte)Math.Round((g+m)*255), (byte)Math.Round((b+m)*255));
    }
    private static (double Hue, double Saturation, double Value) ToHsv(Color colour)
    {
        double r=colour.R/255d, g=colour.G/255d, b=colour.B/255d;
        var max=Math.Max(r,Math.Max(g,b)); var min=Math.Min(r,Math.Min(g,b)); var delta=max-min;
        var hue=delta == 0 ? 0 : max == r ? 60*((g-b)/delta % 6) : max == g ? 60*((b-r)/delta+2) : 60*((r-g)/delta+4);
        return ((hue+360)%360, max==0 ? 0 : delta/max, max);
    }
    private static System.Windows.Media.Imaging.BitmapSource? _colourWheel;
    private static System.Windows.Media.Imaging.BitmapSource ColourWheel
    {
        get
        {
            if (_colourWheel is not null) return _colourWheel;
            const int size=160; var pixels=new byte[size*size*4];
            for(var y=0;y<size;y++) for(var x=0;x<size;x++)
            {
                var dx=x-79.5; var dy=y-79.5; var distance=Math.Sqrt(dx*dx+dy*dy)/79;
                if(distance>1) continue;
                var c=FromHsv((Math.Atan2(dy,dx)*180/Math.PI+360)%360,distance,1); var i=(y*size+x)*4;
                pixels[i]=c.B; pixels[i+1]=c.G; pixels[i+2]=c.R; pixels[i+3]=255;
            }
            _colourWheel=System.Windows.Media.Imaging.BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,pixels,size*4);
            _colourWheel.Freeze(); return _colourWheel;
        }
    }

    private void AddColourEditor(StackPanel panel, string label, string value, Action<string> changed)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        var editor = new TextBox { Text = value, MaxLength = 7, Width = 100, Padding = new Thickness(6, 4, 6, 4),
            Background = Card, Foreground = TextBrush, CaretBrush = TextBrush, BorderBrush = AccentFocusBrush };
        AutomationProperties.SetName(editor, label); SetSettingsHelp(editor, "Enter # and six hexadecimal digits.");
        var swatch = new Border { Width = 26, Height = 26, Margin = new Thickness(6, 0, 7, 0), BorderBrush = Muted, BorderThickness = new Thickness(1) };
        var validity = new TextBlock { FontSize = 10, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLiveSetting(validity, System.Windows.Automation.AutomationLiveSetting.Polite);
        row.Children.Add(editor); row.Children.Add(swatch); row.Children.Add(validity); panel.Children.Add(row);
        var presets = new WrapPanel();
        foreach (var preset in SettingsRegistry.AccentPresets)
        {
            var button = ActionButton(preset.Name, () => editor.Text = preset.Hex);
            AutomationProperties.SetName(button, label + ": " + preset.Name);
            presets.Children.Add(button);
        }
        panel.Children.Add(presets);
        var picker = new WrapPanel { Margin = new Thickness(0, 6, 0, 8) };
        var canvas = new Canvas { Width = 160, Height = 160, Margin = new Thickness(2,2,18,2) };
        var wheel = new Image { Source = ColourWheel, Width = 160, Height = 160, Focusable = true, Cursor = System.Windows.Input.Cursors.Cross };
        AutomationProperties.SetName(wheel, label + " colour wheel");
        SetSettingsHelp(wheel, "Click or drag to choose hue and saturation. Arrow keys adjust hue and saturation; Page Up and Page Down adjust brightness.");
        System.Windows.Input.KeyboardNavigation.SetIsTabStop(wheel, true);
        var marker = new System.Windows.Shapes.Ellipse { Width=12, Height=12, Stroke=Brushes.Black, StrokeThickness=2, Fill=Brushes.White, IsHitTestVisible=false };
        canvas.Children.Add(wheel); canvas.Children.Add(marker); picker.Children.Add(canvas);
        var sliders = new StackPanel { Width = 220 };
        Slider Channel(string name, double maximum)
        {
            var title = new TextBlock { Text=name, Foreground=TextBrush, FontSize=11, Focusable=true, HorizontalAlignment=HorizontalAlignment.Left };
            SetSettingsHelp(title, name + ": use the arrow keys to adjust, or drag the slider.");
            sliders.Children.Add(title);
            var slider = new Slider { Minimum=0, Maximum=maximum, SmallChange=1, LargeChange=10, Margin=new Thickness(0,2,0,7) };
            AutomationProperties.SetName(slider,label + " " + name); SetSettingsHelp(slider, name + ": use the arrow keys to adjust, or drag the slider."); sliders.Children.Add(slider); return slider;
        }
        var hue=Channel("Hue",359); var saturation=Channel("Saturation",100); var brightness=Channel("Brightness",100);
        sliders.Children.Add(new TextBlock { Text="Arrow keys adjust each slider.",Foreground=Muted,FontSize=10 });
        picker.Children.Add(sliders); panel.Children.Add(Disclosure("colour-wheel-"+label,"Colour wheel",picker,true));
        var updating=false;
        void Sync()
        {
            changed(editor.Text);
            if (ValidColour(editor.Text))
            {
                var colour=(Color)ColorConverter.ConvertFromString(editor.Text); swatch.Background=new SolidColorBrush(colour);
                var hsv=ToHsv(colour); updating=true; hue.Value=hsv.Hue; saturation.Value=hsv.Saturation*100; brightness.Value=hsv.Value*100; updating=false;
                Canvas.SetLeft(marker,79.5+Math.Cos(hsv.Hue*Math.PI/180)*hsv.Saturation*79-6);
                Canvas.SetTop(marker,79.5+Math.Sin(hsv.Hue*Math.PI/180)*hsv.Saturation*79-6);
                var white=SettingsRegistry.Contrast(editor.Text,"#FFFFFF"); var black=SettingsRegistry.Contrast(editor.Text,"#000000");
                validity.Text=(white >= 4.5 ? "White" : "Black") + " control text · " + Math.Max(white,black).ToString("0.0") + ":1";
                if (_preview) { RefreshVisualPreview(); ApplyControlContrast(TilesPanel); }
            }
            else { swatch.Background=Brushes.Transparent; validity.Text="Enter a colour such as #7F298F"; }
            if (_draftStatus is not null) _draftStatus.Text=HasDraft ? "Draft changes · Apply to save" : "Saved preferences";
        }
        void Choose() { if (!updating) editor.Text=ColourHex(FromHsv(hue.Value,saturation.Value/100,brightness.Value/100)); }
        hue.ValueChanged+=(_,_)=>Choose(); saturation.ValueChanged+=(_,_)=>Choose(); brightness.ValueChanged+=(_,_)=>Choose();
        void Point(Point point)
        {
            var dx=point.X-79.5; var dy=point.Y-79.5;
            updating=true; hue.Value=(Math.Atan2(dy,dx)*180/Math.PI+360)%360; saturation.Value=Math.Min(100,Math.Sqrt(dx*dx+dy*dy)/79*100); updating=false; Choose();
        }
        wheel.MouseLeftButtonDown+=(_,e)=> { wheel.Focus(); wheel.CaptureMouse(); Point(e.GetPosition(wheel)); e.Handled=true; };
        wheel.MouseMove+=(_,e)=> { if(wheel.IsMouseCaptured && e.LeftButton==System.Windows.Input.MouseButtonState.Pressed) Point(e.GetPosition(wheel)); };
        wheel.MouseLeftButtonUp+=(_,_)=>wheel.ReleaseMouseCapture();
        wheel.GotKeyboardFocus+=(_,_)=>marker.Stroke=AccentFocusBrush;
        wheel.LostKeyboardFocus+=(_,_)=>marker.Stroke=Brushes.Black;
        wheel.PreviewKeyDown+=(_,e)=>
        {
            switch(e.Key)
            {
                case System.Windows.Input.Key.Left: hue.Value=(hue.Value+359)%360; break;
                case System.Windows.Input.Key.Right: hue.Value=(hue.Value+1)%360; break;
                case System.Windows.Input.Key.Up: saturation.Value=Math.Min(100,saturation.Value+1); break;
                case System.Windows.Input.Key.Down: saturation.Value=Math.Max(0,saturation.Value-1); break;
                case System.Windows.Input.Key.PageUp: brightness.Value=Math.Min(100,brightness.Value+1); break;
                case System.Windows.Input.Key.PageDown: brightness.Value=Math.Max(0,brightness.Value-1); break;
                default:return;
            }
            e.Handled=true;
        };
        editor.TextChanged+=(_,_)=>Sync(); Sync();
    }

    private TextBlock PrivacyDetails() => Note("Preferences, selections, backups and reports stay on this PC. Toolbox has no telemetry and never uploads support reports. Online queries contact your configured WinGet sources. An approved installer may contact its publisher and request Windows permission. Review local reports and backups before sharing them.");

    private void RenderHelp()
    {
        var panel = PagePanel();
        var actions = new WrapPanel();
        actions.Children.Add(ActionButton("Browse collections", () => Navigate("collections")));
        actions.Children.Add(ActionButton("Backup / Restore", () => Navigate("backup")));
        actions.Children.Add(ActionButton("Diagnostics", () => Navigate("diagnostics")));
        panel.Children.Add(actions);
        panel.Children.Add(Note("Add apps to your selection, then review them in Install Centre. Selecting an app never installs it.", true));
        panel.Children.Add(Disclosure("help-browse", "Browsing, selection and Properties", Note("Open a collection or search for an app. Tiles and Compact List show the same packages. Right-click an app, or use the keyboard context-menu key, for Properties. Right-click a collection to add an eligible app by name. Add All selects eligible apps and keeps you on the collection. Review your choices in Install Centre. Back and Forward retain each page's search and scroll position.")));
        var keyboard = new StackPanel();
        foreach (var binding in _shortcuts.Values)
            keyboard.Children.Add(Note(FormatShortcut(binding.Key, binding.Modifiers) + " · " + binding.Label));
        keyboard.Children.Add(Note("These are your saved mappings. Remap in Settings → Keyboard, then Apply. D1–D4 mean the top-row number keys; NumPad keys are separate. Ctrl+Shift+F12 always opens Keyboard settings. Normal text editing takes priority in fields, including Ctrl+A for Select All and Ctrl+X for Cut. Bare arrows stay with text and lists. Alt+F4 closes Toolbox. Windows may intercept Win combinations.", true));
        keyboard.Children.Add(ActionButton("Keyboard settings", () => { Navigate("settings"); _settingsTab = "Keyboard"; ApplySearch(); }));
        panel.Children.Add(Disclosure("help-keyboard", "Keyboard shortcuts", keyboard));
        var commands = new StackPanel();
        commands.Children.Add(Note("Use your existing tlbxm command in Run or a terminal. The launcher beside the app supports the same commands. This page does not register or execute anything.", true));
        foreach (var line in LaunchCommand.HelpLines) commands.Children.Add(Note(line));
        panel.Children.Add(Disclosure("help-commands", "Toolbox Commands", commands));
        panel.Children.Add(Disclosure("help-clear", "Clear Cache and Clear Data", Note("In Advanced settings, Clear Cache forgets Toolbox's local catalogue, search, details and inventory cache, then refreshes. Clear Data resets Toolbox preferences, custom inputs, selection and private reports. Report folders, recovery snapshots, backups, installed apps, WinGet sources and Windows integrations are preserved. Reports saved outside Toolbox's private folder are left in place. The optional running tray icon switches off with the reset.")));
        panel.Children.Add(Disclosure("help-refresh", "Refresh and inventory status", Note("The circular arrow and your Refresh shortcut run the same read-only checks. A brief spin acknowledges the request; the status above the page shows the pending checks and elapsed time. You can keep navigating. Refresh retains your settings, selection and active jobs. By default it keeps your page; Home on refresh is an optional preference. An incomplete result means a source or Windows inventory could not be checked. Use Diagnostics for details, then retry when the source is available.")));
        panel.Children.Add(Disclosure("help-backup", "Backup and restore limits", Note("A Toolbox backup contains the WinGet-recognised reinstall list and saved portable preferences, including appearance. Export keeps unsaved drafts unchanged. Personal files, installed program files, licences and app data are excluded. The checksum detects changes, not authorship. Opening a backup validates it and rechecks packages. Restore settings asks for confirmation, then saves and applies immediately without another Apply click. Confirmation also covers replacing unsaved settings edits; Cancel keeps them. Machine-specific destinations stay, retention becomes Keep all and deletion permissions are cleared. Missing packages remain blocked; a different available version needs an explicit choice. Installing selected apps still requires separate review and approval.")));
        panel.Children.Add(Disclosure("help-appearance", "Appearance and tile artwork", Note("In Settings → Appearance, Tile Artwork Colour defaults to Follow Accent. Choose Custom Colour for an independent #RRGGBB artwork colour. Apply saves it; Cancel draft restores the saved appearance. Preview draft lets you inspect changes before saving. Tile labels keep their readable text colour.")));
        panel.Children.Add(Disclosure("help-troubleshooting", "Diagnostics, recovery and reports", Note("Quick Check reads configuration, sources, storage and package inventory. Full Check also reads Windows inventory and report history. Expand each PASS, WARNING, ERROR, NO DATA or SKIPPED result for details; warnings and errors start open. Recovery snapshots cover only Toolbox settings, selection and catalogue cache. They do not repair Windows. Preview and review a support report before saving or sharing it.")));
        panel.Children.Add(Disclosure("help-privacy", "Privacy and online services", PrivacyDetails()));
        panel.Children.Add(ActionButton("About, credits and licensing", () => Navigate("about")));
        FooterNote.Text = "";
    }
}
