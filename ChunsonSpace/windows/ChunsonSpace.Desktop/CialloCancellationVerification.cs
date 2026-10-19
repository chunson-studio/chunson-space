using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

// Exercise queue and in-flight cancellation with local fake transport only.
internal static class CialloCancellationVerification
{
    internal static async Task RunAsync(string directory, Action<string, bool> check, Action<FrameworkElement, int, int> arrange)
    {
        Directory.CreateDirectory(directory);
        var facts = new List<object>();
        foreach (var dark in new[] { false, true })
        foreach (var size in new[] { (Width: 1040, Height: 670), (Width: 1280, Height: 850) })
        {
            var state = $"{(dark ? "dark" : "light")}-{size.Width}x{size.Height}";
            var fixture = Path.Combine(directory, "fixtures", state);
            var accounts = Path.Combine(fixture, "accounts"); var home = Path.Combine(fixture, "home");
            Directory.CreateDirectory(accounts); Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(accounts, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
            var store = new FileProfileStore(accounts);
            store.CreateBoundAccount("取消回归", new("http://127.0.0.1:7901", "America/New_York", "en-US"));
            store.SaveAccount("取消回归", new JsonObject { ["tokens"] = new JsonObject {
                ["access_token"] = "fixture-access-cancel", ["account_id"] = "fixture-id-cancel" } }, overwrite: true);
            var workspace = new DesktopWorkspace(accounts, home, Path.Combine(fixture, "desktop"), fixture: true);
            var shell = new ShellViewModel(workspace) { DarkTheme = dark };
            var window = new MainWindow { DataContext = shell, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft - 4000, Top = SystemParameters.VirtualScreenTop - 4000 };
            var root = (FrameworkElement)window.FindName("RootLayout");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<int>? predecessor = null;
            try
            {
                void Layout() => arrange(root, size.Width, size.Height);
                void Capture(string name)
                {
                    Layout();
                    var bitmap = new RenderTargetBitmap(size.Width, size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, state + "-" + name + ".png")); encoder.Save(stream);
                }
                shell.OpenCialloCommand.Execute(null); Layout();
                var panel = shell.Ciallo!;
                var transport = workspace.Accounts!.OfflineTransport!;
                var progress = (TextBlock)window.FindName("CialloProgressText");
                var queue = (AccountTestQueue)typeof(ShellViewModel).GetField("accountTests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
                void Record(string stage) => facts.Add(new { state, stage, panel.Status, panel.ProgressSummary,
                    boundProgress = progress.Text, requests = transport.RequestCount, historyCount = panel.History.Count,
                    rows = panel.Rows.Select(row => new { row.Model, row.State, row.Text, row.Detail, row.Finished, row.Failed }).ToArray() });
                check(state + ".initial.pending", panel.Rows.Count == CialloTest.Models.Count && panel.Rows.All(row => row.State == "待测试" && !row.Finished)
                    && transport.RequestCount == 0 && panel.History.Count == 0);
                Capture("initial");
                predecessor = queue.RunAsync("取消回归", async () => { await gate.Task; return 1; }, CancellationToken.None);
                foreach (var single in new[] { false, true })
                {
                    panel.Model = single ? "gpt-6-astra" : "全部模型";
                    var count = single ? 1 : CialloTest.Models.Count;
                    var phase = single ? "queued-single" : "queued-all";
                    panel.StartCommand.Execute(null); Layout();
                    check(state + "." + phase + ".pending-reset", panel.IsBusy && panel.Rows.Count == count
                        && panel.Rows.All(row => !row.Finished && row.State == "待测试" && row.Text.Length == 0) && transport.RequestCount == 0);
                    var originalDetails = panel.Rows.Select(row => row.Detail).ToArray();
                    panel.CancelCommand.Execute(null); await panel.CurrentOperation; Layout();
                    Record(phase);
                    check(state + "." + phase + ".rows.corrected", panel.Rows.All(row => row.Finished && !row.Failed && row.State == "未请求"
                        && row.Text.Contains("未发送请求")) && panel.Rows.Select(row => row.Detail).SequenceEqual(originalDetails));
                    check(state + "." + phase + ".progress.corrected", panel.ProgressSummary.StartsWith($"已结束 {count}/{count} 个模型")
                        && progress.Text == panel.ProgressSummary && shell.BackgroundModelStatus.Contains(panel.ProgressSummary));
                    check(state + "." + phase + ".no-request-or-history", transport.RequestCount == 0 && panel.History.Count == 0
                        && panel.SelectedHistory is null && panel.DiagnosticDirectory is null && !panel.CanExportDiagnostics);
                    check(state + "." + phase + ".queue-and-controls", !predecessor.IsCompleted && queue.IsBusy("取消回归")
                        && !panel.IsBusy && panel.CanEdit && panel.StartCommand.CanExecute(null) && !panel.CancelCommand.CanExecute(null)
                        && panel.Status.Contains("已取消"));
                    Capture(phase);
                }
                gate.SetResult(); await predecessor;
                check(state + ".queue.released", !queue.IsBusy("取消回归"));

                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                transport.ResponseFactory = async (_, token) =>
                {
                    if (transport.RequestCount == 1) return "已完成的本地回复";
                    entered.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return "unreachable";
                };
                panel.Model = "全部模型";
                panel.StartCommand.Execute(null);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Layout();
                check(state + ".active.two-requests", panel.IsBusy && transport.RequestCount == 2 && panel.Rows[0].State == "已完成");
                panel.CancelCommand.Execute(null); await panel.CurrentOperation; Layout();
                Record("active-cancelled");
                check(state + ".active.pending-rows.corrected", panel.Rows.Skip(2).All(row => row.Finished && !row.Failed
                    && row.State == "未请求" && row.Text.Contains("未发送请求")));
                check(state + ".active.progress.corrected", panel.ProgressSummary.StartsWith($"已结束 {CialloTest.Models.Count}/{CialloTest.Models.Count} 个模型")
                    && progress.Text == panel.ProgressSummary && shell.BackgroundModelStatus.Contains(panel.ProgressSummary));
                check(state + ".active.real-results-preserved", panel.Rows[0].State == "已完成" && panel.Rows[0].Text == "已完成的本地回复"
                    && panel.Rows[0].Detail.Contains("fixture-model") && panel.Rows[1].State == "已取消" && panel.Rows[1].Finished
                    && panel.Rows[1].Text == "已取消，后续模型未请求。" && panel.Rows.All(row => !row.Failed));
                check(state + ".active.no-extra-requests-or-synthetic-history", transport.RequestCount == 2 && panel.History.Count == 1
                    && panel.SelectedHistory?.Result.Replies.Count == 2 && panel.SelectedHistory.Result.Completed == 1
                    && panel.SelectedHistory.Result.Cancelled && panel.DiagnosticDirectory is not null && panel.CanExportDiagnostics
                    && Directory.GetDirectories(panel.DiagnosticDirectory, "request-*").Length == 2);
                check(state + ".active.queue-and-controls", !queue.IsBusy("取消回归") && !panel.IsBusy && panel.StartCommand.CanExecute(null)
                    && !panel.CancelCommand.CanExecute(null) && !shell.IsModelTestRunning);
                Capture("active-cancelled");

                transport.ResponseFactory = null;
                panel.Model = "gpt-6-astra";
                panel.StartCommand.Execute(null); await panel.CurrentOperation; Layout();
                check(state + ".retry.completes", transport.RequestCount == 3 && panel.Rows.Count == 1 && panel.Rows[0].State == "已完成"
                    && panel.Rows[0].Text.Contains("Ciallo") && panel.History.Count == 2 && panel.SelectedHistory?.Result.Completed == 1
                    && !panel.IsBusy && !queue.IsBusy("取消回归"));
                check(state + ".window.nonactivating", !window.IsVisible && !window.IsActive && !window.ShowActivated && !window.ShowInTaskbar);
            }
            finally
            {
                gate.TrySetResult();
                if (predecessor is not null) await predecessor;
                if (shell.Ciallo?.IsBusy == true) { shell.Ciallo.CancelCommand.Execute(null); await shell.Ciallo.CurrentOperation; }
                window.Close();
                await workspace.Accounts!.DisposeAsync();
            }
        }
        File.WriteAllText(Path.Combine(directory, "cancellation-facts.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
    }
}
