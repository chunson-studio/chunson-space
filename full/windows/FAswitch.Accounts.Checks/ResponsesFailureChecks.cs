using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using FAswitch.Accounts;

internal static class ResponsesFailureChecks
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS " + label); }
        var repository = new AccountRepository(root);
        repository.Profiles.CreateBoundAccount("Fixture", new("http://127.0.0.1:7890"));
        repository.Profiles.SaveAccount("Fixture", new JsonObject { ["tokens"] = new JsonObject { ["access_token"] = "fixture-access", ["account_id"] = "fixture-id" } }, overwrite: true);
        var originalAuth = File.ReadAllBytes(Path.Combine(repository.AccountPath("Fixture"), "auth.json"));

        foreach (var phase in new[] { "headers", "error-body", "event-stream" })
        {
            foreach (var cancelByUser in new[] { false, true })
            {
                var transport = new FixtureTransport(phase);
                var service = new AccountService(repository, transport);
                using var cancellation = new CancellationTokenSource();
                if (cancelByUser) cancellation.CancelAfter(30);
                Exception? failure = null;
                try { await ReadWithDeadline(service, repository, cancelByUser ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(30), cancellation.Token); }
                catch (Exception error) { failure = error; }
                Check(cancelByUser ? failure is OperationCanceledException : failure is AccountException { Code: AccountError.Timeout },
                    "response " + phase + " distinguishes " + (cancelByUser ? "user cancellation" : "request deadline"));
                Check(transport.Requests == 1 && (phase == "headers" || transport.Body?.Disposed == true), "failed response releases resources without retry: " + phase + "/" + cancelByUser);
            }
        }

        var cialloTransport = new FixtureTransport("ciallo-failures");
        var ciallo = new AccountService(repository, cialloTransport);
        var result = await ciallo.TestCialloAsync("Fixture");
        Check(!result.Cancelled && result.Replies.Count == CialloTest.Models.Count && result.Replies.Take(3).All(reply => reply.State == "failed" && reply.Text.Contains("超时")),
            "Ciallo retains header, error-body and event-stream cancellations as failed replies when caller did not cancel");
        Check(result.Completed == CialloTest.Models.Count - 3 && cialloTransport.Requests == CialloTest.Models.Count,
            "Ciallo continues remaining models without retrying failed requests");
        var history = ciallo.CialloHistory("Fixture").Single();
        Check(history.Finished && !history.Result.Cancelled && history.Result.Replies.Take(3).All(reply => reply.State == "failed"),
            "Ciallo failed replies and final status are persisted to history");

        var pendingTransport = new FixtureTransport("headers");
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = new AccountService(repository, pendingTransport).TestCialloAsync("Fixture", token: cancelled.Token);
            await pendingTransport.Entered.Task;
            Check(!pending.IsCompleted, "Ciallo remains pending while awaiting the response until explicitly cancelled");
            cancelled.Cancel();
            var stopped = await pending;
            Check(stopped.Cancelled && stopped.Replies.Count == 1 && stopped.Replies[0].State == "cancelled" && pendingTransport.Requests == 1,
                "explicit Ciallo cancellation saves current state and does not request another model");
        }
        Check(originalAuth.SequenceEqual(File.ReadAllBytes(Path.Combine(repository.AccountPath("Fixture"), "auth.json"))), "response failure checks preserve authorization bytes");
        return count;
    }

    // Exercise the existing per-call deadline without adding a production setting.
    private static Task ReadWithDeadline(AccountService service, AccountRepository repository, TimeSpan timeout, CancellationToken token)
    {
        var sessionType = typeof(AccountService).GetNestedType("AuthSession", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(sessionType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object[] { repository.ReadAuth("Fixture"), repository.Proxy("Fixture") }, null)!;
        var method = typeof(AccountService).GetMethod("ReadResponses", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(service, new object?[] { "Fixture", "fixture-model", session, token, "ciallo", "fixture", "high", null, timeout, false })!;
    }

    private sealed class FixtureTransport(string mode) : IAccountTransport
    {
        public int Requests { get; private set; }
        public PendingStream? Body { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken token)
        {
            Requests++; Entered.TrySetResult();
            if (mode == "headers") { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException("unreachable"); }
            if (mode == "ciallo-failures")
            {
                if (Requests == 1) throw new OperationCanceledException("Fixture upstream send cancellation");
                if (Requests <= 3) { Body = new(true); return new(Requests == 2 ? 503 : 200, Body); }
                return new(200, new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"response.completed\",\"response\":{\"model\":\"fixture-model\",\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"fixture response\"}]}]}}\n\n")));
            }
            Body = new(false); return new(mode == "error-body" ? 503 : 200, Body);
        }
    }
    private sealed class PendingStream(bool cancelWithoutCaller) : MemoryStream
    {
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancelWithoutCaller) throw new OperationCanceledException("Fixture upstream read cancellation");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
