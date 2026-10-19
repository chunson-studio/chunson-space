using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

// Runs only from --verify-storage-ui, against explicitly marked synthetic workspaces.
internal static class AccountLayoutVerification
{
    internal static async Task RunAsync(MainWindow window, string directory, Action<string, bool> check,
        Action<string, int, int> capture)
    {
        Directory.CreateDirectory(directory);
        var previous = window.DataContext;
        var root = window.RenderRoot;
        var facts = new List<object>();
        var automationFacts = new List<object>();
        foreach (var count in new[] { 0, 1, 3, 10 })
        {
            var workspaceRoot = Path.Combine(directory, "fixtures-" + count);
            var home = Path.Combine(workspaceRoot, "home");
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(workspaceRoot, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
            var store = new FileProfileStore(workspaceRoot);
            var names = Enumerable.Range(1, count).Select(i => i == 1 ? "01 很长的账号名称用于验证省略与完整提示 Long account name" : i.ToString("D2") + " 账号").ToArray();
            for (var i = 0; i < count; i++)
            {
                store.CreateBoundAccount(names[i], new(i % 2 == 0 ? "http://127.0.0.1:" + (7900 + i) : "", "America/Chicago", "en-US", FollowSystemProxy: i % 2 == 1));
                if (i == 1) continue; // Deliberately pending account alongside current/saved/frozen accounts.
                store.SaveAccount(names[i], new JsonObject { ["tokens"] = new JsonObject
                {
                    ["account_id"] = "fixture-id-" + i, ["access_token"] = "fixture-access-" + i,
                    ["refresh_token"] = "fixture-refresh-fixture-id-" + i
                } }, overwrite: true);
            }
            if (count > 0) File.Copy(Path.Combine(store.ProfileDirectory(new(IdentityKind.Account, names[0])), "auth.json"), Path.Combine(home, "auth.json"));
            if (count >= 3) store.SetTestsFrozen(names[2], true);
            var workspace = new DesktopWorkspace(workspaceRoot, home, null, null, fixture: true);
            var model = new ShellViewModel(workspace);
            var services = workspace.Accounts!;
            var transport = services.OfflineTransport!;
            window.DataContext = model;
            foreach (var dark in new[] { false, true })
            foreach (var size in new[] { (Width: 1280, Height: 850), (Width: 1040, Height: 670) })
            {
                model.DarkTheme = dark;
                var name = $"matrix-{count}-{(dark ? "dark" : "light")}-{size.Width}x{size.Height}";
                capture(name, size.Width, size.Height);
                // Inspect what assistive technology receives, not just the XAML attributes.
                foreach (var expected in new[] {
                    (Id: "MinimizeButton", Name: "最小化", Shortcut: "", Pattern: PatternInterface.Invoke),
                    (Id: "MaximizeButton", Name: "最大化 / 还原", Shortcut: "", Pattern: PatternInterface.Invoke),
                    (Id: "CloseButton", Name: "关闭窗口", Shortcut: "", Pattern: PatternInterface.Invoke),
                    (Id: "AccountRefreshButton", Name: "刷新档案", Shortcut: "F5", Pattern: PatternInterface.Invoke),
                    (Id: "AccountFilterBox", Name: "筛选账号", Shortcut: "", Pattern: PatternInterface.ExpandCollapse),
                    (Id: "SearchBox", Name: "搜索档案", Shortcut: "Ctrl+F", Pattern: PatternInterface.Value)
                })
                {
                    var control = (UIElement)window.FindName(expected.Id);
                    var peer = UIElementAutomationPeer.CreatePeerForElement(control);
                    check(name + " accessible name, shortcut and interaction pattern for " + expected.Id,
                        peer is not null && peer.GetName() == expected.Name && peer.GetAcceleratorKey() == expected.Shortcut
                        && peer.GetPattern(expected.Pattern) is not null && control.Focusable);
                    automationFacts.Add(new { state = name, expected.Id, name = peer!.GetName(),
                        shortcut = peer.GetAcceleratorKey(), pattern = expected.Pattern.ToString() });
                }
                var table = (FrameworkElement)window.FindName("AccountTablePanel");
                var viewport = (FrameworkElement)window.FindName("AccountViewport");
                var body = (FrameworkElement)window.FindName("WorkspaceBody");
                var detail = (FrameworkElement)window.FindName("SelectedExitDetail");
                var list = (ListBox)window.FindName("ProfileList");
                var capacity = Math.Clamp((int)Math.Floor((viewport.ActualHeight + 0.1) / 44), 1, 10);
                check(name + " full-height table, constrained viewport and complete rows", Math.Abs(table.ActualHeight - body.ActualHeight) < 1
                    && table.ActualWidth > detail.ActualWidth && Math.Abs(list.ActualHeight - viewport.ActualHeight) < 1
                    && model.IdentityPageSize == capacity && model.IdentityPageItems.Count * 44 <= viewport.ActualHeight + 1
                    && UiVerification.IdentityRowsFullyVisible(list, root, model, Path.Combine(directory, name + "-rows-failure.json")));
                check(name + " stable footer and fixed operation bar", UiVerification.FullyVisible((FrameworkElement)window.FindName("AccountPager"), root)
                    && UiVerification.FullyVisible((FrameworkElement)window.FindName("BottomLaunchButton"), root)
                    && UiVerification.Bounds(table, root).Bottom <= UiVerification.Bounds((FrameworkElement)window.FindName("ActiveOperationBar"), root).Top + 1);
                check(name + " summary and empty guidance", UiVerification.HomeDetailsFullyVisible(window, root, model, Path.Combine(directory, name + "-detail-failure.json"))
                    && (count != 0 || model.Selected is null && UiVerification.FullyVisible((FrameworkElement)window.FindName("EmptyAccountTitle"), root)
                        && !model.QuotaCommand.CanExecute(null) && !model.RefreshAccountCommand.CanExecute(null) && !model.ChooseVisualTestCommand.CanExecute(null)));
                check(name + " no requests from navigation layout theme or F5", transport.RequestCount == 0 && ((OfflineLoginBrowser)services.Browser).OpenCount == 0);
                facts.Add(new { name, count, dark, size.Width, size.Height, tableHeight = table.ActualHeight,
                    tableWidth = table.ActualWidth, detailWidth = detail.ActualWidth, viewportHeight = viewport.ActualHeight, capacity, shown = list.Items.Count });
                if (count > 0)
                {
                    var selected = model.Selected;
                    model.OpenDetailsCommand.Execute(null);
                    capture(name + "-actions", size.Width, size.Height);
                    var feedback = model.FeedbackTitle;
                    var search = model.Search;
                    check(name + " management blocks home refresh and search shortcuts without side effects",
                        window.HandleShortcut(Key.F5, ModifierKeys.None) && window.HandleShortcut(Key.F, ModifierKeys.Control)
                        && model.IsDetailOpen && model.FeedbackTitle == feedback && model.Search == search
                        && model.Selected == selected && transport.RequestCount == 0);
                    var panel = (FrameworkElement)window.FindName("AccountManagementPanel");
                    var info = (FrameworkElement)window.FindName("AccountInfoCard");
                    var groups = (FrameworkElement)window.FindName("AccountActionGroups");
                    var title = (TextBlock)window.FindName("AccountDetailTitle");
                    var gap = UiVerification.Bounds(groups, root).Top - UiVerification.Bounds(info, root).Bottom;
                    check(name + " management keeps full actions and constrained title in a compact card",
                        UiVerification.FullyVisible(panel, root) && UiVerification.Within(title, panel)
                        && title.ToolTip?.ToString() == selected?.Name && title.TextTrimming == TextTrimming.CharacterEllipsis
                        && gap >= 12 && gap <= 22 && UiVerification.FullyVisible(groups, root)
                        && UiVerification.FullyVisible((FrameworkElement)window.FindName("SwitchButton"), root));
                    check(name + " modal rendering never activates a verification window", !window.IsActive && !window.ShowActivated && !window.ShowInTaskbar);
                    var primary = (Button)window.FindName("UsagePrimaryButton");
                    var arrow = (Button)window.FindName("UsageMenuButton");
                    var menu = UiVerification.ArrangeMenu(arrow.ContextMenu, arrow);
                    var refresh = menu.Items.OfType<MenuItem>().Single();
                    check(name + " split controls preserve independent accessible command bindings", UiVerification.FullyVisible(primary, root)
                        && UiVerification.FullyVisible(arrow, root) && primary.Focusable && arrow.Focusable && refresh.Focusable
                        && AutomationProperties.GetName(primary) == "读取用量" && AutomationProperties.GetName(arrow).Length > 0
                        && primary.Command == model.QuotaCommand && refresh.Command == model.RefreshAccountCommand
                        && arrow.IsEnabled == model.RefreshAccountCommand.CanExecute(null) && primary.IsEnabled == model.QuotaCommand.CanExecute(null) && refresh.IsEnabled == model.RefreshAccountCommand.CanExecute(null)
                        && menu.DataContext == model && model.Selected == selected && transport.RequestCount == 0);
                    check(name + " popup follows native theme and readable outline", menu.Background == window.FindResource("SurfaceBrush")
                        && menu.Foreground == window.FindResource("InkBrush") && UiVerification.ContrastRatio(menu.Foreground, menu.Background) >= 4.5
                        && UiVerification.ContrastRatio(menu.BorderBrush, menu.Background) >= 1.5 && refresh.ActualHeight >= 28);
                    if (count == 1)
                    {
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(menu); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(directory, name + "-usage-menu.png")); encoder.Save(output);
                    }
                    model.CloseDetailsCommand.Execute(null);
                }
                if (count == 10)
                {
                    model.Selected = model.FilteredItems.Last();
                    capture(name + "-last-account", size.Width, size.Height);
                    check(name + " paging reaches account ten with selection retained", model.Selected?.Name == names.Last()
                        && model.IdentityPageItems.Contains(model.Selected) && UiVerification.IdentityRowsFullyVisible(list, root, model, Path.Combine(directory, name + "-last-failure.json")));
                    model.Selected = model.FilteredItems.First();
                }
                model.RefreshCommand.Execute(null);
            }
            if (count > 0)
            {
                model.Selected = model.FilteredItems.First();
                var primary = (Button)window.FindName("UsagePrimaryButton");
                var arrow = (Button)window.FindName("UsageMenuButton");
                var refresh = UiVerification.ArrangeMenu(arrow.ContextMenu, arrow).Items.OfType<MenuItem>().Single();
                var authPath = Path.Combine(services.Repository.AccountPath(names[0]), "auth.json");
                var authBefore = File.ReadAllBytes(authPath);
                var browserBefore = ((OfflineLoginBrowser)services.Browser).OpenCount;
                var frozenBefore = workspace.Profiles.AreTestsFrozen(names[0]);
                model.ToggleTestFreezeCommand.Execute(null);
                check($"matrix-{count} freeze is local and preserves authorization", transport.RequestCount == 0
                    && File.ReadAllBytes(authPath).SequenceEqual(authBefore) && workspace.Profiles.AreTestsFrozen(names[0]) != frozenBefore);
                primary.Command.Execute(primary.CommandParameter); await model.CurrentOperation;
                check($"matrix-{count} primary reads usage while frozen without token login or model request", transport.Requests.Select(r => r.Url).SequenceEqual(new[] { AccountEndpoints.Usage, AccountEndpoints.ResetCredits })
                    && model.DiagnosticTitle.Contains("用量读取完成") && model.DiagnosticDetails.Contains("余额") && File.ReadAllBytes(authPath).SequenceEqual(authBefore));
                model.CloseDiagnosticCommand.Execute(null);
                var start = transport.RequestCount;
                refresh.Command.Execute(refresh.CommandParameter); await model.CurrentOperation;
                check($"matrix-{count} secondary refreshes only while frozen with separate result", transport.RequestCount == start + 1
                    && transport.Requests.Last().Url == AccountEndpoints.Token && model.DiagnosticTitle.Contains("授权更新完成")
                    && ((OfflineLoginBrowser)services.Browser).OpenCount == browserBefore && workspace.Profiles.AreTestsFrozen(names[0]));
                model.CloseDiagnosticCommand.Execute(null);
                model.ToggleTestFreezeCommand.Execute(null);
                var afterActions = transport.RequestCount;
                var shortcut = (Button)window.FindName("HomeSyncTimezoneButton");
                ((CheckBox)window.FindName("SettingsAdvancedToggle")).IsChecked = true;
                shortcut.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                shortcut.Command.Execute(shortcut.CommandParameter);
                check($"matrix-{count} settings shortcut performs navigation only", model.IsSettingsPage && ((CheckBox)window.FindName("SettingsAdvancedToggle")).IsChecked == false && transport.RequestCount == afterActions);
                model.NavigateCommand.Execute("Accounts");
                model.Search = "missing"; model.ClearSearchCommand.Execute(null);
                check($"matrix-{count} filtering resets without requests or automatic unfreeze", model.ItemCount == count
                    && transport.RequestCount == afterActions && !workspace.Profiles.AreTestsFrozen(names[0])
                    && (count < 3 || workspace.Profiles.AreTestsFrozen(names[2])));
            }
            await services.DisposeAsync();
        }
        File.WriteAllText(Path.Combine(directory, "matrix-layout.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "matrix-accessibility.json"), JsonSerializer.Serialize(automationFacts, new JsonSerializerOptions { WriteIndented = true }));
        window.DataContext = previous;
    }
}
