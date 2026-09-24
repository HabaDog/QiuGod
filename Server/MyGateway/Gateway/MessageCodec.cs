using GameNetworkCommon;
using ProtoBuf;

public static class MessageCodec
{
    public static T Decode<T>(BaseMessage message)
    {
        using var stream = new MemoryStream(message.Body);
        return Serializer.Deserialize<T>(stream);
    }

    public static byte[] EncodeBody<T>(T message)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, message);
        return stream.ToArray();
    }

    public static byte[] EncodePacket<T>(ushort protocolId, T message)
    {
        return new BaseMessage(protocolId, EncodeBody(message)).ToBytes();
    }
}
