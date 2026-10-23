using System.Buffers.Binary;

namespace UotanStudio.Compress.Codecs.Xz;

/// <summary>
/// Inverse transform of one non-LZMA2 XZ block filter. Each instance is used by exactly one block and one thread.
/// <para>单个非 LZMA2 XZ 块过滤器的逆变换。每个实例只服务一个块和一个线程。</para>
/// </summary>
internal abstract class XzBlockFilter
{
    /// <summary>Filter identifier declared by the block header.
    /// <para>块头声明的过滤器标识。</para>
    /// </summary>
    internal abstract ulong Id { get; }

    /// <summary>
    /// Applies the inverse filter to the leading bytes of <paramref name="buffer"/>.
    /// <para>对 <paramref name="buffer"/> 的前缀字节应用逆过滤。</para>
    /// </summary>
    /// <param name="buffer">Decoded block bytes; the filter rewrites them in place.
    /// <para>已解码的块字节；过滤器就地改写。</para></param>
    /// <param name="length">Number of valid bytes at the start of <paramref name="buffer"/>.
    /// <para><paramref name="buffer"/> 起始处有效字节数。</para></param>
    /// <param name="position">Absolute offset of <paramref name="buffer"/>[0] inside the block's uncompressed data; used by branch filters to compute converted targets.
    /// <para><paramref name="buffer"/>[0] 在块未压缩数据内的绝对偏移；分支过滤器据此计算转换目标。</para></param>
    /// <returns>
    /// The number of bytes that are final. The remaining tail bytes are not final yet and must be presented again at the front of the next chunk.
    /// <para>已定稿的字节数。剩余尾部字节尚未定稿，必须放到下一块数据的前端再次呈现。</para>
    /// </returns>
    internal abstract int Transform(Span<byte> buffer, int length, long position);

    /// <summary>
    /// Creates the inverse filter for a declared identifier and its property bytes.
    /// <para>为声明的标识与属性字节创建逆过滤器。</para>
    /// </summary>
    /// <exception cref="CompressionException">The identifier is not a branch or delta filter, or its properties are invalid.
    /// <para>该标识不是分支或差分过滤器，或其属性非法。</para></exception>
    internal static XzBlockFilter Create(ulong id, ReadOnlySpan<byte> properties) => id switch
    {
        0x03 => CreateDelta(properties),
        0x04 => CreateX86(properties),
        0x05 => CreatePowerPc(properties),
        0x06 => CreateIa64(properties),
        0x07 => CreateArm(properties),
        0x08 => CreateArmThumb(properties),
        0x09 => CreateSparc(properties),
        _ => throw new CompressionException($"Unsupported XZ block filter: {id}.")
    };

    private static XzBlockFilter CreateDelta(ReadOnlySpan<byte> properties)
    {
        if (properties.Length != 1)
        {
            throw new InvalidDataException("The XZ delta filter must carry exactly one property byte.");
        }

        return new XzDeltaFilter(properties[0] + 1);
    }

    private static XzBlockFilter CreateX86(ReadOnlySpan<byte> properties)
    {
        RequireEmptyProperties(properties, "x86");
        return new XzX86Filter();
    }

    private static XzBlockFilter CreatePowerPc(ReadOnlySpan<byte> properties)
    {
        RequireEmptyProperties(properties, "PowerPC");
        return new XzPowerPcFilter();
    }

    private static XzBlockFilter CreateIa64(ReadOnlySpan<byte> properties)
    {
        RequireEmptyProperties(properties, "IA-64");
        return new XzIa64Filter();
    }

    private static XzBlockFilter CreateArm(ReadOnlySpan<byte> properties)
    {
        RequireEmptyProperties(properties, "ARM");
        return new XzArmFilter();
    }

    private static XzBlockFilter CreateArmThumb(ReadOnlySpan<byte> properties)
    {
        RequireEmptyProperties(properties, "ARM-Thumb");
        return new XzArmThumbFilter();
    }

    private static XzBlockFilter CreateSparc(ReadOnlySpan<byte> properties)
    {
        RequireEmptyProperties(properties, "SPARC");
        return new XzSparcFilter();
    }

    private static void RequireEmptyProperties(ReadOnlySpan<byte> properties, string name)
    {
        if (!properties.IsEmpty)
        {
            throw new InvalidDataException($"The XZ {name} branch filter does not accept properties.");
        }
    }
}

/// <summary>
/// Inverse delta filter: each byte is added to the byte one distance earlier.
/// <para>逆差分过滤器：每个字节加上距离一个步长之前的字节。</para>
/// </summary>
internal sealed class XzDeltaFilter : XzBlockFilter
{
    private readonly byte[] _history;
    private int _index;

    internal XzDeltaFilter(int distance)
    {
        _history = new byte[distance];
    }

    internal override ulong Id => 0x03;

    internal override int Transform(Span<byte> buffer, int length, long position)
    {
        for (var i = 0; i < length; i++)
        {
            var value = unchecked((byte)(buffer[i] + _history[_index]));
            buffer[i] = value;
            _history[_index] = value;
            _index++;
            if (_index == _history.Length)
            {
                _index = 0;
            }
        }

        return length;
    }
}

/// <summary>
/// Inverse ARM branch filter for 32-bit little-endian <c>BL</c> instructions.
/// <para>面向 32 位小端 <c>BL</c> 指令的逆 ARM 分支过滤器。</para>
/// </summary>
internal sealed class XzArmFilter : XzBlockFilter
{
    internal override ulong Id => 0x07;

    internal override int Transform(Span<byte> buffer, int length, long position)
    {
        var i = 0;
        var nowPosition = unchecked((uint)position);
        while (i + 4 <= length)
        {
            if (buffer[i + 3] == 0xEB)
            {
                var source = unchecked(((uint)buffer[i + 2] << 16) | ((uint)buffer[i + 1] << 8) | buffer[i]);
                source <<= 2;
                var destination = unchecked(source - (nowPosition + (uint)i + 8));
                destination >>= 2;
                buffer[i + 2] = unchecked((byte)(destination >> 16));
                buffer[i + 1] = unchecked((byte)(destination >> 8));
                buffer[i] = unchecked((byte)destination);
            }

            i += 4;
        }

        return i;
    }
}

/// <summary>
/// Inverse ARM-Thumb branch filter for the two half-word <c>BL</c> encoding.
/// <para>面向双半字 <c>BL</c> 编码的逆 ARM-Thumb 分支过滤器。</para>
/// </summary>
internal sealed class XzArmThumbFilter : XzBlockFilter
{
    internal override ulong Id => 0x08;

    internal override int Transform(Span<byte> buffer, int length, long position)
    {
        var i = 0;
        var nowPosition = unchecked((uint)position);
        while (i + 4 <= length)
        {
            if ((buffer[i + 1] & 0xF8) == 0xF0 && (buffer[i + 3] & 0xF8) == 0xF8)
            {
                var source = unchecked(((uint)(buffer[i + 1] & 0x07) << 19)
                    | ((uint)buffer[i] << 11)
                    | ((uint)(buffer[i + 3] & 0x07) << 8)
                    | buffer[i + 2]);
                source <<= 1;
                var destination = unchecked(source - (nowPosition + (uint)i + 4));
                destination >>= 1;
                buffer[i + 1] = unchecked((byte)(0xF0 | ((destination >> 19) & 0x07)));
                buffer[i] = unchecked((byte)((destination >> 11) & 0xFF));
                buffer[i + 3] = unchecked((byte)(0xF8 | ((destination >> 8) & 0x07)));
                buffer[i + 2] = unchecked((byte)(destination & 0xFF));
                i += 4;
            }
            else
            {
                i += 2;
            }
        }

        return i;
    }
}

/// <summary>
/// Inverse PowerPC branch filter for the big-endian <c>bl</c> encoding.
/// <para>面向大端 <c>bl</c> 编码的逆 PowerPC 分支过滤器。</para>
/// </summary>
internal sealed class XzPowerPcFilter : XzBlockFilter
{
    internal override ulong Id => 0x05;

    internal override int Transform(Span<byte> buffer, int length, long position)
    {
        var i = 0;
        var nowPosition = unchecked((uint)position);
        while (i + 4 <= length)
        {
            if ((buffer[i] >> 2) == 0x12 && (buffer[i + 3] & 0x03) == 0x01)
            {
                var source = unchecked(((uint)(buffer[i] & 0x03) << 24)
                    | ((uint)buffer[i + 1] << 16)
                    | ((uint)buffer[i + 2] << 8)
                    | ((uint)buffer[i + 3] & 0xFC));
                var destination = unchecked(source - (nowPosition + (uint)i));
                buffer[i] = unchecked((byte)(0x48 | ((destination >> 24) & 0x03)));
                buffer[i + 1] = unchecked((byte)(destination >> 16));
                buffer[i + 2] = unchecked((byte)(destination >> 8));
                buffer[i + 3] = unchecked((byte)((buffer[i + 3] & 0x03) | ((int)destination & 0xFC)));
            }

            i += 4;
        }

        return i;
    }
}

/// <summary>
/// Inverse SPARC branch filter for the big-endian <c>call</c> instruction.
/// <para>面向大端 <c>call</c> 指令的逆 SPARC 分支过滤器。</para>
/// </summary>
internal sealed class XzSparcFilter : XzBlockFilter
{
    internal override ulong Id => 0x09;

    internal override int Transform(Span<byte> buffer, int length, long position)
    {
        var i = 0;
        var nowPosition = unchecked((uint)position);
        while (i + 4 <= length)
        {
            if ((buffer[i] == 0x40 && (buffer[i + 1] & 0xC0) == 0x00)
                || (buffer[i] == 0x7F && (buffer[i + 1] & 0xC0) == 0xC0))
            {
                var source = unchecked(((uint)buffer[i] << 24)
                    | ((uint)buffer[i + 1] << 16)
                    | ((uint)buffer[i + 2] << 8)
                    | buffer[i + 3]);
                source <<= 2;
                var destination = unchecked(source - (nowPosition + (uint)i));
                destination >>= 2;
                destination = unchecked((0 - ((destination >> 22) & 1) << 22) & 0x3FFFFFFF)
                    | (destination & 0x3FFFFF)
                    | 0x40000000;
                buffer[i] = unchecked((byte)(destination >> 24));
                buffer[i + 1] = unchecked((byte)(destination >> 16));
                buffer[i + 2] = unchecked((byte)(destination >> 8));
                buffer[i + 3] = unchecked((byte)destination);
            }

            i += 4;
        }

        return i;
    }
}

/// <summary>
/// Inverse IA-64 branch filter for the three branch instruction slots of a 16-byte bundle.
/// <para>面向 16 字节指令束三个分支指令槽的逆 IA-64 分支过滤器。</para>
/// </summary>
internal sealed class XzIa64Filter : XzBlockFilter
{
    internal override ulong Id => 0x06;

    internal override int Transform(Span<byte> buffer, int length, long position)
    {
        var i = 0;
        var limit = length - 16;
        if (limit < 0)
        {
            return 0;
        }

        var nowPosition = unchecked((uint)position);
        while (i <= limit)
        {
            var mask = (860553216u >> (buffer[i] & 0x1E)) & 3u;
            if (mask != 0)
            {
                mask++;
                while (mask <= 4)
                {
                    var shift = (int)(mask & 31);
                    var bytePosition = i + (int)mask * 5 - 8;
                    if (((buffer[bytePosition + 3] >> shift) & 0x0F) == 0x05
                        && (((buffer[bytePosition - 1] | (buffer[bytePosition] << 8)) >> shift) & 0x70) == 0)
                    {
                        var instruction = BinaryPrimitives.ReadUInt32LittleEndian(buffer[bytePosition..]);
                        var normalized = instruction >> shift;
                        normalized = (normalized & 0xFFFFF) | ((normalized & 0x800000) >> 3);
                        normalized <<= 4;
                        normalized = unchecked(normalized - (nowPosition + (uint)i));
                        normalized >>= 4;
                        normalized &= 0x1FFFFF;
                        normalized += 0x700000;
                        normalized &= 0x8FFFFF;
                        instruction &= ~(0x8FFFFFu << shift);
                        instruction |= normalized << shift;
                        BinaryPrimitives.WriteUInt32LittleEndian(buffer[bytePosition..], instruction);
                    }

                    mask++;
                }
            }

            i += 16;
        }

        return i;
    }
}

/// <summary>
/// Inverse x86 branch filter, a port of the 7-Zip <c>x86_Convert</c> state machine with the inverse direction.
/// <para>逆 x86 分支过滤器：7-Zip <c>x86_Convert</c> 状态机的反向移植。</para>
/// </summary>
internal sealed class XzX86Filter : XzBlockFilter
{
    private static ReadOnlySpan<byte> MaskToAllowedStatus => [1, 1, 1, 0, 1, 0, 0, 0];

    private static ReadOnlySpan<byte> MaskToBitNumber => [0, 1, 2, 2, 3, 3, 3, 3];

    private uint _state;

    internal override ulong Id => 0x04;

    internal override int Transform(Span<byte> buffer, int length, long position)
    {
        if (length < 5)
        {
            return 0;
        }

        var allowed = MaskToAllowedStatus;
        var bitNumbers = MaskToBitNumber;
        var previousPosition = -1;
        var positionInBuffer = 0;
        var limit = length - 4;
        var ip = checked((uint)position);
        var mask = _state & 7;

        while (positionInBuffer < limit)
        {
            if ((buffer[positionInBuffer] & 0xFE) != 0xE8)
            {
                positionInBuffer++;
                continue;
            }

            previousPosition = positionInBuffer - previousPosition;
            if (previousPosition > 3)
            {
                mask = 0;
            }
            else
            {
                mask = (mask << ((previousPosition - 1) & 31)) & 7;
                if (mask != 0)
                {
                    var tested = buffer[positionInBuffer + 4 - bitNumbers[(int)mask]];
                    if (allowed[(int)mask] == 0 || TestByte(tested))
                    {
                        previousPosition = positionInBuffer;
                        mask = (mask << 1) | 1;
                        positionInBuffer++;
                        continue;
                    }
                }
            }

            previousPosition = positionInBuffer;
            if (!TestByte(buffer[positionInBuffer + 4]))
            {
                mask = (mask << 1) | 1;
                positionInBuffer++;
                continue;
            }

            var value = unchecked(((uint)buffer[positionInBuffer + 4] << 24)
                | ((uint)buffer[positionInBuffer + 3] << 16)
                | ((uint)buffer[positionInBuffer + 2] << 8)
                | buffer[positionInBuffer + 1]);
            uint destination;
            while (true)
            {
                destination = unchecked(value - (ip + (uint)positionInBuffer + 5));
                if (mask == 0)
                {
                    break;
                }

                var bitPosition = bitNumbers[(int)mask] * 8;
                var tested = unchecked((byte)(destination >> ((24 - bitPosition) & 31)));
                if (!TestByte(tested))
                {
                    break;
                }

                value = destination ^ ((1u << ((32 - bitPosition) & 31)) - 1);
            }

            destination &= 0x1FFFFFF;
            destination |= unchecked((uint)0 - (destination & 0x1000000));
            buffer[positionInBuffer + 1] = unchecked((byte)destination);
            buffer[positionInBuffer + 2] = unchecked((byte)(destination >> 8));
            buffer[positionInBuffer + 3] = unchecked((byte)(destination >> 16));
            buffer[positionInBuffer + 4] = unchecked((byte)(destination >> 24));
            positionInBuffer += 4;
            positionInBuffer++;
        }

        previousPosition = positionInBuffer - previousPosition;
        _state = previousPosition <= 3 ? mask << ((previousPosition - 1) & 31) : 0;
        return positionInBuffer;
    }

    private static bool TestByte(byte value) => value is 0x00 or 0xFF;
}