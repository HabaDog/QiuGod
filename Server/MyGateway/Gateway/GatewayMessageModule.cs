using GameNetworkCommon;

public interface IGatewayMessageModule
{
    void Register(GatewayMessageRouter router, GatewayModuleServices services);
}

public sealed record GatewayModuleServices(
    Action<int, byte[]> ForwardToGameServer,
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
        if (!context.Session.PlayerId.HasValue)
        {
            return;
        }

        // 转发游戏消息到 GameServer（通过 gRPC）
        services.ForwardToGameServer(context.Session.PlayerId.Value, context.Message.ToBytes());
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

        // 转发离开游戏请求到 GameServer
        services.ForwardToGameServer(request.PlayerId, context.Message.ToBytes());
        services.NotifyPlayerReturnedToRoom(request.PlayerId);

        var packet = MessageCodec.EncodePacket(
            Protocols.GameThreeLeaveGameResponse,
            new GameThreeLeaveGameResponse
            {
                Success = true,
                Message = "Left game session"
            });
        if (!context.Session.TrySend(packet))
        {
            throw new IOException("The gateway session closed before the leave response could be sent.");
        }
    }
}
