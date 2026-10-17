using System.Text.Json.Nodes;
using System.Threading.Channels;
using FAswitch.Windows;

internal static class ManagedRuntimeCdpChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunsonSwitch-RuntimeCdpChecks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var main = new ProcessIdentity(101, 1, "Codex.exe", @"C:\OpenAI\Codex\Codex.exe", 100, true);
        var clone = main with { Pid = 102, CreationTime = 200 };
        var system = new FakeProcessSystem([main, clone]) { AllowStop = true };
        var store = new FakeOwnedStore(); var scope = new ManagedCloneProcessScope(system, store); scope.RegisterRoot(clone, "fixture");
        var factory = new Factory(); factory.Connection.ContextChangesRemaining = 1;
        await using var inspector = new ManagedCodexRuntimeInspector(new FakeCommands { Response = new(0, "[102]") }, factory);
        var options = new ManagedCloneLaunchOptions(Path.Combine(root, "home"), root, scope, RuntimeDiagnosticPath: Path.Combine(root, "inspection.json"));
        var request = new DesktopLaunchRequest(null, [], new Dictionary<string, string> { ["TZ"] = "Asia/Tokyo" }, ExpectedEgressIp: "203.0.113.7");
        File.WriteAllText(Path.Combine(root, "DevToolsActivePort"), "12345\n/devtools/browser/fixture\n");
        try
        {
            var result = await inspector.InspectAsync(options, request, default);
            check(result.Verified, "managed CDP applies timezone and English instead of relying on Windows TZ environment");
            var rpc = factory.Connection;
            check(rpc.ContextChangesRemaining == 0 && rpc.EnvironmentReads >= 2, "initial page navigation context replacement is retried before deciding failure");
            check(rpc.Calls.FindIndex(call => call.Method == "Emulation.setTimezoneOverride" && call.Session == "app-session") < rpc.Calls.FindIndex(call => call.Method == "Runtime.evaluate" && call.Session == "app-session"), "app timezone override precedes actual Intl verification");
            check(rpc.Calls.All(call => !call.Expression.Contains("fetch(") && call.Method is not ("Target.createTarget" or "Page.navigate")), "runtime inspection performs no second public IP query or probe-page creation");
            check(rpc.Configures.GetValueOrDefault("app-session") == 1 && system.StopCalls.Count == 0, "auto attach duplicate events cannot reconfigure or stop confirmed clone");
            check(rpc.Calls.Any(call => call.Method == "Runtime.runIfWaitingForDebugger" && call.Session == "duplicate-app-session"), "duplicate auto attachment resumes its waiting session instead of leaving the page paused");
            check(!rpc.Disposed && result.Message.Contains("启动前已核对") && result.Message.Contains("启动后不重复查询"), "runtime connection remains owned and explicitly reuses prelaunch exit evidence");
            check(rpc.Calls.Where(call => call.Method == "Target.setAutoAttach").All(call => !call.Pauses), "runtime tracking never pauses new app or overlay pages for its own verification");
            rpc.Emit("new-page", "new-page-session");
            await rpc.NewPageConfigured.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(rpc.Configures.GetValueOrDefault("new-page-session") == 1 && rpc.Calls.Any(call => call.Session == "new-page-session" && call.Method == "Emulation.setLocaleOverride"), "new clone page receives timezone and English before continuing");
            rpc.Emit("closed-page", "closed-page-session", exists: false);
            rpc.Emit("sentinel", "sentinel-session");
            await rpc.SentinelConfigured.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(system.StopCalls.Count == 0 && !rpc.Disposed, "page already closed by the app is ignored only after checking the live target list");
            rpc.RejectNextPage = true; rpc.Emit("bad-page", "bad-page-session");
            rpc.Emit("followup", "followup-session");
            await rpc.FollowupConfigured.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(system.Items.Contains(main) && system.Items.Contains(clone) && system.StopCalls.Count == 0 && !rpc.Disposed, "a future page protocol failure keeps a confirmed clone running and continues tracking");
            var diagnostic = JsonNode.Parse(File.ReadAllText(options.RuntimeDiagnosticPath!));
            check(diagnostic?["verified"]?.GetValue<bool>() == false && diagnostic?["message"]?.GetValue<string>().Contains("设置分身页面时区和英语") == true && diagnostic.ToJsonString().IndexOf("fixture-token", StringComparison.Ordinal) < 0, "runtime failure retains a sanitized stage diagnostic outside disposable launch state");
            check(diagnostic?["method"]?.GetValue<string>() == "Emulation.setTimezoneOverride" && diagnostic?["code"]?.GetValue<int>() == -32602,
                "background failure identifies the exact protocol method and code without raw personal error text");
            foreach (var mode in new[] { "typed-timeout", "cancelled-call" })
            {
                var timeoutSystem = new FakeProcessSystem([main, clone]) { AllowStop = true };
                var timeoutScope = new ManagedCloneProcessScope(timeoutSystem, new FakeOwnedStore()); timeoutScope.RegisterRoot(clone, "fixture");
                var timeoutFactory = new Factory(); timeoutFactory.Connection.TimeoutMode = mode;
                await using var timeoutInspector = new ManagedCodexRuntimeInspector(new FakeCommands { Response = new(0, "[102]") }, timeoutFactory);
                check((await timeoutInspector.InspectAsync(options with { ProcessScope = timeoutScope }, request, default)).Verified, "primary launch passes before background timeout: " + mode);
                timeoutFactory.Connection.Emit("timeout", "timeout-session"); timeoutFactory.Connection.Emit("followup", "followup-session");
                await timeoutFactory.Connection.FollowupConfigured.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var timeoutDiagnostic = JsonNode.Parse(File.ReadAllText(options.RuntimeDiagnosticPath!));
                check(timeoutSystem.StopCalls.Count == 0 && timeoutSystem.Items.Contains(clone) && !timeoutFactory.Connection.Disposed
                    && timeoutDiagnostic?["verified"]?.GetValue<bool>() == false && timeoutDiagnostic?["message"]?.GetValue<string>().Contains("分身保持运行") == true,
                    "background timeout is reported as incomplete and cannot close the confirmed window: " + mode);
                if (mode == "typed-timeout") check(timeoutDiagnostic?["method"]?.GetValue<string>() == "Emulation.setLocaleOverride", "command timeout records its method without protocol payloads");
                timeoutFactory.Connection.EndEvents(); await timeoutFactory.Connection.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));
                check(timeoutSystem.StopCalls.Count == 0 && timeoutSystem.Items.Contains(clone), "lost monitor connection is not proof of mismatched environment and cannot kill the clone");
            }
            foreach (var sharedMethod in new[] { "Emulation.setTimezoneOverride", "Emulation.setLocaleOverride", "both" })
            foreach (var wrongValue in new[] { false, true })
            {
                var sharedSystem = new FakeProcessSystem([main, clone]) { AllowStop = true };
                var sharedScope = new ManagedCloneProcessScope(sharedSystem, new FakeOwnedStore()); sharedScope.RegisterRoot(clone, "fixture");
                var sharedFactory = new Factory(); sharedFactory.Connection.SharedMethod = sharedMethod;
                sharedFactory.Connection.WrongSharedValue = wrongValue;
                await using var sharedInspector = new ManagedCodexRuntimeInspector(new FakeCommands { Response = new(0, "[102]") }, sharedFactory);
                check((await sharedInspector.InspectAsync(options with { ProcessScope = sharedScope }, request, default)).Verified,
                    "primary page is confirmed before shared renderer fixture " + sharedMethod);
                sharedFactory.Connection.Emit("new-page", "new-page-session");
                if (wrongValue)
                {
                    await sharedFactory.Connection.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    check(sharedSystem.Items.Contains(main) && !sharedSystem.Items.Contains(clone), "shared override reply cannot admit a different actual timezone or Intl locale: " + sharedMethod);
                }
                else
                {
                    sharedFactory.Connection.Emit("sentinel", "sentinel-session");
                    await sharedFactory.Connection.SentinelConfigured.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    check(sharedSystem.StopCalls.Count == 0 && !sharedFactory.Connection.Disposed
                        && sharedFactory.Connection.Calls.Any(call => call.Method == "Runtime.evaluate" && call.Session == "new-page-session"),
                        "shared renderer override is accepted only after actual page readback, keeping the clone open: " + sharedMethod);
                }
            }
            check(!new ManagedRuntimeCdpException("Network.enable", -32000, "Another locale override is already in effect").SharedOverride
                && !new ManagedRuntimeCdpException("Emulation.setLocaleOverride", -32602, "Another locale override is already in effect").SharedOverride
                && !new ManagedRuntimeCdpException("Emulation.setLocaleOverride", -32000, "Invalid locale name").SharedOverride,
                "shared override exception handling excludes other methods, codes and real invalid parameters");
            foreach (var wrongZone in new[] { false, true })
            {
                var aliasSystem = new FakeProcessSystem([main, clone]) { AllowStop = true };
                var aliasStore = new FakeOwnedStore(); var aliasScope = new ManagedCloneProcessScope(aliasSystem, aliasStore); aliasScope.RegisterRoot(clone, "fixture");
                var aliasFactory = new Factory("Asia/Kolkata", wrongZone ? "Asia/Colombo" : null);
                await using var aliasInspector = new ManagedCodexRuntimeInspector(new FakeCommands { Response = new(0, "[102]") }, aliasFactory);
                var aliasResult = await aliasInspector.InspectAsync(options with { ProcessScope = aliasScope }, request with { Environment = new Dictionary<string, string> { ["TZ"] = "Asia/Kolkata" } }, default);
                check(aliasResult.Verified == !wrongZone, wrongZone ? "managed CDP rejects different IANA zone with same current offset" : "managed CDP accepts alias normalized to canonical Intl name");
                check(aliasFactory.Connection.Calls.Where(call => call.Expression.Contains("JSON.stringify({timezone")).All(call => call.Expression.Contains("expectedTimezone:new Intl.DateTimeFormat") && call.Expression.Contains("Asia/Kolkata")), "managed CDP canonicalizes requested timezone in each page Intl environment");
                if (!wrongZone)
                {
                    aliasFactory.Connection.Emit("new-page", "new-page-session");
                    await aliasFactory.Connection.NewPageConfigured.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    check(aliasSystem.StopCalls.Count == 0, "alias-normalized future page does not stop owned clone");
                }
            }
            check(!BrowserEnvironmentVerification.TimezoneMatches(new JsonObject { ["timezone"] = "Asia/Calcutta" }) && !BrowserEnvironmentVerification.TimezoneMatches(new JsonObject { ["timezone"] = "", ["expectedTimezone"] = "" }), "missing canonical target or timezone cannot pass runtime verification");
            var missingExit = await inspector.InspectAsync(options, request with { ExpectedEgressIp = "" }, default);
            check(!missingExit.Verified, "removing duplicate queries does not admit a launch without preverified exit evidence");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Factory(string timezone = "Asia/Tokyo", string? actualTimezone = null) : IManagedRuntimeCdpFactory
    {
        public Rpc Connection { get; } = new(timezone, actualTimezone);
        public Task<IManagedRuntimeCdp> ConnectAsync(Uri endpoint, CancellationToken token) => Task.FromResult<IManagedRuntimeCdp>(Connection);
    }
    private sealed class Rpc(string timezone, string? actualTimezone) : IManagedRuntimeCdp
    {
        private readonly Channel<JsonObject> events = Channel.CreateUnbounded<JsonObject>();
        public List<(string Method, string? Session, string Expression, string Target, bool Pauses)> Calls { get; } = [];
        public Dictionary<string, int> Configures { get; } = [];
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> targets = new([new KeyValuePair<string, byte>("app", 0)]);
        public int ContextChangesRemaining { get; set; }
        public int EnvironmentReads { get; private set; }
        public bool RejectNextPage { get; set; }
        public string SharedMethod { get; set; } = "";
        public bool WrongSharedValue { get; set; }
        public string TimeoutMode { get; set; } = "";
        public bool Disposed { get; private set; }
        public TaskCompletionSource<bool> NewPageConfigured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SentinelConfigured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FollowupConfigured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> DisposedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(string target, string session, bool exists = true)
        {
            if (exists) targets.TryAdd(target, 0);
            events.Writer.TryWrite(new() { ["method"] = "Target.attachedToTarget", ["params"] = new JsonObject { ["sessionId"] = session, ["targetInfo"] = new JsonObject { ["type"] = "page", ["targetId"] = target } } });
        }
        public Task<JsonObject> CallAsync(string method, JsonObject parameters, string? session, CancellationToken token)
        {
            var expression = parameters["expression"]?.GetValue<string>() ?? ""; var target = parameters["targetId"]?.GetValue<string>() ?? "";
            Calls.Add((method, session, expression, target, parameters["waitForDebuggerOnStart"]?.GetValue<bool>() ?? false));
            if (session == "timeout-session" && method == "Emulation.setLocaleOverride")
            {
                if (TimeoutMode == "typed-timeout") throw new ManagedRuntimeCdpTimeoutException(method);
                if (TimeoutMode == "cancelled-call") throw new TaskCanceledException();
            }
            if (method == "Emulation.setTimezoneOverride")
            {
                if (RejectNextPage && session == "bad-page-session") throw new ManagedRuntimeCdpException(method, -32602, "fixture-token override refused");
                if (session == "closed-page-session") throw new ManagedRuntimeCdpException(method, -32000, "Session closed");
                Configures[session!] = Configures.GetValueOrDefault(session!) + 1;
            }
            if (session == "new-page-session" && SharedMethod.Length > 0 && (SharedMethod == method || SharedMethod == "both")
                && method is "Emulation.setTimezoneOverride" or "Emulation.setLocaleOverride")
                throw new ManagedRuntimeCdpException(method, -32000, method == "Emulation.setTimezoneOverride" ? "Timezone override is already in effect" : "Another locale override is already in effect");
            if (method == "Target.setAutoAttach" && session is null) Emit("app", "duplicate-app-session");
            if (method == "Runtime.evaluate" && !expression.Contains("fetch("))
            {
                EnvironmentReads++;
                if (session == "app-session" && ContextChangesRemaining > 0) { ContextChangesRemaining--; throw new ManagedRuntimeCdpException(method, -32000, "Execution context was destroyed."); }
            }
            if (method is "Page.navigate" or "Target.createTarget") throw new IOException("runtime must not perform another egress query");
            if (expression.Contains("fetch(")) throw new IOException("fixture CSP blocks cross-origin fetch");
            JsonObject Value(string value) => new() { ["result"] = new JsonObject { ["value"] = value } };
            JsonObject result = method switch
            {
                "Browser.getVersion" => new() { ["userAgent"] = "fixture-codex" },
                "Target.getTargets" => new() { ["targetInfos"] = new JsonArray(targets.Keys.Select(id => (JsonNode)new JsonObject { ["type"] = "page", ["targetId"] = id }).ToArray()) },
                "Target.attachToTarget" => new() { ["sessionId"] = target + "-session" },
                "Runtime.evaluate" => Value(new JsonObject
                {
                    ["timezone"] = session == "new-page-session" && WrongSharedValue && SharedMethod != "Emulation.setLocaleOverride" ? "Europe/London"
                        : Configures.ContainsKey(session!) ? actualTimezone ?? (timezone == "Asia/Kolkata" ? "Asia/Calcutta" : timezone) : "America/Chicago",
                    ["expectedTimezone"] = timezone == "Asia/Kolkata" ? "Asia/Calcutta" : timezone, ["language"] = "en-US",
                    ["locale"] = session == "new-page-session" && WrongSharedValue && SharedMethod == "Emulation.setLocaleOverride" ? "zh-CN" : "en-US"
                }.ToJsonString()),
                _ => new()
            };
            if (method == "Runtime.evaluate" && session == "new-page-session") NewPageConfigured.TrySetResult(true);
            if (method == "Target.setAutoAttach" && session == "sentinel-session") SentinelConfigured.TrySetResult(true);
            if (method == "Target.setAutoAttach" && session == "followup-session") FollowupConfigured.TrySetResult(true);
            return Task.FromResult(result);
        }
        public void EndEvents() => events.Writer.TryComplete();
        public IAsyncEnumerable<JsonObject> Events(CancellationToken token) => events.Reader.ReadAllAsync(token);
        public ValueTask DisposeAsync() { Disposed = true; events.Writer.TryComplete(); DisposedSignal.TrySetResult(true); return ValueTask.CompletedTask; }
    }
}
