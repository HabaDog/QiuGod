using System;
using System.Collections.Generic;
using System.IO;

namespace GameNetworkCommon
{
    /// <summary>
    /// Reassembles the byte stream produced by TCP into complete BaseMessage frames.
    /// </summary>
    public sealed class MessageFrameDecoder
    {
        private readonly int maxBodyLength;
        private readonly List<byte> buffer = new List<byte>();

        public MessageFrameDecoder(int maxBodyLength)
        {
            if (maxBodyLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBodyLength));
            }

            this.maxBodyLength = maxBodyLength;
        }

        public int BufferedByteCount => buffer.Count;

        public void Append(byte[] data, int offset, int count)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            for (var index = offset; index < offset + count; index++)
            {
                buffer.Add(data[index]);
            }
        }

        public bool TryRead(out BaseMessage message)
        {
            message = default!;
            if (buffer.Count < BaseMessage.HeaderSize)
            {
                return false;
            }

            var header = buffer.GetRange(0, BaseMessage.HeaderSize).ToArray();
            var bodyLength = BitConverter.ToInt32(header, sizeof(ushort));
            if (bodyLength < 0 || bodyLength > maxBodyLength)
            {
                throw new InvalidDataException(
                    $"Packet body length {bodyLength} is outside the allowed range 0..{maxBodyLength}.");
            }

            var frameLength = BaseMessage.HeaderSize + bodyLength;
            if (buffer.Count < frameLength)
            {
                return false;
            }

            var frame = buffer.GetRange(0, frameLength).ToArray();
            buffer.RemoveRange(0, frameLength);
            message = BaseMessage.FromBytes(frame, 0, frame.Length);
            return true;
        }

        public void Clear()
        {
            buffer.Clear();
        }
    }
}
