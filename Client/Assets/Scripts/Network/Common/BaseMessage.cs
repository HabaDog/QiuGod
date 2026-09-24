using System;
using System.IO;

namespace GameNetworkCommon
{
    /// <summary>
    /// Network packet envelope: [protocolId: ushort][bodyLength: int][body bytes].
    /// </summary>
    public class BaseMessage
    {
        public const int HeaderSize = 6;

        public ushort ProtocolId { get; set; }

        public int BodyLength { get; set; }

        public byte[] Body { get; set; } = Array.Empty<byte>();

        public BaseMessage()
        {
        }

        public BaseMessage(ushort protocolId, byte[] body)
        {
            ProtocolId = protocolId;
            Body = body ?? Array.Empty<byte>();
            BodyLength = Body.Length;
        }

        public int TotalLength => HeaderSize + BodyLength;

        public byte[] ToBytes()
        {
            Body ??= Array.Empty<byte>();
            BodyLength = Body.Length;

            using var stream = new MemoryStream(HeaderSize + BodyLength);
            using var writer = new BinaryWriter(stream);
            writer.Write(ProtocolId);
            writer.Write(BodyLength);
            if (BodyLength > 0)
            {
                writer.Write(Body, 0, BodyLength);
            }

            return stream.ToArray();
        }

        public static BaseMessage FromBytes(byte[] data, int startIndex, int count)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (startIndex < 0 || count < 0 || startIndex + count > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Packet range is outside the source buffer.");
            }

            if (count < HeaderSize)
            {
                throw new ArgumentException($"Packet length {count} is smaller than header length {HeaderSize}.");
            }

            using var stream = new MemoryStream(data, startIndex, count);
            using var reader = new BinaryReader(stream);
            var message = new BaseMessage
            {
                ProtocolId = reader.ReadUInt16(),
                BodyLength = reader.ReadInt32()
            };

            if (message.BodyLength < 0)
            {
                throw new ArgumentException($"Packet body length cannot be negative: {message.BodyLength}.");
            }

            var expectedTotalSize = HeaderSize + message.BodyLength;
            if (count < expectedTotalSize)
            {
                throw new ArgumentException($"Incomplete packet, expected {expectedTotalSize} bytes, got {count} bytes.");
            }

            message.Body = message.BodyLength > 0
                ? reader.ReadBytes(message.BodyLength)
                : Array.Empty<byte>();

            return message;
        }

        public override string ToString()
        {
            return $"BaseMessage[ProtocolId={ProtocolId}, BodyLength={BodyLength}, TotalLength={TotalLength}]";
        }
    }
}
