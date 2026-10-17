using FAswitch.Accounts;
using FAswitch.NodeTesting;

namespace FAswitch.Desktop;

// Reopen only ports explicitly bound to a selected node. Ordinary FlClash requests pass through.
internal sealed class ManagedRouteTransport(IAccountTransport transport, ManagedNodeRoutes routes) : IAccountTransport
{
    public async Task<AccountHttpResponse> SendAsync(AccountHttpRequest request, CancellationToken cancellationToken)
    {
        await routes.EnsureProxyAsync(request.Proxy, cancellationToken);
        return await transport.SendAsync(request, cancellationToken);
    }
}
