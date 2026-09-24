using System;
using System.Collections.Generic;
using System.Linq;
using GameNetworkCommon;

/// <summary>
/// Owns active game-session lifecycles and routes player commands to the
/// correct local execution instance. The gateway does not own game state.
/// </summary>
public sealed class GameSessionService : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, ActiveGameSession> _sessions = new();
    private readonly Dictionary<int, Guid> _sessionByPlayerId = new();
    private readonly Dictionary<string, IGameSessionFactory> _factories;

    public event Action<IReadOnlyList<int>, byte[]>? BroadcastToPlayers;
    public event Action<IReadOnlyList<int>, string>? SessionCompleted;

    public GameSessionService(IEnumerable<IGameSessionFactory>? factories = null)
    {
        _factories = (factories ?? new IGameSessionFactory[]
        {
            new GameThreeSessionFactory()
        })
            .ToDictionary(factory => GameModes.Normalize(factory.GameMode), StringComparer.Ordinal);
        if (_factories.Count == 0)
        {
            throw new ArgumentException("At least one game-session factory must be registered.", nameof(factories));
        }
    }

    public GameSessionStartResult StartSession(
        IReadOnlyCollection<int> playerIds,
        string? gameMode = null,
        IReadOnlyDictionary<int, string>? ballSelections = null)
    {
        var normalizedGameMode = GameModes.Normalize(gameMode);
        if (!_factories.TryGetValue(normalizedGameMode, out var factory))
        {
            return new GameSessionStartResult(
                false,
                $"Unknown game mode: {normalizedGameMode}",
                null,
                normalizedGameMode,
                string.Empty);
        }

        var participants = playerIds
            .Where(playerId => playerId > 0)
            .Distinct()
            .ToArray();

        if (participants.Length == 0)
        {
            return new GameSessionStartResult(
                false,
                "A game session requires at least one player",
                null,
                normalizedGameMode,
                factory.SceneName);
        }

        if (participants.Length > factory.MaxPlayers || participants.Length != playerIds.Count)
        {
            return new GameSessionStartResult(
                false,
                $"Game mode {normalizedGameMode} requires one to {factory.MaxPlayers} unique players",
                null,
                normalizedGameMode,
                factory.SceneName);
        }

        var sessionId = Guid.NewGuid();
        var startOptions = ballSelections is null
            ? GameSessionStartOptions.Empty
            : new GameSessionStartOptions(ballSelections);
        var game = factory.Create(startOptions);
        var session = new ActiveGameSession(sessionId, participants, game);

        game.Broadcast += packet => Broadcast(sessionId, packet);
        game.Ended += () => CompleteSession(sessionId);

        lock (_lock)
        {
            var playerAlreadyRunning = participants.FirstOrDefault(_sessionByPlayerId.ContainsKey);
            if (playerAlreadyRunning != 0)
            {
                return new GameSessionStartResult(
                    false,
                    $"Player {playerAlreadyRunning} is already in an active game session",
                    null,
                    normalizedGameMode,
                    factory.SceneName);
            }

            _sessions.Add(sessionId, session);
            foreach (var playerId in participants)
            {
                _sessionByPlayerId.Add(playerId, sessionId);
            }
        }

        try
        {
            game.Start(participants);
            Console.WriteLine(
                $"[GameSession] Started {sessionId}, mode={normalizedGameMode}, " +
                $"players=[{string.Join(", ", participants)}]");
            return new GameSessionStartResult(
                true,
                "Game session started",
                sessionId,
                normalizedGameMode,
                factory.SceneName);
        }
        catch (Exception exception)
        {
            RemoveSession(sessionId, stopGame: true);
            Console.WriteLine($"[GameSession] Failed to start {sessionId}: {exception.Message}");
            return new GameSessionStartResult(
                false,
                "Failed to initialize the game session",
                null,
                normalizedGameMode,
                factory.SceneName);
        }
    }

    public bool HandleMessage(int playerId, BaseMessage message)
    {
        var session = FindByPlayerId(playerId);
        return session is not null && session.Game.HandleMessage(playerId, message);
    }

    public bool TryGetResumeState(int playerId, out GameSessionResumeState? resumeState)
    {
        lock (_lock)
        {
            if (!_sessionByPlayerId.TryGetValue(playerId, out var sessionId)
                || !_sessions.TryGetValue(sessionId, out var session))
            {
                resumeState = null;
                return false;
            }

            var slotIndex = Array.IndexOf(session.PlayerIds, playerId);
            resumeState = new GameSessionResumeState(
                session.SessionId,
                slotIndex,
                session.PlayerIds.Length,
                session.Game.GameMode,
                session.Game.SceneName,
                session.Game.CreateRecoverySnapshot(playerId));
            return true;
        }
    }

    public bool StopSessionForPlayer(int playerId)
    {
        Guid sessionId;
        lock (_lock)
        {
            if (!_sessionByPlayerId.TryGetValue(playerId, out sessionId))
            {
                return false;
            }
        }

        return RemoveSession(sessionId, stopGame: true);
    }

    public void StopAll()
    {
        ActiveGameSession[] sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            _sessionByPlayerId.Clear();
        }

        foreach (var session in sessions)
        {
            session.Game.Stop();
        }
    }

    public void Dispose()
    {
        StopAll();
    }

    private ActiveGameSession? FindByPlayerId(int playerId)
    {
        lock (_lock)
        {
            return _sessionByPlayerId.TryGetValue(playerId, out var sessionId)
                && _sessions.TryGetValue(sessionId, out var session)
                    ? session
                    : null;
        }
    }

    private void Broadcast(Guid sessionId, byte[] packet)
    {
        int[] playerIds;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                return;
            }

            playerIds = session.PlayerIds;
        }

        BroadcastToPlayers?.Invoke(playerIds, packet);
    }

    private void CompleteSession(Guid sessionId)
    {
        ActiveGameSession? completed;
        lock (_lock)
        {
            _sessions.TryGetValue(sessionId, out completed);
        }

        if (completed is not null && RemoveSession(sessionId, stopGame: false))
        {
            Console.WriteLine($"[GameSession] Completed {sessionId}");
            SessionCompleted?.Invoke(completed.PlayerIds, completed.Game.GameMode);
        }
    }

    private bool RemoveSession(Guid sessionId, bool stopGame)
    {
        ActiveGameSession? session;
        lock (_lock)
        {
            if (!_sessions.Remove(sessionId, out session))
            {
                return false;
            }

            foreach (var playerId in session.PlayerIds)
            {
                if (_sessionByPlayerId.TryGetValue(playerId, out var mappedSessionId)
                    && mappedSessionId == sessionId)
                {
                    _sessionByPlayerId.Remove(playerId);
                }
            }
        }

        if (stopGame)
        {
            session.Game.Stop();
            Console.WriteLine($"[GameSession] Stopped {sessionId}");
        }

        return true;
    }

    private sealed record ActiveGameSession(Guid SessionId, int[] PlayerIds, IGameSession Game);
}

public sealed record GameSessionStartResult(
    bool Success,
    string Message,
    Guid? SessionId,
    string GameMode,
    string SceneName);
public sealed record GameSessionResumeState(
    Guid SessionId,
    int SlotIndex,
    int PlayerCount,
    string GameMode,
    string SceneName,
    GameNetworkCommon.BaseMessage SnapshotPacket);
