using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ChunsonSpace.Accounts;
using ChunsonSpace.Core;

namespace ChunsonSpace.Desktop;

internal static class AccountLoginTimeVerification
{
    internal static async Task RunAsync(string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Use a new fixture directory.");
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        var root = Path.Combine(directory, "fixture");
        var repository = new AccountRepository(root);
        var store = repository.Profiles;
        var first = DateTimeOffset.Parse("2026-10-16T10:00:00Z", CultureInfo.InvariantCulture);
        const string proxy = "http://127.0.0.1:7890";
        store.SaveAccount("A", Auth(first));
        store.SaveEnvironment("A", new(proxy, "", ""));
        store.SaveAccount("Unknown", Auth(null, "fixture-unknown"));
        var metadata = Path.Combine(repository.AccountPath("A"), "login-metadata.json");
        File.WriteAllText(metadata, "{\"loggedInAt\":\"2026-10-18T09:00:00.0000000+00:00\"}");
        Check("existing credential auth_time wins over old latest-authorization metadata", store.ReadLoginTime("A") == first);
        Check("missing auth_time ignores iat and last_refresh", store.ReadLoginTime("Unknown") is null);

        var refreshed = await new AccountService(repository, new FixtureTransport(first.AddHours(4))).RefreshAsync("A");
        var preserved = File.ReadAllText(metadata);
        Check("token refresh snapshots original auth_time and its source", refreshed.Refreshed && store.ReadLoginTime("A") == first
            && preserved.Contains("firstCredentialAt") && preserved.Contains("id_token.auth_time"));
        repository.SaveLogin("A", Auth(first.AddHours(10)));
        repository.ImportLogin(Auth(first.AddHours(20)), proxy);
        Check("reauthorization and matching import retain original timestamp without rewriting metadata", store.ReadLoginTime("A") == first && File.ReadAllText(metadata) == preserved);

        Check("one decimal hours", IdentityItem.FormatLoginHours(first, first.AddHours(12.5)) == "12.5 小时");
        Check("UTC elapsed hours accumulate across days", IdentityItem.FormatLoginHours(first.ToOffset(TimeSpan.FromHours(8)), first.AddHours(60.5)) == "60.5 小时");
        Check("unknown and future timestamps", IdentityItem.FormatLoginHours(null, first) == "未知"
            && IdentityItem.FormatLoginHours(first.AddHours(1), first) == "0.0 小时");

        File.WriteAllText(Path.Combine(root, "ChunsonSpace-fixture.json"), "{\"fixture\":true}");
        var workspace = new DesktopWorkspace(root, Path.Combine(directory, "home"), Path.Combine(directory, "desktop"), fixture: true);
        var listener = new UiVerification.BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var model = new ShellViewModel(workspace);
        var window = UiVerification.CreateWindow(model);
        try
        {
            model.Navigate("Accounts");
            foreach (var name in new[] { "A", "Unknown" })
            {
                model.Selected = model.FilteredItems.Single(a => a.Name == name);
                model.RefreshLoginTimes(first.AddHours(12.5));
                UiVerification.ArrangeForCapture(window.RenderRoot, 1040, 670);
                var selected = model.Selected!;
                var detail = (TextBlock)window.FindName("SelectedLoginTimeText");
                var list = (ListBox)window.FindName("ProfileList");
                var row = UiVerification.VisualDescendants(list).OfType<TextBlock>().Single(t => t.Name == "RowLoginTimeText" && ReferenceEquals(t.DataContext, selected));
                var expected = name == "A" ? "已登录：12.5 小时" : "已登录：未知";
                Check(name + ": row and detail agree", row.Text == expected && detail.Text == expected);
                if (name == "A")
                {
                    model.RefreshLoginTimes(first.AddHours(36.5));
                    UiVerification.ArrangeForCapture(window.RenderRoot, 1040, 670);
                    Check("shared notification updates both bindings without replacing identity or origin", row.Text == "已登录：36.5 小时" && detail.Text == row.Text
                        && ReferenceEquals(selected, model.Selected) && selected.LoggedInAt == first);
                }
            }
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var started = window.LoginHoursClockRunning;
            window.Close();
            Check("one window clock starts on load and stops on close", started && !window.LoginHoursClockRunning);
            Check("no binding errors", listener.Errors.Count == 0);
        }
        finally { window.Close(); PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); await workspace.Accounts!.DisposeAsync(); }
        File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new
        { passed = true, scope = "first-credential-hours", checks, bindingErrors = listener.Errors, realOAuth = 0, realModelRequests = 0, realSystemWrites = 0, screenshots = 0 }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static JsonObject Tokens(DateTimeOffset? first, string id = "fixture-A") => new()
    {
        ["access_token"] = "fixture-access", ["refresh_token"] = "fixture-refresh",
        ["id_token"] = "fixture." + OAuthPkce.Base64Url(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["auth_time"] = first?.ToUnixTimeSeconds(), ["iat"] = 1792319946,
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = id }
        }.ToJsonString())) + ".fixture"
    };
    private static JsonObject Auth(DateTimeOffset? first, string id = "fixture-A") => JwtClaims.ToAuth(Tokens(first, id), DateTimeOffset.UtcNow);
    private sealed class FixtureTransport(DateTimeOffset first) : IAccountTransport
    {
        public Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Url != AccountEndpoints.Token) throw new InvalidOperationException("Unexpected fixture request.");
            return Task.FromResult(new AccountHttpResponse(200, new MemoryStream(Encoding.UTF8.GetBytes(Tokens(first).ToJsonString()))));
        }
    }
}
