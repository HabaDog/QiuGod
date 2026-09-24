using Grpc.Core;
using Grpc.Net.Client;
using MyDemo.InterServer;
using ProtoBuf;

public sealed class LobbyGrpcService : MyDemo.InterServer.LobbyService.LobbyServiceBase
{
    private readonly LobbyDirectoryService _lobbyDirectory;
    private readonly GatewayCallback.GatewayCallbackClient _gatewayCallback;
    private readonly object _ballSelectionLock = new();
    private readonly Dictionary<int, PendingBallSelection> _pendingBallSelectionByPlayer = new();
    private readonly Dictionary<int, string> _returnSceneByPlayer = new();

    /// <summary>
    /// 初始化大厅gRPC服务，注入大厅目录和网关回调客户端
    /// </summary>
    public LobbyGrpcService(
        LobbyDirectoryService lobbyDirectory,
        GatewayCallback.GatewayCallbackClient gatewayCallback)
    {
        _lobbyDirectory = lobbyDirectory;
        _gatewayCallback = gatewayCallback;
    }

    // ── Room management ──────────────────────────────────────────

    /// <summary>
    /// 创建房间
    /// </summary>
    public override Task<CreateRoomResponse> CreateRoom(
        CreateRoomRequest request,
        ServerCallContext context)
    {
        var result = _lobbyDirectory.CreateRoom(
            request.PlayerId,
            request.Username,
            string.IsNullOrWhiteSpace(request.RoomName) ? null : request.RoomName,
            string.IsNullOrWhiteSpace(request.GameMode) ? null : request.GameMode,
            request.IsPrivate,
            string.IsNullOrWhiteSpace(request.JoinCode) ? null : request.JoinCode);

        if (result.Success)
        {
            BroadcastLobbyState(request.PlayerId);
        }

        return Task.FromResult(new CreateRoomResponse
        {
            Success = result.Success,
            Message = result.Message,
            RoomId = result.RoomId,
            JoinCode = result.JoinCode
        });
    }

    /// <summary>
    /// 加入房间
    /// </summary>
    public override Task<JoinRoomResponse> JoinRoom(
        JoinRoomRequest request,
        ServerCallContext context)
    {
        var result = _lobbyDirectory.JoinRoom(
            request.PlayerId,
            request.Username,
            string.IsNullOrWhiteSpace(request.RoomId) ? null : request.RoomId,
            string.IsNullOrWhiteSpace(request.JoinCode) ? null : request.JoinCode);

        if (result.Success)
        {
            BroadcastRoomState(request.PlayerId);
            BroadcastLobbyState(request.PlayerId);
        }

        return Task.FromResult(new JoinRoomResponse
        {
            Success = result.Success,
            Message = result.Message,
            RoomId = result.RoomId
        });
    }

    /// <summary>
    /// 离开房间
    /// </summary>
    public override Task<LeaveRoomResponse> LeaveRoom(
        LeaveRoomRequest request,
        ServerCallContext context)
    {
        var result = _lobbyDirectory.LeaveRoom(request.PlayerId);

        if (result.Success)
        {
            BroadcastLobbyState(request.PlayerId);
        }

        return Task.FromResult(new LeaveRoomResponse
        {
            Success = result.Success,
            Message = result.Message
        });
    }

    /// <summary>
    /// 进入当前房间
    /// </summary>
    public override Task<EnterRoomResponse> EnterRoom(
        EnterRoomRequest request,
        ServerCallContext context)
    {
        var result = _lobbyDirectory.EnterCurrentRoom(request.PlayerId, request.Username);

        var response = new EnterRoomResponse
        {
            Success = result.Success,
            Message = result.Message
        };
        foreach (var player in result.Players)
        {
            response.Players.Add(new RoomPlayer
            {
                PlayerId = player.PlayerId,
                Username = player.Username,
                IsReady = player.IsReady,
                IsHost = player.IsHost,
                SelectedBallId = player.SelectedBallId
            });
        }

        if (result.Changed)
        {
            BroadcastRoomState(request.PlayerId);
        }

        return Task.FromResult(response);
    }

    // ── Lobby actions ────────────────────────────────────────────

    /// <summary>
    /// 切换准备状态
    /// </summary>
    public override Task<ToggleReadyResponse> ToggleReady(
        ToggleReadyRequest request,
        ServerCallContext context)
    {
        var result = _lobbyDirectory.SetReady(request.PlayerId, request.Ready);

        if (result.Success)
        {
            BroadcastRoomState(request.PlayerId);
        }

        return Task.FromResult(new ToggleReadyResponse
        {
            Success = result.Success,
            Message = result.Message
        });
    }

    /// <summary>
    /// 选择游戏模式
    /// </summary>
    public override Task<SelectGameModeResponse> SelectGameMode(
        SelectGameModeRequest request,
        ServerCallContext context)
    {
        var result = _lobbyDirectory.SetGameMode(
            request.PlayerId,
            string.IsNullOrWhiteSpace(request.GameMode) ? null : request.GameMode);

        if (result.Success)
        {
            BroadcastRoomState(request.PlayerId);
        }

        return Task.FromResult(new SelectGameModeResponse
        {
            Success = result.Success,
            Message = result.Message,
            GameMode = request.GameMode
        });
    }

    /// <summary>
    /// 选择球（GameThree模式下的球选择确认流程）
    /// </summary>
    public override Task<SelectBallResponse> SelectBall(
        SelectBallRequest request,
        ServerCallContext context)
    {
        if (!GameThreeSession.TryNormalizeBallId(request.BallId, out var normalizedBallId))
        {
            return Task.FromResult(new SelectBallResponse
            {
                Success = false,
                Message = "Unknown ball selection"
            });
        }

        if (!TryConfirmPendingBallSelection(
                request.PlayerId,
                normalizedBallId,
                out var completedSelection,
                out var selectionState,
                out var rejectionMessage))
        {
            return Task.FromResult(new SelectBallResponse
            {
                Success = false,
                Message = rejectionMessage
            });
        }

        var response = new SelectBallResponse
        {
            Success = true,
            Message = completedSelection is null
                ? "Ball confirmed. Waiting for other players."
                : "All balls confirmed. Starting match.",
            BallId = normalizedBallId,
            AllConfirmed = completedSelection is not null
        };

        if (completedSelection is not null)
        {
            response.PlayerIds.AddRange(completedSelection.PlayerIds);
            response.GameMode = completedSelection.GameMode;
            response.ReturnSceneName = completedSelection.ReturnSceneName;
            foreach (var selection in completedSelection.BallSelections)
            {
                response.BallSelections.Add(selection.Key, selection.Value);
            }
        }
        else
        {
            response.PlayerIds.AddRange(selectionState.PlayerIds);
            response.GameMode = string.Empty;
            response.ReturnSceneName = selectionState.ReturnSceneName;
            foreach (var selection in selectionState.Selections)
            {
                response.BallSelections.Add(selection.Key, selection.Value);
            }
        }

        BroadcastBallSelectionState(
            completedSelection?.PlayerIds ?? selectionState.PlayerIds,
            completedSelection?.BallSelections
                ?? selectionState.Selections.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

        // The Gateway owns the client-facing transition after it has
        // successfully created the authoritative game session. Do not send a
        // Room notification here: it races the Gateway's gameplay notification
        // and leaves the client with a completed selection but no match.

        return Task.FromResult(response);
    }

    /// <summary>
    /// 开始游戏
    /// </summary>
    public override Task<StartGameResponse> StartGame(
        StartGameRequest request,
        ServerCallContext context)
    {
        var lobbyResult = _lobbyDirectory.TryStart(request.PlayerId);

        if (!lobbyResult.Success)
        {
            return Task.FromResult(new StartGameResponse
            {
                Success = false,
                Message = lobbyResult.Message
            });
        }

        var response = new StartGameResponse
        {
            Success = true,
            Message = lobbyResult.Message,
            GameMode = lobbyResult.GameMode
        };
        response.PlayerIds.AddRange(lobbyResult.PlayerIds);

        if (string.Equals(lobbyResult.GameMode, GameModes.GameThree, StringComparison.Ordinal))
        {
            // Prepare ball selection state for GameThree
            var returnScene = _returnSceneByPlayer.TryGetValue(request.PlayerId, out var scene)
                ? scene
                : "Room";
            var ballSelection = PrepareBallSelection(
                lobbyResult.PlayerIds,
                lobbyResult.GameMode,
                returnScene);
            Console.WriteLine(
                $"[BallSelection] {ballSelection.Disposition} for players " +
                $"[{string.Join(",", ballSelection.PlayerIds)}], " +
                $"confirmed={ballSelection.Selections.Count}");

            response.NeedsBallSelection = true;
            response.ReturnSceneName = returnScene;

            BroadcastBallSelectionState(
                ballSelection.PlayerIds,
                ballSelection.Selections.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
        }
        else
        {
            response.NeedsBallSelection = false;
            response.ReturnSceneName = _returnSceneByPlayer.TryGetValue(request.PlayerId, out var scene)
                ? scene
                : "Room";
            foreach (var selection in lobbyResult.BallSelections)
            {
                response.BallSelections.Add(selection.Key, selection.Value);
            }
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// 取消球选择
    /// </summary>
    public override Task<CancelBallSelectionResponse> CancelBallSelection(
        CancelBallSelectionRequest request,
        ServerCallContext context)
    {
        if (!TryRemovePendingBallSelection(
                request.PlayerId,
                out var affectedPlayerIds,
                out var gameMode,
                out var returnSceneName))
        {
            return Task.FromResult(new CancelBallSelectionResponse
            {
                Success = false,
                Message = "No active ball selection"
            });
        }

        BroadcastSceneNotification(affectedPlayerIds, gameMode, returnSceneName);

        return Task.FromResult(new CancelBallSelectionResponse
        {
            Success = true,
            Message = "Ball selection cancelled",
            GameMode = gameMode,
            ReturnSceneName = returnSceneName,
            AffectedPlayerIds = { affectedPlayerIds }
        });
    }

    // ── Snapshots ────────────────────────────────────────────────

    /// <summary>
    /// 获取大厅快照
    /// </summary>
    public override Task<LobbySnapshotResponse> GetLobbySnapshot(
        LobbySnapshotRequest request,
        ServerCallContext context)
    {
        var snapshot = _lobbyDirectory.GetLobbySnapshot(request.PlayerId);

        var response = new LobbySnapshotResponse
        {
            Success = true,
            Message = "OK",
            IsMatchmaking = snapshot.IsMatchmaking,
            QueuePosition = snapshot.QueuePosition
        };

        foreach (var room in snapshot.Rooms)
        {
            response.Rooms.Add(new LobbyRoomInfo
            {
                RoomId = room.RoomId,
                RoomName = room.RoomName,
                GameMode = room.GameMode,
                PlayerCount = room.PlayerCount,
                MaxPlayers = room.MaxPlayers,
                IsPrivate = room.IsPrivate
            });
        }

        if (snapshot.Party is not null)
        {
            response.Party = ToGrpcPartyInfo(snapshot.Party);
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// 获取房间快照
    /// </summary>
    public override Task<RoomSnapshotResponse> GetRoomSnapshot(
        RoomSnapshotRequest request,
        ServerCallContext context)
    {
        var snapshot = _lobbyDirectory.GetRoomSnapshot(request.PlayerId);

        var response = new RoomSnapshotResponse
        {
            Success = true,
            Message = "OK",
            GameMode = snapshot.GameMode,
            CanStart = snapshot.CanStart
        };

        foreach (var player in snapshot.Players)
        {
            response.Players.Add(new RoomPlayer
            {
                PlayerId = player.PlayerId,
                Username = player.Username,
                IsReady = player.IsReady,
                IsHost = player.IsHost,
                SelectedBallId = player.SelectedBallId
            });
        }

        return Task.FromResult(response);
    }

    // ── Party management ─────────────────────────────────────────

    /// <summary>
    /// 创建队伍
    /// </summary>
    public override Task<CreatePartyResponse> CreateParty(
        CreatePartyRequest request,
        ServerCallContext context)
    {
        var result = _lobbyDirectory.CreateParty(request.PlayerId, request.Username);

        if (result.Success)
        {
            BroadcastLobbyState(request.PlayerId);
        }

        var response = new CreatePartyResponse
        {
            Success = result.Success,
            Message = result.Message
        };
        if (result.Party is not null)
        {
            response.Party = ToGrpcPartyInfo(result.Party);
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// 加入队伍
    /// </summary>
    public override Task<JoinPartyResponse> JoinParty(
        JoinPartyRequest request,
        ServerCallContext context)
    {
        var result = _lobbyDirectory.JoinParty(
            request.PlayerId,
            request.Username,
            string.IsNullOrWhiteSpace(request.JoinCode) ? null : request.JoinCode);

        if (result.Success)
        {
            BroadcastLobbyState(request.PlayerId);
        }

        var response = new JoinPartyResponse
        {
            Success = result.Success,
            Message = result.Message
        };
        if (result.Party is not null)
        {
            response.Party = ToGrpcPartyInfo(result.Party);
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// 离开队伍
    /// </summary>
    public override Task<LeavePartyResponse> LeaveParty(
        LeavePartyRequest request,
        ServerCallContext context)
    {
        var result = _lobbyDirectory.LeaveParty(request.PlayerId);

        if (result)
        {
            BroadcastLobbyState(request.PlayerId);
        }

        return Task.FromResult(new LeavePartyResponse
        {
            Success = result,
            Message = result ? "Left party" : "Player is not in a party"
        });
    }

    // ── Player management ────────────────────────────────────────

    /// <summary>
    /// 移除玩家
    /// </summary>
    public override Task<RemovePlayerResponse> RemovePlayer(
        RemovePlayerRequest request,
        ServerCallContext context)
    {
        var ballSelectionCleared = TryRemovePendingBallSelection(
            request.PlayerId,
            out var affectedPlayerIds,
            out var gameMode,
            out var returnSceneName);

        var lobbyChanged = _lobbyDirectory.RemovePlayer(request.PlayerId);

        if (lobbyChanged)
        {
            BroadcastLobbyState(request.PlayerId);
        }

        if (ballSelectionCleared && affectedPlayerIds.Length > 0)
        {
            BroadcastSceneNotification(affectedPlayerIds, gameMode, returnSceneName);
        }

        var response = new RemovePlayerResponse
        {
            Success = lobbyChanged || ballSelectionCleared,
            Message = "Player removed",
            BallSelectionCleared = ballSelectionCleared,
            GameMode = ballSelectionCleared ? gameMode : string.Empty,
            ReturnSceneName = ballSelectionCleared ? returnSceneName : string.Empty
        };
        if (affectedPlayerIds.Length > 0)
        {
            response.AffectedPlayerIds.AddRange(affectedPlayerIds);
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// 重置所有玩家的准备状态
    /// </summary>
    public override Task<ResetReadyResponse> ResetReadyStates(
        ResetReadyRequest request,
        ServerCallContext context)
    {
        var result = _lobbyDirectory.ResetReadyStates(request.PlayerId);

        if (result.Success)
        {
            BroadcastRoomState(request.PlayerId);
        }

        return Task.FromResult(new ResetReadyResponse
        {
            Success = result.Success,
            Message = result.Message
        });
    }

    /// <summary>
    /// 设置玩家返回的目标场景
    /// </summary>
    public override Task<SetReturnDestinationResponse> SetReturnDestination(
        SetReturnDestinationRequest request,
        ServerCallContext context)
    {
        _returnSceneByPlayer[request.PlayerId] = request.SceneName;

        return Task.FromResult(new SetReturnDestinationResponse
        {
            Success = true
        });
    }

    // ── Broadcasting helpers ─────────────────────────────────────

    /// <summary>
    /// 向房间内所有玩家广播房间状态更新
    /// </summary>
    private void BroadcastRoomState(int playerId)
    {
        _ = BroadcastRoomStateAsync(playerId);
    }

    private async Task BroadcastRoomStateAsync(int playerId)
    {
        var snapshot = _lobbyDirectory.GetRoomSnapshot(playerId);
        var roomState = new GameNetworkCommon.RoomStateUpdate
        {
            GameMode = snapshot.GameMode,
            GameCanStart = snapshot.CanStart
        };
        foreach (var player in snapshot.Players)
        {
            roomState.Players.Add(player);
        }

        var playerIds = snapshot.Players.Select(p => p.PlayerId).ToList();
        if (playerIds.Count == 0)
        {
            return;
        }

        try
        {
            var request = new BroadcastRoomStateRequest
            {
                RoomStateData = Google.Protobuf.ByteString.CopyFrom(SerializeProtobuf(roomState))
            };
            request.PlayerIds.AddRange(playerIds);
            await _gatewayCallback.BroadcastRoomStateAsync(request);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LobbyGrpc] BroadcastRoomState failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 向所有玩家广播大厅状态更新
    /// </summary>
    private void BroadcastLobbyState(int playerId)
    {
        _ = BroadcastLobbyStateAsync(playerId);
    }

    private async Task BroadcastLobbyStateAsync(int playerId)
    {
        try
        {
            var snapshot = _lobbyDirectory.GetLobbySnapshot(playerId);
            var lobbyState = new GameNetworkCommon.LobbyStateUpdate
            {
                IsMatchmaking = snapshot.IsMatchmaking,
                QueuePosition = snapshot.QueuePosition
            };
            foreach (var room in snapshot.Rooms)
            {
                lobbyState.Rooms.Add(room);
            }

            if (snapshot.Party is not null)
            {
                lobbyState.Party = snapshot.Party;
            }

            var request = new BroadcastLobbyStateRequest
            {
                LobbyStateData = Google.Protobuf.ByteString.CopyFrom(SerializeProtobuf(lobbyState))
            };
            await _gatewayCallback.BroadcastLobbyStateAsync(request);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LobbyGrpc] BroadcastLobbyState failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 向指定玩家广播场景切换通知
    /// </summary>
    private void BroadcastSceneNotification(
        IReadOnlyList<int> playerIds,
        string gameMode,
        string sceneName)
    {
        try
        {
            var request = new BroadcastSceneNotificationRequest
            {
                GameMode = gameMode,
                SceneName = sceneName
            };
            request.PlayerIds.AddRange(playerIds);
            _gatewayCallback.BroadcastSceneNotification(request);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LobbyGrpc] BroadcastSceneNotification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 向指定玩家广播球选择状态更新
    /// </summary>
    private void BroadcastBallSelectionState(
        IReadOnlyList<int> playerIds,
        IReadOnlyDictionary<int, string> selections)
    {
        try
        {
            var selectionState = new GameNetworkCommon.GameThreeBallSelectionStateUpdate();
            foreach (var selection in selections.OrderBy(s => s.Key))
            {
                selectionState.Selections.Add(new GameNetworkCommon.GameThreeBallSelectionEntry
                {
                    PlayerId = selection.Key,
                    BallId = selection.Value
                });
            }

            var request = new BroadcastBallSelectionStateRequest
            {
                SelectionStateData = Google.Protobuf.ByteString.CopyFrom(SerializeProtobuf(selectionState))
            };
            request.PlayerIds.AddRange(playerIds);
            _gatewayCallback.BroadcastBallSelectionState(request);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LobbyGrpc] BroadcastBallSelectionState failed: {ex.Message}");
        }
    }

    // ── Ball selection state ─────────────────────────────────────

    /// <summary>
    /// 为GameThree模式准备球选择状态，支持创建、恢复和替换
    /// </summary>
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

    /// <summary>
    /// 尝试确认待处理的球选择，若所有玩家均已选择则完成
    /// </summary>
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

    /// <summary>
    /// 尝试移除指定玩家的待处理球选择
    /// </summary>
    private bool TryRemovePendingBallSelection(
        int playerId,
        out int[] affectedPlayerIds,
        out string gameMode,
        out string returnSceneName)
    {
        lock (_ballSelectionLock)
        {
            if (!_pendingBallSelectionByPlayer.TryGetValue(playerId, out var pending))
            {
                affectedPlayerIds = Array.Empty<int>();
                gameMode = string.Empty;
                returnSceneName = string.Empty;
                return false;
            }

            affectedPlayerIds = pending.PlayerIds;
            gameMode = pending.GameMode;
            returnSceneName = pending.ReturnSceneName;
            RemovePendingBallSelectionUnsafe(pending);
            return true;
        }
    }

    /// <summary>
    /// 移除待处理球选择（不加锁，调用方需持有锁）
    /// </summary>
    private void RemovePendingBallSelectionUnsafe(PendingBallSelection pending)
    {
        foreach (var participantId in pending.PlayerIds)
        {
            _pendingBallSelectionByPlayer.Remove(participantId);
        }
    }

    /// <summary>
    /// 根据待处理球选择创建BallSelectionPreparation对象
    /// </summary>
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

    // ── Serialization ────────────────────────────────────────────

    /// <summary>
    /// 使用Protobuf序列化消息为字节数组
    /// </summary>
    private static byte[] SerializeProtobuf<T>(T message)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, message);
        return stream.ToArray();
    }

    /// <summary>
    /// 将PartyInfo转换为gRPC格式的PartyInfo
    /// </summary>
    private static PartyInfo ToGrpcPartyInfo(GameNetworkCommon.PartyInfo party)
    {
        var result = new PartyInfo
        {
            PartyId = party.PartyId,
            JoinCode = party.JoinCode
        };
        foreach (var member in party.Members)
        {
            result.Members.Add(new PartyMember
            {
                PlayerId = member.PlayerId,
                Username = member.Username,
                IsLeader = member.IsLeader
            });
        }
        return result;
    }

    // ── Internal types for ball selection ────────────────────────

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
}
