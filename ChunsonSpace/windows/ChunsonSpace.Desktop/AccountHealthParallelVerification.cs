using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.Verification;

namespace ChunsonSpace.Desktop;

// Exercises the production batch, queue, transport boundary, SSE reader and disk persistence.
// Signals hold real asynchronous requests open; no HTTP transport is constructed by the fixture.
internal static class AccountHealthParallelVerification
{
    private sealed class Progress(Action<AccountHealthProgress> report) : IProgress<AccountHealthProgress>
    { public void Report(AccountHealthProgress value) => report(value); }
    internal static void Max(ref int maximum, int value)
    {
        int old;
        do { old = Volatile.Read(ref maximum); if (old >= value) return; }
        while (Interlocked.CompareExchange(ref maximum, value, old) != old);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Name(int i) => $"SYNTHETIC-{i:D4}";
    private static string Id(int i) => $"SYNTHETIC-ID-{i:D4}";
    private static string Access(int i) => $"SYNTHETIC-access-{i:D4}";
    private static string Proxy(int i) => $"http://127.0.0.1:{10000+i}";
    private static DesktopWorkspace Fixture(string directory, int count)
    {
        var root = Path.Combine(directory, "accounts"); var home = Path.Combine(directory, "home");
        Directory.CreateDirectory(root); Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(root, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var store = new FileProfileStore(root);
        for (var i = 0; i < count; i++)
        {
            store.CreateBoundAccount(Name(i), new(Proxy(i), "America/New_York", "en-US"));
            store.SaveAccount(Name(i), new JsonObject { ["tokens"] = new JsonObject
                { ["access_token"] = Access(i), ["account_id"] = Id(i) } }, overwrite: true);
        }
        return new(root, home, Path.Combine(directory, "desktop"), fixture: true);
    }
    internal static async Task RunAsync(string directory, Action<string, bool> check)
    {
        Directory.CreateDirectory(directory);
        foreach (var count in new[] { 100, 1000 }) await ScaleAsync(Path.Combine(directory, count.ToString()), count, check);
        await CancellationAsync(Path.Combine(directory, "cancel-100"), check);
    }
    private static async Task ScaleAsync(string directory, int count, Action<string, bool> check)
    {
        var watch = Stopwatch.StartNew();
        var w = await Task.Run(() => Fixture(directory, count));
        var service = w.Accounts!.Accounts; var transport = w.Accounts.OfflineTransport!;
        transport.ResponsesAreSse = true;
        var queue = new AccountTestQueue(); var uiThread = Environment.CurrentManagedThreadId; var wrongThread = 0;
        queue.Changed += () => { if (Environment.CurrentManagedThreadId != uiThread) Interlocked.Increment(ref wrongThread); };
        var firstWave = Signal(); var release = Signal(); var slow = Signal(); var othersFinished = Signal();
        var finished = new ConcurrentDictionary<string, int>(); var requests = new ConcurrentDictionary<int, int>();
        var inAccount = new ConcurrentDictionary<int, int>(); var results = new ConcurrentDictionary<string, AccountHealthResult>();
        var active = 0; var peak = 0; var accountPeak = 0; var identityErrors = 0; var workerOnUi = 0; var otherCount = 0;
        var completeOrder = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();
        w.Profiles.SetTestsFrozen(Name(count-1), true);
        var past = DateTimeOffset.UtcNow.AddMinutes(-5);
        w.Profiles.SetAccountMarks(Name(4), AccountQuality.SuspectedDegraded, false, true, past);
        w.Profiles.SetAccountMarks(Name(5), AccountQuality.Normal, false, false, past, AccountMarkMode.Automatic);
        w.Profiles.ApplyAccountEvidence(Name(5), FileProfileStore.AccountIdentityHash(Id(5)), DateTimeOffset.UtcNow.AddMinutes(5),
            new string('A', 64), AccountQuality.Normal, "software", CialloTest.Models[0], "SYNTHETIC newer evidence");
        var newerMarks = w.Profiles.ReadAccountMarks(Name(5));
        w.Profiles.SetAccountMarks(Name(6), AccountQuality.SuspectedDegraded, false, false, past, AccountMarkMode.Automatic);
        w.Profiles.ApplyAccountEvidence(Name(6), FileProfileStore.AccountIdentityHash(Id(6)), past.AddMinutes(1),
            new string('B', 64), AccountQuality.SuspectedDegraded, "software", CialloTest.Models[0], "SYNTHETIC prior abnormal evidence");
        var recoveredMarks = new AccountMarks();
        transport.ResponseFactory = async (request, token) =>
        {
            if (Environment.CurrentManagedThreadId == uiThread) Interlocked.Increment(ref workerOnUi);
            var id = request.Headers.GetValueOrDefault("chatgpt-account-id") ?? "";
            var i = int.Parse(id["SYNTHETIC-ID-".Length..], System.Globalization.CultureInfo.InvariantCulture);
            var body = JsonNode.Parse(request.Body!)!; var model = body["model"]!.GetValue<string>();
            if (id != Id(i) || request.Headers.GetValueOrDefault("authorization") != "Bearer " + Access(i)
                || request.Proxy != Proxy(i) || body["reasoning"]?["effort"]?.ToString() != "high"
                || !CialloTest.Models.Contains(model) || !body.ToJsonString().Contains("ciallo")) Interlocked.Increment(ref identityErrors);
            var ordinal = requests.AddOrUpdate(i, 1, (_, n) => n+1);
            Max(ref accountPeak, inAccount.AddOrUpdate(i, 1, (_, n) => n+1));
            var concurrent = Interlocked.Increment(ref active); Max(ref peak, concurrent);
            if (concurrent == AccountService.AccountHealthConcurrency) firstWave.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                if (i == 0) await slow.Task.WaitAsync(token);
                // Yield without per-account wall-clock sleeps; the wave gate proves overlap.
                await Task.Yield(); token.ThrowIfCancellationRequested();
                if (i == 1) throw new AccountException(AccountError.Network, "SYNTHETIC isolated failure");
                if (i == 2) throw new FormatException("SYNTHETIC unexpected transport failure");
                if (i == 3 && ordinal == 1) w.Profiles.SetTestsFrozen(Name(i), true);
                return SafetyBufferingFixtures.Stream(i % 2 == 0 ? "false" : "missing", Name(i) + " raw reply")
                    .Replace("fixture-declared", model);
            }
            finally { Interlocked.Decrement(ref active); inAccount.AddOrUpdate(i, 0, (_, n) => n-1); }
        };
        Task<AccountHealthBatchResult>? operation = null;
        var passed = false; var slowDidNotBlock = false; var duplicateBlocked = false;
        try
        {
            operation = Task.Run(() => service.TestAccountHealthBatchAsync(new Progress(p =>
            {
                if (p.Result is not { } row) return;
                finished.AddOrUpdate(p.Profile, 1, (_, n) => n+1); results[p.Profile] = row; completeOrder.Enqueue(p.Profile);
                if (p.Profile != Name(0) && Interlocked.Increment(ref otherCount) == count-1) othersFinished.TrySetResult();
            }), stop.Token, (name, run, token) => queue.RunAsync(name, run, token)));
            await firstWave.Task.WaitAsync(TimeSpan.FromSeconds(30));
            try { await service.TestAccountHealthBatchAsync(); }
            catch (InvalidOperationException) { duplicateBlocked = true; }
            // A snapshot-frozen account remains skipped even if the user unfreezes it later.
            w.Profiles.SetTestsFrozen(Name(count-1), false);
            // An edit while requests are in flight must remain authoritative.
            w.Profiles.SetAccountMarks(Name(4), AccountQuality.SuspectedDegraded, true, true);
            var manualMarks = w.Profiles.ReadAccountMarks(Name(4));
            release.SetResult();
            await othersFinished.Task.WaitAsync(TimeSpan.FromSeconds(90));
            slowDidNotBlock = !operation.IsCompleted && !results.ContainsKey(Name(0)) && otherCount == count-1;
            slow.SetResult();
            var batch = await operation.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Yield();
            check($"{count} actual bounded transport overlap", peak > 1 && peak <= AccountService.AccountHealthConcurrency);
            check($"{count} slow first account does not block all other results", slowDidNotBlock && completeOrder.Last() == Name(0));
            check($"{count} exactly one terminal result per account", batch.Results.Count == count && results.Count == count && finished.Values.All(n => n == 1));
            check($"{count} per-account transport remains serial and at most two requests", accountPeak == 1 && requests.Values.All(n => n <= 2) && requests.Count == count-1);
            // Frozen snapshot: 0; between-model freeze and unexpected failure: 1 each; network failure: 2.
            check($"{count} exact finite request total", transport.RequestCount == 2*count-4 && !requests.ContainsKey(count-1) && requests[3] == 1 && requests[2] == 1);
            check($"{count} identity credentials route and model match", identityErrors == 0 && workerOnUi == 0);
            check($"{count} per-account failures isolated", results[Name(1)].State == "inconclusive" && results[Name(2)].State == "inconclusive"
                && results[Name(0)].State == "normal" && results[Name(7)].State == "abnormal");
            check($"{count} snapshot and dispatch freeze retained", results[Name(count-1)].State == "frozen" && results[Name(3)].State == "frozen");
            check($"{count} manual edit and recovered badge retained", w.Profiles.ReadAccountMarks(Name(4)) == manualMarks);
            check($"{count} newer evidence cannot be overwritten", w.Profiles.ReadAccountMarks(Name(5)) == newerMarks);
            recoveredMarks = w.Profiles.ReadAccountMarks(Name(6));
            check($"{count} automatic recovery retained", recoveredMarks is {Quality: AccountQuality.Normal, Recovered: true, RecoverySource: "signal"});
            check($"{count} original replies remain with matching account", batch.Results.Where(r => r.Test is not null)
                .All(r => r.Test!.Replies.Where(reply => reply.State == "completed").All(reply => reply.Text == r.Profile + " raw reply")));
            check($"{count} independent persisted marks", Enumerable.Range(7, count-8).All(i =>
                w.Profiles.ReadAccountMarks(Name(i)).Quality == (i%2 == 0 ? AccountQuality.Normal : AccountQuality.SuspectedDegraded)));
            check($"{count} duplicate batch rejected and UI queue notifications marshalled", duplicateBlocked && wrongThread == 0 && !queue.IsBusy(Name(0)));
            passed = true;
        }
        finally
        {
            stop.Cancel(); release.TrySetResult(); slow.TrySetResult();
            if (operation is not null) { try { await operation; } catch (Exception) { } }
            await w.Accounts.DisposeAsync();
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                passed, syntheticOnly = true, actualAccountRequests = 0, accounts = count,
                concurrencyLimit = AccountService.AccountHealthConcurrency, peak, accountPeak,
                requests = transport.RequestCount, results = results.Count, terminalEvents = finished.Values.Sum(),
                identityErrors, wrongThread, workerOnUi, slowDidNotBlock, duplicateBlocked,
                elapsedMilliseconds = watch.ElapsedMilliseconds,
                perAccount = Enumerable.Range(0, count).Select(i => new { profile = Name(i), requests = requests.GetValueOrDefault(i),
                    terminalEvents = finished.GetValueOrDefault(Name(i)), state = results.GetValueOrDefault(Name(i))?.State }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static async Task CancellationAsync(string directory, Action<string, bool> check)
    {
        const int count = 100;
        var w = await Task.Run(() => Fixture(directory, count)); var transport = w.Accounts!.OfflineTransport!;
        var started = Signal(); var active = 0; var peak = 0; var cancelled = 0;
        transport.ResponseFactory = async (_, token) =>
        {
            var n = Interlocked.Increment(ref active); Max(ref peak, n);
            if (n == AccountService.AccountHealthConcurrency) started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return ""; }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            finally { Interlocked.Decrement(ref active); }
        };
        var queue = new AccountTestQueue();
        var panel = new AccountHealthPanelViewModel(w.Accounts, queue, () => { });
        var passed = false;
        try
        {
            panel.Start(); await started.Task.WaitAsync(TimeSpan.FromSeconds(30)); panel.Start();
            var atCancel = transport.RequestCount; panel.CancelCommand.Execute(null);
            await panel.CurrentOperation.WaitAsync(TimeSpan.FromSeconds(30));
            check("100 cancellation stops new dispatch and cancels all in-flight requests", atCancel == AccountService.AccountHealthConcurrency
                && transport.RequestCount == atCancel && cancelled == atCancel && active == 0);
            check("100 cancellation makes every UI row and unsent model terminal", panel.Rows.Count == count
                && panel.Rows.All(r => r.Result?.State == "cancelled" && r.Models.All(m => m.Finished))
                && panel.ProgressSummary.Contains("已结束 100/100") && panel.Summary.Contains("已取消 100"));
            check("100 cancellation preserves marks and releases original queue", !panel.IsBusy && !queue.IsBusy(Name(0))
                && w.Profiles.ListAccounts().All(a => w.Profiles.ReadAccountMarks(a.Name).Quality == AccountQuality.Unmarked));
            passed = true;
        }
        finally
        {
            panel.CancelCommand.Execute(null); await panel.CurrentOperation; await w.Accounts.DisposeAsync();
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            { passed, accounts = count, peak, requests = transport.RequestCount, cancelled, active,
                terminalRows = panel.Rows.Count(r => r.Finished), syntheticOnly = true, actualAccountRequests = 0 }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
