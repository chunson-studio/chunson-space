using ChunsonSpace.Core;
using System.IO;
using ChunsonSpace.Accounts;

namespace ChunsonSpace.Desktop;

public enum ProfileEditMode { Create, Edit, Rename, Archive, SaveCurrent, Logout, Rebuild, RebuildOriginal }

public sealed partial class ProfileEditorViewModel : ObservableObject
{
    private string name;
    private string proxy;
    private string timezone;
    private string locale;
    private string error = "";
    private bool overwrite;
    private bool saving;
    private bool followSystemProxy;

    public ProfileEditorViewModel(ProfileEditMode mode, IdentityKind kind, IdentityItem? original, Func<ProfileEditorViewModel, Task> save, Action cancel)
    {
        if (kind != IdentityKind.Account) throw new ArgumentException("仅支持账号档案。", nameof(kind));
        Mode = mode; Kind = kind; Original = original;
        name = (mode == ProfileEditMode.Rename ? original?.Label : original?.Name) ?? "";
        proxy = original?.Proxy ?? "";
        followSystemProxy = original?.FollowSystemProxy ?? false;
        timezone = original?.Timezone ?? "";
        locale = original?.Locale ?? "zh-CN";
        SaveCommand = new(_ => CurrentOperation = SaveAsync(save), _ => !saving);
        CancelCommand = new(_ => cancel(), _ => !saving);
        RandomIpCommand = new(_ => CurrentOperation = PickRandomIpAsync(), _ => !saving && !FollowSystemProxy && PickRandomIp is not null);
    }
    public ProfileEditMode Mode { get; }
    public IdentityKind Kind { get; }
    public IdentityItem? Original { get; }
    public bool UseVerifiedEnvironment { get; init; }
    public bool ShowManualEnvironment => IsEnvironmentEditor && !UseVerifiedEnvironment;
    public string Name { get => name; set => Set(ref name, value); }
    public string Proxy { get => proxy; set => Set(ref proxy, value); }
    public bool FollowSystemProxy { get => followSystemProxy; set { if (!saving && Set(ref followSystemProxy, value)) { if (value) { selectedIp = null; Raise(nameof(SelectedIp)); } Raise(nameof(UsesDedicatedProxy)); Raise(nameof(ShowIpPicker)); Raise(nameof(NetworkModeDescription)); RandomIpCommand.Refresh(); } } }
    public bool UsesDedicatedProxy => !followSystemProxy;
    public bool ShowManualProxy => !UseVerifiedEnvironment;
    public string NetworkModeDescription => FollowSystemProxy
        ? "代理和时区跟随本机，支持 FlClash 仅开 TUN；保存后释放旧独立端口。"
        : SelectedIp is { } ip ? $"待更换：{ip.ExitIp} · 保存时由 IPinfo 核对时区。点击保存后生效，释放不再使用的旧端口。"
        : Original?.FollowSystemProxy == true || Proxy.Length == 0
            ? "保存时从已检测达标的 IP 中选取线路，自动分配独立端口并匹配时区。"
            : "使用独立 IP 和对应时区，当前端口：" + Proxy;
    public string Timezone { get => timezone; set => Set(ref timezone, value); }
    public string Locale { get => locale; set => Set(ref locale, value); }
    public string Error => error;
    public string Details { get; set; } = "";
    public bool HasDetails => Details.Length > 0;
    public bool Overwrite { get => overwrite; set => Set(ref overwrite, value); }
    public bool ShowOverwrite => Mode == ProfileEditMode.SaveCurrent;
    public bool HasError => error.Length > 0;
    public bool IsEnvironmentEditor => Kind == IdentityKind.Account && Mode is ProfileEditMode.Create or ProfileEditMode.Edit;
    public bool CanEditName => Mode is ProfileEditMode.Create or ProfileEditMode.Rename or ProfileEditMode.SaveCurrent;
    public bool ShowName => Mode is ProfileEditMode.Create or ProfileEditMode.Edit or ProfileEditMode.Rename or ProfileEditMode.SaveCurrent;
    public bool IsArchive => Mode == ProfileEditMode.Archive;
    public string Title => Mode switch { ProfileEditMode.SaveCurrent => "保存当前账号档案", ProfileEditMode.Logout => "退出受管分身登录", ProfileEditMode.Rebuild => "重建分身", ProfileEditMode.RebuildOriginal => "清理 Codex 本体", ProfileEditMode.Rename => "重命名档案", ProfileEditMode.Archive => "删除账号", ProfileEditMode.Edit => "账号配置", _ => "新建账号档案" };
    public string Description => Mode switch { ProfileEditMode.SaveCurrent => "先关闭受管分身，再保存当前授权资料。覆盖前保留旧档案。", ProfileEditMode.Logout => "先关闭受管分身，再移除其当前登录与自定义接口配置。账号库授权资料保留。", ProfileEditMode.Rebuild => "先关闭唯一受管分身，保存其授权资料，再清空重建分身运行目录。账号库和 Codex 本体独立保留。", ProfileEditMode.RebuildOriginal => "此操作影响独立的 Codex 本体：会关闭 Codex 本体进程，将下列登录、历史会话和桌面状态移入可恢复归档。本体配置、插件、技能及项目文件保留。仅点击下方确认按钮后执行。", ProfileEditMode.Archive => $"永久删除“{Original?.Label}”的本地凭据及关联环境，不保留备份，无法恢复。仅目标为当前分身时关闭并清理该分身；OpenAI 账号、本体和项目保留。", ProfileEditMode.Rename => "只修改显示名，账号资料和运行身份保持原位。", _ => UseVerifiedEnvironment ? "勾选跟随本机网络与时区；取消勾选使用独立 IP 与对应时区。保存时会重开正在使用的档案浏览器，登录资料保留。Codex 本体和分身继续沿用本机环境。" : "选择独立端口或跟随 FlClash 当前网络；不会修改 Windows 时区。" };
    public string SaveLabel => saving && IsArchive ? "正在删除…" : Mode switch { ProfileEditMode.Archive => "确认删除", ProfileEditMode.Logout => "关闭并退出分身登录", ProfileEditMode.Rebuild => "清空重建受管分身", ProfileEditMode.RebuildOriginal => "确认关闭并清理 Codex 本体", _ => "保存" };
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }
    private async Task SaveAsync(Func<ProfileEditorViewModel, Task> save)
    {
        if (saving) return;
        saving = true; RefreshEditing(); error = ""; Raise(nameof(Error)); Raise(nameof(HasError));
        Raise(nameof(SaveLabel));
        try { await save(this); }
        catch (Exception exception) when (exception is AccountException or StorageException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { error = IsArchive && exception is not InvalidOperationException ? "删除未完成，请检查文件占用或权限后重试；已删除的资料无法恢复。" : exception.Message; Raise(nameof(Error)); Raise(nameof(HasError)); }
        finally { saving = false; RefreshEditing(); Raise(nameof(SaveLabel)); }
    }
}
