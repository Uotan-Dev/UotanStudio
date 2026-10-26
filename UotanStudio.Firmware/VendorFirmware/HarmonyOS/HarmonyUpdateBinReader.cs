using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.VendorFirmware.HarmonyOS;

/// <summary>
/// Bounded reader for the HarmonyOS <c>update.bin</c> container: it validates the 180-byte header, the component
/// table, the reserved field and the metadata TLVs, and resolves every component to its payload range.
/// <para>HarmonyOS <c>update.bin</c> 容器的有界读取器：校验 180 字节头部、组件表、保留字段与元数据 TLV，
/// 并把每个组件解析到其载荷区间。</para>
/// </summary>
internal static class HarmonyUpdateBinReader
{
    /// <summary>Fixed header length that precedes the component table.
    /// <para>组件表之前的固定头部长度。</para></summary>
    internal const int HeaderLength = 180;

    /// <summary>Offset of the component-table length inside the fixed header.
    /// <para>固定头部内组件表长度所在的偏移。</para></summary>
    internal const int ComponentTableLengthOffset = 178;

    /// <summary>Reserved field length between the component table and the metadata TLVs.
    /// <para>组件表与元数据 TLV 之间的保留字段长度。</para></summary>
    internal const int ReserveLength = 16;

    /// <summary>Component-name field width of an L1 record.
    /// <para>L1 记录的组件名字段宽度。</para></summary>
    internal const int L1NameLength = 16;

    /// <summary>Component-name field width of an L2 record.
    /// <para>L2 记录的组件名字段宽度。</para></summary>
    internal const int L2NameLength = 32;

    /// <summary>Byte width of an L1 component record.
    /// <para>L1 组件记录的字节宽度。</para></summary>
    internal const int L1RecordLength = 71;

    /// <summary>Byte width of an L2 component record.
    /// <para>L2 组件记录的字节宽度。</para></summary>
    internal const int L2RecordLength = 87;

    /// <summary>Offset of the component type inside a record, relative to the name-field end.
    /// <para>记录内组件类型相对名称字段末尾的偏移。</para></summary>
    private const int ComponentTypeOffset = 4;

    /// <summary>Offset of the declared payload length inside a record, relative to the name-field end.
    /// <para>记录内声明载荷长度相对名称字段末尾的偏移。</para></summary>
    private const int SizeOffset = 15;

    /// <summary>Offset of the recorded digest inside a record, relative to the name-field end.
    /// <para>记录内已记录摘要相对名称字段末尾的偏移。</para></summary>
    private const int DigestOffset = 23;

    /// <summary>Recorded digest width in bytes.
    /// <para>已记录摘要的字节宽度。</para></summary>
    private const int DigestLength = 32;

    /// <summary>Upper bound on the component count that keeps a hostile table from consuming unbounded memory.
    /// <para>组件数量上限，避免恶意组件表占用无界内存。</para></summary>
    internal const int MaximumComponents = 4096;

    /// <summary>Upper bound on one metadata TLV payload.
    /// <para>单个元数据 TLV 载荷上限。</para></summary>
    internal const long MaximumMetadataLength = 512L * 1024 * 1024;

    /// <summary>Upper bound on a package-version component that is decoded as text.
    /// <para>作为文本解码的包版本组件上限。</para></summary>
    internal const int MaximumVersionBytes = 64 * 1024;

    /// <summary>Upper bound on one component file name.
    /// <para>单个组件文件名上限。</para></summary>
    private const int MaximumNameLength = 255;

    /// <summary>Smallest file length that can hold the header, one L1 record, the reserved field and one TLV header.
    /// <para>可容纳头部、一条 L1 记录、保留字段与一个 TLV 头部的最小文件长度。</para></summary>
    internal const int MinimumFileLength = HeaderLength + ReserveLength + L1RecordLength + 6;

    /// <summary>Layout hint stored at the start of the header for an L2 table.
    /// <para>头部起始处表示 L2 表的布局提示。</para></summary>
    private const ushort L2LayoutHint = 0x01;

    /// <summary>Layout hint stored at the start of the header for an L1 table.
    /// <para>头部起始处表示 L1 表的布局提示。</para></summary>
    private const ushort L1LayoutHint = 0x11;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Parses the package and throws when any structure, bound or name check fails.
    /// <para>解析包，并在任何结构、边界或名称校验失败时抛出异常。</para>
    /// </summary>
    /// <param name="stream">Readable, seekable container stream; it is positioned but not disposed.
    /// <para>可读且可定位的容器流；本方法会改变其位置但不释放它。</para></param>
    /// <param name="fileLength">Container length in bytes; ranges are validated against it.
    /// <para>容器字节长度；所有区间都以它为界进行校验。</para></param>
    /// <param name="cancellationToken">Checked before parsing and between table records.
    /// <para>在解析前与记录之间检查。</para></param>
    /// <returns>The parsed package index.<para>解析出的包索引。</para></returns>
    /// <exception cref="InvalidDataException">The header, component table, reserved field, metadata TLVs or payload ranges are invalid.
    /// <para>头部、组件表、保留字段、元数据 TLV 或载荷区间无效。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    internal static HarmonyUpdateBinPackage Read(Stream stream, long fileLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (fileLength < MinimumFileLength)
        {
            throw new InvalidDataException(
                $"The HarmonyOS update.bin is {fileLength} bytes, below the {MinimumFileLength}-byte minimum.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var header = new byte[HeaderLength];
        stream.Position = 0;
        stream.ReadExactly(header);
        var tableLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(ComponentTableLengthOffset));
        if (tableLength == 0)
        {
            throw new InvalidDataException("The HarmonyOS update.bin component table is empty.");
        }

        var reserveOffset = HeaderLength + tableLength;
        if (reserveOffset > fileLength - ReserveLength)
        {
            throw new InvalidDataException(
                $"The HarmonyOS update.bin component table ({tableLength} bytes) exceeds the container length.");
        }

        var table = new byte[tableLength];
        stream.Position = HeaderLength;
        stream.ReadExactly(table);
        stream.Position = reserveOffset;
        Span<byte> reserve = stackalloc byte[ReserveLength];
        stream.ReadExactly(reserve);

        var dataOffset = SkipMetadata(stream, reserveOffset + ReserveLength, fileLength, out var signatureKind);
        var hint = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var candidates = hint switch
        {
            L2LayoutHint => (First: HarmonyUpdateBinLayout.L2, Second: HarmonyUpdateBinLayout.L1),
            L1LayoutHint => (First: HarmonyUpdateBinLayout.L1, Second: HarmonyUpdateBinLayout.L2),
            _ => (First: HarmonyUpdateBinLayout.L2, Second: HarmonyUpdateBinLayout.L1)
        };

        var failures = new List<string>(2);
        foreach (var layout in new[] { candidates.First, candidates.Second })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseComponentTable(table, layout, cancellationToken, out var parsed, out var error))
            {
                failures.Add($"{layout}: {error}");
                continue;
            }

            var payloadLength = 0L;
            try
            {
                foreach (var component in parsed)
                {
                    payloadLength = checked(payloadLength + component.Size);
                }
            }
            catch (OverflowException)
            {
                failures.Add($"{layout}: the declared payload length overflows a 64-bit offset");
                continue;
            }

            if (payloadLength > fileLength - dataOffset)
            {
                failures.Add(
                    $"{layout}: the declared {payloadLength}-byte payload does not fit after offset {dataOffset}");
                continue;
            }

            var components = new HarmonyUpdateBinComponent[parsed.Count];
            var offset = dataOffset;
            for (var index = 0; index < parsed.Count; index++)
            {
                var component = parsed[index];
                components[index] = new HarmonyUpdateBinComponent
                {
                    Name = component.Name,
                    OutputName = component.OutputName,
                    ComponentType = component.ComponentType,
                    Size = component.Size,
                    DataOffset = offset,
                    Sha256 = component.Sha256
                };
                offset = checked(offset + component.Size);
            }

            return new HarmonyUpdateBinPackage
            {
                Layout = layout,
                Components = components,
                DataOffset = dataOffset,
                PayloadLength = payloadLength,
                SignatureKind = signatureKind
            };
        }

        throw new InvalidDataException(
            "The HarmonyOS update.bin component table does not match an L1 or L2 layout "
            + $"({string.Join("; ", failures)}).");
    }

    /// <summary>Attempts to parse the package and reports whether the structure is accepted.
    /// <para>尝试解析包并报告结构是否被接受。</para>
    /// </summary>
    /// <param name="stream">Readable, seekable container stream; the position is not restored.
    /// <para>可读且可定位的容器流；不会恢复其位置。</para></param>
    /// <param name="fileLength">Container length in bytes.
    /// <para>容器字节长度。</para></param>
    /// <param name="requireLayoutHint">When <see langword="true"/>, the first header word must be the L1 or L2 hint.
    /// Content detection passes <see langword="true"/> so an arbitrary binary cannot be claimed by structure alone.
    /// <para>为 <see langword="true"/> 时，头部首个字必须是 L1 或 L2 提示。内容探测传入 <see langword="true"/>，
    /// 避免任意二进制仅凭结构被认领。</para></param>
    /// <param name="package">The parsed package when the method returns <see langword="true"/>; otherwise <see langword="null"/>.
    /// <para>方法返回 <see langword="true"/> 时解析出的包；否则为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the container is a supported update.bin package.
    /// <para>容器为受支持的 update.bin 包时返回 <see langword="true"/>。</para></returns>
    internal static bool TryRead(Stream stream, long fileLength, bool requireLayoutHint,
        out HarmonyUpdateBinPackage? package)
    {
        package = null;
        if (fileLength < MinimumFileLength)
        {
            return false;
        }

        try
        {
            if (requireLayoutHint)
            {
                Span<byte> hintBytes = stackalloc byte[2];
                stream.Position = 0;
                if (stream.Read(hintBytes) != hintBytes.Length)
                {
                    return false;
                }

                var hint = BinaryPrimitives.ReadUInt16LittleEndian(hintBytes);
                if (hint is not L1LayoutHint and not L2LayoutHint)
                {
                    return false;
                }
            }

            package = Read(stream, fileLength);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException
            or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Reads the package version recorded by a <c>VERSION.mbn</c> or <c>SOFTWARE_VER_LIST.mbn</c> component.
    /// <para>读取由 <c>VERSION.mbn</c> 或 <c>SOFTWARE_VER_LIST.mbn</c> 组件记录的包版本。</para>
    /// </summary>
    /// <param name="stream">Container stream the package was parsed from; it is positioned but not disposed.
    /// <para>解析该包所用的容器流；本方法会改变其位置但不释放它。</para></param>
    /// <param name="package">Parsed package whose components are searched.
    /// <para>要搜索其组件的已解析包。</para></param>
    /// <param name="cancellationToken">Checked before the component is read.
    /// <para>读取组件前检查。</para></param>
    /// <returns>The decoded version text, or <see langword="null"/> when no version component exists or it is empty.
    /// <para>解码后的版本文本；不存在版本组件或内容为空时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    internal static string? ReadPackageVersion(Stream stream, HarmonyUpdateBinPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(package);
        HarmonyUpdateBinComponent? versionComponent = null;
        foreach (var component in package.Components)
        {
            if (component.OutputName.Equals("VERSION.mbn", StringComparison.OrdinalIgnoreCase)
                || component.OutputName.Equals("SOFTWARE_VER_LIST.mbn", StringComparison.OrdinalIgnoreCase)
                || component.Name.Equals("version_list", StringComparison.OrdinalIgnoreCase)
                || component.Name.Equals("software_ver_list", StringComparison.OrdinalIgnoreCase))
            {
                versionComponent = component;
                break;
            }
        }

        if (versionComponent is null || versionComponent.Size is <= 0 or > MaximumVersionBytes)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = new byte[checked((int)versionComponent.Size)];
        stream.Position = versionComponent.DataOffset;
        stream.ReadExactly(bytes);
        return NormalizeVersion(Encoding.UTF8.GetString(bytes));
    }

    private static bool TryParseComponentTable(ReadOnlySpan<byte> table, HarmonyUpdateBinLayout layout,
        CancellationToken cancellationToken, out List<HarmonyUpdateBinComponent> components, out string error)
    {
        components = [];
        var nameLength = NameLength(layout);
        var recordLength = RecordLength(layout);
        if (table.Length % recordLength != 0)
        {
            error = $"the component table length {table.Length} is not divisible by the {recordLength}-byte record";
            return false;
        }

        var count = table.Length / recordLength;
        if (count is < 1 or > MaximumComponents)
        {
            error = $"the component count {count} is outside 1..{MaximumComponents}";
            return false;
        }

        var parsed = new List<HarmonyUpdateBinComponent>(count);
        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            if ((index & 0x3F) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var record = table.Slice(index * recordLength, recordLength);
            var rawName = record[..nameLength];
            var terminator = rawName.IndexOf((byte)0);
            if (terminator >= 0)
            {
                rawName = rawName[..terminator];
            }

            string name;
            try
            {
                name = StrictUtf8.GetString(rawName).Trim('/');
            }
            catch (DecoderFallbackException)
            {
                error = $"the component name at record {index} is not valid UTF-8";
                return false;
            }

            if (!IsSafeName(name))
            {
                error = $"the component name at record {index} is not a safe file name";
                return false;
            }

            var componentType = record[nameLength + ComponentTypeOffset];
            var size = BinaryPrimitives.ReadUInt64LittleEndian(
                record.Slice(nameLength + SizeOffset, sizeof(ulong)));
            if (size > long.MaxValue)
            {
                error = $"the component '{name}' declares a payload length above the 64-bit offset range";
                return false;
            }

            var outputName = GetOutputName(name, componentType);
            if (!outputNames.Add(outputName))
            {
                error = $"the component output name '{outputName}' is duplicated";
                return false;
            }

            parsed.Add(new HarmonyUpdateBinComponent
            {
                Name = name,
                OutputName = outputName,
                ComponentType = componentType,
                Size = (long)size,
                DataOffset = 0,
                Sha256 = record.Slice(nameLength + DigestOffset, DigestLength).ToArray()
            });
        }

        components = parsed;
        error = string.Empty;
        return true;
    }

    private static long SkipMetadata(Stream stream, long offset, long fileLength,
        out HarmonyUpdateBinSignatureKind signatureKind)
    {
        var position = offset;
        var (kind, length) = ReadTlvHeader(stream, ref position, fileLength);
        switch (kind)
        {
            case 0x06:
                {
                    position += length;
                    var (hashKind, hashLength) = ReadTlvHeader(stream, ref position, fileLength);
                    if (hashKind != 0x07)
                    {
                        throw new InvalidDataException(
                            $"The HarmonyOS update.bin expects a hash-data TLV (0x07) after the hash-info TLV, found 0x{hashKind:X2}.");
                    }

                    position += hashLength;
                    var (signatureKindValue, signatureLength) = ReadTlvHeader(stream, ref position, fileLength);
                    if (signatureKindValue != 0x08)
                    {
                        throw new InvalidDataException(
                            $"The HarmonyOS update.bin expects a signature TLV (0x08) after the hash-data TLV, found 0x{signatureKindValue:X2}.");
                    }

                    position += signatureLength;
                    signatureKind = HarmonyUpdateBinSignatureKind.HashAndSignature;
                    return position;
                }

            case 0x08:
                position += length;
                signatureKind = HarmonyUpdateBinSignatureKind.Signature;
                return position;

            default:
                throw new InvalidDataException(
                    $"The HarmonyOS update.bin metadata TLV 0x{kind:X2} is not supported.");
        }
    }

    private static (ushort Kind, uint Length) ReadTlvHeader(Stream stream, ref long position, long fileLength)
    {
        if (fileLength - position < 6)
        {
            throw new InvalidDataException("The HarmonyOS update.bin metadata TLV header is truncated.");
        }

        Span<byte> header = stackalloc byte[6];
        stream.Position = position;
        stream.ReadExactly(header);
        var kind = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header[2..]);
        position += 6;
        if (length > MaximumMetadataLength)
        {
            throw new InvalidDataException(
                $"The HarmonyOS update.bin metadata TLV 0x{kind:X2} declares {length} bytes, above the {MaximumMetadataLength}-byte bound.");
        }

        if ((long)length > fileLength - position)
        {
            throw new InvalidDataException(
                $"The HarmonyOS update.bin metadata TLV 0x{kind:X2} exceeds the container length.");
        }

        return (kind, length);
    }

    private static int NameLength(HarmonyUpdateBinLayout layout)
        => layout == HarmonyUpdateBinLayout.L1 ? L1NameLength : L2NameLength;

    private static int RecordLength(HarmonyUpdateBinLayout layout)
        => layout == HarmonyUpdateBinLayout.L1 ? L1RecordLength : L2RecordLength;

    private static string GetOutputName(string name, byte componentType) => name switch
    {
        "version_list" => "VERSION.mbn",
        "software_ver_list" => "SOFTWARE_VER_LIST.mbn",
        "board_list" => "BOARD.list",
        _ when componentType == 0 => name + ".img",
        _ when componentType == 1 => name + ".zip",
        _ => name
    };

    private static bool IsSafeName(string name)
    {
        if (name.Length is 0 or > MaximumNameLength || name is "." or "..")
        {
            return false;
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (char.IsControl(character)
                || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                return false;
            }
        }

        return true;
    }

    private static string? NormalizeVersion(string text)
    {
        var trimmed = text.AsSpan().TrimStart('\uFEFF');
        var builder = new StringBuilder(trimmed.Length);
        var pendingSpace = false;
        foreach (var character in trimmed)
        {
            if (character == '\0' || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}