using System.Buffers.Binary;
using System.Text;
using UotanStudio.Mirroring;

namespace UotanStudio.Tests.Mirroring;

internal static class OhscrcpyTestData
{
    internal static byte[] BuildSps(int widthInMacroblocks, int heightInMapUnits, int cropBottom)
    {
        var writer = new BitWriter();
        writer.WriteBits(66, 8);
        writer.WriteBits(0, 8);
        writer.WriteBits(31, 8);
        writer.WriteUnsignedExpGolomb(0);
        writer.WriteUnsignedExpGolomb(0);
        writer.WriteUnsignedExpGolomb(2);
        writer.WriteUnsignedExpGolomb(1);
        writer.WriteBit(false);
        writer.WriteUnsignedExpGolomb(widthInMacroblocks - 1);
        writer.WriteUnsignedExpGolomb(heightInMapUnits - 1);
        writer.WriteBit(true);
        writer.WriteBit(true);
        if (cropBottom > 0)
        {
            writer.WriteBit(true);
            writer.WriteUnsignedExpGolomb(0);
            writer.WriteUnsignedExpGolomb(0);
            writer.WriteUnsignedExpGolomb(0);
            writer.WriteUnsignedExpGolomb(cropBottom);
        }
        else
        {
            writer.WriteBit(false);
        }

        writer.WriteBit(false);
        writer.WriteBit(true);
        while (writer.BitCount % 8 != 0) writer.WriteBit(false);
        return [0x67, .. writer.ToArray()];
    }

    internal static byte[] AnnexB(params byte[][] nalUnits)
    {
        using var stream = new MemoryStream();
        foreach (var nal in nalUnits)
        {
            stream.Write([0, 0, 0, 1]);
            stream.Write(nal);
        }

        return stream.ToArray();
    }

    internal static byte[] BuildGrpcFrame(byte[] payload)
    {
        var frame = new byte[OhscrcpyConstants.GrpcHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(frame, OhscrcpyConstants.GrpcHeaderLength);
        return frame;
    }

    internal static byte[] BuildVideoReply(byte[] accessUnit)
    {
        var output = new List<byte>();
        var entry = new List<byte>();
        entry.Add(0x0A);
        WriteVarint(entry, 4);
        entry.AddRange(Encoding.UTF8.GetBytes("data"));
        var value = new List<byte>();
        value.Add(0x2A);
        WriteVarint(value, accessUnit.Length);
        value.AddRange(accessUnit);
        entry.Add(0x12);
        WriteVarint(entry, value.Count);
        entry.AddRange(value);
        output.Add(0x1A);
        WriteVarint(output, entry.Count);
        output.AddRange(entry);
        return [.. output];
    }

    private static void WriteVarint(List<byte> destination, int value)
    {
        var remaining = (uint)value;
        while (remaining >= 0x80)
        {
            destination.Add((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        destination.Add((byte)remaining);
    }

    private sealed class BitWriter
    {
        private readonly List<bool> _bits = [];

        internal int BitCount => _bits.Count;

        internal void WriteBit(bool value) => _bits.Add(value);

        internal void WriteBits(int value, int count)
        {
            for (var index = count - 1; index >= 0; index--) _bits.Add(((value >> index) & 1) != 0);
        }

        internal void WriteUnsignedExpGolomb(int value)
        {
            var code = (uint)value + 1;
            var bitLength = 0;
            for (var remaining = code; remaining > 0; remaining >>= 1) bitLength++;
            for (var index = 0; index < bitLength - 1; index++) _bits.Add(false);
            for (var index = bitLength - 1; index >= 0; index--) _bits.Add(((code >> index) & 1) != 0);
        }

        internal byte[] ToArray()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (var index = 0; index < _bits.Count; index++)
            {
                if (_bits[index]) bytes[index >> 3] |= (byte)(0x80 >> (index & 7));
            }

            return bytes;
        }
    }
}