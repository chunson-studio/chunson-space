using System.Text.Json.Nodes;

namespace FAswitch.Core;

public sealed class FileProfileStore
{
    private readonly IFileStorage files;
    public string Root { get; }
    public FileProfileStore(string root, IFileStorage? files = null)
    {
        Root = Path.GetFullPath(root);
        this.files = files ?? new PhysicalFileStorage();
    }
    public string ProfileDirectory(IdentityRef identity) => Path.Combine(Root, identity.Kind == IdentityKind.Account ? "profiles" : "providers", ProfileRules.Name(identity.Name));
    public IReadOnlyList<AccountProfile> ListAccounts() => files.Directories(Path.Combine(Root, "profiles"))
        .Select(path => new AccountProfile(Path.GetFileName(path), files.FileExists(Path.Combine(path, "auth.json")), ReadEnvironment(Path.GetFileName(path)), AreTestsFrozen(Path.GetFileName(path)))).ToArray();
    public bool AreTestsFrozen(string name)
    {
        var path = Path.Combine(ProfileDirectory(new(IdentityKind.Account, name)), "test-settings.json");
        if (!files.FileExists(path)) return false;
        var value = JsonFiles.Read(files, path);
        if (value["frozen"] is System.Text.Json.Nodes.JsonValue flag && flag.TryGetValue<bool>(out var frozen)) return frozen;
        throw new StorageException(StorageError.Corrupt, path, "账号冻结状态无法读取，未允许测试。请检查账号资料。");
    }
    public void SetTestsFrozen(string name, bool frozen)
    {
        var directory = ProfileDirectory(new(IdentityKind.Account, name));
        if (!files.DirectoryExists(directory)) throw new StorageException(StorageError.Missing, directory, "账号档案不存在。");
        JsonFiles.Write(files, Path.Combine(directory, "test-settings.json"), new() { ["frozen"] = frozen });
    }
    public IReadOnlyList<ProviderProfile> ListProviders() => files.Directories(Path.Combine(Root, "providers")).Select(path => ReadProvider(Path.GetFileName(path))).ToArray();
    public EnvironmentSettings ReadEnvironment(string name)
    {
        var value = JsonFiles.Read(files, EnvironmentPath(name), true);
        return new(JsonFiles.Text(value, "proxy"), JsonFiles.Text(value, "timezone"), JsonFiles.Text(value, "locale"), JsonFiles.Boolean(value, "locale_auto"), JsonFiles.Boolean(value, "follow_system_proxy"));
    }
    public EnvironmentSettings SaveEnvironment(string name, EnvironmentSettings environment)
    {
        environment = ProfileRules.Environment(environment);
        var path = EnvironmentPath(name);
        var value = JsonFiles.Read(files, path, true);
        value["proxy"] = environment.Proxy;
        value["timezone"] = environment.Timezone;
        value["locale"] = environment.Locale;
        value["locale_auto"] = environment.LocaleAuto;
        value["follow_system_proxy"] = environment.FollowSystemProxy;
        JsonFiles.Write(files, path, value);
        return environment;
    }
    public EnvironmentSettings UpdateEnvironment(string name, string? proxy = null, string? timezone = null, string? locale = null, bool? followSystemProxy = null)
    {
        var current = ReadEnvironment(name);
        return SaveEnvironment(name, current with
        {
            Proxy = proxy ?? current.Proxy, Timezone = timezone ?? current.Timezone,
            Locale = locale ?? current.Locale, LocaleAuto = locale is null && current.LocaleAuto,
            FollowSystemProxy = followSystemProxy ?? (proxy is null && current.FollowSystemProxy)
        });
    }
    public ExitObservation? ReadExitObservation(IdentityRef identity)
    {
        var value = JsonFiles.Read(files, Path.Combine(ProfileDirectory(identity), "exit-observation.json"), true);
        var ip = JsonFiles.Text(value, "ip");
        if (!System.Net.IPAddress.TryParse(ip, out _) || !DateTimeOffset.TryParse(JsonFiles.Text(value, "checkedAt"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var checkedAt)) return null;
        return new(JsonFiles.Text(value, "proxy"), JsonFiles.Boolean(value, "followSystemProxy"), ip, JsonFiles.Text(value, "timezone"), JsonFiles.Text(value, "location"), checkedAt);
    }
    public void SaveExitObservation(IdentityRef identity, ExitObservation observation)
    {
        if (!System.Net.IPAddress.TryParse(observation.Ip, out _)) throw new StorageException(StorageError.InvalidValue, "", "出口检测结果必须包含有效 IP。");
        JsonFiles.Write(files, Path.Combine(ProfileDirectory(identity), "exit-observation.json"), new()
        {
            ["proxy"] = observation.Proxy, ["followSystemProxy"] = observation.FollowSystemProxy,
            ["ip"] = observation.Ip, ["timezone"] = observation.Timezone, ["location"] = observation.Location,
            ["checkedAt"] = observation.CheckedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        });
    }
    public void ClearExitObservation(IdentityRef identity) => files.DeleteFile(Path.Combine(ProfileDirectory(identity), "exit-observation.json"));
    public AppSettings ReadSettings()
    {
        var value = ReadSettingsDocument();
        var theme = JsonFiles.Text(value, "workspaceTheme");
        return new(JsonFiles.Boolean(value, "sessionIsolation", true), JsonFiles.Boolean(value, "autoSystemTimezone", true), JsonFiles.Boolean(value, "quitConfirm", true), theme.Length == 0 ? "light" : theme);
    }
    public JsonObject ReadSettingsDocument() => JsonFiles.Read(files, Path.Combine(Root, "settings.json"), true);
    public void SaveSettings(AppSettings settings)
    {
        if (settings.WorkspaceTheme is not ("light" or "dark")) throw new StorageException(StorageError.InvalidValue, "", "外观只能选择 light 或 dark。");
        var value = ReadSettingsDocument();
        value["sessionIsolation"] = settings.SessionIsolation;
        value["autoSystemTimezone"] = settings.AutoSystemTimezone;
        value["quitConfirm"] = settings.QuitConfirm;
        value["workspaceTheme"] = settings.WorkspaceTheme;
        JsonFiles.Write(files, Path.Combine(Root, "settings.json"), value);
    }
    public JsonObject ReadAccountCredentials(string name) => JsonFiles.Read(files, Path.Combine(ProfileDirectory(new(IdentityKind.Account, name)), "auth.json"));
    public void SaveAccount(string name, JsonObject credentials, bool overwrite = false, bool developer = false)
    {
        var path = ProfileDirectory(new(IdentityKind.Account, name));
        if (files.DirectoryExists(path) && !overwrite) throw new StorageException(StorageError.Conflict, path, "账号档案已存在。");
        JsonFiles.Write(files, Path.Combine(path, "auth.json"), credentials);
    }
    public void CreateBoundAccount(string name, EnvironmentSettings environment, bool developer = false)
    {
        name = ProfileRules.Name(name);
        environment = ProfileRules.Environment(environment);
        if (!environment.FollowSystemProxy && (environment.Proxy.Length == 0 || environment.Proxy.StartsWith("socks5://", StringComparison.Ordinal)))
            throw new StorageException(StorageError.InvalidValue, "", "新档案必须先绑定 HTTP/HTTPS 代理。");
        var path = ProfileDirectory(new(IdentityKind.Account, name));
        if (files.DirectoryExists(path) || files.DirectoryExists(Path.GetDirectoryName(EnvironmentPath(name))!))
            throw new StorageException(StorageError.Conflict, path, "名称已被占用。");
        SaveEnvironment(name, environment);
        files.CreateDirectory(path);
    }
    public ProviderProfile ReadProvider(string name)
    {
        name = ProfileRules.Name(name);
        var data = ProviderDocument(name);
        return new(name, JsonFiles.Text(data, "base_url").Trim(), JsonFiles.Text(data, "model").Trim(), JsonFiles.Text(data, "created"), JsonFiles.Text(data, "api_key").Length > 0);
    }
    public ProviderSaveResult SaveProvider(string name, string baseUrl, string apiKey, string model = "", bool overwrite = false, IdentityRef? active = null)
    {
        name = ProfileRules.Name(name);
        baseUrl = ProfileRules.BaseUrl(baseUrl);
        var directory = ProfileDirectory(new(IdentityKind.Provider, name));
        var exists = files.DirectoryExists(directory);
        if (exists && !overwrite) throw new StorageException(StorageError.Conflict, directory, "供应商已存在。");
        if (exists && active == new IdentityRef(IdentityKind.Provider, name)) throw new StorageException(StorageError.Conflict, directory, "供应商正在使用中，请先切换再覆盖。");
        var key = apiKey.Trim();
        if (key.Length == 0) throw new StorageException(StorageError.InvalidValue, directory, "API Key 不能为空。");
        var data = new JsonObject { ["base_url"] = baseUrl, ["api_key"] = key, ["model"] = model.Trim(), ["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) };
        var trash = Path.Combine(Root, "xy_clean", "CodexX-01", "Boss");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var staging = Path.Combine(trash, "provider-new-" + name + "-" + stamp);
        var archive = Path.Combine(trash, "provider-" + name + "-" + stamp);
        var movedOld = false;
        try
        {
            WriteProviderFiles(staging, data);
            if (exists) { files.MoveDirectory(directory, archive); movedOld = true; }
            files.CreateDirectory(Path.GetDirectoryName(directory)!);
            files.MoveDirectory(staging, directory);
        }
        catch (Exception original) when (original is StorageException or IOException or UnauthorizedAccessException)
        {
            if (movedOld)
            {
                try { if (!files.DirectoryExists(directory)) files.MoveDirectory(archive, directory); }
                catch (Exception recovery) when (recovery is StorageException or IOException or UnauthorizedAccessException)
                { throw new StorageException(StorageError.Io, archive, "供应商覆盖失败，旧目录留在恢复位置：" + archive, recovery); }
            }
            throw new StorageException(StorageError.Io, movedOld ? archive : staging, "供应商保存失败，原目录与暂存恢复材料已保留。", original);
        }
        return new(ReadProvider(name), exists ? archive : null);
    }
    public ProviderUpdateResult UpdateProvider(string name, string baseUrl, string apiKey = "", string model = "", string? activeHome = null)
    {
        name = ProfileRules.Name(name);
        baseUrl = ProfileRules.BaseUrl(baseUrl);
        var identity = new IdentityRef(IdentityKind.Provider, name);
        var directory = ProfileDirectory(identity);
        var data = ProviderDocument(name);
        var key = apiKey.Trim();
        if (key.Length == 0) key = JsonFiles.Text(data, "api_key");
        if (key.Length == 0) throw new StorageException(StorageError.InvalidValue, directory, "API Key 不能为空。");
        var active = false;
        if (activeHome is not null)
        {
            var checkedStorage = new FileSwitchStorage(new(Root, activeHome), files);
            activeHome = checkedStorage.Paths.ActiveHome;
            active = checkedStorage.ReadActiveIdentity() == identity;
        }
        var recovery = Path.Combine(Root, "xy_clean", "CodexX-01", "Boss", "provider-edit-" + name + "-" + Guid.NewGuid().ToString("N"));
        var records = new List<(string Target, string Saved, bool Present)>();
        foreach (var item in new[] { (Target: Path.Combine(directory, "provider.json"), Saved: "provider.json"), (Target: Path.Combine(directory, "auth.json"), Saved: "auth.json") })
            records.Add((item.Target, Path.Combine(recovery, item.Saved), files.FileExists(item.Target)));
        if (active)
        {
            records.Add((Path.Combine(activeHome!, "auth.json"), Path.Combine(recovery, "active-auth.json"), files.FileExists(Path.Combine(activeHome!, "auth.json"))));
            records.Add((Path.Combine(activeHome!, "config.toml"), Path.Combine(recovery, "active-config.toml"), files.FileExists(Path.Combine(activeHome!, "config.toml"))));
            records.Add((Path.Combine(Root, "providers", ".state.json"), Path.Combine(recovery, "active-provider-state.json"), files.FileExists(Path.Combine(Root, "providers", ".state.json"))));
        }
        // Preserve the small, exact set of files this provider edit owns before any edit.
        try
        {
            files.CreateDirectory(recovery);
            foreach (var record in records) if (record.Present) files.CopyAtomic(record.Target, record.Saved);
            JsonFiles.Write(files, Path.Combine(recovery, "_provider-edit.json"), new JsonObject { ["version"] = 1, ["name"] = name, ["active_home"] = active ? activeHome : null });
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException)
        { throw new StorageException(StorageError.Io, recovery, "供应商编辑前备份失败，文件未修改。", error); }
        data["base_url"] = baseUrl; data["api_key"] = key; data["model"] = model.Trim();
        try
        {
            WriteProviderFiles(directory, data);
            if (active)
            {
                new ProviderConfig(files, Root, activeHome!).Enter(name, baseUrl, model.Trim(), false);
                JsonFiles.Write(files, Path.Combine(activeHome!, "auth.json"), new JsonObject { ["auth_mode"] = "apikey", ["OPENAI_API_KEY"] = key });
            }
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException)
        {
            var failed = new List<string>();
            foreach (var record in records)
                try
                {
                    if (!record.Present) files.DeleteFile(record.Target);
                    else if (!files.FileExists(record.Target) || files.Hash(record.Target) != files.Hash(record.Saved)) files.CopyAtomic(record.Saved, record.Target);
                }
                catch (Exception restore) when (restore is StorageException or IOException or UnauthorizedAccessException) { failed.Add(record.Target); }
            throw new StorageException(StorageError.Io, recovery, failed.Count == 0 ? "供应商编辑失败，已恢复原文件；原副本仍保留。" : "供应商编辑与部分恢复失败，请保留恢复目录。", error);
        }
        return new(ReadProvider(name), active, recovery, active ? ["配置与凭据已保存；现有客户端可能需重启后生效。"] : []);
    }
    private void WriteProviderFiles(string directory, JsonObject data)
    {
        JsonFiles.Write(files, Path.Combine(directory, "auth.json"), new JsonObject { ["auth_mode"] = "apikey", ["OPENAI_API_KEY"] = JsonFiles.Text(data, "api_key") });
        JsonFiles.Write(files, Path.Combine(directory, "provider.json"), data);
    }
    public void Rename(IdentityRef identity, string newName)
    {
        newName = ProfileRules.Name(newName);
        var oldName = ProfileRules.Name(identity.Name);
        if (new IdentityRef(identity.Kind, oldName) == new IdentityRef(identity.Kind, newName)) throw new StorageException(StorageError.Conflict, "", "名称未变化。");
        var pairs = DirectoriesFor(identity).Select(source => (Source: source, Destination: Path.Combine(Path.GetDirectoryName(source)!, newName))).Where(pair => files.DirectoryExists(pair.Source)).ToArray();
        if (pairs.Length == 0 || !files.DirectoryExists(ProfileDirectory(identity))) throw new StorageException(StorageError.Missing, ProfileDirectory(identity), "档案不存在。");
        foreach (var pair in pairs) if (files.DirectoryExists(pair.Destination)) throw new StorageException(StorageError.Conflict, pair.Destination, "名称已被占用。");
        var browsers = new AccountBrowserProfiles(Root, files);
        var browser = identity.Kind == IdentityKind.Account ? browsers.Read(oldName) : null;
        MoveGroup(pairs);
        try { browsers.RenameBinding(oldName, newName, browser); }
        catch { MoveGroup(pairs.Reverse().Select(pair => (pair.Destination, pair.Source))); throw; }
    }
    public ArchiveResult Archive(IdentityRef identity, IdentityRef? active = null)
    {
        if (identity == active) throw new StorageException(StorageError.Conflict, "", "档案正在使用中，请先切换再归档。");
        var browser = identity.Kind == IdentityKind.Account ? new AccountBrowserProfiles(Root, files).Read(identity.Name) : null;
        var sources = DirectoriesFor(identity).Where(files.DirectoryExists).ToList();
        if (browser is not null && !sources.Any(source => browser.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) sources.Add(browser);
        if (!files.DirectoryExists(ProfileDirectory(identity))) throw new StorageException(StorageError.Missing, ProfileDirectory(identity), "档案不存在。");
        var trash = Path.Combine(Root, "xy_clean", "CodexX-01", "Boss");
        files.CreateDirectory(trash);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var pairs = sources.Select(source => (Source: source, Destination: Path.Combine(trash, $"{(identity.Kind == IdentityKind.Provider ? "provider-" : "")}{identity.Name}-{Path.GetFileName(Path.GetDirectoryName(source))}-{stamp}"))).ToArray();
        MoveGroup(pairs);
        return new(trash, pairs.Select(pair => pair.Destination).ToArray());
    }
    private void MoveGroup(IEnumerable<(string Source, string Destination)> pairs)
    {
        var moved = new List<(string Source, string Destination)>();
        try { foreach (var pair in pairs) { files.MoveDirectory(pair.Source, pair.Destination); moved.Add(pair); } }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var recoveryErrors = new List<string>();
            foreach (var pair in moved.AsEnumerable().Reverse())
                try { files.MoveDirectory(pair.Destination, pair.Source); }
                catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException) { recoveryErrors.Add(pair.Destination); }
            throw new StorageException(StorageError.Io, Root, recoveryErrors.Count == 0 ? "目录移动失败，已恢复原路径。" : "目录移动和恢复失败，请保留恢复材料：" + string.Join("、", recoveryErrors), error);
        }
    }
    private IEnumerable<string> DirectoriesFor(IdentityRef identity)
    {
        yield return ProfileDirectory(identity);
        if (identity.Kind != IdentityKind.Account) yield break;
        yield return Path.Combine(Root, "browser-envs", ProfileRules.Name(identity.Name));
        yield return Path.Combine(Root, "electron-envs", ProfileRules.Name(identity.Name));
    }
    private JsonObject ProviderDocument(string name) => JsonFiles.Read(files, Path.Combine(ProfileDirectory(new(IdentityKind.Provider, name)), "provider.json"));
    private string EnvironmentPath(string name) => Path.Combine(Root, "browser-envs", ProfileRules.Name(name), "env.json");
}
