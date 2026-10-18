using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

public sealed record ImportedLogin(string Profile, bool Created, IReadOnlyList<string> Warnings);

public sealed class AccountRepository
{
    private readonly IFileStorage files;
    private readonly IPendingAccountGuard pending;
    private readonly Func<string?>? systemProxyProvider;
    public FileProfileStore Profiles { get; }
    public string Root => Profiles.Root;
    public string? ActiveHome { get; }
    public bool IsPending => pending.IsPending;
    public AccountRepository(string root, string? activeHome = null, IPendingAccountGuard? pending = null, IFileStorage? files = null, Func<string?>? systemProxyProvider = null)
    { this.files = files ?? new PhysicalFileStorage(); this.pending = pending ?? new NoPendingAccountGuard(); this.systemProxyProvider = systemProxyProvider; Profiles = new(root, this.files); ActiveHome = activeHome is null ? null : Path.GetFullPath(activeHome); }
    public string AccountPath(string name) => Profiles.ProfileDirectory(new(IdentityKind.Account, name));
    public JsonObject ReadAuth(string name) => Read(Path.Combine(AccountPath(name), "auth.json"));
    public JsonObject ReadProvider(string name) => Read(Path.Combine(Profiles.ProfileDirectory(new(IdentityKind.Provider, name)), "provider.json"));
    public bool Exists(string name) => files.DirectoryExists(AccountPath(name));
    public void RequireTestsAllowed(string name)
    {
        if (Profiles.AreTestsFrozen(name)) throw new AccountException(AccountError.Busy, "账号已冻结静置，未发送测试请求；解冻后才能测试。");
    }
    public string Proxy(string name)
    {
        var environment = Profiles.ReadEnvironment(name);
        return environment.FollowSystemProxy ? HttpAccountTransport.ResolveSystemHttpProxy(systemProxyProvider) : HttpAccountTransport.RequireHttpProxy(environment.Proxy);
    }
    public void WriteProfileAuth(string name, JsonObject auth) => Write(Path.Combine(AccountPath(name), "auth.json"), auth);
    public void WriteQuotaCache(string name, JsonObject quota) => Write(Path.Combine(AccountPath(name), "quota.json"), quota);
    public JsonObject? ReadQuotaCache(string name)
    {
        var path = Path.Combine(AccountPath(name), "quota.json");
        return files.FileExists(path) ? Read(path) : null;
    }
    public CredentialSaveResult SaveLogin(string name, JsonObject auth)
    {
        name = ProfileRules.Name(name);
        if (!Exists(name)) throw new AccountException(AccountError.Storage, "请先创建并绑定代理的账号档案。");
        var newId = AccountJson.Text(auth["tokens"] as JsonObject, "account_id");
        if (newId.Length == 0) throw new AccountException(AccountError.CorruptResponse, "登录凭据未包含账号身份，未保存。");
        var path = Path.Combine(AccountPath(name), "auth.json");
        if (files.FileExists(path))
        {
            var old = Read(path);
            var oldId = AccountJson.Text(old["tokens"] as JsonObject, "account_id");
            var oldEmail = JwtClaims.Email(AccountJson.Text(old["tokens"] as JsonObject, "id_token"));
            var newEmail = JwtClaims.Email(AccountJson.Text(auth["tokens"] as JsonObject, "id_token"));
            if (oldId.Length > 0 && oldId != newId || oldId.Length == 0 && oldEmail.Length > 0 && !oldEmail.Equals(newEmail, StringComparison.OrdinalIgnoreCase))
                throw new AccountException(AccountError.IdentityMismatch, "登录的是另一个账号，未覆盖既有档案，请使用其他档案名称。");
        }
        Write(path, auth);
        var warnings = new List<string>();
        var synced = false;
        if (ActiveHome is not null && files.FileExists(Path.Combine(ActiveHome, "auth.json")))
        {
            try
            {
                var live = Read(Path.Combine(ActiveHome, "auth.json"));
                if (AccountJson.Text(live["tokens"] as JsonObject, "account_id") == newId)
                {
                    if (pending.IsPending) warnings.Add("档案已保存；桌面启动待确认，活动凭据未同步。");
                    else { Write(Path.Combine(ActiveHome, "auth.json"), auth); synced = true; }
                }
            }
            catch (AccountException) { warnings.Add("档案已保存；活动凭据同步失败。"); }
        }
        return new(true, synced, warnings);
    }
    public ImportedLogin ImportLocalLogin(string originalHome)
    {
        var auth = Read(Path.Combine(Path.GetFullPath(originalHome), "auth.json"));
        var mode = AccountJson.Text(auth, "auth_mode");
        if (mode.Length > 0 && mode != "chatgpt")
            throw new AccountException(AccountError.MissingCredentials, "本机当前不是 ChatGPT 账号授权，无法导入账号测试。");
        return ImportLogin(auth, followSystemProxy: true, preserveExistingEnvironment: true);
    }
    public ImportedLogin ImportLogin(JsonObject auth, string? configuredProxy = null, bool followSystemProxy = false, bool preserveExistingEnvironment = false)
    {
        var accountId = AccountJson.Text(auth["tokens"] as JsonObject, "account_id");
        if (accountId.Length == 0 || AccountJson.Text(auth["tokens"] as JsonObject, "access_token").Length == 0)
            throw new AccountException(AccountError.CorruptResponse, "授权凭据缺少账号身份，未录入。");
        var warnings = new List<string>();
        var matches = new List<string>();
        var proxy = followSystemProxy ? "" : HttpAccountTransport.RequireHttpProxy(configuredProxy);
        try
        {
            foreach (var directory in files.Directories(Path.Combine(Root, "profiles")))
            {
                var candidate = Path.Combine(directory, "auth.json");
                if (!files.FileExists(candidate)) continue;
                JsonObject existing;
                try { existing = Read(candidate); }
                catch (AccountException error) when (error.Code == AccountError.CorruptResponse) { continue; }
                if (AccountJson.Text(existing["tokens"] as JsonObject, "account_id") == accountId) matches.Add(Path.GetFileName(directory));
            }
            if (matches.Count == 1)
            {
                UpdateImportedLogin(matches[0], auth, proxy, followSystemProxy, warnings, preserveExistingEnvironment);
                warnings.Add(preserveExistingEnvironment ? "已更新同账号授权，原有 IP、时区和浏览器配置保持不变。" : "已更新同账号凭据与本次授权所选出口；其余环境设置保持不变。");
                return new(matches[0], false, warnings); // Deliberately bypass ActiveHome synchronization.
            }
            if (matches.Count > 1 && preserveExistingEnvironment)
                throw new AccountException(AccountError.IdentityMismatch, "本机账号对应多个已有档案，未覆盖授权。请先整理重复档案后重试。");
            if (matches.Count > 1) warnings.Add("该账号已有多个档案；本次单独录入，未猜测或覆盖原档案。");
            var email = JwtClaims.Email(AccountJson.Text(auth["tokens"] as JsonObject, "id_token"));
            var cleaned = new string(email.Select(c => c < 32 || "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
            if (cleaned.Length > 100) cleaned = cleaned[..100].TrimEnd('.', ' ');
            string stem;
            try { stem = ProfileRules.Name(cleaned); }
            catch (StorageException) { stem = "账号"; }
            var name = stem;
            for (var suffix = 2; Taken(name); suffix++) name = stem + " (" + suffix + ")";
            var stage = Path.Combine(Root, "xy_clean", "CodexX-01", "Boss", "account-import-" + Guid.NewGuid().ToString("N"));
            var stagedProfile = Path.Combine(stage, "profile");
            var stagedEnvironment = Path.Combine(stage, "environment");
            var destination = AccountPath(name);
            var environmentPath = Path.Combine(Root, "browser-envs", name);
            var profileCommitted = false;
            var environmentCommitted = false;
            try
            {
                files.CreateDirectory(stagedProfile);
                Write(Path.Combine(stagedProfile, "auth.json"), auth);
                files.CreateDirectory(stagedEnvironment);
                Write(Path.Combine(stagedEnvironment, "env.json"), new JsonObject { ["proxy"] = proxy, ["follow_system_proxy"] = followSystemProxy, ["timezone"] = "", ["locale"] = "", ["locale_auto"] = false });
                if (Taken(name)) throw new AccountException(AccountError.Storage, "录入名称已被占用，原目录未覆盖；请重试。");
                files.CreateDirectory(Path.Combine(Root, "profiles"));
                files.MoveDirectory(stagedProfile, destination); profileCommitted = true;
                files.CreateDirectory(Path.Combine(Root, "browser-envs"));
                files.MoveDirectory(stagedEnvironment, environmentPath); environmentCommitted = true;
            }
            catch (Exception error) when (error is AccountException or IOException or UnauthorizedAccessException or StorageException)
            {
                var restored = true;
                if (environmentCommitted) try { files.MoveDirectory(environmentPath, stagedEnvironment); } catch (Exception restore) when (restore is IOException or UnauthorizedAccessException or StorageException) { restored = false; }
                if (profileCommitted) try { files.MoveDirectory(destination, stagedProfile); } catch (Exception restore) when (restore is IOException or UnauthorizedAccessException or StorageException) { restored = false; }
                throw new AccountException(AccountError.Storage, restored ? "授权已取得，但账号录入失败；未留下空账号，恢复材料保留。" : "账号录入失败且部分恢复未完成，请保留恢复材料并检查档案目录。");
            }
            try { files.DeleteDirectory(stage, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException) { warnings.Add("账号已录入；本次临时保存材料清理未完成。"); }
            return new(name, true, warnings);
        }
        catch (AccountException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException)
        { throw new AccountException(AccountError.Storage, "无法读取或保存账号目录，未确认录入成功。"); }

        bool Taken(string name) => new[] { "profiles", "browser-envs", "electron-envs" }.Any(folder =>
            files.DirectoryExists(Path.Combine(Root, folder, name)) || files.FileExists(Path.Combine(Root, folder, name)));
    }
    private void UpdateImportedLogin(string name, JsonObject auth, string proxy, bool followSystemProxy, List<string> warnings, bool preserveEnvironment)
    {
        var authPath = Path.Combine(AccountPath(name), "auth.json");
        var environmentPath = Path.Combine(Root, "browser-envs", name, "env.json");
        var oldAuth = files.Read(authPath);
        var oldEnvironment = files.FileExists(environmentPath) ? files.Read(environmentPath) : null;
        var environment = oldEnvironment is null ? new JsonObject() : AccountJson.Parse(oldEnvironment);
        environment["proxy"] = proxy;
        environment["follow_system_proxy"] = followSystemProxy;
        var recovery = Path.Combine(Root, "xy_clean", "CodexX-01", "Boss", "account-update-" + Guid.NewGuid().ToString("N"));
        files.CreateDirectory(recovery);
        files.WriteAtomic(Path.Combine(recovery, "auth.json"), oldAuth);
        if (oldEnvironment is not null) files.WriteAtomic(Path.Combine(recovery, "env.json"), oldEnvironment);
        try
        {
            if (!preserveEnvironment) Write(environmentPath, environment);
            Write(authPath, auth);
        }
        catch (Exception error) when (error is AccountException or IOException or UnauthorizedAccessException or StorageException)
        {
            bool restored = true;
            try { files.WriteAtomic(authPath, oldAuth); } catch (Exception restore) when (restore is IOException or UnauthorizedAccessException or StorageException) { restored = false; }
            try { if (oldEnvironment is null) files.DeleteFile(environmentPath); else files.WriteAtomic(environmentPath, oldEnvironment); }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException or StorageException) { restored = false; }
            throw new AccountException(AccountError.Storage, (restored ? "账号更新失败，原授权与出口已恢复；恢复材料：" : "账号更新失败且部分恢复未确认，请先检查恢复材料：") + recovery);
        }
        try { files.DeleteDirectory(recovery, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException) { warnings.Add("账号更新已完成；旧授权恢复材料尚未清理：" + recovery); }
    }
    private JsonObject Read(string path)
    {
        try
        {
            if (!files.FileExists(path)) throw new AccountException(AccountError.MissingCredentials, "档案缺少凭据，请先登录。");
            return AccountJson.Parse(files.Read(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException) { throw new AccountException(AccountError.Storage, "无法读取档案文件，请检查权限或文件占用。"); }
    }
    private void Write(string path, JsonObject value)
    {
        try { files.WriteAtomic(path, Encoding.UTF8.GetBytes(value.ToJsonString())); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or StorageException) { throw new AccountException(AccountError.Storage, "无法原子保存档案文件，请检查权限或文件占用。"); }
    }
}

public static class JwtClaims
{
    public static JsonObject Read(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return new();
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            return JsonNode.Parse(Convert.FromBase64String(payload)) as JsonObject ?? new();
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException) { return new(); }
    }
    public static string Email(string token)
    { var claims = Read(token); var email = AccountJson.Text(claims, "email"); return email.Length > 0 ? email : AccountJson.Text(claims["https://api.openai.com/profile"] as JsonObject, "email"); }
    public static JsonObject ToAuth(JsonObject response, DateTimeOffset now)
    {
        var idToken = AccountJson.Text(response, "id_token");
        var access = AccountJson.Text(response, "access_token");
        var accountId = AccountJson.Text(Read(idToken)["https://api.openai.com/auth"] as JsonObject, "chatgpt_account_id");
        if (idToken.Length == 0 || access.Length == 0 || accountId.Length == 0) throw new AccountException(AccountError.CorruptResponse, "上游没有返回完整的登录凭据及身份。");
        return new JsonObject { ["auth_mode"] = "chatgpt", ["OPENAI_API_KEY"] = null,
            ["tokens"] = new JsonObject { ["id_token"] = idToken, ["access_token"] = access, ["refresh_token"] = AccountJson.Text(response, "refresh_token"), ["account_id"] = accountId },
            ["last_refresh"] = now.ToString("O") };
    }
}
