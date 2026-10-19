using System.IO;
using ChunsonSpace.Core;
using ChunsonSpace.Application;
using ChunsonSpace.Windows;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.Desktop;

public sealed class DesktopWorkspace
{
    public DesktopWorkspace(string root, string activeHome, string desktopRoot, bool fixture = false, IRuntimeOperations? runtime = null)
        : this(root, activeHome, desktopRoot, null, fixture, runtime) { }
    public DesktopWorkspace(string root, string activeHome, string? packagedDesktopRoot, string? webDesktopRoot, bool fixture = false, IRuntimeOperations? runtime = null, ManagedClonePaths? managedPaths = null)
    {
        Root = Path.GetFullPath(root);
        ActiveHome = Path.GetFullPath(activeHome);
        OriginalHome = managedPaths is null ? ActiveHome : null;
        PackagedDesktopRoot = packagedDesktopRoot is null ? null : Path.GetFullPath(packagedDesktopRoot);
        WebDesktopRoot = webDesktopRoot is null ? null : Path.GetFullPath(webDesktopRoot);
        IsFixture = fixture;
        ManagedPaths = managedPaths;
        AccountRoot = managedPaths?.AccountRoot ?? Root;
        if (fixture && !File.Exists(Path.Combine(Root, "ChunsonSpace-fixture.json")))
            throw new InvalidOperationException("隔离工作区缺少 ChunsonSpace-fixture.json 标记。请提供已创建的测试工作区。");
        Profiles = new FileProfileStore(AccountRoot);
        ProxyServices = DesktopProxyServices.Create(this);
        StoragePaths = managedPaths?.StoragePaths ?? new(Root, ActiveHome, PackagedDesktopRoot, WebDesktopRoot);
        SwitchStorage = managedPaths is null ? new FileSwitchStorage(StoragePaths) : new ManagedCloneStorage(managedPaths);
        ProfileMaintenance = new(StoragePaths);
        Rebuild = new(StoragePaths, managedPaths is not null ? "" : fixture ? Path.GetDirectoryName(Root)! : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Runtime = runtime ?? (fixture ? new OfflineRuntimeOperations() : null);
        if (fixture) { CodexInstaller = new(new OfflineCodexInstallSystem()); SilentTimezoneChannel = new(new OfflineSilentTimezoneSystem()); }
        if (Runtime is not null)
        {
            if (fixture)
            {
                Accounts = DesktopAccountServices.CreateFixture(this);
                if (managedPaths is not null) LocalEnvironment = new(Accounts.ResolveLoginProxyAsync, Accounts.LocalEgress.ProbeRegionProxyAsync, Accounts.Admission);
            }
            Switching = new(SwitchStorage, Runtime, preparation: Accounts?.Accounts,
                environments: (IIdentityEnvironmentSource?)LocalEnvironment ?? new FileIdentityEnvironmentSource(Profiles),
                systemProxy: Accounts?.ProxyResolver, egressProbe: managedPaths is null ? null : (ILaunchEgressProbe?)LocalEnvironment ?? Accounts?.Admission);
            Maintenance = new(Runtime);
        }
    }
    public string Root { get; }
    public string AccountRoot { get; }
    public ManagedClonePaths? ManagedPaths { get; }
    public string ActiveHome { get; }
    public string? OriginalHome { get; private init; }
    public string? PackagedDesktopRoot { get; }
    public string? WebDesktopRoot { get; }
    public bool IsFixture { get; }
    public FileProfileStore Profiles { get; }
    public DesktopProxyServices ProxyServices { get; }
    public ISwitchStorage SwitchStorage { get; }
    public SwitchStoragePaths StoragePaths { get; }
    public ProfileMaintenance ProfileMaintenance { get; }
    public EnvironmentRebuild Rebuild { get; }
    public IRuntimeOperations? Runtime { get; }
    public SwitchService? Switching { get; private set; }
    public DesktopAccountServices? Accounts { get; private set; }
    public LocalEnvironmentService? LocalEnvironment { get; internal set; }
    public MaintenanceService? Maintenance { get; }
    public CodexInstallService? CodexInstaller { get; private set; }
    public SilentTimezoneChannelService? SilentTimezoneChannel { get; private set; }
    public OfflineRuntimeOperations? OfflineRuntime => Runtime as OfflineRuntimeOperations;
    public WindowsDesktopLaunchSystem? NativeLaunch { get; private init; }
    public SystemTimezoneService? NativeTimezone { get; private init; }
    public CodexProcessService? NativeProcesses { get; private init; }
    public OwnedProcessLifecycleService? Ownership { get; private init; }
    public MaintenanceService? OriginalMaintenance { get; private init; }
    private Func<EnvironmentRebuild>? OriginalRebuildFactory { get; init; }
    public bool HasOriginalRebuild => OriginalRebuildFactory is not null;
    public EnvironmentRebuild? OriginalRebuild => OriginalRebuildFactory?.Invoke();
    public static string DefaultDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChunsonSwitch");
    // CLI may be hosted by dotnet.exe; protection must target this application,
    // never the shared .NET runtime used by unrelated programs.
    public static string ApplicationExecutable => Path.Combine(AppContext.BaseDirectory, "ChunsonSwitch.exe");
    public static DesktopWorkspace CreateManagedFixture(string root, SwitchStoragePaths? originalPaths = null)
    {
        var paths = new ManagedClonePaths(root);
        return new(root, paths.CodexHome, paths.DesktopRoot, null, fixture: true, managedPaths: paths)
        {
            OriginalHome = originalPaths?.ActiveHome,
            OriginalMaintenance = originalPaths is null ? null : new(new OfflineRuntimeOperations()),
            OriginalRebuildFactory = originalPaths is null ? null : () => new(originalPaths)
        };
    }
    public static DesktopWorkspace CreateNormal(SwitchStoragePaths originalPaths)
    {
        var paths = new ManagedClonePaths(originalPaths.Root);
        var processSystem = new WindowsProcessSystem();
        var clock = new SystemWaitClock();
        var commands = new WindowsCommandRunner();
        var ownedStore = new JsonOwnedProcessStore(Path.Combine(paths.DataRoot, "managed-owned-processes.json"));
        var cloneScope = new ManagedCloneProcessScope(processSystem, ownedStore, paths.DesktopRoot);
        var processes = new CodexProcessService(processSystem, clock, cloneScope);
        // System-level direct blocking is optional, never a prerequisite for normal launch.
        var cloneOptions = new ManagedCloneLaunchOptions(paths.CodexHome, paths.DesktopRoot, cloneScope,
            RuntimeDiagnosticPath: Path.Combine(paths.DataRoot, "last-runtime-inspection.json"));
        var launchSystem = new WindowsDesktopLaunchSystem(paths.LaunchStateRoot, processSystem, commands, clock, cloneOptions);
        var launch = new DesktopLaunchService(launchSystem, clock);
        var timezone = new SystemTimezoneService(new WindowsTimezoneSystem(commands), new JsonTimezoneRestoreStore(Path.Combine(paths.AccountRoot, "tz-restore.json")));
        var ownership = new OwnedProcessLifecycleService(processSystem, clock, ownedStore, timezone, launchSystem, cloneScope: cloneScope);
        var originalLaunchSystem = new WindowsDesktopLaunchSystem(Path.Combine(paths.AccountRoot, "original-maintenance"), processSystem, commands, clock);
        var originalRuntime = new WindowsRuntimeOperations(new CodexProcessService(processSystem, clock, cloneScope, excludeManagedClone: true), new DesktopLaunchService(originalLaunchSystem, clock), originalLaunchSystem, timezone);
        var workspace = new DesktopWorkspace(paths.DataRoot, paths.CodexHome, paths.DesktopRoot, null, runtime: new WindowsRuntimeOperations(processes, launch, launchSystem, timezone, ownership), managedPaths: paths)
        { NativeLaunch = launchSystem, NativeTimezone = timezone, NativeProcesses = processes, Ownership = ownership, OriginalHome = originalPaths.ActiveHome,
          OriginalMaintenance = new(originalRuntime), OriginalRebuildFactory = () => new(new(paths.AccountRoot, originalPaths.ActiveHome, originalPaths.PackagedDesktopRoot, originalPaths.WebDesktopRoot), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) };
        workspace.Accounts = DesktopAccountServices.CreateNormal(workspace);
        workspace.CodexInstaller = new(new WindowsCodexInstallSystem(commands, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        workspace.SilentTimezoneChannel = new(new WindowsSilentTimezoneSystem(commands, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.UserDomainName + "\\" + Environment.UserName));
        workspace.LocalEnvironment = new(workspace.Accounts.ResolveLoginProxyAsync, workspace.Accounts.LocalEgress.ProbeRegionProxyAsync, workspace.Accounts.Admission);
        workspace.Switching = new(workspace.SwitchStorage, workspace.Runtime!, workspace.Accounts.Accounts, workspace.LocalEnvironment, workspace.Accounts.ProxyResolver, workspace.LocalEnvironment);
        return workspace;
    }
    private static async Task<SwitchStoragePaths> ResolveNormalPathsAsync()
    {
        var commands = new WindowsCommandRunner();
        var install = new WindowsCodexInstallSystem(commands, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var state = await install.ReadStateAsync(CancellationToken.None);
        var original = WindowsDataPaths.Resolve(WindowsPathInputs.Capture(state.PackageFamily.Length > 0 ? state.PackageFamily : null), new WindowsPathProbe());
        return original with { Root = DefaultDataRoot };
    }
    public static async Task<DesktopWorkspace?> FromArgumentsAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return CreateNormal(await ResolveNormalPathsAsync());
        if (args.Count == 1 && args[0] == "--preview") return null;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count || args[index] is not ("--fixture-root" or "--data-root" or "--active-home" or "--desktop-root" or "--packaged-desktop-root" or "--web-desktop-root") || !options.TryAdd(args[index], args[index + 1]))
                throw new InvalidOperationException("参数应为 --fixture-root/--data-root <目录> --active-home <目录> --desktop-root <目录>。");
        }
        var fixture = options.TryGetValue("--fixture-root", out var root);
        if (fixture && options.ContainsKey("--data-root")) throw new InvalidOperationException("只可指定一种工作区模式。");
        if (!fixture && !options.TryGetValue("--data-root", out root))
            throw new InvalidOperationException("显式工作区必须指定根目录、活动目录与桌面数据目录。");
        options.TryGetValue("--active-home", out var home);
        var packaged = options.GetValueOrDefault("--packaged-desktop-root") ?? options.GetValueOrDefault("--desktop-root");
        var web = options.GetValueOrDefault("--web-desktop-root");
        if (fixture)
        {
            if (home is null) return CreateManagedFixture(root!);
            return new(root!, home, packaged, web, true);
        }
        var normal = await ResolveNormalPathsAsync();
        if (home is not null || packaged is not null || web is not null)
            throw new InvalidOperationException("正常模式只允许指定账号库根目录；活动目录由唯一受管分身确定。显式活动目录仅用于隔离验证。");
        return CreateNormal(normal with { Root = root! });
    }
}

internal sealed class OfflineBrowserProcessSystem : IProcessSystem
{
    public IReadOnlyList<ProcessIdentity> Snapshot() => [];
    public bool TryStop(ProcessIdentity process, bool force) => false;
    public bool HasDesktopWindow(IReadOnlyList<ProcessIdentity> desktopProcesses) => false;
}
