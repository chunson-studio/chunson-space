using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Core;

var count = 0;
var checkRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "核心-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(checkRoot);
void Assert(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
    count++;
}
void Expect(StorageError code, Action action, string label)
{
    try { action(); throw new InvalidOperationException(label + ": expected failure"); }
    catch (StorageException error) { Assert(error.Code == code, label); }
}
void Write(string relative, string content)
{
    var path = Path.Combine(checkRoot, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, new UTF8Encoding(false));
}
string Read(string relative) => File.ReadAllText(Path.Combine(checkRoot, relative));
try
{
    var files = new PhysicalFileStorage();
    var store = new FileProfileStore(checkRoot, files);
    Assert(store.ListAccounts().Count == 0 && store.ListProviders().Count == 0, "missing stores are empty");
    Assert(store.ReadSettings() == new AppSettings(), "legacy settings defaults");
    var routeStore = new FileProfileStore(Path.Combine(checkRoot, "route-mode-fixture"));
    Assert(!routeStore.ReadEnvironment("legacy").FollowSystemProxy, "legacy missing route mode does not enable system fallback");
    routeStore.CreateBoundAccount("系统出口", new(FollowSystemProxy: true));
    Assert(routeStore.ReadEnvironment("系统出口") is { FollowSystemProxy: true, Proxy.Length: 0 }, "system route mode persists without freezing a port");
    routeStore.UpdateEnvironment("系统出口", locale: "zh-CN");
    Assert(routeStore.ReadEnvironment("系统出口").FollowSystemProxy, "unrelated environment edits preserve route mode");
    routeStore.UpdateEnvironment("系统出口", proxy: "127.0.0.1:7897");
    Assert(routeStore.ReadEnvironment("系统出口") is { FollowSystemProxy: false, Proxy: "http://127.0.0.1:7897" }, "explicit proxy update chooses dedicated route");
    routeStore.UpdateEnvironment("系统出口", followSystemProxy: true);
    Assert(routeStore.ReadEnvironment("系统出口") is { FollowSystemProxy: true, Proxy.Length: 0 }, "system route removes stale dedicated port");
    Expect(StorageError.InvalidValue, () => routeStore.CreateBoundAccount("未选出口", new()), "unselected route still refuses empty proxy");
    foreach (var bad in new[] { "", ".", "..", "CON", "nul.json", "com1", "LPT9.txt", "abc.", "x/y", "x\\y", "x:y", "x\nq" })
        Expect(StorageError.InvalidName, () => ProfileRules.Name(bad), "invalid name " + bad);
    Assert(ProfileRules.Name(" 档案一 ") == "档案一", "Chinese name trims");
    Assert(ProfileRules.NormalizeProxy("127.0.0.1:7897/") == "http://127.0.0.1:7897", "normalize shorthand");
    Assert(ProfileRules.NormalizeProxy("SOCKS5://[::1]:7897") == "socks5://[::1]:7897", "normalize IPv6");
    foreach (var bad in new[] { "socks4://localhost:80", "http://a:b@localhost:80", "http://localhost:0", "localhost:65536", "中:80", "ftp://host:80", "host:8x" })
        Expect(StorageError.InvalidValue, () => ProfileRules.NormalizeProxy(bad), "invalid proxy " + bad);
    Assert(ProfileRules.ValidTimezone("America/Chicago") && !ProfileRules.ValidTimezone("x;echo"), "timezone syntax");
    Assert(ProfileRules.ValidLocale("zh-Hans-CN") && !ProfileRules.ValidLocale("en --evil"), "locale syntax");
    var selected = new IdentityRef(IdentityKind.Account, "选中");
    var active = new IdentityRef(IdentityKind.Account, "当前");
    var state = new IdentityState(active, active, active) with { Selected = selected };
    Assert(state.Selected == selected && state.Active == active && state.Running == active, "selection does not change live identities");
    Assert(new IdentityRef(IdentityKind.Provider, "Work") == new IdentityRef(IdentityKind.Provider, "work"), "Windows identity case equality");
    Assert(new IdentityRef(IdentityKind.Account, "Work") != new IdentityRef(IdentityKind.Provider, "work"), "identity kind independent");
    Assert(!ProfileRules.ValidLocale("en-US\n") && !ProfileRules.ValidTimezone("UTC\n"), "locale/timezone strict syntax");
    Write("browser-envs/档案一/env.json", "{\"proxy\":\"http://localhost:80\",\"timezone\":\"Asia/Tokyo\",\"locale\":\"ja-JP\",\"locale_auto\":true,\"_说明\":[\"fixture\"],\"custom\":7}");
    store.UpdateEnvironment("档案一", proxy: "127.0.0.1:7897");
    Assert(store.ReadEnvironment("档案一").Timezone == "Asia/Tokyo" && store.ReadEnvironment("档案一").LocaleAuto, "partial env preserves fields");
    Assert(JsonNode.Parse(Read("browser-envs/档案一/env.json"))!["custom"]!.GetValue<int>() == 7, "env preserves unknown keys");
    var observedIdentity = new IdentityRef(IdentityKind.Account, "已测出口");
    var observed = new ExitObservation("http://127.0.0.1:7897", false, "192.0.2.7", "America/Chicago", "美国", DateTimeOffset.UtcNow);
    store.SaveExitObservation(observedIdentity, observed);
    Assert(new FileProfileStore(checkRoot).ReadExitObservation(observedIdentity) == observed, "exit observation survives profile store restart");
    Assert(observed.Matches(new(observed.Proxy, "Asia/Tokyo", "zh-CN")), "timezone and language edits keep route observation");
    Assert(!observed.Matches(new("http://127.0.0.1:7898")) && !observed.Matches(new(FollowSystemProxy: true)), "proxy or routing mode change rejects previous route observation");
    Assert(store.ReadExitObservation(new(IdentityKind.Account, "未测出口")) is null, "another identity does not inherit an observed IP");
    Write("profiles/已测出口/exit-observation.json", "{\"ip\":\"not-an-ip\",\"checkedAt\":\"bad\"}");
    Assert(store.ReadExitObservation(observedIdentity) is null, "invalid exit observation is not evidence");
    Expect(StorageError.InvalidValue, () => store.SaveExitObservation(observedIdentity, observed with { Ip = "invalid" }), "invalid observation cannot be saved");
    store.ClearExitObservation(observedIdentity);
    Assert(store.ReadExitObservation(observedIdentity) is null, "explicit maintenance clears saved exit observation");
    Directory.Delete(store.ProfileDirectory(observedIdentity));
    store.UpdateEnvironment("档案一", locale: "en-US");
    Assert(!store.ReadEnvironment("档案一").LocaleAuto, "manual locale disables automatic override");
    store.UpdateEnvironment("档案一", proxy: "");
    Assert(store.ReadEnvironment("档案一").Proxy == "", "explicit empty clears proxy");
    Write("settings.json", "{\"custom\":{\"fixture\":true}}");
    store.SaveSettings(new(false, false, false, "dark"));
    Assert(store.ReadSettings() == new AppSettings(false, false, false, "dark") && JsonNode.Parse(Read("settings.json"))!["custom"] is JsonObject, "settings compatibility");
    Expect(StorageError.InvalidValue, () => store.SaveSettings(new(WorkspaceTheme: "bad")), "theme validation");
    store.SaveAccount("档案一", JsonNode.Parse("{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"fixture-only\"},\"other\":42}")!.AsObject());
    Assert(store.ReadAccountCredentials("档案一")["other"]!.GetValue<int>() == 42, "opaque credentials preserved");
    Assert(store.ListAccounts().Single().HasCredentials, "credential presence only public");
    Expect(StorageError.Conflict, () => store.SaveAccount("档案一", new()), "account conflict");
    store.SaveAccount("二", new()); store.SaveAccount("三", new());
    store.SaveAccount("四", new());
    Assert(store.ListAccounts().Count == 4, "ordinary account count isn't artificially limited");
    Write("browser-envs/损坏/env.json", "{");
    Expect(StorageError.Corrupt, () => store.ReadEnvironment("损坏"), "corrupt env explicit");
    File.Delete(Path.Combine(checkRoot, "browser-envs/损坏/env.json"));
    store.SaveProvider("测试供应商", "https://fixture.invalid/v1/", "fixture-only", "demo-model");
    Assert(store.ReadProvider("测试供应商") is { BaseUrl: "https://fixture.invalid/v1", HasKey: true }, "public provider metadata");
    Assert(JsonNode.Parse(Read("providers/测试供应商/auth.json"))!["auth_mode"]!.GetValue<string>() == "apikey", "legacy provider auth shape");
    Expect(StorageError.Conflict, () => store.SaveProvider("测试供应商", "https://fixture.invalid", "fixture", overwrite: true, active: new(IdentityKind.Provider, "测试供应商")), "active provider protected");
    store.UpdateProvider("测试供应商", "https://fixture.invalid/v2", "", "model-two");
    store.SaveProvider("Work", "https://fixture.invalid", "fixture");
    Expect(StorageError.Conflict, () => store.SaveProvider("work", "https://fixture.invalid", "fixture", overwrite: true, active: new(IdentityKind.Provider, "Work")), "active case variation protected");
    Expect(StorageError.Conflict, () => store.Archive(new(IdentityKind.Provider, "work"), active: new(IdentityKind.Provider, "Work")), "archive active case variation protected");
    Expect(StorageError.Conflict, () => store.Rename(new(IdentityKind.Provider, "Work"), "work"), "case-only rename rejected");
    Assert(store.ReadProvider("测试供应商").HasKey, "blank update keeps provider key");
    var fault = new FaultStorage(files);
    var failedStore = new FileProfileStore(checkRoot, fault);
    var providerBefore = Read("providers/测试供应商/provider.json");
    var authBefore = Read("providers/测试供应商/auth.json");
    fault.FailWrite = path => path.EndsWith("provider.json", StringComparison.Ordinal);
    Expect(StorageError.Io, () => failedStore.SaveProvider("测试供应商", "https://fixture.invalid/v3", "changed-fixture", overwrite: true), "provider failure reports");
    Assert(Read("providers/测试供应商/auth.json") == authBefore && Read("providers/测试供应商/provider.json") == providerBefore, "provider pair recovered");
    fault.FailWrite = null;
    Directory.CreateDirectory(Path.Combine(checkRoot, "electron-envs/档案一"));
    fault.FailMove = path => path.Contains("browser-envs", StringComparison.Ordinal);
    Expect(StorageError.Io, () => failedStore.Rename(new(IdentityKind.Account, "档案一"), "改名"), "rename failure reports");
    Assert(Directory.Exists(Path.Combine(checkRoot, "profiles/档案一")) && !Directory.Exists(Path.Combine(checkRoot, "profiles/改名")), "rename rollback");
    fault.FailMove = null;
    store.Rename(new(IdentityKind.Account, "档案一"), "改名");
    Assert(Directory.Exists(Path.Combine(checkRoot, "browser-envs/改名")) && Directory.Exists(Path.Combine(checkRoot, "electron-envs/改名")), "rename associated directories");
    var archive = store.Archive(new(IdentityKind.Account, "四"));
    Assert(archive.Moved.Count == 1 && Directory.Exists(archive.Moved[0]) && !Directory.Exists(Path.Combine(checkRoot, "profiles/四")), "archive reversible material");
    foreach (var (input, expected) in new (string, long?)[] { ("{}", null), ("{\"cached_input_tokens\":true}", null), ("{\"cached_input_tokens\":-1}", null), ("{\"cached_input_tokens\":10.0}", null), ("{\"cached_input_tokens\":\"10\"}", null), ("{\"cached_input_tokens\":0}", 0), ("{\"cached_input_tokens\":4096}", 4096), ("{\"cached_input_tokens\":50000}", 50000), ("{\"input_tokens_details\":{\"cached_tokens\":8}}", 8) })
        Assert(TokenUsageReader.Read(JsonNode.Parse(input)!.AsObject()).CachedInputTokens == expected, "raw cache count " + input);
    Assert(TokenUsageReader.Read(new JsonObject { ["cached_input_tokens"] = 4096 }).CachedInputTokens == 4096, "programmatic raw count");
    Assert(TokenUsageReader.Read(new JsonObject { ["cached_input_tokens"] = 4096L }).CachedInputTokens == 4096, "programmatic long count");
    Assert(TokenUsageReader.Read(new JsonObject { ["cached_input_tokens"] = 4096.0 }).CachedInputTokens is null, "programmatic double unknown");
    Assert(TokenUsageReader.Read(new JsonObject { ["cached_input_tokens"] = 4096m }).CachedInputTokens is null, "programmatic decimal unknown");
    Assert(System.Text.Json.JsonSerializer.Serialize(TokenUsageReader.Read(new JsonObject { ["cached_input_tokens"] = 8 })) == "{\"CachedInputTokens\":8}", "usage contains only raw count no capability fields");
    Write("browser-envs/改名/Default/Cookies", "fixture browser body");
    Write("unrelated.txt", "untouched");
    var backups = new BackupService(checkRoot, files);
    var first = backups.Create("fixture");
    Assert(first.Ok && first.Count > 0, "backup creates version one");
    var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(first.Path, "MANIFEST.json")))!.AsObject();
    Assert(manifest["version"]!.GetValue<int>() == 1, "v1 manifest compatibility");
    Assert(!File.Exists(Path.Combine(first.Path, "browser-envs/改名/Default/Cookies")) && !File.Exists(Path.Combine(first.Path, "unrelated.txt")), "backup scope excludes browser body");
    Assert(backups.List().Items.Any(item => item.Name == first.Name), "list reads manifest");
    var settingsBefore = Read("settings.json");
    Write("settings.json", "{\"later\":true}");
    var restored = backups.Restore(first.Name);
    Assert(restored.Ok && restored.PreBackup.Length > 0 && Read("settings.json") == settingsBefore, "restore old backup");
    Assert(File.ReadAllText(Path.Combine(checkRoot, "backups", restored.PreBackup, "settings.json")) == "{\"later\":true}" && Read("unrelated.txt") == "untouched", "restore preserves pre state and unrelated file");
    var manifestPath = Path.Combine(first.Path, "MANIFEST.json");
    var originalManifest = File.ReadAllText(manifestPath);
    manifest["files"]![0]!["sha256"] = new string('0', 64);
    File.WriteAllText(manifestPath, manifest.ToJsonString());
    var invalid = backups.Restore(first.Name);
    Assert(!invalid.Ok && invalid.Restored == 0 && invalid.PreBackup.Length == 0, "hash invalid blocks all writes");
    File.WriteAllText(manifestPath, originalManifest);
    var external = checkRoot + "-outside-fixture";
    Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(external, "auth.json"), "external fixture unchanged");
    var junction = Path.Combine(checkRoot, "profiles", "联接");
    var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "/c", "mklink", "/J", junction, external }) start.ArgumentList.Add(argument);
    using (var process = System.Diagnostics.Process.Start(start)!)
    { process.WaitForExit(); Assert(process.ExitCode == 0, "create isolated junction fixture: " + process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd()); }
    Expect(StorageError.Io, () => store.ReadAccountCredentials("联接"), "linked profile read blocked");
    Expect(StorageError.Io, () => store.SaveAccount("联接", new(), overwrite: true), "linked profile save blocked");
    manifest = JsonNode.Parse(originalManifest)!.AsObject();
    var entryWithLink = manifest["files"]![0]!.AsObject();
    var existingBackup = Path.Combine(first.Path, entryWithLink["path"]!.GetValue<string>());
    var linkedBackupTarget = Path.Combine(first.Path, "profiles", "联接", "auth.json");
    Directory.CreateDirectory(Path.GetDirectoryName(linkedBackupTarget)!);
    File.Copy(existingBackup, linkedBackupTarget);
    entryWithLink["path"] = "profiles/联接/auth.json";
    File.WriteAllText(manifestPath, manifest.ToJsonString());
    var linked = backups.Restore(first.Name);
    Assert(!linked.Ok && linked.Restored == 0 && linked.PreBackup.Length == 0 && File.ReadAllText(Path.Combine(external, "auth.json")) == "external fixture unchanged", "linked destination blocks entire restore");
    Directory.Delete(junction);
    var sourceLink = Path.Combine(first.Path, "profiles", "来源联接");
    start.ArgumentList.Clear();
    foreach (var argument in new[] { "/c", "mklink", "/J", sourceLink, external }) start.ArgumentList.Add(argument);
    using (var process = System.Diagnostics.Process.Start(start)!)
    { process.WaitForExit(); Assert(process.ExitCode == 0, "create source junction fixture"); }
    entryWithLink["path"] = "profiles/来源联接/auth.json";
    File.WriteAllText(manifestPath, manifest.ToJsonString());
    var linkedSource = backups.Restore(first.Name);
    Assert(!linkedSource.Ok && linkedSource.Restored == 0 && linkedSource.PreBackup.Length == 0, "linked backup source blocked before any writes");
    Directory.Delete(sourceLink);
    File.WriteAllText(manifestPath, originalManifest);
    var failedBackups = new BackupService(checkRoot, fault);
    fault.FailWrite = path => path.Contains("backups", StringComparison.Ordinal) && path.EndsWith("settings.json", StringComparison.Ordinal);
    var blocked = failedBackups.Restore(first.Name);
    Assert(!blocked.Ok && blocked.Restored == 0 && blocked.PreBackup.Length > 0, "failed pre backup prevents restore");
    fault.FailWrite = path => path == Path.Combine(checkRoot, "settings.json");
    var partial = failedBackups.Restore(first.Name);
    Assert(!partial.Ok && partial.Restored > 0 && partial.Failed.Count == 1 && Directory.Exists(Path.Combine(checkRoot, "backups", partial.PreBackup)), "partial restore retains recovery backup");
    fault.FailWrite = null;
    Write("backups/broken/MANIFEST.json", "{");
    Assert(backups.List().Issues.Any(issue => issue.Code == StorageError.Corrupt), "corrupt list entry explicit");
    foreach (var path in new[] { "../settings.json", "D:\\outside.json", "unrelated.txt", "profiles/../settings.json", "browser-envs/改名/Default/Cookies" })
    {
        manifest = JsonNode.Parse(originalManifest)!.AsObject();
        manifest["files"]![0]!["path"] = path;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        Assert(!backups.Restore(first.Name).Ok, "unsafe restore scope " + path);
    }
    File.WriteAllText(manifestPath, originalManifest);
    using (var locked = new FileStream(Path.Combine(checkRoot, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        Expect(StorageError.Io, () => store.SaveSettings(new()), "locked file failure explicit");
    Assert(!Directory.GetFiles(checkRoot, "*.tmp", SearchOption.AllDirectories).Any(), "atomic files clean temporary materials");
    count += SwitchStorageChecks.Run(Path.Combine(checkRoot, "switch"));
    count += ProviderEditingChecks.Run(Path.Combine(checkRoot, "provider-edit"));
    count += BackupStreamingChecks.Run(Path.Combine(checkRoot, "backup-streaming"));
    count += ManyAccountChecks.Run(Path.Combine(checkRoot, "many-accounts"));
    count += ManagedCloneChecks.Run(Path.Combine(checkRoot, "managed-clone"));
    count += ManagedToolIsolationChecks.Run(Path.Combine(checkRoot, "managed-tools"));
    LinkFixtures.Report();
    Console.WriteLine($"PASS {count} isolated core checks; fixture root: {checkRoot}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL after {count} checks: {error}; fixture root: {checkRoot}");
    return 1;
}

sealed class FaultStorage(IFileStorage actual) : IFileStorage
{
    public Func<string, bool>? FailWrite { get; set; }
    public Func<string, bool>? FailMove { get; set; }
    public Func<string, bool>? FailDeleteDirectory { get; set; }
    public Func<string, bool>? FailDeleteFile { get; set; }
    public bool FileExists(string path) => actual.FileExists(path);
    public bool DirectoryExists(string path) => actual.DirectoryExists(path);
    public byte[] Read(string path) => actual.Read(path);
    public void WriteAtomic(string path, byte[] content)
    { if (FailWrite?.Invoke(path) == true) throw new IOException("injected write failure"); actual.WriteAtomic(path, content); }
    public void CreateDirectory(string path) => actual.CreateDirectory(path);
    public IReadOnlyList<string> Files(string path, bool recursive) => actual.Files(path, recursive);
    public IReadOnlyList<string> Directories(string path) => actual.Directories(path);
    public void MoveDirectory(string source, string destination)
    { if (FailMove?.Invoke(source) == true) throw new IOException("injected move failure"); actual.MoveDirectory(source, destination); }
    public void DeleteFile(string path)
    { if (FailDeleteFile?.Invoke(path) == true) throw new IOException("injected delete failure"); actual.DeleteFile(path); }
    public void DeleteDirectory(string path, bool recursive)
    { if (FailDeleteDirectory?.Invoke(path) == true) throw new IOException("injected delete failure"); actual.DeleteDirectory(path, recursive); }
}
