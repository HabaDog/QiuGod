using System;
using System.Collections.Generic;
using System.Text;
using GameNetworkCommon;

/// <summary>
/// Protobuf 手动解析工具类
/// 用于在 IL2CPP 下解析包含 List<T> 字段的消息类型
/// 因为 protobuf-net 在 IL2CPP 下无法正确反序列化 List<T> 字段
/// </summary>
public static partial class ProtoBufManualParser
{
    public static EnterRoomResponse ParseEnterRoomResponse(byte[] data)
    {
        var result = new EnterRoomResponse();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.Success = ReadVarInt(data, ref pos) != 0;
            else if (field == 2 && wire == 2) result.Message = ReadString(data, ref pos, data.Length);
            else if (field == 3 && wire == 2) result.Players.Add(ReadMessage(data, ref pos, ParseRoomPlayerInfo));
            else if (field == 4 && wire == 2) result.GameMode = ReadString(data, ref pos, data.Length);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    public static RoomStateUpdate ParseRoomStateUpdate(byte[] data)
    {
        var result = new RoomStateUpdate();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 2) result.Players.Add(ReadMessage(data, ref pos, ParseRoomPlayerInfo));
            else if (field == 2 && wire == 0) result.GameCanStart = ReadVarInt(data, ref pos) != 0;
            else if (field == 3 && wire == 2) result.GameMode = ReadString(data, ref pos, data.Length);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    public static GameThreeBallSelectionStateUpdate ParseGameThreeBallSelectionStateUpdate(byte[] data)
    {
        var result = new GameThreeBallSelectionStateUpdate();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 2)
            {
                result.Selections.Add(ReadMessage(data, ref pos, ParseGameThreeBallSelectionEntry));
            }
            else
            {
                SkipField(data, ref pos, wire);
            }
        }
        return result;
    }

    private static GameThreeBallSelectionEntry ParseGameThreeBallSelectionEntry(byte[] data, int startPos, int length)
    {
        var result = new GameThreeBallSelectionEntry();
        var pos = startPos;
        var end = startPos + length;
        while (pos < end && TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.PlayerId = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 2) result.BallId = ReadString(data, ref pos, end);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    /// <summary>
    /// 手动解析 EnterRoomResponse 中的 Players 列表
    /// </summary>
    public static List<RoomPlayerInfo> ParseEnterRoomResponsePlayers(byte[] data)
    {
        return ParseRepeatedMessage<RoomPlayerInfo>(data, 3, ParseRoomPlayerInfo);
    }
    
    /// <summary>
    /// 手动解析 RoomStateUpdate 中的 Players 列表
    /// </summary>
    public static List<RoomPlayerInfo> ParseRoomStateUpdatePlayers(byte[] data)
    {
        return ParseRepeatedMessage<RoomPlayerInfo>(data, 1, ParseRoomPlayerInfo);
    }
    
    /// <summary>
    /// 手动解析 RoomPlayerInfo
    /// </summary>
    private static RoomPlayerInfo ParseRoomPlayerInfo(byte[] data, int startPos, int length)
    {
        var player = new RoomPlayerInfo();
        int pos = startPos;
        int endPos = startPos + length;
        
        while (pos < endPos)
        {
            if (pos >= endPos) break;
            int fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;
            
            int fieldNumber = fieldHeader >> 3;
            int wireType = fieldHeader & 0x7;
            
            if (fieldNumber == 1 && wireType == 0) // PlayerId (int32)
            {
                player.PlayerId = (int)ReadVarInt(data, ref pos);
            }
            else if (fieldNumber == 2 && wireType == 2) // Username (string)
            {
                int strLength = (int)ReadVarInt(data, ref pos);
                if (strLength > 0 && pos + strLength <= endPos)
                {
                    player.Username = Encoding.UTF8.GetString(data, pos, strLength);
                    pos += strLength;
                }
            }
            else if (fieldNumber == 3 && wireType == 0) // IsReady (bool)
            {
                player.IsReady = ReadVarInt(data, ref pos) != 0;
            }
            else if (fieldNumber == 4 && wireType == 0) // IsHost (bool)
            {
                player.IsHost = ReadVarInt(data, ref pos) != 0;
            }
            else if (fieldNumber == 5 && wireType == 2) // SelectedBallId (string)
            {
                int strLength = (int)ReadVarInt(data, ref pos);
                if (strLength > 0 && pos + strLength <= endPos)
                {
                    player.SelectedBallId = Encoding.UTF8.GetString(data, pos, strLength);
                    pos += strLength;
                }
            }
            else
            {
                SkipField(data, ref pos, wireType);
            }
        }
        
        return player;
    }
    
    /// <summary>
    /// Parses the GameThree authoritative snapshot. This mirrors the generated
    /// protobuf contract and is used only as an IL2CPP fallback for repeated
    /// fields, where protobuf-net can otherwise return empty lists.
    /// </summary>
    public static void ParseGameThreeStateSync(byte[] data, GameThreeStateSync result)
    {
        result.Players = ParseRepeatedMessage<GameThreePlayerState>(data, 1, ParseGameThreePlayerState);
        result.Bombs = ParseRepeatedMessage<GameThreeBombState>(data, 2, ParseGameThreeBombState);
        result.TerritoryGrid = ParseRepeatedInt32(data, 3);
        result.Pickups = ParseRepeatedMessage<GameThreePickupState>(data, 4, ParseGameThreePickupState);
        result.Obstacles = ParseRepeatedMessage<GameThreeObstacleState>(data, 5, ParseGameThreeObstacleState);
        result.TerrainGrid = ParseRepeatedInt32(data, 22);
        result.FrostZones = ParseRepeatedMessage<GameThreeFrostZoneState>(data, 23, ParseGameThreeFrostZoneState);

        var pos = 0;
        while (pos < data.Length)
        {
            var fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;

            var fieldNumber = fieldHeader >> 3;
            var wireType = fieldHeader & 0x7;
            if (fieldNumber == 6 && wireType == 5) result.RoundTimeRemaining = ReadFloat(data, ref pos);
            else if (fieldNumber == 7 && wireType == 0) result.MapWidth = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 8 && wireType == 0) result.MapHeight = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 9 && wireType == 0) result.MapSeed = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 10 && wireType == 0) result.IsRecoverySnapshot = ReadVarInt(data, ref pos) != 0;
            else if (fieldNumber == 11 && wireType == 0) result.IsRoundActive = ReadVarInt(data, ref pos) != 0;
            else SkipField(data, ref pos, wireType);
        }
    }

    public static void ParseGameThreeRoundStart(byte[] data, GameThreeRoundStart result)
    {
        result.Players = ParseRepeatedMessage<GameThreePlayerState>(data, 5, ParseGameThreePlayerState);
        result.Obstacles = ParseRepeatedMessage<GameThreeObstacleState>(data, 6, ParseGameThreeObstacleState);

        var pos = 0;
        while (pos < data.Length)
        {
            var fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;

            var fieldNumber = fieldHeader >> 3;
            var wireType = fieldHeader & 0x7;
            if (fieldNumber == 1 && wireType == 5) result.RoundDuration = ReadFloat(data, ref pos);
            else if (fieldNumber == 2 && wireType == 0) result.MapWidth = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 3 && wireType == 0) result.MapHeight = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 4 && wireType == 0) result.MapSeed = (int)ReadVarInt(data, ref pos);
            else SkipField(data, ref pos, wireType);
        }
    }

    public static void ParseGameThreeBombExploded(byte[] data, GameThreeBombExploded result)
    {
        result.AffectedCells = ParseRepeatedMessage<GameThreeGridCell>(data, 4, ParseGameThreeGridCell);
        result.DestroyedObstacles = ParseRepeatedMessage<GameThreeGridCell>(data, 5, ParseGameThreeGridCell);

        var pos = 0;
        while (pos < data.Length)
        {
            var fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;

            var fieldNumber = fieldHeader >> 3;
            var wireType = fieldHeader & 0x7;
            if (fieldNumber == 1 && wireType == 0) result.BombId = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 2 && wireType == 0) result.GridX = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 3 && wireType == 0) result.GridZ = (int)ReadVarInt(data, ref pos);
            else SkipField(data, ref pos, wireType);
        }
    }

    public static void ParseGameThreeGameOver(byte[] data, GameThreeGameOver result)
    {
        result.TerritoryScores = ParseRepeatedInt32(data, 2);

        var pos = 0;
        while (pos < data.Length)
        {
            var fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;

            var fieldNumber = fieldHeader >> 3;
            var wireType = fieldHeader & 0x7;
            if (fieldNumber == 1 && wireType == 0) result.WinnerSlot = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 3 && wireType == 2) result.Reason = ReadString(data, ref pos, data.Length);
            else SkipField(data, ref pos, wireType);
        }
    }

    private static GameThreePlayerState ParseGameThreePlayerState(byte[] data, int startPos, int length)
    {
        var result = new GameThreePlayerState();
        var pos = startPos;
        var endPos = startPos + length;
        while (pos < endPos)
        {
            var fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;

            var fieldNumber = fieldHeader >> 3;
            var wireType = fieldHeader & 0x7;
            if (fieldNumber == 1 && wireType == 0) result.PlayerId = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 2 && wireType == 0) result.SlotIndex = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 3 && wireType == 0) result.IsAi = ReadVarInt(data, ref pos) != 0;
            else if (fieldNumber == 4 && wireType == 2) result.BallId = ReadString(data, ref pos, endPos);
            else if (fieldNumber == 5 && wireType == 5) result.PositionX = ReadFloat(data, ref pos);
            else if (fieldNumber == 6 && wireType == 5) result.PositionY = ReadFloat(data, ref pos);
            else if (fieldNumber == 7 && wireType == 5) result.PositionZ = ReadFloat(data, ref pos);
            else if (fieldNumber == 8 && wireType == 0) result.IsAlive = ReadVarInt(data, ref pos) != 0;
            else if (fieldNumber == 9 && wireType == 0) result.CurrentHealth = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 10 && wireType == 0) result.MaxHealth = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 11 && wireType == 0) result.TerritoryCount = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 12 && wireType == 5) result.SkillCooldownRemaining = ReadFloat(data, ref pos);
            else if (fieldNumber == 13 && wireType == 0) result.ActivePowerUpType = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 14 && wireType == 5) result.PowerUpRemaining = ReadFloat(data, ref pos);
            else if (fieldNumber == 15 && wireType == 5) result.ShieldRemaining = ReadFloat(data, ref pos);
            else if (fieldNumber == 16 && wireType == 5) result.VelocityX = ReadFloat(data, ref pos);
            else if (fieldNumber == 17 && wireType == 5) result.VelocityY = ReadFloat(data, ref pos);
            else if (fieldNumber == 18 && wireType == 5) result.VelocityZ = ReadFloat(data, ref pos);
            else if (fieldNumber == 19 && wireType == 0) result.Energy = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 20 && wireType == 0) result.EnergyRequired = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 21 && wireType == 5) result.BombCooldownRemaining = ReadFloat(data, ref pos);
            else if (fieldNumber == 22 && wireType == 5) result.DashCooldownRemaining = ReadFloat(data, ref pos);
            else if (fieldNumber == 23 && wireType == 5) result.ActiveSkillRemaining = ReadFloat(data, ref pos);
            else if (fieldNumber == 24 && wireType == 2) result.ActiveSkillId = ReadString(data, ref pos, endPos);
            else if (fieldNumber == 25 && wireType == 0) result.LastProcessedInputSequence = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 26 && wireType == 5) result.EffectiveMoveSpeed = ReadFloat(data, ref pos);
            else if (fieldNumber == 27 && wireType == 0) result.IsTimeStopped = ReadVarInt(data, ref pos) != 0;
            else if (fieldNumber == 28 && wireType == 5) result.ShiftBoostCharge = ReadFloat(data, ref pos);
            else if (fieldNumber == 29 && wireType == 0) result.IsShiftBoosting = ReadVarInt(data, ref pos) != 0;
            else if (fieldNumber == 30 && wireType == 0) result.IsUnstoppable = ReadVarInt(data, ref pos) != 0;
            else if (fieldNumber == 31 && wireType == 5) result.Imbalance = ReadFloat(data, ref pos);
            else if (fieldNumber == 32 && wireType == 0) result.CombatLayer = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 33 && wireType == 0) result.KillRewardType = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 34 && wireType == 5) result.KillRewardRemaining = ReadFloat(data, ref pos);
            else if (fieldNumber == 35 && wireType == 0) result.IsConcealed = ReadVarInt(data, ref pos) != 0;
            else if (fieldNumber == 36 && wireType == 0) result.OverchargeShotsRemaining = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 37 && wireType == 5) result.OverchargeRemaining = ReadFloat(data, ref pos);
            else if (fieldNumber == 38 && wireType == 0) result.TwinShotShotsRemaining = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 39 && wireType == 0) result.FreeSkillCharges = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 40 && wireType == 5) result.TerritoryPulseRemaining = ReadFloat(data, ref pos);
            else if (fieldNumber == 41 && wireType == 0) result.AegisCoreReady = ReadVarInt(data, ref pos) != 0;
            else if (fieldNumber == 42 && wireType == 0) result.EchoCoreReady = ReadVarInt(data, ref pos) != 0;
            else if (fieldNumber == 43 && wireType == 0) result.TemporalAnchorReady = ReadVarInt(data, ref pos) != 0;
            else if (fieldNumber == 44 && wireType == 0) result.StormCoreReady = ReadVarInt(data, ref pos) != 0;
            else if (fieldNumber == 45 && wireType == 5) result.StormCoreRemaining = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wireType);
        }

        return result;
    }

    private static GameThreeBombState ParseGameThreeBombState(byte[] data, int startPos, int length)
    {
        var result = new GameThreeBombState();
        var pos = startPos;
        var endPos = startPos + length;
        while (pos < endPos)
        {
            var fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;

            var fieldNumber = fieldHeader >> 3;
            var wireType = fieldHeader & 0x7;
            if (fieldNumber == 1 && wireType == 0) result.BombId = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 2 && wireType == 0) result.OwnerSlot = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 3 && wireType == 5) result.OriginX = ReadFloat(data, ref pos);
            else if (fieldNumber == 4 && wireType == 5) result.OriginZ = ReadFloat(data, ref pos);
            else if (fieldNumber == 5 && wireType == 5) result.TargetX = ReadFloat(data, ref pos);
            else if (fieldNumber == 6 && wireType == 5) result.TargetZ = ReadFloat(data, ref pos);
            else if (fieldNumber == 7 && wireType == 5) result.TimeRemaining = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wireType);
        }

        return result;
    }

    private static GameThreePickupState ParseGameThreePickupState(byte[] data, int startPos, int length)
    {
        var result = new GameThreePickupState();
        var pos = startPos;
        var endPos = startPos + length;
        while (pos < endPos)
        {
            var fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;

            var fieldNumber = fieldHeader >> 3;
            var wireType = fieldHeader & 0x7;
            if (fieldNumber == 1 && wireType == 0) result.PickupId = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 2 && wireType == 0) result.PickupType = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 3 && wireType == 0) result.GridX = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 4 && wireType == 0) result.GridZ = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 5 && wireType == 5) result.TimeRemaining = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wireType);
        }

        return result;
    }

    private static GameThreeFrostZoneState ParseGameThreeFrostZoneState(byte[] data, int startPos, int length)
    {
        var result = new GameThreeFrostZoneState();
        var pos = startPos;
        var endPos = startPos + length;
        while (pos < endPos)
        {
            var fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;

            var fieldNumber = fieldHeader >> 3;
            var wireType = fieldHeader & 0x7;
            if (fieldNumber == 1 && wireType == 0) result.ZoneId = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 2 && wireType == 0) result.OwnerSlot = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 3 && wireType == 5) result.PositionX = ReadFloat(data, ref pos);
            else if (fieldNumber == 4 && wireType == 5) result.PositionZ = ReadFloat(data, ref pos);
            else if (fieldNumber == 5 && wireType == 5) result.Radius = ReadFloat(data, ref pos);
            else if (fieldNumber == 6 && wireType == 5) result.RemainingSeconds = ReadFloat(data, ref pos);
            else SkipField(data, ref pos, wireType);
        }

        return result;
    }

    private static GameThreeObstacleState ParseGameThreeObstacleState(byte[] data, int startPos, int length)
    {
        var result = new GameThreeObstacleState();
        var pos = startPos;
        var endPos = startPos + length;
        while (pos < endPos)
        {
            var fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;

            var fieldNumber = fieldHeader >> 3;
            var wireType = fieldHeader & 0x7;
            if (fieldNumber == 1 && wireType == 0) result.GridX = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 2 && wireType == 0) result.GridZ = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 3 && wireType == 0) result.IsDestructible = ReadVarInt(data, ref pos) != 0;
            else SkipField(data, ref pos, wireType);
        }

        return result;
    }

    private static GameThreeGridCell ParseGameThreeGridCell(byte[] data, int startPos, int length)
    {
        var result = new GameThreeGridCell();
        var pos = startPos;
        var endPos = startPos + length;
        while (pos < endPos)
        {
            var fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;

            var fieldNumber = fieldHeader >> 3;
            var wireType = fieldHeader & 0x7;
            if (fieldNumber == 1 && wireType == 0) result.GridX = (int)ReadVarInt(data, ref pos);
            else if (fieldNumber == 2 && wireType == 0) result.GridZ = (int)ReadVarInt(data, ref pos);
            else SkipField(data, ref pos, wireType);
        }

        return result;
    }

    private static string ReadString(byte[] data, ref int pos, int endPos)
    {
        var length = (int)ReadVarInt(data, ref pos);
        if (length <= 0 || pos + length > endPos)
        {
            return string.Empty;
        }

        var value = Encoding.UTF8.GetString(data, pos, length);
        pos += length;
        return value;
    }
    
    /// <summary>
    /// 解析 repeated message 字段（List<T> where T is a message type）
    /// </summary>
    private static List<T> ParseRepeatedMessage<T>(byte[] data, int targetFieldNumber, Func<byte[], int, int, T> parser)
    {
        var list = new List<T>();
        int pos = 0;
        
        while (pos < data.Length)
        {
            int fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;
            
            int fieldNumber = fieldHeader >> 3;
            int wireType = fieldHeader & 0x7;
            
            if (fieldNumber == targetFieldNumber && wireType == 2) // Length-delimited
            {
                int length = (int)ReadVarInt(data, ref pos);
                if (length > 0 && pos + length <= data.Length)
                {
                    var item = parser(data, pos, length);
                    list.Add(item);
                    pos += length;
                }
            }
            else
            {
                SkipField(data, ref pos, wireType);
            }
        }
        
        return list;
    }
    
    /// <summary>
    /// 从指定位置解析 repeated message 字段（用于嵌套消息）
    /// </summary>
    private static List<T> ParseRepeatedMessageFromPosition<T>(byte[] data, int startPos, int totalLength, Func<byte[], int, int, T> parser)
    {
        var list = new List<T>();
        int pos = startPos;
        int endPos = startPos + totalLength;
        
        while (pos < endPos)
        {
            if (pos >= endPos) break;
            int fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;
            
            int fieldNumber = fieldHeader >> 3;
            int wireType = fieldHeader & 0x7;
            
            if (wireType == 2) // Length-delimited (nested message)
            {
                int length = (int)ReadVarInt(data, ref pos);
                if (length > 0 && pos + length <= endPos)
                {
                    var item = parser(data, pos, length);
                    list.Add(item);
                    pos += length;
                }
            }
            else
            {
                SkipField(data, ref pos, wireType);
            }
        }
        
        return list;
    }
    
    /// <summary>
    /// 解析 repeated int32 字段（List<int>）
    /// </summary>
    private static List<int> ParseRepeatedInt32(byte[] data, int targetFieldNumber)
    {
        var list = new List<int>();
        int pos = 0;
        
        while (pos < data.Length)
        {
            int fieldHeader = (int)ReadVarInt(data, ref pos);
            if (fieldHeader == 0) break;
            
            int fieldNumber = fieldHeader >> 3;
            int wireType = fieldHeader & 0x7;
            
            if (fieldNumber == targetFieldNumber)
            {
                if (wireType == 0) // Packed varint
                {
                    list.Add((int)ReadVarInt(data, ref pos));
                }
                else if (wireType == 2) // Length-delimited (packed)
                {
                    int length = (int)ReadVarInt(data, ref pos);
                    int endPos = pos + length;
                    while (pos < endPos)
                    {
                        list.Add((int)ReadVarInt(data, ref pos));
                    }
                }
            }
            else
            {
                SkipField(data, ref pos, wireType);
            }
        }
        
        return list;
    }
    
    /// <summary>
    /// 解析 repeated int32 字段（从指定位置开始，用于嵌套消息）
    /// </summary>
    private static List<int> ParseRepeatedInt32(byte[] data, ref int pos)
    {
        var list = new List<int>();
        int length = (int)ReadVarInt(data, ref pos);
        int endPos = pos + length;
        
        while (pos < endPos)
        {
            list.Add((int)ReadVarInt(data, ref pos));
        }
        
        return list;
    }
    
    /// <summary>
    /// 读取 protobuf varint
    /// </summary>
    private static ulong ReadVarInt(byte[] data, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            
            if ((b & 0x80) == 0)
                break;
                
            shift += 7;
            if (shift >= 64)
                throw new InvalidOperationException("Varint too long");
        }
        
        return result;
    }
    
    /// <summary>
    /// 读取 float (32-bit)
    /// </summary>
    private static float ReadFloat(byte[] data, ref int pos)
    {
        if (pos + 4 > data.Length)
            throw new InvalidOperationException("Not enough data for float");
        
        uint bits = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
        pos += 4;
        return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
    }
    
    /// <summary>
    /// 跳过字段
    /// </summary>
    private static void SkipField(byte[] data, ref int pos, int wireType)
    {
        if (wireType == 0) // Varint
        {
            ReadVarInt(data, ref pos);
        }
        else if (wireType == 2) // Length-delimited
        {
            int length = (int)ReadVarInt(data, ref pos);
            pos += length;
        }
        else if (wireType == 5) // 32-bit
        {
            pos += 4;
        }
        else if (wireType == 1) // 64-bit
        {
            pos += 8;
        }
    }
}
