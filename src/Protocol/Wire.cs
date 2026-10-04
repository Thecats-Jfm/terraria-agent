using System;
using System.Diagnostics;
using System.IO;

namespace TerrariaAgent.Protocol
{
    // Four-byte unsigned network-order length, then exactly that many UTF-8 JSON bytes.
    // The length is validated before payload allocation. No network server lives here.
    public static class Wire
    {
        public const int IoTimeoutMs = 3000;

        public static byte[] ReadFrame(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            var deadline = Stopwatch.StartNew();
            int originalTimeout = stream.CanTimeout ? stream.ReadTimeout : 0;
            try
            {
                var header = new byte[4];
                ReadExact(stream, header, deadline);
                uint length = ((uint)header[0] << 24) | ((uint)header[1] << 16) |
                              ((uint)header[2] << 8) | header[3];
                if (length == 0 || length > ProtocolLimits.MaxFrameBytes)
                    throw new InvalidDataException("Frame length must be 1 to 4096 bytes.");
                var payload = new byte[(int)length];
                ReadExact(stream, payload, deadline);
                return payload;
            }
            finally
            {
                if (stream.CanTimeout)
                    try { stream.ReadTimeout = originalTimeout; } catch (ObjectDisposedException) { }
            }
        }

        public static void WriteFrame(Stream stream, byte[] payload)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (payload == null || payload.Length == 0 || payload.Length > ProtocolLimits.MaxFrameBytes)
                throw new InvalidDataException("Frame length must be 1 to 4096 bytes.");
            int length = payload.Length;
            var header = new[] { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length };
            stream.Write(header, 0, header.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        public static T Read<T>(Stream stream)
        {
            return JsonCodec.Deserialize<T>(ReadFrame(stream));
        }

        public static void Write<T>(Stream stream, T value)
        {
            WriteFrame(stream, JsonCodec.Serialize(value));
        }

        private static void ReadExact(Stream stream, byte[] buffer, Stopwatch deadline)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                long remaining = IoTimeoutMs - deadline.ElapsedMilliseconds;
                if (remaining <= 0) throw new IOException("Frame read exceeded its 3 second deadline.");
                // Setting the remaining budget also prevents slow trickle bytes from
                // extending NetworkStream reads indefinitely. In-memory tests need no timeout.
                if (stream.CanTimeout) stream.ReadTimeout = (int)remaining;
                int count = stream.Read(buffer, offset, buffer.Length - offset);
                if (count == 0) throw new EndOfStreamException("Truncated frame.");
                offset += count;
                if (deadline.ElapsedMilliseconds >= IoTimeoutMs)
                    throw new IOException("Frame read exceeded its 3 second deadline.");
            }
        }
    }
}
