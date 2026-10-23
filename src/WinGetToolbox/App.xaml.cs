using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace WinGetToolbox;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--headless-verification") || e.Args.Contains("--verify-launch"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int result;
            try { result = LaunchVerification.Run(e.Args); }
            catch { result = 3; }
            Shutdown(result);
            return;
        }
        // Assign the normal startup window only after the silent verification branch.
        // WPF rejects assigning null to StartupUri once it has been configured.
        StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        base.OnStartup(e);
    }

    public App()
    {
        // App-wide suppression covers windows, templates and WPF tooltip defaults.
        System.Windows.Controls.ToolTipService.IsEnabledProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(false));
        System.Windows.Controls.ToolTipService.IsEnabledProperty.OverrideMetadata(typeof(FrameworkContentElement), new FrameworkPropertyMetadata(false));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), System.Windows.Controls.ToolTipService.ToolTipOpeningEvent,
            new System.Windows.Controls.ToolTipEventHandler((_, e) => e.Handled = true), true);
        var reviewArgs = Environment.GetCommandLineArgs();
        var reviewIndex = Array.IndexOf(reviewArgs, "--capture-screenshots");
        if (reviewIndex < 0) reviewIndex = Array.IndexOf(reviewArgs, "--verify-live-appearance");
        if (reviewIndex >= 0 && reviewIndex + 1 < reviewArgs.Length)
        {
            var reviewFolder = Path.GetFullPath(reviewArgs[reviewIndex + 1]);
            void Lifecycle(string stage)
            {
                Directory.CreateDirectory(reviewFolder);
                File.AppendAllText(Path.Combine(reviewFolder, "PROCESS-LIFECYCLE.txt"), DateTime.UtcNow.ToString("O") + " " + stage + "\n");
            }
            Exit += (_, e) => Lifecycle("Application Exit " + e.ApplicationExitCode);
            Dispatcher.ShutdownStarted += (_, _) => Lifecycle("Dispatcher shutdown started");
            Dispatcher.ShutdownFinished += (_, _) => Lifecycle("Dispatcher shutdown finished");
        }
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                var args = Environment.GetCommandLineArgs(); var i = Array.IndexOf(args, "--capture-screenshots");
                var folder = i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : Path.Combine(ToolboxPaths.Root, "logs");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, i >= 0 ? "CAPTURE-ERROR.txt" : "Toolbox-Crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"), ReportPrivacy.Redact(e.Exception.ToString()));
                if (i < 0 && !args.Contains("--headless-verification") && !args.Contains("--verify-launch"))
                    MessageBox.Show("Toolbox encountered an unexpected error and will close. A redacted crash report was saved locally in Toolbox's logs folder. No report was uploaded.", "Toolbox Matrix");
            }
            catch { }
            e.Handled = true; Shutdown(2);
        };
    }
}
