using GameNetworkCommon;

/// <summary>
/// Owns the global lobby directory. Individual room rules remain in
/// <see cref="LobbyService"/>, keeping discovery/matchmaking separate from the
/// ready/start lifecycle.
/// </summary>
public sealed class LobbyDirectoryService
{
    public const int MaxRoomPlayers = 4;
    private static readonly TimeSpan MatchmakingTimeout = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly Dictionary<string, ManagedRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _roomByPlayer = new();
    private readonly Dictionary<string, PartyState> _parties = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _partyByPlayer = new();
    private readonly Dictionary<int, PlayerIdentity> _players = new();
    private readonly List<MatchTicket> _matchQueue = new();

    public event Action<IReadOnlyList<int>, string>? MatchFound;

    public LobbyDirectorySnapshot GetLobbySnapshot(int playerId)
    {
        lock (_lock)
        {
            return CreateLobbySnapshotUnsafe(playerId);
        }
    }

    public LobbyDirectoryActionResult CreateRoom(
        int playerId,
        string username,
        string? roomName,
        string? gameMode,
        bool isPrivate,
        string? requestedJoinCode = null)
    {
        lock (_lock)
        {
            RememberPlayerUnsafe(playerId, username);
            if (_roomByPlayer.ContainsKey(playerId))
            {
                return new(false, "Leave the current room first", string.Empty, string.Empty);
            }

            if (!GameModes.TryNormalizeSupported(gameMode, out var normalizedMode))
            {
                return new(false, "Unsupported game mode", string.Empty, string.Empty);
            }

            var requestedRoomId = ResolveRequestedRoomIdUnsafe(roomName);
            if (requestedRoomId is null)
            {
                return new(false, "Room ID is required, invalid, or already in use", string.Empty, string.Empty);
            }

            // A user-created room always uses the creator's password as its
            // invitation code. Do not silently replace an omitted password
            // with a random code that nobody else knows.
            if (isPrivate && string.IsNullOrWhiteSpace(requestedJoinCode))
            {
                return new(false, "Room password is required", string.Empty, string.Empty);
            }
            var joinCode = string.IsNullOrWhiteSpace(requestedJoinCode)
                ? string.Empty
                : ResolveRequestedJoinCodeUnsafe(requestedJoinCode);
            if (joinCode is null)
            {
                return new(false, "Join code is already in use or invalid", string.Empty, string.Empty);
            }

            RemoveFromQueueUnsafe(playerId);
            var room = CreateRoomUnsafe(requestedRoomId, normalizedMode, isPrivate, joinCode, requestedRoomId);
            EnterRoomUnsafe(room, playerId, username);
            return new(true, "Room created", room.RoomId, room.JoinCode);
        }
    }

    public LobbyDirectoryActionResult JoinRoom(int playerId, string username, string? roomId, string? joinCode)
    {
        lock (_lock)
        {
            RememberPlayerUnsafe(playerId, username);
            if (_roomByPlayer.TryGetValue(playerId, out var currentRoomId))
            {
                return string.Equals(currentRoomId, roomId, StringComparison.OrdinalIgnoreCase)
                    ? new(true, "Already in room", currentRoomId, _rooms[currentRoomId].JoinCode)
                    : new(false, "Leave the current room first", string.Empty, string.Empty);
            }

            if (string.IsNullOrWhiteSpace(roomId))
            {
                return new(false, "Room ID is required", string.Empty, string.Empty);
            }

            // The room ID identifies the room. Private rooms additionally
            // require the creator's password as their invitation code.
            _rooms.TryGetValue(roomId.Trim(), out var room);
            if (room is null)
            {
                return new(false, "Room ID not found", string.Empty, string.Empty);
            }
            if (room.IsPrivate
                && (string.IsNullOrWhiteSpace(joinCode)
                    || !string.Equals(room.JoinCode, joinCode.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return new(false, "Room password is invalid", string.Empty, string.Empty);
            }
            if (room.State.GetSnapshot().Players.Count >= MaxRoomPlayers)
            {
                return new(false, "Room is full", string.Empty, string.Empty);
            }

            RemoveFromQueueUnsafe(playerId);
            EnterRoomUnsafe(room, playerId, username);
            return new(true, "Joined room", room.RoomId, room.JoinCode);
        }
    }

    public LobbyDirectoryActionResult LeaveRoom(int playerId)
    {
        lock (_lock)
        {
            return LeaveRoomUnsafe(playerId)
                ? new(true, "Left room", string.Empty, string.Empty)
                : new(false, "Player is not in a room", string.Empty, string.Empty);
        }
    }

    public MatchmakingResult JoinMatchmaking(int playerId, string username, string? gameMode)
    {
        MatchReady? readyMatch = null;
        MatchmakingResult result;
        lock (_lock)
        {
            RememberPlayerUnsafe(playerId, username);
            if (_roomByPlayer.ContainsKey(playerId))
            {
                return new(false, "Leave the current room first", false, string.Empty, 0);
            }
            if (!GameModes.TryNormalizeSupported(gameMode, out var normalizedMode))
            {
                return new(false, "Unsupported game mode", false, string.Empty, 0);
            }

            var group = GetPartyPlayerIdsUnsafe(playerId);
            if (_partyByPlayer.TryGetValue(playerId, out var partyId)
                && _parties.TryGetValue(partyId, out var party)
                && party.LeaderPlayerId != playerId)
            {
                return new(false, "Only the party leader can start matchmaking", false, string.Empty, 0);
            }
            if (group.Any(_roomByPlayer.ContainsKey))
            {
                return new(false, "A party member is already in a room", false, string.Empty, 0);
            }
            if (_matchQueue.Any(ticket => ticket.PlayerIds.Contains(playerId)))
            {
                var existingPosition = QueuePositionUnsafe(playerId);
                return new(true, "Already matchmaking", false, string.Empty, existingPosition);
            }

            foreach (var memberId in group)
            {
                RemoveFromQueueUnsafe(memberId);
            }
            _matchQueue.Add(new MatchTicket(Guid.NewGuid(), normalizedMode, group.ToArray(), DateTime.UtcNow));

            if (TryCreateMatchUnsafe(normalizedMode, DateTime.UtcNow, allowExpiredPartial: false, out var createdMatch))
            {
                readyMatch = createdMatch;
                result = new(true, "Match found", true, createdMatch.RoomId, 0);
            }
            else
            {
                result = new(true, "Searching for players (30 seconds maximum)", false, string.Empty, QueuePositionUnsafe(playerId));
            }
        }

        if (readyMatch is not null)
        {
            MatchFound?.Invoke(readyMatch.PlayerIds, readyMatch.RoomId);
        }
        return result;
    }

    public void ProcessMatchmakingTimeouts(DateTime nowUtc)
    {
        var readyMatches = new List<MatchReady>();
        lock (_lock)
        {
            var gameModes = _matchQueue
                .Select(ticket => ticket.GameMode)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var gameMode in gameModes)
            {
                while (TryCreateMatchUnsafe(gameMode, nowUtc, allowExpiredPartial: true, out var readyMatch))
                {
                    readyMatches.Add(readyMatch);
                }
            }
        }

        foreach (var readyMatch in readyMatches)
        {
            MatchFound?.Invoke(readyMatch.PlayerIds, readyMatch.RoomId);
        }
    }

    public bool CancelMatchmaking(int playerId)
    {
        lock (_lock)
        {
            return RemoveFromQueueUnsafe(playerId);
        }
    }

    public PartyActionResult CreateParty(int playerId, string username)
    {
        lock (_lock)
        {
            RememberPlayerUnsafe(playerId, username);
            if (_partyByPlayer.TryGetValue(playerId, out var currentPartyId))
            {
                return new(true, "Already in party", ToProtocolPartyUnsafe(_parties[currentPartyId]));
            }

            var party = new PartyState(Guid.NewGuid().ToString("N"), CreateCodeUnsafe());
            party.PlayerIds.Add(playerId);
            party.LeaderPlayerId = playerId;
            _parties.Add(party.PartyId, party);
            _partyByPlayer[playerId] = party.PartyId;
            return new(true, "Party created", ToProtocolPartyUnsafe(party));
        }
    }

    public PartyActionResult JoinParty(int playerId, string username, string? joinCode)
    {
        lock (_lock)
        {
            RememberPlayerUnsafe(playerId, username);
            if (_partyByPlayer.ContainsKey(playerId))
            {
                return new(false, "Leave the current party first", null);
            }
            var party = _parties.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.JoinCode, joinCode?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (party is null)
            {
                return new(false, "Party code not found", null);
            }
            if (party.PlayerIds.Any(memberId =>
                    _matchQueue.Any(ticket => ticket.PlayerIds.Contains(memberId))))
            {
                return new(false, "Party is currently matchmaking and cannot be joined", null);
            }
            if (party.PlayerIds.Any(memberId => _roomByPlayer.ContainsKey(memberId)))
            {
                return new(false, "Party is already in a room and cannot be joined", null);
            }
            if (party.PlayerIds.Count >= MaxRoomPlayers)
            {
                return new(false, "Party is full", null);
            }
            party.PlayerIds.Add(playerId);
            _partyByPlayer[playerId] = party.PartyId;
            return new(true, "Joined party", ToProtocolPartyUnsafe(party));
        }
    }

    public bool LeaveParty(int playerId)
    {
        lock (_lock)
        {
            return LeavePartyUnsafe(playerId);
        }
    }

    public LobbyEnterResult EnterCurrentRoom(int playerId, string username)
    {
        lock (_lock)
        {
            RememberPlayerUnsafe(playerId, username);
            if (!_roomByPlayer.TryGetValue(playerId, out var roomId))
            {
                var legacyRoom = _rooms.Values.FirstOrDefault(room => !room.IsPrivate && room.State.GetSnapshot().Players.Count < MaxRoomPlayers)
                    ?? CreateRoomUnsafe("Open Room", GameModes.GameThree, isPrivate: false);
                EnterRoomUnsafe(legacyRoom, playerId, username);
                roomId = legacyRoom.RoomId;
            }
            var snapshot = _rooms[roomId].State.GetSnapshot();
            return new(true, false, "Entered room", snapshot.Players);
        }
    }

    public LobbySnapshot GetRoomSnapshot(int playerId)
    {
        lock (_lock)
        {
            return _roomByPlayer.TryGetValue(playerId, out var roomId) && _rooms.TryGetValue(roomId, out var room)
                ? room.State.GetSnapshot()
                : new LobbySnapshot(new List<RoomPlayerInfo>(), GameModes.GameThree);
        }
    }

    public LobbyMutationResult SetReady(int playerId, bool ready) => WithRoom(playerId, room => room.State.SetReady(playerId, ready));

    public LobbyMutationResult ResetReadyStates(int playerId) => WithRoom(playerId, room => room.State.ResetReadyStates());

    public LobbyMutationResult SetGameMode(int playerId, string? mode) => WithRoom(playerId, room => room.State.SetGameMode(playerId, mode));
    public LobbyMutationResult SetGameThreeBall(int playerId, string? ballId) => WithRoom(playerId, room => room.State.SetGameThreeBall(playerId, ballId));

    public LobbyStartResult TryStart(int playerId)
    {
        lock (_lock)
        {
            return _roomByPlayer.TryGetValue(playerId, out var roomId) && _rooms.TryGetValue(roomId, out var room)
                ? room.State.TryStart(playerId)
                : new LobbyStartResult(false, "Player is not in a room", Array.Empty<int>(), GameModes.GameThree, new Dictionary<int, string>());
        }
    }

    public bool RemovePlayer(int playerId)
    {
        lock (_lock)
        {
            var changed = RemoveFromQueueUnsafe(playerId);
            changed |= LeavePartyUnsafe(playerId);
            changed |= LeaveRoomUnsafe(playerId);
            _players.Remove(playerId);
            return changed;
        }
    }

    public bool TryGetRoomId(int playerId, out string roomId)
    {
        lock (_lock)
        {
            return _roomByPlayer.TryGetValue(playerId, out roomId!);
        }
    }

    private LobbyMutationResult WithRoom(int playerId, Func<ManagedRoom, LobbyMutationResult> mutation)
    {
        lock (_lock)
        {
            return _roomByPlayer.TryGetValue(playerId, out var roomId) && _rooms.TryGetValue(roomId, out var room)
                ? mutation(room)
                : new LobbyMutationResult(false, "Player is not in a room", new List<RoomPlayerInfo>());
        }
    }

    private ManagedRoom CreateRoomUnsafe(
        string? roomName,
        string gameMode,
        bool isPrivate,
        string? joinCode = null,
        string? requestedRoomId = null)
    {
        var roomId = string.IsNullOrWhiteSpace(requestedRoomId)
            ? CreateIdUnsafe(_rooms.Keys)
            : requestedRoomId.Trim();
        if (_rooms.ContainsKey(roomId))
        {
            throw new InvalidOperationException("Room ID is already in use.");
        }
        var room = new ManagedRoom(
            roomId,
            string.IsNullOrWhiteSpace(roomName) ? "Player Room" : roomName.Trim(),
            joinCode ?? CreateCodeUnsafe(),
            gameMode,
            isPrivate,
            new LobbyService());
        _rooms.Add(roomId, room);
        return room;
    }

    private string? ResolveRequestedRoomIdUnsafe(string? requestedRoomId)
    {
        if (string.IsNullOrWhiteSpace(requestedRoomId))
        {
            string generated;
            do
            {
                generated = CreateCodeUnsafe();
            }
            while (_rooms.ContainsKey(generated));
            return generated;
        }

        var normalized = requestedRoomId.Trim();
        if (normalized.Length < 3
            || normalized.Length > 32
            || normalized.Any(character => !char.IsLetterOrDigit(character))
            || _rooms.ContainsKey(normalized)
            || _parties.Values.Any(party => string.Equals(party.JoinCode, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return normalized;
    }

    private string? ResolveRequestedJoinCodeUnsafe(string? requestedJoinCode)
    {
        if (string.IsNullOrWhiteSpace(requestedJoinCode))
        {
            return null;
        }

        var normalized = requestedJoinCode.Trim().ToUpperInvariant();
        if (normalized.Length < 3
            || normalized.Length > 32
            || normalized.Any(character => !char.IsLetterOrDigit(character))
            || _rooms.Values.Any(room => string.Equals(room.JoinCode, normalized, StringComparison.OrdinalIgnoreCase))
            || _parties.Values.Any(party => string.Equals(party.JoinCode, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return normalized;
    }

    private void EnterRoomUnsafe(ManagedRoom room, int playerId, string username)
    {
        room.State.Enter(playerId, username);
        if (room.State.GetSnapshot().Players.Count == 1 && room.GameMode != GameModes.GameThree)
        {
            room.State.SetGameMode(playerId, room.GameMode);
        }
        _roomByPlayer[playerId] = room.RoomId;
    }

    private bool LeaveRoomUnsafe(int playerId)
    {
        if (!_roomByPlayer.Remove(playerId, out var roomId) || !_rooms.TryGetValue(roomId, out var room))
        {
            return false;
        }
        room.State.Remove(playerId);
        if (room.State.GetSnapshot().Players.Count == 0)
        {
            _rooms.Remove(roomId);
        }
        return true;
    }

    private bool LeavePartyUnsafe(int playerId)
    {
        if (!_partyByPlayer.Remove(playerId, out var partyId) || !_parties.TryGetValue(partyId, out var party))
        {
            return false;
        }
        party.PlayerIds.Remove(playerId);
        if (party.PlayerIds.Count == 0)
        {
            _parties.Remove(partyId);
        }
        else if (party.LeaderPlayerId == playerId)
        {
            party.LeaderPlayerId = party.PlayerIds[0];
        }
        return true;
    }

    private bool RemoveFromQueueUnsafe(int playerId)
    {
        return _matchQueue.RemoveAll(ticket => ticket.PlayerIds.Contains(playerId)) > 0;
    }

    private int QueuePositionUnsafe(int playerId)
    {
        var index = _matchQueue.FindIndex(ticket => ticket.PlayerIds.Contains(playerId));
        return index < 0 ? 0 : index + 1;
    }

    private bool TryCreateMatchUnsafe(
        string gameMode,
        DateTime nowUtc,
        bool allowExpiredPartial,
        out MatchReady readyMatch)
    {
        var compatible = _matchQueue
            .Where(ticket => ticket.GameMode == gameMode)
            .OrderBy(ticket => ticket.CreatedAtUtc)
            .ToList();
        var selected = new List<MatchTicket>();
        var playerCount = 0;
        foreach (var ticket in compatible)
        {
            if (playerCount + ticket.PlayerIds.Length > MaxRoomPlayers)
            {
                continue;
            }

            selected.Add(ticket);
            playerCount += ticket.PlayerIds.Length;
            if (playerCount == MaxRoomPlayers)
            {
                break;
            }
        }

        var oldestExpired = compatible.Count > 0
            && nowUtc - compatible[0].CreatedAtUtc >= MatchmakingTimeout;
        if (playerCount == 0
            || (playerCount < MaxRoomPlayers && (!allowExpiredPartial || !oldestExpired)))
        {
            readyMatch = null!;
            return false;
        }

        var room = CreateRoomUnsafe("Quick Match", gameMode, isPrivate: true);
        var playerIds = selected.SelectMany(ticket => ticket.PlayerIds).ToArray();
        foreach (var matchedPlayerId in playerIds)
        {
            var identity = _players[matchedPlayerId];
            EnterRoomUnsafe(room, identity.PlayerId, identity.Username);
        }
        _matchQueue.RemoveAll(selected.Contains);
        readyMatch = new MatchReady(playerIds, room.RoomId);
        return true;
    }

    private IReadOnlyList<int> GetPartyPlayerIdsUnsafe(int playerId)
    {
        return _partyByPlayer.TryGetValue(playerId, out var partyId) && _parties.TryGetValue(partyId, out var party)
            ? party.PlayerIds
            : new[] { playerId };
    }

    private LobbyDirectorySnapshot CreateLobbySnapshotUnsafe(int playerId)
    {
        var rooms = _rooms.Values
            .Where(room => !room.IsPrivate)
            .Select(ToProtocolRoomUnsafe)
            .OrderBy(room => room.RoomName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        PartyInfo? party = null;
        if (_partyByPlayer.TryGetValue(playerId, out var partyId) && _parties.TryGetValue(partyId, out var state))
        {
            party = ToProtocolPartyUnsafe(state);
        }
        return new(rooms, party, QueuePositionUnsafe(playerId));
    }

    private LobbyRoomInfo ToProtocolRoomUnsafe(ManagedRoom room)
    {
        return new LobbyRoomInfo
        {
            RoomId = room.RoomId,
            RoomName = room.RoomName,
            GameMode = room.State.GetSnapshot().GameMode,
            PlayerCount = room.State.GetSnapshot().Players.Count,
            MaxPlayers = MaxRoomPlayers,
            IsPrivate = room.IsPrivate
        };
    }

    private PartyInfo ToProtocolPartyUnsafe(PartyState party)
    {
        var result = new PartyInfo { PartyId = party.PartyId, JoinCode = party.JoinCode };
        foreach (var playerId in party.PlayerIds)
        {
            var identity = _players[playerId];
            result.Members.Add(new PartyMemberInfo
            {
                PlayerId = playerId,
                Username = identity.Username,
                IsLeader = party.LeaderPlayerId == playerId
            });
        }
        return result;
    }

    private void RememberPlayerUnsafe(int playerId, string username)
    {
        if (playerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(playerId));
        }
        _players[playerId] = new PlayerIdentity(playerId, string.IsNullOrWhiteSpace(username) ? $"Player {playerId}" : username.Trim());
    }

    private string CreateCodeUnsafe()
    {
        string code;
        do
        {
            code = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        }
        while (_rooms.Values.Any(room => room.JoinCode == code) || _parties.Values.Any(party => party.JoinCode == code));
        return code;
    }

    private static string CreateIdUnsafe(IEnumerable<string> existingIds)
    {
        var ids = existingIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        }
        while (ids.Contains(id));
        return id;
    }

    private sealed record ManagedRoom(string RoomId, string RoomName, string JoinCode, string GameMode, bool IsPrivate, LobbyService State);
    private sealed record PlayerIdentity(int PlayerId, string Username);
    private sealed record MatchTicket(Guid TicketId, string GameMode, int[] PlayerIds, DateTime CreatedAtUtc);
    private sealed record MatchReady(int[] PlayerIds, string RoomId);
    private sealed class PartyState
    {
        public PartyState(string partyId, string joinCode) { PartyId = partyId; JoinCode = joinCode; }
        public string PartyId { get; }
        public string JoinCode { get; }
        public List<int> PlayerIds { get; } = new();
        public int LeaderPlayerId { get; set; }
    }
}

public sealed record LobbyDirectorySnapshot(List<LobbyRoomInfo> Rooms, PartyInfo? Party, int QueuePosition)
{
    public bool IsMatchmaking => QueuePosition > 0;
}

public sealed record LobbyDirectoryActionResult(bool Success, string Message, string RoomId, string JoinCode);
public sealed record MatchmakingResult(bool Success, string Message, bool MatchFound, string RoomId, int QueuePosition);
public sealed record PartyActionResult(bool Success, string Message, PartyInfo? Party);
