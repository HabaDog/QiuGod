using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GameNetworkCommon;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using ProtoBuf;

namespace MyGateway;

/// <summary>
/// Gateway 进程：客户端连接管理、鉴权、消息路由。
/// 不包含任何业务逻辑——Lobby/Match/Game 逻辑通过 gRPC 调用下游服务。
/// </summary>
public class GatewayServer
{
    private const int MaxTcpPacketBodyBytes = 64 * 1024;
    private const int MaxUdpPacketBodyBytes = 60 * 1024;
    private static readonly TimeSpan DownstreamRpcTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionResumeGracePeriod = TimeSpan.FromMinutes(1);
    private static int _nextLanPlayerId = 100000;

    private TcpListener? _tcpListener;
    private UdpClient? _udpClient;
    private bool _isRunning;
    private CancellationTokenSource? _cancellationTokenSource;
    private int _udpPort;

    private readonly GatewaySessionRegistry _gatewaySessions = new(SessionResumeGracePeriod);
    private readonly GatewayMessageRouter _messageRouter = new();
    private readonly IAccountService _accountService;

    // 连接管理
    private readonly Dictionary<NetworkStream, int> _connectionToPlayerId = new();
    private readonly object _connectionLock = new();
    private readonly Dictionary<int, CancellationTokenSource> _disconnectCleanup = new();
    private readonly TimeSpan _resumeGracePeriod = SessionResumeGracePeriod;
    private readonly bool _matchmakingEnabled;
    private readonly bool _roomOnlyMode;

    // gRPC 客户端
    private readonly MyDemo.InterServer.LobbyService.LobbyServiceClient _lobbyClient;
    private readonly MyDemo.InterServer.MatchService.MatchServiceClient _matchClient;
    private readonly MyDemo.InterServer.GameService.GameServiceClient _gameClient;

    // 游戏状态订阅（GameServer → Gateway 推送）
    private readonly ConcurrentDictionary<string, IDisposable> _gameStateSubscriptions = new();

    // 匹配结果监听
    private readonly ConcurrentDictionary<int, IDisposable> _matchWatchSubscriptions = new();

    // UDP 日志限流
    private readonly Dictionary<int, DateTime> _lastUdpInputLogUtcByPlayer = new();

    /// <summary>
    /// 初始化网关服务器，配置 gRPC 客户端并注册消息路由
    /// </summary>
    public GatewayServer(IAccountService? accountService, IConfiguration configuration)
    {
        _accountService = accountService ?? new AccountService();
        _matchmakingEnabled = configuration.GetValue("Features:MatchmakingEnabled", true);
        _roomOnlyMode = configuration.GetValue("Features:RoomOnly", false);

        var lobbyAddress = configuration["Services:Lobby"] ?? "http://localhost:5001";
        var matchAddress = configuration["Services:Match"] ?? "http://localhost:5002";
        var gameAddress = configuration["Services:GameServer"] ?? "http://localhost:5003";

        _lobbyClient = new MyDemo.InterServer.LobbyService.LobbyServiceClient(
            GrpcChannel.ForAddress(lobbyAddress, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true
                }
            }));
        _matchClient = new MyDemo.InterServer.MatchService.MatchServiceClient(
            GrpcChannel.ForAddress(matchAddress, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true
                }
            }));
        _gameClient = new MyDemo.InterServer.GameService.GameServiceClient(
            GrpcChannel.ForAddress(gameAddress, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true
                }
            }));

        RegisterGatewayRoutes();
    }

    /// <summary>
    /// 注册所有协议消息路由及其对应的处理器
    /// </summary>
    private void RegisterGatewayRoutes()
    {
        // 不需要认证的协议
        _messageRouter.Register(Protocols.LoginRequest, false, HandleLoginAsync);
        _messageRouter.Register(Protocols.RegisterRequest, false, HandleRegisterAsync);
        _messageRouter.Register(Protocols.HeartbeatRequest, false, context =>
        {
            HandleTcpHeartbeat(context.Message, context.Stream);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.ResumeSessionRequest, false, context =>
        {
            return new ValueTask(HandleResumeSession(context.Message, context.Stream));
        });

        // 需要认证的协议 - 大厅
        _messageRouter.Register(Protocols.LogoutRequest, true, context =>
        {
            HandleLogout(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.EnterRoomRequest, true, context =>
        {
            return new ValueTask(HandleEnterRoomAsync(context.Message, context.Stream, context.Session));
        });
        _messageRouter.Register(Protocols.ToggleReadyRequest, true, context =>
        {
            return new ValueTask(HandleToggleReadyAsync(context.Message, context.Stream, context.Session));
        });
        _messageRouter.Register(Protocols.StartGameRequest, true, context =>
        {
            return new ValueTask(HandleStartGameAsync(context.Message, context.Stream, context.Session));
        });
        _messageRouter.Register(Protocols.SelectGameModeRequest, true, context =>
        {
            return new ValueTask(HandleSelectGameModeAsync(context.Message, context.Stream, context.Session));
        });
        _messageRouter.Register(Protocols.SelectGameThreeBallRequest, true, context =>
        {
            return new ValueTask(HandleSelectBallAsync(context.Message, context.Stream, context.Session));
        });
        _messageRouter.Register(Protocols.GameThreeCancelBallSelectionRequest, true, context =>
        {
            return new ValueTask(HandleCancelBallSelectionAsync(context.Message, context.Session));
        });
        _messageRouter.Register(Protocols.LobbySnapshotRequest, true, context =>
        {
            return new ValueTask(HandleLobbySnapshotAsync(context.Message, context.Stream, context.Session));
        });
        _messageRouter.Register(Protocols.CreateRoomRequest, true, context =>
        {
            return new ValueTask(HandleCreateRoomAsync(context.Message, context.Stream, context.Session));
        });
        _messageRouter.Register(Protocols.JoinRoomRequest, true, context =>
        {
            return new ValueTask(HandleJoinRoomAsync(context.Message, context.Stream, context.Session));
        });
        _messageRouter.Register(Protocols.LeaveRoomRequest, true, context =>
        {
            return new ValueTask(HandleLeaveRoomAsync(context.Message, context.Stream, context.Session));
        });
        if (!_roomOnlyMode)
        {
            _messageRouter.Register(Protocols.MatchmakingJoinRequest, true, context =>
            {
                return new ValueTask(HandleMatchmakingJoinAsync(context.Message, context.Stream, context.Session));
            });
            _messageRouter.Register(Protocols.MatchmakingCancelRequest, true, context =>
            {
                return new ValueTask(HandleMatchmakingCancelAsync(context.Message, context.Stream, context.Session));
            });
            _messageRouter.Register(Protocols.PartyCreateRequest, true, context =>
            {
                return new ValueTask(HandlePartyCreateAsync(context.Message, context.Stream, context.Session));
            });
            _messageRouter.Register(Protocols.PartyJoinRequest, true, context =>
            {
                return new ValueTask(HandlePartyJoinAsync(context.Message, context.Stream, context.Session));
            });
            _messageRouter.Register(Protocols.PartyLeaveRequest, true, context =>
            {
                return new ValueTask(HandlePartyLeaveAsync(context.Message, context.Stream, context.Session));
            });
        }

        // 游戏消息 - 转发到 GameServer
        _messageRouter.Register(Protocols.PlayerMoveRequest, true, context =>
        {
            return new ValueTask(ForwardToGameServer(context.Message, context.Session));
        });

        // GameThree 消息模块 - 转发到 GameServer
        var moduleServices = new GatewayModuleServices(
            (playerId, data) =>
            {
                _ = ForwardToGameServerAsync(playerId, data);
            },
            _ => { }); // NotifyPlayerReturnedToRoom 由 Gateway 自行处理
        new GameThreeGatewayMessageModule().Register(_messageRouter, moduleServices);
    }

    // ============================================================
    // 服务器启动/停止
    // ============================================================

    /// <summary>
    /// 启动网关服务器，监听 TCP 和 UDP 端口
    /// </summary>
    public void Start(string ip = "127.0.0.1", int tcpPort = 8888, int udpPort = 8889)
    {
        try
        {
            Console.WriteLine("=== Gateway 启动中 ===");
            _udpPort = udpPort;
            _cancellationTokenSource = new CancellationTokenSource();
            _isRunning = true;

            IPAddress localAddr = IPAddress.Parse(ip);
            _tcpListener = new TcpListener(localAddr, tcpPort);
            _tcpListener.Start();
            Console.WriteLine($"TCP 已启动: {ip}:{tcpPort}");

            _udpClient = new UdpClient(new IPEndPoint(localAddr, _udpPort));
            Console.WriteLine($"UDP 已启动: {ip}:{_udpPort}");

            Task.Run(() => AcceptClients(), _cancellationTokenSource.Token);
            Task.Run(() => ReceiveUdpMessages(), _cancellationTokenSource.Token);

            Console.WriteLine("=== Gateway 就绪 ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Gateway 启动失败: {ex.Message}");
            Stop();
            throw;
        }
    }

    /// <summary>
    /// 停止网关服务器，释放所有连接和资源
    /// </summary>
    public void Stop()
    {
        Console.WriteLine("\n停止 Gateway...");
        _isRunning = false;
        _cancellationTokenSource?.Cancel();

        try
        {
            _tcpListener?.Stop();
            _udpClient?.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"停止异常: {ex.Message}");
        }

        lock (_connectionLock)
        {
            foreach (var cleanup in _disconnectCleanup.Values) cleanup.Cancel();
            _disconnectCleanup.Clear();
            _connectionToPlayerId.Clear();
        }
        _gatewaySessions.DisposeAll();

        Console.WriteLine("Gateway 已完全停止。");
    }

    // ============================================================
    // 客户端连接处理
    // ============================================================

    /// <summary>
    /// 循环接受 TCP 客户端连接
    /// </summary>
    private async Task AcceptClients()
    {
        var listener = _tcpListener;
        var cts = _cancellationTokenSource;
        if (listener is null || cts is null) return;

        while (_isRunning && !cts.Token.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cts.Token);
                Console.WriteLine($"客户端已连接: {client.Client.RemoteEndPoint}");
                _ = HandleClientAsync(client);
            }
            catch (Exception ex)
            {
                if (_isRunning) Console.WriteLine($"接受连接异常: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 处理单个 TCP 客户端连接的消息收发
    /// </summary>
    private async Task HandleClientAsync(TcpClient client)
    {
        var session = _gatewaySessions.Register(client);
        NetworkStream stream = session.Stream;
        byte[] buffer = new byte[4096];
        var frameDecoder = new MessageFrameDecoder(MaxTcpPacketBodyBytes);
        Task dispatchTail = Task.CompletedTask;

        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, _cancellationTokenSource?.Token ?? CancellationToken.None)) != 0)
            {
                frameDecoder.Append(buffer, 0, bytesRead);

                try
                {
                    while (frameDecoder.TryRead(out var receivedMsg))
                    {
                        if (receivedMsg.ProtocolId != Protocols.GameThreeInput
                            && receivedMsg.ProtocolId != Protocols.PlayerMoveRequest)
                        {
                            Console.WriteLine($"TCP 消息 -> Protocol: {receivedMsg.ProtocolId}");
                        }

                        // Keep transport progress independent from slow business RPCs.
                        // Heartbeats are handled inline; business handlers run with
                        // their own exception boundary so a Lobby/GameServer call
                        // cannot block the TCP reader or take down the connection.
                        if (receivedMsg.ProtocolId == Protocols.HeartbeatRequest)
                        {
                            HandleTcpHeartbeat(receivedMsg, stream);
                        }
                        else
                        {
                            // Keep business requests ordered per connection while
                            // allowing the reader to continue servicing heartbeats.
                            var queuedMessage = receivedMsg;
                            dispatchTail = dispatchTail
                                .ContinueWith(
                                    _ => DispatchMessageAsync(queuedMessage, stream, session),
                                    CancellationToken.None,
                                    TaskContinuationOptions.ExecuteSynchronously,
                                    TaskScheduler.Default)
                                .Unwrap();
                        }
                    }
                }
                catch (InvalidDataException exception)
                {
                    Console.WriteLine($"[Gateway] 丢弃 TCP 会话 {session.SessionId}: {exception.Message}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"客户端连接异常断开: {ex.Message}");
        }
        finally
        {
            DetachPlayerConnection(stream);
            var detachedPlayerId = await _gatewaySessions.RemoveAsync(stream);
            if (detachedPlayerId.HasValue)
                SchedulePlayerExpiration(detachedPlayerId.Value);
            Console.WriteLine("客户端连接已关闭。");
        }
    }

    private async Task DispatchMessageAsync(
        BaseMessage message,
        NetworkStream stream,
        GatewaySession session)
    {
        try
        {
            var dispatchResult = await _messageRouter.DispatchAsync(
                new GatewayMessageContext(message, stream, session));
            if (dispatchResult == GatewayDispatchResult.UnknownProtocol)
            {
                Console.WriteLine($"未知协议 {message.ProtocolId}");
            }
            else if (dispatchResult == GatewayDispatchResult.AuthenticationRequired)
            {
                Console.WriteLine($"[Gateway] 未认证协议 {message.ProtocolId} 被拒绝");
            }
        }
        catch (OperationCanceledException)
        {
            // The connection was closed while the handler was waiting on a
            // downstream RPC.
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"[Gateway] 协议 {message.ProtocolId} 处理失败 " +
                $"(session={session.SessionId}): {exception.Message}");
        }
    }

    // ============================================================
    // UDP 消息处理
    // ============================================================

    /// <summary>
    /// 循环接收 UDP 消息
    /// </summary>
    private async Task ReceiveUdpMessages()
    {
        var udpClient = _udpClient;
        var cts = _cancellationTokenSource;
        if (udpClient is null || cts is null) return;

        while (_isRunning && !cts.Token.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await udpClient.ReceiveAsync(cts.Token);
                ProcessUdpMessage(result.Buffer, result.RemoteEndPoint);
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    Console.WriteLine($"[UDP] 接收异常: {ex.Message}");
                    await Task.Delay(100);
                }
            }
        }
    }

    /// <summary>
    /// 解析并路由处理收到的 UDP 消息
    /// </summary>
    private void ProcessUdpMessage(byte[] receivedData, IPEndPoint clientEndPoint)
    {
        if (receivedData.Length < BaseMessage.HeaderSize) return;

        try
        {
            BaseMessage udpMsg = BaseMessage.FromBytes(receivedData, 0, receivedData.Length);

            if (udpMsg.ProtocolId == Protocols.UdpBindRequest)
            {
                HandleUdpBind(udpMsg, clientEndPoint);
                return;
            }

            if (!_gatewaySessions.IsUdpEndPointAuthorized(clientEndPoint))
            {
                return;
            }

            switch (udpMsg.ProtocolId)
            {
                case Protocols.HeartbeatRequest:
                    HandleUdpHeartbeat(udpMsg, clientEndPoint);
                    break;
                case Protocols.PlayerMoveRequest:
                    HandlePlayerMove(udpMsg, clientEndPoint);
                    break;
                case Protocols.GameThreeInput:
                    DispatchAuthenticatedUdpMessage(udpMsg, clientEndPoint);
                    break;
                default:
                    Console.WriteLine($"[UDP] 未知协议: {udpMsg.ProtocolId}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UDP] 消息解析失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 将已认证的 UDP 消息分发给对应的处理器
    /// </summary>
    private void DispatchAuthenticatedUdpMessage(BaseMessage udpMsg, IPEndPoint clientEndPoint)
    {
        if (!_gatewaySessions.TryGetSessionByUdpEndPoint(clientEndPoint, out var session) || session is null)
            return;

        try
        {
            if (udpMsg.ProtocolId == Protocols.GameThreeInput)
            {
                // 转发给 GameServer
                _ = ForwardToGameServer(udpMsg, session);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Gateway] UDP 分发异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理 UDP 绑定请求，建立客户端 UDP 端点与玩家会话的关联
    /// </summary>
    private void HandleUdpBind(BaseMessage bindMessage, IPEndPoint clientEndPoint)
    {
        var udpClient = _udpClient;
        if (udpClient is null) return;

        var request = MessageCodec.Decode<UdpBindRequest>(bindMessage);
        var success = !string.IsNullOrWhiteSpace(request.SessionToken)
            && _gatewaySessions.TryBindUdpEndPoint(request.SessionToken, request.PlayerId, clientEndPoint);

        var response = new UdpBindResponse { Success = success, Message = success ? "UDP 绑定成功" : "Token 无效" };
        var packet = MessageCodec.EncodePacket(Protocols.UdpBindResponse, response);
        udpClient.Send(packet, packet.Length, clientEndPoint);
    }

    /// <summary>
    /// 处理 UDP 心跳请求并返回服务器时间戳
    /// </summary>
    private void HandleUdpHeartbeat(BaseMessage heartbeatMsg, IPEndPoint clientEndPoint)
    {
        var udpClient = _udpClient;
        if (udpClient is null) return;

        try
        {
            var request = MessageCodec.Decode<HeartbeatRequest>(heartbeatMsg);
            var response = new HeartbeatResponse
            {
                ClientTimestamp = request.ClientTimestamp,
                ServerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Sequence = request.Sequence
            };
            var packet = MessageCodec.EncodePacket(Protocols.HeartbeatResponse, response);
            udpClient.Send(packet, packet.Length, clientEndPoint);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UDP] 心跳异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理 UDP 玩家移动消息并返回确认响应
    /// </summary>
    private void HandlePlayerMove(BaseMessage moveMsg, IPEndPoint clientEndPoint)
    {
        var udpClient = _udpClient;
        if (udpClient is null) return;

        var request = MessageCodec.Decode<PlayerMove>(moveMsg);
        if (!_gatewaySessions.IsUdpPlayerAuthorized(clientEndPoint, request.PlayerId)) return;

        var response = new BaseMessage(Protocols.PlayerMoveResponse, new byte[] { 0x01 });
        udpClient.Send(response.ToBytes(), response.ToBytes().Length, clientEndPoint);
    }

    // ============================================================
    // 协议处理器 - 账号
    // ============================================================

    /// <summary>
    /// 处理局域网玩家进入请求。
    ///
    /// LAN 版本不依赖账号数据库：客户端把 LoginRequest.Username 当作昵称，
    /// 服务端为本次连接分配临时玩家 ID 和会话令牌。保留原协议编号是为了
    /// 兼容已有客户端和序列化代码，密码字段在 LAN 模式下会被忽略。
    /// </summary>
    private async ValueTask HandleLoginAsync(GatewayMessageContext context)
    {
        try
        {
            var request = MessageCodec.Decode<LoginRequest>(context.Message);
            var nickname = NormalizeLanNickname(request.Username);
            var playerId = Interlocked.Increment(ref _nextLanPlayerId);
            var sessionToken = _gatewaySessions.IssueSessionToken(context.Stream, playerId);
            CancelPlayerExpiration(playerId);
            BindPlayerConnection(context.Stream, playerId);
            Console.WriteLine($"[TCP] LAN 玩家进入: {nickname} (临时 ID {playerId})");

            // 查询游戏恢复状态
            int slotIndex = -1;
            int playerCount = 0;
            string gameMode = string.Empty;
            string sceneName = string.Empty;
            bool isInGame = false;
            if (playerId > 0)
            {
                try
                {
                    var resumeState = await _gameClient.GetResumeStateAsync(new MyDemo.InterServer.GetResumeStateRequest { PlayerId = playerId });
                    isInGame = resumeState.HasActiveSession;
                    slotIndex = resumeState.SlotIndex;
                    playerCount = resumeState.PlayerCount;
                    gameMode = resumeState.GameMode;
                    sceneName = resumeState.SceneName;
                }
                catch { }
            }

            SendResponse(context.Stream, Protocols.LoginResponse, new LoginResponse
            {
                Success = true,
                PlayerId = playerId,
                Message = $"欢迎，{nickname}",
                SessionToken = sessionToken,
                IsInGame = isInGame,
                SlotIndex = slotIndex,
                PlayerCount = playerCount,
                GameMode = gameMode,
                SceneName = sceneName
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 登录异常: {ex.Message}");
            SendResponse(context.Stream, Protocols.LoginResponse,
                new LoginResponse { Success = false, Message = "内部错误" });
        }
    }

    /// <summary>
    /// 处理玩家注册请求
    /// </summary>
    private ValueTask HandleRegisterAsync(GatewayMessageContext context)
    {
        try
        {
            SendResponse(context.Stream, Protocols.RegisterResponse, new RegisterResponse
            {
                Success = false,
                Message = "LAN mode does not support account registration. Enter a nickname instead."
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 注册异常: {ex.Message}");
            SendResponse(context.Stream, Protocols.RegisterResponse,
                new RegisterResponse { Success = false, Message = "内部错误" });
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 处理 TCP 心跳请求并返回服务器时间戳
    /// </summary>
    private void HandleTcpHeartbeat(BaseMessage message, NetworkStream stream)
    {
        try
        {
            var request = MessageCodec.Decode<HeartbeatRequest>(message);
            SendResponse(stream, Protocols.HeartbeatResponse, new HeartbeatResponse
            {
                ClientTimestamp = request.ClientTimestamp,
                ServerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Sequence = request.Sequence
            });
        }
        catch (Exception ex) { Console.WriteLine($"[TCP] 心跳异常: {ex.Message}"); }
    }

    /// <summary>
    /// 处理玩家登出请求，释放会话资源
    /// </summary>
    private void HandleLogout(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<LogoutRequest>(message);
            if (session.PlayerId != request.PlayerId) throw new InvalidOperationException("玩家不匹配");

            SendResponse(stream, Protocols.LogoutResponse, new LogoutResponse { Success = true, Message = "已登出" });

            if (_gatewaySessions.DetachForLogout(stream, request.PlayerId, out _))
            {
                lock (_connectionLock) { _connectionToPlayerId.Remove(stream); }
                SchedulePlayerExpiration(request.PlayerId);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 登出异常: {ex.Message}");
            SendResponse(stream, Protocols.LogoutResponse, new LogoutResponse { Success = false, Message = "登出失败" });
        }
    }

    // ============================================================
    // 协议处理器 - 大厅 (通过 gRPC 调用 Lobby Server)
    // ============================================================

    /// <summary>
    /// 处理进入房间请求，通过 gRPC 调用 Lobby 服务
    /// </summary>
    private async Task HandleEnterRoomAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<EnterRoomRequest>(message);
            if (session.PlayerId != request.PlayerId) throw new InvalidOperationException("玩家不匹配");

            BindPlayerConnection(stream, request.PlayerId);

            var lobbyResponse = await _lobbyClient.EnterRoomAsync(new MyDemo.InterServer.EnterRoomRequest
            {
                PlayerId = request.PlayerId,
                Username = request.Username
            });

            SendResponse(stream, Protocols.EnterRoomResponse, new EnterRoomResponse
            {
                Success = lobbyResponse.Success,
                Message = lobbyResponse.Message,
                Players = lobbyResponse.Players.Select(p => new RoomPlayerInfo
                {
                    PlayerId = p.PlayerId,
                    Username = p.Username,
                    IsReady = p.IsReady,
                    IsHost = p.IsHost,
                    SelectedBallId = p.SelectedBallId
                }).ToList(),
                GameMode = lobbyResponse.GameMode
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 进入房间异常: {ex.Message}");
            SendResponse(stream, Protocols.EnterRoomResponse,
                new EnterRoomResponse { Success = false, Message = "内部错误" });
        }
    }

    /// <summary>
    /// 处理玩家准备状态切换请求
    /// </summary>
    private async Task HandleToggleReadyAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<ToggleReadyRequest>(message);
            if (session.PlayerId != request.PlayerId) throw new InvalidOperationException("玩家不匹配");

            var lobbyResponse = await _lobbyClient.ToggleReadyAsync(new MyDemo.InterServer.ToggleReadyRequest
            {
                PlayerId = request.PlayerId,
                Ready = request.Ready
            });

            SendResponse(stream, Protocols.ToggleReadyResponse, new ToggleReadyResponse
            {
                Success = lobbyResponse.Success,
                Message = lobbyResponse.Message
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 准备切换异常: {ex.Message}");
            SendResponse(stream, Protocols.ToggleReadyResponse,
                new ToggleReadyResponse { Success = false, Message = "内部错误" });
        }
    }

    /// <summary>
    /// 处理开始游戏请求，创建游戏会话并通知所有玩家
    /// </summary>
    private async Task HandleStartGameAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<StartGameRequest>(message);
            if (session.PlayerId != request.PlayerId) throw new InvalidOperationException("玩家不匹配");

            var lobbyResponse = await _lobbyClient.StartGameAsync(new MyDemo.InterServer.StartGameRequest
            {
                PlayerId = request.PlayerId,
                GameMode = request.GameMode
            });

            if (lobbyResponse.Success)
            {
                if (lobbyResponse.NeedsBallSelection)
                {
                    // Gateway is the single owner of client-facing scene
                    // notifications for the selection flow.
                    for (int i = 0; i < lobbyResponse.PlayerIds.Count; i++)
                    {
                        var notification = new GameStartNotification
                        {
                            Success = true,
                            Message = "选择你的球",
                            YourSlotIndex = i,
                            GameMode = lobbyResponse.GameMode,
                            SceneName = "BallSelection"
                        };
                        SendPacketToPlayer(lobbyResponse.PlayerIds[i],
                            MessageCodec.EncodePacket(Protocols.GameStartNotification, notification));
                    }
                }
                else
                {
                    // 直接开始游戏，调用 GameServer 创建会话
                    var gameResponse = await _gameClient.CreateSessionAsync(new MyDemo.InterServer.CreateGameSessionRequest
                    {
                        PlayerIds = { lobbyResponse.PlayerIds },
                        GameMode = lobbyResponse.GameMode,
                        BallSelections = { lobbyResponse.BallSelections }
                    });

                    if (gameResponse.Success)
                    {
                        for (int i = 0; i < lobbyResponse.PlayerIds.Count; i++)
                        {
                            var notification = new GameStartNotification
                            {
                                Success = true,
                                Message = "游戏开始!",
                                YourSlotIndex = gameResponse.SlotIndices.TryGetValue(lobbyResponse.PlayerIds[i], out var slot) ? slot : i,
                                GameMode = lobbyResponse.GameMode,
                                SceneName = gameResponse.SceneName
                            };
                            SendPacketToPlayer(lobbyResponse.PlayerIds[i],
                                MessageCodec.EncodePacket(Protocols.GameStartNotification, notification));
                        }
                    }
                }
            }

            SendResponse(stream, Protocols.StartGameResponse, new StartGameResponse
            {
                Success = lobbyResponse.Success,
                Message = lobbyResponse.Message
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 开始游戏异常: {ex.Message}");
            SendResponse(stream, Protocols.StartGameResponse,
                new StartGameResponse { Success = false, Message = "内部错误" });
        }
    }

    /// <summary>
    /// 处理选择游戏模式请求
    /// </summary>
    private async Task HandleSelectGameModeAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<SelectGameModeRequest>(message);
            if (session.PlayerId != request.PlayerId) throw new InvalidOperationException("玩家不匹配");

            var lobbyResponse = await _lobbyClient.SelectGameModeAsync(new MyDemo.InterServer.SelectGameModeRequest
            {
                PlayerId = request.PlayerId,
                GameMode = request.GameMode
            });

            SendResponse(stream, Protocols.SelectGameModeResponse, new SelectGameModeResponse
            {
                Success = lobbyResponse.Success,
                Message = lobbyResponse.Message,
                GameMode = lobbyResponse.GameMode
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 选择模式异常: {ex.Message}");
            SendResponse(stream, Protocols.SelectGameModeResponse,
                new SelectGameModeResponse { Success = false, Message = "无法选择模式" });
        }
    }

    /// <summary>
    /// 处理选球请求，全部确认后创建游戏会话
    /// </summary>
    private async Task HandleSelectBallAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<SelectGameThreeBallRequest>(message);
            if (session.PlayerId != request.PlayerId) throw new InvalidOperationException("玩家不匹配");

            var lobbyResponse = await _lobbyClient.SelectBallAsync(new MyDemo.InterServer.SelectBallRequest
            {
                PlayerId = request.PlayerId,
                BallId = request.BallId
            });

            if (lobbyResponse.Success && lobbyResponse.AllConfirmed)
            {
                // 所有玩家选球完毕，创建游戏会话
                var gameResponse = await _gameClient.CreateSessionAsync(new MyDemo.InterServer.CreateGameSessionRequest
                {
                    PlayerIds = { lobbyResponse.PlayerIds },
                    GameMode = lobbyResponse.GameMode,
                    BallSelections = { lobbyResponse.BallSelections }
                });

                if (gameResponse.Success)
                {
                    for (int i = 0; i < lobbyResponse.PlayerIds.Count; i++)
                    {
                        var notification = new GameStartNotification
                        {
                            Success = true,
                            Message = "游戏开始!",
                            YourSlotIndex = gameResponse.SlotIndices.TryGetValue(lobbyResponse.PlayerIds[i], out var slot) ? slot : i,
                            GameMode = lobbyResponse.GameMode,
                            SceneName = gameResponse.SceneName
                        };
                        SendPacketToPlayer(lobbyResponse.PlayerIds[i],
                            MessageCodec.EncodePacket(Protocols.GameStartNotification, notification));
                    }
                }
                else
                {
                    SendResponse(stream, Protocols.SelectGameThreeBallResponse,
                        new SelectGameThreeBallResponse
                        {
                            Success = false,
                            Message = string.IsNullOrWhiteSpace(gameResponse.Message)
                                ? "游戏会话创建失败"
                                : gameResponse.Message,
                            BallId = lobbyResponse.BallId
                        });
                    return;
                }
            }

            SendResponse(stream, Protocols.SelectGameThreeBallResponse, new SelectGameThreeBallResponse
            {
                Success = lobbyResponse.Success,
                Message = lobbyResponse.Message,
                BallId = lobbyResponse.BallId
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 选球异常: {ex.Message}");
            SendResponse(stream, Protocols.SelectGameThreeBallResponse,
                new SelectGameThreeBallResponse { Success = false, Message = "无法选球" });
        }
    }

    /// <summary>
    /// 处理取消选球请求，通知相关玩家返回房间
    /// </summary>
    private async Task HandleCancelBallSelectionAsync(BaseMessage message, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<GameThreeCancelBallSelectionRequest>(message);
            if (session.PlayerId != request.PlayerId) return;

            var lobbyResponse = await _lobbyClient.CancelBallSelectionAsync(new MyDemo.InterServer.CancelBallSelectionRequest
            {
                PlayerId = request.PlayerId
            });

            if (lobbyResponse.Success && lobbyResponse.AffectedPlayerIds.Count > 0)
            {
                for (int i = 0; i < lobbyResponse.AffectedPlayerIds.Count; i++)
                {
                    var notification = new GameStartNotification
                    {
                        Success = true,
                        Message = "选球已取消",
                        YourSlotIndex = -1,
                        GameMode = lobbyResponse.GameMode,
                        SceneName = lobbyResponse.ReturnSceneName
                    };
                    SendPacketToPlayer(lobbyResponse.AffectedPlayerIds[i],
                        MessageCodec.EncodePacket(Protocols.GameStartNotification, notification));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 取消选球异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理大厅快照请求，返回当前房间和匹配状态
    /// </summary>
    private async Task HandleLobbySnapshotAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<LobbySnapshotRequest>(message);
        if (session.PlayerId != request.PlayerId)
        {
            SendResponse(stream, Protocols.LobbySnapshotResponse,
                new LobbySnapshotResponse { Success = false, Message = "玩家不匹配" });
            return;
        }

        try
        {
            var lobbyResponse = await _lobbyClient.GetLobbySnapshotAsync(new MyDemo.InterServer.LobbySnapshotRequest
            {
                PlayerId = request.PlayerId
            });

            var response = new LobbySnapshotResponse
            {
                Success = lobbyResponse.Success,
                Message = lobbyResponse.Message,
                IsMatchmaking = lobbyResponse.IsMatchmaking,
                QueuePosition = lobbyResponse.QueuePosition,
                Rooms = lobbyResponse.Rooms.Select(r => new LobbyRoomInfo
                {
                    RoomId = r.RoomId,
                    RoomName = r.RoomName,
                    GameMode = r.GameMode,
                    PlayerCount = r.PlayerCount,
                    MaxPlayers = r.MaxPlayers,
                    IsPrivate = r.IsPrivate
                }).ToList()
            };
            SendResponse(stream, Protocols.LobbySnapshotResponse, response);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 大厅快照异常: {ex.Message}");
            SendResponse(stream, Protocols.LobbySnapshotResponse,
                new LobbySnapshotResponse { Success = false, Message = "内部错误" });
        }
    }

    /// <summary>
    /// 处理创建房间请求
    /// </summary>
    private async Task HandleCreateRoomAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<CreateRoomRequest>(message);
        try
        {
            var lobbyResponse = await _lobbyClient.CreateRoomAsync(new MyDemo.InterServer.CreateRoomRequest
            {
                PlayerId = request.PlayerId,
                Username = request.Username,
                RoomName = request.RoomName,
                GameMode = request.GameMode,
                IsPrivate = request.IsPrivate,
                JoinCode = request.JoinCode
            }, deadline: DateTime.UtcNow + DownstreamRpcTimeout);

            SendResponse(stream, Protocols.CreateRoomResponse, new CreateRoomResponse
            {
                Success = lobbyResponse.Success,
                Message = lobbyResponse.Message,
                RoomId = lobbyResponse.RoomId,
                JoinCode = lobbyResponse.JoinCode
            });

            if (lobbyResponse.Success)
            {
                BindPlayerConnection(stream, request.PlayerId);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 创建房间异常: {ex.Message}");
            SendResponse(stream, Protocols.CreateRoomResponse,
                new CreateRoomResponse { Success = false, Message = "内部错误" });
        }
    }

    /// <summary>
    /// 处理加入房间请求
    /// </summary>
    private async Task HandleJoinRoomAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<JoinRoomRequest>(message);
        try
        {
            var lobbyResponse = await _lobbyClient.JoinRoomAsync(new MyDemo.InterServer.JoinRoomRequest
            {
                PlayerId = request.PlayerId,
                Username = request.Username,
                RoomId = request.RoomId,
                JoinCode = request.JoinCode
            });

            SendResponse(stream, Protocols.JoinRoomResponse, new JoinRoomResponse
            {
                Success = lobbyResponse.Success,
                Message = lobbyResponse.Message,
                RoomId = lobbyResponse.RoomId
            });

            if (lobbyResponse.Success) BindPlayerConnection(stream, request.PlayerId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 加入房间异常: {ex.Message}");
            SendResponse(stream, Protocols.JoinRoomResponse,
                new JoinRoomResponse { Success = false, Message = "内部错误" });
        }
    }

    /// <summary>
    /// 处理离开房间请求
    /// </summary>
    private async Task HandleLeaveRoomAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<LeaveRoomRequest>(message);
        try
        {
            var lobbyResponse = await _lobbyClient.LeaveRoomAsync(new MyDemo.InterServer.LeaveRoomRequest
            {
                PlayerId = request.PlayerId
            });

            SendResponse(stream, Protocols.LeaveRoomResponse, new LeaveRoomResponse
            {
                Success = lobbyResponse.Success,
                Message = lobbyResponse.Message
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 离开房间异常: {ex.Message}");
            SendResponse(stream, Protocols.LeaveRoomResponse,
                new LeaveRoomResponse { Success = false, Message = "内部错误" });
        }
    }

    // ============================================================
    // 协议处理器 - 匹配
    // ============================================================

    /// <summary>
    /// 处理加入匹配队列请求
    /// </summary>
    private async Task HandleMatchmakingJoinAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<MatchmakingJoinRequest>(message);
        if (!_matchmakingEnabled)
        {
            SendResponse(stream, Protocols.MatchmakingJoinResponse,
                new MatchmakingJoinResponse
                {
                    Success = false,
                    Message = "LAN mode does not support matchmaking. Create or join a room."
                });
            return;
        }

        try
        {
            BindPlayerConnection(stream, request.PlayerId);

            var matchResponse = await _matchClient.JoinQueueAsync(new MyDemo.InterServer.MatchJoinRequest
            {
                PlayerId = request.PlayerId,
                Username = request.Username,
                GameMode = request.GameMode
            });

            SendResponse(stream, Protocols.MatchmakingJoinResponse, new MatchmakingJoinResponse
            {
                Success = matchResponse.Success,
                Message = matchResponse.Message,
                MatchFound = matchResponse.MatchFound,
                RoomId = matchResponse.RoomId,
                QueuePosition = matchResponse.QueuePosition
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 匹配加入异常: {ex.Message}");
            SendResponse(stream, Protocols.MatchmakingJoinResponse,
                new MatchmakingJoinResponse { Success = false, Message = "内部错误" });
        }
    }

    /// <summary>
    /// 处理取消匹配队列请求
    /// </summary>
    private async Task HandleMatchmakingCancelAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<MatchmakingCancelRequest>(message);
        if (!_matchmakingEnabled)
        {
            SendResponse(stream, Protocols.MatchmakingCancelResponse,
                new MatchmakingCancelResponse
                {
                    Success = false,
                    Message = "LAN mode does not support matchmaking."
                });
            return;
        }

        try
        {
            var matchResponse = await _matchClient.CancelQueueAsync(new MyDemo.InterServer.MatchCancelRequest
            {
                PlayerId = request.PlayerId
            });

            SendResponse(stream, Protocols.MatchmakingCancelResponse, new MatchmakingCancelResponse
            {
                Success = matchResponse.Success,
                Message = matchResponse.Message
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 匹配取消异常: {ex.Message}");
            SendResponse(stream, Protocols.MatchmakingCancelResponse,
                new MatchmakingCancelResponse { Success = false, Message = "内部错误" });
        }
    }

    /// <summary>
    /// 处理创建组队请求
    /// </summary>
    private async Task HandlePartyCreateAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<PartyCreateRequest>(message);
        try
        {
            var lobbyResponse = await _lobbyClient.CreatePartyAsync(new MyDemo.InterServer.CreatePartyRequest
            {
                PlayerId = request.PlayerId,
                Username = request.Username
            });

            var response = new PartyCreateResponse { Success = lobbyResponse.Success, Message = lobbyResponse.Message };
            if (lobbyResponse.Party is not null)
            {
                response.Party = new PartyInfo
                {
                    PartyId = lobbyResponse.Party.PartyId,
                    JoinCode = lobbyResponse.Party.JoinCode,
                    Members = lobbyResponse.Party.Members.Select(m => new PartyMemberInfo
                    {
                        PlayerId = m.PlayerId,
                        Username = m.Username,
                        IsLeader = m.IsLeader
                    }).ToList()
                };
            }
            SendResponse(stream, Protocols.PartyCreateResponse, response);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 创建组队异常: {ex.Message}");
            SendResponse(stream, Protocols.PartyCreateResponse,
                new PartyCreateResponse { Success = false, Message = "内部错误" });
        }
    }

    /// <summary>
    /// 处理加入组队请求
    /// </summary>
    private async Task HandlePartyJoinAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<PartyJoinRequest>(message);
        try
        {
            var lobbyResponse = await _lobbyClient.JoinPartyAsync(new MyDemo.InterServer.JoinPartyRequest
            {
                PlayerId = request.PlayerId,
                Username = request.Username,
                JoinCode = request.JoinCode
            });

            var response = new PartyJoinResponse { Success = lobbyResponse.Success, Message = lobbyResponse.Message };
            if (lobbyResponse.Party is not null)
            {
                response.Party = new PartyInfo
                {
                    PartyId = lobbyResponse.Party.PartyId,
                    JoinCode = lobbyResponse.Party.JoinCode,
                    Members = lobbyResponse.Party.Members.Select(m => new PartyMemberInfo
                    {
                        PlayerId = m.PlayerId,
                        Username = m.Username,
                        IsLeader = m.IsLeader
                    }).ToList()
                };
            }
            SendResponse(stream, Protocols.PartyJoinResponse, response);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 加入组队异常: {ex.Message}");
            SendResponse(stream, Protocols.PartyJoinResponse,
                new PartyJoinResponse { Success = false, Message = "内部错误" });
        }
    }

    /// <summary>
    /// 处理离开组队请求
    /// </summary>
    private async Task HandlePartyLeaveAsync(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<PartyLeaveRequest>(message);
        try
        {
            var lobbyResponse = await _lobbyClient.LeavePartyAsync(new MyDemo.InterServer.LeavePartyRequest
            {
                PlayerId = request.PlayerId
            });
            SendResponse(stream, Protocols.PartyLeaveResponse, new PartyLeaveResponse
            {
                Success = lobbyResponse.Success,
                Message = lobbyResponse.Message
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 离开组队异常: {ex.Message}");
            SendResponse(stream, Protocols.PartyLeaveResponse,
                new PartyLeaveResponse { Success = false, Message = "内部错误" });
        }
    }

    /// <summary>
    /// 处理会话恢复请求，用于断线重连
    /// </summary>
    private async Task HandleResumeSession(BaseMessage message, NetworkStream stream)
    {
        try
        {
            var request = MessageCodec.Decode<ResumeSessionRequest>(message);
            var success = _gatewaySessions.TryResumeSession(stream, request.SessionToken, request.PlayerId, out var resumedSession);

            if (success && resumedSession is not null)
            {
                BindPlayerConnection(stream, request.PlayerId);
                CancelPlayerExpiration(request.PlayerId);
            }

            SendResponse(stream, Protocols.ResumeSessionResponse, new ResumeSessionResponse
            {
                Success = success,
                Message = success ? "会话已恢复" : "Token 无效或已过期",
                PlayerId = success ? request.PlayerId : 0,
                SessionToken = success ? request.SessionToken : string.Empty
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] 恢复会话异常: {ex.Message}");
        }
    }

    // ============================================================
    // 游戏消息转发
    // ============================================================

    /// <summary>
    /// 将游戏消息转发到 GameServer
    /// </summary>
    private async Task ForwardToGameServer(BaseMessage message, GatewaySession session)
    {
        if (!session.PlayerId.HasValue) return;
        await ForwardToGameServerAsync(session.PlayerId.Value, message.ToBytes());
    }

    /// <summary>
    /// 通过 gRPC 将玩家输入数据异步转发到 GameServer
    /// </summary>
    private async Task ForwardToGameServerAsync(int playerId, byte[] packetData)
    {
        try
        {
            await _gameClient.ForwardPlayerInputAsync(new MyDemo.InterServer.ForwardPlayerInputRequest
            {
                PlayerId = playerId,
                PacketData = Google.Protobuf.ByteString.CopyFrom(packetData)
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Gateway] 转发游戏消息失败: {ex.Message}");
        }
    }

    // ============================================================
    // 广播方法（供 GatewayGrpcService 回调使用）
    // ============================================================

    /// <summary>
    /// 向指定房间内的玩家广播房间状态数据
    /// </summary>
    public void BroadcastRoomStateInternal(int[] playerIds, byte[] roomStateData)
    {
        var packet = EncodeBroadcastPacket(Protocols.RoomStateUpdate, roomStateData);
        lock (_connectionLock)
        {
            foreach (var kvp in _connectionToPlayerId.Where(c => playerIds.Contains(c.Value)))
            {
                try { SendToClient(kvp.Key, packet); }
                catch (Exception ex) { Console.WriteLine($"广播房间状态失败: {ex.Message}"); }
            }
        }
    }

    /// <summary>
    /// 向所有已连接玩家广播大厅状态数据
    /// </summary>
    public void BroadcastLobbyStateInternal(byte[] lobbyStateData)
    {
        var packet = EncodeBroadcastPacket(Protocols.LobbyStateUpdate, lobbyStateData);
        List<(NetworkStream Stream, int PlayerId)> recipients;
        lock (_connectionLock)
        {
            recipients = _connectionToPlayerId.Select(c => (c.Key, c.Value)).ToList();
        }
        foreach (var recipient in recipients)
        {
            try { SendToClient(recipient.Stream, packet); }
            catch (Exception ex) { Console.WriteLine($"广播大厅状态失败: {ex.Message}"); }
        }
    }

    /// <summary>
    /// 向指定玩家广播场景切换通知
    /// </summary>
    public void BroadcastSceneNotificationInternal(int[] playerIds, string gameMode, string sceneName)
    {
        for (int i = 0; i < playerIds.Length; i++)
        {
            var notification = new GameStartNotification
            {
                Success = true,
                Message = sceneName == "Room" ? "选球已取消" : "选择你的球",
                YourSlotIndex = sceneName == "Room" ? -1 : i,
                GameMode = gameMode,
                SceneName = sceneName
            };
            SendPacketToPlayer(playerIds[i], MessageCodec.EncodePacket(Protocols.GameStartNotification, notification));
        }
    }

    /// <summary>
    /// 向指定玩家广播选球状态数据
    /// </summary>
    public void BroadcastBallSelectionStateInternal(int[] playerIds, byte[] selectionStateData)
    {
        var packet = EncodeBroadcastPacket(Protocols.GameThreeBallSelectionStateUpdate, selectionStateData);
        foreach (var playerId in playerIds)
        {
            SendPacketToPlayer(playerId, packet);
        }
    }

    private static string NormalizeLanNickname(string? value)
    {
        var nickname = string.IsNullOrWhiteSpace(value) ? "Player" : value.Trim();
        if (nickname.Length > 24)
        {
            nickname = nickname[..24];
        }
        return nickname;
    }

    /// <summary>
    /// 通知玩家匹配成功并进入选球阶段
    /// </summary>
    public void NotifyMatchFoundInternal(int[] playerIds, string roomId, string gameMode)
    {
        foreach (var playerId in playerIds)
        {
            var notification = new GameStartNotification
            {
                Success = true,
                Message = "匹配成功!",
                GameMode = gameMode,
                SceneName = "BallSelection"
            };
            SendPacketToPlayer(playerId, MessageCodec.EncodePacket(Protocols.GameStartNotification, notification));
        }
    }

    /// <summary>
    /// 向指定玩家广播游戏数据包
    /// </summary>
    public void BroadcastGamePacketInternal(int[] playerIds, byte[] packetData)
    {
        foreach (var playerId in playerIds)
        {
            SendPacketToPlayer(playerId, packetData);
        }
    }

    /// <summary>
    /// 通知玩家游戏会话已结束
    /// </summary>
    public void NotifySessionCompletedInternal(int[] playerIds, string gameMode)
    {
        // 通知玩家返回房间
        foreach (var playerId in playerIds)
        {
            SendPacketToPlayer(playerId, MessageCodec.EncodePacket(Protocols.GameThreeGameOver,
                new GameThreeGameOver { Reason = "游戏结束" }));
        }
    }

    // ============================================================
    // 辅助方法
    // ============================================================

    private static byte[] EncodeBroadcastPacket(ushort protocolId, byte[] body)
    {
        if (body is null) throw new ArgumentNullException(nameof(body));
        return new BaseMessage(protocolId, body).ToBytes();
    }

    /// <summary>
    /// 向客户端发送数据包，若会话不可用则静默忽略
    /// </summary>
    private void SendToClient(NetworkStream stream, byte[] packet)
    {
        if (!_gatewaySessions.TrySend(stream, packet))
        {
            // 客户端已断开连接，静默忽略，不抛出异常
            Console.WriteLine("[Gateway] 发送失败: 客户端已断开");
        }
    }

    /// <summary>
    /// 将响应对象编码后发送给客户端
    /// </summary>
    private void SendResponse<T>(NetworkStream stream, ushort protocolId, T response)
    {
        SendToClient(stream, MessageCodec.EncodePacket(protocolId, response));
    }

    /// <summary>
    /// 根据玩家 ID 查找连接并发送数据包
    /// </summary>
    private bool SendPacketToPlayer(int playerId, byte[] packet)
    {
        NetworkStream? targetStream = null;
        lock (_connectionLock)
        {
            foreach (var connection in _connectionToPlayerId)
            {
                if (connection.Value == playerId) { targetStream = connection.Key; break; }
            }
        }
        if (targetStream is not null)
        {
            try { SendToClient(targetStream, packet); return true; }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// 将网络流与玩家 ID 绑定，清理同一玩家的旧连接
    /// </summary>
    private void BindPlayerConnection(NetworkStream stream, int playerId)
    {
        lock (_connectionLock)
        {
            var staleStreams = _connectionToPlayerId
                .Where(c => c.Value == playerId && c.Key != stream)
                .Select(c => c.Key).ToArray();
            foreach (var stale in staleStreams) _connectionToPlayerId.Remove(stale);
            _connectionToPlayerId[stream] = playerId;
        }
    }

    /// <summary>
    /// 解除网络流与玩家 ID 的绑定关系
    /// </summary>
    private void DetachPlayerConnection(NetworkStream stream)
    {
        lock (_connectionLock) { _connectionToPlayerId.Remove(stream); }
    }

    /// <summary>
    /// 安排玩家断线后在宽限期内等待重连，超时后清理会话
    /// </summary>
    private void SchedulePlayerExpiration(int playerId)
    {
        CancellationTokenSource cleanup;
        lock (_connectionLock)
        {
            if (_disconnectCleanup.Remove(playerId, out var previous)) previous.Cancel();
            cleanup = new CancellationTokenSource();
            _disconnectCleanup[playerId] = cleanup;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_resumeGracePeriod, cleanup.Token);
                if (_gatewaySessions.ExpireDisconnectedPlayer(playerId))
                    Console.WriteLine($"[Gateway] 玩家 {playerId} 断线恢复超时");
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (_connectionLock)
                {
                    if (_disconnectCleanup.TryGetValue(playerId, out var current)
                        && ReferenceEquals(current, cleanup))
                        _disconnectCleanup.Remove(playerId);
                }
                cleanup.Dispose();
            }
        });
    }

    /// <summary>
    /// 取消玩家的断线过期计时器
    /// </summary>
    private void CancelPlayerExpiration(int playerId)
    {
        lock (_connectionLock)
        {
            if (_disconnectCleanup.Remove(playerId, out var cleanup)) cleanup.Cancel();
        }
    }
}
