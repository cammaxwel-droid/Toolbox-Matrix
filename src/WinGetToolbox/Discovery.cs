using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WinGetToolbox;

internal sealed record PackageItem(string Name, string Id, string Version, string Available, string Source, string Publisher = "Unknown", bool IsInstalled = false, string Operation = "Install", string RequestedVersion = "");
internal sealed record QueryResult(IReadOnlyList<PackageItem> Items, string Status, bool? Reliable = null, bool Truncated = false, DateTime? CheckedAt = null)
{
    internal bool IsReliable => Reliable ?? (Status.Contains("from WinGet", StringComparison.OrdinalIgnoreCase) || Status.StartsWith("No matching packages") || Status.StartsWith("No installed packages") || Status.StartsWith("No available upgrades"));
}

internal sealed record CommandResult(int ExitCode, string Output);
internal interface IWinGetRunner
{
    Task<CommandResult> RunAsync(string[] arguments, CancellationToken token);
}

internal static class Discovery
{
    private const int QueryCacheLimit = 128;
    private const int DetailsCacheLimit = 256;
    internal static TimeSpan CommandTimeout(string[] arguments) => TimeSpan.FromSeconds(arguments.FirstOrDefault() == "list" ? 75 : 35);
    private static readonly Dictionary<string, (QueryResult Result, DateTime Time)> Cache = new();
    private static readonly object CacheLock = new();
    private sealed class QueryGate
    {
        internal readonly System.Threading.SemaphoreSlim Semaphore = new(1, 1);
        internal int References;
    }
    private static readonly Dictionary<string, QueryGate> KeyLocks = new();
    private static readonly Dictionary<string, (PackageItem Item, DateTime Time)> DetailsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Threading.SemaphoreSlim QuerySlots = new(4);
    private static long _cacheGeneration;
    internal static IWinGetRunner Runner { get; set; } = new ProcessWinGetRunner();
    internal static bool UsesTestProvider => Runner is not ProcessWinGetRunner;
    private static Task<CommandResult> RunAsync(string[] arguments, CancellationToken token) => Runner.RunAsync(arguments, token);
    internal static (int QueryEntries, int DetailEntries, int ActiveKeyLocks) CacheCounts
    {
        get { lock (CacheLock) return (Cache.Count, DetailsCache.Count, KeyLocks.Count); }
    }
    internal static void InvalidateAll() { lock (CacheLock) { _cacheGeneration++; Cache.Clear(); DetailsCache.Clear(); } }

    private sealed class ProcessWinGetRunner : IWinGetRunner
    {
    public async Task<CommandResult> RunAsync(string[] arguments, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("winget")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.ArgumentList.Add("--disable-interactivity");
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(token);
        var limit = CommandTimeout(arguments);
        timed.CancelAfter(limit);
        try
        {
            var launch = Task.Run(() => { process.Start(); return process.Id; }, timed.Token);
            try { await launch.WaitAsync(TimeSpan.FromSeconds(15), timed.Token); }
            catch
            {
                _ = launch.ContinueWith(t =>
                {
                    if (!t.IsCompletedSuccessfully) return;
                    try { using var late = Process.GetProcessById(t.Result); if (!late.HasExited) late.Kill(true); }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }, TaskScheduler.Default);
                throw;
            }
            var output = process.StandardOutput.ReadToEndAsync(timed.Token);
            var error = process.StandardError.ReadToEndAsync(timed.Token);
            await process.WaitForExitAsync(timed.Token);
            return new(process.ExitCode, (await output) + "\n" + (await error));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            return new(-1, $"WinGet timed out after {limit.TotalSeconds:0} seconds while reading {(arguments.FirstOrDefault() == "list" ? "inventory" : "source data")}. Retry when ready.");
        }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            return new(-1, "WinGet did not start within 15 seconds. Try again or check Windows Package Manager.");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(-1, "WinGet is unavailable. Install or repair Windows Package Manager (App Installer) to use discovery.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(-1, "WinGet could not start: " + ex.Message);
        }
    }

    }

    private static string Message(CommandResult result)
    {
        var message = Regex.Replace(result.Output, @"\s+", " ").Trim();
        if (message.Contains("No applicable upgrade", StringComparison.OrdinalIgnoreCase) || message.Contains("No installed package found matching input criteria", StringComparison.OrdinalIgnoreCase)) return "No available upgrades found.";
        if (message.Contains("No package found", StringComparison.OrdinalIgnoreCase) || message.Contains("No installed package", StringComparison.OrdinalIgnoreCase)) return "No matching packages found.";
        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) || message.Contains("did not start within", StringComparison.OrdinalIgnoreCase)) return message;
        if (message.Contains("internet", StringComparison.OrdinalIgnoreCase) || message.Contains("network", StringComparison.OrdinalIgnoreCase) || message.Contains("0x80072", StringComparison.OrdinalIgnoreCase))
            return "WinGet reported a network connection error. Check your connection, then retry.";
        if (message.Contains("source", StringComparison.OrdinalIgnoreCase) && message.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return "WinGet reported a source query failure. Retry or inspect the read-only source check in Diagnostics.";
        return result.ExitCode == 0 ? "Ready" : (message.Length > 180 ? message[..180] + "…" : message.Length == 0 ? "WinGet returned an error without details." : message);
    }

    internal static async Task<string> DetectAsync(CancellationToken token)
    {
        var version = await RunAsync(["--version"], token);
        if (version.ExitCode != 0) return Message(version);
        var sources = await RunAsync(["source", "list"], token);
        if (sources.ExitCode != 0) return "WinGet " + version.Output.Trim() + ". " + Message(sources);
        var names = ParseSources(sources.Output);
        return "WinGet " + version.Output.Trim() + " · Sources: " + (names.Count == 0 ? "none available" : string.Join(", ", names));
    }

    private static List<string> ParseSources(string output)
    {
        var lines = Regex.Replace(output, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Replace("\r", "").Split('\n');
        var header = Array.FindIndex(lines, x => x.StartsWith("Name", StringComparison.OrdinalIgnoreCase) && x.Contains("Argument", StringComparison.OrdinalIgnoreCase));
        if (header < 0) return [];
        var argumentColumn = lines[header].IndexOf("Argument", StringComparison.OrdinalIgnoreCase);
        return lines.Skip(header + 2).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x[..Math.Min(argumentColumn, x.Length)].Trim())
            .Where(x => x.Length > 0 && !x.All(c => c == '-'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal const int SearchBatchSize = 99;
    internal static Task<QueryResult> SearchAsync(string term, CancellationToken token, int count = 100) =>
        QueryAsync(count == 0 ? ["search", "--query", term] :
            ["search", "--query", term, "--count", Math.Clamp(count, 1, 1000).ToString()], token);

    // WinGet has a count, not an offset. Ask for one real row beyond the next page.
    // Above its 1000-count limit, an ordinary search (no count) supplies the remaining matches.
    internal static async Task<QueryResult> SearchPageAsync(string term, CancellationToken token, IReadOnlyList<PackageItem>? previous = null)
    {
        var prior = (previous ?? []).DistinctBy(x => (x.Id.ToUpperInvariant(), x.Source.ToUpperInvariant())).ToArray();
        var target = prior.Length + SearchBatchSize;
        var requested = target < 1000 ? target + 1 : 0;
        while (true)
        {
            var result = await SearchAsync(term, token, requested);
            token.ThrowIfCancellationRequested();
            if (!result.IsReliable) return result;
            var merged = prior.Concat(result.Items).DistinctBy(x => (x.Id.ToUpperInvariant(), x.Source.ToUpperInvariant())).ToArray();
            // Truncation metadata alone is not enough to create a Load More item.
            if (merged.Length <= target && result.Truncated && requested != 0)
            {
                requested = requested >= 1000 ? 0 : Math.Min(1000, requested + SearchBatchSize);
                continue;
            }
            var shown = merged.Take(target).ToArray();
            return result with { Items = shown, Truncated = merged.Length > target,
                Status = shown.Length == 0 ? "No matching packages found." : $"{shown.Length} packages from WinGet." +
                    (merged.Length <= target && result.Truncated ? " The source did not return any further readable packages." : "") };
        }
    }

    internal static void InvalidateInventory()
    {
        lock (CacheLock)
        {
            _cacheGeneration++;
            Cache.Remove("list");
            Cache.Remove("list|--upgrade-available");
        }
    }

    internal static void InvalidatePackage(string id, string source)
    {
        lock (CacheLock)
        {
            _cacheGeneration++;
            Cache.Remove("search|--id|" + id + "|--exact");
            DetailsCache.Remove(source + "|" + id);
        }
    }

    internal static async Task<QueryResult> InstalledAsync(CancellationToken token)
    {
        var result = await QueryAsync(["list"], token);
        return result with { Items = result.Items.Select(x => x with { IsInstalled = true }).ToArray() };
    }

    internal static async Task<QueryResult> UpgradesAsync(CancellationToken token)
    {
        var result = await QueryAsync(["list", "--upgrade-available"], token);
        return result with { Items = result.Items.Select(x => x with { IsInstalled = true }).ToArray() };
    }

    internal static async Task<QueryResult> ExactAsync(string id, CancellationToken token) =>
        await QueryAsync(["search", "--id", id, "--exact"], token);

    private static async Task<QueryResult> QueryAsync(string[] args, CancellationToken token)
    {
        var key = string.Join("|", args);
        var lifetime = CacheLifetime(key);
        lock (CacheLock)
        {
            PruneExpiredLocked(DateTime.UtcNow);
            if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Time < lifetime) return cached.Result;
        }
        await QuerySlots.WaitAsync(token);
        try
        {
            var gate = await AcquireQueryGateAsync(key, token);
            try
            {
                long generation;
                lock (CacheLock)
                {
                    PruneExpiredLocked(DateTime.UtcNow);
                    if (Cache.TryGetValue(key, out var fresh) && DateTime.UtcNow - fresh.Time < lifetime) return fresh.Result;
                    generation = _cacheGeneration;
                }
                var result = await RunAsync(args, token);
                var items = result.ExitCode == 0 ? ParseTable(result.Output) : Array.Empty<PackageItem>();
                var status = result.ExitCode == 0 ? (items.Count == 0 ? args.SequenceEqual(new[] { "list", "--upgrade-available" }) ? "No available upgrades found." : args[0] == "list" ? "No installed packages found." : "No matching packages found." : $"{items.Count} result{(items.Count == 1 ? "" : "s")} from WinGet.") : Message(result);
                if (result.ExitCode == 0 && items.Count == 0 && !result.Output.Contains("No package", StringComparison.OrdinalIgnoreCase) &&
                    !result.Output.Contains("No installed package", StringComparison.OrdinalIgnoreCase) &&
                    !result.Output.Contains("No applicable upgrade", StringComparison.OrdinalIgnoreCase))
                    status = "WinGet returned no readable package rows. Results could not be determined.";
                var answer = new QueryResult(items, status, result.ExitCode == 0 && !status.Contains("could not be determined"), Regex.IsMatch(result.Output, @"(?im)^\s*<?(?:Additional entries|Results?|Search results?).*truncat"), DateTime.UtcNow);
                if (result.ExitCode == 0 && !status.Contains("could not be determined", StringComparison.OrdinalIgnoreCase))
                {
                    lock (CacheLock)
                        if (generation == _cacheGeneration)
                        {
                            Cache[key] = (answer, DateTime.UtcNow);
                            TrimOldestLocked(Cache, QueryCacheLimit);
                        }
                }
                return answer;
            }
            finally { ReleaseQueryGate(key, gate); }
        }
        finally { QuerySlots.Release(); }
    }

    private static TimeSpan CacheLifetime(string key) =>
        key is "list" or "list|--upgrade-available" ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(20);

    private static void PruneExpiredLocked(DateTime now)
    {
        foreach (var key in Cache.Where(x => now - x.Value.Time >= CacheLifetime(x.Key)).Select(x => x.Key).ToArray())
            Cache.Remove(key);
        foreach (var key in DetailsCache.Where(x => now - x.Value.Time >= TimeSpan.FromMinutes(20)).Select(x => x.Key).ToArray())
            DetailsCache.Remove(key);
    }

    private static void TrimOldestLocked<T>(Dictionary<string, (T Value, DateTime Time)> entries, int capacity)
    {
        while (entries.Count > capacity)
        {
            var oldest = entries.MinBy(x => x.Value.Time).Key;
            entries.Remove(oldest);
        }
    }

    private static async Task<QueryGate> AcquireQueryGateAsync(string key, CancellationToken token)
    {
        QueryGate gate;
        lock (CacheLock)
        {
            if (!KeyLocks.TryGetValue(key, out gate!)) KeyLocks[key] = gate = new QueryGate();
            gate.References++;
        }
        try { await gate.Semaphore.WaitAsync(token); return gate; }
        catch
        {
            ReleaseQueryGateReference(key, gate);
            throw;
        }
    }

    private static void ReleaseQueryGate(string key, QueryGate gate)
    {
        gate.Semaphore.Release();
        ReleaseQueryGateReference(key, gate);
    }

    private static void ReleaseQueryGateReference(string key, QueryGate gate)
    {
        lock (CacheLock)
        {
            gate.References--;
            if (gate.References == 0 && KeyLocks.TryGetValue(key, out var current) && ReferenceEquals(gate, current))
                KeyLocks.Remove(key);
        }
    }

    // WinGet's CLI currently emits a fixed-width text table, not structured JSON for these commands.
    // Column positions come from the header, so names and versions may contain spaces.
    internal static IReadOnlyList<PackageItem> ParseTable(string output)
    {
        var lines = Regex.Replace(output, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Replace("\r", "").Split('\n');
        var headerIndex = Array.FindIndex(lines, x => x.StartsWith("Name", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(x, @"\bId\b") && x.Contains("Version", StringComparison.OrdinalIgnoreCase));
        if (headerIndex < 0) return [];
        var header = lines[headerIndex];
        var labels = new[] { "Name", "Id", "Version", "Available", "Match", "Source" };
        var columns = labels.Select(x => (Label: x, Start: Regex.Match(header, @"(?<!\S)" + x + @"(?=\s|$)", RegexOptions.IgnoreCase).Index,
            Found: Regex.IsMatch(header, @"(?<!\S)" + x + @"(?=\s|$)", RegexOptions.IgnoreCase))).Where(x => x.Found).OrderBy(x => x.Start).ToArray();
        if (!columns.Any(x => x.Label == "Id")) return [];
        var results = new List<PackageItem>();
        foreach (var line in lines.Skip(headerIndex + 2))
        {
            if (string.IsNullOrWhiteSpace(line) || Regex.IsMatch(line, @"(?i)^\s*<?(?:Additional entries\b|(?:Search )?Results?\b.*truncat|\d+ upgrades? available\b|(?:More |\d+ )?packages? (?:have|had)\b|.*packages? (?:with unknown versions?|are pinned)\b|(?:warning|error|info):)")) continue;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Length; i++)
            {
                var start = columns[i].Start;
                var end = i + 1 < columns.Length ? columns[i + 1].Start : line.Length;
                values[columns[i].Label] = start >= line.Length ? "" : line.Substring(start, Math.Min(end, line.Length) - start).Trim();
            }
            var id = values.GetValueOrDefault("Id", "");
            if (string.IsNullOrWhiteSpace(id) || id.StartsWith("-") || id.Contains(' ') || id.Contains('…') ||
                id.Any(char.IsControl) || id.Length > 256 || string.IsNullOrWhiteSpace(values.GetValueOrDefault("Name")) ||
                string.IsNullOrWhiteSpace(values.GetValueOrDefault("Version"))) continue;
            results.Add(new(values.GetValueOrDefault("Name", ""), id, values.GetValueOrDefault("Version", ""),
                values.GetValueOrDefault("Available", ""), values.GetValueOrDefault("Source", "")));
        }
        return results.DistinctBy(x => (x.Id.ToUpperInvariant(), x.Source.ToUpperInvariant())).ToArray();
    }

    internal static Task<CommandResult> ReadCommandAsync(string[] arguments, CancellationToken token) =>
        arguments.Length > 0 && (arguments[0] is "--version" or "source" or "show" or "list" or "search")
            ? RunAsync(arguments, token) : throw new ArgumentException("Only read-only discovery commands are allowed.");

    internal static bool SafeIdentity(string id, string source) =>
        Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9_.+\-]{0,255}$") &&
        Regex.IsMatch(source, @"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}$");

    internal static async Task<bool> VersionAvailableAsync(PackageItem item, string version, CancellationToken token)
    {
        if (!SafeIdentity(item.Id, item.Source) || !Regex.IsMatch(version, @"^[A-Za-z0-9][A-Za-z0-9_.+\-]{0,99}$")) return false;
        var result = await RunAsync(["show", "--id", item.Id, "--source", item.Source, "--exact", "--version", version], token);
        return result.ExitCode == 0 && Regex.IsMatch(result.Output, @"(?im)^Version:\s*" + Regex.Escape(version) + @"\s*$");
    }

    internal static async Task<PackageItem> DetailsAsync(PackageItem item, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Source)) return item;
        var key = item.Source + "|" + item.Id;
        lock (CacheLock)
        {
            PruneExpiredLocked(DateTime.UtcNow);
            if (DetailsCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(20))
                return item with { Publisher = cached.Item.Publisher, Available = item.Available.Length == 0 ? cached.Item.Available : item.Available };
        }
        long generation;
        lock (CacheLock) generation = _cacheGeneration;
        var result = await RunAsync(["show", "--id", item.Id, "--exact", "--source", item.Source], token);
        if (result.ExitCode != 0) return item;
        string Field(string label) => Regex.Match(result.Output, "^" + label + @":\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline).Groups[1].Value.Trim();
        var publisher = Field("Publisher");
        var available = Field("Version");
        var detailed = item with { Publisher = publisher.Length == 0 ? "Unknown" : publisher,
            Available = item.Available.Length == 0 ? available : item.Available };
        if (detailed.Publisher != "Unknown")
            lock (CacheLock)
                if (generation == _cacheGeneration)
                {
                    PruneExpiredLocked(DateTime.UtcNow);
                    DetailsCache[key] = (detailed, DateTime.UtcNow);
                    TrimOldestLocked(DetailsCache, DetailsCacheLimit);
                }
        return detailed;
    }
}
