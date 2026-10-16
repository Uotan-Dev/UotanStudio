using System.Buffers.Binary;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.AndroidMeta.Art;

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

    /// <summary>
    /// Verifies the DEX signature and header checksum. SHA-1 covers every byte from offset 32 on and Adler-32 is
    /// folded chunk by chunk through <c>UotanStudio.Hash.Adler32.Adler32.Compute</c>, starting at the seed <c>1</c>,
    /// over every byte from offset 12 on; that fold is provably equal to a single
    /// <c>UotanStudio.Hash.Adler32.Adler32.Compute(data.AsSpan(12))</c> call over the whole range.
    /// <para>校验 DEX 的签名与头部校验和。SHA-1 覆盖偏移 32 起的每个字节；Adler-32 以种子 <c>1</c> 起步，经
    /// <c>UotanStudio.Hash.Adler32.Adler32.Compute</c> 分块折叠偏移 12 起的每个字节，该折叠结果与对完整区间调用一次
    /// <c>UotanStudio.Hash.Adler32.Adler32.Compute(data.AsSpan(12))</c> 可证相等。</para>
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The restored DEX does not reproduce its stored SHA-1 signature and Adler-32 checksum.
    /// <para>还原后的 DEX 未能复现其存储的 SHA-1 签名与 Adler-32 校验和。</para>
    /// </exception>
    internal void VerifyDexHashes()
    {
        using var sha = Sha1.CreateAccumulator();
        var buffer = new byte[65536];
        var adler = 1u;
        for (long offset = 12; offset < length;)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            Read(offset, buffer.AsSpan(0, count));
            var skip = (int)Math.Max(0, 32 - offset);
            if (count > skip)
            {
                sha.Append(buffer.AsSpan(skip, count - skip));
            }
            adler = Hash.Adler32.Adler32.Compute(buffer.AsSpan(0, count), adler);
            offset += count;
        }
        Span<byte> signature = stackalloc byte[20];
        Read(12, signature);
        if (U32(8) != adler || !signature.SequenceEqual(sha.GetHashAndReset()))
        {
            throw new InvalidDataException("Restored DEX does not match its original SHA-1 and Adler32.");
        }
    }

    /// <summary>
    /// Recomputes and stores the DEX signature and header checksum. The Adler-32 fold starts at the seed <c>1</c> and
    /// covers every byte from offset 12 on, chunk by chunk through <c>UotanStudio.Hash.Adler32.Adler32.Compute</c>;
    /// folding chunk-wise from <c>1u</c> equals a single
    /// <c>UotanStudio.Hash.Adler32.Adler32.Compute(data.AsSpan(12))</c> call over the whole range, so the packed value
    /// written at offset 8 keeps the on-disk ODEX/DEX header checksum byte-identical.
    /// <para>重新计算并写入 DEX 的签名与头部校验和。Adler-32 以种子 <c>1</c> 起步，经
    /// <c>UotanStudio.Hash.Adler32.Adler32.Compute</c> 分块折叠偏移 12 起的每个字节；从 <c>1u</c> 开始的逐块折叠与对
    /// <c>data.AsSpan(12)</c> 完整区间调用一次 <c>UotanStudio.Hash.Adler32.Adler32.Compute</c> 相等，因此写入偏移 8
    /// 的打包值使落盘的 ODEX/DEX 头部校验和保持逐字节一致。</para>
    /// </summary>
    internal void WriteDexHashes()
    {
        using var sha = Sha1.CreateAccumulator();
        var buffer = new byte[65536];
        for (long offset = 32; offset < length;)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            Read(offset, buffer.AsSpan(0, count));
            sha.Append(buffer.AsSpan(0, count));
            offset += count;
        }
        stream.Position = origin + 12;
        stream.Write(sha.GetHashAndReset());
        var adler = 1u;
        for (long offset = 12; offset < length;)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            Read(offset, buffer.AsSpan(0, count));
            adler = Hash.Adler32.Adler32.Compute(buffer.AsSpan(0, count), adler);
            offset += count;
        }
        Write32(8, adler);
    }
}