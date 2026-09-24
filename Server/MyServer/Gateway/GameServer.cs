using GameNetworkCommon;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

public class GameServer
{
    private const int MaxTcpPacketBodyBytes = 64 * 1024;
    private const int MaxUdpPacketBodyBytes = 60 * 1024;
    private static readonly TimeSpan SessionResumeGracePeriod = TimeSpan.FromMinutes(1);

    private TcpListener? _tcpListener;
    private UdpClient? _udpClient;
    private bool _isRunning;
    private Thread? _serverThread;
    private CancellationTokenSource? _cancellationTokenSource;
    private int _udpPort;

    private readonly LobbyDirectoryService _lobbyDirectory = new();
    
    // 娣诲姞杩炴帴涓庣帺瀹禝D鐨勬槧灏?
    private readonly Dictionary<NetworkStream, int> _connectionToPlayerId = new Dictionary<NetworkStream, int>();
    private readonly object _connectionLock = new object();
    private readonly GatewaySessionRegistry _gatewaySessions = new(SessionResumeGracePeriod);
    private readonly GatewayMessageRouter _messageRouter = new();
    private readonly GameSessionService _gameSessionService;
    private readonly IAccountService _accountService;
    private readonly Dictionary<int, CancellationTokenSource> _disconnectCleanup = new();
    private readonly TimeSpan _resumeGracePeriod = SessionResumeGracePeriod;
    private readonly object _ballSelectionLock = new();
    private readonly Dictionary<int, PendingBallSelection> _pendingBallSelectionByPlayer = new();
    private readonly object _postMatchLock = new();
    private readonly Dictionary<int, PostMatchBarrier> _postMatchByPlayer = new();
    private readonly Dictionary<int, string> _matchReturnSceneByPlayer = new();
    private readonly Dictionary<int, ResumeDestination> _resumeDestinationByPlayer = new();
    private readonly Dictionary<int, DateTime> _lastUdpInputLogUtcByPlayer = new();

    public GameServer(
        IAccountService? accountService = null,
        GameSessionService? gameSessionService = null,
        IEnumerable<IGatewayMessageModule>? gatewayModules = null)
    {
        _accountService = accountService ?? new AccountService();
        _gameSessionService = gameSessionService ?? new GameSessionService();
        _gameSessionService.BroadcastToPlayers += BroadcastGamePacket;
        _gameSessionService.SessionCompleted += HandleGameSessionCompleted;
        _lobbyDirectory.MatchFound += HandleMatchFound;
        RegisterGatewayRoutes();

        var moduleServices = new GatewayModuleServices(_gameSessionService, HandlePlayerReturnedToRoom);
        new GameThreeGatewayMessageModule().Register(_messageRouter, moduleServices);
        if (gatewayModules is not null)
        {
            foreach (var module in gatewayModules)
            {
                module.Register(_messageRouter, moduleServices);
            }
        }
    }

    private void RegisterGatewayRoutes()
    {
        _messageRouter.Register(Protocols.LoginRequest, false, HandleLoginAsync);
        _messageRouter.Register(Protocols.RegisterRequest, false, HandleRegisterAsync);
        _messageRouter.Register(Protocols.HeartbeatRequest, false, context =>
        {
            HandleTcpHeartbeat(context.Message, context.Stream);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.LogoutRequest, true, context =>
        {
            HandleLogout(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.EnterRoomRequest, true, context =>
        {
            HandleEnterRoom(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.ToggleReadyRequest, true, context =>
        {
            HandleToggleReady(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.StartGameRequest, true, context =>
        {
            HandleStartGame(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.SelectGameModeRequest, true, context =>
        {
            HandleSelectGameMode(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.SelectGameThreeBallRequest, true, context =>
        {
            HandleSelectGameThreeBall(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.GameThreeCancelBallSelectionRequest, true, context =>
        {
            HandleCancelBallSelection(context.Message, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.LobbySnapshotRequest, true, context =>
        {
            HandleLobbySnapshot(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.CreateRoomRequest, true, context =>
        {
            HandleCreateRoom(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.JoinRoomRequest, true, context =>
        {
            HandleJoinRoom(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.LeaveRoomRequest, true, context =>
        {
            HandleLeaveRoom(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.MatchmakingJoinRequest, true, context =>
        {
            HandleMatchmakingJoin(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.MatchmakingCancelRequest, true, context =>
        {
            HandleMatchmakingCancel(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.PartyCreateRequest, true, context =>
        {
            HandlePartyCreate(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.PartyJoinRequest, true, context =>
        {
            HandlePartyJoin(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.PartyLeaveRequest, true, context =>
        {
            HandlePartyLeave(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.ResumeSessionRequest, false, context =>
        {
            HandleResumeSession(context.Message, context.Stream);
            return ValueTask.CompletedTask;
        });
        _messageRouter.Register(Protocols.PlayerMoveRequest, true, context =>
        {
            HandleTcpPlayerMove(context.Message, context.Stream, context.Session);
            return ValueTask.CompletedTask;
        });
    }

    private async ValueTask HandleLoginAsync(GatewayMessageContext context)
    {
        try
        {
            var request = MessageCodec.Decode<LoginRequest>(context.Message);
            Console.WriteLine($"[TCP] User login: {request.Username}");

            var authentication = await _accountService.AuthenticateAsync(request.Username, request.Password);
            GatewaySession? previousSession = null;
            var sessionToken = string.Empty;
            GameSessionResumeState? loginGameState = null;
            var loginBallSelection = false;
            var loginBallSlot = -1;
            var loginBallPlayerCount = 0;
            var loginBallGameMode = string.Empty;
            if (authentication.Success)
            {
                _gatewaySessions.TryGetActiveSession(authentication.PlayerId, out previousSession);
                sessionToken = _gatewaySessions.IssueSessionToken(context.Stream, authentication.PlayerId);
                // A fresh login claims the player again before the old
                // disconnect/explicit-logout cleanup task can expire it.
                CancelPlayerExpiration(authentication.PlayerId);
                _gameSessionService.TryGetResumeState(authentication.PlayerId, out loginGameState);
                loginBallSelection = loginGameState is null
                    && TryGetPendingBallSelection(
                        authentication.PlayerId,
                        out loginBallSlot,
                        out loginBallPlayerCount,
                        out loginBallGameMode);
            }

            var loginSceneName = loginGameState?.SceneName
                ?? (loginBallSelection ? "BallSelection" : ResolveLoginDestination(authentication.PlayerId));

            if (authentication.Success)
            {
                // A player may enter matchmaking directly from the lobby without
                // passing through CreateRoom/JoinRoom/EnterRoom. Register the
                // authenticated stream immediately so server-initiated match
                // notifications can be delivered to that player.
                BindPlayerConnection(context.Stream, authentication.PlayerId);
                Console.WriteLine(
                    $"[Gateway] Registered authenticated stream for player {authentication.PlayerId} " +
                    $"(session={context.Session.SessionId}).");

                if (previousSession is not null && !ReferenceEquals(previousSession, context.Session))
                {
                    var forcedLogout = MessageCodec.EncodePacket(
                        Protocols.LogoutResponse,
                        new LogoutResponse
                        {
                            Success = false,
                            Message = "This account was signed in elsewhere."
                        });
                    if (previousSession.TrySend(forcedLogout))
                    {
                        Console.WriteLine(
                            $"[Gateway] Notified previous session {previousSession.SessionId} " +
                            $"that player {authentication.PlayerId} signed in elsewhere.");
                    }
                }
            }

            SendResponse(
                context.Stream,
                Protocols.LoginResponse,
                new LoginResponse
                {
                    Success = authentication.Success,
                    PlayerId = authentication.PlayerId,
                    Message = authentication.Message,
                    SessionToken = sessionToken,
                    IsInGame = loginGameState is not null,
                    SlotIndex = loginGameState?.SlotIndex ?? (loginBallSelection ? loginBallSlot : -1),
                    PlayerCount = loginGameState?.PlayerCount ?? (loginBallSelection ? loginBallPlayerCount : 0),
                    GameMode = loginGameState?.GameMode ?? (loginBallSelection ? loginBallGameMode : string.Empty),
                    SceneName = loginSceneName
                });
            Console.WriteLine($"[TCP] Sent login response: {authentication.Message}");
            if (authentication.Success && loginGameState is not null)
            {
                Console.WriteLine(
                    $"[Gateway] Login recovery for player {authentication.PlayerId}: " +
                    $"scene={loginGameState.SceneName}, slot={loginGameState.SlotIndex}.");
                SendToClient(context.Stream, loginGameState.SnapshotPacket.ToBytes());
            }
            else if (authentication.Success && loginBallSelection
                && TryGetBallSelectionState(authentication.PlayerId, out var selectionState))
            {
                // Ball selection can also be interrupted. Restore both the
                // scene and the authoritative choices on a fresh login.
                var notification = new GameStartNotification
                {
                    Success = true,
                    Message = "Choose your ball",
                    YourSlotIndex = loginBallSlot,
                    GameMode = loginBallGameMode,
                    SceneName = "BallSelection"
                };
                SendToClient(context.Stream, MessageCodec.EncodePacket(Protocols.GameStartNotification, notification));
                var selectionUpdate = new GameThreeBallSelectionStateUpdate();
                foreach (var selection in selectionState.Selections)
                {
                    selectionUpdate.Selections.Add(new GameThreeBallSelectionEntry
                    {
                        PlayerId = selection.Key,
                        BallId = selection.Value
                    });
                }
                SendToClient(context.Stream, MessageCodec.EncodePacket(
                    Protocols.GameThreeBallSelectionStateUpdate,
                    selectionUpdate));
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[TCP] Error processing login request: {exception.Message}");
            SendResponse(
                context.Stream,
                Protocols.LoginResponse,
                new LoginResponse { Success = false, Message = "Internal server error" });
        }
    }

    private async ValueTask HandleRegisterAsync(GatewayMessageContext context)
    {
        try
        {
            var request = MessageCodec.Decode<RegisterRequest>(context.Message);
            Console.WriteLine($"[TCP] User registration: {request.Username}");
            var registration = await _accountService.RegisterAsync(request.Username, request.Password);

            SendResponse(
                context.Stream,
                Protocols.RegisterResponse,
                new RegisterResponse
                {
                    Success = registration.Success,
                    Message = registration.Message
                });
            Console.WriteLine($"[TCP] Sent registration response: {registration.Message}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[TCP] Error processing registration request: {exception.Message}");
            SendResponse(
                context.Stream,
                Protocols.RegisterResponse,
                new RegisterResponse { Success = false, Message = "Internal server error" });
        }
    }

    private void HandleTcpHeartbeat(BaseMessage message, NetworkStream stream)
    {
        try
        {
            var request = MessageCodec.Decode<HeartbeatRequest>(message);
            SendResponse(
                stream,
                Protocols.HeartbeatResponse,
                new HeartbeatResponse
                {
                    ClientTimestamp = request.ClientTimestamp,
                    ServerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Sequence = request.Sequence
                });
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[TCP] Error processing heartbeat request: {exception.Message}");
        }
    }

    private void HandleLogout(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<LogoutRequest>(message);
            if (session.PlayerId != request.PlayerId)
            {
                throw new InvalidOperationException("Authenticated player mismatch");
            }

            SendResponse(
                stream,
                Protocols.LogoutResponse,
                new LogoutResponse { Success = true, Message = "Logout successful" });

            // Keep the authoritative room/party/game state for the one-minute
            // recovery window. The client may now return through a fresh login.
            if (_gatewaySessions.DetachForLogout(stream, request.PlayerId, out _))
            {
                lock (_connectionLock)
                {
                    _connectionToPlayerId.Remove(stream);
                }
                SchedulePlayerExpiration(request.PlayerId);
                Console.WriteLine($"[Gateway] Explicit logout detached player {request.PlayerId}; state preserved for {_resumeGracePeriod.TotalSeconds:0} seconds.");
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[TCP] Error processing logout request: {exception.Message}");
            SendResponse(
                stream,
                Protocols.LogoutResponse,
                new LogoutResponse { Success = false, Message = "Internal server error during logout" });
        }
    }

    private void HandleEnterRoom(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<EnterRoomRequest>(message);
            if (session.PlayerId != request.PlayerId)
            {
                throw new InvalidOperationException("Authenticated player mismatch");
            }

            // Register the stream before broadcasting so the entering player
            // receives the same authoritative room snapshot as everyone else.
            lock (_connectionLock)
            {
                _connectionToPlayerId[stream] = request.PlayerId;
            }

            HandlePlayerReturnedToRoom(request.PlayerId);
            lock (_postMatchLock)
            {
                // Entering a room establishes a new authoritative return
                // point; discard any stale post-match destination.
                _resumeDestinationByPlayer.Remove(request.PlayerId);
            }

            var currentLobby = _lobbyDirectory.GetRoomSnapshot(request.PlayerId);
            var playerExists = currentLobby.Players.Exists(player => player.PlayerId == request.PlayerId);
            var players = currentLobby.Players;
            var responseMessage = "Already in room";

            if (!playerExists)
            {
                var enterResult = _lobbyDirectory.EnterCurrentRoom(request.PlayerId, request.Username);
                players = enterResult.Players;
                responseMessage = "Successfully entered room";
            }

            SendResponse(
                stream,
                Protocols.EnterRoomResponse,
                new EnterRoomResponse
                {
                    Success = true,
                    Message = responseMessage,
                    Players = players,
                    GameMode = _lobbyDirectory.GetRoomSnapshot(request.PlayerId).GameMode
                });

            // Entering (or re-entering after a result/reconnect) is itself a
            // room lifecycle change. Push the current snapshot to every room
            // member so all clients converge immediately, even when the
            // player was already indexed in the room.
            BroadcastRoomStateUpdate(request.PlayerId);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[TCP] Error processing enter room request: {exception.Message}");
            SendResponse(
                stream,
                Protocols.EnterRoomResponse,
                new EnterRoomResponse { Success = false, Message = "Internal server error during enter room" });
        }
    }

    private void HandleToggleReady(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<ToggleReadyRequest>(message);
            if (session.PlayerId != request.PlayerId)
            {
                throw new InvalidOperationException("Authenticated player mismatch");
            }

            var result = _lobbyDirectory.SetReady(request.PlayerId, request.Ready);
            if (result.Success)
            {
                BroadcastRoomStateUpdate(request.PlayerId);
            }

            SendResponse(
                stream,
                Protocols.ToggleReadyResponse,
                new ToggleReadyResponse { Success = result.Success, Message = result.Message });
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[TCP] Error processing ready toggle request: {exception.Message}");
            SendResponse(
                stream,
                Protocols.ToggleReadyResponse,
                new ToggleReadyResponse { Success = false, Message = "Internal server error during ready toggle" });
        }
    }

    private void HandleStartGame(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<StartGameRequest>(message);
            var lobbyResult = session.PlayerId == request.PlayerId
                ? _lobbyDirectory.TryStart(request.PlayerId)
                : new LobbyStartResult(
                    false,
                    "Authenticated player mismatch",
                    Array.Empty<int>(),
                    GameModes.GameThree,
                    new Dictionary<int, string>());

            if (lobbyResult.Success && !CanStartAfterMatch(lobbyResult.PlayerIds, out var blockedMessage))
            {
                lobbyResult = lobbyResult with { Success = false, Message = blockedMessage };
            }

            GameSessionStartResult? gameSessionResult = null;
            BallSelectionPreparation? ballSelection = null;
            if (lobbyResult.Success && string.Equals(lobbyResult.GameMode, GameModes.GameThree, StringComparison.Ordinal))
            {
                ballSelection = PrepareBallSelection(lobbyResult.PlayerIds, lobbyResult.GameMode);
                Console.WriteLine(
                    $"[BallSelection] {ballSelection.Disposition} for players " +
                    $"[{string.Join(",", ballSelection.PlayerIds)}], confirmed={ballSelection.Selections.Count}");
                BroadcastSceneNotification(ballSelection.PlayerIds, lobbyResult.GameMode, "BallSelection");
                BroadcastBallSelectionState(ballSelection.PlayerIds, ballSelection.Selections);
            }
            else if (lobbyResult.Success)
            {
                TrackMatchReturnScene(lobbyResult.PlayerIds, "Room");
                gameSessionResult = _gameSessionService.StartSession(
                    lobbyResult.PlayerIds.ToArray(),
                    lobbyResult.GameMode,
                    lobbyResult.BallSelections);
                if (gameSessionResult.Success)
                {
                    BroadcastGameStartNotification(lobbyResult.PlayerIds, gameSessionResult);
                }
            }

            SendResponse(
                stream,
                Protocols.StartGameResponse,
                new StartGameResponse
                {
                    Success = ballSelection is not null || (lobbyResult.Success && gameSessionResult?.Success == true),
                    Message = !lobbyResult.Success
                        ? lobbyResult.Message
                        : ballSelection is not null
                            ? ballSelection.Disposition == BallSelectionDisposition.Resumed
                                ? "Ball selection resumed"
                                : "Ball selection started"
                            : gameSessionResult?.Message ?? "Failed to start game session"
                });
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[TCP] Error processing start game request: {exception.Message}");
            SendResponse(
                stream,
                Protocols.StartGameResponse,
                new StartGameResponse { Success = false, Message = "Internal server error during start game" });
        }
    }

    private void HandleSelectGameMode(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<SelectGameModeRequest>(message);
            if (session.PlayerId != request.PlayerId)
            {
                throw new InvalidOperationException("Authenticated player mismatch");
            }

            var result = _lobbyDirectory.SetGameMode(request.PlayerId, request.GameMode);
            var selectedMode = _lobbyDirectory.GetRoomSnapshot(request.PlayerId).GameMode;
            if (result.Success)
            {
                BroadcastRoomStateUpdate(request.PlayerId);
            }

            SendResponse(
                stream,
                Protocols.SelectGameModeResponse,
                new SelectGameModeResponse
                {
                    Success = result.Success,
                    Message = result.Message,
                    GameMode = selectedMode
                });
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[TCP] Error processing game mode selection: {exception.Message}");
            SendResponse(
                stream,
                Protocols.SelectGameModeResponse,
                new SelectGameModeResponse { Success = false, Message = "Unable to select game mode" });
        }
    }

    private void HandleSelectGameThreeBall(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<SelectGameThreeBallRequest>(message);
            if (session.PlayerId != request.PlayerId)
            {
                throw new InvalidOperationException("Authenticated player mismatch");
            }

            if (!GameThreeSession.TryNormalizeBallId(request.BallId, out var normalizedBallId))
            {
                SendResponse(
                    stream,
                    Protocols.SelectGameThreeBallResponse,
                    new SelectGameThreeBallResponse
                    {
                        Success = false,
                        Message = "Unknown ball selection"
                    });
                return;
            }

            if (!TryConfirmPendingBallSelection(
                    request.PlayerId,
                    normalizedBallId,
                    out var completedSelection,
                    out var selectionState,
                    out var rejectionMessage))
            {
                SendResponse(
                    stream,
                    Protocols.SelectGameThreeBallResponse,
                    new SelectGameThreeBallResponse
                    {
                        Success = false,
                        Message = rejectionMessage
                    });
                return;
            }

            SendResponse(
                stream,
                Protocols.SelectGameThreeBallResponse,
                new SelectGameThreeBallResponse
                {
                    Success = true,
                    Message = completedSelection is null ? "Ball confirmed. Waiting for other players." : "All balls confirmed. Starting match.",
                    BallId = normalizedBallId
                });

            BroadcastBallSelectionState(completedSelection?.PlayerIds ?? selectionState.PlayerIds, selectionState.Selections);

            if (completedSelection is not null)
            {
                var gameSession = _gameSessionService.StartSession(
                    completedSelection.PlayerIds,
                    completedSelection.GameMode,
                    completedSelection.BallSelections);
                if (gameSession.Success)
                {
                    TrackMatchReturnScene(completedSelection.PlayerIds, completedSelection.ReturnSceneName);
                    BroadcastGameStartNotification(completedSelection.PlayerIds, gameSession);
                }
                else
                {
                    BroadcastSceneNotification(
                        completedSelection.PlayerIds,
                        completedSelection.GameMode,
                        completedSelection.ReturnSceneName);
                }
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[TCP] Error processing GamePlayThree ball selection: {exception.Message}");
            SendResponse(
                stream,
                Protocols.SelectGameThreeBallResponse,
                new SelectGameThreeBallResponse { Success = false, Message = "Unable to select GamePlayThree ball" });
        }
    }

    private bool TryConfirmPendingBallSelection(
        int playerId,
        string ballId,
        out CompletedBallSelection? completedSelection,
        out BallSelectionState selectionState,
        out string rejectionMessage)
    {
        completedSelection = null;
        selectionState = BallSelectionState.Empty;
        rejectionMessage = "Ball selection is not active";
        lock (_ballSelectionLock)
        {
            if (!_pendingBallSelectionByPlayer.TryGetValue(playerId, out var pending))
            {
                return false;
            }

            if (pending.BallSelections.Any(selection => selection.Key != playerId
                && string.Equals(selection.Value, ballId, StringComparison.Ordinal)))
            {
                rejectionMessage = "Ball already selected by another player";
                return false;
            }

            pending.BallSelections[playerId] = ballId;
            selectionState = new BallSelectionState(
                pending.PlayerIds,
                pending.BallSelections
                    .OrderBy(selection => selection.Key)
                    .ToArray(),
                pending.ReturnSceneName);
            if (pending.BallSelections.Count != pending.PlayerIds.Length)
            {
                return true;
            }

            completedSelection = new CompletedBallSelection(
                pending.PlayerIds,
                pending.GameMode,
                new Dictionary<int, string>(pending.BallSelections),
                pending.ReturnSceneName);
            foreach (var participantId in pending.PlayerIds)
            {
                _pendingBallSelectionByPlayer.Remove(participantId);
            }
            return true;
        }
    }

    private BallSelectionPreparation PrepareBallSelection(
        IReadOnlyCollection<int> playerIds,
        string gameMode,
        string returnSceneName = "Room")
    {
        var requestedPlayerIds = playerIds.Distinct().ToArray();
        lock (_ballSelectionLock)
        {
            var existingSelections = new HashSet<PendingBallSelection>();
            foreach (var playerId in requestedPlayerIds)
            {
                if (_pendingBallSelectionByPlayer.TryGetValue(playerId, out var pending))
                {
                    existingSelections.Add(pending);
                }
            }

            if (existingSelections.Count == 1)
            {
                var existing = existingSelections.Single();
                var hasSameParticipants = existing.PlayerIds.Length == requestedPlayerIds.Length
                    && existing.PlayerIds.ToHashSet().SetEquals(requestedPlayerIds);
                var hasCompleteIndex = requestedPlayerIds.All(playerId =>
                    _pendingBallSelectionByPlayer.TryGetValue(playerId, out var indexed)
                    && ReferenceEquals(indexed, existing));
                if (hasSameParticipants
                    && hasCompleteIndex
                    && string.Equals(existing.GameMode, gameMode, StringComparison.Ordinal)
                    && string.Equals(existing.ReturnSceneName, returnSceneName, StringComparison.Ordinal))
                {
                    return CreateBallSelectionPreparation(existing, BallSelectionDisposition.Resumed);
                }
            }

            var disposition = existingSelections.Count == 0
                ? BallSelectionDisposition.Created
                : BallSelectionDisposition.Replaced;
            foreach (var existing in existingSelections)
            {
                RemovePendingBallSelectionUnsafe(existing);
            }

            var created = new PendingBallSelection(requestedPlayerIds, gameMode, returnSceneName);
            foreach (var participantId in created.PlayerIds)
            {
                _pendingBallSelectionByPlayer[participantId] = created;
            }

            return CreateBallSelectionPreparation(created, disposition);
        }
    }

    private static BallSelectionPreparation CreateBallSelectionPreparation(
        PendingBallSelection pending,
        BallSelectionDisposition disposition)
    {
        return new BallSelectionPreparation(
            pending.PlayerIds,
            pending.BallSelections
                .OrderBy(selection => selection.Key)
                .ToArray(),
            disposition);
    }

    private bool TryRemovePendingBallSelection(
        int playerId,
        out BallSelectionState selectionState,
        out string gameMode)
    {
        lock (_ballSelectionLock)
        {
            if (!_pendingBallSelectionByPlayer.TryGetValue(playerId, out var pending))
            {
                selectionState = BallSelectionState.Empty;
                gameMode = GameModes.GameThree;
                return false;
            }

            selectionState = new BallSelectionState(
                pending.PlayerIds,
                pending.BallSelections
                    .OrderBy(selection => selection.Key)
                    .ToArray(),
                pending.ReturnSceneName);
            gameMode = pending.GameMode;
            RemovePendingBallSelectionUnsafe(pending);
            return true;
        }
    }

    private void RemovePendingBallSelectionUnsafe(PendingBallSelection pending)
    {
        foreach (var participantId in pending.PlayerIds)
        {
            if (_pendingBallSelectionByPlayer.TryGetValue(participantId, out var indexed)
                && ReferenceEquals(indexed, pending))
            {
                _pendingBallSelectionByPlayer.Remove(participantId);
            }
        }
    }

    private void HandleGameSessionCompleted(IReadOnlyList<int> playerIds, string gameMode)
    {
        if (playerIds.Count == 0)
        {
            return;
        }

        string returnSceneName;
        lock (_postMatchLock)
        {
            returnSceneName = playerIds
                .Select(playerId => _matchReturnSceneByPlayer.TryGetValue(playerId, out var sceneName) ? sceneName : null)
                .FirstOrDefault(sceneName => !string.IsNullOrWhiteSpace(sceneName)) ?? "Room";
            foreach (var playerId in playerIds)
            {
                _matchReturnSceneByPlayer.Remove(playerId);
                _resumeDestinationByPlayer[playerId] = new ResumeDestination(
                    returnSceneName,
                    DateTime.UtcNow + _resumeGracePeriod);
            }
        }

        if (string.Equals(returnSceneName, "Lobby", StringComparison.Ordinal))
        {
            Console.WriteLine(
                $"[PostMatch] Matchmade session completed for [{string.Join(",", playerIds)}]. " +
                "Players return directly to the lobby; clearing temporary room membership.");
            foreach (var playerId in playerIds)
            {
                _lobbyDirectory.LeaveRoom(playerId);
                ClearPostMatchForPlayer(playerId);
            }

            // Matchmaking parties are deliberately preserved, but the private
            // room created for this match must not block the next queue request.
            BroadcastLobbyStateUpdate();
            return;
        }

        var barrier = new PostMatchBarrier(playerIds.ToArray(), gameMode);
        var resetAnchor = playerIds.FirstOrDefault(playerId => _lobbyDirectory.TryGetRoomId(playerId, out _));
        var readyReset = resetAnchor > 0
            ? _lobbyDirectory.ResetReadyStates(resetAnchor)
            : new LobbyMutationResult(false, "No room members remain", new List<RoomPlayerInfo>());
        if (readyReset.Success)
        {
            Console.WriteLine($"[PostMatch] Reset ready states for room players [{string.Join(",", playerIds)}].");
        }
        lock (_postMatchLock)
        {
            foreach (var playerId in barrier.PlayerIds)
            {
                _postMatchByPlayer[playerId] = barrier;
            }
        }

        Console.WriteLine(
            $"[PostMatch] Session completed for [{string.Join(",", barrier.PlayerIds)}]. " +
            "Waiting for every player to return to the room.");
        BroadcastRoomStateUpdate(barrier.PlayerIds);
    }

    private void TrackMatchReturnScene(IReadOnlyCollection<int> playerIds, string returnSceneName)
    {
        lock (_postMatchLock)
        {
            foreach (var playerId in playerIds)
            {
                _matchReturnSceneByPlayer[playerId] = returnSceneName;
            }
        }
    }

    private string ResolveLoginDestination(int playerId)
    {
        if (playerId <= 0)
        {
            return string.Empty;
        }

        lock (_postMatchLock)
        {
            if (_resumeDestinationByPlayer.TryGetValue(playerId, out var destination))
            {
                if (destination.ExpiresAtUtc > DateTime.UtcNow)
                {
                    return destination.SceneName;
                }

                _resumeDestinationByPlayer.Remove(playerId);
            }
        }

        // Room membership is authoritative in LobbyDirectoryService. This
        // covers a disconnect while waiting in a private/public room.
        return _lobbyDirectory.TryGetRoomId(playerId, out _)
            ? "Room"
            : string.Empty;
    }

    private void HandlePlayerReturnedToRoom(int playerId)
    {
        lock (_postMatchLock)
        {
            _resumeDestinationByPlayer.Remove(playerId);
        }
        PostMatchBarrier? barrier;
        bool completed;
        int returnedCount;
        int requiredCount;
        lock (_postMatchLock)
        {
            if (!_postMatchByPlayer.TryGetValue(playerId, out barrier))
            {
                return;
            }

            barrier.ReturnedPlayerIds.Add(playerId);
            completed = barrier.ReturnedPlayerIds.Count >= barrier.PlayerIds.Length;
            returnedCount = barrier.ReturnedPlayerIds.Count;
            requiredCount = barrier.PlayerIds.Length;
            if (completed)
            {
                RemovePostMatchBarrierUnsafe(barrier);
            }
        }

        Console.WriteLine(
            $"[PostMatch] Player {playerId} returned ({returnedCount}/{requiredCount}). " +
            (completed ? "Room is ready." : "Waiting for remaining players."));
        BroadcastRoomStateUpdate(barrier.PlayerIds);
    }

    private bool CanStartAfterMatch(IReadOnlyCollection<int> playerIds, out string message)
    {
        message = string.Empty;
        var requested = playerIds.Distinct().ToArray();
        lock (_postMatchLock)
        {
            var barriers = requested
                .Where(playerId => _postMatchByPlayer.ContainsKey(playerId))
                .Select(playerId => _postMatchByPlayer[playerId])
                .Distinct()
                .ToArray();
            if (barriers.Length == 0)
            {
                return true;
            }

            var barrier = barriers[0];
            if (barriers.Length == 1
                && barrier.PlayerIds.Length == requested.Length
                && barrier.PlayerIds.ToHashSet().SetEquals(requested))
            {
                if (barrier.ReturnedPlayerIds.Count < barrier.PlayerIds.Length)
                {
                    var waiting = barrier.PlayerIds
                        .Where(playerId => !barrier.ReturnedPlayerIds.Contains(playerId))
                        .ToArray();
                    message = $"Waiting for all players to return to the room ({string.Join(", ", waiting)}).";
                    return false;
                }

                RemovePostMatchBarrierUnsafe(barrier);
            }
        }

        return true;
    }

    private bool IsRoomReadyAfterMatch(IReadOnlyCollection<int> playerIds)
    {
        var requested = playerIds.Distinct().ToArray();
        lock (_postMatchLock)
        {
            var barriers = requested
                .Where(playerId => _postMatchByPlayer.ContainsKey(playerId))
                .Select(playerId => _postMatchByPlayer[playerId])
                .Distinct()
                .ToArray();
            if (barriers.Length != 1)
            {
                return true;
            }

            var barrier = barriers[0];
            return barrier.PlayerIds.Length != requested.Length
                || !barrier.PlayerIds.ToHashSet().SetEquals(requested)
                || barrier.ReturnedPlayerIds.Count >= barrier.PlayerIds.Length;
        }
    }

    private void RemovePostMatchBarrierUnsafe(PostMatchBarrier barrier)
    {
        foreach (var playerId in barrier.PlayerIds)
        {
            if (_postMatchByPlayer.TryGetValue(playerId, out var indexed)
                && ReferenceEquals(indexed, barrier))
            {
                _postMatchByPlayer.Remove(playerId);
            }
        }
    }

    private void ClearPostMatchForPlayer(int playerId)
    {
        lock (_postMatchLock)
        {
            if (_postMatchByPlayer.TryGetValue(playerId, out var barrier))
            {
                RemovePostMatchBarrierUnsafe(barrier);
            }
        }
    }

    private void BroadcastBallSelectionState(
        IReadOnlyCollection<int> playerIds,
        IEnumerable<KeyValuePair<int, string>> selections)
    {
        var update = new GameThreeBallSelectionStateUpdate();
        foreach (var selection in selections.OrderBy(selection => selection.Key))
        {
            update.Selections.Add(new GameThreeBallSelectionEntry
            {
                PlayerId = selection.Key,
                BallId = selection.Value
            });
        }

        var packet = MessageCodec.EncodePacket(Protocols.GameThreeBallSelectionStateUpdate, update);
        foreach (var playerId in playerIds)
        {
            SendPacketToPlayer(playerId, packet);
        }
    }

    private bool TryGetPendingBallSelection(int playerId, out int slotIndex, out int playerCount, out string gameMode)
    {
        lock (_ballSelectionLock)
        {
            if (!_pendingBallSelectionByPlayer.TryGetValue(playerId, out var pending))
            {
                slotIndex = -1;
                playerCount = 0;
                gameMode = string.Empty;
                return false;
            }

            slotIndex = Array.IndexOf(pending.PlayerIds, playerId);
            if (slotIndex < 0)
            {
                playerCount = 0;
                gameMode = string.Empty;
                return false;
            }

            playerCount = pending.PlayerIds.Length;
            gameMode = pending.GameMode;
            return true;
        }
    }

    private bool TryGetBallSelectionState(int playerId, out BallSelectionState selectionState)
    {
        lock (_ballSelectionLock)
        {
            if (!_pendingBallSelectionByPlayer.TryGetValue(playerId, out var pending))
            {
                selectionState = BallSelectionState.Empty;
                return false;
            }

            selectionState = new BallSelectionState(
                pending.PlayerIds,
                pending.BallSelections
                    .OrderBy(selection => selection.Key)
                    .ToArray(),
                pending.ReturnSceneName);
            return true;
        }
    }

    private void HandleCancelBallSelection(BaseMessage message, GatewaySession session)
    {
        try
        {
            var request = MessageCodec.Decode<GameThreeCancelBallSelectionRequest>(message);
            if (session.PlayerId != request.PlayerId)
            {
                return;
            }

            if (!TryRemovePendingBallSelection(request.PlayerId, out var selectionState, out var gameMode))
            {
                return;
            }

            Console.WriteLine($"[BallSelection] Cancelled by player {request.PlayerId}");
            // Matchmaking creates a private room before opening ball selection.
            // Cancelling a matchmaking selection must unwind that room for every
            // participant so the next matchmaking request is not rejected as
            // "still in a room". A room-created selection returns to Room and
            // intentionally keeps its room membership.
            if (string.Equals(selectionState.ReturnSceneName, "Lobby", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var participantId in selectionState.PlayerIds)
                {
                    _lobbyDirectory.CancelMatchmaking(participantId);
                    _gameSessionService.StopSessionForPlayer(participantId);
                    ClearPostMatchForPlayer(participantId);
                    if (_lobbyDirectory.LeaveRoom(participantId).Success)
                    {
                        Console.WriteLine($"[Matchmaking] Cleared cancelled match room membership for player {participantId}.");
                    }
                }

                BroadcastLobbyStateUpdate();
            }

            BroadcastSceneNotification(selectionState.PlayerIds, gameMode, selectionState.ReturnSceneName);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[TCP] Error cancelling ball selection: {exception.Message}");
        }
    }

    private void HandleLobbySnapshot(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<LobbySnapshotRequest>(message);
        if (session.PlayerId != request.PlayerId)
        {
            SendResponse(stream, Protocols.LobbySnapshotResponse, new LobbySnapshotResponse { Success = false, Message = "Authenticated player mismatch" });
            return;
        }

        var snapshot = _lobbyDirectory.GetLobbySnapshot(request.PlayerId);
        var response = new LobbySnapshotResponse
        {
            Success = true,
            Message = "Lobby snapshot",
            Rooms = snapshot.Rooms,
            IsMatchmaking = snapshot.IsMatchmaking,
            QueuePosition = snapshot.QueuePosition
        };
        if (snapshot.Party is not null)
        {
            response.Party = snapshot.Party;
        }
        SendResponse(stream, Protocols.LobbySnapshotResponse, response);
    }

    private void HandleCreateRoom(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<CreateRoomRequest>(message);
        var result = session.PlayerId == request.PlayerId
            ? _lobbyDirectory.CreateRoom(
                request.PlayerId,
                request.Username,
                request.RoomName,
                request.GameMode,
                request.IsPrivate,
                request.JoinCode)
            : new LobbyDirectoryActionResult(false, "Authenticated player mismatch", string.Empty, string.Empty);
        SendResponse(stream, Protocols.CreateRoomResponse, new CreateRoomResponse
        {
            Success = result.Success,
            Message = result.Message,
            RoomId = result.RoomId,
            JoinCode = result.JoinCode
        });
        Console.WriteLine(
            $"[Lobby] Create room result player={request.PlayerId}, success={result.Success}, " +
            $"name='{request.RoomName}', room={result.RoomId}, joinCode={result.JoinCode}");
        if (result.Success)
        {
            lock (_connectionLock)
            {
                _connectionToPlayerId[stream] = request.PlayerId;
            }

            BroadcastRoomStateUpdate(request.PlayerId);
            BroadcastLobbyStateUpdate();
        }
    }

    private void HandleJoinRoom(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<JoinRoomRequest>(message);
        Console.WriteLine(
            $"[Lobby] Join room request player={request.PlayerId}, " +
            $"roomOrName='{request.RoomId}', code='{request.JoinCode}'");
        var result = session.PlayerId == request.PlayerId
            ? _lobbyDirectory.JoinRoom(request.PlayerId, request.Username, request.RoomId, request.JoinCode)
            : new LobbyDirectoryActionResult(false, "Authenticated player mismatch", string.Empty, string.Empty);
        SendResponse(stream, Protocols.JoinRoomResponse, new JoinRoomResponse
        {
            Success = result.Success,
            Message = result.Message,
            RoomId = result.RoomId
        });
        Console.WriteLine(
            $"[Lobby] Join room result player={request.PlayerId}, success={result.Success}, " +
            $"message='{result.Message}', room={result.RoomId}");
        if (result.Success)
        {
            lock (_connectionLock)
            {
                _connectionToPlayerId[stream] = request.PlayerId;
            }

            BroadcastRoomStateUpdate(request.PlayerId);
            BroadcastLobbyStateUpdate();
        }
    }

    private void HandleLeaveRoom(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<LeaveRoomRequest>(message);
        var previousPlayers = session.PlayerId == request.PlayerId
            ? _lobbyDirectory.GetRoomSnapshot(request.PlayerId).Players
                .Select(player => player.PlayerId)
                .ToArray()
            : Array.Empty<int>();
        var result = session.PlayerId == request.PlayerId && _lobbyDirectory.LeaveRoom(request.PlayerId).Success;
        if (result)
        {
            lock (_postMatchLock)
            {
                _resumeDestinationByPlayer.Remove(request.PlayerId);
                _matchReturnSceneByPlayer.Remove(request.PlayerId);
            }
        }
        SendResponse(stream, Protocols.LeaveRoomResponse, new LeaveRoomResponse
        {
            Success = result,
            Message = result ? "Left room" : "Player is not in a room"
        });
        if (result)
        {
            ClearPostMatchForPlayer(request.PlayerId);
            // Always push the removal to the remaining room members. They may
            // still be on the result screen and will consume this cached state
            // when they return to the room later.
            BroadcastRoomStateUpdate(previousPlayers);
            if (TryRemovePendingBallSelection(request.PlayerId, out var selectionState, out var gameMode))
            {
                var remainingPlayers = selectionState.PlayerIds
                    .Where(playerId => playerId != request.PlayerId)
                    .ToArray();
                Console.WriteLine($"[BallSelection] Cleared because player {request.PlayerId} left the room");
                if (remainingPlayers.Length > 0)
                {
                    BroadcastSceneNotification(remainingPlayers, gameMode, "Room");
                    BroadcastRoomStateUpdate(remainingPlayers);
                }
            }

            BroadcastLobbyStateUpdate();
        }
    }

    private void HandleMatchmakingJoin(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<MatchmakingJoinRequest>(message);
        if (session.PlayerId == request.PlayerId)
        {
            BindPlayerConnection(stream, request.PlayerId);
        }

        if (session.PlayerId == request.PlayerId
            && TryGetPendingBallSelection(request.PlayerId, out _, out _, out var pendingGameMode))
        {
            SendResponse(stream, Protocols.MatchmakingJoinResponse, new MatchmakingJoinResponse
            {
                Success = true,
                Message = "Match already found. Resuming ball selection.",
                MatchFound = true,
                QueuePosition = 0
            });
            BroadcastSceneNotification(new[] { request.PlayerId }, pendingGameMode, "BallSelection");
            return;
        }

        var result = session.PlayerId == request.PlayerId
            ? _lobbyDirectory.JoinMatchmaking(request.PlayerId, request.Username, request.GameMode)
            : new MatchmakingResult(false, "Authenticated player mismatch", false, string.Empty, 0);
        SendResponse(stream, Protocols.MatchmakingJoinResponse, new MatchmakingJoinResponse
        {
            Success = result.Success,
            Message = result.Message,
            MatchFound = result.MatchFound,
            RoomId = result.RoomId,
            QueuePosition = result.QueuePosition
        });
        BroadcastLobbyStateUpdate();
    }

    private void HandleMatchmakingCancel(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<MatchmakingCancelRequest>(message);
        var result = session.PlayerId == request.PlayerId && _lobbyDirectory.CancelMatchmaking(request.PlayerId);
        SendResponse(stream, Protocols.MatchmakingCancelResponse, new MatchmakingCancelResponse
        {
            Success = result,
            Message = result ? "Matchmaking cancelled" : "Player is not matchmaking"
        });
        BroadcastLobbyStateUpdate();
    }

    private void HandlePartyCreate(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<PartyCreateRequest>(message);
        var result = session.PlayerId == request.PlayerId
            ? _lobbyDirectory.CreateParty(request.PlayerId, request.Username)
            : new PartyActionResult(false, "Authenticated player mismatch", null);
        var response = new PartyCreateResponse { Success = result.Success, Message = result.Message };
        if (result.Party is not null) response.Party = result.Party;
        SendResponse(stream, Protocols.PartyCreateResponse, response);
        BroadcastLobbyStateUpdate();
    }

    private void HandlePartyJoin(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<PartyJoinRequest>(message);
        var result = session.PlayerId == request.PlayerId
            ? _lobbyDirectory.JoinParty(request.PlayerId, request.Username, request.JoinCode)
            : new PartyActionResult(false, "Authenticated player mismatch", null);
        var response = new PartyJoinResponse { Success = result.Success, Message = result.Message };
        if (result.Party is not null) response.Party = result.Party;
        SendResponse(stream, Protocols.PartyJoinResponse, response);
        BroadcastLobbyStateUpdate();
    }

    private void HandlePartyLeave(BaseMessage message, NetworkStream stream, GatewaySession session)
    {
        var request = MessageCodec.Decode<PartyLeaveRequest>(message);
        var result = session.PlayerId == request.PlayerId && _lobbyDirectory.LeaveParty(request.PlayerId);
        SendResponse(stream, Protocols.PartyLeaveResponse, new PartyLeaveResponse
        {
            Success = result,
            Message = result ? "Left party" : "Player is not in a party"
        });
        BroadcastLobbyStateUpdate();
    }

    private void HandleMatchFound(IReadOnlyList<int> playerIds, string roomId)
    {
        var ballSelection = PrepareBallSelection(playerIds, GameModes.GameThree, "Lobby");
        Console.WriteLine(
            $"[Matchmaking] Match ready in room {roomId}; humans={playerIds.Count}, " +
            $"aiFill={LobbyDirectoryService.MaxRoomPlayers - playerIds.Count}. Entering ball selection.");

        var participants = new HashSet<int>(playerIds);
        List<(NetworkStream Stream, int PlayerId)> recipients;
        lock (_connectionLock)
        {
            recipients = _connectionToPlayerId
                .Where(connection => participants.Contains(connection.Value))
                .Select(connection => (connection.Key, connection.Value))
                .ToList();
        }
        foreach (var recipient in recipients)
        {
            Console.WriteLine($"[Matchmaking] Sending LobbyStateUpdate to player {recipient.PlayerId} for room {roomId}.");
            var snapshot = _lobbyDirectory.GetLobbySnapshot(recipient.PlayerId);
            SendResponse(recipient.Stream, Protocols.LobbyStateUpdate, CreateLobbyStateUpdate(snapshot, true, roomId));
        }
        BroadcastBallSelectionState(ballSelection.PlayerIds, ballSelection.Selections);
        BroadcastSceneNotification(ballSelection.PlayerIds, GameModes.GameThree, "BallSelection");
    }

    private void BroadcastLobbyStateUpdate()
    {
        List<(NetworkStream Stream, int PlayerId)> recipients;
        lock (_connectionLock)
        {
            recipients = _connectionToPlayerId.Select(connection => (connection.Key, connection.Value)).ToList();
        }
        foreach (var recipient in recipients)
        {
            var snapshot = _lobbyDirectory.GetLobbySnapshot(recipient.PlayerId);
            SendResponse(recipient.Stream, Protocols.LobbyStateUpdate, CreateLobbyStateUpdate(snapshot, false, string.Empty));
        }
    }

    private static LobbyStateUpdate CreateLobbyStateUpdate(LobbyDirectorySnapshot snapshot, bool matchFound, string roomId)
    {
        var update = new LobbyStateUpdate
        {
            Rooms = snapshot.Rooms,
            IsMatchmaking = snapshot.IsMatchmaking,
            QueuePosition = snapshot.QueuePosition,
            MatchFound = matchFound,
            RoomId = roomId
        };
        if (snapshot.Party is not null) update.Party = snapshot.Party;
        return update;
    }

    private void SendResponse<T>(NetworkStream stream, ushort protocolId, T response)
    {
        SendToClient(stream, MessageCodec.EncodePacket(protocolId, response));
    }

    public void Start(string ip = "127.0.0.1", int tcpPort = 8888, int udpPort = 8889)//tcp鍜寀dp鐨勭鍙ｄ笉鍚?
    {
        try
        {
            Console.WriteLine("=== Game Server Starting ===");
            _udpPort = udpPort;

            _cancellationTokenSource = new CancellationTokenSource();//鍒涘缓浠ょ墝婧?
            _isRunning = true;

            // 1. 鍚姩TCP鏈嶅姟鍣?
            IPAddress localAddr = IPAddress.Parse(ip);//瑙ｆ瀽IP鍦板潃
            _tcpListener = new TcpListener(localAddr, tcpPort);//鍒涘缓TCP鐩戝惉鍣?
            _tcpListener.Start();//寮€濮嬬洃鍚?
            Console.WriteLine($"TCP server started, listening on {ip}:{tcpPort}");

            // 2. 鍚姩UDP鏈嶅姟鍣?
            _udpClient = new UdpClient(new IPEndPoint(localAddr, _udpPort));
            Console.WriteLine($"UDP server started, listening on {ip}:{_udpPort}");

            // 3. 寮€濮嬪紓姝ユ帴鍙楀鎴风杩炴帴
            Task.Run(() => AcceptClients(), _cancellationTokenSource.Token);

            // 4. 寮€濮嬫帴鏀禪DP娑堟伅
            Task.Run(() => ReceiveUdpMessages(), _cancellationTokenSource.Token);

            Console.WriteLine("=== Game Server Ready ===");
            Console.WriteLine("Press Ctrl+C or close the window to stop the server...");
            Console.WriteLine("========================================");

            // 5. 淇濇寔鏈嶅姟鍣ㄨ繍琛岋紝鐩村埌琚仠姝?
            _serverThread = new Thread(() =>
            {
                while (_isRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    Thread.Sleep(250);
                    try
                    {
                        _lobbyDirectory.ProcessMatchmakingTimeouts(DateTime.UtcNow);
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine($"[Matchmaking] Queue processing failed: {exception.Message}");
                    }
                }
            });
            _serverThread.Start();

            // 6. 绛夊緟鏈嶅姟鍣ㄧ嚎绋嬬粨鏉?
            _serverThread.Join();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server failed to start: {ex.Message}");
            Stop();
            throw;
        }
    }

    private async Task AcceptClients()
    {
        var listener = _tcpListener;
        var cancellationTokenSource = _cancellationTokenSource;
        if (listener is null || cancellationTokenSource is null)
        {
            Console.WriteLine("[Gateway] TCP listener is not initialized.");
            return;
        }

        while (_isRunning && !cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationTokenSource.Token);//寮傛鎺ユ敹Tcp瀹㈡埛绔繛鎺ヨ繑鍥濼cpClient绫诲璞?
                Console.WriteLine($"Client connected: {client.Client.RemoteEndPoint}");

                // A gateway session owns the connection and serializes outbound writes.
                _ = HandleClientAsync(client);
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    Console.WriteLine($"Exception while accepting client connection: {ex.Message}");
                }
            }
        }
    }

    private async Task ReceiveUdpMessages()
    {
        Console.WriteLine($"[UDP] Start receiving UDP messages, port: {_udpPort}");

        var udpClient = _udpClient;
        var cancellationTokenSource = _cancellationTokenSource;
        if (udpClient is null || cancellationTokenSource is null)
        {
            Console.WriteLine("[Gateway] UDP client is not initialized.");
            return;
        }

        while (_isRunning && !cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await udpClient.ReceiveAsync(cancellationTokenSource.Token);
                byte[] receivedData = result.Buffer;
                IPEndPoint clientEndPoint = result.RemoteEndPoint;

                if (receivedData.Length > BaseMessage.HeaderSize + MaxUdpPacketBodyBytes)
                {
                    Console.WriteLine($"[Gateway] Dropped oversized UDP packet from {clientEndPoint}: {receivedData.Length} bytes");
                    continue;
                }

                // 澶勭悊UDP娑堟伅
                ProcessUdpMessage(receivedData, clientEndPoint);
            }
            catch (ObjectDisposedException)
            {
                // UDP瀹㈡埛绔鍏抽棴锛屾甯搁€€鍑?
                Console.WriteLine("[UDP] UDP client closed; receive loop stopped.");
                break;
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    Console.WriteLine($"[UDP] 鎺ユ敹娑堟伅寮傚父: {ex.Message}");
                    await Task.Delay(100); // 閬垮厤寮傚父寰幆
                }
            }
        }
    }

    //澶勭悊UDP娑堟伅鐨勬柟娉?
    private void ProcessUdpMessage(byte[] receivedData, IPEndPoint clientEndPoint)
    {
        if (receivedData.Length < BaseMessage.HeaderSize)
        {
            Console.WriteLine($"[UDP DEBUG] Packet too small ({receivedData.Length} bytes), discarded");
            return;
        }

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
                Console.WriteLine($"[Gateway] Dropped unauthenticated UDP packet from {clientEndPoint}");
                return;
            }

            switch (udpMsg.ProtocolId)
            {
                case Protocols.HeartbeatRequest://澶勭悊蹇冭烦璇锋眰
                    Console.WriteLine($"[UDP] Processing heartbeat request from: {clientEndPoint}");
                    HandleUdpHeartbeat(udpMsg, clientEndPoint);
                    break;

                case Protocols.PlayerMoveRequest://澶勭悊鐜╁绉诲姩璇锋眰
                    Console.WriteLine($"[UDP] Processing player move request from: {clientEndPoint}");
                    HandlePlayerMove(udpMsg, clientEndPoint);
                    break;

                case Protocols.GameThreeInput:
                    DispatchAuthenticatedUdpMessage(udpMsg, clientEndPoint);
                    break;

                default:
                    Console.WriteLine($"[UDP] Unknown UDP protocol: {udpMsg.ProtocolId}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UDP] Message parsing failed: {ex.Message}");
            Console.WriteLine($"[UDP] Exception stack trace: {ex.StackTrace}");
        }
    }

    private void DispatchAuthenticatedUdpMessage(BaseMessage udpMsg, IPEndPoint clientEndPoint)
    {
        if (!_gatewaySessions.TryGetSessionByUdpEndPoint(clientEndPoint, out var session) || session is null)
        {
            Console.WriteLine($"[Gateway] Dropped UDP protocol {udpMsg.ProtocolId} from unknown endpoint {clientEndPoint}");
            return;
        }

        try
        {
            if (udpMsg.ProtocolId == Protocols.GameThreeInput && session.PlayerId is int playerId)
            {
                var now = DateTime.UtcNow;
                if (!_lastUdpInputLogUtcByPlayer.TryGetValue(playerId, out var lastLog)
                    || now - lastLog >= TimeSpan.FromSeconds(5))
                {
                    _lastUdpInputLogUtcByPlayer[playerId] = now;
                    Console.WriteLine($"[GameThreeNet] UDP movement input active for player {playerId}.");
                }
            }

            var dispatchResult = _messageRouter.DispatchAsync(
                new GatewayMessageContext(udpMsg, session.Stream, session)).AsTask().GetAwaiter().GetResult();
            if (dispatchResult == GatewayDispatchResult.UnknownProtocol)
            {
                Console.WriteLine($"Unknown UDP protocol {udpMsg.ProtocolId}, ignored.");
            }
            else if (dispatchResult == GatewayDispatchResult.AuthenticationRequired)
            {
                Console.WriteLine($"[Gateway] Rejected unauthenticated UDP protocol {udpMsg.ProtocolId} for session {session.SessionId}.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Gateway] Error dispatching UDP protocol {udpMsg.ProtocolId}: {ex.Message}");
        }
    }

    private void HandleUdpBind(BaseMessage bindMessage, IPEndPoint clientEndPoint)
    {
        var udpClient = _udpClient;
        if (udpClient is null)
        {
            Console.WriteLine("[Gateway] Cannot bind UDP session because UDP client is not available.");
            return;
        }

        var request = MessageCodec.Decode<UdpBindRequest>(bindMessage);
        var success = !string.IsNullOrWhiteSpace(request.SessionToken)
            && _gatewaySessions.TryBindUdpEndPoint(request.SessionToken, request.PlayerId, clientEndPoint);

        var response = new UdpBindResponse
        {
            Success = success,
            Message = success ? "UDP session bound" : "Invalid or expired session token"
        };

        var packet = MessageCodec.EncodePacket(Protocols.UdpBindResponse, response);
        udpClient.Send(packet, packet.Length, clientEndPoint);
        Console.WriteLine($"[Gateway] UDP bind {(success ? "accepted" : "rejected")} for player {request.PlayerId}, endpoint={clientEndPoint}");
    }

    /// <summary>
    /// 澶勭悊UDP蹇冭烦鍖?    /// </summary>
    private void HandleUdpHeartbeat(BaseMessage heartbeatMsg, IPEndPoint clientEndPoint)
    {
        try
        {
            var udpClient = _udpClient;
            if (udpClient is null)
            {
                Console.WriteLine("[UDP HEARTBEAT] UDP client is not available.");
                return;
            }

            Console.WriteLine($"[UDP HEARTBEAT] Start processing heartbeat request, client: {clientEndPoint}");

            // 1. 鍙嶅簭鍒楀寲蹇冭烦璇锋眰
            using (var ms = new System.IO.MemoryStream(heartbeatMsg.Body))
            {
                HeartbeatRequest heartbeatReq = ProtoBuf.Serializer.Deserialize<HeartbeatRequest>(ms);//鍩轰簬Protobuf鍙嶅簭鍒楀寲蹇冭烦璇锋眰
                Console.WriteLine($"[UDP HEARTBEAT] Heartbeat sequence: {heartbeatReq.Sequence}, client timestamp: {heartbeatReq.ClientTimestamp}");

                // 2. 鏋勯€犲績璺冲搷搴?
                HeartbeatResponse heartbeatResp = new HeartbeatResponse
                {
                    ClientTimestamp = heartbeatReq.ClientTimestamp,
                    ServerTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Sequence = heartbeatReq.Sequence
                };

                Console.WriteLine($"[UDP HEARTBEAT] Generating response, server timestamp: {heartbeatResp.ServerTimestamp}");

                // 3. 搴忓垪鍖栧搷搴?
                using (var respMs = new System.IO.MemoryStream())
                {
                    ProtoBuf.Serializer.Serialize(respMs, heartbeatResp);
                    byte[] responseData = respMs.ToArray();
                    Console.WriteLine($"[UDP HEARTBEAT] Response serialized, length: {responseData.Length} bytes");

                    // 4. 鏋勯€燘aseMessage鏍煎紡鐨勫搷搴?
                    BaseMessage responseMsg = new BaseMessage(Protocols.HeartbeatResponse, responseData);
                    byte[] responseBytes = responseMsg.ToBytes();

                    Console.WriteLine($"[UDP HEARTBEAT] Preparing to send response, total length: {responseBytes.Length} bytes");

                    // 5. 閫氳繃UDP鍙戦€佸搷搴?
                    int bytesSent = udpClient.Send(responseBytes, responseBytes.Length, clientEndPoint);
                    Console.WriteLine($"[UDP HEARTBEAT] Sent heartbeat response, sequence: {heartbeatResp.Sequence}, bytes sent: {bytesSent}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UDP HEARTBEAT] Error processing heartbeat request: {ex.Message}");
            Console.WriteLine($"[UDP HEARTBEAT] Exception stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// 澶勭悊鐜╁绉诲姩璇锋眰
    /// </summary>
    private void HandlePlayerMove(BaseMessage moveMsg, IPEndPoint clientEndPoint)
    {
        try
        {
            var udpClient = _udpClient;
            if (udpClient is null)
            {
                Console.WriteLine("[UDP] Cannot process player move because UDP client is not available.");
                return;
            }

            using (var ms = new System.IO.MemoryStream(moveMsg.Body))
            {
                PlayerMove moveReq = ProtoBuf.Serializer.Deserialize<PlayerMove>(ms);

                if (!_gatewaySessions.IsUdpPlayerAuthorized(clientEndPoint, moveReq.PlayerId))
                {
                    Console.WriteLine($"[Gateway] Rejected UDP player mismatch from {clientEndPoint}");
                    return;
                }

                Console.WriteLine($"[UDP] Player move: ID={moveReq.PlayerId}, Position=({moveReq.PosX}, {moveReq.PosY}, {moveReq.PosZ})");

                // 绠€鍗曞洖澶嶇‘璁?
                var response = new BaseMessage(Protocols.PlayerMoveResponse, new byte[] { 0x01 });
                byte[] responseBytes = response.ToBytes();
                udpClient.Send(responseBytes, responseBytes.Length, clientEndPoint);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UDP] Error processing move request: {ex.Message}");
        }
    }

    private void HandleTcpPlayerMove(BaseMessage moveMsg, NetworkStream stream, GatewaySession session)
    {
        try
        {
            using var messageStream = new MemoryStream(moveMsg.Body);
            var moveRequest = ProtoBuf.Serializer.Deserialize<PlayerMove>(messageStream);

            if (session.PlayerId != moveRequest.PlayerId)
            {
                Console.WriteLine($"[Gateway] Rejected TCP player mismatch for session {session.SessionId}");
                return;
            }

            Console.WriteLine($"[TCP] Player move: ID={moveRequest.PlayerId}, Position=({moveRequest.PosX}, {moveRequest.PosY}, {moveRequest.PosZ})");
            var response = new BaseMessage(Protocols.PlayerMoveResponse, new byte[] { 0x01 });
            SendToClient(stream, response.ToBytes());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP] Error processing move request: {ex.Message}");
        }
    }

    /// <summary>
    /// 澶勭悊瀹㈡埛绔繛鎺?    /// </summary>
    private async Task HandleClientAsync(TcpClient client)
    {
        var session = _gatewaySessions.Register(client);
        NetworkStream stream = session.Stream;
        byte[] buffer = new byte[4096];
        var frameDecoder = new MessageFrameDecoder(MaxTcpPacketBodyBytes);

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
                            Console.WriteLine($"Received TCP message -> Protocol ID: {receivedMsg.ProtocolId}, Length: {receivedMsg.BodyLength}");
                        }

                        var dispatchResult = await _messageRouter.DispatchAsync(
                            new GatewayMessageContext(receivedMsg, stream, session));
                        if (dispatchResult == GatewayDispatchResult.UnknownProtocol)
                        {
                            Console.WriteLine($"Unknown TCP protocol {receivedMsg.ProtocolId}, ignored.");
                        }
                        else if (dispatchResult == GatewayDispatchResult.AuthenticationRequired)
                        {
                            Console.WriteLine($"[Gateway] Rejected unauthenticated protocol {receivedMsg.ProtocolId} for session {session.SessionId}.");
                        }

                    }
                }
                catch (InvalidDataException exception)
                {
                    Console.WriteLine($"[Gateway] Dropped TCP session {session.SessionId}: {exception.Message}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Client connection disconnected abnormally: {ex.Message}");
        }
        finally
        {
            DetachPlayerConnection(stream);
            var detachedPlayerId = await _gatewaySessions.RemoveAsync(stream);
            if (detachedPlayerId.HasValue)
            {
                SchedulePlayerExpiration(detachedPlayerId.Value);
            }
            Console.WriteLine("Client connection closed.");
        }
    }

    private void SendToClient(NetworkStream stream, byte[] packet)
    {
        if (!_gatewaySessions.TrySend(stream, packet))
        {
            throw new IOException("The gateway session is no longer available for this client.");
        }
    }

    private void HandleResumeSession(BaseMessage message, NetworkStream stream)
    {
        ResumeSessionRequest request;
        using (var requestStream = new MemoryStream(message.Body))
        {
            request = ProtoBuf.Serializer.Deserialize<ResumeSessionRequest>(requestStream);
        }

        var success = _gatewaySessions.TryResumeSession(
            stream,
            request.SessionToken,
            request.PlayerId,
            out var resumedGatewaySession);

        GameSessionResumeState? gameState = null;
        var lobby = _lobbyDirectory.GetRoomSnapshot(request.PlayerId);
        var isInRoom = lobby.Players.Any(player => player.PlayerId == request.PlayerId);
        var isInGame = success && _gameSessionService.TryGetResumeState(request.PlayerId, out gameState);
        var pendingSlotIndex = -1;
        var pendingPlayerCount = 0;
        var pendingGameMode = string.Empty;
        var isInBallSelection = success && TryGetPendingBallSelection(
            request.PlayerId,
            out pendingSlotIndex,
            out pendingPlayerCount,
            out pendingGameMode);

        if (success && resumedGatewaySession is not null)
        {
            lock (_connectionLock)
            {
                var staleStreams = _connectionToPlayerId
                    .Where(connection => connection.Value == request.PlayerId && connection.Key != stream)
                    .Select(connection => connection.Key)
                    .ToArray();
                foreach (var staleStream in staleStreams)
                {
                    _connectionToPlayerId.Remove(staleStream);
                }

                _connectionToPlayerId[stream] = request.PlayerId;
            }

            CancelPlayerExpiration(request.PlayerId);
        }

        var response = new ResumeSessionResponse
        {
            Success = success,
            Message = success ? "Session resumed" : "Session token is invalid or expired",
            PlayerId = success ? request.PlayerId : 0,
            SessionToken = success ? request.SessionToken : string.Empty,
            IsInLobby = success,
            IsInGame = success && isInGame,
            SlotIndex = gameState?.SlotIndex ?? (isInBallSelection ? pendingSlotIndex : -1),
            PlayerCount = gameState?.PlayerCount ?? (isInBallSelection ? pendingPlayerCount : lobby.Players.Count),
            GameMode = gameState?.GameMode ?? (isInBallSelection ? pendingGameMode : string.Empty),
            SceneName = gameState?.SceneName ?? (isInBallSelection ? "BallSelection" : isInRoom ? "Room" : "Lobby")
        };

        using (var responseStream = new MemoryStream())
        {
            ProtoBuf.Serializer.Serialize(responseStream, response);
            SendToClient(
                stream,
                new BaseMessage(Protocols.ResumeSessionResponse, responseStream.ToArray()).ToBytes());
        }

        if (success && gameState is not null)
        {
            SendToClient(stream, gameState.SnapshotPacket.ToBytes());
        }

        // A resumed client may still have a cached room list from before the
        // disconnect. Re-send the authoritative snapshot so members who left
        // during the grace period cannot reappear in the room UI.
        if (success && isInRoom)
        {
            var roomStateUpdate = new RoomStateUpdate
            {
                Players = lobby.Players,
                GameCanStart = lobby.CanStart
                    && IsRoomReadyAfterMatch(lobby.Players.Select(player => player.PlayerId).ToArray()),
                GameMode = lobby.GameMode
            };
            var roomStatePacket = MessageCodec.EncodePacket(Protocols.RoomStateUpdate, roomStateUpdate);
            SendToClient(stream, roomStatePacket);
            Console.WriteLine(
                $"[Gateway] Sent resumed room snapshot to player {request.PlayerId}: " +
                $"players={lobby.Players.Count}, canStart={roomStateUpdate.GameCanStart}");
        }

        if (success && isInBallSelection && TryGetBallSelectionState(request.PlayerId, out var selectionState))
        {
            BroadcastBallSelectionState(new[] { request.PlayerId }, selectionState.Selections);
        }

        Console.WriteLine($"[Gateway] Session resume {(success ? "accepted" : "rejected")} for player {request.PlayerId}");
    }

    private void SchedulePlayerExpiration(int playerId)
    {
        CancellationTokenSource cleanup;
        lock (_connectionLock)
        {
            if (_disconnectCleanup.Remove(playerId, out var previous))
            {
                previous.Cancel();
            }

            cleanup = new CancellationTokenSource();
            _disconnectCleanup[playerId] = cleanup;
        }

        Console.WriteLine($"[Gateway] Player {playerId} disconnected; preserving session for {_resumeGracePeriod.TotalSeconds:0} seconds");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_resumeGracePeriod, cleanup.Token);
                if (_gatewaySessions.ExpireDisconnectedPlayer(playerId))
                {
                    Console.WriteLine($"[Gateway] Resume grace period expired for player {playerId}");
                    RemovePlayerFromRoomById(playerId);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_connectionLock)
                {
                    if (_disconnectCleanup.TryGetValue(playerId, out var current)
                        && ReferenceEquals(current, cleanup))
                    {
                        _disconnectCleanup.Remove(playerId);
                    }
                }
                cleanup.Dispose();
            }
        });
    }

    private void CancelPlayerExpiration(int playerId)
    {
        lock (_connectionLock)
        {
            if (_disconnectCleanup.Remove(playerId, out var cleanup))
            {
                cleanup.Cancel();
            }
        }
    }

    /// <summary>
    /// 浠庢埧闂翠腑绉婚櫎鐜╁
    /// </summary>
    private void DetachPlayerConnection(NetworkStream stream)
    {
        // 鏍规嵁杩炴帴鑾峰彇鐜╁ID骞朵粠鎴块棿涓Щ闄?
        int playerId = -1;
        lock (_connectionLock)
        {
            if (_connectionToPlayerId.TryGetValue(stream, out playerId))
            {
                _connectionToPlayerId.Remove(stream);
                Console.WriteLine($"[TCP] Connection removed for Player ID: {playerId}");
            }
        }
        
        if (playerId == -1)
        {
            Console.WriteLine("[TCP] Connection closed, but no player ID found for this connection.");
        }
    }
    
    /// <summary>
    /// 鏍规嵁鐜╁ID浠庢埧闂翠腑绉婚櫎鐜╁
    /// </summary>
    /// <param name="playerId">鐜╁ID</param>
    private void RemovePlayerFromRoomById(int playerId)
    {
        _gameSessionService.StopSessionForPlayer(playerId);
        lock (_postMatchLock)
        {
            _resumeDestinationByPlayer.Remove(playerId);
            _matchReturnSceneByPlayer.Remove(playerId);
        }
        ClearPostMatchForPlayer(playerId);
        var previousPlayers = _lobbyDirectory.GetRoomSnapshot(playerId).Players.Select(player => player.PlayerId).ToArray();
        var selectionWasCleared = TryRemovePendingBallSelection(playerId, out var selectionState, out var gameMode);
        if (_lobbyDirectory.RemovePlayer(playerId))
        {
            Console.WriteLine($"[Lobby] Removed player {playerId}");
            BroadcastRoomStateUpdate(previousPlayers);
            BroadcastLobbyStateUpdate();
        }

        if (selectionWasCleared)
        {
            var remainingPlayers = selectionState.PlayerIds
                .Where(participantId => participantId != playerId)
                .ToArray();
            Console.WriteLine($"[BallSelection] Cleared after reconnect grace expired for player {playerId}");
            if (remainingPlayers.Length > 0)
            {
                BroadcastSceneNotification(remainingPlayers, gameMode, "Room");
            }
        }
    }

    /// <summary>
    /// 骞挎挱鎴块棿鐘舵€佹洿鏂扮粰鎵€鏈夌帺瀹?
    /// </summary>
    private void BroadcastRoomStateUpdate(int playerId)
    {
        var lobby = _lobbyDirectory.GetRoomSnapshot(playerId);
        BroadcastRoomStateUpdate(lobby.Players.Select(player => player.PlayerId).ToArray(), lobby);
    }

    private void BroadcastRoomStateUpdate(IReadOnlyCollection<int> playerIds)
    {
        var remainingPlayer = playerIds.FirstOrDefault(playerId => _lobbyDirectory.TryGetRoomId(playerId, out _));
        var lobby = remainingPlayer > 0
            ? _lobbyDirectory.GetRoomSnapshot(remainingPlayer)
            : new LobbySnapshot(new List<RoomPlayerInfo>(), GameModes.GameThree);
        BroadcastRoomStateUpdate(playerIds, lobby);
    }

    private void BroadcastRoomStateUpdate(IReadOnlyCollection<int> playerIds, LobbySnapshot lobby)
    {
        Console.WriteLine($"[TCP] BroadcastRoomStateUpdate called. roomPlayers={lobby.Players.Count}, connections={_connectionToPlayerId.Count}");
        
        RoomStateUpdate roomStateUpdate = new RoomStateUpdate
        {
            Players = lobby.Players,
            GameCanStart = lobby.CanStart && IsRoomReadyAfterMatch(lobby.Players.Select(player => player.PlayerId).ToArray()),
            GameMode = lobby.GameMode
        };
    
        byte[] responseBody;
        using (var respMs = new System.IO.MemoryStream())
        {
            ProtoBuf.Serializer.Serialize(respMs, roomStateUpdate);
            responseBody = respMs.ToArray();
        }
    
        BaseMessage responseMsg = new BaseMessage(Protocols.RoomStateUpdate, responseBody);
        byte[] dataToSend = responseMsg.ToBytes();
    
        // 瀹為檯鍚戞墍鏈夊凡杩炴帴鐜╁骞挎挱鎴块棿鐘舵€佹洿鏂?
        lock (_connectionLock)
        {
            foreach (var kvp in _connectionToPlayerId.Where(connection => playerIds.Contains(connection.Value)))
            {
                var stream = kvp.Key;
                var playerId = kvp.Value;
                try
                {
                    SendToClient(stream, dataToSend);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TCP] Failed to send room state update to Player ID {playerId}: {ex.Message}");
                }
            }
        }
    
        Console.WriteLine($"[TCP] Broadcast room state update to {lobby.Players.Count} players, GameCanStart: {roomStateUpdate.GameCanStart}");
    
        // 鎵撳嵃鎴块棿鍐呮墍鏈夌帺瀹朵俊鎭?
        Console.WriteLine($"[TCP] Current room players:");
        foreach (var player in lobby.Players)
        {
            Console.WriteLine($"  - Player ID: {player.PlayerId}, Username: {player.Username}, IsHost: {player.IsHost}, IsReady: {player.IsReady}");
        }
    }
    
    private void BroadcastSceneNotification(IReadOnlyList<int> playerIds, string gameMode, string sceneName)
    {
        for (var slotIndex = 0; slotIndex < playerIds.Count; slotIndex++)
        {
            var notification = new GameStartNotification
            {
                Success = true,
                Message = sceneName == "Room" ? "Ball selection cancelled" : "Choose your ball",
                YourSlotIndex = sceneName == "Room" ? -1 : slotIndex,
                GameMode = gameMode,
                SceneName = sceneName
            };
            var packet = MessageCodec.EncodePacket(Protocols.GameStartNotification, notification);
            var playerId = playerIds[slotIndex];
            Console.WriteLine($"[Matchmaking] Sending GameStartNotification scene={sceneName} to player {playerId}.");
            var sent = SendPacketToPlayer(playerId, packet);
            Console.WriteLine(sent
                ? $"[Matchmaking] GameStartNotification sent successfully to player {playerId}."
                : $"[Matchmaking] GameStartNotification NOT sent: no active stream for player {playerId}.");
        }
    }

    private bool SendPacketToPlayer(int playerId, byte[] packet)
    {
        NetworkStream? targetStream = null;
        lock (_connectionLock)
        {
            foreach (var connection in _connectionToPlayerId)
            {
                if (connection.Value == playerId)
                {
                    targetStream = connection.Key;
                    break;
                }
            }
        }

        if (targetStream is not null)
        {
            try
            {
                SendToClient(targetStream, packet);
                return true;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[Matchmaking] Send to player {playerId} failed: {exception.Message}");
                return false;
            }
        }

        Console.WriteLine($"[Matchmaking] No active stream found for player {playerId}.");
        return false;
    }

    private void BindPlayerConnection(NetworkStream stream, int playerId)
    {
        lock (_connectionLock)
        {
            var staleStreams = _connectionToPlayerId
                .Where(connection => connection.Value == playerId && connection.Key != stream)
                .Select(connection => connection.Key)
                .ToArray();
            foreach (var staleStream in staleStreams)
            {
                _connectionToPlayerId.Remove(staleStream);
            }

            _connectionToPlayerId[stream] = playerId;
        }
    }

    private void BroadcastGameStartNotification(
        IReadOnlyList<int> playerIds,
        GameSessionStartResult gameSession)
    {
        Console.WriteLine("[TCP] Broadcasting game start notification to all players with SlotIndex");

        // 涓烘瘡涓帺瀹跺彂閫佸甫鏈夊叾SlotIndex鐨勯€氱煡
        for (int i = 0; i < playerIds.Count; i++)
        {
            var playerId = playerIds[i];
                
            // 鏋勫缓姣忎釜鐜╁鐨勪釜鎬у寲閫氱煡
            GameStartNotification notification = new GameStartNotification
            {
                Success = true,
                Message = "Game is starting now!",
                YourSlotIndex = i, // 鍛婅瘔鐜╁浠栨槸绗嚑涓帺瀹?0-3)
                GameMode = gameSession.GameMode,
                SceneName = gameSession.SceneName
            };
    
            byte[] responseBody;
            using (var respMs = new System.IO.MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(respMs, notification);
                responseBody = respMs.ToArray();
            }
    
            BaseMessage responseMsg = new BaseMessage(Protocols.GameStartNotification, responseBody);
            byte[] dataToSend = responseMsg.ToBytes();
    
            // 鍙戦€佺粰瀵瑰簲鐜╁
            lock (_connectionLock)
            {
                foreach (var kvp in _connectionToPlayerId)
                {
                    if (kvp.Value == playerId)
                    {
                        try
                        {
                            SendToClient(kvp.Key, dataToSend);
                            Console.WriteLine($"[TCP] Sent game start notification to Player ID {playerId}, SlotIndex: {i}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[TCP] Failed to send to Player ID {playerId}: {ex.Message}");
                        }
                        break;
                    }
                }
            }
        }
    }
    
    private sealed class PendingBallSelection
    {
        public PendingBallSelection(int[] playerIds, string gameMode, string returnSceneName)
        {
            PlayerIds = playerIds;
            GameMode = gameMode;
            ReturnSceneName = returnSceneName;
        }

        public int[] PlayerIds { get; }
        public string GameMode { get; }
        public string ReturnSceneName { get; }
        public Dictionary<int, string> BallSelections { get; } = new();
    }

    private sealed class PostMatchBarrier
    {
        public PostMatchBarrier(int[] playerIds, string gameMode)
        {
            PlayerIds = playerIds;
            GameMode = gameMode;
        }

        public int[] PlayerIds { get; }
        public string GameMode { get; }
        public HashSet<int> ReturnedPlayerIds { get; } = new();
    }

    private sealed record ResumeDestination(string SceneName, DateTime ExpiresAtUtc);

    private enum BallSelectionDisposition
    {
        Created,
        Resumed,
        Replaced
    }

    private sealed record BallSelectionPreparation(
        IReadOnlyList<int> PlayerIds,
        IReadOnlyCollection<KeyValuePair<int, string>> Selections,
        BallSelectionDisposition Disposition);

    private sealed record CompletedBallSelection(
        int[] PlayerIds,
        string GameMode,
        IReadOnlyDictionary<int, string> BallSelections,
        string ReturnSceneName);

    private sealed record BallSelectionState(
        IReadOnlyList<int> PlayerIds,
        IReadOnlyCollection<KeyValuePair<int, string>> Selections,
        string ReturnSceneName)
    {
        public static BallSelectionState Empty { get; } = new(
            Array.Empty<int>(),
            Array.Empty<KeyValuePair<int, string>>(),
            "Room");
    }

    public void Stop()
    {
        Console.WriteLine("\nStopping server...");
        _isRunning = false;
        _cancellationTokenSource?.Cancel();

        try
        {
            _tcpListener?.Stop();
            _udpClient?.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception while stopping server: {ex.Message}");
        }

        _gameSessionService.StopAll();

        lock (_connectionLock)
        {
            foreach (var cleanup in _disconnectCleanup.Values)
            {
                cleanup.Cancel();
            }
            _disconnectCleanup.Clear();
        }
        
        // 娓呯悊杩炴帴鏄犲皠
        lock (_connectionLock)
        {
            _connectionToPlayerId.Clear();
        }
        _gatewaySessions.DisposeAll();

        Console.WriteLine("Server stopped completely.");
    }

    private void BroadcastGamePacket(IReadOnlyList<int> playerIds, byte[] data)
    {
        var participantIds = new HashSet<int>(playerIds);
        List<(NetworkStream Stream, int PlayerId)> recipients;
        lock (_connectionLock)
        {
            recipients = _connectionToPlayerId
                .Where(connection => participantIds.Contains(connection.Value))
                .Select(connection => (connection.Key, connection.Value))
                .ToList();
        }

        foreach (var recipient in recipients)
        {
            try
            {
                SendToClient(recipient.Stream, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameServer] Failed to broadcast game packet to player {recipient.PlayerId}: {ex.Message}");
            }
        }
    }
}
