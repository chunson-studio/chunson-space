using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

internal static class LocalAccountImportVerification
{
    public static async Task RunAsync(string root, Action<string, bool> check)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ChunsonSpace-fixture.json"), "{}");
        var original = Path.Combine(root, "original"); Directory.CreateDirectory(original);
        var originalAuth = Path.Combine(original, "auth.json");
        var auth = new JsonObject { ["tokens"] = new JsonObject { ["account_id"] = "local-fixture-id", ["access_token"] = "fixture-local-token" } };
        File.WriteAllText(originalAuth, auth.ToJsonString());
        var bytes = File.ReadAllBytes(originalAuth);
        var workspace = DesktopWorkspace.CreateManagedFixture(root, new(root, original, null, null));
        var model = new ShellViewModel(workspace);
        model.Search = "unmatched search"; model.AccountFilter = "待登录";
        var requests = workspace.Accounts!.OfflineTransport!.RequestCount;
        check("managed local import uses original client home while legacy save-current stays disabled", model.ImportLocalAccountCommand.CanExecute(null) && !model.SaveCurrentAccountCommand.CanExecute(null) && workspace.OriginalHome == original && workspace.ActiveHome != original);
        model.ImportLocalAccountCommand.Execute(null); await model.CurrentOperation;
        check("homepage local import selects saved account and keeps main credentials", model.Selected is not null && workspace.Profiles.ListAccounts().Single().HasCredentials && model.ItemCount == 1 && model.Search == "" && model.AccountFilter == "全部账号" && bytes.SequenceEqual(File.ReadAllBytes(originalAuth)) && model.FeedbackTitle == "本机账号已导入");
        model.ImportLocalAccountCommand.Execute(null); await model.CurrentOperation;
        check("repeating homepage import reports unchanged without extra profile or browser and model request", model.ItemCount == 1 && model.FeedbackTitle == "本机账号已是最新" && workspace.Accounts.OfflineTransport.RequestCount == requests && !model.IsLoggingIn && !model.IsOnboardingOpen && workspace.OfflineRuntime!.StopCalls == 0);
        File.Delete(originalAuth);
        model.ImportLocalAccountCommand.Execute(null); await model.CurrentOperation;
        check("missing original login gives visible failure and retains saved account", model.FeedbackTitle == "本机账号未导入" && model.ItemCount == 1 && model.ImportLocalAccountCommand.CanExecute(null));
        check("manual import stays on the single home", model.IsSingleAccountMode && model.Page == "Dashboard");
        await workspace.Accounts.DisposeAsync(); model.DisposeUpdates();
    }

    // Same fixture-only storage interception used by navigation checks. No runtime hook.
    public class ImportStorage : DispatchProxy
    {
        internal IFileStorage Inner = new PhysicalFileStorage();
        internal string Source = "";
        internal int Reads;
        internal ManualResetEventSlim? Release;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IFileStorage.Read) && args?[0] is string path && path == Source)
            {
                Interlocked.Increment(ref Reads); Entered.TrySetResult();
                if (Release is not null && !Release.Wait(TimeSpan.FromSeconds(15))) throw new IOException("Synthetic import release timed out");
            }
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
    }

    internal static async Task RunFocusedAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); var screenshots = new List<string>(); var workspaces = new List<DesktopWorkspace>();
        var models = new List<ShellViewModel>(); MainWindow? window = null; bool passed = false;
        using var release = new ManualResetEventSlim();
        var listener = new UiVerification.BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        void Check(string name, bool value) { checks.Add(new { name, passed = value }); if (!value) throw new InvalidOperationException(name); }
        void WriteAuth(string home, string id = "startup-id", string token = "synthetic-first")
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "auth.json"), new JsonObject { ["tokens"] = new JsonObject
                { ["account_id"] = id, ["access_token"] = token } }.ToJsonString());
        }
        (DesktopWorkspace Workspace, ShellViewModel Model, ImportStorage Storage) Fixture(string name, bool startup = true)
        {
            var root = Path.Combine(directory, name); Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "ChunsonSpace-fixture.json"), "{}");
            var original = Path.Combine(root, "original"); Directory.CreateDirectory(original);
            var workspace = DesktopWorkspace.CreateManagedFixture(root, new(root, original, null, null), startup);
            var storage = DispatchProxy.Create<IFileStorage, ImportStorage>(); var probe = (ImportStorage)storage;
            probe.Source = Path.Combine(original, "auth.json");
            typeof(ChunsonSpace.Accounts.AccountRepository).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace.Accounts!.Repository, storage);
            var model = new ShellViewModel(workspace); workspaces.Add(workspace); models.Add(model);
            return (workspace, model, probe);
        }
        async Task Idle() => await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            await RunAsync(Path.Combine(directory, "manual-regression"), Check);
            var preview = new ShellViewModel(); models.Add(preview); await preview.ImportLocalAccountAtStartupAsync();
            Check("preview has no automatic operation or manual credentials access", preview.CurrentOperation.IsCompleted && !preview.ImportLocalAccountCommand.CanExecute(null));
            var isolated = Fixture("default-fixture", startup: false); WriteAuth(isolated.Workspace.OriginalHome!);
            window = UiVerification.CreateWindow(isolated.Model); window.Show(); await Idle();
            Check("default fixture ContentRendered never reads saved login", isolated.Storage.Reads == 0 && isolated.Model.AccountTotal == 0);
            window.Close(); window = null;

            var start = Fixture("normal-start-seam"); var original = start.Workspace.OriginalHome!;
            WriteAuth(original); WriteAuth(start.Workspace.ActiveHome, "clone-must-not-import", "synthetic-clone");
            foreach (var file in new[] { "config.toml", "history.jsonl", "skills/sample/SKILL.md", "browser/cache" })
            { var path = Path.Combine(original, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "synthetic unchanged"); }
            var sourceBytes = Directory.GetFiles(original, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            var cloneBytes = File.ReadAllBytes(Path.Combine(start.Workspace.ActiveHome, "auth.json"));
            start.Storage.Release = release;
            Check("construction does not import before UI ready", start.Storage.Reads == 0 && start.Model.AccountTotal == 0);
            window = UiVerification.CreateWindow(start.Model); window.Show(); await start.Storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var auto = start.Model.CurrentOperation;
            var singleButton = (Button)((Dashboard)window.FindName("DashboardPage")).FindName("SingleImportLocalAccountButton");
            UiVerification.ArrangeForCapture(window.RenderRoot, 1040, 670);
            Check("rendered startup runs one async import with busy label and disabled fallback", !auto.IsCompleted && start.Model.IsBusy
                && !singleButton.IsEnabled && singleButton.Content?.ToString() == "正在导入…" && UiVerification.FullyVisible(singleButton, window.RenderRoot));
            start.Model.ImportLocalAccountCommand.Execute(null); await start.Model.ImportLocalAccountAtStartupAsync();
            Check("manual during automatic import cannot replace or duplicate operation", ReferenceEquals(auto, start.Model.CurrentOperation) && start.Storage.Reads == 1);
            Check("navigation remains enabled during local disk work", start.Model.NavigateCommand.CanExecute("Settings"));
            start.Model.NavigateCommand.Execute("Settings");
            start.Model.ReportLifecycleFailure("Synthetic newer feedback", "preserved");
            await Idle(); release.Set(); await auto;
            Check("automatic new account stays on chosen page and single mode without overwriting newer feedback", start.Model.AccountTotal == 1
                && start.Model.IsSingleAccountMode && start.Model.Page == "Settings" && start.Model.FeedbackTitle == "Synthetic newer feedback");
            Check("automatic source uses OriginalHome only and keeps original plus clone files byte exact", sourceBytes.All(p => p.Value.SequenceEqual(File.ReadAllBytes(p.Key)))
                && cloneBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(start.Workspace.ActiveHome, "auth.json")))
                && start.Workspace.Accounts!.Repository.ReadAuth(start.Model.Selected!.Name)["tokens"]?["account_id"]?.GetValue<string>() == "startup-id");
            WriteAuth(original, token: "synthetic-next");
            start.Model.Navigate("Dashboard"); start.Model.Navigate("Settings"); await start.Model.ImportLocalAccountAtStartupAsync();
            window.Hide(); window.Show(); await Idle();
            Check("navigation and reactivation do not reimport", start.Storage.Reads == 1);
            window.Close(); window = null;
            var nextStartup = new ShellViewModel(start.Workspace); models.Add(nextStartup); await nextStartup.ImportLocalAccountAtStartupAsync();
            Check("next startup updates same stable identity once", start.Storage.Reads == 2 && nextStartup.AccountTotal == 1 && nextStartup.FeedbackTitle == "本机账号授权已更新");
            var unchangedStartup = new ShellViewModel(start.Workspace); models.Add(unchangedStartup); var beforeFeedback = unchangedStartup.FeedbackTitle;
            await unchangedStartup.ImportLocalAccountAtStartupAsync();
            Check("no-change startup is quiet and distinct from failure", start.Storage.Reads == 3 && unchangedStartup.FeedbackTitle == beforeFeedback && !unchangedStartup.IsError);

            foreach (var failure in new[] { "missing", "invalid", "unreadable" })
            {
                var f = Fixture(failure); var path = f.Storage.Source;
                FileStream? held = null;
                if (failure == "invalid") File.WriteAllText(path, "{");
                if (failure == "unreadable") { WriteAuth(f.Workspace.OriginalHome!); held = new(path, FileMode.Open, FileAccess.Read, FileShare.None); }
                try { await f.Model.ImportLocalAccountAtStartupAsync(); } finally { held?.Dispose(); }
                Check(failure + " automatic failure leaves manual fallback usable and no fake success", f.Model.AccountTotal == 0 && !f.Model.IsBusy
                    && f.Model.ImportLocalAccountCommand.CanExecute(null) && f.Model.FeedbackTitle is "本机账号未导入" or "未找到可导入的本机账号");
                WriteAuth(f.Workspace.OriginalHome!); f.Model.ImportLocalAccountCommand.Execute(null); await f.Model.CurrentOperation;
                Check(failure + " manual recovery imports on single home", f.Model.AccountTotal == 1 && f.Model.FeedbackTitle == "本机账号已导入"
                    && f.Model.IsSingleAccountMode && f.Model.Page == "Dashboard");
            }

            var manual = Fixture("manual-first"); WriteAuth(manual.Workspace.OriginalHome!); release.Reset(); manual.Storage.Release = release;
            manual.Model.ImportLocalAccountCommand.Execute(null); var manualTask = manual.Model.CurrentOperation;
            await manual.Storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await manual.Model.ImportLocalAccountAtStartupAsync();
            Check("automatic startup never interrupts an existing manual import", ReferenceEquals(manualTask, manual.Model.CurrentOperation) && manual.Storage.Reads == 1 && !manualTask.IsCompleted);
            release.Set(); await manualTask; await manual.Model.ImportLocalAccountAtStartupAsync();
            Check("manual-first startup does not schedule a later second import", manual.Storage.Reads == 1 && manual.Model.AccountTotal == 1);

            var editing = Fixture("editing-first"); WriteAuth(editing.Workspace.OriginalHome!); editing.Model.NewAccountLoginCommand.Execute(null);
            await editing.Model.ImportLocalAccountAtStartupAsync();
            Check("startup leaves open manual add-login flow intact", editing.Model.IsOnboardingOpen && editing.Storage.Reads == 0 && editing.Model.AccountTotal == 0);
            editing.Model.CloseAccountOnboarding();

            var stable = Fixture("single-selection"); WriteAuth(stable.Workspace.OriginalHome!, "existing");
            stable.Workspace.Accounts!.Repository.ImportLocalLogin(stable.Workspace.OriginalHome!);
            stable.Model.RefreshCommand.Execute(null); var selected = stable.Model.Selected!.Identity;
            WriteAuth(stable.Workspace.OriginalHome!, "second"); stable.Model.Search = selected.Name; var savedMode = stable.Workspace.Profiles.ReadSettings().InterfaceMode;
            await stable.Model.ImportLocalAccountAtStartupAsync();
            Check("second automatic account preserves initial single mode, selection, filter and stored preference", stable.Model.AccountTotal == 2
                && stable.Model.IsSingleAccountMode && stable.Model.Selected?.Identity == selected && stable.Model.Search == selected.Name
                && stable.Model.Page == "Dashboard" && stable.Workspace.Profiles.ReadSettings().InterfaceMode == savedMode);

            var layout = Fixture("layout", startup: false);
            window = UiVerification.CreateWindow(layout.Model); window.Show(); await Idle();
            var dashboard = (Dashboard)window.FindName("DashboardPage");
            var button = (Button)dashboard.FindName("SingleImportLocalAccountButton"); var multi = (Button)window.FindName("ImportLocalAccountButton");
            foreach (var populated in new[] { false, true })
            {
                if (populated) { WriteAuth(layout.Workspace.OriginalHome!); layout.Model.ImportLocalAccountCommand.Execute(null); await layout.Model.CurrentOperation; }
                foreach (var dark in new[] { false, true })
                {
                    layout.Model.DarkTheme = dark; UiVerification.ArrangeForCapture(window.RenderRoot, 1040, 670); await Idle();
                    Check($"single {(populated ? "populated" : "empty")} {(dark ? "night" : "paper")} visible full manual button", UiVerification.FullyVisible(button, window.RenderRoot)
                        && button.Command == layout.Model.ImportLocalAccountCommand && button.IsEnabled && button.Content?.ToString() == "导入本机账号"
                        && !multi.IsVisible && !((Button)window.FindName("NavAccounts")).IsVisible);
                    if (populated)
                    {
                        var bitmap = new RenderTargetBitmap(1040, 670, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window.RenderRoot);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        var imagePath = Path.Combine(directory, dark ? "single-night.png" : "single-paper.png"); using (var stream = File.Create(imagePath)) encoder.Save(stream); screenshots.Add(imagePath);
                    }
                }
            }
            layout.Model.InterfaceMode = "多账号"; layout.Model.Navigate("Accounts"); UiVerification.ArrangeForCapture(window.RenderRoot, 1040, 670);
            Check("multi Accounts retains shared manual import header", UiVerification.FullyVisible(multi, window.RenderRoot) && multi.IsEnabled
                && multi.Command == layout.Model.ImportLocalAccountCommand && !button.IsVisible);
            layout.Model.ImportLocalAccountCommand.Execute(null); await layout.Model.CurrentOperation;
            Check("multi manual unchanged result retains Accounts and mode", layout.Model.IsMultiAccountMode && layout.Model.Page == "Accounts" && layout.Model.FeedbackTitle == "本机账号已是最新");
            UiVerification.AssertWindowsOutsideDesktop();
            Check("no bindings, model/login/network requests or clone operations", listener.Errors.Count == 0 && workspaces.All(w => w.Accounts!.OfflineTransport!.RequestCount == 0
                && w.OfflineRuntime!.StopCalls == 0 && w.OfflineRuntime.LastLaunchRequest is null && !w.Accounts.Login.Status.Active));
            passed = true;
        }
        finally
        {
            release.Set(); window?.Close(); foreach (var model in models) model.DisposeUpdates();
            foreach (var workspace in workspaces) await workspace.Accounts!.DisposeAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new
            { passed, scope = "local-import", checks, screenshots, bindingErrors = listener.Errors, realRequests = 0,
              actualAccountOperations = false, actualProxyChanges = false, actualFingerprintRequests = false, actualUserProcessStartsOrStops = 0 }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
