using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

internal static class TestHistoryClearVerification
{
    internal static async Task RunStandalone(string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Fresh fixture directory required");
        var workspace = BackgroundAccountTestsVerification.Fixture(directory);
        var checks = new List<object>(); var passed = false;
        void Check(string name, bool ok) { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }
        try
        {
            var targets = new List<string>();
            foreach (var name in new[] { "A", "B" })
            {
                workspace.Profiles.SetAccountMarks(name, AccountQuality.Normal, false, false);
                workspace.Profiles.SetTestsFrozen(name, name == "B");
                var profile = workspace.Accounts!.Repository.AccountPath(name);
                foreach (var child in new[] { "ciallo-tests", "visual-tests", "fingerprint-tests" })
                { var path = Path.Combine(profile, child); Directory.CreateDirectory(path); targets.Add(path); }
                var entry = new CialloHistoryEntry(DateTimeOffset.UtcNow, new([new(CialloTest.Models[0], CialloTest.Models[0], "high", "completed", "SYNTHETIC reply", TimeSpan.Zero)], false));
                File.WriteAllText(Path.Combine(profile, "ciallo-tests", "fixture.json"), JsonSerializer.Serialize(entry));
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"fixture-id-{name}\ngpt-6-astra\nStatic\noctopus-organ\n{VisualTestCatalog.Version}")));
                var run = Path.Combine(profile, "visual-tests", key, "fixture"); Directory.CreateDirectory(run);
                var result = new VisualTestResult(name, "gpt-6-astra", VisualTestKind.Static, "octopus-organ", VisualTestCatalog.Version,
                    DateTimeOffset.UtcNow, run, [new("initial", "SYNTHETIC", OfflineVisualTestRuntime.Svg, 0, 0, "fixture", "art.svg", null, "")], true, false, "SYNTHETIC");
                File.WriteAllText(Path.Combine(run, "result.json"), JsonSerializer.Serialize(result));
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(run)!, "latest.json"), JsonSerializer.Serialize(result));
                File.WriteAllText(Path.Combine(run, "art.svg"), OfflineVisualTestRuntime.Svg);
                File.WriteAllText(Path.Combine(profile, "fingerprint-tests", "history.json"), "{\"synthetic\":true}");
                Directory.CreateDirectory(Path.Combine(profile, "sessions"));
                File.WriteAllText(Path.Combine(profile, "sessions", "keep.jsonl"), "SYNTHETIC chat");
            }
            File.WriteAllText(Path.Combine(directory, "project.txt"), "SYNTHETIC project");
            var preserved = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => !targets.Any(root => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(path => path, File.ReadAllBytes);
            var shell = new ShellViewModel(workspace); var confirmations = 0;
            shell.ConfirmClearTestHistory = () => { confirmations++; return true; };
            shell.Selected = shell.FilteredItems.Single(item => item.Name == "A");
            shell.OpenCialloCommand.Execute(null); var ciallo = shell.Ciallo!; ciallo.CloseCommand.Execute(null);
            shell.OpenModelTestCommand.Execute(null); shell.ChooseVisualTestCommand.Execute("Static"); var visual = shell.VisualTests!; visual.CloseCommand.Execute(null);
            var gallery = shell.ResultsGallery; gallery.Enter(); await gallery.CurrentLoad;
            var control = new ResultsGallery { DataContext = gallery };
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            var button = (Button)control.FindName("ClearAllTestHistory");
            Check("button bound; both accounts have saved Ciallo and artwork", button.Command == shell.ClearAllTestHistoryCommand && button.IsEnabled && gallery.Items.Cast<object>().Count() == 4 && ciallo.History.Count == 1 && visual.History.Count == 1);
            var queue = (AccountTestQueue)typeof(ShellViewModel).GetField("accountTests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
            var release = new TaskCompletionSource<int>(); var pending = queue.RunAsync("A", () => release.Task, default);
            Check("clear disabled while test owns queue; no deletion", !button.Command.CanExecute(null) && targets.All(Directory.Exists));
            release.SetResult(0); await pending;
            button.Command.Execute(null); await shell.CurrentOperation;
            Check("one confirmation; all test directories removed", confirmations == 1 && targets.All(path => !Directory.Exists(path)));
            Check("gallery and retained dropdowns cleared", gallery.IsEmpty && ciallo.History.Count == 0 && ciallo.SelectedHistory is null && visual.History.Count == 0 && visual.Result is null && !shell.IsArtworkOpen);
            Check("accounts authorization marks freeze chat and project preserved", workspace.Profiles.ListAccounts().Count == 2 && preserved.All(pair => File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)));
            var text = new TextBlock { Style = (Style)System.Windows.Application.Current.FindResource("AccountHealthText") };
            foreach (var theme in new[] { "light", "dark" })
            {
                WorkspaceTheme.Apply(theme);
                foreach (var (label, brush) in new[] { ("账号正常", "SuccessBrush"), ("账号异常", "ErrorBrush"), ("本次没判断成功", "MutedBrush"), ("未标记", "MutedBrush") })
                { text.Text = label; if (!Equals(text.Foreground, System.Windows.Application.Current.FindResource(brush))) throw new InvalidOperationException("health theme color"); }
            }
            Check("shared light dark health colors; zero account requests", workspace.Accounts!.OfflineTransport!.RequestCount == 0 && workspace.Accounts.VisualRuntimeFixture!.Requests.Count == 0);
            passed = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { passed, syntheticOnly = true, actualAccountRequests = 0, checks }, new JsonSerializerOptions { WriteIndented = true }));
            await workspace.Accounts!.DisposeAsync();
        }
    }
}
