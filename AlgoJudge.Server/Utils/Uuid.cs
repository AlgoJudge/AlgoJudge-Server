using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// Single point where entity identifiers are generated.
    /// <para>
    /// Version 7 rather than version 4: a random v4 primary key scatters inserts
    /// across the index and fragments it as the table grows, while v7 is
    /// time-ordered and appends. The difference is invisible at development
    /// scale and expensive to reverse afterwards.
    /// </para>
    /// <para>
    /// <c>Guid.CreateVersion7()</c> arrived in .NET 9 and this project targets
    /// .NET 8, so the layout is written out here. Delete this and call the
    /// framework method after an upgrade — the produced values are compatible.
    /// </para>
    /// </summary>
    public static class Uuid
    {
        public static Guid New() => NewV7(DateTimeOffset.UtcNow);

        internal static Guid NewV7(DateTimeOffset timestamp)
        {
            Span<byte> bytes = stackalloc byte[16];
            RandomNumberGenerator.Fill(bytes[6..]);

            // Big-endian milliseconds since the Unix epoch in the first 48 bits.
            long unixMs = timestamp.ToUnixTimeMilliseconds();
            bytes[0] = (byte)(unixMs >> 40);
            bytes[1] = (byte)(unixMs >> 32);
            bytes[2] = (byte)(unixMs >> 24);
            bytes[3] = (byte)(unixMs >> 16);
            bytes[4] = (byte)(unixMs >> 8);
            bytes[5] = (byte)unixMs;

            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);   // version 7
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 4122 variant

            // Guid's own layout is little-endian for the first three groups, so
            // the big-endian bytes above have to be reversed to survive a
            // round trip through ToByteArray/ToString.
            return new Guid(
                BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]),
                BinaryPrimitives.ReadUInt16BigEndian(bytes[4..6]),
                BinaryPrimitives.ReadUInt16BigEndian(bytes[6..8]),
                bytes[8], bytes[9], bytes[10], bytes[11],
                bytes[12], bytes[13], bytes[14], bytes[15]);
        }
    }
}
