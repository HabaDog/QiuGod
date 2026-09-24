using GameNetworkCommon;
using MyDemo.InterServer;
using Grpc.Core;
using System.Collections.Concurrent;

namespace MyGateway;

/// <summary>
/// Gateway 作为 gRPC Server，接收 Lobby/Match/GameServer 的回调广播。
/// </summary>
public sealed class GatewayGrpcService : GatewayCallback.GatewayCallbackBase
{
    private readonly GatewayServer _gateway;

    /// <summary>
    /// 初始化 GatewayGrpcService 并注入 GatewayServer 实例
    /// </summary>
    public GatewayGrpcService(GatewayServer gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// 向指定玩家广播房间状态数据
    /// </summary>
    public override Task<BroadcastAck> BroadcastRoomState(BroadcastRoomStateRequest request, ServerCallContext context)
    {
        _gateway.BroadcastRoomStateInternal(request.PlayerIds.ToArray(), request.RoomStateData.ToByteArray());
        return Task.FromResult(new BroadcastAck { Success = true });
    }

    /// <summary>
    /// 向所有在线玩家广播大厅状态数据
    /// </summary>
    public override Task<BroadcastAck> BroadcastLobbyState(BroadcastLobbyStateRequest request, ServerCallContext context)
    {
        _gateway.BroadcastLobbyStateInternal(request.LobbyStateData.ToByteArray());
        return Task.FromResult(new BroadcastAck { Success = true });
    }

    /// <summary>
    /// 向指定玩家广播场景切换通知
    /// </summary>
    public override Task<BroadcastAck> BroadcastSceneNotification(BroadcastSceneNotificationRequest request, ServerCallContext context)
    {
        _gateway.BroadcastSceneNotificationInternal(
            request.PlayerIds.ToArray(),
            request.GameMode,
            request.SceneName);
        return Task.FromResult(new BroadcastAck { Success = true });
    }

    /// <summary>
    /// 向指定玩家广播选球状态数据
    /// </summary>
    public override Task<BroadcastAck> BroadcastBallSelectionState(BroadcastBallSelectionStateRequest request, ServerCallContext context)
    {
        _gateway.BroadcastBallSelectionStateInternal(
            request.PlayerIds.ToArray(),
            request.SelectionStateData.ToByteArray());
        return Task.FromResult(new BroadcastAck { Success = true });
    }

    /// <summary>
    /// 通知指定玩家匹配成功
    /// </summary>
    public override Task<BroadcastAck> NotifyMatchFound(NotifyMatchFoundRequest request, ServerCallContext context)
    {
        _gateway.NotifyMatchFoundInternal(
            request.PlayerIds.ToArray(),
            request.RoomId,
            request.GameMode);
        return Task.FromResult(new BroadcastAck { Success = true });
    }

    /// <summary>
    /// 向指定玩家广播游戏数据包
    /// </summary>
    public override Task<BroadcastAck> BroadcastGamePacket(BroadcastGamePacketRequest request, ServerCallContext context)
    {
        _gateway.BroadcastGamePacketInternal(
            request.PlayerIds.ToArray(),
            request.PacketData.ToByteArray());
        return Task.FromResult(new BroadcastAck { Success = true });
    }

    /// <summary>
    /// 通知指定玩家会话已结束
    /// </summary>
    public override Task<BroadcastAck> NotifySessionCompleted(NotifySessionCompletedRequest request, ServerCallContext context)
    {
        _gateway.NotifySessionCompletedInternal(
            request.PlayerIds.ToArray(),
            request.GameMode);
        return Task.FromResult(new BroadcastAck { Success = true });
    }
}