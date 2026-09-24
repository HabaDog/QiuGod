using GameNetworkCommon;

public static partial class ProtoBufManualParser
{
    public static bool TryParseLobby<T>(byte[] data, out T result)
    {
        object parsed;
        var type = typeof(T);

        if (type == typeof(LobbySnapshotResponse)) parsed = ParseLobbySnapshotResponse(data);
        else if (type == typeof(LobbyStateUpdate)) parsed = ParseLobbyStateUpdate(data);
        else if (type == typeof(CreateRoomResponse)) parsed = ParseCreateRoomResponse(data);
        else if (type == typeof(JoinRoomResponse)) parsed = ParseJoinRoomResponse(data);
        else if (type == typeof(MatchmakingJoinResponse)) parsed = ParseMatchmakingJoinResponse(data);
        else if (type == typeof(MatchmakingCancelResponse)) parsed = ParseMatchmakingCancelResponse(data);
        else if (type == typeof(PartyCreateResponse)) parsed = ParsePartyCreateResponse(data);
        else if (type == typeof(PartyJoinResponse)) parsed = ParsePartyJoinResponse(data);
        else if (type == typeof(PartyLeaveResponse)) parsed = ParsePartyLeaveResponse(data);
        else
        {
            result = default(T);
            return false;
        }

        result = (T)parsed;
        return true;
    }

    private static LobbySnapshotResponse ParseLobbySnapshotResponse(byte[] data)
    {
        var result = new LobbySnapshotResponse();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.Success = ReadVarInt(data, ref pos) != 0;
            else if (field == 2 && wire == 2) result.Message = ReadString(data, ref pos, data.Length);
            else if (field == 3 && wire == 2) result.Rooms.Add(ReadMessage(data, ref pos, ParseLobbyRoomInfo));
            else if (field == 4 && wire == 2) result.Party = ReadMessage(data, ref pos, ParsePartyInfo);
            else if (field == 5 && wire == 0) result.IsMatchmaking = ReadVarInt(data, ref pos) != 0;
            else if (field == 6 && wire == 0) result.QueuePosition = (int)ReadVarInt(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static LobbyStateUpdate ParseLobbyStateUpdate(byte[] data)
    {
        var result = new LobbyStateUpdate();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 2) result.Rooms.Add(ReadMessage(data, ref pos, ParseLobbyRoomInfo));
            else if (field == 2 && wire == 2) result.Party = ReadMessage(data, ref pos, ParsePartyInfo);
            else if (field == 3 && wire == 0) result.IsMatchmaking = ReadVarInt(data, ref pos) != 0;
            else if (field == 4 && wire == 0) result.QueuePosition = (int)ReadVarInt(data, ref pos);
            else if (field == 5 && wire == 0) result.MatchFound = ReadVarInt(data, ref pos) != 0;
            else if (field == 6 && wire == 2) result.RoomId = ReadString(data, ref pos, data.Length);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static CreateRoomResponse ParseCreateRoomResponse(byte[] data)
    {
        var result = new CreateRoomResponse();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.Success = ReadVarInt(data, ref pos) != 0;
            else if (field == 2 && wire == 2) result.Message = ReadString(data, ref pos, data.Length);
            else if (field == 3 && wire == 2) result.RoomId = ReadString(data, ref pos, data.Length);
            else if (field == 4 && wire == 2) result.JoinCode = ReadString(data, ref pos, data.Length);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static JoinRoomResponse ParseJoinRoomResponse(byte[] data)
    {
        var result = new JoinRoomResponse();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.Success = ReadVarInt(data, ref pos) != 0;
            else if (field == 2 && wire == 2) result.Message = ReadString(data, ref pos, data.Length);
            else if (field == 3 && wire == 2) result.RoomId = ReadString(data, ref pos, data.Length);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static MatchmakingJoinResponse ParseMatchmakingJoinResponse(byte[] data)
    {
        var result = new MatchmakingJoinResponse();
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.Success = ReadVarInt(data, ref pos) != 0;
            else if (field == 2 && wire == 2) result.Message = ReadString(data, ref pos, data.Length);
            else if (field == 3 && wire == 0) result.MatchFound = ReadVarInt(data, ref pos) != 0;
            else if (field == 4 && wire == 2) result.RoomId = ReadString(data, ref pos, data.Length);
            else if (field == 5 && wire == 0) result.QueuePosition = (int)ReadVarInt(data, ref pos);
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static MatchmakingCancelResponse ParseMatchmakingCancelResponse(byte[] data)
    {
        var result = new MatchmakingCancelResponse();
        ParseBasicResponse(data, (success, message) =>
        {
            result.Success = success;
            result.Message = message;
        });
        return result;
    }

    private static PartyCreateResponse ParsePartyCreateResponse(byte[] data)
    {
        var result = new PartyCreateResponse();
        ParsePartyResponse(data, (success, message, party) =>
        {
            result.Success = success;
            result.Message = message;
            result.Party = party;
        });
        return result;
    }

    private static PartyJoinResponse ParsePartyJoinResponse(byte[] data)
    {
        var result = new PartyJoinResponse();
        ParsePartyResponse(data, (success, message, party) =>
        {
            result.Success = success;
            result.Message = message;
            result.Party = party;
        });
        return result;
    }

    private static PartyLeaveResponse ParsePartyLeaveResponse(byte[] data)
    {
        var result = new PartyLeaveResponse();
        ParseBasicResponse(data, (success, message) =>
        {
            result.Success = success;
            result.Message = message;
        });
        return result;
    }

    private static LobbyRoomInfo ParseLobbyRoomInfo(byte[] data, int start, int length)
    {
        var result = new LobbyRoomInfo();
        var pos = start;
        var end = start + length;
        while (pos < end && TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 2) result.RoomId = ReadString(data, ref pos, end);
            else if (field == 2 && wire == 2) result.RoomName = ReadString(data, ref pos, end);
            else if (field == 3 && wire == 2) result.GameMode = ReadString(data, ref pos, end);
            else if (field == 4 && wire == 0) result.PlayerCount = (int)ReadVarInt(data, ref pos);
            else if (field == 5 && wire == 0) result.MaxPlayers = (int)ReadVarInt(data, ref pos);
            else if (field == 6 && wire == 0) result.IsPrivate = ReadVarInt(data, ref pos) != 0;
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static PartyInfo ParsePartyInfo(byte[] data, int start, int length)
    {
        var result = new PartyInfo();
        var pos = start;
        var end = start + length;
        while (pos < end && TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 2) result.PartyId = ReadString(data, ref pos, end);
            else if (field == 2 && wire == 2) result.JoinCode = ReadString(data, ref pos, end);
            else if (field == 3 && wire == 2) result.Members.Add(ReadMessage(data, ref pos, ParsePartyMemberInfo));
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static PartyMemberInfo ParsePartyMemberInfo(byte[] data, int start, int length)
    {
        var result = new PartyMemberInfo();
        var pos = start;
        var end = start + length;
        while (pos < end && TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) result.PlayerId = (int)ReadVarInt(data, ref pos);
            else if (field == 2 && wire == 2) result.Username = ReadString(data, ref pos, end);
            else if (field == 3 && wire == 0) result.IsLeader = ReadVarInt(data, ref pos) != 0;
            else SkipField(data, ref pos, wire);
        }
        return result;
    }

    private static TMessage ReadMessage<TMessage>(byte[] data, ref int pos, System.Func<byte[], int, int, TMessage> parser)
    {
        var length = (int)ReadVarInt(data, ref pos);
        var value = parser(data, pos, length);
        pos += length;
        return value;
    }

    private static void ParseBasicResponse(byte[] data, System.Action<bool, string> assign)
    {
        var success = false;
        var message = string.Empty;
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) success = ReadVarInt(data, ref pos) != 0;
            else if (field == 2 && wire == 2) message = ReadString(data, ref pos, data.Length);
            else SkipField(data, ref pos, wire);
        }
        assign(success, message);
    }

    private static void ParsePartyResponse(byte[] data, System.Action<bool, string, PartyInfo> assign)
    {
        var success = false;
        var message = string.Empty;
        PartyInfo party = null;
        var pos = 0;
        while (TryReadHeader(data, ref pos, out var field, out var wire))
        {
            if (field == 1 && wire == 0) success = ReadVarInt(data, ref pos) != 0;
            else if (field == 2 && wire == 2) message = ReadString(data, ref pos, data.Length);
            else if (field == 3 && wire == 2) party = ReadMessage(data, ref pos, ParsePartyInfo);
            else SkipField(data, ref pos, wire);
        }
        assign(success, message, party);
    }
}
