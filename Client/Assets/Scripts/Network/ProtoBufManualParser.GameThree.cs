using System;
using GameNetworkCommon;

public static partial class ProtoBufManualParser
{
    public static bool TryParseGameThree<T>(byte[] data, out T result)
    {
        object parsed;
        var type = typeof(T);

        if (type == typeof(GameThreeInput)) parsed = ParseGameThreeInputMessage(data);
        else if (type == typeof(GameThreeThrowBombRequest)) parsed = ParseGameThreeThrowBombRequestMessage(data);
        else if (type == typeof(GameThreeUseSkillRequest)) parsed = ParseGameThreeUseSkillRequestMessage(data);
        else if (type == typeof(GameThreeDashRequest)) parsed = ParseGameThreeDashRequestMessage(data);
        else if (type == typeof(GameThreeStateSync)) parsed = ParseGameThreeStateSyncMessage(data);
        else if (type == typeof(GameThreeRoundStart)) parsed = ParseGameThreeRoundStartMessage(data);
        else if (type == typeof(GameThreeBombThrown)) parsed = ParseGameThreeBombThrownMessage(data);
        else if (type == typeof(GameThreeBombExploded)) parsed = ParseGameThreeBombExplodedMessage(data);
        else if (type == typeof(GameThreePlayerDamaged)) parsed = ParseGameThreePlayerDamagedMessage(data);
        else if (type == typeof(GameThreePlayerDeath)) parsed = ParseGameThreePlayerDeathMessage(data);
        else if (type == typeof(GameThreeTileClaimed)) parsed = ParseGameThreeTileClaimedMessage(data);
        else if (type == typeof(GameThreePickupSpawned)) parsed = ParseGameThreePickupSpawnedMessage(data);
        else if (type == typeof(GameThreePickupCollected)) parsed = ParseGameThreePickupCollectedMessage(data);
        else if (type == typeof(GameThreeGameOver)) parsed = ParseGameThreeGameOverMessage(data);
        else if (type == typeof(GameThreeLeaveGame)) parsed = ParseGameThreeLeaveGameMessage(data);
        else if (type == typeof(GameThreeLeaveGameResponse)) parsed = ParseGameThreeLeaveGameResponseMessage(data);
        else if (type == typeof(GameThreeSkillUsed)) parsed = ParseGameThreeSkillUsedMessage(data);
        else
        {
            result = default(T);
            return false;
        }

        result = (T)parsed;
        return true;
    }

    private static GameThreeInput ParseGameThreeInputMessage(byte[] data)
    {
        var result = new GameThreeInput();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.PlayerId = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 5) result.MoveX = ReadFloat(data, ref pos);
            else if (field == 3 && wire == 5) result.MoveZ = ReadFloat(data, ref pos);
            else if (field == 4 && wire == 0) result.Jump = ReadVarInt(data, ref pos) != 0;
            else if (field == 5 && wire == 0) result.Sequence = (int)ReadVarInt(data, ref pos);
            else if (field == 6 && wire == 5) result.DeltaTime = ReadFloat(data, ref pos);
            else if (field == 7 && wire == 0) result.Fire = ReadVarInt(data, ref pos) != 0;
            else if (field == 8 && wire == 0) result.Boost = ReadVarInt(data, ref pos) != 0;
            else if (field == 9 && wire == 5) result.AimX = ReadFloat(data, ref pos);
            else if (field == 10 && wire == 5) result.AimZ = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeThrowBombRequest ParseGameThreeThrowBombRequestMessage(byte[] data)
    {
        var values = ParseDirectionalRequest(data);
        return new GameThreeThrowBombRequest
        {
            PlayerId = values.PlayerId,
            DirectionX = values.DirectionX,
            DirectionZ = values.DirectionZ,
            Sequence = values.Sequence
        };
    }

    private static GameThreeUseSkillRequest ParseGameThreeUseSkillRequestMessage(byte[] data)
    {
        var values = ParseDirectionalRequest(data);
        return new GameThreeUseSkillRequest
        {
            PlayerId = values.PlayerId,
            DirectionX = values.DirectionX,
            DirectionZ = values.DirectionZ,
            Sequence = values.Sequence
        };
    }

    private static GameThreeDashRequest ParseGameThreeDashRequestMessage(byte[] data)
    {
        var values = ParseDirectionalRequest(data);
        return new GameThreeDashRequest
        {
            PlayerId = values.PlayerId,
            DirectionX = values.DirectionX,
            DirectionZ = values.DirectionZ,
            Sequence = values.Sequence
        };
    }

    private static DirectionalRequestValues ParseDirectionalRequest(byte[] data)
    {
        var result = new DirectionalRequestValues();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.PlayerId = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 5) result.DirectionX = ReadFloat(data, ref pos);
            else if (field == 3 && wire == 5) result.DirectionZ = ReadFloat(data, ref pos);
            else if (field == 4 && wire == 0) result.Sequence = (int)ReadVarInt(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeStateSync ParseGameThreeStateSyncMessage(byte[] data)
    {
        var result = new GameThreeStateSync();
        ParseGameThreeStateSync(data, result);
        result.TerritoryRevisionGrid = ParseRepeatedInt32(data, 12);
        result.Clones = ParseRepeatedMessage<GameThreeCloneState>(data, 13, ParseGameThreeCloneState);
        result.HiddenTerritory = ParseRepeatedMessage<GameThreeHiddenTerritoryState>(data, 14, ParseGameThreeHiddenTerritoryState);
        result.Markers = ParseRepeatedMessage<GameThreeTileMarkerState>(data, 15, ParseGameThreeTileMarkerState);
        result.Projectiles = ParseRepeatedMessage<GameThreeProjectileState>(data, 18, ParseGameThreeProjectileState);
        result.HealingZones = ParseRepeatedMessage<GameThreeHealingZoneState>(data, 20, ParseGameThreeHealingZoneState);

        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 16 && wire == 0) result.ServerTimestampMs = (long)ReadVarInt(data, ref pos);
            else if (field == 17 && wire == 0) result.SnapshotSequence = (int)ReadVarInt(data, ref pos);
            else if (field == 19 && wire == 2) result.Weather = ReadMessage(data, ref pos, ParseGameThreeWeatherState);
            else if (field == 21 && wire == 2) result.ActiveEvent = ReadMessage(data, ref pos, ParseGameThreeEventState);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeRoundStart ParseGameThreeRoundStartMessage(byte[] data)
    {
        var result = new GameThreeRoundStart();
        ParseGameThreeRoundStart(data, result);
        return result;
    }

    private static GameThreeBombThrown ParseGameThreeBombThrownMessage(byte[] data)
    {
        var result = new GameThreeBombThrown();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.BombId = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 0) result.OwnerSlot = (int)ReadVarInt(data, ref pos);
            else if (field == 3 && wire == 5) result.OriginX = ReadFloat(data, ref pos);
            else if (field == 4 && wire == 5) result.OriginZ = ReadFloat(data, ref pos);
            else if (field == 5 && wire == 5) result.TargetX = ReadFloat(data, ref pos);
            else if (field == 6 && wire == 5) result.TargetZ = ReadFloat(data, ref pos);
            else if (field == 7 && wire == 5) result.FuseSeconds = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeBombExploded ParseGameThreeBombExplodedMessage(byte[] data)
    {
        var result = new GameThreeBombExploded();
        ParseGameThreeBombExploded(data, result);
        return result;
    }

    private static GameThreePlayerDamaged ParseGameThreePlayerDamagedMessage(byte[] data)
    {
        var result = new GameThreePlayerDamaged();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.VictimSlot = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 0) result.SourceSlot = (int)ReadVarInt(data, ref pos);
            else if (field == 3 && wire == 0) result.Damage = (int)ReadVarInt(data, ref pos);
            else if (field == 4 && wire == 0) result.RemainingHealth = (int)ReadVarInt(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreePlayerDeath ParseGameThreePlayerDeathMessage(byte[] data)
    {
        var result = new GameThreePlayerDeath();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.PlayerSlot = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 0) result.KillerSlot = (int)ReadVarInt(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeTileClaimed ParseGameThreeTileClaimedMessage(byte[] data)
    {
        var result = new GameThreeTileClaimed();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.GridX = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 0) result.GridZ = (int)ReadVarInt(data, ref pos);
            else if (field == 3 && wire == 0) result.OwnerSlot = (int)ReadVarInt(data, ref pos);
            else if (field == 4 && wire == 0) result.Revision = (int)ReadVarInt(data, ref pos);
            else if (field == 5 && wire == 0) result.IsHidden = ReadVarInt(data, ref pos) != 0;
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreePickupSpawned ParseGameThreePickupSpawnedMessage(byte[] data)
    {
        var result = new GameThreePickupSpawned();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 2)
            {
                var length = (int)ReadVarInt(data, ref pos);
                result.Pickup = ParseGameThreePickupState(data, pos, length);
                pos += length;
            }
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreePickupCollected ParseGameThreePickupCollectedMessage(byte[] data)
    {
        var result = new GameThreePickupCollected();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.PickupId = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 0) result.CollectorSlot = (int)ReadVarInt(data, ref pos);
            else if (field == 3 && wire == 0) result.PickupType = (int)ReadVarInt(data, ref pos);
            else if (field == 4 && wire == 0) result.CurrentHealth = (int)ReadVarInt(data, ref pos);
            else if (field == 5 && wire == 5) result.SkillCooldownRemaining = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeGameOver ParseGameThreeGameOverMessage(byte[] data)
    {
        var result = new GameThreeGameOver();
        ParseGameThreeGameOver(data, result);
        result.Results = ParseRepeatedMessage<GameThreeMatchPlayerResult>(data, 4, ParseGameThreeMatchPlayerResult);
        return result;
    }

    private static GameThreeLeaveGame ParseGameThreeLeaveGameMessage(byte[] data)
    {
        var result = new GameThreeLeaveGame();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.PlayerId = (int)ReadVarInt(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeLeaveGameResponse ParseGameThreeLeaveGameResponseMessage(byte[] data)
    {
        var result = new GameThreeLeaveGameResponse();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.Success = ReadVarInt(data, ref pos) != 0;
            else if (field == 2 && wire == 2) result.Message = ReadString(data, ref pos, data.Length);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeSkillUsed ParseGameThreeSkillUsedMessage(byte[] data)
    {
        var result = new GameThreeSkillUsed();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.PlayerSlot = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 2) result.SkillId = ReadString(data, ref pos, data.Length);
            else if (field == 3 && wire == 5) result.TargetX = ReadFloat(data, ref pos);
            else if (field == 4 && wire == 5) result.TargetZ = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeCloneState ParseGameThreeCloneState(byte[] data, int startPos, int length)
    {
        var result = new GameThreeCloneState();
        var pos = startPos;
        var end = startPos + length;
        while (pos < end && TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.CloneId = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 0) result.OwnerSlot = (int)ReadVarInt(data, ref pos);
            else if (field == 3 && wire == 5) result.PositionX = ReadFloat(data, ref pos);
            else if (field == 4 && wire == 5) result.PositionZ = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeHiddenTerritoryState ParseGameThreeHiddenTerritoryState(byte[] data, int startPos, int length)
    {
        var result = new GameThreeHiddenTerritoryState();
        var pos = startPos;
        var end = startPos + length;
        while (pos < end && TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.GridX = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 0) result.GridZ = (int)ReadVarInt(data, ref pos);
            else if (field == 3 && wire == 0) result.OwnerSlot = (int)ReadVarInt(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeTileMarkerState ParseGameThreeTileMarkerState(byte[] data, int startPos, int length)
    {
        var result = new GameThreeTileMarkerState();
        var pos = startPos;
        var end = startPos + length;
        while (pos < end && TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.GridX = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 0) result.GridZ = (int)ReadVarInt(data, ref pos);
            else if (field == 3 && wire == 0) result.OwnerSlot = (int)ReadVarInt(data, ref pos);
            else if (field == 4 && wire == 2) result.MarkerId = ReadString(data, ref pos, end);
            else if (field == 5 && wire == 5) result.TimeRemaining = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeProjectileState ParseGameThreeProjectileState(byte[] data, int startPos, int length)
    {
        var result = new GameThreeProjectileState();
        var pos = startPos;
        var end = startPos + length;
        while (pos < end && TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.ProjectileId = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 0) result.OwnerSlot = (int)ReadVarInt(data, ref pos);
            else if (field == 3 && wire == 5) result.PositionX = ReadFloat(data, ref pos);
            else if (field == 4 && wire == 5) result.PositionY = ReadFloat(data, ref pos);
            else if (field == 5 && wire == 5) result.PositionZ = ReadFloat(data, ref pos);
            else if (field == 6 && wire == 5) result.VelocityX = ReadFloat(data, ref pos);
            else if (field == 7 && wire == 5) result.VelocityZ = ReadFloat(data, ref pos);
            else if (field == 8 && wire == 0) result.CombatLayer = (int)ReadVarInt(data, ref pos);
            else if (field == 9 && wire == 5) result.Scale = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeWeatherState ParseGameThreeWeatherState(byte[] data, int startPos, int length)
    {
        var result = new GameThreeWeatherState();
        var pos = startPos;
        var end = startPos + length;
        while (pos < end && TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.WeatherType = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 5) result.RemainingSeconds = ReadFloat(data, ref pos);
            else if (field == 3 && wire == 5) result.WindX = ReadFloat(data, ref pos);
            else if (field == 4 && wire == 5) result.WindZ = ReadFloat(data, ref pos);
            else if (field == 5 && wire == 5) result.LightningTargetX = ReadFloat(data, ref pos);
            else if (field == 6 && wire == 5) result.LightningTargetZ = ReadFloat(data, ref pos);
            else if (field == 7 && wire == 5) result.LightningWarningRemaining = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeHealingZoneState ParseGameThreeHealingZoneState(byte[] data, int startPos, int length)
    {
        var result = new GameThreeHealingZoneState();
        var pos = startPos;
        var end = startPos + length;
        while (pos < end && TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.ZoneId = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 0) result.MinX = (int)ReadVarInt(data, ref pos);
            else if (field == 3 && wire == 0) result.MaxX = (int)ReadVarInt(data, ref pos);
            else if (field == 4 && wire == 0) result.MinZ = (int)ReadVarInt(data, ref pos);
            else if (field == 5 && wire == 0) result.MaxZ = (int)ReadVarInt(data, ref pos);
            else if (field == 6 && wire == 0) result.OwnerSlot = (int)ReadVarInt(data, ref pos);
            else if (field == 7 && wire == 5) result.HealPerSecond = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeEventState ParseGameThreeEventState(byte[] data, int startPos, int length)
    {
        var result = new GameThreeEventState();
        var pos = startPos;
        var end = startPos + length;
        while (pos < end && TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.EventType = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 5) result.RemainingSeconds = ReadFloat(data, ref pos);
            else if (field == 3 && wire == 0) result.OwnerSlot = (int)ReadVarInt(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static GameThreeMatchPlayerResult ParseGameThreeMatchPlayerResult(byte[] data, int startPos, int length)
    {
        var result = new GameThreeMatchPlayerResult();
        var pos = startPos;
        var end = startPos + length;
        while (pos < end && TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.Rank = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 0) result.SlotIndex = (int)ReadVarInt(data, ref pos);
            else if (field == 3 && wire == 0) result.PlayerId = (int)ReadVarInt(data, ref pos);
            else if (field == 4 && wire == 2) result.BallId = ReadString(data, ref pos, end);
            else if (field == 5 && wire == 0) result.TerritoryCount = (int)ReadVarInt(data, ref pos);
            else if (field == 6 && wire == 0) result.CurrentHealth = (int)ReadVarInt(data, ref pos);
            else if (field == 7 && wire == 0) result.MaxHealth = (int)ReadVarInt(data, ref pos);
            else if (field == 8 && wire == 0) result.IsAlive = ReadVarInt(data, ref pos) != 0;
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static bool TryReadHeader(byte[] data, ref int pos, out int field, out int wire)
    {
        field = 0;
        wire = 0;
        if (data == null || pos >= data.Length)
        {
            return false;
        }

        var header = (int)ReadVarInt(data, ref pos);
        if (header == 0)
        {
            return false;
        }

        field = header >> 3;
        wire = header & 0x7;
        return true;
    }

    private sealed class DirectionalRequestValues
    {
        public int PlayerId;
        public float DirectionX;
        public float DirectionZ;
        public int Sequence;
    }
}
