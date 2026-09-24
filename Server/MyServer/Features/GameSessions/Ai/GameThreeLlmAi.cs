using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Optional, isolated LLM planning boundary for GamePlayThree AI. It has no
/// authority over simulation and returns only short-lived high-level intent.
/// </summary>
public interface IGameThreeAiDecisionProvider
{
    bool IsEnabled { get; }
    TimeSpan DecisionInterval { get; }
    TimeSpan MaximumDecisionAge { get; }
    Task<GameThreeAiDecision?> DecideAsync(GameThreeAiObservation observation, CancellationToken cancellationToken);
}

public sealed record GameThreeAiObservation(
    int SlotIndex,
    string BallId,
    float PositionX,
    float PositionZ,
    int CurrentHealth,
    int MaxHealth,
    int Energy,
    int EnergyRequired,
    int TerritoryCount,
    float RoundTimeRemaining,
    GameThreeAiAbilityState Abilities,
    IReadOnlyList<string> LocalMapRows,
    IReadOnlyList<GameThreeAiVisibleBall> VisibleOpponents,
    IReadOnlyList<GameThreeAiBomb> NearbyBombs,
    IReadOnlyList<GameThreeAiPickup> NearbyPickups,
    IReadOnlyList<GameThreeAiMarker> NearbyMarkers,
    IReadOnlyList<GameThreeAiClone> NearbyClones);

public sealed record GameThreeAiAbilityState(
    string SkillId,
    bool IsSkillReady,
    string ActiveSkillId,
    float ActiveSkillRemaining,
    float BombCooldownRemaining,
    float DashCooldownRemaining,
    bool IsFloating);

public sealed record GameThreeAiVisibleBall(
    int SlotIndex,
    string BallId,
    float PositionX,
    float PositionZ,
    int CurrentHealth,
    string ActiveSkillId,
    bool IsFloating);

public sealed record GameThreeAiBomb(int OwnerSlot, int RelativeX, int RelativeZ, float TimeRemaining);
public sealed record GameThreeAiPickup(string Type, int RelativeX, int RelativeZ);
public sealed record GameThreeAiMarker(string MarkerId, int OwnerSlot, int RelativeX, int RelativeZ, float TimeRemaining);
public sealed record GameThreeAiClone(int OwnerSlot, int RelativeX, int RelativeZ);

public sealed record GameThreeAiDecision(float TargetX, float TargetZ, bool ThrowBomb, bool UseSkill, DateTime CreatedAtUtc);

public sealed class GameThreeLlmAiSettings
{
    public bool Enabled { get; init; }
    public string ApiKey { get; init; } = string.Empty;
    public string Endpoint { get; init; } = "https://api.openai.com/v1/chat/completions";
    public string Model { get; init; } = "gpt-5.2";
    public string ApiKeyEnvironmentVariable { get; init; } = "OPENAI_API_KEY";
    public float DecisionIntervalSeconds { get; init; } = 2f;
    public float RequestTimeoutSeconds { get; init; } = 1.2f;
    public float MaximumDecisionAgeSeconds { get; init; } = 2.5f;
    public bool DiagnosticsEnabled { get; init; }
}

public static class GameThreeLlmAiBootstrap
{
    private static IGameThreeAiDecisionProvider _provider = DisabledGameThreeAiDecisionProvider.Instance;

    public static IGameThreeAiDecisionProvider Provider => _provider;

    public static void Configure(IConfiguration configuration)
    {
        var settings = configuration.GetSection("GameThreeLlmAi").Get<GameThreeLlmAiSettings>() ?? new GameThreeLlmAiSettings();
        var apiKey = string.IsNullOrWhiteSpace(settings.ApiKey)
            ? Environment.GetEnvironmentVariable(settings.ApiKeyEnvironmentVariable)
            : settings.ApiKey;
        _provider = settings.Enabled && !string.IsNullOrWhiteSpace(apiKey)
            ? new OpenAiCompatibleGameThreeAiDecisionProvider(settings, apiKey)
            : DisabledGameThreeAiDecisionProvider.Instance;

        Console.WriteLine(_provider.IsEnabled
            ? $"[GameThreeLlmAi] Enabled with model {settings.Model}; diagnostics={settings.DiagnosticsEnabled}."
            : "[GameThreeLlmAi] Disabled; Utility AI remains active.");
    }
}

internal sealed class DisabledGameThreeAiDecisionProvider : IGameThreeAiDecisionProvider
{
    public static DisabledGameThreeAiDecisionProvider Instance { get; } = new();
    public bool IsEnabled => false;
    public TimeSpan DecisionInterval => TimeSpan.MaxValue;
    public TimeSpan MaximumDecisionAge => TimeSpan.Zero;

    public Task<GameThreeAiDecision?> DecideAsync(GameThreeAiObservation observation, CancellationToken cancellationToken)
        => Task.FromResult<GameThreeAiDecision?>(null);
}

/// <summary>
/// Uses the OpenAI-compatible chat-completions JSON shape. The endpoint and
/// model are configuration only so this adapter can be removed or replaced
/// without changing match simulation.
/// </summary>
internal sealed class OpenAiCompatibleGameThreeAiDecisionProvider : IGameThreeAiDecisionProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly GameThreeLlmAiSettings _settings;

    public OpenAiCompatibleGameThreeAiDecisionProvider(GameThreeLlmAiSettings settings, string apiKey)
    {
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 0.2f, 10f)) };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public bool IsEnabled => true;
    public TimeSpan DecisionInterval => TimeSpan.FromSeconds(Math.Clamp(_settings.DecisionIntervalSeconds, 0.5f, 10f));
    public TimeSpan MaximumDecisionAge => TimeSpan.FromSeconds(Math.Clamp(_settings.MaximumDecisionAgeSeconds, 0.5f, 10f));

    public async Task<GameThreeAiDecision?> DecideAsync(GameThreeAiObservation observation, CancellationToken cancellationToken)
    {
        try
        {
            Log($"Request slot={observation.SlotIndex} ball={observation.BallId} energy={observation.Energy}/{observation.EnergyRequired} opponents={observation.VisibleOpponents.Count}.");
            var request = new
            {
                model = _settings.Model,
                temperature = 0.2f,
                max_tokens = 96,
                messages = new[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = JsonSerializer.Serialize(observation, JsonOptions) }
                }
            };
            using var response = await _httpClient.PostAsync(
                _settings.Endpoint,
                new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json"),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorSummary = CreateSafeErrorSummary(await response.Content.ReadAsStringAsync(cancellationToken));
                Log($"Response slot={observation.SlotIndex} status={(int)response.StatusCode} ({response.ReasonPhrase}): {errorSummary}; falling back to rule AI.");
                return null;
            }

            using var responseDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var content = responseDocument.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            if (string.IsNullOrWhiteSpace(content))
            {
                Log($"Response slot={observation.SlotIndex} had no message content; falling back to rule AI.");
                return null;
            }

            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
            {
                Log($"Response slot={observation.SlotIndex} was not JSON; falling back to rule AI.");
                return null;
            }

            var payload = JsonSerializer.Deserialize<DecisionPayload>(content.Substring(jsonStart, jsonEnd - jsonStart + 1), JsonOptions);
            if (payload is null || !float.IsFinite(payload.TargetX) || !float.IsFinite(payload.TargetZ))
            {
                Log($"Response slot={observation.SlotIndex} had an invalid decision; falling back to rule AI.");
                return null;
            }

            Log($"Decision slot={observation.SlotIndex} target=({payload.TargetX:F1},{payload.TargetZ:F1}) bomb={payload.ThrowBomb} skill={payload.UseSkill}.");
            return new GameThreeAiDecision(payload.TargetX, payload.TargetZ, payload.ThrowBomb, payload.UseSkill, DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            Log(cancellationToken.IsCancellationRequested
                ? $"Request slot={observation.SlotIndex} cancelled because the match ended."
                : $"Request slot={observation.SlotIndex} timed out; falling back to rule AI.");
            return null;
        }
        catch (HttpRequestException exception)
        {
            Log($"Request slot={observation.SlotIndex} failed with {exception.GetType().Name}; falling back to rule AI.");
            return null;
        }
        catch (JsonException)
        {
            Log($"Response slot={observation.SlotIndex} could not be parsed; falling back to rule AI.");
            return null;
        }
        catch (Exception exception)
        {
            Log($"Request slot={observation.SlotIndex} failed with {exception.GetType().Name}; falling back to rule AI.");
            return null;
        }
    }

    private void Log(string message)
    {
        if (_settings.DiagnosticsEnabled)
        {
            Console.WriteLine($"[GameThreeLlmAi] {message}");
        }
    }

    private static string CreateSafeErrorSummary(string body)
    {
        var summary = string.IsNullOrWhiteSpace(body)
            ? "no error body"
            : string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        summary = System.Text.RegularExpressions.Regex.Replace(summary, "sk-[A-Za-z0-9_-]+", "sk-***");
        summary = System.Text.RegularExpressions.Regex.Replace(
            summary,
            "(?i)(api[_-]?key|authorization)\\s*[:=]\\s*[^,}\\s]+",
            "$1=[redacted]");
        return summary.Length <= 480 ? summary : summary.Substring(0, 480) + "...";
    }

    private sealed class DecisionPayload
    {
        public float TargetX { get; init; }
        public float TargetZ { get; init; }
        public bool ThrowBomb { get; init; }
        public bool UseSkill { get; init; }
    }

    private const string SystemPrompt = """
You control one arena ball. You receive only server-authoritative visible state. Never assume an unlisted opponent, obstacle, bomb, pickup, or tile exists.
Victory: the match ends when one ball survives, or when time reaches zero. At time expiry, the alive ball with the highest TerritoryCount wins; if every ball is eliminated, compare all balls instead. A tied score is resolved by lower SlotIndex.
LocalMapRows is a 9x9 map centered on you. Row 0 is relative Z=-4 and column 0 is relative X=-4. X means outside the arena, I means indestructible obstacle, D means destructible obstacle, . means unclaimed open tile, S means your territory, and 0-3 means territory owned by that slot.
NearbyBombs, NearbyPickups, NearbyMarkers, and NearbyClones use relative grid coordinates. VisibleOpponents excludes invisible opponents; do not target an unseen opponent.
Skills by SkillId: ram grants temporary damage immunity, speed, obstacle breaking, and collision resistance; skyfall rises briefly, then lands with area damage and knockback; time_stop freezes other balls; bomb_frenzy grants bomb immunity and throws four larger instant bombs; trap_trail places a mine on the current tile, and an enemy stepping on it takes damage and is knocked away; phase_shift passes through obstacles and balls but cannot attack, throw bombs, or gain energy while active, then relocates to the nearest open tile when it ends; recall places one marker, and using it again returns there while energy cannot recharge during the marker; clone creates a fragile moving clone that claims tiles and damages on contact; invisibility hides you and newly claimed tiles from opponents.
UseSkill is legal only when Abilities.IsSkillReady is true. The server validates every action, so prefer a nearby safe target and avoid bombs about to explode. Return only JSON with exactly: {targetX:number,targetZ:number,throwBomb:boolean,useSkill:boolean}. Never add text.
""";
}
