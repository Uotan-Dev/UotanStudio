using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BootSecurity.Kernel;

/// <summary>
/// Layout evidence found while decoding a compressed binary kallsyms table.
/// <para>解码压缩的二进制 kallsyms 表时发现的布局证据。</para>
/// </summary>
/// <param name="SymbolCount">Number of decoded symbols. <para>解码得到的符号数量。</para></param>
/// <param name="IsBigEndian"><see langword="true"/> when the token index matched only in big-endian byte order. <para>token 索引仅以大端字节序匹配时为 <see langword="true"/>。</para></param>
/// <param name="AddressSize">Width in bytes (<c>2</c>, <c>4</c> or <c>8</c>) of the marker and absolute address fields. <para>marker 与绝对地址字段的字节宽度（<c>2</c>、<c>4</c> 或 <c>8</c>）。</para></param>
/// <param name="IsBaseRelative"><see langword="true"/> when addresses were rebuilt from 32-bit offsets against a base value; <see langword="false"/> for the absolute address table. <para>地址由相对基址的 32 位偏移还原时为 <see langword="true"/>；绝对地址表为 <see langword="false"/>。</para></param>
/// <param name="RelativeBase">Base address used in relative mode; <see langword="null"/> for absolute addresses. <para>相对模式使用的基址；绝对地址表为 <see langword="null"/>。</para></param>
/// <param name="AddressesOffset">Offset where the address table starts inside the kernel image. <para>地址表在内核镜像内的起始偏移。</para></param>
/// <param name="NamesOffset">Offset where the token-encoded name stream starts. <para>token 编码名称流的起始偏移。</para></param>
/// <param name="MarkersOffset">Offset of the marker value table. <para>marker 值表的偏移。</para></param>
/// <param name="TokenTableOffset">Four-aligned offset of the 256-entry token table. <para>256 项 token 表的 4 字节对齐偏移。</para></param>
/// <param name="TokenIndexOffset">Offset of the 16-bit token index array whose byte order fixes the table endianness. <para>16 位 token 索引数组的偏移，其字节序决定全表端序。</para></param>
public sealed record KallsymsBinaryMetadata(
    int SymbolCount,
    bool IsBigEndian,
    int AddressSize,
    bool IsBaseRelative,
    ulong? RelativeBase,
    int AddressesOffset,
    int NamesOffset,
    int MarkersOffset,
    int TokenTableOffset,
    int TokenIndexOffset);

/// <summary>
/// Symbols and structural evidence decoded from a binary kernel image.
/// <para>从二进制内核镜像解码得到的符号与结构证据。</para>
/// </summary>
/// <param name="Symbols">Decoded symbols in name-stream order, each pairing an address with a type character and name. <para>按名称流顺序解码的符号，每项包含地址、类型字符与名称。</para></param>
/// <param name="Metadata">Layout evidence recorded while decoding. <para>解码过程中记录的布局证据。</para></param>
public sealed record KallsymsBinaryResult(
    IReadOnlyList<KernelSymbol> Symbols,
    KallsymsBinaryMetadata Metadata);

internal static class KallsymsBinaryReader
{
    private const int MaximumSymbols = 4_000_000;
    private const int MaximumTokenLength = 50;
    private const int MaximumNameSearchBytes = 4 * 1024 * 1024;

    internal static KallsymsBinaryResult Read(ReadOnlySpan<byte> kernel)
    {
        var tokenTableOffset = FindTokenTable(kernel);
        var (tokens, tokenOffsets, tokenTableEnd) = ReadTokens(kernel, tokenTableOffset);
        var (tokenIndexOffset, bigEndian) = FindTokenIndex(kernel, tokenTableEnd, tokenOffsets);
        var (markersOffset, markerSize, markerValues) = FindMarkers(kernel, tokenTableOffset, bigEndian);
        var names = FindAndDecodeNames(kernel, markersOffset, markerSize, markerValues, tokens, bigEndian);
        var addresses = FindAddresses(kernel, names.CountOffset, names.Symbols.Count, markerSize, bigEndian);

        var symbols = new KernelSymbol[names.Symbols.Count];
        for (var index = 0; index < symbols.Length; index++)
        {
            var decoded = names.Symbols[index];
            symbols[index] = new(addresses.Addresses[index], decoded.Type, decoded.Name);
        }
        return new(
            symbols,
            new(
                symbols.Length,
                bigEndian,
                addresses.AddressSize,
                addresses.IsBaseRelative,
                addresses.RelativeBase,
                addresses.Offset,
                names.Offset,
                markersOffset,
                tokenTableOffset,
                tokenIndexOffset));
    }

    private static int FindTokenTable(ReadOnlySpan<byte> kernel)
    {
        using var sequenceBuffer = new MemoryStream();
        for (var value = (byte)'0'; value <= (byte)'9'; value++)
        {
            sequenceBuffer.WriteByte(value);
            sequenceBuffer.WriteByte(0);
        }
        var sequence = sequenceBuffer.ToArray();
        var candidates = new List<int>();
        var searchOffset = 0;
        while (searchOffset <= kernel.Length - sequence.Length)
        {
            var relative = kernel[searchOffset..].IndexOf(sequence);
            if (relative < 0)
            {
                break;
            }
            var digitsOffset = checked(searchOffset + relative);
            var position = digitsOffset - 1;
            if (position >= 0 && kernel[position] == 0)
            {
                var valid = true;
                for (var token = 0; token < '0'; token++)
                {
                    position--;
                    var length = 0;
                    while (position >= 0 && kernel[position] != 0 && kernel[position] <= 'z')
                    {
                        position--;
                        if (++length == MaximumTokenLength)
                        {
                            valid = false;
                            break;
                        }
                    }
                    if (!valid || position < 0)
                    {
                        valid = false;
                        break;
                    }
                }
                if (valid)
                {
                    candidates.Add(AlignUp(position + 1, 4));
                }
            }
            searchOffset = digitsOffset + 1;
        }
        var distinct = candidates.Distinct().ToArray();
        return distinct.Length == 1
            ? distinct[0]
            : throw new InvalidDataException($"Expected one kallsyms token table candidate, found {distinct.Length}.");
    }

    private static (string[] Tokens, ushort[] Offsets, int End) ReadTokens(ReadOnlySpan<byte> kernel, int offset)
    {
        var tokens = new string[256];
        var offsets = new ushort[256];
        var position = offset;
        for (var index = 0; index < tokens.Length; index++)
        {
            offsets[index] = checked((ushort)(position - offset));
            var end = position;
            while (end < kernel.Length && kernel[end] != 0 && end - position < MaximumTokenLength)
            {
                end++;
            }
            if (end >= kernel.Length || end - position == MaximumTokenLength)
            {
                throw new InvalidDataException("kallsyms token table is truncated or contains an oversized token.");
            }
            tokens[index] = Encoding.Latin1.GetString(kernel[position..end]);
            position = end + 1;
        }
        return (tokens, offsets, position);
    }

    private static (int Offset, bool BigEndian) FindTokenIndex(
        ReadOnlySpan<byte> kernel,
        int tokenTableEnd,
        IReadOnlyList<ushort> offsets)
    {
        var little = new byte[512];
        var big = new byte[512];
        for (var index = 0; index < offsets.Count; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(little.AsSpan(index * 2), offsets[index]);
            BinaryPrimitives.WriteUInt16BigEndian(big.AsSpan(index * 2), offsets[index]);
        }
        var search = kernel.Slice(tokenTableEnd, Math.Min(kernel.Length - tokenTableEnd, 512 + 256));
        var littleOffset = search.IndexOf(little);
        var bigOffset = search.IndexOf(big);
        if (littleOffset < 0 == (bigOffset < 0))
        {
            throw new InvalidDataException("kallsyms token index is missing or has ambiguous endianness.");
        }
        return littleOffset >= 0
            ? (tokenTableEnd + littleOffset, false)
            : (tokenTableEnd + bigOffset, true);
    }

    private static (int Offset, int ElementSize, ulong[] Values) FindMarkers(
        ReadOnlySpan<byte> kernel,
        int tokenTableOffset,
        bool bigEndian)
    {
        foreach (var size in new[] { 8, 4, 2 })
        {
            var lower = Math.Max(0, tokenTableOffset - MaximumNameSearchBytes);
            for (var position = tokenTableOffset - size * 4; position >= lower; position -= size)
            {
                if (position % size != 0 || ReadUnsigned(kernel, position, size, bigEndian) != 0)
                {
                    continue;
                }
                List<ulong> values = [0];
                var cursor = position + size;
                while (cursor + size <= tokenTableOffset)
                {
                    var value = ReadUnsigned(kernel, cursor, size, bigEndian);
                    var previous = values[^1];
                    if (value <= previous + 0x200 || value > previous + 0x40000)
                    {
                        break;
                    }
                    values.Add(value);
                    cursor += size;
                }
                if (values.Count >= 4)
                {
                    return (position, size, [.. values]);
                }
            }
        }
        throw new InvalidDataException("kallsyms markers were not found.");
    }

    private static (int Offset, int CountOffset, List<DecodedName> Symbols) FindAndDecodeNames(
        ReadOnlySpan<byte> kernel,
        int markersOffset,
        int markerSize,
        IReadOnlyList<ulong> markers,
        IReadOnlyList<string> tokens,
        bool bigEndian)
    {
        var estimate = checked(markersOffset - (int)markers[^1]);
        var minimumCount = checked((markers.Count - 1) * 256);
        for (var offset = AlignUp(estimate, markerSize); offset >= Math.Max(0, estimate - MaximumNameSearchBytes); offset -= 4)
        {
            if (!TryDecodeNames(kernel, offset, markersOffset, tokens, minimumCount, out var names))
            {
                continue;
            }
            var encodedCount = EncodeUnsigned(checked((ulong)names.Count), markerSize, bigEndian);
            var countSearchStart = Math.Max(0, offset - 256 - markerSize);
            var relative = kernel[countSearchStart..offset].LastIndexOf(encodedCount);
            if (relative >= 0)
            {
                return (offset, countSearchStart + relative, names);
            }
        }
        throw new InvalidDataException("kallsyms names or symbol count were not found.");
    }

    private static bool TryDecodeNames(
        ReadOnlySpan<byte> kernel,
        int offset,
        int end,
        IReadOnlyList<string> tokens,
        int minimumCount,
        out List<DecodedName> names)
    {
        names = [];
        var position = offset;
        while (position < end)
        {
            var length = kernel[position++];
            if (length == 0)
            {
                return names.Count >= minimumCount && kernel[position..end].IndexOfAnyExcept((byte)0) < 0;
            }
            var encodedLength = (int)length;
            if ((length & 0x80) != 0)
            {
                if (position >= end)
                {
                    return false;
                }
                encodedLength = (length & 0x7F) | kernel[position++] << 7;
            }
            if (encodedLength <= 0 || encodedLength > 0x3FFF || position > end - encodedLength)
            {
                return false;
            }
            var text = new StringBuilder();
            for (var index = 0; index < encodedLength; index++)
            {
                text.Append(tokens[kernel[position + index]]);
                if (text.Length > 4096)
                {
                    return false;
                }
            }
            position += encodedLength;
            if (text.Length < 2 || !char.IsAsciiLetter(text[0]) || text.ToString().IndexOf('\0') >= 0)
            {
                return false;
            }
            names.Add(new(text[0], text.ToString(1, text.Length - 1)));
            if (names.Count > MaximumSymbols)
            {
                throw new InvalidDataException("kallsyms contains too many symbols.");
            }
        }
        return names.Count >= minimumCount;
    }

    private static AddressResult FindAddresses(
        ReadOnlySpan<byte> kernel,
        int countOffset,
        int count,
        int addressSize,
        bool bigEndian)
    {
        var cursor = countOffset;
        while (cursor >= addressSize && ReadUnsigned(kernel, cursor - addressSize, addressSize, bigEndian) == 0)
        {
            cursor -= addressSize;
        }

        AddressResult? best = null;
        if (cursor >= addressSize + count * 4)
        {
            var baseOffset = cursor - addressSize;
            var relativeBase = ReadUnsigned(kernel, baseOffset, addressSize, bigEndian);
            var offsetsStart = baseOffset - count * 4;
            var addresses = new ulong[count];
            for (var index = 0; index < count; index++)
            {
                var relative = ReadSigned32(kernel, offsetsStart + index * 4, bigEndian);
                addresses[index] = unchecked((ulong)((long)relativeBase + relative));
            }
            best = new(offsetsStart, addressSize, true, relativeBase, addresses, Score(addresses));
        }
        if (cursor >= count * addressSize)
        {
            var absoluteStart = cursor - count * addressSize;
            var addresses = new ulong[count];
            for (var index = 0; index < count; index++)
            {
                addresses[index] = ReadUnsigned(kernel, absoluteStart + index * addressSize, addressSize, bigEndian);
            }
            var absolute = new AddressResult(absoluteStart, addressSize, false, null, addresses, Score(addresses));
            if (best is null || absolute.Score > best.Score)
            {
                best = absolute;
            }
        }
        if (best is null || best.Score < 0.75)
        {
            throw new InvalidDataException("kallsyms address table was not found with sufficient confidence.");
        }
        return best;
    }

    private static double Score(IReadOnlyList<ulong> addresses)
    {
        if (addresses.Count < 2)
        {
            return 0;
        }
        var plausible = 0;
        for (var index = 1; index < addresses.Count; index++)
        {
            if (addresses[index] >= addresses[index - 1] && addresses[index] != 0)
            {
                plausible++;
            }
        }
        return (double)plausible / (addresses.Count - 1);
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> data, int offset, int size, bool bigEndian)
    {
        if (offset < 0 || offset > data.Length - size)
        {
            throw new InvalidDataException("kallsyms numeric field exceeds the kernel image.");
        }
        var value = data.Slice(offset, size);
        return (size, bigEndian) switch
        {
            (2, false) => BinaryPrimitives.ReadUInt16LittleEndian(value),
            (2, true) => BinaryPrimitives.ReadUInt16BigEndian(value),
            (4, false) => BinaryPrimitives.ReadUInt32LittleEndian(value),
            (4, true) => BinaryPrimitives.ReadUInt32BigEndian(value),
            (8, false) => BinaryPrimitives.ReadUInt64LittleEndian(value),
            (8, true) => BinaryPrimitives.ReadUInt64BigEndian(value),
            _ => throw new InvalidDataException("Unsupported kallsyms integer width.")
        };
    }

    private static int ReadSigned32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));

    private static byte[] EncodeUnsigned(ulong value, int size, bool bigEndian)
    {
        var data = new byte[size];
        if (size == 2)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(data, checked((ushort)value));
            else BinaryPrimitives.WriteUInt16LittleEndian(data, checked((ushort)value));
        }
        else if (size == 4)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(data, checked((uint)value));
            else BinaryPrimitives.WriteUInt32LittleEndian(data, checked((uint)value));
        }
        else if (size == 8)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(data, value);
            else BinaryPrimitives.WriteUInt64LittleEndian(data, value);
        }
        return data;
    }

    private static int AlignUp(int value, int alignment) => checked((value + alignment - 1) & -alignment);

    private sealed record DecodedName(char Type, string Name);

    private sealed record AddressResult(
        int Offset,
        int AddressSize,
        bool IsBaseRelative,
        ulong? RelativeBase,
        ulong[] Addresses,
        double Score);
}