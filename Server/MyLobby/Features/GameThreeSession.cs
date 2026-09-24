using GameNetworkCommon;

/// <summary>
/// Minimal GameThreeSession helpers required by LobbyService.
/// The full GameThreeSession lives in MyServer; this copy provides only the
/// static members that the lobby layer depends on.
/// </summary>
public static class GameThreeSession
{
    public const string DefaultBallId = "sprinter";

    private static readonly IReadOnlyDictionary<string, GameThreeBallCatalogItem> BallDefinitions =
        GameThreeBallCatalogData.All.ToDictionary(
            item => item.BallId,
            item => item,
            StringComparer.OrdinalIgnoreCase);

    public static bool TryNormalizeBallId(string? ballId, out string normalizedBallId)
    {
        normalizedBallId = DefaultBallId;
        if (string.IsNullOrWhiteSpace(ballId))
        {
            return false;
        }

        var candidate = ballId.Trim().ToLowerInvariant();
        if (!BallDefinitions.ContainsKey(candidate))
        {
            return false;
        }

        normalizedBallId = candidate;
        return true;
    }
}