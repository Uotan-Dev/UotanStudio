using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;

internal static class SparseFill
{
    internal static void Fill(Span<byte> destination, uint value, int phase = 0)
    {
        Span<byte> pattern = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pattern, value);
        if (phase != 0)
        {
            var head = Math.Min(4 - phase, destination.Length);
            pattern.Slice(phase, head).CopyTo(destination);
            destination = destination[head..];
        }
        var bodyLength = destination.Length & ~3;
        MemoryMarshal.Cast<byte, uint>(destination[..bodyLength])
            .Fill(BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value));
        pattern[..(destination.Length - bodyLength)].CopyTo(destination[bodyLength..]);
    }
}