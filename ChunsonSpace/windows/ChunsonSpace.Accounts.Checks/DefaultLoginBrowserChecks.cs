using ChunsonSpace.Accounts;

internal static class DefaultLoginBrowserChecks
{
    public static async Task<int> Run()
    {
        int checks = 0, launches = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        Uri? captured = null;
        var browser = new DefaultLoginBrowser(uri => { launches++; captured = uri; });
        var authorize = OAuthPkce.Authorize(1455, "fixture-challenge", "fixture-state");
        var result = await browser.OpenAsync("", authorize, CancellationToken.None);
        Check(launches == 1 && captured == authorize, "default login dispatches the generated official OAuth URI without a precreated profile");
        Check(result.Executable.Length == 0 && result.ProfileDirectory.Length == 0 && result.PreviousRoots is null && result.LaunchLease is null,
            "default login creates no browser profile directory or process ownership lease");
        Check(await browser.ObserveAsync(result, CancellationToken.None) is null,
            "default browser observation remains unknown, allowing callback/cancel/timeout instead of false BrowserClosed");
        foreach (var invalid in new[]
        {
            new Uri("http://auth.openai.com/oauth/authorize"), new Uri("https://other.invalid/oauth/authorize"),
            new Uri("https://auth.openai.com/oauth/token"), new Uri("https://auth.openai.com:444/oauth/authorize"),
            new Uri("https:account-16084d587a@example.invalid/oauth/authorize"), new Uri("https://auth.openai.com/oauth/authorize#fragment"),
            new Uri("/oauth/authorize", UriKind.Relative)
        })
        {
            try { await browser.OpenAsync("", invalid, CancellationToken.None); throw new InvalidOperationException("unauthorized URI allowed"); }
            catch (AccountException error) { Check(error.Code == AccountError.Browser && launches == 1, "default login restricts authorization origin and exact route before dispatch"); }
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await browser.OpenAsync("", authorize, cancelled.Token); throw new InvalidOperationException("cancelled launch dispatched"); }
        catch (OperationCanceledException) { Check(launches == 1, "cancelled default browser launch performs no dispatch"); }
        try { await browser.ObserveAsync(result, cancelled.Token); throw new InvalidOperationException("cancelled observation continued"); }
        catch (OperationCanceledException) { Check(true, "default browser observation honors login cancellation"); }
        var failed = new DefaultLoginBrowser(uri => throw new IOException(uri.AbsoluteUri));
        try { await failed.OpenAsync("", authorize, CancellationToken.None); throw new InvalidOperationException("browser failure reported success"); }
        catch (AccountException error)
        {
            Check(error.Code == AccountError.Browser && error.InnerException is null
                && !error.ToString().Contains("fixture-state", StringComparison.Ordinal)
                && !error.ToString().Contains("fixture-challenge", StringComparison.Ordinal),
                "default browser launch failure hides sensitive OAuth query and raw exception");
        }
        return checks;
    }
}
