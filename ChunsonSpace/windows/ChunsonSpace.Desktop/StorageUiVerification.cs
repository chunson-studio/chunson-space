using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using ChunsonSpace.Application;
using ChunsonSpace.Core;
using ChunsonSpace.Windows;
using ChunsonSpace.Accounts;
using ChunsonSpace.Proxy;

namespace ChunsonSpace.Desktop;

internal static class StorageUiVerification
{
    // Command-only fixture: no workspace, window, startup, network, score, or system services.
    internal static async Task RunFlClashDirectAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("FlClash direct assertion failed: " + name);
        }
        var runner = new OfflineFlClashDnsRunner
        {
            PreviewStatusOverride = FlClashDnsStatus.Failed, Groups = [], SelectedGroup = null,
            ApplicationCandidates = [], Scope = "all-subscriptions", SubscriptionCount = 2
        };
        var urls = new OfflineUrlOpener();
        var services = new DesktopProxyServices(new(client: new OfflineClashVergeState()),
            new(runner, FlClashDnsAssets.FromApplicationRoot(AppContext.BaseDirectory)), urls,
            Path.Combine(directory, "missing-flclash-data"), Path.Combine(directory, "unused-verge"),
            Path.Combine(directory, "backups"), fixture: true);
        var shellBusy = new List<bool>();
        var proxyResolutions = 0;
        var panel = new ProxyPanelViewModel(services, value => shellBusy.Add(value), _ =>
        {
            proxyResolutions++;
            throw new InvalidOperationException("Configure must not resolve or test a proxy.");
        });
        Check("unread missing configuration and application do not gate configure", panel.ConfigureNetworkCommand.CanExecute(null)
            && !panel.PreviewReady && panel.ApplicationPath.Length == 0);
        panel.ConfigureNetworkCommand.Execute(null); await panel.CurrentOperation;
        Check("fresh configure dispatches Apply exactly once even when Preview would fail", runner.Invocations.Count == 1
            && runner.Invocations[0].Action == FlClashDnsAction.Apply
            && runner.Invocations[0].Request.ApplicationPath is null);
        Check("saved but reload-unconfirmed feedback is preserved", !panel.IsError && panel.DnsSummary == "设置已保存"
            && panel.Status.Contains("重载尚未确认") && panel.DnsStage.Contains("重载待确认"));

        foreach (var ambiguous in new[] { false, true })
        {
            runner.Groups = ambiguous ? ["代理甲", "代理乙"] : [];
            runner.ApplicationCandidates = ambiguous ? ["D:/fixture/FlClash-a.exe", "D:/fixture/FlClash-b.exe"] : [];
            runner.PreviewStatusOverride = ambiguous ? FlClashDnsStatus.SelectionRequired : FlClashDnsStatus.Failed;
            var beforeRead = runner.Invocations.Count;
            panel.EnterPage(); await panel.CurrentOperation;
            Check($"{ambiguous}: automatic readonly page preview remains independent", runner.Invocations.Count == beforeRead + 1
                && runner.Invocations[^1].Action == FlClashDnsAction.Preview && (ambiguous || panel.IsError) && !panel.PreviewReady
                && panel.ConfigureNetworkCommand.CanExecute(null));
            var beforeConfigure = runner.Invocations.Count;
            panel.ConfigureNetworkCommand.Execute(null); await panel.CurrentOperation;
            Check($"{ambiguous}: failed missing or ambiguous preview cannot block direct Apply", runner.Invocations.Count == beforeConfigure + 1
                && runner.Invocations[^1].Action == FlClashDnsAction.Apply && !panel.IsError);
        }

        panel.ApplicationPath = Path.Combine(directory, "selected-FlClash.exe");
        panel.ProxyGroup = "stale-group";
        panel.BackupPath = Path.Combine(directory, "selected-backup");
        runner.ApplyStatus = FlClashDnsStatus.Failed;
        runner.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        shellBusy.Clear();
        var beforeBusy = runner.Invocations.Count;
        panel.ConfigureNetworkCommand.Execute(null);
        var operation = panel.CurrentOperation;
        Check("pending Apply retains local and shell busy state", panel.IsBusy && !panel.CanEdit
            && !panel.ConfigureNetworkCommand.CanExecute(null) && !operation.IsCompleted && shellBusy.SequenceEqual(new[] { true }));
        panel.ConfigureNetworkCommand.Execute(null);
        Check("busy duplicate is ignored without replacing the active operation", runner.Invocations.Count == beforeBusy + 1
            && ReferenceEquals(operation, panel.CurrentOperation));
        Check("direct Apply preserves explicit application backup and all-subscription inputs", runner.Invocations[^1].Request.ApplicationPath == panel.ApplicationPath
            && runner.Invocations[^1].Request.BackupDirectory == panel.BackupPath && runner.Invocations[^1].Request.ProxyGroup is null
            && runner.Invocations[^1].Request.AllSubscriptions);
        runner.Hold.SetResult(); await operation; runner.Hold = null;
        Check("script failure is shown and busy state recovers", panel.IsError && panel.Status.Contains("离线协议替身")
            && panel.DnsStage.Contains("未完成") && panel.DnsSummary == "未确认" && !panel.IsBusy && panel.CanEdit
            && panel.ConfigureNetworkCommand.CanExecute(null) && shellBusy.SequenceEqual(new[] { true, false }));

        runner.ApplyStatus = FlClashDnsStatus.GeneratedConfigVerified;
        panel.ConfigureNetworkCommand.Execute(null); await panel.CurrentOperation;
        Check("retry after failure dispatches once and reports actual successful stage", runner.Invocations.Count == beforeBusy + 2
            && !panel.IsError && panel.Status.Contains("生成配置已核对") && panel.DnsStage.Contains("实际网络效果尚未验证")
            && panel.BackupPath == Path.Combine(directory, "selected-backup") && panel.ConfigureNetworkCommand.CanExecute(null));
        runner.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        panel.ConfigureNetworkCommand.Execute(null);
        runner.Hold.SetException(new IOException("offline runner failure"));
        await panel.CurrentOperation; runner.Hold = null;
        Check("runner exception is reported as uncertain and releases busy state", panel.IsError
            && panel.Status.Contains("调用未得到可验证的报告") && !panel.IsBusy && panel.ConfigureNetworkCommand.CanExecute(null));
        Check("all dispatches stay offline and no unrelated services are consulted", runner.Invocations.All(x => x.Request.OfflineFixture
            && x.Request.AllSubscriptions && (x.Action != FlClashDnsAction.Apply || x.Request.ProxyGroup is null))
            && proxyResolutions == 0 && urls.Opened.Count == 0 && services.NodeTesting is null && services.Protection is null
            && services.BrowserUdp is null && System.Windows.Application.Current.Windows.Count == 0);
        File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new
        {
            passed = true, scope = "flclash-direct", checks,
            fakeApplyCalls = runner.Invocations.Count(x => x.Action == FlClashDnsAction.Apply),
            fakePagePreviewCalls = runner.Invocations.Count(x => x.Action == FlClashDnsAction.Preview),
            configurePreviewCalls = 0, proxyResolutions, realScriptExecutions = 0, realRequests = 0, realSystemWrites = 0,
            startupAdmissionCalls = 0, accountOperations = 0, ipScoreModelRequests = 0, screenshots = 0,
            isolation = "Only ProxyPanelViewModel, FlClashDnsService and OfflineFlClashDnsRunner; no workspace or live service construction."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task RunAsync(string directory)
    {
        var bindingListener = new UiVerification.BindingListener();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
        Directory.CreateDirectory(directory);
        var fixtureContainer = Path.Combine(directory, "fixtures", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(fixtureContainer, "工作区");
        var home = Path.Combine(fixtureContainer, "活动目录");
        var desktop = Path.Combine(fixtureContainer, "桌面数据");
        var webDesktop = Path.Combine(fixtureContainer, "APPDATA", "Codex", "web", "Codex");
        Directory.CreateDirectory(root); Directory.CreateDirectory(home); Directory.CreateDirectory(Path.Combine(desktop, "Default"));
        File.WriteAllText(Path.Combine(root, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var store = new FileProfileStore(root);
        JsonObject Credentials(string id) => new() { ["tokens"] = new JsonObject { ["account_id"] = id, ["access_token"] = "fixture-token-" + id } };
        store.CreateBoundAccount("工作甲", new("http://127.0.0.1:7890", "America/Chicago", "zh-CN"));
        store.SaveAccount("工作甲", Credentials("account-a"), overwrite: true);
        store.CreateBoundAccount("工作乙", new("http://127.0.0.1:7891", "Asia/Shanghai", "en-US"));
        store.SaveAccount("工作乙", Credentials("account-b"), overwrite: true);
        File.Copy(Path.Combine(store.ProfileDirectory(new(IdentityKind.Account, "工作甲")), "auth.json"), Path.Combine(home, "auth.json"));
        File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"fixture-model\"\n");
        File.WriteAllText(Path.Combine(home, ".codex-global-state.json"), "{\"installation_id\":\"fixture-installation\"}");
        Directory.CreateDirectory(Path.Combine(home, "sessions")); File.WriteAllText(Path.Combine(home, "sessions", "fixture-session.jsonl"), "fixture-session");
        File.WriteAllText(Path.Combine(desktop, "Default", "fixture.txt"), "fixture-desktop");
        Directory.CreateDirectory(Path.Combine(webDesktop, "Default")); File.WriteAllText(Path.Combine(webDesktop, "Default", "fixture.txt"), "fixture-web-desktop");
        var workspace = new DesktopWorkspace(root, home, desktop, webDesktop, fixture: true);
        var model = new ShellViewModel(workspace);
        var window = UiVerification.CreateWindow(model);
        var layout = window.RenderRoot;
        var checks = new List<object>();
        var renders = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("Storage UI assertion failed: " + name);
        }
        void Flush() => layout.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        await Chunson.Setup.FlClashSetupChecks.RunAsync(Path.Combine(directory, "flclash-setup"), (passed, name) => Check(name, passed));
        var fixtureIcon = Path.Combine(root, "native-taskbar.ico"); File.WriteAllBytes(fixtureIcon, BrandIcon.CreateIco());
        Check("native taskbar identity and icon properties round trip on the isolated fixture window", new NativeWindowTaskbarProperties().Apply(
            new WindowInteropHelper(window).EnsureHandle(), new("Chunson.Space.IconFixture." + Guid.NewGuid().ToString("N"), fixtureIcon + ",0", "\"" + Environment.ProcessPath + "\"", "隔离图标检查")));
        var helpOutput = new StringWriter();
        var helpCode = await AccountCli.TryRunWithoutServicesAsync(["--help"], helpOutput, "ChunsonSwitch.exe");
        Check("CLI help returns output without normal service composition", helpCode == 0 && helpOutput.ToString().Contains("Chunson Space") && helpOutput.ToString().Contains("ChunsonSwitch.exe --list"));
        var errorOutput = new StringWriter();
        var formatCode = await AccountCli.TryRunWithoutServicesAsync(["--new"], errorOutput, "ChunsonSwitch.exe");
        Check("CLI format error returns code two without normal composition", formatCode == 2 && errorOutput.ToString().Contains("参数缺少值"));
        void Capture(string name, int width = 1280, int height = 850)
        {
            UiVerification.ArrangeForCapture(layout, width, height);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(layout);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, name + ".png"); using (var stream = File.Create(path)) encoder.Save(stream);
            renders.Add(new { name, path, width, height, source = "WPF XAML RenderTargetBitmap" });
        }
        void CheckNodeSurface(string state)
        {
            var list = (ListBox)window.FindName("NodeTestingList");
            var bitmap = new RenderTargetBitmap((int)list.ActualWidth, (int)list.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(list);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)list.ActualWidth / 2, (int)list.ActualHeight - 4, 1, 1), pixel, 4, 0);
            var rendered = new SolidColorBrush(Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]));
            Check("node list rendered background and text contrast remain themed " + state,
                rendered.Color == ((SolidColorBrush)window.FindResource("SurfaceBrush")).Color
                && UiVerification.ContrastRatio((Brush)window.FindResource("InkBrush"), rendered) >= 7
                && UiVerification.ContrastRatio((Brush)window.FindResource("MutedBrush"), rendered) >= 4.5);
        }
        void CaptureGroupDropdown(string name)
        {
            var toggle = (CheckBox)window.FindName("ProxySimpleAdvancedToggle"); var prior = toggle.IsChecked; toggle.IsChecked = true; Flush();
            var combo = (ComboBox)window.FindName("ProxyGroupBox"); combo.ApplyTemplate();
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            var content = (Border)popup.Child;
            content.Width = 300; content.Measure(new Size(300, 300));
            var height = Math.Max(45, (int)Math.Ceiling(content.DesiredSize.Height));
            content.Arrange(new Rect(0, 0, 300, height)); content.UpdateLayout(); Flush();
            var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0);
            Check(name + " uses theme surface and readable theme ink", item is not null && item.Foreground == window.FindResource("InkBrush") && content.Background == window.FindResource("SurfaceBrush"));
            Check(name + " keeps text and popup outline visible", item is not null && UiVerification.ContrastRatio(item.Foreground, content.Background) >= 4.5 && content.BorderThickness.Left >= 1 && UiVerification.ContrastRatio(content.BorderBrush, content.Background) >= 1.5);
            var bitmap = new RenderTargetBitmap(300, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, name + ".png"); using (var stream = File.Create(path)) encoder.Save(stream);
            renders.Add(new { name, path, width = 300, height, source = "WPF ComboBox Popup template RenderTargetBitmap; no desktop capture" });
            toggle.IsChecked = prior; Flush();
        }
        void CaptureAccountMenu(string name, ListBoxItem row)
        {
            var menu = UiVerification.ArrangeAccountContextMenu(row);
            var items = menu.Items.OfType<MenuItem>().ToArray();
            var commandsMatch = menu.DataContext == model && UiVerification.AccountContextCommandsMatch(menu, model);
            var readable = menu.Foreground == window.FindResource("InkBrush")
                && menu.Background == window.FindResource("SurfaceBrush") && UiVerification.ContrastRatio(menu.Foreground, menu.Background) >= 4.5
                && menu.BorderThickness.Left >= 1 && UiVerification.ContrastRatio(menu.BorderBrush, menu.Background) >= 1.5
                && items.All(item => item.ActualWidth > 0 && item.ActualHeight >= 28 && ReferenceEquals(item.Style, window.FindResource("AccountMenuItem"))
                    && item.Template.FindName("MenuFrame", item) is Border);
            var width = Math.Max(1, (int)Math.Ceiling(menu.ActualWidth)); var height = Math.Max(1, (int)Math.Ceiling(menu.ActualHeight));
            File.WriteAllText(Path.Combine(directory, name + "-diagnostics.json"), JsonSerializer.Serialize(new { commandsMatch, readable,
                selected = model.Selected?.Name, placementTarget = (menu.PlacementTarget as FrameworkElement)?.DataContext?.ToString(),
                menu.ActualWidth, menu.ActualHeight, desiredWidth = menu.DesiredSize.Width, desiredHeight = menu.DesiredSize.Height,
                foreground = menu.Foreground?.ToString(), background = menu.Background?.ToString(), border = menu.BorderBrush?.ToString(),
                borderWidth = menu.BorderThickness.Left, textContrast = UiVerification.ContrastRatio(menu.Foreground, menu.Background),
                borderContrast = UiVerification.ContrastRatio(menu.BorderBrush, menu.Background),
                foregroundMatchesTheme = menu.Foreground == window.FindResource("InkBrush"), backgroundMatchesTheme = menu.Background == window.FindResource("SurfaceBrush"),
                items = items.Select(item => new { label = item.Header?.ToString(), item.ActualWidth, item.ActualHeight, item.IsEnabled,
                    styleMatches = ReferenceEquals(item.Style, window.FindResource("AccountMenuItem")), templateFrame = item.Template.FindName("MenuFrame", item) is Border,
                    foreground = item.Foreground?.ToString() }) }, new JsonSerializerOptions { WriteIndented = true }));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(menu);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, name + ".png"); using (var stream = File.Create(path)) encoder.Save(stream);
            renders.Add(new { name, path, width, height, source = "WPF account ContextMenu template RenderTargetBitmap; no desktop capture" });
            Check(name + " binds all account actions to the selected row shell", commandsMatch);
            Check(name + " uses readable theme ink and an outlined theme surface", readable);
        }
        async Task SaveEditor() { var current = model.Editor ?? throw new InvalidOperationException("Editor missing"); current.SaveCommand.Execute(null); await current.CurrentOperation; Flush(); }
        bool Displayed(FrameworkElement element) => UiVerification.FullyVisible(element, layout);
        object LayoutFact(FrameworkElement element)
        {
            var bounds = UiVerification.Bounds(element, layout);
            var ancestors = new List<object>();
            for (var parent = VisualTreeHelper.GetParent(element); parent is not null && parent != layout; parent = VisualTreeHelper.GetParent(parent))
                if (parent is FrameworkElement ancestor) ancestors.Add(new { type = ancestor.GetType().Name, name = ancestor.Name, visibility = ancestor.Visibility.ToString(), ancestor.ClipToBounds, width = ancestor.ActualWidth, height = ancestor.ActualHeight });
            return new { type = element.GetType().Name, name = element.Name, visibility = element.Visibility.ToString(), displayed = Displayed(element), width = element.ActualWidth, height = element.ActualHeight,
                bounds = new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height }, ancestors };
        }
        bool PageRowActionsVisible() => UiVerification.IdentityRowsFullyVisible((ListBox)window.FindName("ProfileList"), layout, model,
            Path.Combine(directory, "row-action-layout-failure.json"));
        bool DetailActionsVisible(params DelegateCommand[] commands)
        {
            var info = (ScrollViewer)window.FindName("IdentityInfoScroll");
            var card = FindAncestor<Border>(info) ?? throw new InvalidOperationException("Identity detail card missing");
            var dialog = (FrameworkElement)window.FindName("AccountManagementPanel");
            var buttons = Descendants(dialog).OfType<Button>().Where(button => commands.Any(command => button.Command == command)).ToArray();
            var passed = UiVerification.FullyVisible(dialog, layout) && commands.All(command => buttons.Any(button => button.Command == command && Displayed(button)))
                && !Descendants(info).OfType<Button>().Any(button => commands.Any(command => button.Command == command))
                && buttons.All(button => UiVerification.FullyVisible(button, layout) && UiVerification.Within(button, dialog)
                    && button.ActualHeight >= 28 && button.ActualWidth >= 30 && button.IsHitTestVisible)
                && buttons.SelectMany((button, index) => buttons.Skip(index + 1).Select(other => UiVerification.DoNotOverlap(button, other, layout))).All(value => value);
            if (!passed)
            {
                var overlaps = buttons.SelectMany((button, index) => buttons.Skip(index + 1)
                    .Where(other => !UiVerification.DoNotOverlap(button, other, layout)).Select(other => new { first = button.Content?.ToString(), second = other.Content?.ToString() }));
                File.WriteAllText(Path.Combine(directory, "detail-action-layout-failure.json"), JsonSerializer.Serialize(new { passed,
                    page = model.Page, selected = model.Selected?.Name, dialog = LayoutFact(dialog), card = LayoutFact(card), info = LayoutFact(info),
                    missingCommands = commands.Select((command, index) => new { index, presentAndVisible = buttons.Any(button => button.Command == command && Displayed(button)) }),
                    buttons = buttons.Select(button => new { label = button.Content?.ToString(), commandIndex = Array.FindIndex(commands, command => button.Command == command),
                        geometry = LayoutFact(button), withinDialog = UiVerification.Within(button, dialog), button.IsHitTestVisible }), overlaps }, new JsonSerializerOptions { WriteIndented = true }));
            }
            return passed;
        }
        await AccountLayoutVerification.RunAsync(window, Path.Combine(directory, "account-layout"), Check, Capture);
        var loginRoot = Path.Combine(fixtureContainer, "首页登录工作区"); var loginHome = Path.Combine(fixtureContainer, "首页登录活动目录");
        Directory.CreateDirectory(loginRoot); Directory.CreateDirectory(loginHome);
        File.WriteAllText(Path.Combine(loginRoot, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var loginWorkspace = new DesktopWorkspace(loginRoot, loginHome, null, null, fixture: true);
        var loginModel = new ShellViewModel(loginWorkspace); window.DataContext = loginModel;
        var loginTransport = loginWorkspace.Accounts!.OfflineTransport!;
        bool OnlyPublicLoginProbes() => loginTransport.Requests.All(request => request.Url == AccountEndpoints.Egress || request.Url == AccountEndpoints.Trace
            || request.Url == NetCoffeeService.TraceEndpoint || NetCoffeeService.IsLookup(request.Url));
        var newBrowser = loginWorkspace.Accounts.OfflineNewAccountBrowser!;
        Capture("login-empty-accounts");
        var compactTable = (FrameworkElement)window.FindName("AccountTablePanel");
        var compactDetail = (FrameworkElement)window.FindName("SelectedExitDetail");
        var compactGarden = (BotanicalGarden)window.FindName("HomeGarden");
        Check("empty home stretches table with a stable disabled pager", compactTable.ActualHeight > 440
            && Displayed((FrameworkElement)window.FindName("AccountPager")) && !loginModel.HasIdentityPages);
        var homeLoginButton = (Button)window.FindName("AddAccountLoginButton");
        Check("empty page has one direct login entry without prior profile or requests", loginModel.HasNoAccounts && homeLoginButton.Content?.ToString()?.TrimStart('＋', '+', ' ').Equals("添加账号", StringComparison.Ordinal) == true && Displayed(homeLoginButton) && homeLoginButton.IsEnabled && Descendants(layout).OfType<Button>().Count(button => Displayed(button) && button.Command == loginModel.NewAccountLoginCommand) == 1 && ((TextBlock)window.FindName("EmptyAccountTitle")).Text == "还没有账号" && loginTransport.RequestCount == 0 && newBrowser.OpenCount == 0);
        AccountOnboardingViewModel OpenNewLogin(bool follow = true, string proxy = "")
        {
            newBrowser.SystemProxy = follow ? "http://127.0.0.1:7890" : proxy;
            loginModel.NewAccountLoginCommand.Execute(null); var input = loginModel.Onboarding!; input.AuthorizeCommand.Execute(null); return input;
        }
        async Task AuthorizeNewLogin(bool follow = true, string proxy = "")
        {
            var input = OpenNewLogin(follow, proxy); await TwoStageLoginVerification.ContinueAsync(loginModel, loginWorkspace.Accounts!, input.CurrentOperation); await input.CurrentOperation; Flush();
        }
        newBrowser.SystemProxy = null;
        loginModel.NewAccountLoginCommand.Execute(null); var routeInput = loginModel.Onboarding!;
        Check("login opens two choices without proxy discovery or network requests", routeInput.UseCurrentNetwork && !routeInput.IsStarting && !routeInput.HasError
            && loginTransport.RequestCount == 0 && newBrowser.OpenCount == 0 && routeInput.AuthorizeCommand.CanExecute(null));
        routeInput.UseDedicatedIp = true;
        Check("independent mode waits for a qualified IP without manual port fields", !routeInput.AuthorizeCommand.CanExecute(null) && routeInput.Pool!.Results.Count == 0);
        Capture("login-two-modes-empty-pool", 1040, 670);
        routeInput.UseCurrentNetwork = true; routeInput.AuthorizeCommand.Execute(null);
        await routeInput.CurrentOperation; Capture("login-auto-discovery-unavailable");
        Check("login button attempts discovery and shows actionable retry without proxy forms", routeInput.HasError && routeInput.CanRetry
            && routeInput.Error.Contains("TUN") && routeInput.Error.Contains("不会直连") && loginModel.IsOnboardingOpen
            && window.FindName("OnboardingProxyBox") is null && window.FindName("OnboardingAdvancedToggle") is null
            && newBrowser.OpenCount == 0 && loginTransport.RequestCount == 0 && loginWorkspace.Profiles.ListAccounts().Count == 0);
        var initialLoginTheme = loginModel.DarkTheme;
        foreach (var dark in new[] { false, true })
        {
            loginModel.DarkTheme = dark; Capture("login-auto-" + (dark ? "dark" : "light") + "-minimum", 1040, 670);
            var statusText = (TextBlock)window.FindName("OnboardingStatusText"); var dialog = FindAncestor<Border>(statusText)!;
            Check((dark ? "dark" : "light") + " automatic login keeps status and retry visible", Displayed(dialog) && Displayed(statusText)
                && Displayed((Button)window.FindName("OnboardingAuthorizeButton")) && dialog.BorderThickness.Left >= 1
                && UiVerification.ContrastRatio(dialog.BorderBrush, dialog.Background) >= 1.5 && loginTransport.RequestCount == 0 && newBrowser.OpenCount == 0);
        }
        loginModel.DarkTheme = initialLoginTheme;
        routeInput.CloseCommand.Execute(null);
        await AutomaticLoginProxyVerification.RunAsync(Path.Combine(fixtureContainer, "automatic-login"), Check);
        await LocalEnvironmentVerification.RunAsync(Path.Combine(fixtureContainer, "local-environment"), Check);
        await LocalAccountImportVerification.RunAsync(Path.Combine(fixtureContainer, "local-import"), Check);
        await LoginStartVerification.RunAsync(window, Path.Combine(fixtureContainer, "login-start-choice"), Check, name => Capture(name, 1040, 670));
        await AccountNetworkModeVerification.RunAsync(Path.Combine(fixtureContainer, "account-network-modes"), Check);
        await BackgroundAccountTestsVerification.RunAsync(Path.Combine(fixtureContainer, "background-account-tests"), Check);
        await CialloHistoryVerification.RunAsync(Path.Combine(directory, "ciallo-history"), Check, UiVerification.ArrangeForCapture);
        await SafetyBufferingVerification.RunAsync(Path.Combine(directory, "safety-buffering"), Check, UiVerification.ArrangeForCapture);
        await CialloCancellationVerification.RunAsync(Path.Combine(directory, "ciallo-cancellation"), Check, UiVerification.ArrangeForCapture);
        await ProxyAccessibilityVerification.RunExportAsync(Path.Combine(directory, "proxy-export"), Check, UiVerification.ArrangeForCapture);
        await ReviewLifecycleVerification.RunAsync(Path.Combine(fixtureContainer, "review-lifecycle"), Check);
        await ReviewRecoveryVerification.RunAsync(Path.Combine(fixtureContainer, "review-recovery"), Check);
        var loginProtection = loginWorkspace.ProxyServices.FixtureProtectionRunner!;
        loginProtection.FailApply = true;
        newBrowser.FailureMessage = "fixture browser reached without score admission";
        await AuthorizeNewLogin();
        Check("login without cached score reaches browser without firewall or score requests", loginModel.IsError
            && loginModel.Feedback.Contains(newBrowser.FailureMessage)
            && newBrowser.OpenCount == 0 && loginProtection.Requests.Count == 0
            && loginTransport.Requests.All(request => !NetCoffeeService.IsLookup(request.Url)) && OnlyPublicLoginProbes());
        loginProtection.FailApply = false;
        var acceptedScore = loginTransport.ScorePayload;
        foreach (var rejectedScore in new[] { "{\"ip\":\"192.0.2.10\",\"fraudScore\":16}", "{\"ip\":\"192.0.2.10\"}" })
        {
            loginWorkspace.ProxyServices.NodeTesting!.ResetFixtureScore();
            loginTransport.ScorePayload = rejectedScore;
            await loginWorkspace.ProxyServices.NodeTesting!.ProbeScoredExitAsync("http://127.0.0.1:7897", default);
            var requestsBeforeLogin = loginTransport.RequestCount;
            await AuthorizeNewLogin();
            Check("login ignores insufficient or unknown score without requesting a new score", loginModel.IsError
                && loginModel.Feedback.Contains(newBrowser.FailureMessage)
                && loginTransport.Requests.Skip(requestsBeforeLogin).All(request => request.Url == AccountEndpoints.Egress || request.Url == AccountEndpoints.Trace)
                && newBrowser.OpenCount == 0 && OnlyPublicLoginProbes() && loginWorkspace.Profiles.ListAccounts().Count == 0
                && loginModel.NewAccountLoginCommand.CanExecute(null));
        }
        loginTransport.ScorePayload = acceptedScore;
        loginWorkspace.ProxyServices.NodeTesting!.ResetFixtureScore();
        await loginWorkspace.ProxyServices.NodeTesting!.ProbeScoredExitAsync("http://127.0.0.1:7897", default);
        var loginRegion = loginWorkspace.Accounts.OfflineRegion!;
        Check("browser score policy change never changes system region", loginRegion.TimezoneWrites == 0 && loginRegion.RegionWrites == 0);
        loginRegion.AllowTimezone = false;
        newBrowser.FailureMessage = "专用浏览器出口保护未确认，未打开授权网站。";
        await AuthorizeNewLogin();
        Check("login does not require permission to modify Windows timezone", loginModel.IsError && loginModel.Feedback.Contains("出口保护未确认")
            && newBrowser.OpenCount == 0 && OnlyPublicLoginProbes() && loginRegion.RegionWrites == 0 && loginRegion.State.WindowsTimezone == "China Standard Time");
        loginRegion.AllowTimezone = true;
        newBrowser.FailureMessage = "专用浏览器出口保护未确认，未打开授权网站。";
        await AuthorizeNewLogin(); Capture("login-protection-blocked");
        Check("current-network login preserves Windows region and timezone", loginRegion.State == new RegionState(true, 45, "China Standard Time") && loginRegion.TimezoneWrites == 0 && loginRegion.RegionWrites == 0);
        Check("strict authorization safety failure surfaces reason without browser token request or blank account", loginModel.IsError && loginModel.Feedback.Contains("出口保护未确认") && newBrowser.OpenCount == 0 && OnlyPublicLoginProbes() && loginWorkspace.Profiles.ListAccounts().Count == 0 && !loginModel.IsBusy);
        newBrowser.FailureMessage = "无法确认真实出口 IP 与对应时区，未打开授权网站。";
        await AuthorizeNewLogin();
        Check("strict authorization egress failure preserves precise cause and allows retry", loginModel.IsError && loginModel.Feedback.Contains("真实出口 IP") && loginModel.NewAccountLoginCommand.CanExecute(null) && newBrowser.OpenCount == 0 && OnlyPublicLoginProbes());
        newBrowser.FailureMessage = null;
        loginWorkspace.ProxyServices.NodeTesting!.ResetFixtureScore();
        loginTransport.ScorePayload = "{\"ip\":\"192.0.2.10\"}";
        var cleanupsBeforePendingLogin = newBrowser.CleanupCalls;
        var requestsBeforePendingLogin = loginTransport.RequestCount;
        loginWorkspace.Accounts.OfflineCallbacks!.HoldCallback = true;
        var cancelInput = OpenNewLogin(false, "http://127.0.0.1:7897"); var cancelledHomeLogin = cancelInput.CurrentOperation;
        await BackgroundAccountTestsVerification.Until(() => loginWorkspace.Accounts.Login.Status.Stage == LoginStage.AccountPreparation);
        var callbacksBeforePreparation = loginWorkspace.Accounts.OfflineCallbacks.Starts;
        Check("first step opens password reset entry without OAuth or callback", newBrowser.LastUrl?.AbsoluteUri == "https://chatgpt.com/auth/login"
            && callbacksBeforePreparation == 0 && !loginWorkspace.Accounts.Login.CanResumeBrowser
            && loginModel.LoginContinueLabel == "重新登录并授权" && !loginModel.ResumeLoginCommand.CanExecute(null) && OnlyPublicLoginProbes());
        Capture("login-account-preparation-minimum", 1040, 670);
        await TwoStageLoginVerification.ContinueAsync(loginModel, loginWorkspace.Accounts!, cancelledHomeLogin);
        for (var attempt = 0; attempt < 100 && (loginWorkspace.Accounts.Login.Status.Stage != LoginStage.Browser || !((Button)window.FindName("HomeCancelLoginButton")).IsEnabled); attempt++) await Task.Delay(20);
        for (var attempt = 0; attempt < 50 && loginModel.FeedbackTitle != "等待浏览器授权"; attempt++) await Task.Delay(20);
        Check("missing score cache keeps opened authorization pending without closing browser", loginWorkspace.Accounts.Login.Status.Stage == LoginStage.Browser
            && newBrowser.CleanupCalls == cleanupsBeforePendingLogin + 1 && loginWorkspace.Profiles.ListAccounts().Count == 0
            && loginTransport.Requests.Skip(requestsBeforePendingLogin).All(request => request.Url == AccountEndpoints.Egress || request.Url == AccountEndpoints.Trace));
        loginTransport.ScorePayload = acceptedScore;
        Check("browser authorization progress replaces stale preparation and explains unlimited wait", loginModel.FeedbackTitle == "等待浏览器授权"
            && loginModel.Feedback.Contains("新的 Codex 授权") && loginModel.LoginStageLabel.Contains("不自动超时"));
        var resumeUrl = newBrowser.LastUrl; var resumeOpenCount = newBrowser.OpenCount;
        var resumeRequests = loginTransport.Requests.Count;
        Check("waiting login has an explicit continue authorization button", ((Button)window.FindName("HomeResumeLoginButton")).IsEnabled
            && loginModel.Feedback.Contains("继续本次授权"));
        loginModel.ResumeLoginCommand.Execute(null);
        for (var attempt = 0; attempt < 100 && newBrowser.ResumeCount == 0; attempt++) await Task.Delay(10);
        Check("continue authorization preserves the pending flow without scoring or a second login", newBrowser.ResumeCount == 1
            && newBrowser.LastUrl == resumeUrl && newBrowser.OpenCount == resumeOpenCount
            && loginTransport.Requests.Count == resumeRequests && loginWorkspace.Accounts.Login.Status.Stage == LoginStage.Browser);
        Capture("login-authorizing-minimum", 1040, 670);
        Check("pending authorization empty state does not direct users to disabled login or absent accounts", loginModel.EmptyTitle == "正在添加账号" && loginModel.EmptyDescription.Contains("Codex 授权")
            && !loginModel.EmptyDescription.Contains("点击上方") && loginModel.EmptyDetailDescription.Contains("完成授权后") && ((TextBlock)window.FindName("EmptyAccountTitle")).Text == loginModel.EmptyTitle);
        foreach (var dark in new[] { true, false })
        {
            loginModel.DarkTheme = dark; Capture("login-disabled-list-" + dark, 1040, 670);
            var list = (ListBox)window.FindName("ProfileList");
            var bitmap = new RenderTargetBitmap(1040, 670, 96, 96, PixelFormats.Pbgra32); bitmap.Render(layout);
            var location = list.TransformToAncestor(layout).Transform(new Point(list.ActualWidth / 2, list.ActualHeight - 3));
            var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect((int)location.X, (int)location.Y, 1, 1), pixel, 4, 0);
            var color = Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
            Check("disabled account list keeps theme " + dark, !list.IsEnabled && color == ((SolidColorBrush)window.FindResource("SurfaceBrush")).Color);
        }
        var homeCancelButton = (Button)window.FindName("HomeCancelLoginButton");
        Check("selected route opens authorization with visible progress and cancel", !loginModel.IsOnboardingOpen && !loginModel.IsEditorOpen && !loginModel.IsDetailOpen && loginModel.IsNewAccountLogin && loginModel.IsBusy && Displayed(homeCancelButton) && homeCancelButton.IsEnabled && newBrowser.Proxy == "http://127.0.0.1:7897" && newBrowser.LastProfile == LoginService.NewAccountRequestProfile && newBrowser.LastUrl?.Host == AccountEndpoints.Authorize.Host && loginWorkspace.Profiles.ListAccounts().Count == 0 && OnlyPublicLoginProbes());
        homeCancelButton.Command.Execute(null); await cancelledHomeLogin; Flush();
        Check("cancel restores actionable empty account guidance", loginModel.EmptyTitle == "还没有账号" && loginModel.EmptyDescription.Contains("点击上方") && loginModel.EmptyDetailDescription.StartsWith("登录账号后"));
        Check("cancel home authorization creates no empty profile and enables retry", loginWorkspace.Profiles.ListAccounts().Count == 0 && !loginModel.IsBusy && !loginModel.IsNewAccountLogin && loginModel.NewAccountLoginCommand.CanExecute(null) && loginModel.Feedback.Contains("取消") && OnlyPublicLoginProbes() && !File.Exists(Path.Combine(loginHome, "auth.json")));
        Check("intentional cancellation is neutral and does not demand another authorization", !loginModel.IsError && loginModel.FeedbackTitle == "登录已取消" && !loginModel.HasFeedbackNextAction);
        loginWorkspace.Accounts.OfflineCallbacks.HoldCallback = false; loginTransport.FailTokenRequests = true;
        await AuthorizeNewLogin(false, "http://127.0.0.1:7897"); loginTransport.FailTokenRequests = false;
        Check("failed token exchange leaves no profile and can retry", loginModel.IsError && loginWorkspace.Profiles.ListAccounts().Count == 0 && loginModel.NewAccountLoginCommand.CanExecute(null) && !File.Exists(Path.Combine(loginHome, "auth.json")));
        loginModel.Search = "不会命中新账号"; await AuthorizeNewLogin();
        var recordedProfile = loginWorkspace.Accounts.Login.Status.Result!.Profile;
        Capture("login-account-recorded");
        Check("successful home authorization automatically saves and selects exact returned profile", !loginModel.IsEditorOpen && !loginModel.IsBusy && loginModel.Selected?.Name == recordedProfile && loginModel.Search == "" && loginWorkspace.Profiles.ListAccounts().Single().HasCredentials && File.Exists(Path.Combine(loginWorkspace.Profiles.ProfileDirectory(new(IdentityKind.Account, recordedProfile)), "auth.json")) && loginModel.Active is null && !File.Exists(Path.Combine(loginHome, "auth.json")));
        Check("automatic route is pinned and token exchange uses exact browser proxy", !loginWorkspace.Profiles.ReadEnvironment(recordedProfile).FollowSystemProxy && loginWorkspace.Profiles.ReadEnvironment(recordedProfile).Proxy == "http://127.0.0.1:7890" && loginWorkspace.Profiles.ReadEnvironment(recordedProfile).Timezone == "" && loginTransport.Requests.Last().Proxy == newBrowser.Proxy && newBrowser.Proxy == "http://127.0.0.1:7890");
        Check("completed cancelled and failed authorization invoke only their session cleanup", newBrowser.CleanupCalls >= 3 && loginWorkspace.Profiles.ListAccounts().Single().HasCredentials);
        newBrowser.CleanupWarnings = ["授权会话已结束，临时浏览器资料暂未清理；不会阻止下次登录。"];
        await AuthorizeNewLogin();
        Check("cache-only cleanup warning preserves saved authorization and actionable login without false failure", !loginModel.IsError && !loginModel.IsBusy
            && loginModel.Feedback.Contains("不会阻止下次登录") && loginModel.NewAccountLoginCommand.CanExecute(null));
        newBrowser.CleanupWarnings = [];
        var disposeBeforeCleanupFailure = newBrowser.DisposeCalls; newBrowser.CleanupSucceeds = false; await AuthorizeNewLogin();
        Check("saved authorization is retained when browser cleanup is unconfirmed and UI reports cleanup separately", loginWorkspace.Profiles.ListAccounts().Single().HasCredentials && loginModel.IsError && loginModel.FeedbackTitle == "登录未完成" && loginModel.Feedback.Contains("授权资料保留") && !loginModel.IsBusy && newBrowser.DisposeCalls == disposeBeforeCleanupFailure);
        newBrowser.CleanupSucceeds = true;
        loginModel.Search = "不匹配的搜索"; Capture("login-search-no-result");
        Check("search miss differs from a new empty account list", loginModel.HasSearchMiss && !loginModel.HasNoAccounts && ((TextBlock)window.FindName("EmptyAccountTitle")).Text.Contains("匹配"));
        loginModel.AccountFilter = "待登录";
        loginModel.ClearSearchCommand.Execute(null);
        Check("clear filters restores saved accounts without changing their records", loginModel.Search == "" && loginModel.AccountFilter == "全部账号" && loginModel.FilteredItems.Any(i => i.Name == recordedProfile) && loginWorkspace.Profiles.ListAccounts().Single().HasCredentials);
        loginModel.Selected = loginModel.FilteredItems.Single();
        Capture("home-one-account-garden");
        Check("one account keeps full-height viewport and no enabled paging", loginModel.ItemCount == 1 && compactTable.ActualHeight > 440
            && compactDetail.ActualHeight < 390 && !loginModel.HasIdentityPages
            && UiVerification.HomeDetailsFullyVisible(window, layout, loginModel, Path.Combine(directory, "one-detail-failure.json")));
        Check("interactive landscape is behind opaque account controls with foliage pinned to bottom", compactGarden.Illustration.Visibility == Visibility.Visible
            && compactGarden.Illustration.ActualWidth > 700 && compactGarden.IsHitTestVisible && !compactGarden.Focusable
            && Panel.GetZIndex((UIElement)VisualTreeHelper.GetParent(compactTable)) > Panel.GetZIndex(compactGarden)
            && Panel.GetZIndex(compactDetail) > Panel.GetZIndex(compactGarden)
            && compactGarden.Illustration.VerticalAlignment == VerticalAlignment.Bottom
            && Math.Abs(UiVerification.Bounds(compactGarden.Illustration, layout).Bottom - UiVerification.Bounds(compactGarden, layout).Bottom) < 2);
        Capture("home-one-account-minimum", 1040, 670);
        Check("minimum landscape hides the right plant cluster instead of exposing occluded fragments", compactGarden.Region("tree-east").Visibility == Visibility.Hidden
            && compactGarden.Region("flowers-east").Visibility == Visibility.Hidden && compactGarden.Region("tree-west").Visibility == Visibility.Visible);
        Check("one account minimum window retains all actions and complete row", UiVerification.HomeDetailsFullyVisible(window, layout, loginModel, Path.Combine(directory, "one-minimum-detail-failure.json"))
            && UiVerification.IdentityRowsFullyVisible((ListBox)window.FindName("ProfileList"), layout, loginModel, Path.Combine(directory, "one-minimum-row-failure.json")));
        loginModel.DarkTheme = true; Capture("home-one-account-dark", 1040, 670); loginModel.DarkTheme = false;
        loginWorkspace.Profiles.SaveEnvironment(recordedProfile, new("http://127.0.0.1:7893", "America/Chicago", "en-US"));
        loginWorkspace.Profiles.CreateBoundAccount("旧待登录目标", new("http://127.0.0.1:7894"));
        loginModel.ClearSearchCommand.Execute(null); loginModel.RefreshCommand.Execute(null); loginModel.Selected = loginModel.FilteredItems.Single(i => i.Name == "旧待登录目标"); await loginModel.SwitchSelectedAsync(true);
        Check("old pending switch exists before explicit home login", loginModel.NeedsLogin);
        await AuthorizeNewLogin(false, "http://127.0.0.1:7898");
        Check("repeated home authorization updates exact account and selected route while preserving other environment", loginWorkspace.Profiles.ListAccounts().Count == 2 && loginModel.Selected?.Name == recordedProfile && loginModel.Active is null && !loginModel.NeedsLogin && loginWorkspace.Profiles.ReadEnvironment(recordedProfile).Proxy == "http://127.0.0.1:7898" && !loginWorkspace.Profiles.ReadEnvironment(recordedProfile).FollowSystemProxy && loginWorkspace.Profiles.ReadEnvironment(recordedProfile).Timezone == "America/Chicago" && loginWorkspace.Profiles.ReadEnvironment(recordedProfile).Locale == "en-US" && !loginWorkspace.Profiles.ListAccounts().Single(a => a.Name == "旧待登录目标").HasCredentials && !File.Exists(Path.Combine(loginHome, "auth.json")));
        newBrowser.SystemProxy = null;
        var tunRoot = loginWorkspace.ProxyServices.FlClashRoot; Directory.CreateDirectory(tunRoot);
        File.WriteAllText(Path.Combine(tunRoot, "config.yaml"), "mixed-port: 7897\nbind-address: '*'\nauthentication: []\ntun: {enable: true}\n");
        loginWorkspace.Accounts.OfflineProxyListeners!.Add(new("FlClashCore.exe", "127.0.0.1", 7897));
        loginModel.NewAccountLoginCommand.Execute(null); var tunInput = loginModel.Onboarding!; tunInput.AuthorizeCommand.Execute(null); await TwoStageLoginVerification.ContinueAsync(loginModel, loginWorkspace.Accounts!, tunInput.CurrentOperation); await tunInput.CurrentOperation; Flush();
        Check("current-network choice completes fixture OAuth via TUN core without a system proxy or manual fields", !loginModel.IsBusy && !loginModel.IsOnboardingOpen
            && newBrowser.SystemProxy is null && newBrowser.Proxy == "http://127.0.0.1:7897"
            && loginWorkspace.Profiles.ReadEnvironment(recordedProfile).Proxy == newBrowser.Proxy && loginTransport.Requests.Last().Proxy == newBrowser.Proxy);
        loginModel.OpenDetailsCommand.Execute(loginModel.Selected); Capture("login-management-single-entry");
        Check("management has no second OAuth start button", !Descendants(layout).OfType<Button>().Any(button => button.Command == loginModel.LoginCommand) && Descendants(layout).OfType<Button>().Count(button => button.Command == loginModel.NewAccountLoginCommand) == 1);
        loginModel.CloseDetailsCommand.Execute(null);
        var agentDirectory = Path.Combine(directory, "agent-fixtures"); Directory.CreateDirectory(agentDirectory);
        await AgentLoginVerification.RunAsync(Path.Combine(agentDirectory, "dedicated-login"), Check);
        foreach (var command in new[] { "capabilities", "browser-status", "status", "exit", "invalid-field" })
        {
            var requestFile = Path.Combine(agentDirectory, command + "-request.json");
            var reportFile = Path.Combine(agentDirectory, command + "-report.json");
            File.WriteAllText(requestFile, command == "invalid-field" ? "{\"command\":\"status\",\"unsupported\":true}" : JsonSerializer.Serialize(new { command }));
            var beforeAgent = loginTransport.RequestCount;
            var code = await AgentCli.RunAsync(loginWorkspace, requestFile, reportFile);
            var reportText = File.ReadAllText(reportFile); var reportData = JsonNode.Parse(reportText)!;
            Check("Agent " + command + " reports explicit results without credentials", code == (command == "invalid-field" ? 1 : 0)
                && reportData["state"]?.GetValue<string>() == (command == "invalid-field" ? "failed" : "completed")
                && !reportText.Contains("fixture-access-") && !reportText.Contains("fixture-refresh-")
                && (command == "exit" ? reportData["data"]?["quality"]?["admission"]?["allowed"]?.GetValue<bool>() == true : loginTransport.RequestCount == beforeAgent));
        }
        var dedicatedSource = Path.Combine(tunRoot, "profiles", "dedicated.yaml"); Directory.CreateDirectory(Path.GetDirectoryName(dedicatedSource)!);
        File.WriteAllText(dedicatedSource, "proxies:\n - {name: 家宽验证, type: socks5, server: fixture.invalid, port: 443}\n");
        var agentNodes = loginWorkspace.ProxyServices.NodeTesting!;
        var agentSub = agentNodes.Subscriptions(ProxyClientKind.FlClash, tunRoot).Subscriptions.Single();
        var agentNode = agentNodes.LoadSubscription(agentSub, tunRoot).Nodes.Single();
        var selectionRequest = Path.Combine(agentDirectory, "test-nodes-request.json");
        var selectionReport = Path.Combine(agentDirectory, "test-nodes-report.json");
        File.WriteAllText(selectionRequest, JsonSerializer.Serialize(new { command = "test-nodes", selections = new[] { new AgentNodeSelection(agentSub.Id, agentNode.Id), new AgentNodeSelection(agentSub.Id, "missing") } }));
        var beforeSelectionRequests = loginTransport.RequestCount;
        Check("invalid explicit CLI batch starts no other nodes", await AgentCli.RunAsync(loginWorkspace, selectionRequest, selectionReport) == 1
            && loginTransport.RequestCount == beforeSelectionRequests && agentNodes.OfflineRuntime!.StartedIds.Count == 0);
        var latencySource = Path.Combine(tunRoot, "profiles", "latency-status.yaml");
        File.WriteAllText(latencySource, "proxies:\n - {name: latency-ok, type: socks5, server: fixture.invalid, port: 443}\n - {name: unsupported, type: fixture-unsupported, server: fixture.invalid, port: 443}\n");
        try
        {
            var latencySub = agentNodes.Subscriptions(ProxyClientKind.FlClash, tunRoot).Subscriptions.Single(item => item.File == latencySource);
            var latencyNodes = agentNodes.LoadSubscription(latencySub, tunRoot).Nodes;
            foreach (var expected in new[] { "partial", "failed", "completed" })
            {
                loginTransport.DelayTimeout = expected == "failed";
                var chosen = expected == "completed" ? latencyNodes.Where(item => item.Supported) : latencyNodes;
                var input = Path.Combine(agentDirectory, "latency-" + expected + "-request.json");
                var output = Path.Combine(agentDirectory, "latency-" + expected + "-report.json");
                File.WriteAllText(input, JsonSerializer.Serialize(new { command = "test-nodes", delayOnly = true, selections = chosen.Select(item => new AgentNodeSelection(latencySub.Id, item.Id)) }));
                var beforeLatency = loginTransport.Requests.Count;
                var latencyCode = await AgentCli.RunAsync(loginWorkspace, input, output);
                var latencyResult = JsonNode.Parse(File.ReadAllText(output))!;
                Check("latency-only CLI reports " + expected + " without IP score or account requests", latencyCode == (expected == "completed" ? 0 : expected == "partial" ? 3 : 1)
                    && latencyResult["state"]?.GetValue<string>() == expected
                    && loginTransport.Requests.Skip(beforeLatency).All(item => item.Url == ProxyLatencyService.Endpoint)
                    && latencyResult["data"]?["scoreRequests"]?.GetValue<int>() == 0);
            }
        }
        finally { loginTransport.DelayTimeout = false; File.Delete(latencySource); }
        var loginPool = loginModel.ProxyPanel.NodeTesting;
        loginPool.LoadCommand.Execute(null); await loginPool.CurrentOperation; loginPool.ScreenCommand.Execute(null); await loginPool.CurrentOperation;
        var pickerPool = new NodeTestingViewModel(null, () => (ChunsonSpace.Proxy.ProxyClientKind.FlClash, ""));
        var pendingIpe = new NodeSelectionItem(loginPool.Items.First(item => item.Supported).Node, pickerPool.RefreshResults, "ipe", "IPEqual 一档1号");
        var pendingOther = new NodeSelectionItem(pendingIpe.Node, pickerPool.RefreshResults, "other", "其他订阅");
        pickerPool.Items.Add(pendingIpe); pickerPool.Items.Add(pendingOther);
        var noticeFile = Path.Combine(fixtureContainer, "picker-notices.yaml");
        File.WriteAllText(noticeFile, "proxies:\n  - {name: '(赠送)日本娱乐节点', type: http, server: fixture.invalid, port: 80}\n  - {name: '累计流量 30GB', type: http, server: fixture.invalid, port: 80}\n  - {name: '日均降至10GB后,请更新订阅', type: http, server: fixture.invalid, port: 80}\n  - {name: '建议注册多个账号，使用多个住宅IP', type: http, server: fixture.invalid, port: 80}\n  - {name: '这是运营商限制,感谢理解与支持', type: http, server: fixture.invalid, port: 80}\n");
        foreach (var node in new ChunsonSpace.NodeTesting.NodeCatalogImporter().Read(noticeFile, fixtureContainer).Nodes)
        {
            var hidden = new NodeSelectionItem(node, () => { }, "ipe", "IPEqual 一档1号");
            if (hidden.IsEntertainment) hidden.Apply(new(node.Id, node.Name, node.Type, true, loginPool.Results.First().Quality, "fixture"));
            pickerPool.Items.Add(hidden);
            Check("IPEqual login omits entertainment or metadata " + node.Name, !pickerPool.LoginChoices.Contains(hidden) && !hidden.CanPrepareLogin);
        }
        using (var picker = new AccountOnboardingViewModel(_ => throw new InvalidOperationException(), (_, _, _) => Task.CompletedTask, _ => { }, () => { }, pickerPool,
            (_, _) => throw new InvalidOperationException("offline-only")))
        {
            picker.UseDedicatedIp = true;
            Check("unknown IPE and other routes stay out of both qualified lists", pickerPool.LoginChoices.Count == 0
                && pickerPool.Results.Count == 0 && picker.SelectedIp is null && !picker.AuthorizeCommand.CanExecute(null) && pendingIpe.ChoiceLabel.Contains("待检测"));
            var observed = loginPool.Results.First().Quality!;
            pendingIpe.Apply(new(pendingIpe.Node.Id, pendingIpe.Name, pendingIpe.Type, true, observed with { Score = new(16, "fixture", true) }, "fixture"));
            Check("IPEqual low score stays out of login and screening results", !pickerPool.LoginChoices.Contains(pendingIpe)
                && pickerPool.Results.Count == 0 && picker.SelectedIp is null && !picker.AuthorizeCommand.CanExecute(null));
            pendingIpe.Apply(new(pendingIpe.Node.Id, pendingIpe.Name, pendingIpe.Type, true, observed, "fixture"));
            Check("live IPEqual score update preserves selection and enables qualified login", picker.SelectedIp == pendingIpe && pendingIpe.ChoiceLabel.Contains("95 分") && picker.AuthorizeCommand.CanExecute(null));
            var alternate = new NodeSelectionItem(pendingIpe.Node, pickerPool.RefreshResults, "ipe", "IPEqual 一档1号");
            alternate.Apply(new(alternate.Node.Id, alternate.Name, alternate.Type, true, observed with { Score = new(3, "fixture", true) }, "fixture"));
            pickerPool.Items.Add(alternate);
            Check("each IPE subscription keeps only its best scored route and preserves selected subscription", pickerPool.LoginChoices.SequenceEqual(new[] { alternate }) && picker.SelectedIp == alternate);
            var otherIpe = new NodeSelectionItem(pendingIpe.Node, pickerPool.RefreshResults, "ipe-2", "IQEqual 独享");
            pickerPool.Items.Add(otherIpe);
            Check("unmeasured IPE subscription cannot enter login choices", pickerPool.LoginChoices.Count == 1 && !pickerPool.LoginChoices.Contains(otherIpe));
            otherIpe.Apply(new(otherIpe.Node.Id, otherIpe.Name, otherIpe.Type, true, observed, "fixture"));
            Check("separate qualified IPE subscriptions remain distinct choices", pickerPool.LoginChoices.Count == 2 && pickerPool.LoginChoices.Contains(otherIpe));
            picker.SelectedIp = otherIpe;
            otherIpe.Apply(new(otherIpe.Node.Id, otherIpe.Name, otherIpe.Type, false, null, "fixture timeout", new(ProxyLatencyState.Timeout, null, "fixture")));
            Check("timed-out selected IPE route is removed and selection recovers to a qualified route", otherIpe.ChoiceLabel.Contains("连接超时")
                && !pickerPool.LoginChoices.Contains(otherIpe) && picker.SelectedIp == alternate && picker.AuthorizeCommand.CanExecute(null));
            otherIpe.Apply(new(otherIpe.Node.Id, otherIpe.Name, otherIpe.Type, false, observed with { Score = new(null, "评分服务限流") }, "fixture"));
            Check("verified IP without score stays out of login choices", otherIpe.ChoiceLabel.Contains("评分暂不可用")
                && otherIpe.ChoiceExplanation.Contains("评分服务限流") && !pickerPool.LoginChoices.Contains(otherIpe));
            otherIpe.Apply(new(otherIpe.Node.Id, otherIpe.Name, otherIpe.Type, false, observed, "fixture connection failed", new(ProxyLatencyState.Failed, null, "fixture")));
            Check("failed route with historical high score cannot enter login choices", !pickerPool.LoginChoices.Contains(otherIpe) && !otherIpe.CanPrepareLogin);
            otherIpe.Apply(new(otherIpe.Node.Id, otherIpe.Name, otherIpe.Type, true, observed with { Score = new(15, "fixture", true) }, "fixture"));
            Check("recovered IPE route at85 reenters login without requesting authentication", pickerPool.LoginChoices.Contains(otherIpe)
                && otherIpe.CanPrepareLogin && otherIpe.QualityScore == 85 && pickerPool.LoginChoices.All(item => item.CanPrepareLogin));
        }
        var currentScoreQueries = loginTransport.Requests.Count(request => NetCoffeeService.IsLookup(request.Url));
        var cachedAdmission = await agentNodes.ProbeCachedExitAsync("http://127.0.0.1:7897", CancellationToken.None);
        Check("current network admission reuses background IP score", cachedAdmission.Admission.Allowed && loginTransport.Requests.Count(request => NetCoffeeService.IsLookup(request.Url)) == currentScoreQueries);
        loginTransport.FailRequests = true;
        var failedExit = await agentNodes.ProbeCachedExitAsync("http://127.0.0.1:7897", CancellationToken.None);
        loginTransport.FailRequests = false;
        Check("cached score never bypasses current exit verification", !failedExit.Admission.Allowed);
        var originalMainConfig = File.ReadAllText(Path.Combine(tunRoot, "config.yaml"));
        loginModel.NewAccountLoginCommand.Execute(null); var dedicatedInput = loginModel.Onboarding!; dedicatedInput.UseDedicatedIp = true;
        Capture("login-two-modes-qualified", 1040, 670);
        Check("dedicated selector lists country quality and IP without port input", dedicatedInput.SelectedIp?.QualityScore == 95
            && dedicatedInput.SelectedIp.ExitIp == "192.0.2.10" && dedicatedInput.SelectedIp.Country == "美国"
            && dedicatedInput.SelectedIp.ChoiceLabel == "美国 · 95 分 · 192.0.2.10" && dedicatedInput.AuthorizeCommand.CanExecute(null));
        loginRegion.State = new(true, 45, "China Standard Time");
        var loginRegionWrites = (loginRegion.TimezoneWrites, loginRegion.RegionWrites);
        var loginCulture = (System.Globalization.CultureInfo.CurrentCulture.Name, System.Globalization.CultureInfo.CurrentUICulture.Name);
        dedicatedInput.UseCurrentNetwork = true; dedicatedInput.UseDedicatedIp = true;
        Check("IP picker changes do not apply system configuration", loginRegionWrites == (loginRegion.TimezoneWrites, loginRegion.RegionWrites));
        loginTransport.EgressTimezone = "America/Phoenix";
        agentNodes.ResetFixtureScore();
        var scoresBeforeDedicated = loginTransport.Requests.Count(request => NetCoffeeService.IsLookup(request.Url));
        var previousObserver = loginTransport.RequestObserved;
        var requestsBeforeDedicatedLogin = loginTransport.RequestCount;
        loginTransport.RequestObserved = request =>
        {
            previousObserver?.Invoke(request);
            if (request.Url == NetCoffeeService.TraceEndpoint || NetCoffeeService.IsLookup(request.Url))
                throw new AccountException(AccountError.Network, "fixture screening provider unavailable after successful selection");
        };
        dedicatedInput.AuthorizeCommand.Execute(null); await TwoStageLoginVerification.ContinueAsync(loginModel, loginWorkspace.Accounts!, dedicatedInput.CurrentOperation); await dedicatedInput.CurrentOperation; Flush();
        loginTransport.RequestObserved = previousObserver;
        var independentProxy = loginWorkspace.Profiles.ReadEnvironment(recordedProfile).Proxy;
        Check("dedicated login pins automatic port and preserves main FlClash config", !loginModel.IsError && independentProxy != "http://127.0.0.1:7897"
            && independentProxy == newBrowser.Proxy && loginTransport.Requests.Last().Proxy == independentProxy
            && File.ReadAllText(Path.Combine(tunRoot, "config.yaml")) == originalMainConfig);
        Check("selected scored IP survives missing score cache at dedicated login with zero repeat scoring requests", !loginModel.IsError
            && loginTransport.Requests.Count(request => NetCoffeeService.IsLookup(request.Url)) == scoresBeforeDedicated);
        Check("screened dedicated login succeeds even if the screening provider is unavailable without querying it again", !loginModel.IsError
            && loginTransport.Requests.Skip(requestsBeforeDedicatedLogin).All(request => request.Url != NetCoffeeService.TraceEndpoint
                && !NetCoffeeService.IsLookup(request.Url) && request.Proxy == independentProxy));
        Check("dedicated browser login does not change Windows region or timezone", loginRegion.State == new RegionState(true, 45, "China Standard Time") && loginRegionWrites == (loginRegion.TimezoneWrites, loginRegion.RegionWrites)
            && loginCulture == (System.Globalization.CultureInfo.CurrentCulture.Name, System.Globalization.CultureInfo.CurrentUICulture.Name));
        await loginWorkspace.Accounts.DisposeAsync(); window.DataContext = model;
        var importRoot = Path.Combine(fixtureContainer, "首次导入工作区"); var importHome = Path.Combine(fixtureContainer, "首次导入活动目录");
        Directory.CreateDirectory(importRoot); Directory.CreateDirectory(importHome);
        File.WriteAllText(Path.Combine(importRoot, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        File.Copy(Path.Combine(home, "auth.json"), Path.Combine(importHome, "auth.json"));
        var importWorkspace = new DesktopWorkspace(importRoot, importHome, null, null, fixture: true);
        await importWorkspace.ProxyServices.NodeTesting!.ProbeScoredExitAsync("http://127.0.0.1:7895", default);
        var importModel = new ShellViewModel(importWorkspace);
        Check("unmatched valid current auth can enter save current", importModel.ItemCount == 0 && importModel.Active is null && importModel.SaveCurrentAccountCommand.CanExecute(null));
        importModel.SaveCurrentAccountCommand.Execute(null); var importEditor = importModel.Editor!; importEditor.Name = "首次导入"; importEditor.SaveCommand.Execute(null); await importEditor.CurrentOperation;
        Check("first import saves unknown current identity into profile list", importModel.ItemCount == 1 && importModel.Active?.Name == "首次导入" && importWorkspace.Profiles.ListAccounts()[0].HasCredentials);
        importWorkspace.Profiles.CreateBoundAccount("独立登录目标", new("http://127.0.0.1:7895")); importModel.RefreshCommand.Execute(null);
        importModel.Selected = importModel.FilteredItems.Single(i => i.Name == "独立登录目标"); await importModel.SwitchSelectedAsync(true);
        importModel.Selected = importModel.FilteredItems.Single(i => i.Name == "首次导入"); await importModel.SwitchSelectedAsync(false);
        importModel.Selected = importModel.FilteredItems.Single(i => i.Name == "独立登录目标"); var importedLogin = importModel.LoginSelectedAsync(); await TwoStageLoginVerification.ContinueAsync(importModel, importWorkspace.Accounts!, importedLogin); await importedLogin;
        Check("new switch clears stale login continuation intent", importModel.Active?.Name == "首次导入" && !importModel.NeedsLogin && importWorkspace.Profiles.ListAccounts().Single(a => a.Name == "独立登录目标").HasCredentials);
        void Select(string name) { model.Selected = model.FilteredItems.Single(i => i.Name == name); Flush(); }
        Check("explicit fixture reads real profiles and current identity", !model.IsPreview && model.ItemCount == 2 && model.Active?.Name == "工作甲");
        Select("工作甲"); var requestsBeforeHome = workspace.Accounts!.OfflineTransport!.RequestCount;
        Capture("storage-home-unverified");
        Check("real storage home keeps detection unknown and system protection optional without requests", model.SelectedExitIp == "未验证"
            && model.SelectedExitTimezoneLabel == "配置时区" && model.SelectedNetworkProtection == "未检查"
            && model.SelectedDnsProtection == "未检查" && model.SelectedRuntimeState == "未运行"
            && workspace.Accounts.OfflineTransport.RequestCount == requestsBeforeHome && UiVerification.HomeDetailsFullyVisible(window, layout, model, Path.Combine(directory, "home-detail-layout-failure.json")));
        await model.RunAccountActionAsync(AccountAction.Egress);
        Check("save current maintenance starts with detected selected account evidence", model.SelectedExitIp == "192.0.2.10");
        Check("IP result modal prevents background account context actions", model.IsDiagnosticOpen && !window.SelectContextIdentity(new ListBoxItem { DataContext = model.FilteredItems.Single(item => item.Name == "工作乙") }));
        model.CloseDiagnosticCommand.Execute(null);
        model.SaveCurrentAccountCommand.Execute(null); await SaveEditor();
        Check("save current requires explicit overwrite choice", model.IsEditorOpen && model.Editor!.HasError && !File.Exists(Path.Combine(store.ProfileDirectory(new(IdentityKind.Account, "工作甲")), "installation_id")));
        Check("failed save current maintenance preserves existing selected account evidence", model.SelectedExitIp == "192.0.2.10");
        model.Editor!.Overwrite = true; await SaveEditor();
        var currentDirectory = store.ProfileDirectory(new(IdentityKind.Account, "工作甲"));
        Check("save current captures state sessions and installation identity", !model.IsEditorOpen && File.Exists(Path.Combine(currentDirectory, "sessions", "fixture-session.jsonl")) && File.Exists(Path.Combine(currentDirectory, "installation_id")) && JsonNode.Parse(File.ReadAllText(Path.Combine(currentDirectory, ".codex-global-state.json")))?[SwitchFileRules.InstallationKey]?.GetValue<string>() == File.ReadAllText(Path.Combine(currentDirectory, "installation_id")));
        Check("successful save current maintenance invalidates cached egress and runtime state", model.SelectedExitIp == "未验证" && model.SelectedRuntimeState == "未运行" && model.IdentityState.Running is null);
        model.NewProfileCommand.Execute(null);
        model.Editor!.Name = "新建待登录"; model.Editor.Proxy = "http://127.0.0.1:7892";
        await SaveEditor();
        Check("new account creates proxy-bound empty profile", !model.IsEditorOpen && store.ListAccounts().Count == 3 && !store.ListAccounts().Single(a => a.Name == "新建待登录").HasCredentials && store.ReadEnvironment("新建待登录").Proxy == "http://127.0.0.1:7892");
        model.OpenEditor(ProfileEditMode.Create); model.Editor!.Name = "非法代理"; model.Editor.Proxy = "socks5://127.0.0.1:7890"; await SaveEditor();
        Check("invalid account proxy shows actionable validation without writes", model.Editor.HasError && !Directory.Exists(store.ProfileDirectory(new(IdentityKind.Account, "非法代理"))));
        Capture("storage-editor-validation"); model.CloseEditor();
        Select("工作甲"); UiVerification.ArrangeForCapture(layout, 1280, 850);
        var profileList = (ListBox)window.FindName("ProfileList");
        ListBoxItem AccountRow(string name) => (ListBoxItem)profileList.ItemContainerGenerator.ContainerFromItem(model.FilteredItems.Single(item => item.Name == name));
        var editRow = AccountRow("工作乙");
        Check("right clicking another account selects that account before popup actions", window.SelectContextIdentity(editRow)
            && model.Selected?.Name == "工作乙" && ReferenceEquals(profileList.SelectedItem, model.Selected) && model.Active?.Name == "工作甲");
        foreach (var dark in new[] { false, true })
        {
            model.DarkTheme = dark; UiVerification.ArrangeForCapture(layout, 1280, 850);
            CaptureAccountMenu("account-context-menu-" + (dark ? "dark" : "light"), editRow);
        }
        model.DarkTheme = false;
        var editMenu = UiVerification.ArrangeAccountContextMenu(editRow);
        var editAction = editMenu.Items.OfType<MenuItem>().Single(item => item.Command == model.EditSelectedCommand);
        editAction.Command!.Execute(editAction.CommandParameter);
        Check("popup exit configuration edits the clicked account and blocks background context selection", model.Editor?.Original?.Name == "工作乙"
            && model.Editor.Mode == ProfileEditMode.Edit && !window.SelectContextIdentity(AccountRow("工作甲")) && model.Selected?.Name == "工作乙");
        model.Editor!.Proxy = "https://127.0.0.1:7893"; model.Editor.Locale = "zh-CN"; await SaveEditor();
        Check("environment edits persist and reload", store.ReadEnvironment("工作乙").Proxy == "https://127.0.0.1:7893" && model.Selected?.Locale == "zh-CN");
        UiVerification.ArrangeForCapture(layout, 1280, 850); Select("工作甲");
        var renameRow = AccountRow("工作乙"); Check("rename context selects the clicked account", window.SelectContextIdentity(renameRow));
        var renameAction = UiVerification.ArrangeAccountContextMenu(renameRow).Items.OfType<MenuItem>().Single(item => item.Command == model.RenameSelectedCommand);
        renameAction.Command!.Execute(renameAction.CommandParameter);
        Check("popup rename opens the clicked account", model.Editor?.Original?.Name == "工作乙" && model.Editor.Mode == ProfileEditMode.Rename);
        model.Editor!.Name = "工作乙改"; await SaveEditor();
        Check("rename updates profile and environment directories", !Directory.Exists(store.ProfileDirectory(new(IdentityKind.Account, "工作乙"))) && store.ReadEnvironment("工作乙改").Proxy == "https://127.0.0.1:7893" && model.Selected?.Name == "工作乙改");
        UiVerification.ArrangeForCapture(layout, 1280, 850); Select("工作甲");
        var deleteRow = AccountRow("新建待登录"); Check("delete context selects the clicked account", window.SelectContextIdentity(deleteRow));
        var deleteAction = UiVerification.ArrangeAccountContextMenu(deleteRow).Items.OfType<MenuItem>().Single(item => item.Command == model.ArchiveSelectedCommand);
        var stopCallsBeforeDelete = workspace.OfflineRuntime!.StopCalls;
        deleteAction.Command!.Execute(deleteAction.CommandParameter);
        Check("popup delete opens confirmation for the clicked account without changing storage", model.Editor?.Original?.Name == "新建待登录"
            && model.Editor.Mode == ProfileEditMode.Archive && model.Editor.Title == "删除账号" && model.Editor.SaveLabel == "确认删除"
            && store.ListAccounts().Count == 3 && workspace.OfflineRuntime.StopCalls == stopCallsBeforeDelete);
        model.Editor!.CancelCommand.Execute(null); Flush();
        Check("cancel delete preserves account environment and runtime without archive execution", !model.IsEditorOpen && store.ListAccounts().Count == 3
            && Directory.Exists(store.ProfileDirectory(new(IdentityKind.Account, "新建待登录")))
            && store.ReadEnvironment("新建待登录").Proxy == "http://127.0.0.1:7892" && workspace.OfflineRuntime.StopCalls == stopCallsBeforeDelete);
        deleteAction.Command.Execute(deleteAction.CommandParameter); workspace.OfflineRuntime.StopSucceeds = false; await SaveEditor();
        Check("archive stop failure leaves account and editor intact", model.IsEditorOpen && model.Editor!.HasError && store.ListAccounts().Count == 3);
        workspace.OfflineRuntime.StopSucceeds = true; await SaveEditor();
        Check("archive moves profile into recovery directory", store.ListAccounts().Count == 2 && Directory.GetDirectories(Path.Combine(root, "xy_clean", "CodexX-01", "Boss")).Length > 0);
        Check("account archive records event markers for shared rotation", Directory.GetFiles(Path.Combine(root, "xy_clean", "CodexX-01", "Boss"), ProfileMaintenance.AccountArchiveMarker, SearchOption.AllDirectories).Length >= 1);
        Select("工作甲"); Check("active profile cannot be renamed or archived", !model.RenameSelectedCommand.CanExecute(null) && !model.ArchiveSelectedCommand.CanExecute(null));
        UiVerification.ArrangeForCapture(layout, 1280, 850);
        var activeMenu = UiVerification.ArrangeAccountContextMenu(AccountRow("工作甲"));
        var activeDelete = activeMenu.Items.OfType<MenuItem>().Single(item => item.Command == model.ArchiveSelectedCommand);
        Check("active account delete popup item is disabled", !activeDelete.IsEnabled && !activeDelete.Command!.CanExecute(activeDelete.CommandParameter)
            && !model.IsEditorOpen && store.ListAccounts().Count == 2);
        store.SaveProvider("历史供应商", "https://fixture.example.test/v1", "fixture-legacy-key");
        var legacyProviderBytes = File.ReadAllBytes(Path.Combine(store.ProfileDirectory(new(IdentityKind.Provider, "历史供应商")), "auth.json"));
        var beforeRetiredPage = model.Page; model.Navigate("Providers"); model.RefreshCommand.Execute(null);
        Check("retired provider page and editor are removed without deleting legacy files", model.Page == beforeRetiredPage && model.FilteredItems.All(item => item.Kind == "Account")
            && window.FindName("NavProviders") is null && window.FindName("ProviderKeyBox") is null
            && File.ReadAllBytes(Path.Combine(store.ProfileDirectory(new(IdentityKind.Provider, "历史供应商")), "auth.json")).SequenceEqual(legacyProviderBytes));
        model.Navigate("Settings"); model.DarkTheme = false; model.IsolateSessions = false; model.AutoSystemTimezone = false; model.QuitConfirm = false; model.SaveSettingsCommand.Execute(null);
        var savedSettings = store.ReadSettings();
        Check("settings persist independently of system environment", !savedSettings.SessionIsolation && !savedSettings.AutoSystemTimezone && !savedSettings.QuitConfirm);
        Capture("storage-settings");
        var settingsAdvanced = (CheckBox)window.FindName("SettingsAdvancedToggle");
        Check("settings start in simple mode with original cleanup hidden and no region writes", settingsAdvanced.IsChecked == false
            && !Displayed((Button)window.FindName("OriginalCodexResetButton"))
            && Displayed((Button)window.FindName("OneClickEnvironmentRegionButton"))
            && !model.ResetManagedCommand.CanExecute(null)
            && workspace.Accounts!.OfflineRegion!.TimezoneWrites == 0 && workspace.Accounts.OfflineRegion.RegionWrites == 0);
        settingsAdvanced.IsChecked = true;
        var lightCanvas = ((SolidColorBrush)window.FindResource("CanvasBrush")).Color;
        model.DarkTheme = true; model.SaveSettingsCommand.Execute(null); Flush();
        Check("dark theme persists and changes WPF palette", store.ReadSettings().WorkspaceTheme == "dark" && WorkspaceTheme.Current == "dark"
            && ((SolidColorBrush)window.FindResource("CanvasBrush")).Color != lightCanvas && ((Panel)layout).Background == window.FindResource("CanvasBrush")
            && UiVerification.ContrastRatio((Brush)window.FindResource("InkBrush"), (Brush)window.FindResource("SurfaceBrush")) >= 4.5);
        Capture("storage-settings-dark");
        Capture("storage-settings-dark-minimum", 1040, 670);
        var settingsCommands = new[] { model.SaveSettingsCommand, model.LogoutCommand, model.ExitCommand, model.InspectInstallationCommand, model.InstallCodexCommand, model.InspectTimezoneChannelCommand, model.EnableTimezoneChannelCommand };
        Check("settings functional entrances stay visible without scrolling at minimum size", settingsCommands.All(command => Descendants(layout).OfType<Button>().Any(button => button.Command == command && Displayed(button))));
        settingsAdvanced.IsChecked = false;
        var reopened = new ShellViewModel(workspace);
        Check("workspace reopens with persisted dark theme", reopened.DarkTheme && WorkspaceTheme.Current == "dark");
        model.DarkTheme = false; model.SaveSettingsCommand.Execute(null);
        model.Navigate("Accounts"); Select("工作乙改"); model.IsolateSessions = true;
        await model.SwitchSelectedAsync(false); Flush();
        Check("switch migrates real fixture files and reloads active identity", model.Active?.Name == "工作乙改" && workspace.SwitchStorage.ReadActiveIdentity()?.Name == "工作乙改" && workspace.OfflineRuntime.StopCalls > 0);
        Check("switch leaves running identity independently unset", model.IdentityState.Running is null);
        Select("工作甲"); workspace.OfflineRuntime.LaunchStatus = LaunchStatus.Failed;
        ((Button)window.FindName("BottomLaunchButton")).Command.Execute(null); await model.CurrentOperation; Flush();
        Check("launch failure rolls real files back and retains recovery materials", model.Active?.Name == "工作乙改" && model.FeedbackTitle.Contains("原身份已恢复") && model.Feedback.Contains("恢复材料"));
        Capture("storage-switch-rolled-back");
        workspace.OfflineRuntime.LaunchStatus = LaunchStatus.Pending; model.HomeLaunchCommand.Execute(null); await model.CurrentOperation; Flush();
        Check("pending launch preserves target and does not claim running", model.Active?.Name == "工作甲" && model.RunningLabel == "启动待确认" && model.IdentityState.Running is null && workspace.OfflineRuntime.HasPendingLaunch && model.SelectedRuntimeState == "启动待确认");
        var beforePendingRestore = File.ReadAllBytes(Path.Combine(home, "auth.json"));
        Check("pending launch blocks backup restore", beforePendingRestore.SequenceEqual(File.ReadAllBytes(Path.Combine(home, "auth.json"))) && model.Feedback.Contains("待确认"));
        Capture("storage-pending-launch-minimum", 1040, 670);
        workspace.OfflineRuntime.HasPendingLaunch = false; workspace.OfflineRuntime.LaunchStatus = LaunchStatus.Confirmed;
        model.Navigate("Accounts"); Select("工作乙改"); model.HomeLaunchCommand.Execute(null); await model.CurrentOperation; Flush();
        Check("saved account switch and launch use shared application service", model.Active?.Name == "工作乙改" && model.IdentityState.Running?.Name == "工作乙改" && model.RunningLabel.Contains("隔离替身"));
        Check("rendered text excludes fixture keys and tokens", !TextValues(layout).Any(text => text.Contains("fixture-key-FA") || text.Contains("fixture-token-")));
        model.Navigate("Accounts"); model.NewProfileCommand.Execute(null); model.Editor!.Name = "待登录目标"; model.Editor.Proxy = "http://127.0.0.1:7894"; await SaveEditor();
        model.HomeLaunchCommand.Execute(null); await model.CurrentOperation; Flush();
        Check("empty account returns login target without changing activity", model.NeedsLogin && model.LoginTarget?.Name == "待登录目标" && model.Active?.Name == "工作乙改");
        Check("home launch exposes matching login continuation instead of restarting the switch", model.HomeLaunchLabel == "继续登录并启动"
            && model.HomeLaunchCommand.CanExecute(null) && ((Button)window.FindName("BottomLaunchButton")).Content?.ToString() == model.HomeLaunchLabel);
        Select("工作甲"); await model.LaunchCurrentAsync();
        Check("launch current ignores a different selected target", model.Selected?.Name == "工作甲" && model.Active?.Name == "工作乙改" && model.IdentityState.Running?.Name == "工作乙改");
        Check("another selected identity does not inherit old account login continuation", model.HomeLaunchLabel == "切换并启动");
        Select("待登录目标"); await model.SwitchSelectedAsync(true);
        workspace.Accounts!.OfflineCallbacks!.HoldCallback = true;
        model.LoginCommand.Execute(null); var cancelledLogin = model.CurrentOperation;
        await TwoStageLoginVerification.ContinueAsync(model, workspace.Accounts!, cancelledLogin);
        for (var attempt = 0; attempt < 100 && (workspace.Accounts.Login.Status.Stage != LoginStage.Browser || !model.CancelLoginCommand.CanExecute(null)); attempt++) await Task.Delay(20);
        Check("login exposes browser stage and cancellation command", workspace.Accounts.Login.Status.Stage == LoginStage.Browser && model.CancelLoginCommand.CanExecute(null));
        model.CancelLoginCommand.Execute(null); await cancelledLogin;
        Check("cancelled login leaves credentials and continuation absent", !store.ListAccounts().Single(a => a.Name == "待登录目标").HasCredentials && !model.NeedsLogin && model.Active?.Name == "工作乙改");
        await model.SwitchSelectedAsync(true);
        var editedLoginEnvironment = store.ReadEnvironment("待登录目标") with { Proxy = "http://127.0.0.1:7896", Timezone = "Asia/Shanghai" };
        store.SaveEnvironment("待登录目标", editedLoginEnvironment); model.RefreshCommand.Execute(null); Select("待登录目标");
        var tokensBeforeResume = workspace.Accounts.OfflineTransport!.Requests.Count(request => request.Url == AccountEndpoints.Token);
        var authorizationsBeforeResume = workspace.Accounts.OfflineTransport.Requests.Count(request => request.Url == AccountEndpoints.Token && request.ContentType == "application/x-www-form-urlencoded");
        model.HomeLaunchCommand.Execute(null); var resumedLogin = model.CurrentOperation;
        await TwoStageLoginVerification.ContinueAsync(model, workspace.Accounts!, resumedLogin);
        for (var attempt = 0; attempt < 100 && (workspace.Accounts.Login.Status.Stage != LoginStage.Browser || !model.CancelLoginCommand.CanExecute(null)); attempt++) await Task.Delay(20);
        var continuationCancelButton = (Button)window.FindName("HomeCancelLoginButton");
        UiVerification.ArrangeForCapture(layout, 1040, 670);
        var cancelVisibilityWaitIterations = 0;
        // Login stage can precede the existing 200 ms dispatcher notification that updates the cancel bar.
        while (cancelVisibilityWaitIterations < 100 && !Displayed(continuationCancelButton))
        {
            await Task.Delay(20); cancelVisibilityWaitIterations++;
            UiVerification.ArrangeForCapture(layout, 1040, 670);
        }
        var continuationRequests = workspace.Accounts.OfflineTransport.RequestCount;
        var continuationUrl = ((OfflineLoginBrowser)workspace.Accounts.Browser).LastUrl;
        model.OpenDetailsCommand.Execute(null);
        Capture("login-dialog-single-progress-minimum", 1040, 670);
        Check("management owns the only visible login progress strip without restarting authorization", !Displayed((FrameworkElement)window.FindName("HomeLoginStrip"))
            && Displayed((FrameworkElement)window.FindName("DetailLoginStrip")) && model.IsLoggingIn
            && workspace.Accounts.OfflineTransport.RequestCount == continuationRequests && ((OfflineLoginBrowser)workspace.Accounts.Browser).LastUrl == continuationUrl);
        model.CloseDetailsCommand.Execute(null);
        UiVerification.ArrangeForCapture(layout, 1040, 670);
        Check("closing management restores home progress without cancellation or another request", Displayed((FrameworkElement)window.FindName("HomeLoginStrip"))
            && !Displayed((FrameworkElement)window.FindName("DetailLoginStrip")) && model.IsLoggingIn && workspace.Accounts.OfflineTransport.RequestCount == continuationRequests);
        Capture("home-continue-login-busy-minimum", 1040, 670);
        var pendingHomeLoginConditions = new Dictionary<string, bool>
        {
            ["busy"] = model.IsBusy,
            ["launchCommandDisabled"] = !model.HomeLaunchCommand.CanExecute(null),
            ["launchButtonDisabled"] = !((Button)window.FindName("BottomLaunchButton")).IsEnabled,
            ["selectionDisabled"] = !((ListBox)window.FindName("ProfileList")).IsEnabled,
            ["searchDisabled"] = !((TextBox)window.FindName("SearchBox")).IsEnabled,
            ["filterDisabled"] = !((ComboBox)window.FindName("AccountFilterBox")).IsEnabled,
            ["navigationDisabled"] = !model.NavigateCommand.CanExecute("Settings"),
            ["regionDisabled"] = !model.ConfigureRegionCommand.CanExecute(null),
            ["cancelCommandEnabled"] = model.CancelLoginCommand.CanExecute(null),
            ["cancelButtonDisplayed"] = Displayed(continuationCancelButton)
        };
        File.WriteAllText(Path.Combine(directory, "home-continue-login-busy-conditions.json"), JsonSerializer.Serialize(new
        {
            conditions = pendingHomeLoginConditions, stage = workspace.Accounts.Login.Status.Stage.ToString(),
            model.IsLoggingIn, model.IsNewAccountLogin, model.CanSelectIdentity,
            cancelButtonVisibility = continuationCancelButton.Visibility.ToString(), cancelVisibilityWaitIterations,
            launchLabel = model.HomeLaunchLabel
        }, new JsonSerializerOptions { WriteIndented = true }));
        Check("pending home login blocks duplicate launch selection filtering navigation and region but permits cancel", pendingHomeLoginConditions.Values.All(value => value));
        Select("工作甲"); workspace.Accounts.OfflineCallbacks.HoldCallback = false; await resumedLogin;
        Check("OAuth success resumes original switch target despite changed selection", model.Active?.Name == "待登录目标" && model.IdentityState.Running?.Name == "待登录目标" && store.ListAccounts().Single(a => a.Name == "待登录目标").HasCredentials);
        var continuationTokens = workspace.Accounts.OfflineTransport.Requests.Where(request => request.Url == AccountEndpoints.Token).ToArray();
        var authorizationsAfterResume = continuationTokens.Count(request => request.ContentType == "application/x-www-form-urlencoded");
        var continuationActualEnvironment = store.ReadEnvironment("待登录目标");
        var continuationLaunchProxy = workspace.OfflineRuntime.LastLaunchRequest?.Environment?.GetValueOrDefault("HTTP_PROXY");
        var continuationConditions = new Dictionary<string, bool>
        {
            ["launchUsesEditedProxy"] = continuationLaunchProxy == editedLoginEnvironment.Proxy,
            ["storedProxyPreserved"] = continuationActualEnvironment.Proxy == editedLoginEnvironment.Proxy,
            ["storedTimezonePreserved"] = continuationActualEnvironment.Timezone == editedLoginEnvironment.Timezone,
            ["storedLocalePreserved"] = continuationActualEnvironment.Locale == editedLoginEnvironment.Locale,
            ["storedLocaleAutoPreserved"] = continuationActualEnvironment.LocaleAuto == editedLoginEnvironment.LocaleAuto,
            ["storedFollowSystemProxyPreserved"] = continuationActualEnvironment.FollowSystemProxy == editedLoginEnvironment.FollowSystemProxy,
            ["exactlyOneAuthorizationCodeExchange"] = authorizationsAfterResume == authorizationsBeforeResume + 1,
            ["allNewTokenRequestsUseEditedProxy"] = continuationTokens.Skip(tokensBeforeResume).All(request => request.Proxy == editedLoginEnvironment.Proxy)
        };
        File.WriteAllText(Path.Combine(directory, "home-continuation-environment-and-authorization.json"), JsonSerializer.Serialize(new
        {
            conditions = continuationConditions, launchProxy = continuationLaunchProxy,
            expectedEnvironment = editedLoginEnvironment, actualEnvironment = continuationActualEnvironment,
            tokensBeforeResume, tokensAfterResume = continuationTokens.Length, authorizationsBeforeResume, authorizationsAfterResume,
            newTokenRequests = continuationTokens.Skip(tokensBeforeResume).Select(request => new
            {
                url = request.Url.AbsoluteUri, request.Proxy, request.ContentType,
                kind = request.ContentType == "application/x-www-form-urlencoded" ? "authorization-code-exchange"
                    : request.ContentType == "application/json" ? "credential-refresh" : "unknown"
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Check("home continuation rereads same-account edited proxy and saves exactly one authorization", continuationConditions.Values.All(value => value));
        Select("待登录目标"); await model.RunAccountActionAsync(AccountAction.Quota);
        Check("quota displays windows reset-credit expiry state and balance", model.DiagnosticDetails.Contains("79.0%") && model.DiagnosticDetails.Contains("到期：") && model.DiagnosticDetails.Contains("状态：available") && model.DiagnosticDetails.Contains("余额：12.00") && File.Exists(Path.Combine(store.ProfileDirectory(new(IdentityKind.Account, "待登录目标")), "quota.json")));
        Capture("storage-account-quota-minimum", 1040, 670);
        var quotaResult = (TextBox)window.FindName("IpDiagnosticResult");
        Check("quota completion opens readable results without scrolling account information", model.IsDiagnosticOpen && Displayed(quotaResult)
            && quotaResult.VerticalOffset == 0 && quotaResult.ExtentHeight <= quotaResult.ViewportHeight + 1
            && Displayed((TextBlock)window.FindName("AccountResultTitle")) && Displayed((Button)window.FindName("AccountResultDoneButton")));
        model.DarkTheme = true; Capture("storage-account-quota-dark-minimum", 1040, 670); model.DarkTheme = false;
        model.CloseDiagnosticCommand.Execute(null); model.OpenDetailsCommand.Execute(null); Flush();
        var beforeReopen = workspace.Accounts!.OfflineTransport!.RequestCount;
        Check("last result action remains visible in account management", Displayed((Button)window.FindName("AccountLastResultButton")) && model.OpenDiagnosticCommand.CanExecute(null));
        model.OpenDiagnosticCommand.Execute(null); Flush();
        Check("viewing last result does not request quota again", model.IsDiagnosticOpen && workspace.Accounts.OfflineTransport.RequestCount == beforeReopen);
        model.CloseDiagnosticCommand.Execute(null); model.CloseDetailsCommand.Execute(null);
        workspace.Accounts.OfflineTransport.FailRequests = true; await model.RunAccountActionAsync(AccountAction.Quota); Flush();
        Check("failed quota also opens result instead of a hidden feedback banner", model.IsDiagnosticOpen && model.DiagnosticTitle.Contains("未完成") && Displayed(quotaResult));
        workspace.Accounts.OfflineTransport.FailRequests = false; model.CloseDiagnosticCommand.Execute(null);
        await model.RunAccountActionAsync(AccountAction.Connectivity);
        Check("connectivity displays response facts without cache based capability claims", model.DiagnosticDetails.Contains("响应模型：") && model.DiagnosticDetails.Contains("响应：") && model.DiagnosticDetails.Contains("耗时：") && !model.DiagnosticDetails.Contains("缓存") && !model.DiagnosticDetails.Contains("降智") && !model.DiagnosticDetails.Contains("正常建议"));
        Check("connectivity completion uses the common visible result", model.IsDiagnosticOpen); model.CloseDiagnosticCommand.Execute(null);
        await model.RunAccountActionAsync(AccountAction.Egress); Check("egress uses explicit offline transport", model.DiagnosticDetails.Contains("192.0.2.10"));
        var regionFixture = workspace.Accounts!.OfflineRegion!;
        Check("ordinary account selection launch and egress detection never configure Windows region", regionFixture.TimezoneWrites == 0 && regionFixture.RegionWrites == 0);
        var environmentBeforeRegion = store.ReadEnvironment("待登录目标");
        var cultureBeforeRegion = (System.Globalization.CultureInfo.CurrentCulture.Name, System.Globalization.CultureInfo.CurrentUICulture.Name);
        model.Navigate("Settings"); Capture("region-simple-ready-minimum", 1040, 670);
        var regionButton = (Button)window.FindName("OneClickEnvironmentRegionButton");
        Check("simple environment has one visible enabled region command for the selected account", Displayed(regionButton)
            && regionButton.IsEnabled && regionButton.Command == model.ConfigureRegionCommand);
        regionButton.Command.Execute(null); await model.CurrentOperation; Flush();
        Check("explicit one click region configures fake exact country and timezone while preserving account environment and Chinese culture", !model.IsError
            && regionFixture.State == new RegionState(true, 244, "Central Standard Time") && regionFixture.TimezoneWrites == 1 && regionFixture.RegionWrites == 1
            && model.RegionStatus.Contains("US") && model.RegionStatus.Contains("America/Chicago")
            && store.ReadEnvironment("待登录目标") == environmentBeforeRegion
            && cultureBeforeRegion == (System.Globalization.CultureInfo.CurrentCulture.Name, System.Globalization.CultureInfo.CurrentUICulture.Name));
        Capture("region-simple-configured-minimum", 1040, 670);
        regionFixture.State = new(true, 45, "China Standard Time"); regionFixture.AllowTimezone = false;
        var regionWritesBeforeDenial = regionFixture.RegionWrites;
        model.ConfigureRegionCommand.Execute(null); await model.CurrentOperation;
        Check("declined fake timezone permission cannot report region success or write home location", model.IsError
            && model.FeedbackTitle == "地区与时区同步未完成" && regionFixture.RegionWrites == regionWritesBeforeDenial
            && regionFixture.State == new RegionState(true, 45, "China Standard Time"));
        regionFixture.AllowTimezone = true;
        var regionTransport = workspace.Accounts.OfflineTransport!;
        workspace.ProxyServices.NodeTesting!.ResetFixtureScore();
        regionTransport.HoldQualityResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timezoneWritesBeforeHold = regionFixture.TimezoneWrites; var regionWritesBeforeHold = regionFixture.RegionWrites;
        var regionRequestsBeforeHold = regionTransport.RequestCount;
        model.ConfigureRegionCommand.Execute(null); var heldRegion = model.CurrentOperation;
        for (var attempt = 0; attempt < 100 && regionTransport.RequestCount < regionRequestsBeforeHold + 2; attempt++) await Task.Delay(20);
        var regionBusyGuard = model.IsBusy && !model.ConfigureRegionCommand.CanExecute(null) && !model.HomeLaunchCommand.CanExecute(null)
            && !model.NavigateCommand.CanExecute("Accounts") && !model.CanSelectIdentity
            && regionFixture.TimezoneWrites == timezoneWritesBeforeHold && regionFixture.RegionWrites == regionWritesBeforeHold;
        Capture("region-verification-busy-minimum", 1040, 670);
        regionTransport.HoldQualityResponse.SetResult(); await heldRegion; regionTransport.HoldQualityResponse = null;
        Check("pending region exit verification blocks duplicate actions navigation and selection before any system write", regionBusyGuard);
        model.Navigate("Accounts"); Select("待登录目标");
        Capture("storage-home-detected-egress");
        Check("explicit offline egress populates selected identity evidence and detected timezone", model.SelectedExitIp == "192.0.2.10"
            && model.SelectedExitTimezone == "America/Chicago" && model.SelectedExitTimezoneLabel == "检测时区"
            && model.SelectedExitDetails.Contains("不代表当前连接持续在线") && UiVerification.HomeDetailsFullyVisible(window, layout, model, Path.Combine(directory, "home-detail-layout-failure.json")));
        Select("工作甲"); Check("selecting another identity cannot reuse previous detected IP", model.SelectedExitIp == "未验证" && model.SelectedExitTimezoneLabel == "配置时区");
        await model.RunAccountActionAsync(AccountAction.Egress);
        Select("待登录目标"); Check("returning to detected identity retains its matching evidence", model.SelectedExitIp == "192.0.2.10");
        var reloadedObservations = new ShellViewModel(workspace);
        reloadedObservations.Selected = reloadedObservations.FilteredItems.Single(item => item.Name == "待登录目标");
        Check("reopening the view restores per-account detected IP without network calls", reloadedObservations.SelectedExitIp == "192.0.2.10" && reloadedObservations.SelectedExitDetails.Contains("最近检测"));
        var detectedEnvironment = store.ReadEnvironment("待登录目标");
        store.SaveEnvironment("待登录目标", detectedEnvironment with { Timezone = "Asia/Tokyo" }); model.RefreshCommand.Execute(null); Select("待登录目标");
        Check("timezone-only edit preserves detected IP for the same proxy", model.SelectedExitIp == "192.0.2.10"
            && model.SelectedExitTimezone == "America/Chicago" && model.SelectedExitTimezoneLabel == "检测时区");
        store.SaveEnvironment("待登录目标", detectedEnvironment with { Proxy = "http://127.0.0.1:7999" }); model.RefreshCommand.Execute(null); Select("待登录目标");
        Check("changing proxy hides old observation instead of presenting it as the new route", model.SelectedExitIp == "未验证");
        store.SaveEnvironment("待登录目标", detectedEnvironment); model.RefreshCommand.Execute(null); Select("待登录目标");
        var qualityTransport = workspace.Accounts!.OfflineTransport!; var requestsBeforeQualityOpen = qualityTransport.RequestCount;
        model.OpenDetailsCommand.Execute(null); Capture("ip-quality-manual-entry");
        Check("IP quality remains an explicit manual action without automatic request", model.IpQualityCommand.CanExecute(null) && qualityTransport.RequestCount == requestsBeforeQualityOpen && ((Button)window.FindName("HomeConfigureExitButton")).Command == model.IpQualityCommand && window.FindName("IpQualityCheckButton") is null);
        Capture("storage-account-actions-minimum", 1040, 670);
        Check("common account actions stay visible at minimum size", DetailActionsVisible(model.OpenBrowserCommand, model.QuotaCommand, model.OpenModelTestCommand,
            model.RenameSelectedCommand, model.ArchiveSelectedCommand, model.SwitchCommand, model.EditSelectedCommand, model.CloseDetailsCommand)
            && qualityTransport.RequestCount == requestsBeforeQualityOpen);
        Check("account matching remains visible and token refresh is bound inside the usage menu", DetailActionsVisible(model.ApplyDefaultsCommand)
            && UiVerification.ArrangeMenu(((Button)window.FindName("UsageMenuButton")).ContextMenu, (Button)window.FindName("UsageMenuButton")).Items.OfType<MenuItem>().Single().Command == model.RefreshAccountCommand);
        Check("account consolidations remove duplicate launch and model buttons while retaining manual scopes", window.FindName("FingerprintOpenButton") is null && window.FindName("CialloOpenButton") is null
            && !Descendants(layout).OfType<Button>().Any(button => button.Command == model.LaunchCurrentCommand)
            && window.FindName("IpQualityCheckButton") is null && window.FindName("HomeEnvironmentManageButton") is null
            && !Descendants((DependencyObject)window.FindName("IdentityInfoScroll")).OfType<Button>().Any(button => button.Command == model.HomeLaunchCommand));
        Check("merged account actions do not restore duplicate buttons", !Descendants(layout).OfType<Button>().Any(button => button.Command == model.EgressCommand || button.Command == model.SuggestTimezoneCommand || button.Command == model.ConnectivityCommand));
        model.IpQualityCommand.Execute(null); await model.CurrentOperation;
        Check("IP quality uses same explicit proxy for independent data and retains unknown risk signals", !model.IsError && qualityTransport.Requests.Skip(requestsBeforeQualityOpen).All(request => request.Proxy == store.ReadEnvironment("待登录目标").Proxy && request.RequireProxy) && model.DiagnosticDetails.Contains("192.0.2.10") && model.DiagnosticDetails.Contains("数据中心：未知") && model.DiagnosticDetails.Contains("代理标记：未知") && model.DiagnosticDetails.Contains("VPN 标记：否") && model.DiagnosticDetails.Contains("不作为"));
        ((ScrollViewer)window.FindName("IdentityInfoScroll")).ScrollToEnd(); Capture("ip-quality-result-unknown-signals");
        var originalQualityPayload = qualityTransport.QualityPayload; qualityTransport.QualityPayload = "{\"ip\":\"192.0.2.11\",\"is_proxy\":false,\"is_datacenter\":false}";
        workspace.ProxyServices.NodeTesting!.ResetFixtureScore();
        await model.RunAccountActionAsync(AccountAction.IpQuality);
        Check("mismatched risk source retains independently verified exit while keeping risk labels unknown", model.IsError && model.DiagnosticDetails.Contains("不一致")
            && model.DiagnosticDetails.Contains("数据中心：未知") && model.DiagnosticDetails.Contains("代理标记：未知") && model.SelectedExitIp == "192.0.2.10"
            && model.DiagnosticDetails.Contains("Net.Coffee"));
        Check("partial IP test automatically opens readable result with the source failure", model.IsDiagnosticOpen && Displayed((TextBox)window.FindName("IpDiagnosticResult")) && ((TextBox)window.FindName("IpDiagnosticResult")).Text.Contains("不一致"));
        ((TextBox)window.FindName("IpDiagnosticResult")).ScrollToEnd(); Flush();
        model.CloseDiagnosticCommand.Execute(null);
        Check("IP result close does not clear its retained evidence", !model.IsDiagnosticOpen && model.HasDiagnostics);
        qualityTransport.QualityPayload = originalQualityPayload; model.CloseDetailsCommand.Execute(null);
        var environmentBeforeSuggestion = store.ReadEnvironment("待登录目标");
        await model.RunAccountActionAsync(AccountAction.SuggestTimezone);
        Flush(); Check("new result resets the previous long report scroll offset", model.IsDiagnosticOpen && ((TextBox)window.FindName("IpDiagnosticResult")).VerticalOffset == 0);
        Check("timezone suggestions do not overwrite environment", model.DiagnosticDetails.Contains(environmentBeforeSuggestion.Timezone)
            && store.ReadEnvironment("待登录目标") == environmentBeforeSuggestion);
        var missingDefaultsEnvironment = environmentBeforeSuggestion with { Timezone = "", Locale = "", LocaleAuto = false };
        store.SaveEnvironment("待登录目标", missingDefaultsEnvironment); model.RefreshCommand.Execute(null); Select("待登录目标");
        await model.RunAccountActionAsync(AccountAction.ApplyDefaults);
        Check("explicit environment match persists missing fields and displays details", store.ReadEnvironment("待登录目标")
            == (missingDefaultsEnvironment with { Timezone = "America/Chicago", Locale = "en-US", LocaleAuto = false }) && model.IsDiagnosticOpen && model.DiagnosticDetails.Contains("Windows 系统设置未"));
        model.CloseDiagnosticCommand.Execute(null);
        var standaloneBrowser = (OfflineLoginBrowser)workspace.Accounts.Browser;
        var standaloneOpens = standaloneBrowser.OpenCount; var standaloneScore = qualityTransport.ScorePayload;
        foreach (var payload in new[] { "{\"ip\":\"192.0.2.10\",\"fraudScore\":16}", "{\"ip\":\"192.0.2.10\"}" })
        {
            workspace.ProxyServices.NodeTesting!.ResetFixtureScore();
            qualityTransport.ScorePayload = payload;
            await workspace.ProxyServices.NodeTesting!.ProbeScoredExitAsync("http://127.0.0.1:7897", default);
            var requestsBeforeBrowser = qualityTransport.RequestCount;
            await model.OpenBrowserAsync();
            Check("standalone browser does not reapply score screening", !model.IsError && standaloneBrowser.OpenCount == ++standaloneOpens
                && qualityTransport.Requests.Skip(requestsBeforeBrowser).All(request => request.Url == AccountEndpoints.Egress || request.Url == AccountEndpoints.Trace));
        }
        qualityTransport.ScorePayload = "{\"ip\":\"192.0.2.10\",\"fraudScore\":15}";
        qualityTransport.FailRequests = true;
        await model.OpenBrowserAsync();
        Check("standalone browser still rejects unavailable exit without score lookup", model.IsError
            && standaloneBrowser.OpenCount == standaloneOpens && model.Feedback.Contains("真实出口 IP"));
        qualityTransport.FailRequests = false;
        workspace.ProxyServices.NodeTesting!.ResetFixtureScore();
        await workspace.ProxyServices.NodeTesting.ProbeScoredExitAsync("http://127.0.0.1:7897", default);
        await model.OpenBrowserAsync(); Check("standalone browser is distinct from OAuth", ((OfflineLoginBrowser)workspace.Accounts.Browser).LastUrl?.AbsoluteUri == "https://chatgpt.com/" && model.Feedback.Contains("隔离浏览器替身"));
        qualityTransport.ScorePayload = standaloneScore;
        workspace.ProxyServices.NodeTesting!.ResetFixtureScore();
        await model.RunAccountActionAsync(AccountAction.Refresh); Check("refresh writes fixture credentials without rendering them", store.ReadAccountCredentials("待登录目标")["last_refresh"] is not null && !model.DiagnosticDetails.Contains("fixture-access-"));
        workspace.Accounts.OfflineTransport!.FailRequests = true; await model.RunAccountActionAsync(AccountAction.Egress); Check("failed egress is not reported as successful completion", model.IsError && model.FeedbackTitle.Contains("未完成"));
        Check("failed egress retains last successful historical observation", model.SelectedExitIp == "192.0.2.10" && model.SelectedExitDetails.Contains("不代表当前连接持续在线"));
        store.SaveEnvironment("待登录目标", missingDefaultsEnvironment); model.RefreshCommand.Execute(null); Select("待登录目标");
        await model.RunAccountActionAsync(AccountAction.ApplyDefaults);
        Check("failed defaults probe is not reported successful", model.IsError && store.ReadEnvironment("待登录目标") == missingDefaultsEnvironment);
        workspace.Accounts.OfflineTransport.FailRequests = false;
        model.Navigate("Settings"); await model.RunInfrastructureAsync(InfrastructureAction.InspectInstallation); Check("installation state read remains an explicit action", model.InstallationDetails.Contains("已安装"));
        await model.RunInfrastructureAsync(InfrastructureAction.EnableTimezoneChannel); Check("channel registration does not imply timezone execution", model.TimezoneChannelDetails.Contains("注册不会修改当前系统时区") && model.TimezoneChannelDetails.Contains("实际执行能力仍需使用时确认"));
        model.Navigate("Accounts"); Select("待登录目标"); await model.RunAccountActionAsync(AccountAction.Egress);
        Check("logout maintenance begins with selected account egress evidence", model.SelectedExitIp == "192.0.2.10");
        Directory.CreateDirectory(Path.Combine(home, "sessions")); File.WriteAllText(Path.Combine(home, "sessions", "post-provider.jsonl"), "fixture-session-preserved");
        model.LogoutCommand.Execute(null); await SaveEditor();
        Check("logout clears current auth and retains session data", model.Active is null && !File.Exists(Path.Combine(home, "auth.json")) && Directory.Exists(Path.Combine(home, "sessions")) && model.Feedback.Contains("恢复材料"));
        Select("待登录目标"); Check("logout maintenance invalidates detected egress and confirmed runtime", model.SelectedExitIp == "未验证"
            && model.SelectedRuntimeState == "未运行" && model.IdentityState.Running is null);
        Directory.CreateDirectory(Path.Combine(home, "skills")); File.WriteAllText(Path.Combine(home, "skills", "fixture.md"), "fixture-skill");
        Directory.CreateDirectory(Path.Combine(home, "plugins")); File.WriteAllText(Path.Combine(home, "plugins", "fixture.json"), "{}");
        model.RebuildCommand.Execute(null);
        Check("rebuild review shows exact planned paths", model.Editor!.HasDetails && model.Editor.Details.Contains(home) && model.Editor.Details.Contains("保留"));
        Capture("storage-rebuild-review");
        await SaveEditor();
        Check("rebuild archives runtime state and retains accounts config skills plugins", !Directory.Exists(Path.Combine(home, "sessions")) && File.Exists(Path.Combine(home, "config.toml")) && Directory.Exists(Path.Combine(home, "skills")) && Directory.Exists(Path.Combine(home, "plugins")) && store.ListAccounts().Count == 3 && Directory.GetFiles(Path.Combine(root, "xy_clean", "CodexX-01", "Boss"), EnvironmentRebuild.Marker, SearchOption.AllDirectories).Length > 0);
        var lifetimeModel = new ShellViewModel(workspace) { QuitConfirm = true };
        var lifetimeWindow = UiVerification.CreateWindow(lifetimeModel);
        var tray = new FakeTray(); var lease = new FakeLease(); bool confirm = false, closeOk = true; int closeCalls = 0, shutdownCalls = 0;
        using (var lifecycle = new DesktopLifetime(lifetimeWindow, lifetimeModel, tray, lease, () => Task.FromResult(true), () => { closeCalls++; return Task.FromResult(closeOk); }, () => confirm, () => shutdownCalls++))
        {
            Check("lifetime connects tray and close-to-tray policy", tray.Visible && lifetimeModel.TrayAvailable && lifecycle.TryHideOnClose());
            Check("quit confirmation cancellation preserves owned runtime", !await lifecycle.RequestExitAsync() && closeCalls == 0 && shutdownCalls == 0);
            confirm = true; closeOk = false;
            Check("failed owned cleanup preserves window and retry", !await lifecycle.RequestExitAsync() && lifetimeModel.IsError && shutdownCalls == 0 && !tray.Disposed);
            UiVerification.AssertWindowsOutsideDesktop();
            Check("failed-exit fixture stays outside desktop without stealing focus", lifetimeWindow.IsVisible && !lifetimeWindow.IsActive && !lifetimeWindow.ShowInTaskbar);
            closeOk = true;
            Check("confirmed exit cleans own lifecycle resources", await lifecycle.RequestExitAsync() && shutdownCalls == 1 && tray.Disposed && lease.Disposed);
        }
        var cacheExitModel = new ShellViewModel(loginWorkspace); var cacheExitWindow = UiVerification.CreateWindow(cacheExitModel);
        var cacheExitTray = new FakeTray(); var cacheExitLease = new FakeLease(); var cacheExitShutdown = 0;
        newBrowser.CleanupWarnings = ["本次授权会话已释放；临时缓存保留。"];
        var exitCredential = File.ReadAllBytes(Path.Combine(loginWorkspace.Profiles.ProfileDirectory(new(IdentityKind.Account, recordedProfile)), "auth.json"));
        using (var cacheExitLife = new DesktopLifetime(cacheExitWindow, cacheExitModel, cacheExitTray, cacheExitLease, () => Task.FromResult(false),
            async () => { await loginWorkspace.Accounts!.DisposeAsync(); return true; }, shutdown: () => cacheExitShutdown++))
        {
            cacheExitTray.RequestExit(); Flush(); await Task.Delay(20); Flush();
            Check("tray exit releases lifecycle despite a cache-only warning and preserves account credentials", cacheExitShutdown == 1 && cacheExitTray.Disposed && cacheExitLease.Disposed
                && exitCredential.SequenceEqual(File.ReadAllBytes(Path.Combine(loginWorkspace.Profiles.ProfileDirectory(new(IdentityKind.Account, recordedProfile)), "auth.json"))));
        }
        model.Navigate("Accounts");
        model.Selected = model.FilteredItems.First();
        Capture("home-three-accounts-minimum", 1040, 670);
        Check("three accounts retain the full-height table without reducing capacity to their count", model.ItemCount == 3
            && model.IdentityPageItems.Count == 3 && compactTable.ActualHeight > 290 && PageRowActionsVisible()
            && !model.HasIdentityPages);
        for (var number = 1; store.ListAccounts().Count < 10; number++) store.CreateBoundAccount("扩展账号" + number.ToString("D2"), new("http://127.0.0.1:7890"));
        model.RefreshCommand.Execute(null); Check("ten account profiles are loaded without truncation", model.ItemCount == 10);
        Capture("accounts-ten-default");
        var defaultList = (ListBox)window.FindName("ProfileList");
        var operationBar = (Border)window.FindName("ActiveOperationBar");
        var bottomLaunch = (Button)window.FindName("BottomLaunchButton");
        var bottomRebuild = window.FindName("BottomRebuildButton");
        var exitDetail = (FrameworkElement)window.FindName("SelectedExitDetail");
        bool HomeActionsVisible() => Displayed(operationBar) && Displayed(bottomLaunch) && bottomRebuild is null
            && bottomLaunch.ActualHeight >= 36
            && UiVerification.Within(bottomLaunch, operationBar)
            
            && Displayed(exitDetail) && UiVerification.HomeDetailsFullyVisible(window, layout, model, Path.Combine(directory, "home-detail-layout-failure.json")) && UiVerification.DoNotOverlap(defaultList, exitDetail, layout)
            && UiVerification.Bounds(defaultList, layout).Bottom <= UiVerification.Bounds(operationBar, layout).Top + 1
            && UiVerification.Bounds(exitDetail, layout).Bottom <= UiVerification.Bounds(operationBar, layout).Top + 1;
        Check("default account table shows ten complete rows without ports and fixed selected-target actions", model.IdentityPageItems.Count == 10
            && PageRowActionsVisible() && HomeActionsVisible());
        model.DarkTheme = true; Capture("accounts-ten-borders-dark"); model.DarkTheme = false;
        var tenth = model.FilteredItems.Last(); var accountList = (ListBox)window.FindName("ProfileList");
        var nextPage = (Button)window.FindName("AccountNextPageButton");
        var listScroll = FindVisual<ScrollViewer>(accountList)!;
        Check("default table exposes tenth account without scrolling or unnecessary paging", model.IdentityPageItems.Contains(tenth)
            && !model.PreviousIdentityPageCommand.CanExecute(null) && !model.NextIdentityPageCommand.CanExecute(null) && listScroll.VerticalOffset == 0);
        model.Selected = tenth;
        Capture("accounts-ten-minimum", 1040, 670);
        Check("minimum window preserves selected tenth account and exposes complete rows pager and fixed controls", model.Selected == tenth && model.IdentityPageItems.Contains(tenth)
            && model.IdentityPageItems.Count < 10 && PageRowActionsVisible() && HomeActionsVisible()
            && Displayed((Button)window.FindName("AccountPreviousPageButton")) && Displayed(nextPage));
        model.Search = tenth.Name; Flush(); Check("tenth account is found by exact name with paging reset", model.ItemCount == 1 && model.Selected == tenth && model.IdentityPageItems.Single() == tenth && !model.PreviousIdentityPageCommand.CanExecute(null) && !model.NextIdentityPageCommand.CanExecute(null));
        Capture("accounts-ten-search", 1040, 670); model.Search = "";
        Capture("accounts-ten-first-minimum", 1040, 670);
        var viewport = FindVisual<ScrollContentPresenter>(accountList)!;
        var firstPageCount = model.IdentityPageItems.Count;
        Check("minimum first page uses actual row capacity without truncation or scrolling", model.ItemCount == 10 && firstPageCount > 0 && firstPageCount < 10
            && firstPageCount == model.IdentityPageSize && firstPageCount == Math.Min(10, (int)Math.Floor((viewport.ActualHeight + 0.1) / 44d))
            && !model.PreviousIdentityPageCommand.CanExecute(null) && model.NextIdentityPageCommand.CanExecute(null) && PageRowActionsVisible() && HomeActionsVisible());
        var operationBeforePage = UiVerification.Bounds(operationBar, layout);
        for (var attempt = 0; attempt < 10 && !model.IdentityPageItems.Contains(tenth) && nextPage.Command.CanExecute(null); attempt++) { nextPage.Command.Execute(null); Flush(); }
        Capture("accounts-ten-next-page-minimum", 1040, 670);
        Check("minimum paging reaches tenth account with row buttons visible and fixed controls unmoved", model.IdentityPageItems.Contains(tenth)
            && PageRowActionsVisible() && HomeActionsVisible() && listScroll.VerticalOffset == 0
            && UiVerification.Bounds(operationBar, layout) == operationBeforePage);
        model.Selected = tenth;
        Capture("accounts-ten-resized-default");
        Check("returning to default window restores ten complete rows and selection without account requests", model.IdentityPageItems.Count == 10
            && model.Selected == tenth && PageRowActionsVisible() && HomeActionsVisible());
        var proxy = model.ProxyPanel;
        model.Navigate("Proxy");
        await proxy.CurrentOperation;
        var flFixture = workspace.ProxyServices.FixtureFlClashRunner!;
        Check("entering proxy page reads local group and application automatically without apply", proxy.ProxyGroup == "代理选择" && proxy.ApplicationPath.EndsWith("FlClash.exe", StringComparison.Ordinal) && proxy.ReadStatus.Contains("已读取") && !proxy.PreviewReady && flFixture.Invocations.All(invocation => invocation.Action == FlClashDnsAction.Preview));
        model.DarkTheme = false; Capture("proxy-auto-loaded-light"); CaptureGroupDropdown("proxy-group-dropdown-light");
        model.DarkTheme = true; Capture("proxy-auto-loaded-dark"); CaptureGroupDropdown("proxy-group-dropdown-dark"); model.DarkTheme = false;
        Capture("proxy-primary-controls-minimum", 1040, 670);
        var proxyAdvanced = (CheckBox)window.FindName("ProxySimpleAdvancedToggle");
        Check("network defaults expose global DNS and screening while group selection stays advanced", proxyAdvanced.IsChecked == false
            && Displayed((Button)window.FindName("NetworkIpButton")) && Displayed((Button)window.FindName("SimpleProxySetupButton"))
            && !Displayed((ComboBox)window.FindName("ProxyGroupBox")) && window.FindName("ProtectionEnableButton") is null
            && ((Button)window.FindName("SimpleProxySetupButton")).Command == proxy.ConfigureNetworkCommand && Displayed(operationBar));
        proxyAdvanced.IsChecked = true; Capture("proxy-advanced-controls-minimum", 1040, 670);
        Check("explicit advanced proxy mode retains shared IP entry without retired protection action", Displayed((ComboBox)window.FindName("ProxyGroupBox"))
            && Displayed((Button)window.FindName("NetworkIpButton")) && window.FindName("ProtectionEnableButton") is null);
        Check("network and background progress share one IP task and one configuration refresh", window.FindName("NodeTestingOpenButton") is null
            && Descendants(layout).OfType<Button>().Count(button => button.Command == proxy.NodeTesting.OpenCommand) == 2
            && Descendants(layout).OfType<Button>().Count(button => button.Command == proxy.InspectCommand) == 1);
        var advancedPaths = (Expander)window.FindName("ProxyAdvancedToggle");
        var advancedHeader = Descendants(advancedPaths).OfType<System.Windows.Controls.Primitives.ToggleButton>().Single();
        var advancedFixedActions = Descendants((FrameworkElement)window.FindName("AdvancedProxyPanel")).OfType<Button>()
            .Where(button => button.Command == proxy.InspectCommand || button.Command == proxy.PreviewCommand || button.Command == proxy.DownloadFlClashCommand || button.Command == proxy.NodeTesting.OpenCommand).ToArray();
        Check("minimum advanced directory entry is fully visible and not covered by fixed actions", Displayed(advancedHeader)
            && advancedHeader.IsEnabled && advancedHeader.IsHitTestVisible && UiVerification.Within(advancedHeader, (FrameworkElement)window.FindName("AdvancedProxyPanel"))
            && advancedFixedActions.Length == 2 && advancedFixedActions.All(button => UiVerification.DoNotOverlap(advancedHeader, button, layout)));
        var requestsBeforeAdvanced = workspace.Accounts!.OfflineTransport!.RequestCount; var invocationsBeforeAdvanced = flFixture.Invocations.Count;
        advancedPaths.IsExpanded = true; Capture("proxy-advanced-paths-minimum", 1040, 670);
        var advancedPanel = (FrameworkElement)window.FindName("ProxyAdvancedPathsPanel");
        var dataPath = Descendants(advancedPanel).OfType<TextBox>().Single(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == "DataRoot");
        var applicationPath = (ComboBox)window.FindName("ProxyApplicationBox");
        var browsePaths = Descendants(advancedPanel).OfType<Button>().Where(button => button.Content?.ToString() == "浏览…").ToArray();
        var fixedProxyCommands = new[] { proxy.PreviewCommand, proxy.DownloadFlClashCommand };
        var advancedProxyPanel = (FrameworkElement)window.FindName("AdvancedProxyPanel");
        var fixedProxyButtons = fixedProxyCommands.Select(command => Descendants(advancedProxyPanel).OfType<Button>().Single(button => button.Command == command))
            .Concat(new[] { (Button)window.FindName("NetworkIpButton"), (Button)window.FindName("RefreshProxyConfigButton") }).ToArray();
        var pathInputs = new FrameworkElement[] { dataPath, applicationPath }.Concat(browsePaths).ToArray();
        File.WriteAllText(Path.Combine(directory, "proxy-advanced-layout.json"), JsonSerializer.Serialize(new { expanded = advancedPaths.IsExpanded, expander = LayoutFact(advancedPaths), advancedPanel = LayoutFact(advancedPanel), inputs = pathInputs.Select(LayoutFact), fixedActions = fixedProxyButtons.Select(LayoutFact),
            requestDelta = workspace.Accounts.OfflineTransport.RequestCount - requestsBeforeAdvanced, invocationDelta = flFixture.Invocations.Count - invocationsBeforeAdvanced }, new JsonSerializerOptions { WriteIndented = true }));
        Check("expanded proxy paths and fixed menu actions fit minimum window without overlap or requests", Displayed(advancedPanel) && browsePaths.Length == 2 && pathInputs.All(Displayed) && fixedProxyButtons.All(Displayed)
            && pathInputs.All(input => fixedProxyButtons.All(button => UiVerification.DoNotOverlap(input, button, layout)))
            && fixedProxyButtons.SelectMany((button, index) => fixedProxyButtons.Skip(index + 1).Select(other => UiVerification.DoNotOverlap(button, other, layout))).All(value => value)
            && workspace.Accounts.OfflineTransport.RequestCount == requestsBeforeAdvanced && flFixture.Invocations.Count == invocationsBeforeAdvanced);
        advancedPaths.IsExpanded = false; Flush();
        var originalGroups = flFixture.Groups; var originalGroup = flFixture.SelectedGroup;
        flFixture.Groups = ["专属住宅节点", "备用节点"]; flFixture.NodeTargets = flFixture.Groups;
        flFixture.SelectedGroup = null; flFixture.SelectionNote = "当前分组含直连候选；请选择当前订阅中的具体节点。原分组保留。";
        flFixture.PreviewStatusOverride = FlClashDnsStatus.Failed; proxy.ProxyGroup = "";
        proxyAdvanced.IsChecked = false;
        proxy.InspectCommand.Execute(null); await proxy.CurrentOperation; Capture("proxy-concrete-node-fallback-minimum", 1040, 670);
        Check("global network page remains usable without choosing a specific IP", proxy.Groups.Count == 2
            && proxy.Groups.All(item => item.Type == "固定节点") && proxy.GroupHint.Contains("直连")
            && window.FindName("SimpleProxyGroupBox") is null && Displayed((Button)window.FindName("NetworkIpButton")));
        CaptureGroupDropdown("proxy-concrete-node-choices");
        flFixture.Groups = originalGroups; flFixture.SelectedGroup = originalGroup; flFixture.NodeTargets = []; flFixture.SelectionNote = ""; flFixture.PreviewStatusOverride = null;
        proxyAdvanced.IsChecked = true;
        flFixture.Groups = ["代理选择", "备用代理"]; flFixture.SelectedGroup = null; proxy.ProxyGroup = "";
        proxy.InspectCommand.Execute(null); await proxy.CurrentOperation;
        Check("ambiguous automatic group discovery keeps candidates and explains selection", proxy.Groups.Count == 2 && proxy.ProxyGroup == "" && proxy.GroupHint.Contains("从列表选择") && proxy.PreviewCommand.CanExecute(null));
        Capture("proxy-auto-group-selection-required");
        ((ComboBox)window.FindName("ProxyGroupBox")).SelectedValue = "备用代理"; Flush(); proxy.InspectCommand.Execute(null); await proxy.CurrentOperation;
        Check("explicit group survives a reread of the same configuration", proxy.ProxyGroup == "备用代理");
        flFixture.Groups = []; flFixture.SelectedGroup = null; proxy.InspectCommand.Execute(null); await proxy.CurrentOperation;
        Check("empty discovered group list displays failure and guidance", proxy.Groups.Count == 0 && proxy.GroupHint.Contains("没有可选分组") && proxy.ReadStatus.Contains("未找到") && proxy.PreviewCommand.CanExecute(null));
        Capture("proxy-empty-groups");
        flFixture.Groups = ["代理选择"]; flFixture.SelectedGroup = "代理选择";
        flFixture.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.InspectCommand.Execute(null); var beforeLoadRoot = proxy.DataRoot;
        proxy.Client = ProxyClientKind.ClashVerge; proxy.DataRoot = Path.Combine(fixtureContainer, "拒绝忙碌时的路径变更");
        Check("loading exposes read status and protects client and data source", proxy.IsBusy && !proxy.CanEdit && proxy.Client == ProxyClientKind.FlClash && proxy.DataRoot == beforeLoadRoot && proxy.ReadStatus.Contains("正在读取"));
        flFixture.Hold.SetResult(); await proxy.CurrentOperation; flFixture.Hold = null;
        proxy.DownloadFlClashCommand.Execute(null); proxy.DownloadClashVergeCommand.Execute(null);
        var urlOpener = (OfflineUrlOpener)workspace.ProxyServices.Urls;
        Check("download buttons record only two official release destinations", urlOpener.Opened.Select(uri => uri.AbsoluteUri).SequenceEqual(new[] { ProxyPanelViewModel.FlClashReleases, ProxyPanelViewModel.ClashVergeReleases }));
        proxy.PreviewCommand.Execute(null); await proxy.CurrentOperation;
        Check("FlClash UI uses offline runner and exposes safe HTTP candidate", proxy.PreviewReady && proxy.Ports.Count == 1 && !proxy.Ports[0].RequiresAuthentication);
        Check("configuration previews do not prepare or copy the authorization browser", !Directory.Exists(Path.Combine(workspace.Root, "browser-program")) && !Directory.Exists(Path.Combine(workspace.Root, "browser-source-fixture")));
        var beforeStatusRead = flFixture.Invocations.Count;
        var beforeStatusProtection = workspace.ProxyServices.FixtureProtectionRunner!.Actions.Count;
        var beforeStatusNetwork = workspace.Accounts!.OfflineTransport!.RequestCount;
        flFixture.Scope = "all-subscriptions"; flFixture.SubscriptionCount = 13;
        var statusChanges = flFixture.ChangedFields; flFixture.ChangedFields = [];
        var reopenedProxy = new ProxyPanelViewModel(workspace.ProxyServices);
        var backgroundRead = reopenedProxy.RefreshDnsStatusAsync();
        Check("home DNS status refresh never blocks account controls", !reopenedProxy.IsBusy && reopenedProxy.CanEdit && !model.IsBusy);
        await backgroundRead;
        Check("fresh home recognizes saved all-subscription settings without visiting network page applying or probing", reopenedProxy.DnsSummary == "已配置"
            && reopenedProxy.DnsStage.Contains("13 个订阅") && reopenedProxy.DnsStage.Contains("运行效果尚未验证")
            && flFixture.Invocations.Skip(beforeStatusRead).Count() == 1 && flFixture.Invocations.Last().Action == FlClashDnsAction.Preview
            && workspace.ProxyServices.FixtureProtectionRunner.Actions.Count == beforeStatusProtection
            && workspace.Accounts.OfflineTransport.RequestCount == beforeStatusNetwork);
        flFixture.ChangedFields = ["profiles/14/dns-script"]; flFixture.SubscriptionCount = 14;
        proxy.InspectCommand.Execute(null); await proxy.CurrentOperation;
        Check("new subscription or changed settings remove configured summary on reread", model.SelectedDnsProtection == "待补齐"
            && proxy.DnsStage.Contains("14 个订阅"));
        flFixture.PreviewStatusOverride = FlClashDnsStatus.Failed;
        reopenedProxy.InspectCommand.Execute(null); await reopenedProxy.CurrentOperation;
        Check("failed reread never keeps the prior configured DNS summary", reopenedProxy.DnsSummary == "未确认");
        await reopenedProxy.RefreshDnsStatusAsync();
        Check("failed background DNS read remains unconfirmed and never blocks controls", reopenedProxy.DnsSummary == "未确认" && reopenedProxy.CanEdit);
        flFixture.PreviewStatusOverride = null; flFixture.ChangedFields = [];
        flFixture.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingStatusPanel = new ProxyPanelViewModel(workspace.ProxyServices);
        var pendingReadCount = flFixture.Invocations.Count;
        var pendingStatus = pendingStatusPanel.RefreshDnsStatusAsync();
        await pendingStatusPanel.RefreshDnsStatusAsync();
        Check("concurrent home status reads share the active read without locking account actions", flFixture.Invocations.Count == pendingReadCount + 1 && pendingStatusPanel.CanEdit);
        pendingStatusPanel.DataRoot = Path.Combine(fixtureContainer, "changed-during-status-read");
        flFixture.Hold.SetResult(); await pendingStatus; flFixture.Hold = null;
        Check("late DNS status cannot overwrite the newly selected data source", pendingStatusPanel.DnsSummary == "未确认" && pendingStatusPanel.DnsStage.Contains("来源已更改"));
        reopenedProxy.InspectCommand.Execute(null); await reopenedProxy.CurrentOperation;
        reopenedProxy.DataRoot = Path.Combine(fixtureContainer, "another-unread-proxy-root");
        Check("changing data source clears DNS observations and retains optional system protection", reopenedProxy.DnsSummary == "未确认" && reopenedProxy.ProtectionSummary == "未检查");
        flFixture.Scope = null; flFixture.SubscriptionCount = null; flFixture.ChangedFields = statusChanges;
        var networkStart = flFixture.Invocations.Count;
        var protectionBeforeNetwork = workspace.ProxyServices.FixtureProtectionRunner!.Actions.Count;
        proxy.ConfigureNetworkCommand.Execute(null); await proxy.CurrentOperation;
        Check("global DNS action directly applies every subscription without preview group or firewall operations",
            flFixture.Invocations.Skip(networkStart).Select(i => i.Action).SequenceEqual(new[] { FlClashDnsAction.Apply })
            && flFixture.Invocations.Skip(networkStart).All(i => i.Request.AllSubscriptions && i.Request.ProxyGroup is null)
            && workspace.ProxyServices.FixtureProtectionRunner.Actions.Count == protectionBeforeNetwork
            && proxy.DnsStage.Contains("全部订阅"));
        var protectionFixture = workspace.ProxyServices.FixtureProtectionRunner!;
        var browserUdpFixture = workspace.ProxyServices.FixtureBrowserUdp!;
        var untouchedDns = flFixture.Invocations.Count; var untouchedBrowser = browserUdpFixture.Calls;
        await proxy.RefreshProtectionAsync();
        Check("system protection reads actual disabled state independently of DNS", proxy.ProtectionSummary == "已关闭"
            && model.SelectedNetworkProtection == "已关闭" && flFixture.Invocations.Count == untouchedDns);
        Check("system protection card and homepage row are removed", window.FindName("SystemProtectionCard") is null && window.FindName("SelectedNetworkProtectionText") is null);
        var actionsBeforeRemovedEnable = protectionFixture.Actions.Count;
        proxy.EnableProtectionCommand.Execute(null); await proxy.CurrentOperation;
        Check("retired enable entry never invokes firewall or proxy discovery", proxy.IsError && proxy.Status.Contains("已移除") && protectionFixture.Actions.Count == actionsBeforeRemovedEnable);
        proxy.RestoreProtectionCommand.Execute(null); await proxy.CurrentOperation;
        Check("standalone disable changes only owned firewall rules", !proxy.IsError && proxy.ProtectionSummary == "已关闭"
            && protectionFixture.Actions.Last() == DirectConnectionProtectionAction.Restore && flFixture.Invocations.Count == untouchedDns && browserUdpFixture.Calls == untouchedBrowser);
        var beforeDnsOnly = protectionFixture.Actions.Count;
        flFixture.PreviewStatusOverride = FlClashDnsStatus.Failed;
        proxy.ConfigureNetworkCommand.Execute(null); await proxy.CurrentOperation;
        Check("DNS preview failure does not touch system protection", proxy.IsError && protectionFixture.Actions.Count == beforeDnsOnly);
        flFixture.PreviewStatusOverride = null; flFixture.ApplyStatus = FlClashDnsStatus.Failed;
        proxy.ConfigureNetworkCommand.Execute(null); await proxy.CurrentOperation;
        Check("DNS apply failure preserves independent protection state", proxy.IsError && proxy.DnsStage.Contains("未完成")
            && proxy.ProtectionSummary == "已关闭" && protectionFixture.Actions.Count == beforeDnsOnly && browserUdpFixture.Calls == untouchedBrowser);
        flFixture.ApplyStatus = FlClashDnsStatus.ReloadUnconfirmed;
        workspace.ProxyServices.FixtureFlClashRunner!.ProxyPorts = [new("mixed", 7890, "::1", false), new("http", 7891, "::", false), new("http", 7892, "127.0.0.2", false), new("http", 7893, null, null)];
        proxy.PreviewCommand.Execute(null); await proxy.CurrentOperation;
        Check("IPv6 listeners preserve IPv6 loopback and unknown binding is not usable", proxy.Ports[0].Address == "http://[::1]:7890" && proxy.Ports[1].Address == "http://[::1]:7891" && proxy.Ports[2].Address == "http://127.0.0.2:7892" && proxy.Ports[3].RequiresAuthentication);
        workspace.ProxyServices.FixtureFlClashRunner.ProxyPorts = [new("mixed", 7890, "*", false), new("http", 7891, "*", null), new("http", 7892, "192.0.2.1", false)];
        proxy.PreviewCommand.Execute(null); await proxy.CurrentOperation;
        Check("wildcard listener connects locally but unknown authentication and nonlocal binding stay blocked", proxy.Ports[0].Address == "http://127.0.0.1:7890"
            && !proxy.Ports[0].RequiresAuthentication && proxy.Ports[1].RequiresAuthentication && proxy.Ports[2].RequiresAuthentication);
        workspace.ProxyServices.FixtureFlClashRunner.ProxyPorts = [new("mixed", 7890, "127.0.0.1", false)];
        proxy.PreviewCommand.Execute(null); await proxy.CurrentOperation;
        model.Navigate("Accounts"); Select(tenth.Name); model.OpenEditor(ProfileEditMode.Edit);
        model.Editor!.Proxy = "http://127.0.0.1:7899";
        var oldProxy = model.Editor.Proxy; Check("candidate reading does not overwrite profile proxy", model.Editor.Proxy == oldProxy);
        model.UseProxyCandidateCommand.Execute(proxy.Ports[0]); Check("proxy candidate requires explicit selection", model.Editor.Proxy == "http://127.0.0.1:7890"); model.CloseEditor();
        model.Navigate("Proxy"); await proxy.CurrentOperation; proxy.PreviewCommand.Execute(null); await proxy.CurrentOperation; proxy.ApplyCommand.Execute(null); await proxy.CurrentOperation;
        Check("FlClash save does not imply reload or network confirmation", proxy.Status.Contains("重载尚未确认") && proxy.Status.Contains("离线协议替身"));
        Capture("proxy-flclash-result");
        var nodePanel = proxy.NodeTesting; var nodesNetwork = workspace.Accounts!.OfflineTransport!;
        var nodeRuntime = workspace.ProxyServices.NodeTesting!.OfflineRuntime!; var beforeNodeRequests = nodesNetwork.RequestCount;
        nodePanel.OpenCommand.Execute(null); await nodePanel.CurrentOperation; Capture("nodes-open-no-automatic-requests");
        Check("opening only loads local subscriptions", nodePanel.IsOpen && nodesNetwork.RequestCount == beforeNodeRequests && nodeRuntime.StartedIds.Count == 0);
        Directory.CreateDirectory(workspace.ProxyServices.FlClashRoot);
        var nodeSource = Path.Combine(workspace.ProxyServices.FlClashRoot, "config.yaml");
        var nodeSourceText = "proxies:\n  - name: 离线节点甲\n    type: socks5\n    server: node-a.fixture.test\n    port: 443\n    password: fixture-node-private-secret\n  - name: 家宽节点乙\n    type: http\n    server: node-b.fixture.test\n    port: 443\n    username: fixture-user-secret\n  - name: 不兼容直连条目\n    type: direct\n";
        File.WriteAllText(nodeSource, nodeSourceText);
        nodePanel.LoadCommand.Execute(null); await nodePanel.CurrentOperation; Capture("screen-ready-minimum", 1040, 670);
        Check("one button ready without checkbox or manual network parameters", nodePanel.Items.Count == 3 && nodePanel.Results.Count == 0 && nodePanel.ScreenCommand.CanExecute(null) && nodesNetwork.RequestCount == beforeNodeRequests);
        nodePanel.ScreenCommand.Execute(null); await nodePanel.CurrentOperation; Capture("screen-results-deduplicated", 1040, 670);
        Check("screen prioritizes residential names and skips unsupported nodes", nodeRuntime.StartedIds.SequenceEqual(new[] { nodePanel.Items[1].Node.Id, nodePanel.Items[0].Node.Id }) && nodeRuntime.StopCalls == 2);
        Check("same-IP results merge automatically with score and details", nodePanel.Results.Count == 1 && nodePanel.Items.Count(item => item.Eligible) == 2
            && nodePanel.Results[0].QualityScore == 95 && nodePanel.Results[0].NetworkGroup == "家庭宽带组" && nodePanel.ResultSummary.Contains("已合并 1")
            && ((TextBox)window.FindName("NodeResultDetails")).Text.Contains("192.0.2.10"));
        Check("screening leaves subscription config intact and uses explicit isolated proxy", File.ReadAllText(nodeSource) == nodeSourceText
            && nodesNetwork.Requests.Skip(beforeNodeRequests).All(request => request.Proxy is "http://127.0.0.1:40001" or "http://127.0.0.1:40002" && request.RequireProxy));
        Check("node UI hides server credentials", !TextValues(layout).Any(text => text.Contains("node-a.fixture.test") || text.Contains("fixture-node-private-secret") || text.Contains("fixture-user-secret")));
        workspace.ProxyServices.NodeTesting.ResetFixtureProbeBatch();
        nodeRuntime.HoldMeasureDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeCancelStarts = nodeRuntime.StartedIds.Count; var beforeCancelStops = nodeRuntime.StopCalls; var beforeCancelDelays = nodeRuntime.MeasureDelayCalls;
        nodePanel.ScreenCommand.Execute(null); var cancelledNodes = nodePanel.CurrentOperation;
        await BackgroundAccountTestsVerification.Until(() => nodeRuntime.MeasureDelayCalls == beforeCancelDelays + 1);
        var runningNodeList = (ListBox)window.FindName("NodeTestingList");
        foreach (var dark in new[] { true, false })
        {
            model.DarkTheme = dark; Capture("screen-running-" + (dark ? "dark" : "light"), 1040, 670);
            CheckNodeSurface("running " + dark);
            Check("busy screen remains readable without blocking login " + dark, runningNodeList.IsEnabled && nodePanel.IsBusy && !model.IsBusy && model.NewAccountLoginCommand.CanExecute(null) && !nodePanel.ScreenCommand.CanExecute(null) && nodePanel.CancelCommand.CanExecute(null));
            runningNodeList.IsEnabled = false; Capture("screen-disabled-" + (dark ? "dark" : "light"), 1040, 670); CheckNodeSurface("disabled " + dark); runningNodeList.ClearValue(UIElement.IsEnabledProperty);
        }
        runningNodeList.SelectedItem = nodePanel.Results[0]; Flush();
        Check("completed result details remain accessible during screening", ((TextBox)window.FindName("NodeResultDetails")).Text.Contains("192.0.2.10"));
        Check("IP overlay leaves minimize and background controls reachable", UiVerification.ReceivesInput((Button)window.FindName("MinimizeButton"), layout)
            && Displayed((Button)window.FindName("NodeTestingBackgroundButton")));
        nodePanel.CloseCommand.Execute(null); model.NavigateCommand.Execute("Accounts"); Flush();
        var hiddenNodeRequests = nodesNetwork.RequestCount;
        Capture("background-ip-light-minimum", 1040, 670);
        Check("IP screening survives hiding with navigation and visible progress", !nodePanel.IsOpen && nodePanel.IsBusy && !cancelledNodes.IsCompleted
            && model.IsAccountsPage && Displayed((Button)window.FindName("BackgroundIpButton")));
        nodePanel.OpenCommand.Execute(null); Flush();
        Check("reopening IP task does not restart requests and still allows cancellation", nodePanel.IsOpen && nodePanel.CancelCommand.CanExecute(null) && nodesNetwork.RequestCount == hiddenNodeRequests);
        nodePanel.CancelCommand.Execute(null); await cancelledNodes; nodeRuntime.HoldMeasureDelay = null;
        var cancelledBatchStarts = nodeRuntime.StartedIds.Count; var cancelledBatchStops = nodeRuntime.StopCalls;
        var startedInCancelledBatch = cancelledBatchStarts - beforeCancelStarts;
        Check("cancel drains the held fixture core before dispatching another", startedInCancelledBatch == 1 && cancelledBatchStops - beforeCancelStops == 1);
        await proxy.CurrentOperation; Flush(); await Task.Yield();
        Check("cancelled batch starts no new cores after completion", nodeRuntime.StartedIds.Count == cancelledBatchStarts && nodeRuntime.StopCalls == cancelledBatchStops);
        Check("cancel releases node busy state", !nodePanel.IsBusy);
        Check("cancel leaves the selected account available after local proxy reads", !model.IsBusy);
        Check("cancel restores the test button and reports cancellation", nodePanel.Status.Contains("取消") && nodePanel.ScreenCommand.CanExecute(null));
        nodeRuntime.FailStop = true;
        nodePanel.ScreenCommand.Execute(null); await nodePanel.CurrentOperation;
        Check("unconfirmed cleanup is an interrupted batch rather than successful completion", nodePanel.Status.StartsWith("检测中断") && !nodePanel.Status.Contains("筛选完成"));
        nodeRuntime.FailStop = false;
        var savedProfiles = Path.Combine(workspace.ProxyServices.FlClashRoot, "profiles"); Directory.CreateDirectory(savedProfiles);
        var firstSubscription = Path.Combine(savedProfiles, "10001.yaml"); var secondSubscription = Path.Combine(savedProfiles, "10002.yaml");
        File.WriteAllText(firstSubscription, nodeSourceText); File.WriteAllText(secondSubscription, nodeSourceText.Replace("节点", "线路"));
        var beforeCatalog = nodesNetwork.RequestCount;
        nodePanel.LoadCommand.Execute(null); await nodePanel.CurrentOperation;
        Check("refresh includes inactive subscriptions and sends no probe", nodePanel.Items.Count == 6 && nodePanel.SubscriptionCount == 2 && nodesNetwork.RequestCount == beforeCatalog);
        var allIds = nodePanel.Items.Where(item => item.Supported).Select(item => item.Node.Id).ToHashSet();
        var beforeScope = nodeRuntime.StartedIds.Count; nodePanel.ScreenCommand.Execute(null); await nodePanel.CurrentOperation;
        Check("single button tests all supported nodes across every subscription exactly once", allIds.Count == 4 && nodeRuntime.StartedIds.Skip(beforeScope).Count() == allIds.Count
            && nodeRuntime.StartedIds.Skip(beforeScope).ToHashSet().SetEquals(allIds) && nodePanel.Results.Count == 1 && nodePanel.ResultSummary.Contains("已合并 3"));
        Check("all-node scope needs no subscription selection", window.FindName("SubscriptionPicker") is null && nodePanel.CountLabel.Contains("2 个订阅 · 全部 6 个节点")
            && ((Button)window.FindName("NodeScreenButton")).Content is "重新检测全部");
        var beforeSupplement = nodeRuntime.StartedIds.Count;
        nodePanel.SupplementCommand.Execute(null); await nodePanel.CurrentOperation;
        Check("supplement skips completed routes and keeps successful results", nodeRuntime.StartedIds.Count == beforeSupplement && nodePanel.Results.Count == 1 && nodePanel.Status.Contains("无需补测"));
        var failedNode = nodePanel.Items.First(item => item.Supported);
        workspace.ProxyServices.NodeTesting.History.Put(new(failedNode.Node.Id, failedNode.Name, failedNode.Type, false, null, "fixture timeout", new(ProxyLatencyState.Timeout, null, "timeout")));
        var addedSubscription = Path.Combine(savedProfiles, "10003.yaml");
        File.WriteAllText(addedSubscription, "proxies:\n - {name: 新增家宽, type: socks5, server: new.fixture.invalid, port: 443}\n");
        nodePanel.SupplementCommand.Execute(null); await nodePanel.CurrentOperation; Capture("screen-supplement-complete", 1040, 670);
        Check("supplement refreshes subscriptions and retries only new and failed nodes", nodeRuntime.StartedIds.Count == beforeSupplement + 2 && nodeRuntime.StartedIds.Skip(beforeSupplement).Contains(failedNode.Node.Id) && nodePanel.SubscriptionCount == 3 && nodePanel.Status.Contains("跳过 3"));
        var reloadedNodePanel = new NodeTestingViewModel(workspace.ProxyServices.NodeTesting, () => (ProxyClientKind.FlClash, workspace.ProxyServices.FlClashRoot));
        reloadedNodePanel.SupplementCommand.Execute(null); await reloadedNodePanel.CurrentOperation;
        Check("new panel restores history and sends no repeat probes", nodeRuntime.StartedIds.Count == beforeSupplement + 2 && reloadedNodePanel.Results.Count == 1 && reloadedNodePanel.Status.Contains("无需补测"));
        File.Delete(addedSubscription);
        nodePanel.LoadCommand.Execute(null); await nodePanel.CurrentOperation;
        var normalScore = nodesNetwork.ScorePayload; var scoreClock = workspace.ProxyServices.NodeTesting.OfflineScoreClock!;
        workspace.ProxyServices.NodeTesting!.ResetFixtureScore(); nodesNetwork.ScorePayload = "{\"ip\":\"192.0.2.10\",\"fraudScore\":16,\"isResidential\":true}";
        nodePanel.ScreenCommand.Execute(null); await nodePanel.CurrentOperation; Capture("screen-reject84", 1040, 670);
        Check("84 excluded even with residential name and data", nodePanel.Results.Count == 0 && nodePanel.Items.Where(item => allIds.Contains(item.Node.Id)).All(item => item.QualityScore == 84 && !item.Eligible) && nodePanel.EmptyMessage.Contains("重新检测"));
        var beforeLowSupplement = nodeRuntime.StartedIds.Count; var beforeLowRequests = nodesNetwork.RequestCount;
        nodePanel.SupplementCommand.Execute(null); await nodePanel.CurrentOperation;
        Check("known low scores are complete and excluded from retry without any network requests", nodeRuntime.StartedIds.Count == beforeLowSupplement && nodesNetwork.RequestCount == beforeLowRequests && nodePanel.Results.Count == 0);
        workspace.ProxyServices.NodeTesting!.ResetFixtureScore(); nodesNetwork.ScorePayload = "{\"ip\":\"192.0.2.10\",\"fraudScore\":15,\"isResidential\":false}";
        nodePanel.ScreenCommand.Execute(null); await nodePanel.CurrentOperation;
        Check("85 accepted and actual attributes override residential node names", nodePanel.Results.Count == 1 && nodePanel.Results[0].QualityScore == 85 && nodePanel.Results[0].NetworkGroup == "机房组");
        workspace.ProxyServices.NodeTesting!.ResetFixtureScore(); nodesNetwork.ScorePayload = "{\"ip\":\"192.0.2.10\"}";
        nodePanel.ScreenCommand.Execute(null); await nodePanel.CurrentOperation;
        Check("unknown replaces prior success and stays excluded", nodePanel.Results.Count == 0 && nodePanel.Items.Where(item => allIds.Contains(item.Node.Id)).All(item => item.QualityScore is null && !item.Eligible));
        nodesNetwork.ScorePayload = normalScore;
        File.WriteAllText(firstSubscription, nodeSourceText.Replace("port: 443", "port: 8443"));
        File.WriteAllText(secondSubscription, nodeSourceText.Replace("port: 443", "port: 8443").Replace("节点", "线路"));
        nodePanel.LoadCommand.Execute(null); await nodePanel.CurrentOperation; nodesNetwork.DelayTimeout = true;
        var beforeTimeout = nodesNetwork.RequestCount;
        nodePanel.ScreenCommand.Execute(null); await nodePanel.CurrentOperation; nodesNetwork.DelayTimeout = false;
        Check("unreachable nodes filtered before scoring", nodePanel.Results.Count == 0 && nodesNetwork.Requests.Skip(beforeTimeout).All(request => !NetCoffeeService.IsLookup(request.Url)));
        File.Delete(firstSubscription); File.Delete(secondSubscription);
        File.WriteAllText(nodeSource, "proxies:\n" + string.Join("", Enumerable.Range(1, 30).Select(number => $"  - name: 滚动节点{number:D2}\n    type: socks5\n    server: node-{number}.fixture.test\n    port: 443\n")));
        nodePanel.LoadCommand.Execute(null); await nodePanel.CurrentOperation;
        var beforeScrollRequests = nodesNetwork.RequestCount;
        for (var i = 0; i < nodePanel.Items.Count; i++)
        {
            var item = nodePanel.Items[i];
            var quality = new IpQualityReport(true, $"192.0.2.{i + 20}", "United States", "Fixture city", "America/Chicago", "Fixture ISP", "AS64500", null, null, null, null, null, DateTimeOffset.UtcNow, "Offline layout fixture", "fixture", new(i % 5, "fixture", i % 3 == 2 ? null : i % 2 == 0)) { CountryCode = "US" };
            item.Apply(new(item.Node.Id, item.Name, item.Type, true, quality, "fixture", new(ProxyLatencyState.Success, 50 + i, "fixture")));
        }
        Check("one list sorts score then latency across network types", nodePanel.Results.Select(item => item.Node.Id).SequenceEqual(nodePanel.Items.OrderByDescending(item => item.QualityScore).ThenBy(item => item.DelayMs).Select(item => item.Node.Id))
            && nodePanel.Results.Select(item => item.NetworkTag).Distinct().ToHashSet().SetEquals(new[] { "家宽", "机房", "待分类" }));
        foreach (var dark in new[] { true, false })
        {
            model.DarkTheme = dark; var scroller = Descendants(runningNodeList).OfType<ScrollViewer>().First(); scroller.ScrollToTop(); Flush(); Capture("screen-tags-scroll-top-" + dark, 1040, 670);
            var tags = Descendants(runningNodeList).OfType<TextBlock>().Where(text => text.Name == "NodeTypeTag" && UiVerification.FullyVisible(text, layout)).ToArray();
            Check("individual type tags remain visible without group headings " + dark, tags.Length > 0 && tags.All(tag => tag.DataContext is NodeSelectionItem item && tag.Text == item.NetworkTag)
                && !TextValues(runningNodeList).Any(text => text.Contains("家庭宽带组") || text.Contains("机房组")));
            Check("flat scroll and primary actions fit minimum size " + dark, nodePanel.Results.Count == 30
                && ScrollViewer.GetVerticalScrollBarVisibility(runningNodeList) == ScrollBarVisibility.Auto && Displayed((Button)window.FindName("NodeScreenButton")) && window.FindName("SubscriptionPicker") is null);
            var last = nodePanel.Results.Last(); runningNodeList.SelectedItem = last; runningNodeList.ScrollIntoView(last); scroller.ScrollToEnd(); Flush(); Capture("screen-tags-scroll-bottom-" + dark, 1040, 670);
            Check("last result accessible without pagination " + dark, runningNodeList.ItemContainerGenerator.ContainerFromItem(last) is ListBoxItem container && UiVerification.FullyVisible(container, layout)
                && nodePanel.SelectedNode == last && ((TextBox)window.FindName("NodeResultDetails")).Text.Contains(last.ExitIp));
        }
        Check("scrolling and selecting results never starts requests", nodesNetwork.RequestCount == beforeScrollRequests);
        model.DarkTheme = false; File.WriteAllText(nodeSource, nodeSourceText); nodePanel.LoadCommand.Execute(null); await nodePanel.CurrentOperation;
        Check("refresh restores matching history and drops removed routes", nodePanel.Items.Count == 3 && nodePanel.Results.Count == 1 && nodePanel.Results[0].QualityScore == 95 && nodePanel.Items.Any(item => item.State == "未检测"));
        nodesNetwork.HoldEgressResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var background = nodePanel.StartBackgroundAsync();
        for (var attempt = 0; attempt < 100 && !nodePanel.IsScreening; attempt++) await Task.Delay(20);
        var backgroundStarts = nodeRuntime.StartedIds.Count;
        nodePanel.CloseCommand.Execute(null); nodePanel.OpenCommand.Execute(null);
        Check("background scan can close reopen and keep login available without duplicate run", nodePanel.IsScreening && nodePanel.IsOpen && !model.IsBusy
            && nodePanel.CurrentOperation == background && nodePanel.StartBackgroundAsync() == background && model.NewAccountLoginCommand.CanExecute(null));
        model.OpenAccountOnboarding();
        Check("current network remains available during background scan", model.Onboarding!.UseCurrentNetwork && model.Onboarding.AuthorizeCommand.CanExecute(null));
        model.CloseAccountOnboarding();
        var priorCoreFailureReported = false;
        try { await nodePanel.StopAsync(); } catch (IOException error) { priorCoreFailureReported = error.Message.Contains("IP 筛选核心"); }
        nodesNetwork.HoldEgressResponse = null;
        Check("background stop drains current cores and preserves earlier cleanup failure", priorCoreFailureReported && !nodePanel.IsBusy && nodeRuntime.StartedIds.Count == nodeRuntime.StopCalls);
        nodePanel.CloseCommand.Execute(null);
        proxy.Client = ProxyClientKind.ClashVerge;
        await proxy.CurrentOperation;
        Check("client source change clears old candidates and plan", proxy.Ports.Count == 0 && proxy.Groups.Count == 0 && !proxy.PreviewReady && !proxy.ApplyCommand.CanExecute(null));
        Check("missing client configuration has actionable status", proxy.ReadStatus.Contains("未找到 Clash Verge 配置") && proxy.Status.Contains("高级设置"));
        var vergeRoot = workspace.ProxyServices.ClashVergeRoot; Directory.CreateDirectory(Path.Combine(vergeRoot, "profiles"));
        File.WriteAllText(Path.Combine(vergeRoot, "profiles.yaml"), "current: fixture\nitems:\n  - uid: fixture\n    type: local\n    file: fixture.yaml\n");
        File.WriteAllText(Path.Combine(vergeRoot, "profiles", "fixture.yaml"), "mixed-port: 7890\nbind-address: 127.0.0.1\nauthentication: []\nproxies:\n  - name: fixture-node\n    type: socks5\n    server: example.invalid\n    port: 443\n    password: fixture-node-secret\nproxy-groups:\n  - name: 代理选择\n    type: select\n    proxies: [fixture-node]\nrules: [\"MATCH,代理选择\"]\n");
        proxy.InspectCommand.Execute(null); await proxy.CurrentOperation;
        Check("corrected source loads groups before preview without modifying config", proxy.Groups.Count == 1 && proxy.ProxyGroup == "代理选择" && !proxy.PreviewReady && !File.Exists(Path.Combine(vergeRoot, "profiles", "Script.js")));
        proxy.PreviewCommand.Execute(null); await proxy.CurrentOperation;
        Check("Verge preview reads real fixture config without exposing credentials", proxy.PreviewReady && proxy.Ports.Count == 1 && !proxy.Status.Contains("fixture-node-secret") && proxy.Changes.Count >= 3);
        proxy.ExportPath = Path.Combine(fixtureContainer, "global-dns.js"); proxy.ExportReportCommand.Execute(null); await proxy.CurrentOperation;
        var exportedScript = File.ReadAllText(proxy.ExportPath);
        var parametersLine = exportedScript.Split('\n').Single(line => line.StartsWith("// parameters: ", StringComparison.Ordinal));
        var scriptParameters = JsonNode.Parse(parametersLine[15..]);
        Check("export contains shared rules but no subscription credentials", scriptParameters?["group"]?.GetValue<string>() == "代理选择" && !exportedScript.Contains("fixture-node-secret") && !exportedScript.Contains("example.invalid") && proxy.Status.Contains("尚未接入"));
        Capture("proxy-verge-preview");
        proxy.ApplyCommand.Execute(null); await proxy.CurrentOperation;
        Check("Verge apply writes persistent script and real backup only", File.Exists(Path.Combine(vergeRoot, "profiles", "Script.js")) && Directory.Exists(proxy.BackupPath) && proxy.Status.Contains("尚未验证"));
        proxy.RestoreCommand.Execute(null); await proxy.CurrentOperation;
        Check("Verge restore returns fixture files to unbound script state", !File.Exists(Path.Combine(vergeRoot, "profiles", "Script.js")) && !File.ReadAllText(Path.Combine(vergeRoot, "profiles.yaml")).Contains("Script"));
        proxy.PreviewCommand.Execute(null); await proxy.CurrentOperation; if (proxy.ApplyCommand.CanExecute(null)) { proxy.ApplyCommand.Execute(null); await proxy.CurrentOperation; }
        Check("legacy Verge preview and apply preserve fixture files", !proxy.IsError && File.Exists(Path.Combine(vergeRoot, "profiles", "Script.js")) && Directory.Exists(proxy.BackupPath) && proxy.Status.Contains("尚未验证"));
        proxy.RestoreCommand.Execute(null); await proxy.CurrentOperation;
        var customScript = Path.Combine(vergeRoot, "profiles", "Script.js"); File.WriteAllText(customScript, "function main(config) { return config; } // fixture custom");
        var backupsBeforeConflict = Directory.GetDirectories(workspace.ProxyServices.BackupRoot).Length;
        proxy.PreviewCommand.Execute(null); await proxy.CurrentOperation; if (proxy.ApplyCommand.CanExecute(null)) { proxy.ApplyCommand.Execute(null); await proxy.CurrentOperation; }
        Check("legacy Verge preview refuses a conflicting global script without applying", proxy.IsError && proxy.ReadStatus.Contains("配置已读取；修复预览存在冲突") && proxy.Groups.Count == 1 && File.ReadAllText(customScript).Contains("fixture custom") && Directory.GetDirectories(workspace.ProxyServices.BackupRoot).Length == backupsBeforeConflict);
        Capture("proxy-verge-conflict"); File.Delete(customScript); proxy.InspectCommand.Execute(null); await proxy.CurrentOperation;
        Capture("proxy-verge-restored-minimum", 1040, 670);
        Check("rendered proxy summaries do not contain node credentials", !string.Join("\n", TextValues(layout)).Contains("fixture-node-secret"));
        var preferredModel = new ShellViewModel(workspace); preferredModel.Navigate("Proxy"); await preferredModel.ProxyPanel.CurrentOperation;
        Check("first entry prefers existing Verge configuration when FlClash is absent", preferredModel.ProxyPanel.Client == ProxyClientKind.ClashVerge && preferredModel.ProxyPanel.Groups.Count == 1 && preferredModel.ProxyPanel.DataRoot == vergeRoot);
        preferredModel.ProxyPanel.Client = ProxyClientKind.FlClash; await preferredModel.ProxyPanel.CurrentOperation;
        preferredModel.Navigate("Accounts"); preferredModel.Navigate("Proxy"); await preferredModel.ProxyPanel.CurrentOperation;
        Check("explicit client remains selected on later page entries", preferredModel.ProxyPanel.Client == ProxyClientKind.FlClash);
        flFixture.ApplicationCandidates = []; preferredModel.ProxyPanel.Client = ProxyClientKind.ClashVerge; await preferredModel.ProxyPanel.CurrentOperation;
        preferredModel.ProxyPanel.Client = ProxyClientKind.FlClash; await preferredModel.ProxyPanel.CurrentOperation;
        var missingAppStart = flFixture.Invocations.Count; preferredModel.ProxyPanel.PreviewCommand.Execute(null); await preferredModel.ProxyPanel.CurrentOperation;
        Check("missing application still imports groups and disables DNS apply", preferredModel.ProxyPanel.Groups.Count == 1 && preferredModel.ProxyPanel.ApplicationDiscoveryText.Contains("未识别") && flFixture.Invocations.Skip(missingAppStart).All(invocation => invocation.Action == FlClashDnsAction.Preview) && !preferredModel.ProxyPanel.ApplyCommand.CanExecute(null));
        preferredModel.ProxyPanel.ApplicationPath = "D:/fixture/manual-FlClash.exe";
        Check("hand selected application path is shown when discovery list is empty", preferredModel.ProxyPanel.ApplicationDiscoveryText.Contains("manual-FlClash.exe"));
        var applicationBox = (ComboBox)window.FindName("ProxyApplicationBox"); applicationBox.ApplyTemplate();
        var applicationInput = (TextBox)applicationBox.Template.FindName("PART_EditableTextBox", applicationBox);
        applicationBox.Text = "D:/fixture/edited-FlClash.exe"; applicationBox.GetBindingExpression(ComboBox.TextProperty)!.UpdateSource(); Flush();
        Check("editable application template preserves text binding", applicationInput.Text == applicationBox.Text && proxy.ApplicationPath == "D:/fixture/edited-FlClash.exe");
        model.Navigate("Settings"); Flush();
        var accountModelTestButton = (Button)window.FindName("AccountModelTestButton");
        model.OpenModelTestCommand.Execute(null);
        Check("model selector from settings context uses an account and sends no request", model.IsModelTestOpen && model.ModelTestProfiles.Contains(model.ModelTestProfile));
        model.CloseModelTestCommand.Execute(null);
        model.Navigate("Accounts"); model.Selected = model.FilteredItems.First(item => item.Name == "工作甲"); Flush();
        Check("account selection enables shared model test entry", accountModelTestButton.IsEnabled);
        var accountTransport = workspace.Accounts!.OfflineTransport!;
        var requestsBeforeFingerprint = accountTransport.RequestCount;
        model.OpenModelTestCommand.Execute(null);
        Capture("model-test-selector-minimum", 1040, 670);
        model.DarkTheme = true; Capture("model-test-selector-dark-minimum", 1040, 670); model.DarkTheme = false;
        var choiceButtons = new[] { "ChooseCialloButton", "ChooseAnimationButton", "ChooseVariationButton", "ChooseRevisionButton" }.Select(name => (Button)window.FindName(name)).ToArray();
        Check("four model test choices are visible without requests", choiceButtons.All(Displayed) && accountTransport.RequestCount == requestsBeforeFingerprint);
        model.CloseModelTestCommand.Execute(null);
        model.OpenModelTestCommand.Execute(null); model.ModelTestProfile = "工作甲"; model.OpenCialloCommand.Execute(null); var ciallo = model.Ciallo!;
        var cialloRequests = accountTransport.RequestCount;
        Capture("ciallo-ready-minimum", 1040, 670);
        Check("Ciallo opens from shared selector without requests or stacked test engines", !model.IsModelTestOpen && !model.IsVisualTestsOpen
            && ciallo.SelectedProfile == "工作甲" && accountTransport.RequestCount == cialloRequests && ciallo.StartCommand.CanExecute(null) && Displayed((Button)window.FindName("CialloStartButton")));
        ciallo.StartCommand.Execute(null); await ciallo.CurrentOperation; Flush();
        Capture("ciallo-results-minimum", 1040, 670);
        Check("Ciallo shows every original reply with Chinese high effort label", ciallo.Rows.Count == CialloTest.Models.Count && ciallo.Rows.All(row => row.State == "已完成" && row.Text.Contains("Ciallo") && row.Detail.Contains("思考：高")) && accountTransport.Requests.TakeLast(CialloTest.Models.Count).All(request => JsonNode.Parse(request.Body!)!["reasoning"]!["effort"]!.ToString() == "high") && accountTransport.RequestCount == cialloRequests + CialloTest.Models.Count);
        ciallo.CloseCommand.Execute(null); model.OpenModelTestCommand.Execute(null); model.ModelTestProfile = "工作甲"; model.OpenCialloCommand.Execute(null);
        Check("reopening Ciallo retains replies without another test", ReferenceEquals(model.Ciallo, ciallo) && ciallo.Rows.All(row => row.State == "已完成") && accountTransport.RequestCount == cialloRequests + CialloTest.Models.Count);
        accountTransport.HoldResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ciallo.StartCommand.Execute(null); await Task.Delay(30); Flush();
        Check("Ciallo prevents duplicate requests while allowing hide and cancel", !ciallo.StartCommand.CanExecute(null) && ciallo.CloseCommand.CanExecute(null) && ciallo.CancelCommand.CanExecute(null));
        Check("Ciallo overlay leaves native minimize reachable", UiVerification.ReceivesInput((Button)window.FindName("MinimizeButton"), layout));
        var hiddenCialloRequests = accountTransport.RequestCount;
        ciallo.CloseCommand.Execute(null); model.NavigateCommand.Execute("Proxy"); Flush();
        Check("Ciallo continues hidden and permits navigation", !model.IsCialloOpen && ciallo.IsBusy && !ciallo.CurrentOperation.IsCompleted && model.IsProxyPage);
        model.OpenVisualTestsCommand.Execute(null); Flush();
        Check("visual entry opens independently while hidden Ciallo keeps running", model.IsVisualTestsOpen && ciallo.IsBusy && !ciallo.CurrentOperation.IsCompleted && accountTransport.RequestCount == hiddenCialloRequests);
        Check("visual panel exposes a reachable switch for retained background tasks", UiVerification.ReceivesInput((Button)window.FindName("VisualTestNextTaskButton"), layout));
        window.HandleShortcut(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
        ciallo.CancelCommand.Execute(null); await ciallo.CurrentOperation; accountTransport.HoldResponse = null; Flush();
        await model.ProxyPanel.CurrentOperation; Flush();
        Check("hidden Ciallo cancellation updates background status without reopening", !model.IsCialloOpen && !model.IsModelTestRunning && !model.IsBusy);
        model.OpenBackgroundModelCommand.Execute(null); Flush();
        for (var attempt = 0; attempt < 16 && !ReferenceEquals(model.Ciallo, ciallo); attempt++)
        {
            var nextTask = (Button)window.FindName(model.IsCialloOpen ? "CialloNextTaskButton" : "VisualTestNextTaskButton");
            nextTask.Command.Execute(null); Flush();
        }
        Check("background task navigation restores the original Ciallo panel", ReferenceEquals(model.Ciallo, ciallo) && model.IsCialloOpen);
        Check("Ciallo panel exposes a reachable switch for retained background tasks", UiVerification.ReceivesInput((Button)window.FindName("CialloNextTaskButton"), layout));
        Check("Ciallo cancellation restores controls and marks partial run", ciallo.StartCommand.CanExecute(null) && ciallo.Status.Contains("已取消") && ciallo.Rows.Count(row => row.State == "已取消") == 1);
        var historyRequests = accountTransport.RequestCount;
        ciallo.SelectedHistory = ciallo.History.Last(entry => entry.Result.Completed == CialloTest.Models.Count); Flush();
        Check("Ciallo history restores old replies and recorded cache without requests", ciallo.History.Count >= 2
            && ciallo.Rows.All(row => row.State == "已完成" && row.Detail.Contains("缓存用量：128")) && accountTransport.RequestCount == historyRequests);
        var cialloReload = new CialloPanelViewModel(workspace.Accounts, ciallo.Profiles, ciallo.SelectedProfile, _ => {}, () => {});
        Check("Ciallo history reloads in a new panel without account requests", cialloReload.History.Count == ciallo.History.Count && accountTransport.RequestCount == historyRequests);
        Check("Ciallo history remains visible above the scroll", Displayed((ComboBox)window.FindName("CialloHistoryPicker")));
        var cacheRow = new CialloRow("fixture"); cacheRow.Update(new("fixture", "", "high", "completed", "reply", TimeSpan.Zero, new TokenUsage(0)));
        Check("zero cache is displayed as a real zero", cacheRow.Detail.Contains("缓存用量：0"));
        cacheRow.Update(new("fixture", "", "high", "completed", "reply", TimeSpan.Zero));
        Check("missing cache remains unknown instead of zero", cacheRow.Detail.Contains("缓存用量：未返回"));
        Capture("ciallo-history-cache-minimum", 1040, 670);
        var cialloModelPicker = (ComboBox)window.FindName("CialloModelPicker"); var cialloEffortPicker = (ComboBox)window.FindName("CialloEffortPicker");
        Check("Ciallo model is selectable and effort control is hidden at minimum size", Displayed(cialloModelPicker) && cialloEffortPicker.Visibility == Visibility.Collapsed);
        var beforeSelectedCiallo = accountTransport.RequestCount;
        cialloModelPicker.SetCurrentValue(ComboBox.SelectedItemProperty, "gpt-6-astra"); Flush();
        cialloEffortPicker.SetCurrentValue(ComboBox.SelectedItemProperty, "xhigh"); Flush();
        Check("Ciallo ComboBox selections update bound parameters without requests", ciallo.Model == "gpt-6-astra" && ciallo.Effort == "high" && accountTransport.RequestCount == beforeSelectedCiallo);
        var selectedCialloStart = (Button)window.FindName("CialloStartButton"); selectedCialloStart.Command.Execute(selectedCialloStart.CommandParameter);
        await ciallo.CurrentOperation; Flush();
        var selectedCialloBody = JsonNode.Parse(accountTransport.Requests[^1].Body!)!;
        Check("Ciallo bound start sends one selected model and effort to HTTP transport", accountTransport.RequestCount == beforeSelectedCiallo + 1
            && selectedCialloBody["model"]!.ToString() == "gpt-6-astra" && selectedCialloBody["reasoning"]!["effort"]!.ToString() == "high"
            && ciallo.Rows.Count == 1 && ciallo.Rows[0].State == "已完成" && ciallo.Rows[0].Detail.Contains("思考：高"));
        Check("Ciallo Escape closes the topmost panel", window.HandleShortcut(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None) && !model.IsCialloOpen);
        var emptyCiallo = new CialloPanelViewModel(workspace.Accounts, [], null, _ => {}, () => {});
        Check("Ciallo needs a saved account", !emptyCiallo.StartCommand.CanExecute(null));
        var managedRoot = Path.Combine(fixtureContainer, "单受管分身工作区"); Directory.CreateDirectory(managedRoot);
        File.WriteAllText(Path.Combine(managedRoot, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var originalHome = Path.Combine(fixtureContainer, "独立Codex本体"); Directory.CreateDirectory(originalHome);
        File.WriteAllText(Path.Combine(originalHome, "auth.json"), Credentials("original-account").ToJsonString());
        File.WriteAllText(Path.Combine(originalHome, "config.toml"), "model = \"original-model\"\n[mcp_servers.fixture]\ncommand = 'fixture-only'\n[plugins.'fixture@test']\nenabled = true\n");
        Directory.CreateDirectory(Path.Combine(originalHome, "skills", "fixture"));
        File.WriteAllText(Path.Combine(originalHome, "skills", "fixture", "SKILL.md"), "fixture skill");
        var linkedSkillSource = Path.Combine(fixtureContainer, "项目Skill"); Directory.CreateDirectory(linkedSkillSource);
        File.WriteAllText(Path.Combine(linkedSkillSource, "SKILL.md"), "linked fixture skill");
        LinkFixtures.CreateDirectory(fixtureContainer, Path.Combine(originalHome, "skills", "linked-fixture"), linkedSkillSource, "original skill directory link");
        LinkFixtures.CreateDirectory(fixtureContainer, Path.Combine(originalHome, "skills", "broken-fixture"), Path.Combine(fixtureContainer, "missing-skill"), "original broken skill link");
        Directory.CreateDirectory(Path.Combine(originalHome, "sessions")); File.WriteAllText(Path.Combine(originalHome, "sessions", "original.jsonl"), "original-history");
        var projectFile = Path.Combine(fixtureContainer, "用户项目.txt"); File.WriteAllText(projectFile, "user-project");
        var managedWorkspace = DesktopWorkspace.CreateManagedFixture(managedRoot, new(Path.Combine(managedRoot, "accounts"), originalHome));
        var managedModel = new ShellViewModel(managedWorkspace); window.DataContext = managedModel;
        Check("managed composition keeps stable account library separate from runtime and original", managedWorkspace.AccountRoot == Path.Combine(managedRoot, "accounts") && managedWorkspace.ActiveHome == Path.Combine(managedRoot, "runtime", "codex-home") && managedWorkspace.SwitchStorage is ManagedCloneStorage && managedWorkspace.Accounts!.Repository.ActiveHome is null && managedModel.Active is null && !managedModel.SaveCurrentAccountCommand.CanExecute(null) && !managedModel.NewProfileCommand.CanExecute(null));
        managedWorkspace.Profiles.CreateBoundAccount("分身甲", new("http://127.0.0.1:7890")); managedWorkspace.Profiles.SaveAccount("分身甲", Credentials("managed-a"), overwrite: true);
        File.WriteAllText(Path.Combine(managedWorkspace.Profiles.ProfileDirectory(new(IdentityKind.Account, "分身甲")), "config.toml"), "model='account-model'\n[mcp_servers.old]\ncommand='old-tool'\n");
        managedWorkspace.Profiles.CreateBoundAccount("分身乙", new("http://127.0.0.1:7891")); managedWorkspace.Profiles.SaveAccount("分身乙", Credentials("managed-b"), overwrite: true);
        managedModel.RefreshCommand.Execute(null); managedModel.Selected = managedModel.FilteredItems.Single(i => i.Name == "分身甲");
        managedModel.EditSelectedCommand.Execute(null); Capture("account-network-independent-minimum", 1040, 670);
        var networkToggle = (CheckBox)window.FindName("AccountFollowNetworkToggle");
        Check("managed account editor exposes linked network checkbox without manual proxy fields", Displayed(networkToggle) && networkToggle.Content.ToString() == "跟随 FlClash 当前网络"
            && !Displayed((FrameworkElement)window.FindName("AccountManualProxyFields")) && managedModel.Editor!.NetworkModeDescription.Contains("独立 IP"));
        networkToggle.IsChecked = true; Capture("account-network-follow-minimum", 1040, 670);
        Check("follow toggle links local timezone and old port release in visible guidance", managedModel.Editor!.FollowSystemProxy && managedModel.Editor.NetworkModeDescription.Contains("TUN") && managedModel.Editor.NetworkModeDescription.Contains("释放"));
        var beforeModeEdit = managedWorkspace.Profiles.ReadEnvironment("分身甲");
        var networkEditor = managedModel.Editor;
        var beforeModeRequests = managedWorkspace.Accounts!.OfflineTransport!.RequestCount;
        networkEditor.SaveCommand.Execute(null); await networkEditor.CurrentOperation;
        Check("saving follow mode through actual account editor commits linked configuration without remote requests", !managedModel.IsEditorOpen && !managedModel.IsError
            && managedWorkspace.Profiles.ReadEnvironment("分身甲").FollowSystemProxy && managedWorkspace.Profiles.ReadEnvironment("分身甲").Proxy == ""
            && managedWorkspace.Accounts.Repository.Proxy("分身甲") == "http://127.0.0.1:7890" && managedWorkspace.Accounts.OfflineTransport.RequestCount == beforeModeRequests);
        managedWorkspace.Profiles.SaveEnvironment("分身甲", beforeModeEdit); managedModel.RefreshCommand.Execute(null);
        managedModel.Selected = managedModel.FilteredItems.Single(i => i.Name == "分身甲");
        await managedModel.SwitchSelectedAsync(false);
        Check("managed switch uses only managed auth home and preserves original and project", managedModel.Active?.Name == "分身甲" && File.ReadAllText(Path.Combine(originalHome, "auth.json")).Contains("original-account") && File.ReadAllText(projectFile) == "user-project");
        Check("desktop composition loads account without importing original tools", File.ReadAllText(Path.Combine(managedWorkspace.ActiveHome, "config.toml")).Contains("account-model")
            && !File.ReadAllText(Path.Combine(managedWorkspace.ActiveHome, "config.toml")).Contains("mcp_servers")
            && !Directory.Exists(Path.Combine(managedWorkspace.ActiveHome, "skills")) && !Directory.Exists(Path.Combine(managedWorkspace.ActiveHome, "plugins"))
            && managedModel.SwitchDescription.Contains("不导入"));
        Check("valid and broken original Skill links cannot block managed switching", !managedModel.IsError
            && File.ReadAllText(Path.Combine(linkedSkillSource, "SKILL.md")) == "linked fixture skill"
            && (File.GetAttributes(Path.Combine(originalHome, "skills", "broken-fixture")) & FileAttributes.ReparsePoint) != 0);
        Directory.CreateDirectory(Path.Combine(managedWorkspace.ActiveHome, "sessions")); File.WriteAllText(Path.Combine(managedWorkspace.ActiveHome, "sessions", "stale.jsonl"), "stale-session");
        File.WriteAllText(Path.Combine(managedWorkspace.PackagedDesktopRoot!, "stale-cookie"), "stale-cookie");
        managedModel.Selected = managedModel.FilteredItems.Single(i => i.Name == "分身乙"); await managedModel.SwitchSelectedAsync(false);
        Check("managed switch rebuilds single runtime without archiving per-account history", managedModel.Active?.Name == "分身乙" && !Directory.Exists(Path.Combine(managedWorkspace.ActiveHome, "sessions")) && !File.Exists(Path.Combine(managedWorkspace.PackagedDesktopRoot!, "stale-cookie")) && !Directory.Exists(Path.Combine(managedWorkspace.Profiles.ProfileDirectory(new(IdentityKind.Account, "分身甲")), "sessions")) && managedWorkspace.Profiles.ListAccounts().Count == 2);
        await managedWorkspace.ProxyServices.NodeTesting!.ProbeScoredExitAsync("http://127.0.0.1:7891", default);
        managedWorkspace.Accounts!.OfflineTransport!.FailRequests = true; var beforeBlockedLaunchStops = managedWorkspace.OfflineRuntime!.StopCalls;
        managedModel.Selected = managedModel.FilteredItems.Single(i => i.Name == "分身甲"); await managedModel.SwitchSelectedAsync(true);
        Check("managed launch blocks failed egress verification before stopping or rebuilding runtime", managedModel.IsError && managedModel.Active?.Name == "分身乙" && managedWorkspace.OfflineRuntime.StopCalls == beforeBlockedLaunchStops && managedWorkspace.OfflineRuntime.LastLaunchRequest is null && managedModel.LaunchEgressDescription.Contains("未完成"));
        managedWorkspace.Accounts.OfflineTransport.FailRequests = false; managedModel.Selected = managedModel.FilteredItems.Single(i => i.Name == "分身乙");
        managedWorkspace.OfflineRuntime.LaunchStatus = LaunchStatus.Failed;
        await managedModel.LaunchCurrentAsync();
        Check("failed clone launch keeps independently verified egress without claiming runtime success", managedModel.IsError && managedModel.SelectedExitIp == "192.0.2.10" && managedModel.SelectedRuntimeState == "未运行"
            && managedWorkspace.Profiles.ReadExitObservation(new(IdentityKind.Account, "分身乙"))?.Ip == "192.0.2.10");
        managedWorkspace.OfflineRuntime.LaunchStatus = LaunchStatus.Confirmed;
        await managedModel.SwitchSelectedAsync(true);
        var managedLaunchEnvironment = managedWorkspace.OfflineRuntime.LastLaunchRequest?.Environment;
        Check("managed launch uses verified egress timezone and English without changing system timezone", managedModel.IdentityState.Running?.Name == "分身乙" && managedLaunchEnvironment is { } launchEnvironment && launchEnvironment.GetValueOrDefault("HTTP_PROXY") == "http://127.0.0.1:7891" && launchEnvironment.GetValueOrDefault("TZ") == "America/Chicago" && managedWorkspace.OfflineRuntime.LastLaunchRequest!.ExpectedEgressIp == "192.0.2.10" && managedWorkspace.OfflineRuntime.LastLaunchRequest.Arguments.Contains("--accept-lang=en-US") && !managedWorkspace.OfflineRuntime.LastTimezoneEnabled && managedModel.LaunchEgressDescription.Contains("192.0.2.10") && managedModel.LaunchEgressDescription.Contains("America/Chicago"));
        managedModel.Navigate("Settings"); Capture("managed-runtime-settings");
        Check("managed settings hide manual rebuild and original cleanup", managedModel.ResetManagedCommand.CanExecute(null)
            && Displayed((Button)window.FindName("ResetManagedButton"))
            && !Displayed((Button)window.FindName("OriginalCodexResetButton")));
        var originalAuthBeforeOneClick = File.ReadAllBytes(Path.Combine(originalHome, "auth.json"));
        var originalHistoryBeforeOneClick = File.ReadAllBytes(Path.Combine(originalHome, "sessions", "original.jsonl"));
        var managedEnvironmentBeforeOneClick = managedWorkspace.Profiles.ReadEnvironment("分身乙");
        var managedSelectionBeforeOneClick = managedModel.Selected?.Identity;
        var closeRuntimeAuth = File.ReadAllBytes(Path.Combine(managedWorkspace.ActiveHome, "auth.json"));
        var closeAccountAuth = File.ReadAllBytes(Path.Combine(managedWorkspace.Profiles.ProfileDirectory(new(IdentityKind.Account, "分身乙")), "auth.json"));
        var closeRequestCount = managedWorkspace.Accounts.OfflineTransport.RequestCount;
        Check("single launch entry reflects confirmed selected clone and prevents duplicate startup", managedModel.HomeLaunchLabel == "运行中" && !managedModel.HomeLaunchCommand.CanExecute(null)
            && managedModel.CloseManagedCommand.CanExecute(null) && Displayed((Button)window.FindName("BottomCloseManagedButton")));
        managedWorkspace.OfflineRuntime.HasPendingLaunch = true; var closeStops = managedWorkspace.OfflineRuntime.StopCalls;
        managedModel.CloseManagedCommand.Execute(null); await managedModel.CurrentOperation;
        Check("standalone close refuses an unconfirmed launch without stopping", managedWorkspace.OfflineRuntime.StopCalls == closeStops && managedModel.IdentityState.Running is not null);
        managedWorkspace.OfflineRuntime.HasPendingLaunch = false; managedWorkspace.OfflineRuntime.StopSucceeds = false;
        managedModel.CloseManagedCommand.Execute(null); await managedModel.CurrentOperation;
        Check("standalone close failure keeps running identity and files", managedModel.IsError && managedModel.IdentityState.Running is not null && managedModel.CloseManagedCommand.CanExecute(null)
            && closeRuntimeAuth.SequenceEqual(File.ReadAllBytes(Path.Combine(managedWorkspace.ActiveHome, "auth.json"))));
        managedWorkspace.OfflineRuntime.StopSucceeds = true;
        managedModel.CloseManagedCommand.Execute(null); await managedModel.CurrentOperation;
        Check("standalone close preserves active account runtime library original and timezone without account requests", managedModel.IdentityState.Running is null && managedModel.RunningLabel == "已退出"
            && managedModel.Active?.Name == "分身乙" && managedModel.HomeLaunchLabel == "启动" && !managedModel.CloseManagedCommand.CanExecute(null)
            && managedWorkspace.Accounts.OfflineTransport.RequestCount == closeRequestCount
            && closeRuntimeAuth.SequenceEqual(File.ReadAllBytes(Path.Combine(managedWorkspace.ActiveHome, "auth.json")))
            && closeAccountAuth.SequenceEqual(File.ReadAllBytes(Path.Combine(managedWorkspace.Profiles.ProfileDirectory(new(IdentityKind.Account, "分身乙")), "auth.json")))
            && originalAuthBeforeOneClick.SequenceEqual(File.ReadAllBytes(Path.Combine(originalHome, "auth.json")))
            && originalHistoryBeforeOneClick.SequenceEqual(File.ReadAllBytes(Path.Combine(originalHome, "sessions", "original.jsonl")))
            && managedWorkspace.Accounts.OfflineRegion!.TimezoneWrites == 0 && managedWorkspace.OriginalMaintenance is not null);
        Capture("managed-closed-preserves-login-minimum", 1040, 670);
        var beforeUnifiedLaunch = managedWorkspace.OfflineRuntime.StopCalls;
        managedModel.HomeLaunchCommand.Execute(null); await managedModel.CurrentOperation;
        Check("single launch starts current active account through existing launch service", managedModel.IdentityState.Running?.Name == "分身乙"
            && managedModel.HomeLaunchLabel == "运行中" && managedWorkspace.OfflineRuntime.StopCalls > beforeUnifiedLaunch);
        var settingsAdvancedManaged = (CheckBox)window.FindName("SettingsAdvancedToggle"); settingsAdvancedManaged.IsChecked = true; Flush();
        Check("managed reset consolidates logout and rebuild into existing single action", window.FindName("ManagedCloneResetButton") is null && ((Button)window.FindName("ResetManagedButton")).Command == managedModel.ResetManagedCommand
            && !Descendants((FrameworkElement)window.FindName("AdvancedEnvironmentPanel")).OfType<Button>().Any(button => button.Command == managedModel.LogoutCommand && Displayed(button)));
        Capture("managed-consolidated-settings-minimum", 1040, 670); settingsAdvancedManaged.IsChecked = false;
        managedWorkspace.OfflineRuntime.StopSucceeds = false;
        managedModel.ResetManagedCommand.Execute(null); await managedModel.CurrentOperation;
        Check("one click managed rebuild stop failure preserves runtime account library and original", managedModel.IsError
            && File.Exists(Path.Combine(managedWorkspace.ActiveHome, "auth.json")) && managedModel.Active?.Name == "分身乙"
            && managedWorkspace.Profiles.ListAccounts().All(account => account.HasCredentials)
            && originalAuthBeforeOneClick.SequenceEqual(File.ReadAllBytes(Path.Combine(originalHome, "auth.json"))));
        managedWorkspace.OfflineRuntime.StopSucceeds = true;
        ((Button)window.FindName("ResetManagedButton")).Command.Execute(null); await managedModel.CurrentOperation; Flush();
        Check("one click managed rebuild executes without extra editor and preserves selected account library original and project", !managedModel.IsError
            && managedModel.Editor is null && managedModel.Active is null && !File.Exists(Path.Combine(managedWorkspace.ActiveHome, "auth.json"))
            && managedModel.Selected?.Identity == managedSelectionBeforeOneClick
            && managedWorkspace.Profiles.ListAccounts().Count == 2 && managedWorkspace.Profiles.ListAccounts().All(account => account.HasCredentials)
            && managedWorkspace.Profiles.ReadEnvironment("分身乙") == managedEnvironmentBeforeOneClick
            && originalAuthBeforeOneClick.SequenceEqual(File.ReadAllBytes(Path.Combine(originalHome, "auth.json")))
            && originalHistoryBeforeOneClick.SequenceEqual(File.ReadAllBytes(Path.Combine(originalHome, "sessions", "original.jsonl")))
            && File.ReadAllText(projectFile) == "user-project");
        await managedModel.SwitchSelectedAsync(false);
        ((CheckBox)window.FindName("SettingsAdvancedToggle")).IsChecked = true; Capture("managed-runtime-advanced-settings");
        Check("managed maintenance has one direct reset command", managedModel.ResetManagedCommand.CanExecute(null) && managedModel.Editor is null);
        managedModel.RebuildOriginalCommand.Execute(null);
        Check("skipping tool import does not relax original cleanup link protection", managedModel.Editor is null && managedModel.IsError
            && File.Exists(Path.Combine(linkedSkillSource, "SKILL.md")) && File.Exists(Path.Combine(originalHome, "auth.json")));
        Directory.Delete(Path.Combine(originalHome, "skills", "linked-fixture")); // Remove only the fixture link before testing the separate cleanup flow.
        Directory.Delete(Path.Combine(originalHome, "skills", "broken-fixture"));
        managedModel.RebuildOriginalCommand.Execute(null); var originalEditor = managedModel.Editor!; Capture("original-codex-cleanup-warning");
        Check("original cleanup has explicit original warning and a separate target preview", originalEditor.Mode == ProfileEditMode.RebuildOriginal && originalEditor.Description.Contains("影响独立的 Codex 本体") && originalEditor.SaveLabel.Contains("确认") && originalEditor.Details.Contains(originalHome) && !originalEditor.Details.Contains(managedWorkspace.ManagedPaths!.RuntimeRoot) && File.Exists(Path.Combine(originalHome, "auth.json")));
        Check("original cleanup warning wraps and stays visible in confirmation dialog", ((TextBlock)window.FindName("EditorDescriptionText")).ActualHeight > 26 && Displayed((TextBlock)window.FindName("EditorDescriptionText")));
        originalEditor.CancelCommand.Execute(null);
        Check("cancel original cleanup leaves original and managed runtime intact", File.Exists(Path.Combine(originalHome, "auth.json")) && File.Exists(Path.Combine(managedWorkspace.ActiveHome, "auth.json")) && File.ReadAllText(projectFile) == "user-project");
        managedModel.RebuildCommand.Execute(null); await managedModel.CurrentOperation;
        Check("explicit managed cleanup resets runtime while preserving account authorization and original", managedModel.Editor is null && managedModel.Active is null && !File.Exists(Path.Combine(managedWorkspace.ActiveHome, "auth.json")) && managedWorkspace.Profiles.ListAccounts().All(account => account.HasCredentials) && File.Exists(Path.Combine(originalHome, "sessions", "original.jsonl")) && File.ReadAllText(projectFile) == "user-project");
        managedModel.Selected = managedModel.FilteredItems.Single(i => i.Name == "分身甲"); await managedModel.SwitchSelectedAsync(false);
        managedModel.Navigate("Settings"); managedModel.RebuildOriginalCommand.Execute(null); var cleanOriginalEditor = managedModel.Editor!; cleanOriginalEditor.SaveCommand.Execute(null); await cleanOriginalEditor.CurrentOperation;
        Check("explicit original cleanup affects only original state and retains configuration and project", managedModel.Editor is null && !File.Exists(Path.Combine(originalHome, "auth.json")) && !Directory.Exists(Path.Combine(originalHome, "sessions")) && File.ReadAllText(Path.Combine(originalHome, "config.toml")).Contains("original-model") && File.Exists(Path.Combine(managedWorkspace.ActiveHome, "auth.json")) && managedModel.Active?.Name == "分身甲" && File.ReadAllText(projectFile) == "user-project");
        await AccountIpChangeVerification.RunAsync(window, Path.Combine(fixtureContainer, "account-ip-change"), Check, name => Capture(name, 1040, 670));
        await VisualTestVerification.RunAsync(window, Path.Combine(fixtureContainer, "fingerprint-batch"), Check, name => Capture(name, 1040, 670));
        await AccountMarksVerification.RunAsync(Path.Combine(directory, "account-marks"), Check);
        await AccountFreezeVerification.RunAsync(window, Path.Combine(fixtureContainer, "account-freeze"), Check, name => Capture(name, 1040, 670));
        await SupportReportVerification.RunAsync(window, Path.Combine(fixtureContainer, "support-report"), Check, name => Capture(name, 1040, 670));
        await ResidentialExemptionVerification.RunAsync(window, Path.Combine(fixtureContainer, "residential-exemption"), Check, name => Capture(name, 1040, 670));
        window.DataContext = model;
        Check("storage UI has no binding warnings or errors", bindingListener.Errors.Count == 0);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
        File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new { passed = true, bindingErrors = bindingListener.Errors, mode = "real_storage_with_offline_runtime", actualAccountOperations = false, actualProxyChanges = false, actualFirewallChanges = false, actualOriginalCodexCleanup = false, actualFingerprintRequests = false, actualNodeProbes = false, fixtureContainer, checks, renders }, new JsonSerializerOptions { WriteIndented = true }));
        LinkFixtures.Report();
        window.Close();
    }
    private sealed class FakeTray : ITrayHost
    {
        public bool Visible { get; private set; }
        public bool Disposed { get; private set; }
        public event EventHandler? RestoreRequested;
        public event EventHandler? ExitRequested;
        public void Show(string tooltip) => Visible = true;
        public void Hide() => Visible = false;
        public void RequestRestore() => RestoreRequested?.Invoke(this, EventArgs.Empty);
        public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);
        public void Dispose() { Disposed = true; Visible = false; }
    }
    private sealed class FakeLease : ISingleInstanceLease
    {
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsPrimary => true;
        public bool Disposed { get; private set; }
        public bool ListenerStopped { get; private set; }
        public bool RequestActivation() => true;
        public Task ListenForActivationAsync(Action activate, CancellationToken cancellationToken) { cancellationToken.Register(() => { ListenerStopped = true; completed.TrySetCanceled(cancellationToken); }); return completed.Task; }
        public void Dispose() { if (!ListenerStopped) throw new InvalidOperationException("Instance released before listener joined"); Disposed = true; }
    }
    private static IEnumerable<string> TextValues(DependencyObject root)
    {
        if (root is TextBlock text) yield return text.Text;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var value in TextValues(VisualTreeHelper.GetChild(root, index))) yield return value;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var value in Descendants(VisualTreeHelper.GetChild(root, index))) yield return value;
    }
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) return value;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        return null;
    }
    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T value) return value;
        return null;
    }
}
