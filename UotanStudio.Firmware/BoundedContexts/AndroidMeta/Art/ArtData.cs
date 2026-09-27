using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Art;

internal sealed class ArtData(Stream stream, long origin, long length, CancellationToken cancellationToken)
{
    internal long Length => length;

    internal void Check(long offset, long count)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0 || count < 0 || offset > length || count > length - offset)
        {
            throw new InvalidDataException("ART data range is outside its owning section.");
        }
    }

    internal void Read(long offset, Span<byte> target)
    {
        Check(offset, target.Length);
        stream.Position = checked(origin + offset);
        stream.ReadExactly(target);
    }

    internal byte Byte(long offset)
    {
        Span<byte> bytes = stackalloc byte[1];
        Read(offset, bytes);
        return bytes[0];
    }

    internal ushort U16(long offset)
    {
        Span<byte> bytes = stackalloc byte[2];
        Read(offset, bytes);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    internal uint U32(long offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        Read(offset, bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    internal uint Leb(ref long offset)
    {
        uint result = 0;
        for (var i = 0; i < 5; i++)
        {
            var value = Byte(offset++);
            if (i == 4 && value > 15)
            {
                throw new InvalidDataException("ART ULEB128 overflows UInt32.");
            }
            result |= (uint)(value & 127) << (i * 7);
            if ((value & 128) == 0)
            {
                return result;
            }
        }
        throw new InvalidDataException("Unterminated ART ULEB128.");
    }

    internal int SignedLeb(ref long offset)
    {
        uint result = 0;
        for (var i = 0; i < 5; i++)
        {
            var value = Byte(offset++);
            if (i == 4 && (value & 0x7f) is > 7 and < 120)
            {
                throw new InvalidDataException("ART SLEB128 overflows Int32.");
            }
            result |= (uint)(value & 127) << (i * 7);
            if ((value & 128) == 0)
            {
                var bits = (i + 1) * 7;
                if (bits < 32 && (value & 64) != 0)
                {
                    result |= uint.MaxValue << bits;
                }
                return unchecked((int)result);
            }
        }
        throw new InvalidDataException("Unterminated ART SLEB128.");
    }

    internal void Write16(long offset, ushort value)
    {
        Check(offset, 2);
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Position = checked(origin + offset);
        stream.Write(bytes);
    }

    internal void Write32(long offset, uint value)
    {
        Check(offset, 4);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Position = checked(origin + offset);
        stream.Write(bytes);
    }

    internal void ValidateDex(bool modern = false)
    {
        Span<byte> header = stackalloc byte[112];
        Read(0, header);
        if (!(header[..8].SequenceEqual("dex\n035\0"u8) ||
              header[..8].SequenceEqual("dex\n037\0"u8) ||
              header[..8].SequenceEqual("dex\n038\0"u8) ||
              modern && header[..8].SequenceEqual("dex\n039\0"u8)) ||
            U32(32) != length || U32(36) != 112 || U32(40) != 0x12345678)
        {
            throw new InvalidDataException("Expected a little-endian standard DEX 035, 037 or 038.");
        }
        foreach (var (offset, size) in new[] { (56, 4), (64, 4), (72, 12), (80, 8), (88, 8), (96, 32) })
        {
            var count = U32(offset);
            var start = U32(offset + 4);
            if (count > 1_000_000 || (count != 0 && start < 112))
            {
                throw new InvalidDataException("Invalid DEX table.");
            }
            Check(start, (long)count * size);
        }
        Check(U32(108), U32(104));
    }

    internal void VerifyDexHashes()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[65536];
        uint a = 1;
        uint b = 0;
        for (long offset = 12; offset < length;)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            Read(offset, buffer.AsSpan(0, count));
            var skip = (int)Math.Max(0, 32 - offset);
            if (count > skip)
            {
                sha.AppendData(buffer, skip, count - skip);
            }
            for (var i = 0; i < count; i++)
            {
                a = (a + buffer[i]) % 65521;
                b = (b + a) % 65521;
            }
            offset += count;
        }
        Span<byte> signature = stackalloc byte[20];
        Read(12, signature);
        if (U32(8) != ((b << 16) | a) || !signature.SequenceEqual(sha.GetHashAndReset()))
        {
            throw new InvalidDataException("Restored DEX does not match its original SHA-1 and Adler32.");
        }
    }

    internal void WriteDexHashes()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[65536];
        for (long offset = 32; offset < length;)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            Read(offset, buffer.AsSpan(0, count));
            sha.AppendData(buffer, 0, count);
            offset += count;
        }
        stream.Position = origin + 12;
        stream.Write(sha.GetHashAndReset());
        uint a = 1;
        uint b = 0;
        for (long offset = 12; offset < length;)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            Read(offset, buffer.AsSpan(0, count));
            for (var i = 0; i < count; i++)
            {
                a = (a + buffer[i]) % 65521;
                b = (b + a) % 65521;
            }
            offset += count;
        }
        Write32(8, (b << 16) | a);
    }
}
