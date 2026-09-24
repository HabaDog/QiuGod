using System;
using UnityEngine;

/// <summary>
/// Process-local game flow state shared by the startup, lobby, room and match
/// scenes. It deliberately contains no account credentials: LAN identities are
/// generated locally and the multiplayer host owns the room state.
/// </summary>
public static class GameFlowContext
{
    public enum EntryMode
    {
        None,
        SinglePlayer,
        MultiplayerHost,
        MultiplayerClient
    }

    public static EntryMode Mode { get; private set; }
    public static string GameMode { get; private set; } = "game_three";
    public static string RoomId { get; private set; } = string.Empty;
    public static string RoomPassword { get; private set; } = string.Empty;
    public static bool AllowAiFill { get; private set; } = true;
    public static bool VoiceEnabled { get; private set; }
    public static string SelectedBallId { get; private set; } = "sprinter";
    public static string LocalPlayerName { get; private set; } = string.Empty;
    public static string LocalPlayerId { get; private set; } = string.Empty;

    public static bool IsOffline => Mode == EntryMode.SinglePlayer;
    public static bool IsMultiplayer => Mode == EntryMode.MultiplayerHost || Mode == EntryMode.MultiplayerClient;

    public static void BeginSinglePlayer()
    {
        Mode = EntryMode.SinglePlayer;
        RoomId = string.Empty;
        RoomPassword = string.Empty;
        AllowAiFill = true;
        VoiceEnabled = false;
        EnsureLocalIdentity();
        EnsurePlayerDataManager();
    }

    public static void BeginHost(string roomId, string password, bool allowAiFill)
    {
        Mode = EntryMode.MultiplayerHost;
        RoomId = NormalizeRoomValue(roomId);
        RoomPassword = NormalizeRoomValue(password);
        AllowAiFill = allowAiFill;
        EnsureLocalIdentity();
        EnsurePlayerDataManager();
    }

    public static void BeginClient(string roomId, string password)
    {
        Mode = EntryMode.MultiplayerClient;
        RoomId = NormalizeRoomValue(roomId);
        RoomPassword = NormalizeRoomValue(password);
        EnsureLocalIdentity();
        EnsurePlayerDataManager();
    }

    public static void SelectGameMode(string gameMode)
    {
        GameMode = string.IsNullOrWhiteSpace(gameMode) ? "game_three" : gameMode.Trim().ToLowerInvariant();
    }

    public static void SetVoiceEnabled(bool enabled)
    {
        VoiceEnabled = enabled;
    }

    public static void SelectBall(string ballId)
    {
        SelectedBallId = string.IsNullOrWhiteSpace(ballId) ? "sprinter" : ballId.Trim().ToLowerInvariant();
    }

    public static void SetLocalPlayerName(string name)
    {
        LocalPlayerName = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        EnsureLocalIdentity();
    }

    public static void Reset()
    {
        Mode = EntryMode.None;
        RoomId = string.Empty;
        RoomPassword = string.Empty;
        GameMode = "game_three";
        AllowAiFill = true;
        VoiceEnabled = false;
        SelectedBallId = "sprinter";
    }

    private static void EnsureLocalIdentity()
    {
        if (string.IsNullOrWhiteSpace(LocalPlayerName))
        {
            LocalPlayerName = PlayerPrefs.GetString("MyDemo.LocalPlayerName", string.Empty);
        }

        if (string.IsNullOrWhiteSpace(LocalPlayerName))
        {
            LocalPlayerName = "Player";
        }

        LocalPlayerId = PlayerPrefs.GetString("MyDemo.LocalPlayerId", string.Empty);
        if (string.IsNullOrWhiteSpace(LocalPlayerId))
        {
            LocalPlayerId = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString("MyDemo.LocalPlayerId", LocalPlayerId);
            PlayerPrefs.Save();
        }
    }

    public static void EnsureRuntimePlayerData()
    {
        var manager = UnityEngine.Object.FindObjectOfType<PlayerDataManager>();
        if (manager == null)
        {
            var root = new GameObject("PlayerDataManager");
            manager = root.AddComponent<PlayerDataManager>();
        }

        unchecked
        {
            var stableId = 17;
            foreach (var character in LocalPlayerId)
            {
                stableId = (stableId * 31) + character;
                stableId &= 0x7fffffff;
            }
            if (stableId == 0) stableId = 1;
            manager.SetPlayerInfo(stableId, LocalPlayerName);
        }
    }

    private static void EnsurePlayerDataManager() => EnsureRuntimePlayerData();

    private static string NormalizeRoomValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
