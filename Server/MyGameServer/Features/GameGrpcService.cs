using GameNetworkCommon;
using Google.Protobuf;
using Grpc.Core;
using MyDemo.InterServer;

public sealed class GameGrpcService : GameService.GameServiceBase
{
    private readonly GameSessionService _gameSessionService;
    private readonly GatewayCallback.GatewayCallbackClient _gatewayCallback;
    private readonly object _subscriptionLock = new();
    private readonly Dictionary<int, IServerStreamWriter<GameStateBroadcast>> _subscriptions = new();

    /// <summary>
    /// 初始化游戏gRPC服务，订阅会话事件
    /// </summary>
    public GameGrpcService(
        GameSessionService gameSessionService,
        GatewayCallback.GatewayCallbackClient gatewayCallback)
    {
        _gameSessionService = gameSessionService ?? throw new ArgumentNullException(nameof(gameSessionService));
        _gatewayCallback = gatewayCallback ?? throw new ArgumentNullException(nameof(gatewayCallback));

        _gameSessionService.BroadcastToPlayers += OnBroadcastToPlayers;
        _gameSessionService.SessionCompleted += OnSessionCompleted;
    }

    /// <summary>
    /// 创建游戏会话并返回会话详情
    /// </summary>
    public override Task<CreateGameSessionResponse> CreateSession(
        CreateGameSessionRequest request,
        ServerCallContext context)
    {
        var ballSelections = request.BallSelections?.ToDictionary(
            entry => entry.Key,
            entry => entry.Value);

        var result = _gameSessionService.StartSession(
            request.PlayerIds,
            string.IsNullOrWhiteSpace(request.GameMode) ? null : request.GameMode,
            ballSelections);

        var response = new CreateGameSessionResponse
        {
            Success = result.Success,
            Message = result.Message,
            SessionId = result.SessionId?.ToString() ?? string.Empty,
            SceneName = result.SceneName
        };

        if (result.Success && result.SessionId is not null)
        {
            foreach (var playerId in request.PlayerIds)
            {
                if (_gameSessionService.TryGetResumeState(playerId, out var resumeState)
                    && resumeState is not null)
                {
                    response.SlotIndices.Add(playerId, resumeState.SlotIndex);
                }
            }
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// 停止指定玩家的游戏会话
    /// </summary>
    public override Task<StopGameSessionResponse> StopSession(
        StopGameSessionRequest request,
        ServerCallContext context)
    {
        var stopped = _gameSessionService.StopSessionForPlayer(request.PlayerId);
        return Task.FromResult(new StopGameSessionResponse
        {
            Success = stopped,
            Message = stopped ? "Session stopped" : "No active session found for this player"
        });
    }

    /// <summary>
    /// 转发玩家输入消息到游戏会话服务
    /// </summary>
    public override Task<ForwardPlayerInputResponse> ForwardPlayerInput(
        ForwardPlayerInputRequest request,
        ServerCallContext context)
    {
        try
        {
            var packetData = request.PacketData.ToByteArray();
            var message = BaseMessage.FromBytes(packetData, 0, packetData.Length);
            var handled = _gameSessionService.HandleMessage(request.PlayerId, message);
            return Task.FromResult(new ForwardPlayerInputResponse { Success = handled });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameGrpc] ForwardPlayerInput failed for player {request.PlayerId}: {ex.Message}");
            return Task.FromResult(new ForwardPlayerInputResponse { Success = false });
        }
    }

    /// <summary>
    /// 服务端流式订阅，保持连接并等待游戏状态广播
    /// </summary>
    public override async Task SubscribeGameState(
        GameStateSubscription request,
        IServerStreamWriter<GameStateBroadcast> responseStream,
        ServerCallContext context)
    {
        var playerId = request.PlayerId;
        lock (_subscriptionLock)
        {
            _subscriptions[playerId] = responseStream;
        }

        try
        {
            Console.WriteLine($"[GameGrpc] Player {playerId} subscribed to game state for session {request.SessionId}");

            // Keep the stream alive until the client disconnects or the cancellation token is triggered
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or server shutting down
        }
        finally
        {
            lock (_subscriptionLock)
            {
                _subscriptions.Remove(playerId);
            }

            Console.WriteLine($"[GameGrpc] Player {playerId} unsubscribed from game state");
        }
    }

    /// <summary>
    /// 获取玩家当前的会话恢复状态
    /// </summary>
    public override Task<GetResumeStateResponse> GetResumeState(
        GetResumeStateRequest request,
        ServerCallContext context)
    {
        var hasSession = _gameSessionService.TryGetResumeState(request.PlayerId, out var resumeState);

        var response = new GetResumeStateResponse
        {
            HasActiveSession = hasSession
        };

        if (hasSession && resumeState is not null)
        {
            response.SessionId = resumeState.SessionId.ToString();
            response.SceneName = resumeState.SceneName;
            response.SlotIndex = resumeState.SlotIndex;
            response.PlayerCount = resumeState.PlayerCount;
            response.GameMode = resumeState.GameMode;
            response.SnapshotPacket = ByteString.CopyFrom(resumeState.SnapshotPacket.ToBytes());
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// 向网关和gRPC订阅者广播游戏数据包
    /// </summary>
    private void OnBroadcastToPlayers(IReadOnlyList<int> playerIds, byte[] packet)
    {
        // Forward to Gateway for broadcast
        _ = _gatewayCallback.BroadcastGamePacketAsync(new BroadcastGamePacketRequest
        {
            PlayerIds = { playerIds },
            PacketData = ByteString.CopyFrom(packet)
        });

        // Forward to gRPC subscribers
        List<KeyValuePair<int, IServerStreamWriter<GameStateBroadcast>>> targetWriters;
        lock (_subscriptionLock)
        {
            targetWriters = playerIds
                .Where(id => _subscriptions.ContainsKey(id))
                .Select(id => new KeyValuePair<int, IServerStreamWriter<GameStateBroadcast>>(id, _subscriptions[id]))
                .ToList();
        }

        foreach (var (_, writer) in targetWriters)
        {
            _ = WriteGameStateAsync(writer, playerIds, packet);
        }
    }

    /// <summary>
    /// 向单个订阅者写入游戏状态广播
    /// </summary>
    private async Task WriteGameStateAsync(
        IServerStreamWriter<GameStateBroadcast> writer,
        IReadOnlyList<int> playerIds,
        byte[] packet)
    {
        try
        {
            await writer.WriteAsync(new GameStateBroadcast
            {
                PlayerIds = { playerIds },
                PacketData = ByteString.CopyFrom(packet)
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameGrpc] Failed to write game state to subscriber: {ex.Message}");
        }
    }

    /// <summary>
    /// 会话结束时通知网关
    /// </summary>
    private void OnSessionCompleted(IReadOnlyList<int> playerIds, string gameMode)
    {
        _ = _gatewayCallback.NotifySessionCompletedAsync(new NotifySessionCompletedRequest
        {
            PlayerIds = { playerIds },
            GameMode = gameMode
        });

        Console.WriteLine(
            $"[GameGrpc] Session completed for players [{string.Join(", ", playerIds)}], mode={gameMode}");
    }
}