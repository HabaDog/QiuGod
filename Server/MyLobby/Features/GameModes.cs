/// <summary>
/// Minimal GameModes type required by LobbyDirectoryService and LobbyService.
/// The full definition lives in MyServer; this copy keeps MyLobby self-contained.
/// </summary>
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