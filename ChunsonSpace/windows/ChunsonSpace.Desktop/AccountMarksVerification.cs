using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

internal static class AccountMarksVerification
{
    internal static async Task RunStandalone(string directory)
    {
        Directory.CreateDirectory(directory);
        var listener = new UiVerification.BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var checks = new List<object>(); var passed = false;
        try
        {
            await RunAsync(directory, (name, ok) => { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); });
            if (listener.Errors.Count > 0) throw new InvalidOperationException("Binding errors");
            passed = true;
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new { passed, checks, bindingErrors = listener.Errors, syntheticOnly = true, normalStartup = false, actualAccountOperations = false }, new JsonSerializerOptions { WriteIndented = true }));
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }
    internal static async Task RunAsync(string directory, Action<string, bool> check)
    {
        Directory.CreateDirectory(directory); var renders = new List<object>();
        foreach (var dark in new[] { false, true })
        foreach (var (width, height) in new[] { (1040, 670), (1280, 850) })
        {
            var key = $"{(dark ? "dark" : "light")}-{width}x{height}";
            var fixture = Path.Combine(directory, "fixtures", key);
            var workspace = BackgroundAccountTestsVerification.Fixture(fixture);
            workspace.Profiles.Rename(new(IdentityKind.Account, "A"), "SYNTHETIC甲");
            workspace.Profiles.Rename(new(IdentityKind.Account, "B"), "SYNTHETIC乙");
            const string a = "SYNTHETIC甲", b = "SYNTHETIC乙";
            var store = workspace.Profiles; var services = workspace.Accounts!; var transport = services.OfflineTransport!;
            var credential = store.ReadAccountCredentials(a); credential["tokens"]!["refresh_token"] = "fixture-refresh-fixture-id-A"; store.SaveAccount(a, credential, overwrite: true);
            var auth = File.ReadAllBytes(Path.Combine(services.Repository.AccountPath(a), "auth.json"));
            var env = store.ReadEnvironment(a);
            store.SetTestsFrozen(a, true);
            var history = Path.Combine(services.Repository.AccountPath(a), "ciallo-tests", "synthetic-history.json");
            Directory.CreateDirectory(Path.GetDirectoryName(history)!); File.WriteAllText(history, "SYNTHETIC unchanged history");
            var model = new ShellViewModel(workspace) { DarkTheme = dark }; var window = UiVerification.CreateWindow(model); var root = window.RenderRoot;
            void Layout() { UiVerification.ArrangeForCapture(root, width, height); window.UpdateLayout(); }
            void Check(string name, bool ok) { if (!ok) Capture("failure"); check("marks." + key + "." + name, ok); }
            void Capture(string name)
            {
                Layout(); UiVerification.AssertWindowsOutsideDesktop();
                check("marks." + key + "." + name + ".actual-theme", WorkspaceTheme.Current == (dark ? "dark" : "light"));
                var path = Path.Combine(directory, key + "-" + name + ".png");
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(path); encoder.Save(output); renders.Add(new { path, width, height, synthetic = true });
            }
            void Click(string name)
            {
                var control = (Button)window.FindName(name);
                Check(name + ".reachable", UiVerification.FullyVisible(control, root) && control.IsEnabled && control.Command.CanExecute(control.CommandParameter));
                control.Command.Execute(control.CommandParameter); Layout();
            }
            Button RowButton(string name)
            {
                Layout(); var list = (ListBox)window.FindName("ProfileList");
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(model.FilteredItems.Single(i => i.Name == name));
                return UiVerification.VisualDescendants(row).OfType<Button>().Single(button => button.Name == "RowMarkButton");
            }
            void Open(string name)
            {
                var button = RowButton(name);
                Check("row-mark-reachable", UiVerification.FullyVisible(button, root) && button.IsEnabled);
                button.Command.Execute(button.CommandParameter); Layout();
            }
            try
            {
                Layout(); Capture("legacy-unmarked");
                Check("legacy-remains-neutral", model.FilteredItems.All(i => i.QualityLabel == "未标记" && !i.HasRecoveredMark));
                model.Selected = model.FilteredItems.Single(i => i.Name == b); Open(a);
                Check("clicked-row-owns-editor", model.Selected?.Name == a && model.MarksEditor?.Name == a);
                var picker = (ComboBox)window.FindName("AccountQualityPicker"); picker.SelectedItem = "账号正常";
                ((CheckBox)window.FindName("AccountRecoveredCheck")).IsChecked = true;
                Layout(); Capture("manual-editor"); Click("SaveAccountMarksButton");
                Check("bound-controls-save-only-target", store.ReadAccountMarks(a) is { Quality: AccountQuality.Normal, Recovered: true, RecoverySource: "manual" }
                    && store.ReadAccountMarks(b) == new AccountMarks());
                Check("manual-controls-send-no-requests", transport.RequestCount == 0);
                Check("credentials-history-freeze-preserved", auth.SequenceEqual(File.ReadAllBytes(Path.Combine(services.Repository.AccountPath(a), "auth.json")))
                    && store.ReadEnvironment(a) == env && store.AreTestsFrozen(a) && File.ReadAllText(history) == "SYNTHETIC unchanged history");
                Open(a); picker.SelectedItem = "账号异常"; ((CheckBox)window.FindName("AccountProblemCheck")).IsChecked = true;
                Click("SaveAccountMarksButton");
                store.SetAccountMarks(b, AccountQuality.Normal, false, false); model.RefreshCommand.Execute(null); Layout();
                Capture("badges");
                foreach (var button in new[] { RowButton(a), RowButton(b) })
                    Check("quality-color-readable", UiVerification.ContrastRatio(button.Foreground, button.Background) >= 4.5);
                var rowA = (ListBoxItem)((ListBox)window.FindName("ProfileList")).ItemContainerGenerator.ContainerFromItem(model.FilteredItems.Single(i => i.Name == a));
                Check("all-independent-badges-visible", UiVerification.VisualDescendants(rowA).OfType<Border>().Where(x => x.Name is "RowRecoveredBadge" or "RowProblemBadge").All(x => UiVerification.FullyVisible(x, root) && UiVerification.Within(x, rowA)));
                var reopenedWorkspace = new DesktopWorkspace(workspace.Root, workspace.ActiveHome, Path.Combine(fixture, "desktop"), fixture: true);
                var reopened = new ShellViewModel(reopenedWorkspace) { DarkTheme = dark };
                Check("new-workspace-keeps-marks", reopened.FilteredItems.Single(i => i.Name == a) is { QualityLabel: "账号异常", HasRecoveredMark: true, HasProblemMark: true });
                model.Selected = model.FilteredItems.Single(i => i.Name == b); model.RefreshCommand.Execute(null);
                Check("refresh-keeps-selected-identity", model.Selected?.Name == b);
                Open(a); Click("ClearAccountMarksButton");
                Check("clear-is-draft-until-save", store.ReadAccountMarks(a).Recovered);
                Click("CloseAccountMarksButton"); Check("cancel-preserves-disk", store.ReadAccountMarks(a).Quality == AccountQuality.SuspectedDegraded);
                Open(a); Click("ClearAccountMarksButton"); Click("SaveAccountMarksButton"); model.RefreshCommand.Execute(null);
                Check("clear-survives-reload", store.ReadAccountMarks(a) is { Quality: AccountQuality.Unmarked, Recovered: false, Problem: false });
                // A stale edit must not attach a judgement to a different saved identity.
                Open(b); var bPath = Path.Combine(services.Repository.AccountPath(b), "auth.json"); var bAuth = File.ReadAllBytes(bPath);
                var bMarkPath = Path.Combine(services.Repository.AccountPath(b), FileProfileStore.AccountMarksFile); var bMarkBytes = File.ReadAllBytes(bMarkPath);
                File.WriteAllText(bPath, "{\"tokens\":{\"account_id\":\"SYNTHETIC-replacement\",\"access_token\":\"fixture\"}}");
                picker.SelectedItem = "账号异常"; Click("SaveAccountMarksButton");
                Check("identity-change-refuses-stale-save", model.IsMarksEditorOpen && model.MarksEditor!.Error.Contains("身份")
                    && store.ReadAccountMarks(b) is { ReadFailed: true, Quality: AccountQuality.Unmarked } && bMarkBytes.SequenceEqual(File.ReadAllBytes(bMarkPath)));
                File.WriteAllBytes(bPath, bAuth); Click("CloseAccountMarksButton");
                store.SetAccountMarks(a, AccountQuality.SuspectedDegraded, true, true); model.RefreshCommand.Execute(null);
                model.Selected = model.FilteredItems.Single(i => i.Name == a);
                transport.FailRequests = true; await model.RunAccountActionAsync(AccountAction.Quota); model.CloseDiagnosticCommand.Execute(null); Layout();
                Check("network-failure-is-not-quality-or-ban", store.ReadAccountMarks(a) is { Issue: AccountIssue.TemporaryConnection, Quality: AccountQuality.SuspectedDegraded, Recovered: true, Problem: true }
                    && model.Selected?.OperationIssueLabel == "暂时连接失败");
                Capture("connection-trouble");
                transport.FailRequests = false; await model.RunAccountActionAsync(AccountAction.Quota); model.CloseDiagnosticCommand.Execute(null);
                Check("matching-success-clears-only-operational-trouble", store.ReadAccountMarks(a) is { Issue: AccountIssue.None, Quality: AccountQuality.SuspectedDegraded, Recovered: true, Problem: true });
                transport.RequestObserved = _ => throw new AccountException(AccountError.Http, "private reason not stored", 401);
                await model.RunAccountActionAsync(AccountAction.Quota); model.CloseDiagnosticCommand.Execute(null); transport.RequestObserved = null;
                Check("actual-401-service-failure-is-expired-login", store.ReadAccountMarks(a) is { Issue: AccountIssue.LoginExpired, Quality: AccountQuality.SuspectedDegraded });
                store.RecordAccountIssue(a, AccountIssue.None, "Egress");
                Check("public-egress-success-cannot-clear-expired-login", store.ReadAccountMarks(a).Issue == AccountIssue.LoginExpired);
                store.RecordAccountIssue(a, AccountIssue.None, "Refresh");
                Check("confirmed-auth-success-clears-expiry-without-normal-label", store.ReadAccountMarks(a) is { Issue: AccountIssue.None, Quality: AccountQuality.SuspectedDegraded });
                Check("403-is-request-rejection-not-ban", AccountMarkDisplay.Classify(new AccountException(AccountError.Http, "SYNTHETIC", 403)) == AccountIssue.RequestRejected);
                Check("timeout-is-connection-trouble", AccountMarkDisplay.Classify(new AccountException(AccountError.Timeout, "SYNTHETIC")) == AccountIssue.TemporaryConnection);
                Open(a); ((Expander)window.FindName("AccountMarkDetails")).IsExpanded = true; Layout();
                UiVerification.VisualDescendants((Border)window.FindName("AccountMarksPanel")).OfType<ScrollViewer>().First().ScrollToEnd(); Layout(); Capture("details");
                Check("expanded-editor-save-stays-reachable", UiVerification.FullyVisible((Button)window.FindName("SaveAccountMarksButton"), root));
                Check("escape-closes-editor", window.HandleShortcut(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None) && !model.IsMarksEditorOpen);
                Open(b); ((ComboBox)window.FindName("AccountMarkModePicker")).SelectedItem = "自动"; Click("SaveAccountMarksButton");
                transport.ResponsesAreSse = true;
                string Sse(string kind) => ChunsonSpace.Verification.SafetyBufferingFixtures.Stream(kind).Replace("fixture-declared", "gpt-6-astra", StringComparison.Ordinal);
                transport.Responses.Enqueue(Sse("missing"));
                model.OpenCialloCommand.Execute(null); model.Ciallo!.Model = "gpt-6-astra";
                model.Ciallo.StartCommand.Execute(null); await model.Ciallo.CurrentOperation; model.Ciallo.CloseCommand.Execute(null); Layout();
                Check("actual-panel-completion-refreshes-automatic-abnormal-row", model.FilteredItems.Single(i => i.Name == b).QualityLabel == "账号异常" && store.ReadAccountMarks(a).Quality == AccountQuality.SuspectedDegraded);
                Capture("automatic-abnormal");
                model.OpenCialloCommand.Execute(null); transport.Responses.Enqueue(Sse("false"));
                model.Ciallo!.StartCommand.Execute(null); await model.Ciallo.CurrentOperation; model.Ciallo.CloseCommand.Execute(null); Layout();
                Check("automatic-normal-and-recovery-rendered", model.FilteredItems.Single(i => i.Name == b) is { QualityLabel: "账号正常", HasRecoveredMark: true });
                Capture("automatic-recovered");
                model.OpenCialloCommand.Execute(null); transport.Responses.Enqueue(Sse("incomplete"));
                model.Ciallo!.StartCommand.Execute(null); await model.Ciallo.CurrentOperation; model.Ciallo.CloseCommand.Execute(null); Layout();
                Check("incomplete-false-retains-normal-with-honest-row-reason", model.FilteredItems.Single(i => i.Name == b) is { QualityLabel: "账号正常", OperationIssueLabel: "本次没判断成功" });
                Capture("automatic-inconclusive");
                Open(b);
                store.ApplyAccountEvidence(b, store.ReadAccountIdentityHash(b), DateTimeOffset.UtcNow.AddSeconds(1), AccountEvidence.Id(key + "-new-while-editing"), AccountQuality.SuspectedDegraded, "software", "gpt-6-astra", "完整结果未返回该字段");
                var concurrentMarks = store.ReadAccountMarks(b);
                ((CheckBox)window.FindName("AccountProblemCheck")).IsChecked = true;
                Click("SaveAccountMarksButton");
                Check("stale-editor-cannot-overwrite-background-result", model.IsMarksEditorOpen && model.MarksEditor!.Error.Contains("新结果") && store.ReadAccountMarks(b) == concurrentMarks);
                Check("save-rejection-reason-visible-without-scrolling", UiVerification.FullyVisible((TextBlock)window.FindName("AccountMarksError"), root)
                    && UiVerification.FullyVisible((Button)window.FindName("SaveAccountMarksButton"), root));
                Capture("concurrent-edit-preserved"); Click("CloseAccountMarksButton");
                Open(b); ((CheckBox)window.FindName("AccountProblemCheck")).IsChecked = true; Click("SaveAccountMarksButton");
                Check("reopened-editor-keeps-latest-automatic-verdict", store.ReadAccountMarks(b) is { Quality: AccountQuality.SuspectedDegraded, Mode: AccountMarkMode.Automatic, Problem: true });
                foreach (var item in model.FilteredItems)
                {
                    var row = (ListBoxItem)((ListBox)window.FindName("ProfileList")).ItemContainerGenerator.ContainerFromItem(item);
                    var freeze = UiVerification.VisualDescendants(row).OfType<Button>().Single(button => button.Name == "RowTestFreezeButton");
                    Check("freeze-state-text-and-bounds-" + item.Name, Equals(freeze.Content, item.Name == a ? "🧊 已冻结" : "未冻结") && UiVerification.FullyVisible(freeze, root));
                }
                transport.ResponsesAreSse = false;
                var markPath = Path.Combine(services.Repository.AccountPath(a), FileProfileStore.AccountMarksFile); var saved = File.ReadAllBytes(markPath);
                File.WriteAllText(markPath, "{\"version\":99,\"quality\":\"Normal\"}"); model.RefreshCommand.Execute(null);
                Check("unknown-schema-does-not-pretend-normal", model.FilteredItems.Single(i => i.Name == a) is { QualityLabel: "未标记", OperationIssueLabel: "标记读取失败" });
                Open(a); Click("SaveAccountMarksButton"); Check("unreadable-marks-not-overwritten", model.IsMarksEditorOpen && File.ReadAllText(markPath).Contains("99"));
                Click("CloseAccountMarksButton"); File.WriteAllBytes(markPath, saved);
                store.Rename(new(IdentityKind.Account, a), "SYNTHETIC改名");
                Check("rename-carries-marks-and-history", store.ReadAccountMarks("SYNTHETIC改名").Recovered && File.Exists(Path.Combine(services.Repository.AccountPath("SYNTHETIC改名"), "ciallo-tests", "synthetic-history.json")));
                var backup = workspace.BackupService.Create("SYNTHETIC"); store.SetAccountMarks("SYNTHETIC改名", AccountQuality.Normal, false, false);
                var restored = workspace.BackupService.Restore(backup.Name);
                Check("backup-roundtrip-preserves-explicit-marks", restored.Ok && store.ReadAccountMarks("SYNTHETIC改名") is { Quality: AccountQuality.SuspectedDegraded, Recovered: true });
                Check("fixture-does-not-modify-auth", auth.SequenceEqual(File.ReadAllBytes(Path.Combine(services.Repository.AccountPath("SYNTHETIC改名"), "auth.json"))));
                await reopenedWorkspace.Accounts!.DisposeAsync();
            }
            finally { window.Close(); await services.DisposeAsync(); }
        }
        File.WriteAllText(Path.Combine(directory, "renders.json"), JsonSerializer.Serialize(renders, new JsonSerializerOptions { WriteIndented = true }));
    }
}
