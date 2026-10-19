using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;
using ChunsonSpace.Verification;

namespace ChunsonSpace.Desktop;

// Focused synthetic checks of the collector and the active result surfaces.
internal static class SafetyBufferingVerification
{
    internal static async Task RunStandalone(string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Fresh directory required");
        Directory.CreateDirectory(directory);
        var listener = new UiVerification.BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var checks = new List<object>(); var passed = false;
        try
        {
            await RunAsync(directory, (name, ok) => { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }, UiVerification.ArrangeForCapture);
            if (listener.Errors.Count != 0) throw new InvalidOperationException("Binding errors");
            passed = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new { passed, checks, bindingErrors = listener.Errors, syntheticOnly = true, normalStartup = false }, new JsonSerializerOptions { WriteIndented = true }));
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
    internal static async Task RunAsync(string directory, Action<string, bool> check, Action<FrameworkElement, int, int> arrange)
    {
        Directory.CreateDirectory(directory);
        const string modelName = "gpt-6-astra";
        string Sse(string kind, string model) => SafetyBufferingFixtures.Stream(kind).Replace("fixture-declared", model);
        JsonObject Nested(int depth, JsonNode? leaf)
        {
            for (var i = 0; i < depth; i++) leaf = new JsonObject { ["SYNTHETIC-key-" + i] = leaf };
            return leaf!.AsObject();
        }
        SafetyBufferingObservation Collect(JsonObject extra, bool eof = true)
        {
            var c = new SafetyBufferingCollector(); var created = SafetyBufferingFixtures.Created();
            created["response"]!["SYNTHETIC-settings"] = extra;
            c.Observe(created); c.Observe(SafetyBufferingFixtures.Completed()); return c.Snapshot(eof);
        }
        var absent = Collect(Nested(6, new JsonObject { ["scalar"] = 7, ["empty"] = new JsonArray(), ["text"] = "SYNTHETIC" }));
        check("path-expansion-not-truncation", absent is { Capture: "complete", DetailsTruncated: false, Values.Count: 0 } && SafetyBufferingDisplay.Summary(absent) == "账号异常");
        var nested = Collect(Nested(6, new JsonObject { ["safety_buffering"] = false }));
        check("long-path-field-retained-not-top-level", nested is { Capture: "complete", Values.Count: 1 } && nested.Values[0].Path.Length <= 240 && SafetyBufferingDisplay.Summary(nested) == "本次没判断成功");
        check("depth-budget-protected", Collect(Nested(70, new JsonObject())) is { Capture: "truncated", DetailsTruncated: true });
        check("node-budget-protected", Collect(new JsonObject { ["wide"] = new JsonArray(Enumerable.Range(0, 17000).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) }) is { Capture: "truncated", DetailsTruncated: true });
        check("no-fabricated-eof", Collect(new JsonObject(), false).Capture == "incomplete");
        var w = BackgroundAccountTestsVerification.Fixture(Path.Combine(directory, "parser-SYNTHETIC"));
        var transport = w.Accounts!.OfflineTransport!; transport.ResponsesAreSse = true;
        try
        {
            foreach (var kind in new[] { "false", "missing", "true", "null", "conflict", "truncated", "missing-incomplete", "mismatch", "model-mismatch", "marker", "object" })
            {
                var priorQuality = w.Profiles.ReadAccountMarks("A").Quality;
                var priorRequests = transport.RequestCount;
                transport.ResponseFactory = (_, _) => Task.FromResult(Sse(kind, modelName));
                var result = await w.Accounts.Accounts.TestCialloAsync("A", model: modelName);
                var reply = result.Replies.Single(); var row = new CialloRow(modelName); row.Update(reply);
                check("parser-label-" + kind, row.SafetyBufferingSummary == (kind == "false" ? "账号正常" : kind == "missing" ? "账号异常" : "本次没判断成功") && row.Text == reply.Text);
                check("preservation-no-retry-" + kind, transport.RequestCount == priorRequests + 1
                    && (kind is "false" or "missing" || w.Profiles.ReadAccountMarks("A").Quality == priorQuality));
                if (kind == "false")
                {
                    var trace = Path.Combine(result.DiagnosticDirectory!, "request-1");
                    string Hash(string name) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(trace, name))));
                    var identity = w.Profiles.ReadAccountIdentityHash("A"); var marks = w.Profiles.ReadAccountMarks("A");
                    var inspected = AccountTraceImport.Inspect(trace, Hash("trace-summary.json"), Hash("wire-request.json"), Hash("response.sse"), identity);
                    check("read-only-pinned-inspect", inspected.Verdict.Quality == AccountQuality.Normal && w.Profiles.ReadAccountMarks("A") == marks);
                    void Reject(Action action, string name) { try { action(); check(name, false); } catch (InvalidDataException) { check(name, true); } }
                    Reject(() => AccountTraceImport.Inspect(trace, Hash("trace-summary.json"), Hash("wire-request.json"), new string('0', 64), identity), "inspect-hash-rejection");
                    Reject(() => AccountTraceImport.Inspect(trace, Hash("trace-summary.json"), Hash("wire-request.json"), Hash("response.sse"), new string('0', 64)), "inspect-identity-rejection");
                }
            }
        }
        finally { await w.Accounts.DisposeAsync(); }
        var renders = new List<object>();
        foreach (var dark in new[] { false, true })
        {
            var key = dark ? "dark" : "light";
            var ui = BackgroundAccountTestsVerification.Fixture(Path.Combine(directory, key + "-SYNTHETIC"));
            ui.Profiles.Rename(new(IdentityKind.Account, "A"), "SYNTHETIC-A"); ui.Profiles.Rename(new(IdentityKind.Account, "B"), "SYNTHETIC-B");
            ui.Profiles.SetAccountMarks("SYNTHETIC-A", AccountQuality.SuspectedDegraded, false, true);
            var oldMark = ui.Profiles.ReadAccountMarks("SYNTHETIC-A");
            var t = ui.Accounts!.OfflineTransport!; t.ResponsesAreSse = true;
            t.ResponseFactory = (request, _) => Task.FromResult(Sse(request.Headers.GetValueOrDefault("chatgpt-account-id") == "fixture-id-A" ? "false" : "missing", JsonNode.Parse(request.Body!)!["model"]!.GetValue<string>()));
            var shell = new ShellViewModel(ui) { DarkTheme = dark }; var window = UiVerification.CreateWindow(shell); var root = window.RenderRoot;
            void Layout() => arrange(root, 1040, 670);
            IEnumerable<TextBlock> Blocks() => UiVerification.VisualDescendants(root).OfType<TextBlock>();
            void Capture(string name)
            {
                Layout(); UiVerification.AssertWindowsOutsideDesktop();
                check(key + "-" + name + "-no-diagnostic-copy", !Blocks().Where(b => UiVerification.FullyVisible(b, root)).Any(b => new[] { "安全缓冲", "safety_buffering", "EOF", "capture=", "SHA-256", "保留手动模式", "当前标记：", "没读完整" }.Any(b.Text.Contains)));
                var path = Path.Combine(directory, key + "-" + name + "-1040x670-SYNTHETIC.png");
                var bitmap = new RenderTargetBitmap(1040, 670, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var f = File.Create(path); png.Save(f);
                renders.Add(new { path, width = 1040, height = 670, synthetic = true });
            }
            void Click(string name) { var b = (Button)window.FindName(name); check(key + "-" + name + "-bound", b.Command is not null && b.IsEnabled); b.Command!.Execute(b.CommandParameter); }
            try
            {
                shell.OpenModelTestCommand.Execute(null); Layout();
                check(key + "-batch-entry-no-request", t.RequestCount == 0 && UiVerification.FullyVisible((Button)window.FindName("TestAllAccountsButton"), root));
                Click("TestAllAccountsButton"); await shell.AccountHealth!.CurrentOperation; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout();
                var batch = shell.AccountHealth!;
                check(key + "-batch-short-results", batch.Rows.Select(r => r.Status).SequenceEqual(new[] { "账号正常", "账号异常" }) && batch.Rows.All(r => r.Detail == "") && t.RequestCount == 4);
                check(key + "-manual-mark-preserved", ui.Profiles.ReadAccountMarks("SYNTHETIC-A") == oldMark);
                Capture("batch"); batch.CloseCommand.Execute(null);
                shell.Selected = shell.FilteredItems.Single(i => i.Name == "SYNTHETIC-A"); shell.OpenCialloCommand.Execute(null); Layout();
                check(key + "-ciallo-history-short-label-original", shell.Ciallo!.Rows.All(r => r.SafetyBufferingSummary == "账号正常" && r.Text == SafetyBufferingFixtures.Svg) && t.RequestCount == 4);
                check(key + "-ciallo-bound-label-visible", Blocks().Any(b => b.Name == "CialloSafetyBufferingSummary" && b.Text == "账号正常" && UiVerification.FullyVisible(b, root)));
                Capture("ciallo"); shell.Ciallo.CloseCommand.Execute(null);
                ui.Accounts.VisualRuntimeFixture!.ReplyRuntime = new ResponsesVisualTestRuntime(ui.Accounts.Accounts, () => throw new InvalidOperationException("No browser"));
                shell.OpenModelTestCommand.Execute(null); Layout(); Click("ChooseRevisionButton"); Layout();
                var visual = shell.VisualTests!; visual.SelectedProfile = "SYNTHETIC-A";
                var kinds = new Queue<string>(new[] { "false", "missing", "true" });
                t.ResponseFactory = (request, _) => Task.FromResult(Sse(kinds.Dequeue(), JsonNode.Parse(request.Body!)!["model"]!.GetValue<string>()));
                Click("VisualTestStartButton"); await visual.CurrentOperation; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Layout();
                check(key + "-visual-short-stage-labels", visual.Steps.Select(s => s.SafetyBufferingSummary).SequenceEqual(new[] { "账号正常", "账号异常", "本次没判断成功" }));
                var saved = SavedResultRow.FromVisual(visual.Result!).ToArray();
                check(key + "-gallery-viewer-label-original", saved.Select(s => s.SafetySummary).SequenceEqual(new[] { "账号正常", "账号异常", "本次没判断成功" }) && saved.All(s => s.Text == SafetyBufferingFixtures.Svg && s.ResultSummary == ""));
                if (dark)
                {
                    var scroll = (ScrollViewer)window.FindName("VisualTestScroll");
                    var label = Blocks().Single(b => b.Name == "VisualSafetyBufferingSummary" && b.Text == "本次没判断成功");
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + label.TranslatePoint(new Point(), scroll).Y - 30); Layout();
                }
                check(key + "-visual-bound-conclusion-visible", Blocks().Any(b => b.Name == "VisualSafetyBufferingSummary" && b.Text == (dark ? "本次没判断成功" : "账号正常") && UiVerification.FullyVisible(b, root)));
                Capture("visual");
            }
            finally { window.Close(); await ui.Accounts.DisposeAsync(); }
        }
        File.WriteAllText(Path.Combine(directory, "renders.json"), JsonSerializer.Serialize(renders, new JsonSerializerOptions { WriteIndented = true }));
    }
}
