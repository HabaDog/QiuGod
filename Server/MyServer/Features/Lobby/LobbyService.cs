using GameNetworkCommon;

/// <summary>
/// Owns lobby state and rules. It has no dependency on TCP connections or packet types.
/// </summary>
public sealed class LobbyService
{
    private readonly List<RoomPlayerInfo> _players;
    private readonly object _lock;
    private string _gameMode = GameModes.GameThree;

    public LobbyService(List<RoomPlayerInfo>? players = null, object? synchronizationLock = null)
    {
        _players = players ?? new List<RoomPlayerInfo>();
        _lock = synchronizationLock ?? new object();
    }

    public LobbyEnterResult Enter(int playerId, string username)
    {
        lock (_lock)
        {
            if (_players.Any(player => player.PlayerId == playerId))
            {
                return new LobbyEnterResult(true, false, "Already in room", SnapshotUnsafe());
            }

            _players.Add(new RoomPlayerInfo
            {
                PlayerId = playerId,
                Username = username,
                IsReady = false,
                IsHost = _players.Count == 0,
                SelectedBallId = GameThreeSession.DefaultBallId
            });

            return new LobbyEnterResult(true, true, "Successfully entered room", SnapshotUnsafe());
        }
    }

    public LobbyMutationResult SetReady(int playerId, bool isReady)
    {
        lock (_lock)
        {
            var player = _players.FirstOrDefault(candidate => candidate.PlayerId == playerId);
            if (player is null)
            {
                return new LobbyMutationResult(false, "Player not found in room", SnapshotUnsafe());
            }

            player.IsReady = isReady;
            return new LobbyMutationResult(true, "Ready status updated", SnapshotUnsafe());
        }
    }

    public LobbyMutationResult ResetReadyStates()
    {
        lock (_lock)
        {
            var changed = false;
            foreach (var player in _players)
            {
                if (player.IsReady)
                {
                    player.IsReady = false;
                    changed = true;
                }
            }

            return new LobbyMutationResult(
                true,
                changed ? "Ready states reset" : "Ready states already reset",
                SnapshotUnsafe());
        }
    }

    public LobbyMutationResult SetGameMode(int playerId, string? gameMode)
    {
        lock (_lock)
        {
            var requester = _players.FirstOrDefault(candidate => candidate.PlayerId == playerId);
            if (requester is null || !requester.IsHost)
            {
                return new LobbyMutationResult(false, "Only host can select the game mode", SnapshotUnsafe());
            }

            if (!GameModes.TryNormalizeSupported(gameMode, out var normalizedMode))
            {
                return new LobbyMutationResult(false, "Unsupported game mode", SnapshotUnsafe());
            }

            if (string.Equals(_gameMode, normalizedMode, StringComparison.Ordinal))
            {
                return new LobbyMutationResult(true, "Game mode is already selected", SnapshotUnsafe());
            }

            _gameMode = normalizedMode;
            foreach (var player in _players)
            {
                player.IsReady = false;
            }

            return new LobbyMutationResult(true, "Game mode selected; ready states were reset", SnapshotUnsafe());
        }
    }

    public LobbyMutationResult SetGameThreeBall(int playerId, string? ballId)
    {
        lock (_lock)
        {
            var player = _players.FirstOrDefault(candidate => candidate.PlayerId == playerId);
            if (player is null)
            {
                return new LobbyMutationResult(false, "Player not found in room", SnapshotUnsafe());
            }

            if (!GameThreeSession.TryNormalizeBallId(ballId, out var normalizedBallId))
            {
                return new LobbyMutationResult(false, "Unsupported GamePlayThree ball", SnapshotUnsafe());
            }

            if (!string.Equals(player.SelectedBallId, normalizedBallId, StringComparison.Ordinal))
            {
                player.SelectedBallId = normalizedBallId;
                player.IsReady = false;
            }

            return new LobbyMutationResult(true, "GamePlayThree ball selected", SnapshotUnsafe());
        }
    }

    public LobbyStartResult TryStart(int playerId)
    {
        lock (_lock)
        {
            var requester = _players.FirstOrDefault(candidate => candidate.PlayerId == playerId);
            if (requester is null || !requester.IsHost)
            {
                return CreateStartResultUnsafe(false, "Only host can start the game", Array.Empty<int>());
            }

            if (!requester.IsReady || _players.Count == 0 || _players.Any(player => !player.IsReady))
            {
                return CreateStartResultUnsafe(false, "Not all players are ready", Array.Empty<int>());
            }

            return CreateStartResultUnsafe(
                true,
                "Game started successfully",
                _players.Select(player => player.PlayerId).ToList());
        }
    }

    public LobbyMutationResult Remove(int playerId)
    {
        lock (_lock)
        {
            var player = _players.FirstOrDefault(candidate => candidate.PlayerId == playerId);
            if (player is null)
            {
                return new LobbyMutationResult(false, "Player was not in the room", SnapshotUnsafe());
            }

            var wasHost = player.IsHost;
            _players.Remove(player);
            if (wasHost && _players.Count > 0)
            {
                _players[0].IsHost = true;
            }

            return new LobbyMutationResult(true, "Player removed", SnapshotUnsafe());
        }
    }

    public LobbySnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new LobbySnapshot(SnapshotUnsafe(), _gameMode);
        }
    }

    private LobbyStartResult CreateStartResultUnsafe(
        bool success,
        string message,
        IReadOnlyList<int> playerIds)
    {
        return new LobbyStartResult(
            success,
            message,
            playerIds,
            _gameMode,
            _players.ToDictionary(player => player.PlayerId, player => player.SelectedBallId));
    }

    private List<RoomPlayerInfo> SnapshotUnsafe()
    {
        return _players.Select(player => new RoomPlayerInfo
        {
                PlayerId = player.PlayerId,
                Username = player.Username,
                IsReady = player.IsReady,
                IsHost = player.IsHost,
                SelectedBallId = player.SelectedBallId
            }).ToList();
    }
}

public sealed record LobbySnapshot(List<RoomPlayerInfo> Players, string GameMode)
{
    public bool CanStart => Players.Count > 0 && Players.All(player => player.IsReady);
}

public sealed record LobbyEnterResult(bool Success, bool Changed, string Message, List<RoomPlayerInfo> Players);
public sealed record LobbyMutationResult(bool Success, string Message, List<RoomPlayerInfo> Players);
public sealed record LobbyStartResult(
    bool Success,
    string Message,
    IReadOnlyList<int> PlayerIds,
    string GameMode,
    IReadOnlyDictionary<int, string> BallSelections);
