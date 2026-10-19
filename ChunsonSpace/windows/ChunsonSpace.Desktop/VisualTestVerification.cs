using System.IO;
using System.Windows;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Threading;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

internal static class VisualTestVerification
{
    public static async Task RunAsync(MainWindow window, string directory, Action<string, bool> check, Action<string> capture)
    {
        var root = Path.Combine(directory, "accounts"); var home = Path.Combine(directory, "home");
        Directory.CreateDirectory(root); Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(root, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var store = new FileProfileStore(root);
        foreach (var (name, port) in new[] { ("测试甲", 7901), ("测试乙", 7902), ("测试丙", 7903) })
        {
            store.CreateBoundAccount(name, new($"http://127.0.0.1:{port}"));
            store.SaveAccount(name, new JsonObject { ["tokens"] = new JsonObject { ["access_token"] = "fixture-access-" + name, ["account_id"] = "fixture-id-" + name } }, overwrite: true);
        }
        store.CreateBoundAccount("未授权", new("http://127.0.0.1:7904"));
        File.WriteAllText(Path.Combine(home, "auth.json"), "{\"fixture\":\"primary-preserved\"}");
        var originals = Directory.GetFiles(root, "auth.json", SearchOption.AllDirectories).Append(Path.Combine(home, "auth.json")).ToDictionary(p => p, File.ReadAllBytes);
        var workspace = new DesktopWorkspace(root, home, Path.Combine(directory, "desktop"), Path.Combine(directory, "web"), fixture: true);
        var model = new ShellViewModel(workspace); window.DataContext = model;
        model.Selected = model.FilteredItems.Single(item => item.Name == "测试甲");
        var runtime = workspace.Accounts!.VisualRuntimeFixture!;
        void Flush() => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        async Task Until(Func<bool> predicate) { for (var i = 0; i < 1000 && !predicate(); i++) await Task.Delay(1); if (!predicate()) throw new InvalidOperationException("Visual fixture stalled."); Flush(); }
        void Open(VisualTestKind kind) { model.OpenModelTestCommand.Execute(null); model.ChooseVisualTestCommand.Execute(kind.ToString()); Flush(); }
        Open(VisualTestKind.Revision); var panel = model.VisualTests!;
        check("visual opening excludes unsaved accounts and sends no requests", panel.Profiles.Count == 3 && runtime.Requests.Count == 0);
        check("model and effort selectors are visible at minimum size", UiVerification.FullyVisible((ComboBox)window.FindName("VisualTestModelPicker"), window.RenderRoot) && ((ComboBox)window.FindName("VisualTestEffortPicker")).Visibility == Visibility.Collapsed);
        capture("visual-revision-ready-minimum");
        var start = (Button)window.FindName("VisualTestStartButton"); var all = (Button)window.FindName("VisualTestStartAllButton");
        check("visual fixed start controls visible without overlap", UiVerification.FullyVisible(start, window.RenderRoot) && UiVerification.FullyVisible(all, window.RenderRoot) && UiVerification.DoNotOverlap(start, all, window.RenderRoot));
        var active = new Dictionary<string, int>(); var counts = new Dictionary<string, int>(); int maxAccount = 0, maxAll = 0;
        TaskCompletionSource? gate = new(TaskCreationOptions.RunContinuationsAsynchronously); string? fail = null, free = null;
        runtime.ReplyFactory = async (profile, prompt, token) =>
        {
            counts[profile] = counts.GetValueOrDefault(profile) + 1; active[profile] = active.GetValueOrDefault(profile) + 1;
            maxAccount = Math.Max(maxAccount, active[profile]); maxAll = Math.Max(maxAll, active.Values.Sum());
            try
            {
                if (gate is not null && profile != free) await gate.Task.WaitAsync(token);
                await Task.Yield(); token.ThrowIfCancellationRequested();
                if (profile == fail) throw new IOException("fixture account connection failed");
                return prompt.StartsWith("Inspect") ? "脚踏连接不清晰，补全接触点。" : OfflineVisualTestRuntime.Svg;
            }
            finally { active[profile]--; }
        };
        panel.StartAllCommand.Execute(null); await Until(() => runtime.Requests.Count == 3);
        check("visual accounts run concurrently with one request each", maxAccount == 1 && maxAll == 3 && counts.Values.All(v => v == 1));
        check("visual running can hide but cannot start duplicate or change account", !panel.StartCommand.CanExecute(null) && panel.CloseCommand.CanExecute(null) && !panel.CanEdit && panel.CancelCommand.CanExecute(null));
        await Until(() => panel.Status.Contains("第 1/3 步"));
        check("running tests show actual step and zero replies before first response", panel.Summary.StartsWith("进行中") && panel.CompletedReplies == 0 && panel.ExpectedReplies == 9 && panel.ProgressSummary.Contains("本轮用时"));
        check("live progress stays visible above the result scroll", UiVerification.FullyVisible((TextBlock)window.FindName("VisualTestProgressText"), window.RenderRoot) && UiVerification.FullyVisible((ProgressBar)window.FindName("VisualTestReplyProgress"), window.RenderRoot));
        capture("visual-progress-running-minimum");
        check("visual overlay leaves native minimize reachable", UiVerification.ReceivesInput((Button)window.FindName("MinimizeButton"), window.RenderRoot));
        panel.CloseCommand.Execute(null); Flush();
        var runningRequests = runtime.Requests.Count;
        model.NavigateCommand.Execute("Accounts"); Flush();
        var firstTime = panel.ProgressSummary; var firstDetail = panel.BatchRows[0].Detail; var ticks = 0;
        panel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(panel.ProgressSummary)) ticks++; };
        await Task.Delay(1250); Flush();
        check("hidden waiting request updates elapsed time without more requests", ticks > 0 && panel.ProgressSummary != firstTime && panel.BatchRows[0].Detail != firstDetail && panel.CompletedReplies == 0 && runtime.Requests.Count == runningRequests && model.BackgroundModelStatus.Contains(panel.ProgressSummary));
        check("hidden visual task keeps requests alive and permits page navigation", !model.IsVisualTestsOpen && panel.IsBusy && !panel.CurrentOperation.IsCompleted
            && model.NavigateCommand.CanExecute("Proxy") && model.IsAccountsPage && model.BackgroundModelTitle.Contains("进行中"));
        model.Selected = model.FilteredItems.Single(item => item.Name == "未授权");
        check("an account outside the test batch keeps its settings available", model.EditSelectedCommand.CanExecute(null));
        model.Selected = model.FilteredItems.Single(item => item.Name == "测试甲");
        check("accounts inside the running batch cannot be edited or deleted", !model.EditSelectedCommand.CanExecute(null) && !model.ArchiveSelectedCommand.CanExecute(null));
        var navigation = (ScrollViewer)window.FindName("NavigationScroll");
        navigation.ScrollToBottom(); Flush();
        check("minimum navigation scroll keeps settings reachable above background progress", UiVerification.FullyVisible((Button)window.FindName("NavSettings"), navigation) && UiVerification.ReceivesInput((Button)window.FindName("NavSettings"), window.RenderRoot));
        capture("background-navigation-scrolled-minimum"); navigation.ScrollToTop(); Flush();
        capture("background-visual-light-minimum");
        model.DarkTheme = true; capture("background-visual-dark-minimum"); model.DarkTheme = false;
        check("background model progress is visible at minimum size", UiVerification.FullyVisible((Button)window.FindName("BackgroundModelButton"), window.RenderRoot));
        window.WindowState = System.Windows.WindowState.Minimized; Flush();
        check("minimizing does not cancel or duplicate a visual request", panel.IsBusy && !panel.CurrentOperation.IsCompleted && runtime.Requests.Count == runningRequests);
        window.WindowState = System.Windows.WindowState.Normal; Flush();
        model.OpenCialloCommand.Execute(null); Flush();
        check("another model entry opens independently without restarting the visual task", model.IsCialloOpen && panel.IsBusy && runtime.Requests.Count == runningRequests);
        window.HandleShortcut(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
        model.OpenBackgroundModelCommand.Execute(null); Flush();
        if (!ReferenceEquals(panel, model.VisualTests)) { model.OpenBackgroundModelCommand.Execute(null); Flush(); }
        check("Escape and progress entry retain the visual task and cancellation handle", ReferenceEquals(panel, model.VisualTests) && panel.CancelCommand.CanExecute(null) && runtime.Requests.Count == runningRequests);
        panel.CloseCommand.Execute(null);
        gate.SetResult(); await panel.CurrentOperation; gate = null; Flush();
        check("hidden visual completion updates status without reopening", !model.IsVisualTestsOpen && !model.IsModelTestRunning && !model.IsBusy && model.BackgroundModelTitle.Contains("查看结果"));
        model.OpenBackgroundModelCommand.Execute(null); Flush();
        check("revision completes three stages for each account", runtime.Requests.Count == 9 && panel.BatchRows.All(r => r.Result!.Completed && r.Result.Steps.Count == 3) && maxAccount == 1);
        var finishedTime = panel.ProgressSummary; ticks = 0; await Task.Delay(1250); Flush();
        check("completed run freezes its timer and counts only actual replies", ticks == 0 && panel.ProgressSummary == finishedTime && panel.CompletedReplies == 9 && panel.BatchRows.All(r => r.IsFinished) && panel.Result!.ElapsedSeconds >= 1);
        check("revision uses actual rendered image for critique and original plus critique for revision", runtime.Requests.Where(r => r.Prompt.StartsWith("Inspect")).All(r => r.Images.Count == 1 && File.Exists(r.Images[0]))
            && runtime.Requests.Where(r => r.Prompt.StartsWith("Revise")).All(r => r.Prompt.Contains(OfflineVisualTestRuntime.Svg) && r.Prompt.Contains("脚踏连接")));
        check("all accounts share selected model and initial task", runtime.Requests.All(r => r.Model == "gpt-6-astra") && runtime.Requests.Where(r => r.Prompt.StartsWith("Generate")).Select(r => r.Prompt).Distinct().Count() == 1);
        check("visual result contains usage and before/after files", panel.Result!.Steps.All(s => s.CachedTokens == 4864) && panel.Steps.Count == 3 && panel.Steps[0].OpenCommand.CanExecute(null) && panel.Steps[2].HasThumbnail);
        capture("visual-results-minimum"); model.DarkTheme = true; capture("visual-results-dark-minimum"); model.DarkTheme = false;
        var quick = (Border)window.FindName("VisualTestQuickResult"); var rows = (Border)window.FindName("VisualTestBatchResults");
        check("visual summary and account outcomes stay visible outside scroll", UiVerification.FullyVisible(quick, window.RenderRoot) && UiVerification.FullyVisible(rows, window.RenderRoot) && UiVerification.DoNotOverlap(quick, rows, window.RenderRoot));
        var before = runtime.Requests.Count;
        var restored = new VisualTestPanelViewModel(workspace.Accounts, panel.Profiles[0], VisualTestKind.Revision, _ => {}, () => {}, panel.Profiles);
        check("visual cached results reload in a new panel without requests", restored.Result?.Completed == true && restored.Steps.Count == 3 && runtime.Requests.Count == before);
        check("historical elapsed time survives reload without a live timer", restored.Result?.ElapsedSeconds >= 1 && restored.ProgressSummary.StartsWith("已记录用时"));
        restored.Model = "gpt-6.1-sol";
        check("visual cache is scoped by model", restored.Result is null && runtime.Requests.Count == before);
        restored.Model = "invalid model";
        check("invalid old model selection returns to supported default without requests", restored.Model == "gpt-6-astra"
            && restored.Effort == "high" && runtime.Requests.Count == before);
        counts.Clear(); fail = panel.Profiles[0]; panel.StartAllCommand.Execute(null); await panel.CurrentOperation; Flush();
        check("visual failed account stops only itself", runtime.Requests.Count == before + 7 && counts[fail] == 1 && panel.BatchRows.Count(r => r.Result?.Completed == true) == 2);
        counts.Clear(); fail = null; free = panel.Profiles[0]; gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        before = runtime.Requests.Count; panel.StartAllCommand.Execute(null); await Until(() => panel.BatchRows[0].Result?.Completed == true && runtime.Requests.Count == before + 5);
        panel.CancelCommand.Execute(null); await panel.CurrentOperation; Flush(); gate = null;
        check("visual cancellation retains complete account and cancels other accounts", runtime.Requests.Count == before + 5 && panel.BatchRows[0].Result!.Completed && panel.BatchRows.Skip(1).All(r => r.Result!.Cancelled) && active.Values.All(v => v == 0));
        capture("visual-cancelled-minimum");
        panel.CloseCommand.Execute(null); Open(VisualTestKind.Variation); panel = model.VisualTests!;
        runtime.ReplyFactory = null; before = runtime.Requests.Count;
        panel.StartCommand.Execute(null); await panel.CurrentOperation; Flush();
        check("variation makes one SVG request and retains saved result", runtime.Requests.Count == before + 1 && panel.Result!.Completed && panel.Result.Steps[0].Artifact!.EndsWith(".svg"));
        var firstVariant = panel.DrawingContent; panel.DrawingContent = "second fixture"; check("variant cache separates different prompts", panel.Result is null);
        panel.DrawingContent = firstVariant; check("returning to variant reuses cached result", panel.Result?.Completed == true && runtime.Requests.Count == before + 1);
        capture("visual-variation-minimum"); panel.CloseCommand.Execute(null); Open(VisualTestKind.Static); panel = model.VisualTests!;
        before = runtime.Requests.Count; panel.StartCommand.Execute(null); await panel.CurrentOperation; Flush();
        check("static pelican uses one SVG request with distinct persisted result", panel.Result!.Completed && panel.Kind == VisualTestKind.Static && runtime.Requests.Count == before + 1 && panel.Result.Steps[0].Artifact!.EndsWith(".svg"));
        check("saved gallery and preview are available without new requests", panel.OpenGalleryCommand.CanExecute(null) && File.ReadAllText(Path.Combine(panel.Result!.Directory, "gallery.html")).Contains("sandbox='allow-scripts'"));
        capture("visual-static-default-minimum");
        
        check("animation toggle changes prompt and cache without sending request", panel.Kind == VisualTestKind.Animation && panel.Result is null && runtime.Requests.Count == before + 1);
        before = runtime.Requests.Count; panel.StartCommand.Execute(null); await panel.CurrentOperation; Flush();
        check("animation makes one HTML request", runtime.Requests.Count == before + 1 && panel.Result!.Completed && panel.Result.Steps[0].Artifact!.EndsWith(".html"));
        capture("visual-animation-minimum");
         check("returning to static restores saved SVG without requests", panel.Result?.Kind == VisualTestKind.Static && panel.Result.Completed); 
        runtime.ReplyFactory = (_, _, _) => Task.FromResult("not an artifact"); panel.StartCommand.Execute(null); await panel.CurrentOperation; Flush();
        check("invalid artwork preserves raw reply and cannot report completion", !panel.Result!.Completed && panel.Result.Steps[0].Reply == "not an artifact" && !panel.Steps[0].OpenCommand.CanExecute(null));
        panel.CloseCommand.Execute(null); Open(VisualTestKind.Revision); panel = model.VisualTests!; runtime.ReplyFactory = null; runtime.FailRender = true;
        before = runtime.Requests.Count; panel.StartCommand.Execute(null); await panel.CurrentOperation;
        check("failed draft render stops before critique request", runtime.Requests.Count == before + 1 && !panel.Result!.Completed && panel.Result.Steps.Count == 1);
        before = runtime.Requests.Count;
        var historical = panel.History.ToArray();
        check("history lists all saved visual kinds for only the selected account in reverse time", historical.Length >= 4
            && historical.All(h => h.Result.Profile == panel.SelectedProfile) && historical.Select(h => h.Result.Kind).Distinct().Count() == 4
            && historical.Select(h => h.Result.Started).SequenceEqual(historical.Select(h => h.Result.Started).OrderByDescending(t => t)));
        var previous = historical.Last(h => h.Result.Completed);
        panel.SelectedHistory = previous; Flush();
        check("choosing an older result restores original reply settings and usage without requests", panel.Result == previous.Result
            && panel.Model == previous.Result.Model && panel.Kind == previous.Result.Kind && panel.Steps[0].Step.Reply == previous.Result.Steps[0].Reply
            && panel.Prompt == previous.Result.Steps[0].Prompt && runtime.Requests.Count == before);
        var historyPicker = (ComboBox)window.FindName("VisualTestHistoryPicker");
        check("history control is visible above the result scroll", UiVerification.FullyVisible(historyPicker, window.RenderRoot)
            && UiVerification.DoNotOverlap(historyPicker, all, window.RenderRoot));
        capture("visual-history-light-minimum"); model.DarkTheme = true; capture("visual-history-dark-minimum"); model.DarkTheme = false;
        var reloaded = new VisualTestPanelViewModel(workspace.Accounts, panel.SelectedProfile, panel.Kind, _ => {}, () => {}, panel.Profiles);
        check("history survives a new panel and no replay calls are issued", reloaded.History.Count == historical.Length && runtime.Requests.Count == before);
        var other = panel.Profiles.First(p => p != panel.SelectedProfile); panel.SelectedProfile = other;
        check("history account switch never mixes another account results", panel.History.Count > 0 && panel.History.All(h => h.Result.Profile == other) && runtime.Requests.Count == before);
        panel.SelectedProfile = previous.Result.Profile; panel.SelectedHistory = panel.History.Last(h => h.Result.Completed);
        var corruptDirectory = Path.Combine(Path.GetDirectoryName(previous.Result.Directory)!, "corrupt-history"); Directory.CreateDirectory(corruptDirectory);
        File.WriteAllText(Path.Combine(corruptDirectory, "result.json"), "{broken");
        check("a damaged historical record does not hide valid records", workspace.Accounts.VisualTests.History(previous.Result.Profile).Count == historical.Length);
        File.WriteAllText(Path.Combine(corruptDirectory, "result.json"), System.Text.Json.JsonSerializer.Serialize(previous.Result with { Directory = Path.GetTempPath() }));
        check("history rejects a record that points outside its actual saved run", workspace.Accounts.VisualTests.History(previous.Result.Profile).Count == historical.Length);
        var legacy = previous.Result with { Version = "visual-v1" };
        var accountId = workspace.Accounts.Repository.ReadAuth(legacy.Profile)["tokens"]!["account_id"]!.ToString();
        var legacyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(accountId + "\n" + legacy.Model + "\n" + legacy.Kind + "\n" + legacy.Variant + "\n" + legacy.Version)));
        var legacyDirectory = Path.Combine(workspace.Accounts.Repository.AccountPath(legacy.Profile), "visual-tests", legacyKey, "legacy-run");
        Directory.CreateDirectory(legacyDirectory); legacy = legacy with { Directory = legacyDirectory };
        File.WriteAllText(Path.Combine(legacyDirectory, "result.json"), System.Text.Json.JsonSerializer.Serialize(legacy));
        var migrated = new VisualTestPanelViewModel(workspace.Accounts, legacy.Profile, legacy.Kind, _ => {}, () => {}, panel.Profiles);
        migrated.SelectedHistory = migrated.History.Single(h => h.Result.Version == "visual-v1");
        check("legacy CLI history remains readable while next test uses general request", migrated.History.Count == historical.Length + 1
            && migrated.SelectedHistory.Label.Contains("Codex CLI") && migrated.Status.Contains("历史结果（Codex CLI）")
            && migrated.RequestDescription.StartsWith("通用请求") && runtime.Requests.Count == before);
        var info = CodexVisualTestRuntime.BuildStartInfo("fixture-cli.exe", "D:/fixture/home", "http://127.0.0.1:7901", "gpt-6-astra", ["D:/fixture/draft.png"]);
        check("native CLI plan isolates credentials and carries explicit proxy", info.Environment["CODEX_HOME"] == "D:/fixture/home" && info.Environment["HTTPS_PROXY"] == "http://127.0.0.1:7901" && info.Environment["NO_PROXY"] == "localhost,127.0.0.1,::1" && info.CreateNoWindow && !info.UseShellExecute);
        check("native CLI plan disables tools and retries with high effort and image input", info.ArgumentList.Contains("--ignore-user-config") && info.ArgumentList.Contains("--ephemeral") && info.ArgumentList.Contains("model_reasoning_effort=\"high\"") && info.ArgumentList.Contains("shell_tool") && info.ArgumentList.Contains("model_providers.visual_test.request_max_retries=0") && info.ArgumentList.Contains("--image") && info.ArgumentList.Last() == "-");
        var wrapper = VisualTestArtifact.Wrap("<script>fetch('https://invalid.test')</script>");
        check("artwork viewer is opaque sandbox with network and parent access disabled", wrapper.Contains("sandbox='allow-scripts'") && !wrapper.Contains("allow-same-origin") && wrapper.Contains("connect-src") && wrapper.Contains("&lt;script&gt;"));
        check("SVG rejects broken XML executable foreign content and external entities", VisualTestArtifact.Extract("<svg><broken></svg>", false) is null && VisualTestArtifact.Extract("<svg><script>alert(1)</script></svg>", false) is null && VisualTestArtifact.Extract("<svg><foreignObject/></svg>", false) is null);
        check("visual tests preserve primary and saved credentials", originals.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
        runtime.FailRender = false; runtime.ReplyFactory = null;
        var cliInput = Path.Combine(directory, "visual-cli-request.json"); var cliOutput = Path.Combine(directory, "visual-cli-result.json");
        File.WriteAllText(cliInput, System.Text.Json.JsonSerializer.Serialize(new { command = "visual-test", profile = "测试甲", testKind = "Static", model = "gpt-6-astra" }));
        before = runtime.Requests.Count;
        check("agent CLI uses shared static service and returns saved artwork", await AgentCli.RunAsync(workspace, cliInput, cliOutput) == 0 && runtime.Requests.Count == before + 1 && File.ReadAllText(cliOutput).Contains("Static"));
        File.WriteAllText(cliInput, System.Text.Json.JsonSerializer.Serialize(new { command = "visual-results", profile = "测试甲", testKind = "Static", model = "gpt-6-astra" }));
        check("agent CLI result read does not call model", await AgentCli.RunAsync(workspace, cliInput, Path.Combine(directory, "visual-cli-cached.json")) == 0 && runtime.Requests.Count == before + 1);
        var high = workspace.Accounts.VisualTests.Load("测试甲", "gpt-6-astra", VisualTestKind.Static, "fixture still life")!;
        File.WriteAllText(cliInput, System.Text.Json.JsonSerializer.Serialize(new { command = "visual-test", profile = "测试甲", testKind = "Static", model = "gpt-6-astra", effort = "low" }));
        var beforeRejectedEffort = runtime.Requests.Count;
        check("agent CLI rejects old adjustable effort before model request", await AgentCli.RunAsync(workspace, cliInput, Path.Combine(directory, "visual-cli-low.json")) != 0 && runtime.Requests.Count == beforeRejectedEffort);
        var effortPanel = new VisualTestPanelViewModel(workspace.Accounts, "测试甲", VisualTestKind.Static, _ => {}, () => {}, panel.Profiles);
        before = runtime.Requests.Count; effortPanel.Effort = "low";
        check("old effort cannot change fixed high selection or issue request", effortPanel.Effort == "high" && runtime.Requests.Count == before);
        effortPanel.SelectedHistory = effortPanel.History.Single(h => h.Result.Directory == high.Directory);
        check("historical result remains readable", effortPanel.Result?.Directory == high.Directory && runtime.Requests.Count == before);
        var traceRoot = Path.Combine(directory, "diagnostic-export"); var requestRoot = Path.Combine(traceRoot, "request-1"); Directory.CreateDirectory(requestRoot);
        File.WriteAllText(Path.Combine(requestRoot, "wire-request.json"), "{\"fixture\":true}"); File.WriteAllText(Path.Combine(requestRoot, "response.sse"), "data: [DONE]\n");
        File.WriteAllText(Path.Combine(requestRoot, "auth.json"), "must-not-export"); File.WriteAllText(Path.Combine(traceRoot, "private.json"), "must-not-export");
        var zipPath = Path.Combine(directory, "diagnostics.zip"); TestDiagnosticsExport.Export(traceRoot, zipPath);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(zipPath)) check("diagnostic ZIP only contains selected request evidence", zip.Entries.Count == 2 && zip.Entries.All(e => e.FullName.StartsWith("request-1/") && !e.FullName.Contains("auth")));
        File.WriteAllText(cliInput, System.Text.Json.JsonSerializer.Serialize(new { command = "text-test", profile = "测试甲", prompt = "Reply exactly fixture", model = "gpt-6-astra", effort = "high" }));
        var textOutput = Path.Combine(directory, "text-cli-result.json");
        check("text diagnostic uses shared HTTP service and saves one request without a browser", await AgentCli.RunAsync(workspace, cliInput, textOutput) == 0 && File.Exists(Path.Combine(directory, "text-cli-result-evidence", "request-1", "trace-summary.json")));
        panel.CloseCommand.Execute(null); Open(VisualTestKind.Static); panel = model.VisualTests!;
        var modelPicker = (ComboBox)window.FindName("VisualTestModelPicker"); var effortPicker = (ComboBox)window.FindName("VisualTestEffortPicker");
        before = runtime.Requests.Count;
        modelPicker.SetCurrentValue(ComboBox.SelectedItemProperty, "gpt-6.1-sol"); Flush();
        check("visual model ComboBox changes the bound model before returning to Astra", panel.Model == "gpt-6.1-sol" && runtime.Requests.Count == before);
        modelPicker.SetCurrentValue(ComboBox.SelectedItemProperty, "gpt-6-astra"); Flush();
        effortPicker.SetCurrentValue(ComboBox.SelectedItemProperty, "xhigh"); Flush();
        check("visual ComboBox selections update bound parameters without requests", panel.Model == "gpt-6-astra" && panel.Effort == "high" && runtime.Requests.Count == before);
        var selectedStart = (Button)window.FindName("VisualTestStartButton"); selectedStart.Command.Execute(selectedStart.CommandParameter);
        await panel.CurrentOperation; Flush();
        check("visual bound start sends exactly the selected model and effort", runtime.Requests.Count == before + 1 && runtime.Requests[^1].Model == "gpt-6-astra"
            && runtime.Requests[^1].Effort == "high" && panel.Result?.Completed == true && panel.Result.Model == "gpt-6-astra" && panel.Result.Effort == "high");
        var empty = new VisualTestPanelViewModel(workspace.Accounts, null, VisualTestKind.Animation, _ => {}, () => {}, []);
        check("no authorized accounts means no start", !empty.StartCommand.CanExecute(null) && !empty.StartAllCommand.CanExecute(null));
        panel.CloseCommand.Execute(null); await workspace.Accounts.DisposeAsync();
    }
}
