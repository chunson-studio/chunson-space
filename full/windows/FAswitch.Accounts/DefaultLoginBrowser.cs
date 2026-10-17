using System.Diagnostics;

namespace FAswitch.Accounts;

public sealed class DefaultLoginBrowser(Action<Uri>? open = null) : ILoginBrowser
{
    private readonly Action<Uri> open = open ?? OpenDefaultBrowser;

    public Task<BrowserLaunch> OpenAsync(string profileName, Uri authorizeUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = AccountEndpoints.Authorize;
        if (authorizeUrl is null || !authorizeUrl.IsAbsoluteUri
            || !authorizeUrl.Scheme.Equals(endpoint.Scheme, StringComparison.OrdinalIgnoreCase)
            || !authorizeUrl.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase)
            || authorizeUrl.Port != endpoint.Port
            || !authorizeUrl.AbsolutePath.Equals(endpoint.AbsolutePath, StringComparison.Ordinal)
            || authorizeUrl.UserInfo.Length != 0 || authorizeUrl.Fragment.Length != 0)
            throw new AccountException(AccountError.Browser, "授权地址不是指定的 OpenAI 登录入口，未打开浏览器。");

        try { open(authorizeUrl); }
        catch (Exception)
        {
            // The URL query contains OAuth state/challenge; never attach raw launch errors.
            throw new AccountException(AccountError.Browser, "无法打开默认浏览器，请检查系统默认浏览器设置后重试。");
        }
        return Task.FromResult(new BrowserLaunch("", "",
            ["授权页面已交给默认浏览器，等待登录回调；软件不会跟踪或关闭用户浏览器。"]));
    }

    public Task<IReadOnlyList<BrowserIdentity>?> ObserveAsync(BrowserLaunch launch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Shell activation can reuse any user-owned browser; PID ownership cannot be confirmed.
        return Task.FromResult<IReadOnlyList<BrowserIdentity>?>(null);
    }

    private static void OpenDefaultBrowser(Uri url)
    {
        using var process = Process.Start(new ProcessStartInfo { FileName = url.AbsoluteUri, UseShellExecute = true });
    }
}
