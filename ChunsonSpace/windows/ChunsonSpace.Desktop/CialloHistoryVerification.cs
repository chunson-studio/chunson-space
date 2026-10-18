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

// Synthetic saved history and a held local queue; never constructs a live transport.
internal static class CialloHistoryVerification
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
            store.CreateBoundAccount("历史回归", new("http://127.0.0.1:7901", "America/New_York", "en-US"));
            store.SaveAccount("历史回归", new JsonObject { ["tokens"] = new JsonObject {
                ["access_token"] = "fixture-access-history", ["account_id"] = "fixture-id-history" } }, overwrite: true);
            var historyPath = Path.Combine(store.ProfileDirectory(new(IdentityKind.Account, "历史回归")), "ciallo-tests");
            Directory.CreateDirectory(historyPath);
            var savedDirectory = Path.Combine(historyPath, "saved");
            Directory.CreateDirectory(Path.Combine(savedDirectory, "request-1"));
            File.WriteAllText(Path.Combine(savedDirectory, "request-1", "wire-request.json"), "{\"fixture\":true}");
            var savedReply = new CialloReply("gpt-6-astra", "fixture-model", "high", "completed", "已保存的本地问候", TimeSpan.FromSeconds(2));
            var saved = new CialloHistoryEntry(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), new([savedReply], false, savedDirectory));
            var older = saved with { Started = saved.Started.AddDays(-1), Result = new([savedReply with { Text = "较早的本地问候" }], false) };
            File.WriteAllText(Path.Combine(historyPath, "saved.json"), JsonSerializer.Serialize(saved));
            File.WriteAllText(Path.Combine(historyPath, "older.json"), JsonSerializer.Serialize(older));
            var workspace = new DesktopWorkspace(accounts, home, Path.Combine(fixture, "desktop"), fixture: true);
            var model = new ShellViewModel(workspace) { DarkTheme = dark };
            var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false,
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
                model.OpenCialloCommand.Execute(null); Layout();
                var panel = model.Ciallo!;
                var picker = (ComboBox)window.FindName("CialloHistoryPicker");
                var transport = workspace.Accounts!.OfflineTransport!;
                var queue = (AccountTestQueue)typeof(ShellViewModel).GetField("accountTests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
                check(state + ".initial.saved-selected", panel.History.Count == 2 && panel.SelectedHistory?.Result.DiagnosticDirectory == savedDirectory
                    && panel.Rows.Single().Text == savedReply.Text && ReferenceEquals(picker.SelectedItem, panel.SelectedHistory) && transport.RequestCount == 0);
                Capture("initial");
                predecessor = queue.RunAsync("历史回归", async () => { await gate.Task; return 1; }, CancellationToken.None);
                panel.StartCommand.Execute(null); Layout();
                check(state + ".queued.no-request", panel.IsBusy && !panel.CurrentOperation.IsCompleted && transport.RequestCount == 0);
                check(state + ".queued.selection.corrected", panel.SelectedHistory is null && picker.SelectedItem is null);
                var selectedWhileBusy = panel.SelectedHistory;
                panel.SelectedHistory = panel.History.Last();
                check(state + ".queued.history-disabled", !picker.IsEnabled && panel.SelectedHistory == selectedWhileBusy);
                panel.CancelCommand.Execute(null); await panel.CurrentOperation; Layout();
                facts.Add(new { state, stage = "cancelled", selected = panel.SelectedHistory?.Label, pickerSelected = picker.SelectedItem is not null,
                    panel.Status, requests = transport.RequestCount, panel.DiagnosticDirectory });
                check(state + ".cancelled.selection.corrected", panel.SelectedHistory is null && picker.SelectedItem is null);
                check(state + ".cancelled.queue-and-guards", !panel.IsBusy && !predecessor.IsCompleted && queue.IsBusy("历史回归")
                    && panel.Status.Contains("已取消") && transport.RequestCount == 0 && panel.CanSelectHistory && panel.StartCommand.CanExecute(null));
                check(state + ".cancelled.no-diagnostics", panel.DiagnosticDirectory is null && !panel.CanExportDiagnostics);
                Capture("cancelled");
                picker.SelectedItem = panel.History.First(); Layout();
                var recovered = panel.Status.StartsWith("历史记录") && panel.Rows.Single().Text == savedReply.Text
                    && panel.DiagnosticDirectory == savedDirectory && panel.CanExportDiagnostics;
                facts.Add(new { state, stage = "latest-selected", recovered, selected = panel.SelectedHistory?.Label,
                    panel.Status, requests = transport.RequestCount });
                check(state + ".latest.recovery.corrected", recovered);
                check(state + ".latest.no-request", transport.RequestCount == 0 && !predecessor.IsCompleted);
                Capture("recovered");
                picker.SelectedItem = panel.History.Last(); Layout();
                check(state + ".legacy-history.recovery", panel.Rows.Single().Text == "较早的本地问候" && panel.Status.StartsWith("历史记录")
                    && panel.DiagnosticDirectory is null && transport.RequestCount == 0);
                picker.SelectedItem = panel.History.First(); Layout();
                check(state + ".history.reselection", panel.Rows.Single().Text == savedReply.Text && transport.RequestCount == 0);
                gate.SetResult(); await predecessor;
                check(state + ".queue.released", !queue.IsBusy("历史回归"));
                panel.StartCommand.Execute(null); await panel.CurrentOperation; Layout();
                check(state + ".completed.matches-saved-result", transport.RequestCount == 1 && panel.Rows.Single().Finished
                    && panel.History.Count == 3 && panel.SelectedHistory?.Result.DiagnosticDirectory == panel.DiagnosticDirectory
                    && panel.DiagnosticDirectory is not null && panel.CanExportDiagnostics && !panel.IsBusy);
                check(state + ".window.nonactivating", !window.IsVisible && !window.IsActive && !window.ShowActivated && !window.ShowInTaskbar);
            }
            finally
            {
                gate.TrySetResult();
                if (predecessor is not null) await predecessor;
                window.Close();
                await workspace.Accounts!.DisposeAsync();
            }
        }
        File.WriteAllText(Path.Combine(directory, "history-facts.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
    }
}
