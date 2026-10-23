using WinGetToolbox;

var sample = """
Name                 Id                         Version     Available    Source
--------------------------------------------------------------------------
Visual Studio Code   Microsoft.VisualStudioCode  1.100.0     1.101.0      winget
Git                  Git.Git                    2.49.0                   winget
""";
var rows = Discovery.ParseTable(sample);
if (rows.Count != 2 || rows[0].Id != "Microsoft.VisualStudioCode" || rows[0].Available != "1.101.0" ||
    rows[1].Id != "Git.Git" || rows[1].Source != "winget")
    throw new Exception("WinGet table parsing failed.");
if (Discovery.ParseTable("No package found matching input criteria.").Count != 0)
    throw new Exception("No-result parsing failed.");
var footerSample = sample + "\nAdditional entries truncated due to count limit.\n<additional entries truncated due to result limit>\nResults truncated to 100 entries.\n2 upgrades available.\n";
if (Discovery.ParseTable(footerSample).Count != 2) throw new Exception("A WinGet footer became a package.");
var namedPackage = "Name                        Id                                    Version           Source\n" + new string('-', 95) + "\n" + $"{"Truncate File Tool",-28}{"Example.Truncate",-38}{"1.0",-18}winget";
if (Discovery.ParseTable(namedPackage).Count != 1) throw new Exception("A real package with truncation in its name was discarded.");
Console.WriteLine("Parser smoke checks passed, including truncation notices and a genuine Truncate package.");
var savedRunner = Discovery.Runner;
try
{
    string Table(IEnumerable<int> ids, bool more = false)
    {
        var header = $"{"Name",-28}{"Id",-38}{"Version",-18}Source\n" + new string('-',95) + "\n";
        return header + string.Join("\n",ids.Select(i=>$"{"Package "+i,-28}{"Test.Package"+i,-38}{"1",-18}winget")) +
            (more ? "\nAdditional entries truncated due to count limit." : "");
    }
    var pageChecks = 0;
    foreach(var total in new[]{90,99,100,198,199,301,1205})
    {
        Discovery.InvalidateAll();
        var requests = new List<string[]>();
        Discovery.Runner = new DelegateRunner((arguments, _) =>
        {
            requests.Add(arguments);
            var n=Array.IndexOf(arguments,"--count"); var count=n<0?total:int.Parse(arguments[n+1]);
            return Task.FromResult(new CommandResult(0,Table(Enumerable.Range(1,Math.Min(total,count)),total>count)));
        });
        QueryResult? current = null;
        do
        {
            var prior=current?.Items ?? [];
            current=await Discovery.SearchPageAsync("package-"+total,CancellationToken.None,prior);
            if(!current.IsReliable || current.Items.Count!=Math.Min(total,prior.Count+99) ||
                !prior.SequenceEqual(current.Items.Take(prior.Count)) || current.Truncated!=(current.Items.Count<total) ||
                current.Items.Select(x=>(x.Id,x.Source)).Distinct().Count()!=current.Items.Count)
                throw new Exception("Search paging failed for total "+total+" after "+prior.Count);
            pageChecks++;
        } while(current.Truncated);
        if(requests.Any(x=>x.Contains("--offset")) || (total>1000 && !requests.Any(x=>!x.Contains("--count"))))
            throw new Exception("Paging used unsupported offsets or failed to expand beyond count 1000.");
    }
    Discovery.InvalidateAll();
    Discovery.Runner=new DelegateRunner((arguments,_) =>
    {
        var n=Array.IndexOf(arguments,"--count"); var count=n<0?400:int.Parse(arguments[n+1]);
        var ids=Enumerable.Range(1,205).SelectMany(i=>new[]{i,i}).Take(count).ToArray();
        return Task.FromResult(new CommandResult(0,Table(ids,ids.Length<410)));
    });
    var duplicatePage=await Discovery.SearchPageAsync("duplicate",CancellationToken.None);
    if(duplicatePage.Items.Count!=99 || !duplicatePage.Truncated) throw new Exception("Duplicate look-ahead failed.");
    duplicatePage=await Discovery.SearchPageAsync("duplicate",CancellationToken.None,duplicatePage.Items);
    if(duplicatePage.Items.Count!=198 || !duplicatePage.Truncated) throw new Exception("Second duplicate batch failed.");
    Discovery.InvalidateAll();
    Discovery.Runner=new DelegateRunner((_,_)=>Task.FromResult(new CommandResult(-1,"Source failed: network unavailable")));
    var failedPage=await Discovery.SearchPageAsync("failed",CancellationToken.None,duplicatePage.Items);
    if(failedPage.IsReliable || failedPage.Truncated) throw new Exception("Source failure invented continuation.");
    Discovery.InvalidateAll();
    using(var cancelled=new CancellationTokenSource())
    {
        Discovery.Runner=new DelegateRunner((_,_)=>{cancelled.Cancel(); return Task.FromResult(new CommandResult(0,Table(Enumerable.Range(1,100),true)));});
        try { await Discovery.SearchPageAsync("cancelled",cancelled.Token); throw new Exception("Cancelled page was accepted."); }
        catch(OperationCanceledException) { }
    }
    Console.WriteLine($"Search pagination passed: {pageChecks} batch boundaries; 90, exact 99, 99+more, 198+more, exhaustion beyond 1000, dedupe, source failure and cancellation.");

    Discovery.InvalidateAll();
    Discovery.Runner = new DelegateRunner((_, _) => Task.FromResult(new CommandResult(0, sample)));
    for (var i = 0; i < 140; i++) await Discovery.SearchAsync("cache-bound-" + i, CancellationToken.None);
    if (Discovery.CacheCounts.QueryEntries != 128 || Discovery.CacheCounts.ActiveKeyLocks != 0)
        throw new Exception("Search cache capacity or query-gate cleanup failed.");
    Discovery.Runner = new DelegateRunner((_, _) => Task.FromResult(new CommandResult(0, "Publisher: Fixture\nVersion: 1.2")));
    for (var i = 0; i < 270; i++)
        await Discovery.DetailsAsync(new PackageItem("Fixture " + i, "Fixture.Package" + i, "1", "", "winget", "Fixture"), CancellationToken.None);
    if (Discovery.CacheCounts.DetailEntries != 256)
        throw new Exception("Package details cache capacity failed.");

    Discovery.InvalidateAll();
    var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    Discovery.Runner = new DelegateRunner((_, _) => { started.TrySetResult(true); return release.Task; });
    var inFlight = Discovery.SearchAsync("invalidated-query", CancellationToken.None);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Discovery.InvalidateAll();
    release.SetResult(new CommandResult(0, sample));
    await inFlight;
    if (Discovery.CacheCounts.QueryEntries != 0 || Discovery.CacheCounts.ActiveKeyLocks != 0)
        throw new Exception("An invalidated in-flight query repopulated the cache or leaked its key gate.");
    Console.WriteLine("Discovery cache regression checks passed: bounded entries, cleaned query gates, and invalidation generation.");
}
finally
{
    Discovery.Runner = savedRunner;
    Discovery.InvalidateAll();
}
if (args.Contains("--live-search"))
{
    var first = await Discovery.SearchPageAsync("microsoft", CancellationToken.None);
    if (!first.IsReliable || first.Items.Count != 99 || !first.Truncated)
        throw new Exception("Live initial search did not establish 99+more: " + first.Status);
    var second = await Discovery.SearchPageAsync("microsoft", CancellationToken.None, first.Items);
    if (!second.IsReliable || second.Items.Count != 198 || !second.Truncated ||
        !first.Items.SequenceEqual(second.Items.Take(99)) ||
        second.Items.Select(x => (x.Id.ToUpperInvariant(), x.Source.ToUpperInvariant())).Distinct().Count() != 198)
        throw new Exception("Live appended search failed: " + second.Status);
    Console.WriteLine("LIVE SEARCH PASS: microsoft returned 99 genuine unique apps plus verified more; the next request preserved them and appended to 198 unique apps plus verified more.");
    Console.WriteLine("First batch last app: " + first.Items[^1].Id);
    Console.WriteLine("Second batch first appended app: " + second.Items[99].Id);
    Console.WriteLine("Second batch last app: " + second.Items[^1].Id);
}
if (args.Contains("--live"))
{
    var token = CancellationToken.None;
    var found = await Discovery.ExactAsync("Git.Git", token);
    if (!found.Items.Any(x => x.Id == "Git.Git" && x.Source == "winget"))
        throw new Exception("Live exact search did not return Git.Git from winget: " + found.Status);
    var details = await Discovery.DetailsAsync(found.Items.First(x => x.Id == "Git.Git"), token);
    if (details.Publisher == "Unknown") throw new Exception("Live package publisher was not parsed.");
    var installed = await Discovery.InstalledAsync(token);
    var upgrades = await Discovery.UpgradesAsync(token);
    if (installed.Items.Count == 0) throw new Exception("Live installed inventory parsed no rows: " + installed.Status);
    Console.WriteLine($"Live WinGet checks passed: {installed.Items.Count} installed rows, {upgrades.Items.Count} upgrade rows.");
}

sealed class DelegateRunner(Func<string[], CancellationToken, Task<CommandResult>> run) : IWinGetRunner
{
    public Task<CommandResult> RunAsync(string[] arguments, CancellationToken token) => run(arguments, token);
}
