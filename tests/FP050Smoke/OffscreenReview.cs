using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolboxMatrix;

internal static partial class Program
{
    private sealed class OffscreenApplication : App
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Layout may drain the dispatcher. Never let the product StartupUri or
            // command-line entry point run in this visual-tree-only fixture.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
    }

    private static void RenderReview(string folder)
    {
        folder = Path.GetFullPath(folder);
        var allowed = Path.Combine(Directory.GetCurrentDirectory(), "screenshots", "output");
        if (!SafeDestination.Within(folder, allowed) || Directory.Exists(folder))
            throw new InvalidOperationException("Review output must be a new folder under the existing screenshots/output.");
        Directory.CreateDirectory(folder);
        var window = new MainWindow();
        Set(window, "_captureMode", true);
        Set(window, "_saved", SettingsRegistry.Defaults); Call(window, "ResetDraft");
        Field<List<PackageItem>>(window, "_installSelection").Clear();
        Set(window, "_installedData", new QueryResult([], "SIMULATED DATA · empty inventory fixture", true));
        Set(window, "_upgradeData", new QueryResult([], "SIMULATED DATA · empty update fixture", true));
        Set(window, "_windowsInventory", new WindowsInventorySnapshot([], true, true, "SIMULATED DATA", DateTime.UtcNow));
        Set(window, "_discoveryStatus", "SIMULATED DATA · off-screen layout review; no live providers or installers.");
        Call(window, "UpdateTitleIcon");
        var root = (Grid)window.Content;
        // Detach the actual custom-chrome content from the unshown Window. No HWND,
        // Application.Run, Show, dialog, focus call or desktop capture is used.
        window.Content = null;
        root.Resources = window.Resources;
        root.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
        var scroll = (ScrollViewer)window.FindName("ContentScroll");
        var entries = new List<object>(); var names = new List<string>();
        void Guard()
        {
            using var process = Process.GetCurrentProcess();
            if (Application.Current is not OffscreenApplication || Application.Current.Windows.Cast<Window>().Any(w => w.IsVisible || PresentationSource.FromVisual(w) is not null) ||
                PresentationSource.FromVisual(root) is not null || process.MainWindowHandle != IntPtr.Zero)
                throw new InvalidOperationException("Off-screen guarantee failed; refusing to render.");
        }
        void Layout()
        {
            TextElement.SetForeground(root, window.Foreground);
            TextElement.SetFontFamily(root, window.FontFamily);
            TextElement.SetFontSize(root, window.FontSize);
            root.Measure(new Size(900, 540)); root.Arrange(new Rect(0, 0, 900, 540)); root.UpdateLayout();
            // UpdateLayout is synchronous. Do not pump the application dispatcher:
            // the fixture has no application run loop or native presentation source.
            root.UpdateLayout(); Guard();
        }
        void Shot(string name, string description, bool bottom = false, FrameworkElement? element = null)
        {
            Guard(); Layout(); Call(window, "Render"); Layout();
            scroll.ScrollToTop(); Layout();
            if (bottom) { scroll.ScrollToEnd(); Layout(); }
            var target = element ?? root;
            var width = (int)Math.Ceiling(target.ActualWidth); var height = (int)Math.Ceiling(target.ActualHeight);
            if (width < 10 || height < 10) throw new InvalidOperationException("Off-screen layout has no size.");
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(target);
            var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
            if (pixels.Where((_, i) => i % 4 == 3).Count(alpha => alpha != 0) < width * height / 2)
                throw new InvalidOperationException("Rendered image is empty or mostly transparent.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(folder, name))) encoder.Save(output);
            Guard(); names.Add(name);
            entries.Add(new { File = name, Description = description, Width = width, Height = height, Dpi = 96,
                Source = "Actual WPF controls; SIMULATED DATA; detached visual tree with no presentation source", NativeInputTested = false });
            Console.WriteLine("OFF-SCREEN: " + name);
        }
        void Page(string route, string name, string description, string? section = null, bool bottom = false)
        {
            Set(window, "_route", route); if (section is not null) Set(window, "_settingsTab", section);
            Shot(name, description, bottom);
        }
        Guard(); Layout();
        Page("home", "01-home.png", "Home, unchanged seven dashboard tiles.");
        Page("collections", "02-collections.png", "Collections category layout, including Tools and Sites.");
        Page("collection:tools", "03-tools.png", "Tools: approved empty collection.");
        Page("collection:sites", "04-sites.png", "Sites: approved empty collection.");
        Page("settings", "05-appearance.png", "Settings / Appearance, top.", "Appearance");
        Shot("06-appearance-lower.png", "Appearance lower fields and saved-settings actions.", true);
        Page("settings", "07-keyboard.png", "Settings / Keyboard, current defaults.", "Keyboard");
        Page("settings", "08-report-settings.png", "Settings / Reports destinations and Keep all retention.", "Reports");
        Page("settings", "09-backup-settings.png", "Settings / Backup destination and retention.", "Backup");
        Page("backup", "10-backup-restore.png", "Backup / Restore before import.");
        Set(window, "_importedBackup", new BackupPayload { Preferences = new() { Values = SettingsRegistry.Defaults } });
        Set(window, "_backupStatus", "SIMULATED DATA · validated backup fixture with zero app entries. No installer ran.");
        Shot("11-backup-validated.png", "Validated backup fixture showing the explicit Restore settings action.", true);
        Call(window, "LoadPreferences");
        Page("settings", "12-restored-settings.png", "Actual saved preferences from the isolated confirmed-restore test, reloaded without Apply.", "Appearance");
        Set(window, "_saved", SettingsRegistry.Defaults); Call(window, "ResetDraft");
        Field<Dictionary<string, bool>>(window, "_disclosures")["help-commands"] = true;
        Page("help", "13-help.png", "Help and Resources, expanded command guide (upper portion).");
        Shot("14-help-commands.png", "Expanded command guide generated from the real CLI command catalogue.", true);
        Page("about", "15-about.png", "About with application version 0.5.0.");
        Page("diagnostics", "16-diagnostics-recovery.png", "Diagnostics and the explicit settings snapshot restore action.", bottom: true);
        Shot("17-title-icon.png", "Actual WPF title toolbar and approved v02 icon at 96 DPI; not native taskbar/tray pixels.", element: (FrameworkElement)window.FindName("TitleStrip"));
        File.WriteAllText(Path.Combine(folder, "SCREENSHOT-INDEX.json"), JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(Path.Combine(folder, "SCREENSHOT-INDEX.txt"), new[]
        {
            "Toolbox Matrix 0.5.0 targeted fixes — 17 actual WPF off-screen layout screenshots",
            "SIMULATED DATA. Actual custom-chrome WPF visual tree rendered at 900x540 DIPs, 96 DPI (title crop: 900x34).",
            "No visible window, HWND/presentation source, desktop capture, focus change, mouse/keyboard input, live provider, installer or tray creation.",
            "These images show layout only. They do not prove native input, physical monitor DPI, taskbar/tray appearance or Cam/Baptist acceptance.",
            "12-restored-settings.png reloads the saved result of the isolated restore check. Other pages use simulated/default state.",
            "Visual review belongs to Cam and Baptist.", ""
        }.Concat(names));
        Check(entries.Count == 17 && names.Distinct().Count() == 17, "bounded off-screen review contains 17 genuine WPF layout images");
    }
}
