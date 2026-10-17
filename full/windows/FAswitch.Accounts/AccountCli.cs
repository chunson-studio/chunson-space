using System.Text;
using FAswitch.Core;

namespace FAswitch.Accounts;

public sealed class AccountCli(AccountRepository repository, LoginService login, EgressService egress, TextWriter output, bool developer = false, string programName = "ChunsonSwitch.exe")
{
    public string Usage => FormatUsage(programName);
    public static string FormatUsage(string programName = "ChunsonSwitch.exe") => $"Chunson Space 命令行\n\n{programName} --agent <request.json> <new-report.json>\nAgent commands: status, subscriptions, exit, test-node, test-subscription, login, dns-preview, dns-apply.\n\n{programName} --list\n{programName} --set <账号名> [--proxy <url>] [--timezone <IANA>]\n{programName} --new <账号名> --proxy <HTTP/HTTPS代理> [--timezone auto|<IANA>] [--launch]\n\n--launch 等待你在该档案的浏览器中登录。--set 未提供的字段保持原值；--proxy= / --timezone= 明确清空。代理为空或无效时不会改用系统出口。\n退出码：0 成功 / 2 参数错误 / 3 账号不存在 / 4 拒绝或失败 / 5 时区需要选择";
    public static async Task<int?> TryRunWithoutServicesAsync(IReadOnlyList<string> arguments, TextWriter output, string programName = "ChunsonSwitch.exe")
    {
        if (arguments.Count == 0) return null;
        try
        {
            var options = Parse(arguments);
            if (!options.Help) return null;
            await output.WriteLineAsync(FormatUsage(programName)); return 0;
        }
        catch (ArgumentException error) { await output.WriteLineAsync(error.Message + "\n\n" + FormatUsage(programName)); return 2; }
    }
    public async Task<int?> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Count == 0) return null;
        Options options;
        try { options = Parse(arguments); }
        catch (ArgumentException error) { await Say(error.Message + "\n\n" + Usage); return 2; }
        if (options.Help) { await Say(Usage); return 0; }
        var resolvingTimezone = false;
        try
        {
            if (options.List)
            {
                if (options.Name is not null || options.NewName is not null || options.Proxy is not null || options.Timezone is not null || options.Launch) return await UsageError("--list 不能与其他操作参数一起用。");
                await PrintList(); return 0;
            }
            if (options.Name is not null && options.NewName is not null) return await UsageError("--set 和 --new 不能一起用。");
            if (options.NewName is not null)
            {
                if (options.Proxy is null) return await UsageError("--new 必须提供 --proxy。");
                string name;
                try { name = ProfileRules.Name(options.NewName); }
                catch (StorageException) { return await UsageError("账号名称不合法。"); }
                var auto = options.Timezone?.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) == true;
                if (options.Timezone is not null && !auto && !ProfileRules.ValidTimezone(options.Timezone.Trim()))
                { await Say("时区写法不合法，新档案尚未建立。"); return 4; }
                var proxy = HttpAccountTransport.RequireHttpProxy(options.Proxy);
                repository.Profiles.CreateBoundAccount(name, new(proxy, auto ? "" : options.Timezone?.Trim() ?? ""), developer);
                await Say($"已建好账号 [{name}]，代理已绑定。");
                if (auto)
                {
                    resolvingTimezone = true;
                    var suggestion = await egress.SuggestTimezoneAsync(name, cancellationToken);
                    if (suggestion.Current is not null) await Say("已有时区配置，保持原值。");
                    else if (suggestion.Candidates.Count == 1)
                    {
                        repository.Profiles.UpdateEnvironment(name, timezone: suggestion.Candidates[0].Timezone);
                        await Say("已按出口填好时区：" + suggestion.Candidates[0].Timezone);
                    }
                    else
                    {
                        await Say("档案和代理已经保存；时区尚未确定，浏览器未启动。请从候选中选择后用 --set 保存：");
                        foreach (var candidate in suggestion.Candidates) await Say(candidate.Timezone + "  " + candidate.Label);
                        await Say($"{programName} --set \"{name}\" --timezone <IANA时区>");
                        return 5;
                    }
                    resolvingTimezone = false;
                }
                if (!options.Launch) { await Say("请回到 Chunson Space 刷新列表，再登录此档案。不要再次 --new 同名档案。"); return 0; }
                await Say("请在此档案的浏览器中完成登录；此命令会等待结果，取消不会保存新凭据。");
                var result = await login.LoginAsync(name, cancellationToken: cancellationToken);
                await Say(result.Message);
                foreach (var warning in result.Warnings ?? []) await Say(warning);
                return result.Ok ? 0 : 4;
            }
            if (options.Launch) return await UsageError("--launch 只能与 --new 一起用。");
            if (options.Name is null) return await UsageError("请选择 --list、--set 或 --new。");
            if (options.Proxy is null && options.Timezone is null) return await UsageError("--set 至少提供 --proxy 或 --timezone。");
            if (!repository.Exists(options.Name)) { await Say("账号不存在：" + options.Name); return 3; }
            if (options.Timezone?.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) == true) return await UsageError("auto 只能与 --new 一起用。");
            repository.Profiles.UpdateEnvironment(options.Name, proxy: options.Proxy, timezone: options.Timezone);
            await Say("档案配置已保存，对新启动的浏览器和客户端生效。");
            return 0;
        }
        catch (OperationCanceledException) { await Say("操作已取消。"); return 4; }
        catch (AccountException error) { await Say(error.Message); return resolvingTimezone ? 5 : 4; }
        catch (StorageException error) { await Say(error.Message); return error.Code == StorageError.Missing ? 3 : 4; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await Say("文件访问失败，请检查权限或占用。"); return 4; }
    }
    private async Task PrintList()
    {
        var accounts = repository.Profiles.ListAccounts();
        if (accounts.Count == 0) { await Say("还没有账号档案。"); return; }
        var rows = new List<string[]> { new[] { "账号", "代理", "时区" } };
        rows.AddRange(accounts.Select(account => new[] { account.Name, account.Environment.Proxy.Length > 0 ? ErrorRedaction.Proxy(account.Environment.Proxy) : "（没绑：不会启动）", account.Environment.Timezone.Length > 0 ? account.Environment.Timezone : "（跟随系统）" }));
        var widths = Enumerable.Range(0, 3).Select(column => rows.Max(row => Width(row[column]))).ToArray();
        foreach (var row in rows) await Say(string.Join("  ", row.Select((value, column) => value + new string(' ', widths[column] - Width(value)))).TrimEnd());
    }
    private Task Say(string text) => output.WriteLineAsync(text);
    private async Task<int> UsageError(string text) { await Say(text + "\n\n" + Usage); return 2; }
    private sealed record Options(bool List, bool Help, bool Launch, string? Name, string? NewName, string? Proxy, string? Timezone);
    private static Options Parse(IReadOnlyList<string> args)
    {
        var list = false; var help = false; var launch = false;
        string? name = null, newName = null, proxy = null, timezone = null;
        for (var i = 0; i < args.Count; i++)
        {
            var split = args[i].Split('=', 2);
            var flag = split[0];
            if (flag is "--list" or "--help" or "-h" or "--launch")
            {
                if (split.Length > 1) throw new ArgumentException("布尔参数不能带值：" + flag);
                if (flag == "--list") list = true; else if (flag is "--help" or "-h") help = true; else launch = true;
                continue;
            }
            if (flag is not ("--set" or "--new" or "--proxy" or "--timezone")) throw new ArgumentException("不认识的参数：" + ErrorRedaction.Scrub(flag));
            var value = split.Length > 1 ? split[1] : ++i < args.Count && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i] : throw new ArgumentException("参数缺少值：" + flag);
            switch (flag) { case "--set": name = value; break; case "--new": newName = value; break; case "--proxy": proxy = value; break; case "--timezone": timezone = value; break; }
        }
        return new(list, help, launch, name, newName, proxy, timezone);
    }
    private static int Width(string text) => text.EnumerateRunes().Sum(rune => rune.Value is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE6F or >= 0xFF00 and <= 0xFF60 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD ? 2 : 1);
}
