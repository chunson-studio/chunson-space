using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using FAswitch.Accounts;

internal static class BrowserTimezoneTargetChecks
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS " + label); }
        foreach (var scenario in new[] { "stable", "removed", "removed-after-override", "live-error", "invalid-target-list", "disconnected", "timezone-mismatch" })
        {
            var connection = new FixtureConnection(scenario);
            var firstPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? failure = null;
            try { await BrowserTimezoneController.ApplyTargetsAsync(connection, "America/New_York", firstPage, default); }
            catch (Exception error) { failure = error; }
            var canContinue = scenario is "stable" or "removed" or "removed-after-override";
            Check(firstPage.Task.IsCompletedSuccessfully, "initial browser page confirmed before " + scenario);
            Check((failure is null) == canContinue && connection.Calls.Contains("third:Emulation.setTimezoneOverride") == canContinue,
                "timezone controller only continues after confirmed target removal: " + scenario);
            Check(connection.Calls.Contains("second:Runtime.runIfWaitingForDebugger"), "affected target is never left intentionally paused: " + scenario);
            if (scenario == "timezone-mismatch")
                Check(failure is IOException && !connection.Calls.Contains(":Target.getTargets"), "actual timezone mismatch is not treated as a disappeared target");
            if (scenario is "removed" or "removed-after-override")
                Check(connection.Calls.Contains(":Target.getTargets"), "browser confirms removal before skipping target: " + scenario);
        }
        return count;
    }

    private sealed class FixtureConnection(string scenario) : ICdpConnection
    {
        public List<string> Calls { get; } = [];
        public Task<JsonObject> CallAsync(string method, JsonObject parameters, string? sessionId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(sessionId + ":" + method);
            if (method == "Target.getTargets")
            {
                if (scenario == "disconnected") throw new AccountException(AccountError.Browser, "Fixture connection closed");
                if (scenario == "invalid-target-list") return Task.FromResult(new JsonObject());
                var targets = new JsonArray(new JsonObject { ["targetId"] = "first" }, new JsonObject { ["targetId"] = "third" });
                if (scenario == "live-error") targets.Add(new JsonObject { ["targetId"] = "second" });
                return Task.FromResult(new JsonObject { ["targetInfos"] = targets });
            }
            if (sessionId == "second" && scenario is not ("stable" or "timezone-mismatch")
                && (scenario != "removed-after-override" || method == "Target.setAutoAttach"))
                throw new AccountException(AccountError.Browser, "CDP 未接受操作。");
            return Task.FromResult(method == "Runtime.evaluate"
                ? new JsonObject { ["result"] = new JsonObject { ["value"] = sessionId == "second" && scenario == "timezone-mismatch" ? "Europe/London" : "America/New_York" } }
                : new JsonObject());
        }
        public async IAsyncEnumerable<JsonObject> Events([EnumeratorCancellation] CancellationToken token)
        {
            foreach (var target in new[] { "first", "second", "third" })
            {
                token.ThrowIfCancellationRequested(); await Task.Yield();
                yield return new JsonObject { ["method"] = "Target.attachedToTarget", ["params"] = new JsonObject {
                    ["sessionId"] = target, ["targetInfo"] = new JsonObject { ["targetId"] = target, ["type"] = "page" } } };
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
