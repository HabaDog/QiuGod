using GameNetworkCommon;

public static class GameModes
{
    public const string GameThree = "game_three";

    public static string Normalize(string? gameMode)
    {
        return string.IsNullOrWhiteSpace(gameMode)
            ? GameThree
            : gameMode.Trim().ToLowerInvariant();
    }

    public static bool TryNormalizeSupported(string? gameMode, out string normalizedGameMode)
    {
        normalizedGameMode = Normalize(gameMode);
        return normalizedGameMode == GameThree;
    }
}

public sealed record GameSessionStartOptions(IReadOnlyDictionary<int, string> BallSelections)
{
    public static GameSessionStartOptions Empty { get; } = new(
        new Dictionary<int, string>());
}

public interface IGameSessionFactory
{
    string GameMode { get; }
    string SceneName { get; }
    int MaxPlayers { get; }
    IGameSession Create();
    IGameSession Create(GameSessionStartOptions options) => Create();
}

public interface IGameSession
{
    string GameMode { get; }
    string SceneName { get; }
    event Action<byte[]>? Broadcast;
    event Action? Ended;
    void Start(IReadOnlyList<int> playerIds);
    bool HandleMessage(int authenticatedPlayerId, BaseMessage message);
    BaseMessage CreateRecoverySnapshot(int playerId);
    void Stop();
}

public sealed class GameThreeSessionFactory : IGameSessionFactory
{
    public string GameMode => GameModes.GameThree;
    public string SceneName => "GamePlayThree";
    public int MaxPlayers => GameThreeSession.MaxMatchPlayers;

    public IGameSession Create()
    {
        return new GameThreeSession(
            aiDecisionProvider: GameThreeLlmAiBootstrap.Provider,
            tacticalAi: GameThreeUtilityAiBootstrap.Provider);
    }

    public IGameSession Create(GameSessionStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GameThreeSession(
            selectedBallIds: options.BallSelections,
            aiDecisionProvider: GameThreeLlmAiBootstrap.Provider,
            tacticalAi: GameThreeUtilityAiBootstrap.Provider);
    }
}
