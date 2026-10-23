using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ToolboxMatrix;
internal static class IntegrationService
{
    internal static string AliasFolder => Path.Combine(ToolboxPaths.Root, "launcher");
    internal static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Toolbox Matrix.lnk");
    internal static bool AliasRegistered()
    { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\tlbxm.exe", false); return key?.GetValue("") as string == Environment.ProcessPath; }
    internal static bool CommandPathRegistered() => (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "").Split(';').Any(x => x.Trim().Equals(AliasFolder, StringComparison.OrdinalIgnoreCase)) && File.Exists(Path.Combine(AliasFolder,"tlbxm.cmd"));
    internal static void RemoveAlias()
    {
        if (!AliasRegistered()) throw new InvalidOperationException("This build does not own the current alias registration.");
        var launcher=Path.Combine(AliasFolder,"tlbxm.cmd");
        if (File.Exists(launcher) && !File.ReadAllText(launcher).Contains("Toolbox Matrix owned launcher")) throw new InvalidOperationException("The launcher is not owned by Toolbox; no change was made.");
        var oldPath=Environment.GetEnvironmentVariable("PATH",EnvironmentVariableTarget.User) ?? "";
        var newPath=string.Join(';',oldPath.Split(';').Where(x=>!x.Trim().Equals(AliasFolder,StringComparison.OrdinalIgnoreCase)));
        Environment.SetEnvironmentVariable("PATH",newPath,EnvironmentVariableTarget.User);
        Registry.CurrentUser.DeleteSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\tlbxm.exe",false);
        if(File.Exists(launcher)) File.Move(launcher,launcher+".removed-"+DateTime.UtcNow.Ticks);
    }
    internal static void RemoveShortcut()
    {
        if(!File.Exists(ShortcutPath)) return;
        var type=Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Shortcut inspection is unavailable.");
        dynamic shell=Activator.CreateInstance(type)!; dynamic shortcut=shell.CreateShortcut(ShortcutPath);
        string target;
        try { target=shortcut.TargetPath; } finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        if(!target.Equals(Environment.ProcessPath,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("This build does not own that shortcut.");
        Directory.CreateDirectory(ToolboxPaths.Root); File.Move(ShortcutPath,Path.Combine(ToolboxPaths.Root,"Toolbox-shortcut-removed-"+DateTime.UtcNow.Ticks+".lnk"));
    }
    internal static void RegisterAlias()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        using var existing = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\tlbxm.exe", false);
        if (existing is not null && existing.GetValue("") as string != executable) throw new InvalidOperationException("Another tlbxm registration exists. It was left unchanged.");
        var launcher = Path.Combine(AliasFolder, "tlbxm.cmd");
        if (File.Exists(launcher) && !File.ReadAllText(launcher).Contains("Toolbox Matrix owned launcher")) throw new InvalidOperationException("An existing launcher was left unchanged.");
        var oldPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        ToolboxPaths.AtomicWrite(Path.Combine(ToolboxPaths.Root, "integration-before.json"), JsonSerializer.Serialize(new { UserPath = oldPath, Executable = executable }));
        Directory.CreateDirectory(AliasFolder);
        File.WriteAllText(launcher, "@echo off\r\nrem Toolbox Matrix owned launcher\r\n\"" + executable.Replace("%", "%%") + "\" %*\r\n");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\tlbxm.exe"); key.SetValue("", executable);
        if (!oldPath.Split(';').Any(x => x.Trim().Equals(AliasFolder, StringComparison.OrdinalIgnoreCase))) Environment.SetEnvironmentVariable("PATH", oldPath.TrimEnd(';') + ";" + AliasFolder, EnvironmentVariableTarget.User);
    }
    internal static void RegisterShortcut()
    {
        if (File.Exists(ShortcutPath)) throw new InvalidOperationException("The existing Toolbox shortcut was left unchanged.");
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut creation is unavailable.");
        dynamic shell = Activator.CreateInstance(type)!; dynamic shortcut = shell.CreateShortcut(ShortcutPath);
        try { shortcut.TargetPath = Environment.ProcessPath!; shortcut.WorkingDirectory = AppContext.BaseDirectory; shortcut.Description = "Toolbox Matrix"; shortcut.Save(); }
        finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
    }
}
public partial class MainWindow
{
    private Forms.NotifyIcon? _tray;
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private void RenderIntegration(StackPanel panel)
    {
        bool alias; try { alias = IntegrationService.AliasRegistered() && IntegrationService.CommandPathRegistered(); } catch { alias = false; }
        panel.Children.Add(Note("Run / CMD / PowerShell alias: " + (alias ? "Registered for this executable" : "Not registered") + ". Start Menu: " + (File.Exists(IntegrationService.ShortcutPath) ? "Shortcut exists" : "Not registered") + "."));
        panel.Children.Add(Note("The local tlbxm.cmd launcher beside the build works by path. Global commands need a per-user App Paths entry and a user PATH entry. New terminals are needed after registration. Neither setting requires administrator access.", true));
        panel.Children.Add(ActionButton("Review alias registration…", () =>
        {
            if (!ConfirmLocal("Register tlbxm for this Windows account", "This writes Toolbox's launcher, adds its folder to your user PATH and registers tlbxm.exe in your user App Paths. It does not change the machine PATH, startup or security settings. Register this build?")) return;
            IntegrationService.RegisterAlias(); ShowMock("Alias registered for this account. Open a new terminal to use tlbxm. Keep this build at its current location."); ApplySearch();
        }));
        panel.Children.Add(ActionButton("Review Start Menu shortcut…", () =>
        {
            if (!ConfirmLocal("Create Toolbox Start Menu shortcut", "Create one Toolbox Matrix shortcut in your user Start Menu, pointing to this build? Existing shortcuts are not overwritten.")) return;
            IntegrationService.RegisterShortcut(); ApplySearch();
        }));
        panel.Children.Add(ActionButton("Review removal of this build's alias…", () =>
        {
            if (!ConfirmLocal("Remove this Toolbox alias", "Remove only this build's user App Paths entry and launcher PATH entry? The launcher is retained locally as a removed copy.")) return;
            IntegrationService.RemoveAlias(); ApplySearch();
        }));
        panel.Children.Add(ActionButton("Review removal of this build's shortcut…", () =>
        {
            if (!ConfirmLocal("Remove this Toolbox Start Menu shortcut", "Remove the shortcut only if it points to this build? A recovery copy stays in Toolbox's local folder.")) return;
            IntegrationService.RemoveShortcut(); ApplySearch();
        }));
        panel.Children.Add(Note("These actions require your confirmation in their own dialog. No integration is registered just by changing a preference. Moving this build later may require updating its registration.", true));
    }
    private void UpdateTray()
    {
        if (_saved["Tray icon"] != "On") { _tray?.Dispose(); _tray = null; return; }
        if (_tray is not null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Icon?.Dispose(); _tray.Dispose(); _tray = null; }
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/ToolboxMatrix;component/Assets/Toolbox Matrix.ico")).Stream;
        using var original = new Drawing.Icon(stream);
        _tray = new Forms.NotifyIcon { Text = "Toolbox Matrix", Icon = (Drawing.Icon)original.Clone(), Visible = true };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Open Toolbox", null, (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); }); menu.Items.Add("Install Centre", null, (_, _) => { Show(); WindowState = WindowState.Normal; Navigate("install"); Activate(); }); menu.Items.Add("Exit Toolbox", null, (_, _) => Close()); _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
    }
}
