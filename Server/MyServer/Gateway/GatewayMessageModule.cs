using GameNetworkCommon;

public interface IGatewayMessageModule
{
    void Register(GatewayMessageRouter router, GatewayModuleServices services);
}

public sealed record GatewayModuleServices(
    GameSessionService GameSessions,
    Action<int> NotifyPlayerReturnedToRoom);

/// <summary>
/// Keeps GamePlayThree commands at the gateway boundary. The module only
/// authenticates and routes packets; GameThreeSession validates and mutates
/// the authoritative match state.
/// </summary>
public sealed class GameThreeGatewayMessageModule : IGatewayMessageModule
{
    public void Register(GatewayMessageRouter router, GatewayModuleServices services)
    {
        router.Register(Protocols.GameThreeInput, true, context =>
        {
            RouteGameMessage(context, services);
            return ValueTask.CompletedTask;
        });
        router.Register(Protocols.GameThreeThrowBombRequest, true, context =>
        {
            RouteGameMessage(context, services);
            return ValueTask.CompletedTask;
        });
        router.Register(Protocols.GameThreeUseSkillRequest, true, context =>
        {
            RouteGameMessage(context, services);
            return ValueTask.CompletedTask;
        });
        router.Register(Protocols.GameThreeDashRequest, true, context =>
        {
            RouteGameMessage(context, services);
            return ValueTask.CompletedTask;
        });
        router.Register(Protocols.GameThreeLeaveGame, true, context =>
        {
            HandleLeave(context, services);
            return ValueTask.CompletedTask;
        });
    }

    private static void RouteGameMessage(
        GatewayMessageContext context,
        GatewayModuleServices services)
    {
        if (!context.Session.PlayerId.HasValue
            || !services.GameSessions.HandleMessage(context.Session.PlayerId.Value, context.Message))
        {
            // UDP movement intentionally repeats recent frames. A duplicate or
            // late frame is expected and is rejected by the session sequence
            // gate without needing a warning for every packet.
            if (context.Message.ProtocolId == Protocols.GameThreeInput)
            {
                return;
            }

            Console.WriteLine(
                $"[GameSession] Ignored protocol {context.Message.ProtocolId} " +
                $"for session {context.Session.SessionId}");
        }
    }

    private static void HandleLeave(
        GatewayMessageContext context,
        GatewayModuleServices services)
    {
        var request = MessageCodec.Decode<GameThreeLeaveGame>(context.Message);
        if (context.Session.PlayerId != request.PlayerId)
        {
            Console.WriteLine($"[Gateway] Rejected GameThree leave player mismatch for session {context.Session.SessionId}");
            return;
        }

        var stopped = services.GameSessions.StopSessionForPlayer(request.PlayerId);
        services.NotifyPlayerReturnedToRoom(request.PlayerId);
        var packet = MessageCodec.EncodePacket(
            Protocols.GameThreeLeaveGameResponse,
            new GameThreeLeaveGameResponse
            {
                Success = true,
                Message = stopped ? "Game session stopped successfully" : "No active game session"
            });
        if (!context.Session.TrySend(packet))
        {
            throw new IOException("The gateway session closed before the leave response could be sent.");
        }
    }
}
