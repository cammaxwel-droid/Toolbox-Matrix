using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinGetToolbox;

internal sealed record InstallOutcome(bool Success, bool Cancelled, string Summary, string Details);

internal interface IInstallAdapter
{
    Task<InstallOutcome> InstallAsync(PackageItem item, IProgress<string> progress, CancellationToken token);
}

internal sealed class SimulatedInstallAdapter : IInstallAdapter
{
    internal Func<PackageItem, int, bool>? ShouldFail { get; set; }
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<InstallOutcome> InstallAsync(PackageItem item, IProgress<string> progress, CancellationToken token)
    {
        var key = item.Source + "/" + item.Id;
        _attempts[key] = _attempts.GetValueOrDefault(key) + 1;
        foreach (var stage in new[] { "Download: simulated; no network transfer", "Install: simulated; no system changes", "Verify: checking simulated result" })
        {
            progress.Report(stage);
            await Task.Delay(180, token);
        }
        if (ShouldFail?.Invoke(item, _attempts[key]) == true)
            return new(false, false, "The simulated installation failed.", "No application was changed.");
        return new(true, false, "Simulation completed.", "No application was installed.");
    }
}

internal sealed class WinGetInstallAdapter : IInstallAdapter
{
    internal static string[] BuildArguments(PackageItem item)
    {
        if (!Discovery.SafeIdentity(item.Id, item.Source) || item.Operation is not ("Install" or "Update")) throw new ArgumentException("Invalid package identity or operation");
        var args = new List<string> { item.Operation == "Update" ? "upgrade" : "install", "--id", item.Id, "--exact", "--source", item.Source, "--interactive", "--disable-interactivity" };
        if (item.Operation == "Install") args.Add("--no-upgrade");
        if (item.RequestedVersion.Length > 0)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(item.RequestedVersion, @"^[A-Za-z0-9][A-Za-z0-9_.+\-]{0,99}$")) throw new ArgumentException("Invalid version");
            args.Add("--version"); args.Add(item.RequestedVersion);
        }
        return args.ToArray();
    }
    public async Task<InstallOutcome> InstallAsync(PackageItem item, IProgress<string> progress, CancellationToken token)
    {
        if (!Discovery.SafeIdentity(item.Id, item.Source) || item.Operation is not ("Install" or "Update"))
            return new(false, false, "Package identity is incomplete.", "Choose a verified package with an ID and source.");
        var start = new ProcessStartInfo("winget")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in BuildArguments(item)) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(token);
        timed.CancelAfter(TimeSpan.FromMinutes(45));
        try
        {
            progress.Report("Download: waiting for WinGet. An installer may request Windows permission.");
            var launch = Task.Run(() => { process.Start(); return process.Id; }, timed.Token);
            try { await launch.WaitAsync(TimeSpan.FromSeconds(20), timed.Token); }
            catch
            {
                _ = launch.ContinueWith(t => { if (t.IsCompletedSuccessfully) TryStopById(t.Result); }, TaskScheduler.Default);
                throw;
            }
            var stdout = ReadOutputAsync(process.StandardOutput, progress, timed.Token);
            var stderr = ReadOutputAsync(process.StandardError, progress, timed.Token);
            await process.WaitForExitAsync(timed.Token);
            var details = ((await stdout) + Environment.NewLine + (await stderr)).Trim();
            if (details.Length > 16000) details = details[^16000..];
            return process.ExitCode == 0
                ? new(true, false, "WinGet reported success. Verifying installed state…", details)
                : new(false, false, $"WinGet stopped with exit code {process.ExitCode}.", details);
        }
        catch (OperationCanceledException)
        {
            TryStop(process);
            return token.IsCancellationRequested
                ? new(false, true, "Cancellation requested; the current WinGet process was stopped.", "Stopping a process does not undo any changes already made by its installer. Check the installed inventory before retrying.")
                : new(false, false, "WinGet did not finish within 45 minutes and was stopped.", "An external installer may have continued independently. Check the installed inventory before retrying.");
        }
        catch (TimeoutException)
        {
            TryStop(process);
            return new(false, false, "WinGet startup did not respond within 20 seconds.", "A late-starting WinGet process will be stopped. Check installed state before retrying.");
        }
        catch (Exception ex)
        {
            TryStop(process);
            return new(false, false, "WinGet could not start.", ex.Message);
        }
    }

    private static void TryStop(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { /* Startup was cancelled before a process existed. */ }
        catch (System.ComponentModel.Win32Exception) { /* External installer may have outlived WinGet. */ }
    }

    private static void TryStopById(int processId)
    {
        try { using var late = Process.GetProcessById(processId); if (!late.HasExited) late.Kill(true); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, IProgress<string> progress, CancellationToken token)
    {
        var log = new StringBuilder();
        var buffer = new char[512];
        var lastPercent = -1;
        var downloading = false;
        var installing = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) break;
            var chunk = new string(buffer, 0, count);
            log.Append(chunk);
            if (log.Length > 18000) log.Remove(0, log.Length - 16000);
            var recent = log.ToString(Math.Max(0, log.Length - 350), Math.Min(log.Length, 350));
            if (!downloading && recent.Contains("Downloading", StringComparison.OrdinalIgnoreCase))
            { downloading = true; progress.Report("Download: WinGet is transferring the package. Waiting for size and progress details."); }
            if (!installing && (recent.Contains("Starting package install", StringComparison.OrdinalIgnoreCase) ||
                recent.Contains("Successfully downloaded", StringComparison.OrdinalIgnoreCase)))
            { installing = true; lastPercent = -1; progress.Report("Install: WinGet started the installer; progress unavailable."); }
            if (downloading && !installing)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(recent, @"(?<!\d)(\d{1,3})%");
                if (matches.Count > 0 && int.TryParse(matches[matches.Count - 1].Groups[1].Value, out var percent) &&
                    percent <= 100 && percent != lastPercent)
                { lastPercent = percent; progress.Report($"Download: WinGet reports {percent}%"); }
                var amount = System.Text.RegularExpressions.Regex.Match(recent,
                    @"(?i)(\d+(?:\.\d+)?\s*(?:KB|MB|GB))\s*/\s*(\d+(?:\.\d+)?\s*(?:KB|MB|GB))");
                var speed = System.Text.RegularExpressions.Regex.Match(recent, @"(?i)\d+(?:\.\d+)?\s*(?:KB|MB|GB)/s");
                if (amount.Success) progress.Report($"Download: WinGet reports {amount.Groups[1].Value} of {amount.Groups[2].Value}" +
                    (speed.Success ? " at " + speed.Value : ""));
            }
            else if (installing)
            {
                var marker = recent.LastIndexOf("Starting package install", StringComparison.OrdinalIgnoreCase);
                var active = marker >= 0 ? recent[(marker + "Starting package install".Length)..] : chunk;
                var matches = System.Text.RegularExpressions.Regex.Matches(active, @"(?<!\d)(\d{1,3})%");
                if (matches.Count > 0 && int.TryParse(matches[matches.Count - 1].Groups[1].Value, out var percent) &&
                    percent <= 100 && percent != lastPercent)
                { lastPercent = percent; progress.Report($"Install: WinGet reports {percent}%"); }
            }
        }
        return log.ToString();
    }
}

internal sealed class InstallQueue
{
    private readonly IInstallAdapter _adapter;
    private readonly Func<PackageItem, CancellationToken, Task<bool>> _verify;
    private readonly Func<PackageItem, CancellationToken, Task<string?>>? _beforeAttempt;
    internal InstallQueue(IInstallAdapter adapter, Func<PackageItem, CancellationToken, Task<bool>> verify, Func<PackageItem, CancellationToken, Task<string?>>? beforeAttempt = null)
    { _adapter = adapter; _verify = verify; _beforeAttempt = beforeAttempt; }

    internal async Task RunAsync(IReadOnlyList<PackageItem> items, IProgress<(PackageItem Item, string Stage)> progress,
        Func<PackageItem, InstallOutcome, Task<string>> onFailure, Action<PackageItem, InstallOutcome> onReport,
        CancellationToken token)
    {
        foreach (var item in items)
        {
            while (true)
            {
                if (token.IsCancellationRequested) return;
                if (_beforeAttempt is not null)
                {
                    var blocked = await _beforeAttempt(item, token);
                    if (blocked is not null) { onReport(item, new(false, false, blocked, "The next installer was not started. Review current inventory before a new queue.")); return; }
                }
                progress.Report((item, "Download: preparing package"));
                var stage = new Progress<string>(s => progress.Report((item, s)));
                var outcome = await _adapter.InstallAsync(item, stage, token);
                if (outcome.Success)
                {
                    progress.Report((item, "Verify: checking installed state"));
                    if (_adapter is WinGetInstallAdapter)
                    {
                        try
                        {
                            var verified = await _verify(item, token);
                            outcome = verified
                                ? outcome with { Summary = "Installed and independently found in WinGet's inventory." }
                                : outcome with { Success = false, Summary = "WinGet reported success, but installation could not be verified." };
                        }
                        catch (OperationCanceledException) { return; }
                        catch (Exception ex)
                        { outcome = outcome with { Success = false, Summary = "Verification could not complete.", Details = outcome.Details + "\n" + ex.Message }; }
                    }
                }
                onReport(item, outcome);
                if (token.IsCancellationRequested || outcome.Cancelled) return;
                if (outcome.Success) break;
                var decision = await onFailure(item, outcome);
                if (decision == "Retry") continue;
                if (decision == "Skip") break;
                return;
            }
        }
    }
}
