using GameNetworkCommon;
using MyDemo.InterServer;
using Grpc.Core;

/// <summary>
/// Implements the MatchService gRPC service. Delegates matchmaking logic to
/// <see cref="MatchmakingService"/> and uses <see cref="GatewayCallback.GatewayCallbackClient"/>
/// to notify the Gateway when a match is found.
/// </summary>
public sealed class MatchGrpcService : MyDemo.InterServer.MatchService.MatchServiceBase
{
    private readonly MatchmakingService _matchmaking;
    private readonly GatewayCallback.GatewayCallbackClient _gatewayCallback;
    private readonly object _lock = new();
    private readonly Dictionary<int, TaskCompletionSource<MatchFoundEvent>> _watchers = new();

    /// <summary>
    /// 初始化匹配gRPC服务，订阅匹配成功事件
    /// </summary>
    public MatchGrpcService(MatchmakingService matchmaking, GatewayCallback.GatewayCallbackClient gatewayCallback)
    {
        _matchmaking = matchmaking;
        _gatewayCallback = gatewayCallback;
        _matchmaking.MatchFound += (playerIds, roomId, gameMode) =>
        {
            _ = OnMatchFoundAsync(playerIds, roomId, gameMode);
        };
    }

    /// <summary>
    /// 处理玩家加入匹配队列的gRPC请求
    /// </summary>
    public override Task<MatchJoinResponse> JoinQueue(MatchJoinRequest request, ServerCallContext context)
    {
        var (success, message, matchFound, roomId, queuePosition) = _matchmaking.JoinQueue(
            request.PlayerId, request.Username, request.GameMode);

        return Task.FromResult(new MatchJoinResponse
        {
            Success = success,
            Message = message,
            MatchFound = matchFound,
            RoomId = roomId,
            QueuePosition = queuePosition
        });
    }

    /// <summary>
    /// 处理玩家取消匹配队列的gRPC请求
    /// </summary>
    public override Task<MatchCancelResponse> CancelQueue(MatchCancelRequest request, ServerCallContext context)
    {
        var cancelled = _matchmaking.CancelQueue(request.PlayerId);
        return Task.FromResult(new MatchCancelResponse
        {
            Success = cancelled,
            Message = cancelled ? "Cancelled" : "Not in queue"
        });
    }

    /// <summary>
    /// 处理查询队列状态的gRPC请求
    /// </summary>
    public override Task<QueueStatusResponse> GetQueueStatus(QueueStatusRequest request, ServerCallContext context)
    {
        var (inQueue, position, estimatedWait) = _matchmaking.GetQueueStatus(request.PlayerId);
        return Task.FromResult(new QueueStatusResponse
        {
            InQueue = inQueue,
            QueuePosition = position,
            EstimatedWaitSeconds = estimatedWait
        });
    }

    /// <summary>
    /// 服务端流式推送，等待匹配成功事件并通知客户端
    /// </summary>
    public override async Task WatchMatch(
        WatchMatchRequest request,
        IServerStreamWriter<MatchFoundEvent> responseStream,
        ServerCallContext context)
    {
        var tcs = new TaskCompletionSource<MatchFoundEvent>();
        lock (_lock)
        {
            _watchers[request.PlayerId] = tcs;
        }

        try
        {
            var matchEvent = await tcs.Task.WaitAsync(context.CancellationToken);
            await responseStream.WriteAsync(matchEvent);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or cancelled
        }
        finally
        {
            lock (_lock)
            {
                _watchers.Remove(request.PlayerId);
            }
        }
    }

    /// <summary>
    /// 匹配成功时通知网关并推送给订阅的客户端
    /// </summary>
    private async Task OnMatchFoundAsync(IReadOnlyList<int> playerIds, string roomId, string gameMode)
    {
        // Notify Gateway
        try
        {
            await _gatewayCallback.NotifyMatchFoundAsync(new NotifyMatchFoundRequest
            {
                PlayerIds = { playerIds },
                RoomId = roomId,
                GameMode = gameMode
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MatchGrpcService] Failed to notify Gateway: {ex.Message}");
        }

        // Push to watching clients
        var matchEvent = new MatchFoundEvent
        {
            PlayerIds = { playerIds },
            RoomId = roomId,
            GameMode = gameMode
        };

        foreach (var playerId in playerIds)
        {
            TaskCompletionSource<MatchFoundEvent>? tcs;
            lock (_lock)
            {
                _watchers.Remove(playerId, out tcs);
            }
            tcs?.TrySetResult(matchEvent);
        }
    }
}
