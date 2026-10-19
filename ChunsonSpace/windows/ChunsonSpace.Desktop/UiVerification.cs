using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;

namespace ChunsonSpace.Desktop;

internal static class UiVerification
{
    // Narrow reuse of the existing fixture and renderer; no live window or desktop interaction.
    internal static async Task RunEnvironmentActionsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var screenshots = new List<string>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("Environment action assertion failed: " + name);
        }
        await LocalEnvironmentVerification.RunAsync(Path.Combine(directory, "fixture"), Check);
        var model = new ShellViewModel();
        var window = CreateWindow(model);
        var root = window.RenderRoot;
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Capture(string name)
        {
            var bitmap = new RenderTargetBitmap(1040, 670, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, name + ".png");
            using (var file = File.Create(path)) encoder.Save(file);
            screenshots.Add(path);
        }
        try
        {
            foreach (var dark in new[] { false, true })
            {
                model.DarkTheme = dark; model.Navigate("Accounts");
                ArrangeForCapture(root, 1040, 670);
                var refresh = Control<Button>("HomeRecheckEnvironmentButton");
                var settings = Control<Button>("HomeSyncTimezoneButton");
                var status = Control<TextBlock>("LocalEnvironmentStatusText");
                var actions = new[] { refresh, settings, Control<Button>("AddAccountLoginButton"), Control<Button>("ImportLocalAccountButton") };
                Check($"{dark}: minimum home actions visible and separate", actions.All(x => FullyVisible(x, root))
                    && actions.SelectMany((a, i) => actions.Skip(i + 1).Select(b => DoNotOverlap(a, b, root))).All(x => x)
                    && FullyVisible(status, root) && actions.All(x => DoNotOverlap(status, x, root))
                    && refresh.ActualHeight >= 38 && settings.ActualHeight >= 38
                    && Within(Control<Grid>("HomeTitleBar"), root) && FullyVisible(Control<ListBox>("ProfileList"), root));
                Check($"{dark}: home keeps readonly refresh and settings navigation", refresh.Command == model.RecheckLocalEnvironmentCommand
                    && settings.Command == model.NavigateCommand && Equals(settings.CommandParameter, "Settings"));
                if (!dark) Capture("home-light-minimum");
                model.Navigate("Settings"); ArrangeForCapture(root, 1040, 670);
                var sync = Control<Button>("OneClickEnvironmentRegionButton");
                var feedback = Control<TextBlock>("EnvironmentRegionStatusText");
                Check($"{dark}: sync and nearby feedback remain visible", FullyVisible(sync, root) && FullyVisible(feedback, root)
                    && sync.ActualHeight >= 40 && DoNotOverlap(sync, feedback, root)
                    && sync.Command == model.ConfigureRegionCommand
                    && !VisualDescendants(Control<Grid>("SimpleEnvironmentPanel")).OfType<ScrollViewer>().Any(x => x.IsAncestorOf(feedback)));
                if (dark) Capture("settings-dark-minimum");
            }
        }
        finally { window.Close(); }
        File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new { passed = true, scope = "environment-actions", checks, screenshots, realRequests = 0, realSystemWrites = 0 }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static MainWindow CreateWindow(ShellViewModel model) => new()
    {
        DataContext = model, ShowActivated = false, ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = SystemParameters.VirtualScreenLeft - 3000, Top = SystemParameters.VirtualScreenTop - 3000
    };
    internal static void AssertWindowsOutsideDesktop()
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            if (!window.IsVisible) continue;
            if (window.ShowInTaskbar || window.IsActive || window.ShowActivated
                || new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight).IntersectsWith(screen))
                throw new InvalidOperationException("Verification window must remain outside the desktop without activation or taskbar entry.");
        }
    }
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var listener = new BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var checks = new List<object>();
        var renders = new List<object>();
        var model = new ShellViewModel();
        var fonts = EmbeddedFonts.Inspect();
        var window = CreateWindow(model);
        var root = window.RenderRoot;
        void Check(string name, bool condition)
        {
            checks.Add(new { name, passed = condition });
            if (!condition) throw new InvalidOperationException("UI assertion failed: " + name);
        }
        void Flush() => root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Chunson.Widgets.ClockLauncherChecks.Run((passed, name) => Check(name, passed));
        KeyboardRoutingVerification.Run(Path.Combine(directory, "keyboard-routing"), Check, ArrangeForCapture);
        ModelAccessibilityVerification.Run(Path.Combine(directory, "model-accessibility"), Check, ArrangeForCapture);
        ProxyAccessibilityVerification.Run(Path.Combine(directory, "proxy-accessibility"), Check, ArrangeForCapture);
        EditorAccessibilityVerification.Run(Path.Combine(directory, "editor-accessibility"), Check, ArrangeForCapture);
        AccountContextVerification.Run(Path.Combine(directory, "account-context"), Check, ArrangeForCapture);
        void Render(string name, int width = 1280, int height = 850)
        {
            ArrangeForCapture(root, width, height);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, name + ".png");
            using (var output = File.Create(path)) encoder.Save(output);
            renders.Add(new { name, path, width, height, source = "WPF XAML RenderTargetBitmap" });
        }
        Render("accounts-default");
        var syncTimezone = (Button)window.FindName("HomeSyncTimezoneButton");
        var recheckEnvironment = (Button)window.FindName("HomeRecheckEnvironmentButton");
        Check("homepage has a separate readonly recheck command", FullyVisible(recheckEnvironment, root) && recheckEnvironment.Command == model.RecheckLocalEnvironmentCommand && !recheckEnvironment.IsEnabled);
        Check("homepage timezone shortcut navigates to settings without a system writer", FullyVisible(syncTimezone, root) && syncTimezone.Command == model.NavigateCommand && syncTimezone.CommandParameter?.ToString() == "Settings" && syncTimezone.IsEnabled);
        Render("home-timezone-minimum", 1040, 670);
        var importLocal = (Button)window.FindName("ImportLocalAccountButton");
        Check("local import is visible at minimum size and disabled in preview", FullyVisible(importLocal, root) && importLocal.Command == model.ImportLocalAccountCommand && !importLocal.IsEnabled);
        Check("minimum homepage keeps recheck timezone and environment status separate from login", FullyVisible(recheckEnvironment, root) && FullyVisible(syncTimezone, root) && FullyVisible((TextBlock)window.FindName("LocalEnvironmentStatusText"), root) && FullyVisible((Button)window.FindName("AddAccountLoginButton"), root));
        model.DarkTheme = true; Render("home-timezone-dark-minimum", 1040, 670); model.DarkTheme = false;
        Render("accounts-default-restored");
        Check("English and Chinese regular and bold glyphs use embedded fonts", fonts.UsesEmbeddedEnglishAndChinese);
        Check("default is explicit preview", model.RunningLabel.Contains("演示") && model.Active?.Name == "日常工作");
        var list = (ListBox)window.FindName("ProfileList");
        var selectedContainer = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
        var selectedFrame = (Border)selectedContainer.Template.FindName("Frame", selectedContainer);
        Check("selected account row has a clear accent edge and selected surface", selectedFrame.BorderThickness.Left == 3 && selectedFrame.BorderBrush == window.FindResource("AccentBrush") && selectedFrame.Background == window.FindResource("SelectionBrush"));
        var firstContainer = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var firstFrame = (Border)firstContainer.Template.FindName("Frame", firstContainer);
        Check("unselected account row uses shared separator without shifting row geometry", firstFrame.BorderThickness.Bottom >= 1 && firstFrame.BorderBrush == window.FindResource("LineBrush") && firstContainer.ActualHeight == selectedContainer.ActualHeight);
        Check("default account table shows ten complete rows and actual row actions", model.ItemCount == 10 && model.IdentityPageItems.Count == 10
            && IdentityRowsFullyVisible(list, root, model, Path.Combine(directory, "preview-row-layout-failure.json")));
        Check("preview home does not invent detected IP enabled protection or running runtime", model.SelectedExitIp == "未验证"
            && model.SelectedNetworkProtection == "未检查" && model.SelectedDnsProtection == "未检查"
            && model.SelectedRuntimeState == "未运行" && model.SelectedExitTimezoneLabel == "配置时区");
        Check("default right detail shows bound actual state and complete action buttons", HomeDetailsFullyVisible(window, root, model, Path.Combine(directory, "preview-home-detail-layout-failure.json")));
        var homeLaunch = (Button)window.FindName("BottomLaunchButton");
        var homeRebuild = window.FindName("BottomRebuildButton");
        Check("home primary actions bind selected-target launch and managed-only rebuild", homeLaunch.Command == model.HomeLaunchCommand
            && homeLaunch.Content?.ToString() == model.HomeLaunchLabel && homeRebuild is null
            && !model.ResetManagedCommand.CanExecute(null));
        Check("account context action selects the clicked row without changing the active identity", window.SelectContextIdentity(firstContainer)
            && ReferenceEquals(model.Selected, firstContainer.DataContext) && model.Active?.Name == "日常工作");
        var contextMenu = ArrangeAccountContextMenu(firstContainer);
        Check("account popup commands bind to the row shell and selected target", AccountContextCommandsMatch(contextMenu, model)
            && contextMenu.DataContext == model && model.HomeLaunchTarget == "目标：" + model.Selected!.Name);
        list.SelectedIndex = 1; Flush();
        model.DarkTheme = true; Render("accounts-borders-dark"); model.DarkTheme = false;
        list.SelectedItem = PreviewDataSource.Identities[2]; Flush();
        Check("list selection updates selected independently", model.Selected?.Name == "个人空间" && model.Active?.Name == "日常工作");
        Check("selected and running identities are independent", model.IdentityState.Selected?.Name == "个人空间" && model.IdentityState.Running is null);
        var search = (TextBox)window.FindName("SearchBox");
        search.Text = "不存在的档案"; Flush();
        Check("search binding produces empty state", model.IsEmpty && list.Items.Count == 0);
        Check("no selection disables switch", !model.SimulateSwitchCommand.CanExecute(null));
        Render("accounts-empty");
        model.ClearSearchCommand.Execute(null); Flush();
        Check("clear search restores all ten account rows", model.ItemCount == 10);
        model.AccountFilter = "待登录"; Flush();
        Check("pending account filter resets paging and exposes only pending accounts", model.ItemCount == 1 && model.FilteredItems.All(item => item.Subtitle == "待登录")
            && !model.PreviousIdentityPageCommand.CanExecute(null) && !model.NextIdentityPageCommand.CanExecute(null));
        model.AccountFilter = "当前使用"; Flush();
        Check("current account filter matches actual active identity", model.ItemCount == 1 && model.FilteredItems.Single().Identity == model.Active!.Identity);
        model.AccountFilter = "全部账号"; Flush();
        Check("all account filter restores ten saved rows", model.ItemCount == 10);
        list.SelectedIndex = 1; Flush();
        model.SimulateSwitchCommand.Execute(null);
        Check("simulated switch changes preview activity", model.Active?.Name == "开发测试" && model.Feedback.Contains("没有访问凭据"));
        var beforeFailure = model.Active;
        model.SimulateFailureCommand.Execute(null);
        Check("failure preserves active identity and recovery explanation", model.IsError && model.Active == beforeFailure && model.Feedback.Contains("恢复材料保留"));
        Render("accounts-error");
        model.NavigateCommand.Execute("Providers"); Flush();
        Check("retired provider navigation is unavailable and keeps current account", model.IsAccountsPage && model.ItemCount == 10 && model.Active == beforeFailure && window.FindName("NavProviders") is null && window.FindName("ProviderKeyBox") is null);
        model.OpenDetailsCommand.Execute(null);
        Render("account-actions-minimum", 1040, 670);
        var switchButton = (Button)window.FindName("SwitchButton");
        var switchBounds = switchButton.TransformToAncestor(root).TransformBounds(new Rect(new Point(), switchButton.RenderSize));
        Check("minimum size keeps actual primary action fully visible", FullyVisible(switchButton, root) && switchBounds.Top >= 0 && switchBounds.Bottom <= 660 && switchButton.ActualHeight >= 28 && switchButton.Command == model.SwitchCommand);
        model.CloseDetailsCommand.Execute(null);
        model.Search = "missing"; Flush();
        Check("escape shortcut clears search", window.HandleShortcut(Key.Escape, ModifierKeys.None) && model.Search == "" && model.ItemCount == 10);
        Check("search shortcut is available", window.HandleShortcut(Key.F, ModifierKeys.Control));
        Render("backups-default");
        Check("restore clearly reports simulation", model.FeedbackTitle.Contains("模拟") && model.Feedback.Contains("没有覆盖"));
        model.NavigateCommand.Execute("Settings"); Flush();
        model.IsolateSessions = false; model.SaveSettingsCommand.Execute(null);
        Check("settings do not imply persistence", !model.IsolateSessions && model.Feedback.Contains("演示设置仅在当前窗口"));
        Render("settings-default");
        var settingsAdvanced = (CheckBox)window.FindName("SettingsAdvancedToggle");
        Render("settings-simple-minimum", 1040, 670);
        Check("environment defaults keep region action and the single reset", settingsAdvanced.IsChecked == false
            && ((FrameworkElement)window.FindName("AdvancedEnvironmentPanel")).Visibility == Visibility.Collapsed
            && FullyVisible((FrameworkElement)window.FindName("SimpleEnvironmentPanel"), root)
            && FullyVisible((Button)window.FindName("OneClickEnvironmentRegionButton"), root)
            && FullyVisible((Button)window.FindName("ResetManagedButton"), root)
            && ((Button)window.FindName("OneClickEnvironmentRegionButton")).Command == model.ConfigureRegionCommand
            && ((Button)window.FindName("ResetManagedButton")).Command == model.ResetManagedCommand);
        var windowsSync = (Button)window.FindName("OneClickEnvironmentRegionButton");
        var syncLabel = new FormattedText(windowsSync.Content.ToString()!, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(windowsSync.FontFamily, windowsSync.FontStyle, windowsSync.FontWeight, windowsSync.FontStretch),
            windowsSync.FontSize, windowsSync.Foreground, VisualTreeHelper.GetDpi(windowsSync).PixelsPerDip);
        Check("minimum settings keeps the full explicit Windows sync label readable", syncLabel.WidthIncludingTrailingWhitespace
            <= windowsSync.ActualWidth - windowsSync.Padding.Left - windowsSync.Padding.Right - windowsSync.BorderThickness.Left - windowsSync.BorderThickness.Right);
        settingsAdvanced.IsChecked = true; Render("settings-advanced-minimum", 1040, 670);
        Check("environment advanced controls remain explicitly reachable", ((FrameworkElement)window.FindName("SimpleEnvironmentPanel")).Visibility == Visibility.Collapsed
            && FullyVisible((FrameworkElement)window.FindName("AdvancedEnvironmentPanel"), root)
            && ((Button)window.FindName("ManagedCloneResetButton")).Visibility == Visibility.Collapsed
            && FullyVisible((Button)window.FindName("OriginalCodexResetButton"), root));
        settingsAdvanced.IsChecked = false;
        model.Navigate("Proxy"); Render("proxy-simple-minimum", 1040, 670);
        var proxyAdvanced = (CheckBox)window.FindName("ProxySimpleAdvancedToggle");
        Check("network page exposes global DNS and IP screening without an IP selector", proxyAdvanced.IsChecked == false
            && ((FrameworkElement)window.FindName("AdvancedProxyPanel")).Visibility == Visibility.Collapsed
            && FullyVisible((Button)window.FindName("NetworkIpButton"), root)
            && FullyVisible((Button)window.FindName("SimpleProxySetupButton"), root)
            && ((Button)window.FindName("SimpleProxySetupButton")).Command == model.ProxyPanel.ConfigureNetworkCommand);
        proxyAdvanced.IsChecked = true; Render("proxy-advanced-minimum", 1040, 670);
        Check("proxy advanced mode is reversible and exposes original group controls", ((FrameworkElement)window.FindName("SimpleProxyPanel")).Visibility == Visibility.Collapsed
            && FullyVisible((ComboBox)window.FindName("ProxyGroupBox"), root));
        proxyAdvanced.IsChecked = false; model.DarkTheme = true;
        Render("proxy-simple-dark-minimum", 1040, 670);
        model.Navigate("Settings"); Render("settings-simple-dark-minimum", 1040, 670);
        model.DarkTheme = false;
        model.Navigate("License");
        Check("about and license navigation removed", model.Page == "Settings");
        Check("window uses new branding", window.Title.Contains("Chunson Space"));
        model.NavigateCommand.Execute("Accounts"); Flush();
        Render("accounts-minimum", 1040, 670);
        Check("minimum account table keeps row actions and right detail controls fully visible", IdentityRowsFullyVisible(list, root, model,
            Path.Combine(directory, "preview-row-layout-failure.json")) && HomeDetailsFullyVisible(window, root, model, Path.Combine(directory, "preview-home-detail-layout-failure.json")));
        Check("minimum home retains launch with separate rebuild hidden", FullyVisible(homeLaunch, root) && homeRebuild is null && homeLaunch.ActualHeight >= 36);
        Check("unconnected actions disabled", !model.UnavailableCommand.CanExecute(null));
        foreach (var size in new[] { (Width: 1040, Height: 670), (Width: 1280, Height: 850) })
        {
            var navigation = new[] { (Name: "NavAccounts", Page: "Accounts"), (Name: "NavProxy", Page: "Proxy"), (Name: "NavBackups", Page: "Backups"), (Name: "NavSettings", Page: "Settings") };
            model.DarkTheme = true; Render("sidebar-dark-" + size.Width, size.Width, size.Height);
            var reachable = true;
            foreach (var entry in navigation)
            {
                var button = (Button)window.FindName(entry.Name);
                reachable &= FullyVisible(button, root) && button.IsEnabled && button.IsHitTestVisible && button.Command == model.NavigateCommand && button.CommandParameter?.ToString() == entry.Page;
                button.Command.Execute(button.CommandParameter); Flush();
                reachable &= model.Page == entry.Page;
            }
            Check($"all sidebar destinations remain visible and navigate at {size.Width}x{size.Height}", reachable);
            var nodesButton = (Button)window.FindName("NavProxy"); var modelsButton = (Button)window.FindName("NavModels");
            Check($"merged network and model sidebar entrances remain visible without starting requests at {size.Width}x{size.Height}",
                FullyVisible(nodesButton, root) && nodesButton.IsHitTestVisible && nodesButton.Command == model.NavigateCommand && window.FindName("NavNodes") is null
                && FullyVisible(modelsButton, root) && modelsButton.IsHitTestVisible && modelsButton.Command == model.OpenModelTestCommand
                && DoNotOverlap(nodesButton, modelsButton, root));
            var controls = new[] { "MinimizeButton", "MaximizeButton", "CloseButton" }.Select(name => (Button)window.FindName(name)).ToArray();
            Check($"window controls remain distinct and reachable at {size.Width}x{size.Height}", controls.All(button => FullyVisible(button, root) && button.IsEnabled && button.IsHitTestVisible && button.ActualWidth >= 32 && button.ActualHeight >= 28)
                && controls.Zip(controls.Skip(1)).All(pair => Bounds(pair.First, root).Right <= Bounds(pair.Second, root).Left + 1));
            model.Navigate("Accounts"); model.DarkTheme = false; Render("sidebar-light-" + size.Width, size.Width, size.Height);
        }
        Check("no WPF binding errors", listener.Errors.Count == 0);
        File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new { passed = true, mode = "isolated_preview", actualAccountOperations = false, actualProxyChanges = false, actualFingerprintRequests = false, embeddedFonts = fonts, checks, renders, bindingErrors = listener.Errors }, new JsonSerializerOptions { WriteIndented = true }));
        PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        window.Close();
    }
    internal static Rect Bounds(FrameworkElement element, FrameworkElement root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(new Point(), element.RenderSize));

    internal static ContextMenu ArrangeAccountContextMenu(ListBoxItem row)
    {
        var menu = row.ContextMenu ?? throw new InvalidOperationException("Account row context menu missing");
        return ArrangeMenu(menu, row);
    }
    internal static ContextMenu ArrangeMenu(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        menu.ApplyTemplate();
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
        for (var pass = 0; pass < 2; pass++)
        {
            menu.Measure(new Size(320, 400));
            menu.Arrange(new Rect(new Point(), menu.DesiredSize)); menu.UpdateLayout();
            foreach (var item in menu.Items.OfType<MenuItem>()) item.ApplyTemplate();
        }
        menu.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        return menu;
    }

    internal static bool AccountContextCommandsMatch(ContextMenu menu, ShellViewModel model)
    {
        var items = menu.Items.OfType<MenuItem>().ToArray();
        var expected = new[] { (Label: "切换并启动", Command: model.HomeLaunchCommand), (Label: "账号配置", Command: model.EditSelectedCommand),
            (Label: "重命名", Command: model.RenameSelectedCommand), (Label: model.TestFreezeLabel, Command: model.ToggleTestFreezeCommand), (Label: "删除账号…", Command: model.ArchiveSelectedCommand) };
        return items.Length == expected.Length && items.Zip(expected).All(pair => pair.First.Header?.ToString() == pair.Second.Label
            && pair.First.Command == pair.Second.Command && pair.First.CommandParameter is null);
    }

    internal static void ArrangeForCapture(FrameworkElement root, int width, int height)
    {
        AssertWindowsOutsideDesktop();
        root.Width = width; root.Height = height;
        for (var pass = 0; pass < 2; pass++)
        {
            root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        }
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); root.UpdateLayout();
    }

    internal static bool Within(FrameworkElement element, FrameworkElement container)
    {
        var bounds = Bounds(element, container);
        return element.ActualWidth > 0 && element.ActualHeight > 0 && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= container.ActualWidth + 1 && bounds.Bottom <= container.ActualHeight + 1;
    }

    internal static bool DoNotOverlap(FrameworkElement first, FrameworkElement second, FrameworkElement root)
    {
        var a = Bounds(first, root); var b = Bounds(second, root);
        return a.Right <= b.Left + 1 || b.Right <= a.Left + 1 || a.Bottom <= b.Top + 1 || b.Bottom <= a.Top + 1;
    }

    internal static bool FullyVisible(FrameworkElement element, FrameworkElement root)
    {
        if (element.Visibility != Visibility.Visible || (element != root && !root.IsAncestorOf(element))) return false;
        var connected = false;
        for (DependencyObject? ancestor = element; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is UIElement { Visibility: not Visibility.Visible }) return false;
            if (ancestor is FrameworkElement viewport && ancestor != element
                && (viewport.ClipToBounds || viewport is ScrollContentPresenter)
                && !Within(element, viewport)) return false;
            if (ancestor is UIElement { Clip: { } clip } && ancestor is FrameworkElement clipped)
            {
                var visibleBounds = clip.Bounds;
                var boundsInClip = Bounds(element, clipped);
                visibleBounds.Inflate(1, 1);
                if (!visibleBounds.Contains(boundsInClip)) return false;
            }
            if (ancestor == root) { connected = true; break; }
        }
        if (!connected) return false;
        var bounds = Bounds(element, root);
        return element.ActualWidth > 0 && element.ActualHeight > 0 && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
    }

    internal static bool HomeDetailsFullyVisible(Window window, FrameworkElement root, ShellViewModel model, string evidencePath)
    {
        var panel = (FrameworkElement)window.FindName("SelectedExitDetail");
        var values = new[] { (Name: "SelectedExitIpText", Value: model.SelectedExitIp), (Name: "SelectedTimezoneText", Value: model.SelectedExitTimezone),
            (Name: "SelectedDnsProtectionText", Value: model.SelectedDnsProtection),
            (Name: "SelectedRuntimeStateText", Value: model.SelectedRuntimeState) };
        var fields = values.Select(value => (Element: (TextBlock)window.FindName(value.Name), value.Value)).ToArray();
        var buttons = new[] { (Button)window.FindName("HomeConfigureExitButton") };
        var controls = fields.Select(field => (FrameworkElement)field.Element).Concat(buttons).ToArray();
        if (!model.HasSelection)
            return FullyVisible(panel, root) && FullyVisible((FrameworkElement)window.FindName("EmptySelectedDetail"), root)
                && controls.All(control => !FullyVisible(control, root));
        var passed = FullyVisible(panel, root) && fields.All(field => field.Element.Text == field.Value)
            && controls.All(control => FullyVisible(control, root) && Within(control, panel))
            && buttons.All(button => button.ActualHeight >= 36 && button.ActualWidth >= 50 && button.IsHitTestVisible)
            && buttons[0].Command == model.IpQualityCommand && window.FindName("HomeEnvironmentManageButton") is null
            && controls.SelectMany((control, index) => controls.Skip(index + 1).Select(other => DoNotOverlap(control, other, root))).All(value => value);
        if (!passed)
        {
            object Fact(FrameworkElement element) => new { element.Name, type = element.GetType().Name,
                width = element.ActualWidth, height = element.ActualHeight, bounds = Bounds(element, root), visible = FullyVisible(element, root),
                withinDetail = element == panel || Within(element, panel) };
            var overlaps = controls.SelectMany((control, index) => controls.Skip(index + 1)
                .Where(other => !DoNotOverlap(control, other, root)).Select(other => new { first = control.Name, second = other.Name }));
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new { passed, width = root.ActualWidth, height = root.ActualHeight,
                panel = Fact(panel), controls = controls.Select(Fact), fields = fields.Select(field => new { name = field.Element.Name,
                    expected = field.Value, actual = field.Element.Text }), overlaps }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed;
    }

    internal static bool IdentityRowsFullyVisible(ListBox list, FrameworkElement root, ShellViewModel model, string evidencePath)
    {
        var rows = new List<ListBoxItem>();
        var facts = new List<object>();
        var passed = list.Items.Count == model.IdentityPageItems.Count && FullyVisible(list, root)
            && ScrollViewer.GetVerticalScrollBarVisibility(list) == ScrollBarVisibility.Disabled
            && ScrollViewer.GetHorizontalScrollBarVisibility(list) == ScrollBarVisibility.Disabled;
        foreach (var item in model.IdentityPageItems)
        {
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem row
                || row.Template.FindName("Frame", row) is not Border frame)
            {
                passed = false; facts.Add(new { account = item.Name, missingRow = true }); continue;
            }
            var cells = new[] { "RowAccountCell", "RowRouteCell", "RowStatusCell", "RowActionCell" }
                .Select(name => VisualDescendants(frame).OfType<FrameworkElement>().SingleOrDefault(element => element.Name == name)).ToArray();
            var port = VisualDescendants(frame).OfType<FrameworkElement>().SingleOrDefault(element => element.Name == "RowPortCell");
            var actions = VisualDescendants(frame).OfType<Button>().Where(button => button.Visibility == Visibility.Visible).ToArray();
            var rowPassed = FullyVisible(row, root) && FullyVisible(frame, root) && Within(row, list) && Within(frame, row)
                && row.ActualHeight >= 40 && cells.All(cell => cell is not null && FullyVisible(cell, root) && Within(cell, frame))
                && port is { Visibility: Visibility.Collapsed }
                && actions.Length == 3 && actions.Count(button => button.Command == model.OpenDetailsCommand) == 1
                && actions.Count(button => button.Command == model.ToggleTestFreezeCommand && Equals(button.CommandParameter, item)) == 1
                && actions.All(button => FullyVisible(button, root) && button.ActualHeight >= (button.Name == "RowMarkButton" ? 18 : 28) && button.ActualWidth >= 30
                    && button.IsHitTestVisible && Within(button, frame) && Within(button, list)
                    && cells[button.Name == "RowMarkButton" ? 0 : 3] is { } actionCell && Within(button, actionCell));
            if (cells.All(cell => cell is not null))
                rowPassed &= cells.Zip(cells.Skip(1)).All(pair => Bounds(pair.First!, root).Right <= Bounds(pair.Second!, root).Left + 1);
            rowPassed &= actions.SelectMany((button, index) => actions.Skip(index + 1).Select(other => DoNotOverlap(button, other, root))).All(value => value);
            rows.Add(row); passed &= rowPassed;
            object Fact(FrameworkElement element) => new { element.Name, width = element.ActualWidth, height = element.ActualHeight,
                bounds = Bounds(element, root), visible = FullyVisible(element, root) };
            facts.Add(new { account = item.Name, passed = rowPassed, row = Fact(row), frame = Fact(frame),
                cells = cells.Where(cell => cell is not null).Select(cell => Fact(cell!)), actions = actions.Select(Fact) });
        }
        passed &= rows.Zip(rows.Skip(1)).All(pair => Bounds(pair.First, root).Bottom <= Bounds(pair.Second, root).Top + 1);
        var viewport = VisualDescendants(list).OfType<ScrollContentPresenter>().FirstOrDefault();
        var scroller = VisualDescendants(list).OfType<ScrollViewer>().FirstOrDefault();
        passed &= viewport is not null && rows.All(row => Within(row, viewport)) && scroller is { VerticalOffset: 0, HorizontalOffset: 0 };
        if (!passed)
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new { passed, width = root.ActualWidth, height = root.ActualHeight,
                listBounds = Bounds(list, root), viewportBounds = viewport is null ? (Rect?)null : Bounds(viewport, root), rows = facts }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    internal static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var element in VisualDescendants(VisualTreeHelper.GetChild(root, index))) yield return element;
    }

    internal static bool ReceivesInput(FrameworkElement element, FrameworkElement root)
    {
        var center = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), root);
        DependencyObject? hit = null;
        // The regression window is hidden; test its rendered visual tree rather than Window.IsVisible.
        VisualTreeHelper.HitTest(root,
            node => node is UIElement ui && (ui.Visibility != Visibility.Visible || !ui.IsHitTestVisible)
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(center));
        while (hit is not null)
        {
            if (ReferenceEquals(hit, element)) return true;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return false;
    }

    internal static double ContrastRatio(Brush? foreground, Brush? background)
    {
        if (foreground is not SolidColorBrush first || background is not SolidColorBrush second || first.Color.A != 255 || second.Color.A != 255) return 0;
        static double Luminance(Color color)
        {
            static double Linear(byte channel) { var value = channel / 255d; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var a = Luminance(first.Color); var b = Luminance(second.Color);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    internal sealed class BindingListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
