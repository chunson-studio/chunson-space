using System.Text.Json.Nodes;
using ChunsonSpace.Core;

namespace ChunsonSpace.Accounts;

public enum AccountError { ExitUnavailable, Network, Http, CorruptResponse, MissingCredentials, IdentityMismatch, Busy, Browser, Callback, Cancelled, Timeout, Storage }
public sealed class AccountException(AccountError code, string message, int? status = null, AccountTransportFailure? transportFailure = null) : Exception(message)
{ public AccountError Code { get; } = code; public int? Status { get; } = status; public AccountTransportFailure? TransportFailure { get; } = transportFailure; }
public interface IAccountClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}
public sealed class SystemAccountClock : IAccountClock
{ public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.Delay(duration, cancellationToken); }
public interface IPendingAccountGuard { bool IsPending { get; } }
public sealed class NoPendingAccountGuard : IPendingAccountGuard { public bool IsPending => false; }
public sealed record CredentialSaveResult(bool Saved, bool ActiveSynced, IReadOnlyList<string> Warnings);
public sealed record RefreshResult(bool Refreshed, string Message);
public sealed record QuotaWindow(double UsedPercent, double RemainingPercent, long WindowSeconds, long ResetAfterSeconds, long? ResetAt);
public sealed record ResetCredit(string? ExpiresAt, string? ResetType, string? Status, long? SecondsLeft);
public sealed record ResetCredits(long Count, IReadOnlyList<ResetCredit> Credits, long? TotalEarned, bool? Purchasable, long? SoonestSeconds);
public sealed record QuotaResult(string Email, string PlanType, bool? Allowed, bool? LimitReached, QuotaWindow? Primary, QuotaWindow? Secondary, ResetCredits ResetCredits, JsonNode? CreditsBalance, bool? HasCredits, DateTimeOffset FetchedAt, bool Refreshed);
public sealed record ConnectivityResult(string Status, string Model, string Reply, TokenUsage Usage, TimeSpan Elapsed, bool Strict);
public sealed record ProviderCheckResult(int Status, IReadOnlyList<string> Models, int ModelCount, TimeSpan Elapsed);
public sealed record EgressResult(bool Ok, string Ip = "", string Country = "", string CountryCode = "", string City = "", string Timezone = "", string Message = "")
{
    public ExitAddresses Addresses { get; init; } = ExitAddresses.Unknown;
}
public sealed record TimezoneCandidate(string Timezone, string Label);
public sealed record TimezoneSuggestion(string CountryCode, IReadOnlyList<TimezoneCandidate> Candidates, string Ip, string? Current = null);
public sealed record EnvironmentDefaultsResult(IReadOnlyList<string> Changed, EnvironmentSettings Environment, EgressResult Egress);

public static class AccountEndpoints
{
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public static readonly Uri Token = new("https://auth.openai.com/oauth/token");
    public static readonly Uri Authorize = new("https://auth.openai.com/oauth/authorize");
    public static readonly Uri Usage = new("https://chatgpt.com/backend-api/wham/usage");
    public static readonly Uri ResetCredits = new("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits");
    public static readonly Uri Responses = new("https://chatgpt.com/backend-api/codex/responses");
    public static readonly Uri Egress = new("https://ipwho.is/?fields=success,ip,country,country_code,city,timezone.id");
    public static readonly Uri Trace = new("https://www.cloudflare.com/cdn-cgi/trace");
}
