using GameNetworkCommon;
using System.Net.Sockets;

public enum GatewayDispatchResult
{
    Handled,
    UnknownProtocol,
    AuthenticationRequired
}

public readonly record struct GatewayMessageContext(
    BaseMessage Message,
    NetworkStream Stream,
    GatewaySession Session);

/// <summary>
/// Routes decoded gateway messages and applies protocol-level access policy.
/// Feature handlers can be added without extending the transport receive loop.
/// </summary>
public sealed class GatewayMessageRouter
{
    private readonly Dictionary<ushort, Route> _routes = new();

    public void Register(
        ushort protocolId,
        bool requiresAuthentication,
        Func<GatewayMessageContext, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_routes.TryAdd(protocolId, new Route(requiresAuthentication, handler)))
        {
            throw new InvalidOperationException($"Protocol {protocolId} already has a gateway route.");
        }
    }

    public async ValueTask<GatewayDispatchResult> DispatchAsync(GatewayMessageContext context)
    {
        if (!_routes.TryGetValue(context.Message.ProtocolId, out var route))
        {
            return GatewayDispatchResult.UnknownProtocol;
        }

        if (route.RequiresAuthentication && context.Session.PlayerId is null)
        {
            return GatewayDispatchResult.AuthenticationRequired;
        }

        await route.Handler(context);
        return GatewayDispatchResult.Handled;
    }

    private sealed record Route(
        bool RequiresAuthentication,
        Func<GatewayMessageContext, ValueTask> Handler);
}
