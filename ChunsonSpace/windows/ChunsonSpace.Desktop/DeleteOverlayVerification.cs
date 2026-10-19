using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

// Only explicit --verify-ui delete-overlay: synthetic local files and the existing fake runtime.
internal static class DeleteOverlayVerification
{
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); var screenshots = new List<string>(); bool passed = false;
        var listener = new UiVerification.BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        void Check(string name, bool value)
        { checks.Add(new { name, passed = value }); if (!value) throw new InvalidOperationException(name); }
        string Write(string path, string text = "synthetic sentinel")
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path; }
        var fixture = Path.Combine(directory, "fixture");
        Write(Path.Combine(fixture, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var workspace = DesktopWorkspace.CreateManagedFixture(fixture);
        var store = workspace.Profiles; var storage = (ManagedCloneStorage)workspace.SwitchStorage;
        var runtime = workspace.OfflineRuntime!;
        string Profile(string name) => store.ProfileDirectory(new(IdentityKind.Account, name));
        void Account(string name)
        {
            store.CreateBoundAccount(name, new("", "Asia/Shanghai", "zh-CN", FollowSystemProxy: true));
            Write(Path.Combine(Profile(name), "auth.json"), JsonSerializer.Serialize(new { auth_mode = "chatgpt", tokens = new { account_id = "synthetic-" + name, access_token = "synthetic" } }));
        }
        foreach (var name in new[] { "A", "B", "C", "A-other" }) Account(name);
        var historical = Write(Path.Combine(workspace.AccountRoot, "xy_clean", "CodexX-01", "Boss", "historical", "keep"));
        var projectSentinel = Write(Path.Combine(fixture, "unrelated-project", "keep"));
        var originalSentinel = Write(Path.Combine(fixture, "original-home", "auth.json"));
        var otherObservation = Write(Path.Combine(Profile("C"), "exit-observation.json"), "{}");
        var browsers = new AccountBrowserProfiles(workspace.AccountRoot);
        var browser = browsers.Create(); browsers.Bind("B", browser);
        var cookie = Write(Path.Combine(browser, "Default", "Cookies"));
        var desktop = Path.Combine(workspace.AccountRoot, "electron-envs", "B"); Write(Path.Combine(desktop, "state"));
        storage.ApplyTarget(new(IdentityKind.Account, "A"), null, true);
        MainWindow? window = null;
        using var stopRelease = new ManualResetEventSlim(false);
        try
        {
            using (var held = new FileStream(cookie, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var lockedBrowser = workspace.ProfileMaintenance.DeleteAccount("B");
                Check("locked browser keeps owner and binding for a safe retry", !lockedBrowser.Success && Directory.Exists(Profile("B"))
                    && browsers.Read("B") == browser && File.Exists(cookie) && File.Exists(Path.Combine(browser, "chunson-browser-owner.json")));
            }
            using (var held = new FileStream(Path.Combine(workspace.AccountRoot, "browser-envs", "B", "browser-profile.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var partial = workspace.ProfileMaintenance.DeleteAccount("B");
                Check("later environment failure preserves profile after browser removal", !partial.Success && !Directory.Exists(browser) && Directory.Exists(Profile("B")));
            }
            // Exact unrelated deletion must ignore pending runtime/corrupt runtime auth.
            Write(Path.Combine(workspace.ActiveHome, "auth.json"), "{"); runtime.HasPendingLaunch = true;
            var other = await Task.Run(() => workspace.Maintenance!.DeleteAccountAsync(workspace.ProfileMaintenance, "B", managed: storage));
            Check("permanent delete removes only target credentials and bound browser/desktop", other.Success
                && !Directory.Exists(Profile("B")) && !Directory.Exists(browser) && !Directory.Exists(desktop)
                && Directory.Exists(Profile("A")) && Directory.Exists(Profile("A-other")) && Directory.Exists(Profile("C")));
            Check("no archive or recovery copy and no unrelated runtime stop", other.RecoveryPaths.Count == 0 && other.Delete!.Archives.Count == 0
                && runtime.StopCalls == 0 && runtime.PendingChecks == 0
                && Directory.GetFiles(Path.Combine(workspace.AccountRoot, "xy_clean"), "*", SearchOption.AllDirectories).SequenceEqual(new[] { historical }));

            // Preflight failures must leave all account roots in place.
            var wrongBrowser = browsers.Create(); browsers.Bind("C", wrongBrowser);
            Account("InvalidBrowser");
            Write(Path.Combine(workspace.AccountRoot, "browser-envs", "InvalidBrowser", "browser-profile.json"), "{\"directory\":\"../../unrelated-project\"}");
            var invalid = workspace.ProfileMaintenance.DeleteAccount("InvalidBrowser");
            Check("out of boundary browser binding is rejected before deletion", !invalid.Success && Directory.Exists(Profile("InvalidBrowser")) && File.Exists(projectSentinel));
            Account("WrongOwner");
            Write(Path.Combine(workspace.AccountRoot, "browser-envs", "WrongOwner", "browser-profile.json"), JsonSerializer.Serialize(new { directory = Path.GetRelativePath(Path.Combine(workspace.AccountRoot, "browser-envs"), wrongBrowser) }));
            var wrongOwner = workspace.ProfileMaintenance.DeleteAccount("WrongOwner");
            Check("browser owner mismatch preserves both accounts", !wrongOwner.Success && Directory.Exists(Profile("WrongOwner")) && Directory.Exists(wrongBrowser) && Directory.Exists(Profile("C")));
            bool invalidName = false;
            try { workspace.ProfileMaintenance.DeleteAccount("../C"); } catch (StorageException) { invalidName = true; }
            Check("traversal identity is rejected", invalidName && Directory.Exists(Profile("C")));

            // This junction is created only inside the synthetic fixture; never removed recursively.
            Account("Linked");
            var link = Path.Combine(Profile("Linked"), "outside-link");
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + Path.GetDirectoryName(projectSentinel)!.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
            using (var junction = Process.Start(psi)!)
            { await junction.WaitForExitAsync(); Check("synthetic junction fixture created", junction.ExitCode == 0); }
            var linked = workspace.ProfileMaintenance.DeleteAccount("Linked");
            Check("nested linked directory blocks deletion and preserves external sentinel", !linked.Success && Directory.Exists(Profile("Linked")) && File.Exists(projectSentinel));
            Directory.Delete(link); // Exact nonrecursive fixture link removal, preserving its target.

            Account("Locked"); var lockedFile = Write(Path.Combine(workspace.AccountRoot, "electron-envs", "Locked", "locked"));
            using (var held = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var locked = workspace.ProfileMaintenance.DeleteAccount("Locked");
                Check("I/O failure is explicit and keeps profile for retry without a backup", !locked.Success && locked.Archives.Count == 0
                    && locked.Message.Contains("无法恢复") && Directory.Exists(Profile("Locked")));
            }
            Check("retry after file lock release completes permanent deletion", workspace.ProfileMaintenance.DeleteAccount("Locked").Success && !Directory.Exists(Profile("Locked")));

            var model = new ShellViewModel(workspace); model.InterfaceMode = "多账号"; model.Navigate("Accounts");
            window = UiVerification.CreateWindow(model); window.Show();
            var root = window.RenderRoot;
            T Control<T>(string name) where T : FrameworkElement => (T)(window.FindName(name) ?? ((Dashboard)window.FindName("DashboardPage")).FindName(name));
            model.Selected = model.FilteredItems.Single(i => i.Name == "A");
            model.ArchiveSelectedCommand.Execute(null); var editor = model.Editor!;
            Check("single confirmation describes permanent local deletion", editor.Description.Contains("无法恢复") && editor.Description.Contains("不保留备份") && !editor.Description.Contains("可恢复归档"));
            foreach (var dark in new[] { false, true })
            {
                model.DarkTheme = dark; UiVerification.ArrangeForCapture(root, 1280, 850);
                var overlay = Control<Grid>("EditorOverlay"); var title = Control<Border>("WindowTitleControls");
                bool Covered(Point point) => root.InputHitTest(point) is DependencyObject hit && (hit == overlay || overlay.IsAncestorOf(hit));
                Check((dark ? "night" : "paper") + " scrim covers full client area and title controls", overlay.ActualWidth == 1280 && overlay.ActualHeight == 850
                    && new[] { new Point(100, 12), new Point(650, 12), new Point(1250, 16), new Point(1250, 800) }.All(Covered)
                    && WindowChrome.GetIsHitTestVisibleInChrome(overlay) && !title.IsEnabled
                    && !Control<Grid>("WorkspaceContent").IsEnabled && !Control<Border>("SidebarNavigation").IsEnabled);
                Check((dark ? "night" : "paper") + " underlying mode controls disabled", !Control<Button>("SingleModeButton").IsEnabled && !Control<Button>("MultiModeButton").IsEnabled);
                var bitmap = new RenderTargetBitmap(1280, 850, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var screenshot = Path.Combine(directory, "delete-" + (dark ? "night" : "paper") + ".png");
                using (var stream = File.Create(screenshot)) encoder.Save(stream); screenshots.Add(screenshot);
            }
            var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var uiThread = Environment.CurrentManagedThreadId;
            runtime.BeforeStop = () => { entered.TrySetResult(Environment.CurrentManagedThreadId); if (!stopRelease.Wait(TimeSpan.FromSeconds(15))) throw new IOException("synthetic gate timed out"); };
            editor.SaveCommand.Execute(null); var deleteTask = editor.CurrentOperation;
            Check("click immediately reports deleting and disables repeat/cancel", model.IsBusy && editor.SaveLabel == "正在删除…" && !editor.SaveCommand.CanExecute(null) && !editor.CancelCommand.CanExecute(null));
            var workerThread = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var dispatcherResponded = false; await root.Dispatcher.InvokeAsync(() => dispatcherResponded = true, DispatcherPriority.Background);
            editor.SaveCommand.Execute(null);
            Check("blocked fake stop runs off dispatcher and repeat click is not dispatched", dispatcherResponded && workerThread != uiThread
                && !deleteTask.IsCompleted && runtime.StopCalls == 1 && editor.CurrentOperation == deleteTask);
            Check("escape cannot dismiss active deletion and page shortcuts stay blocked", window.HandleShortcut(Key.Escape, ModifierKeys.None) && model.IsEditorOpen
                && window.HandleShortcut(Key.F5, ModifierKeys.None) && window.HandleShortcut(Key.F, ModifierKeys.Control));
            stopRelease.Set(); await deleteTask.WaitAsync(TimeSpan.FromSeconds(5)); runtime.BeforeStop = null;
            Check("success closes dialog updates list selection and active identity", !model.IsBusy && !model.IsEditorOpen && !editor.HasError
                && model.FeedbackTitle == "账号已删除" && model.FilteredItems.All(i => i.Name != "A") && model.Selected is not null && model.Selected.Name != "A" && model.Active is null
                && !Directory.Exists(Profile("A")) && storage.ReadRuntimeReference() is null);
            Check("current deletion preserves other account metadata and all unrelated files", File.ReadAllText(otherObservation) == "{}"
                && File.Exists(historical) && File.Exists(projectSentinel) && File.Exists(originalSentinel) && Directory.Exists(Profile("A-other"))
                && runtime.LastLaunchRequest is null && runtime.PendingChecks == 0);
            Check("nonmodal chrome and background controls restored", Control<Border>("WindowTitleControls").IsEnabled && Control<Button>("MinimizeButton").IsEnabled
                && Control<Button>("MaximizeButton").IsEnabled && Control<Button>("CloseButton").IsEnabled && WindowChrome.GetWindowChrome(window).CaptionHeight == 34
                && Control<Grid>("WorkspaceContent").IsEnabled);

            // Same real command failure path, with only the fake runtime refusing its stop.
            storage.ApplyTarget(new(IdentityKind.Account, "C"), null, true);
            model.RefreshCommand.Execute(null); model.Selected = model.FilteredItems.Single(i => i.Name == "C");
            runtime.StopSucceeds = false; model.ArchiveSelectedCommand.Execute(null); var failedEditor = model.Editor!;
            failedEditor.SaveCommand.Execute(null); await failedEditor.CurrentOperation;
            Check("failure stays in confirmation with short feedback and releases busy", model.IsEditorOpen && !model.IsBusy && failedEditor.HasError
                && failedEditor.Error.Length < 100 && failedEditor.SaveLabel == "确认删除" && failedEditor.SaveCommand.CanExecute(null)
                && Directory.Exists(Profile("C")) && storage.ReadRuntimeReference()?.Name == "C");
            failedEditor.CancelCommand.Execute(null);
            Check("failed deletion can be dismissed normally", !model.IsEditorOpen && Control<Border>("WindowTitleControls").IsEnabled);
            Check("no deletion creates new historical files", Directory.GetFiles(Path.Combine(workspace.AccountRoot, "xy_clean"), "*", SearchOption.AllDirectories).SequenceEqual(new[] { historical }));
            Check("no WPF binding errors or account/network requests", listener.Errors.Count == 0 && workspace.Accounts!.OfflineTransport!.Requests.Count == 0);
            passed = true;
        }
        finally
        {
            stopRelease.Set(); runtime.BeforeStop = null; window?.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new { passed, scope = "delete-overlay", checks, screenshots,
                bindingErrors = listener.Errors, actualAccountOperations = false, actualUserProcessStartsOrStops = false, actualNetworkRequests = false,
                fixtureOnly = true, syntheticPowerShellJunctionCreation = true }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
