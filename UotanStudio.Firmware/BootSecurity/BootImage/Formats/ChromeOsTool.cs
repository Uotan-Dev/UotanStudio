using System.Buffers.Binary;

namespace UotanStudio.Firmware.BootSecurity.BootImage.Formats;

/// <summary>
/// Recognises ChromeOS kernel images by their leading ASCII magic.
/// <para>依据开头的 ASCII 魔数识别 ChromeOS 内核镜像。</para>
/// </summary>
internal static class ChromeOsTool
{
    private const int SignatureDescriptorSize = 24;
    private const int PackedKeyHeaderSize = 32;
    internal const int KeyblockHeaderSize = 112;
    internal const int MaximumPreambleHeaderSize = 116;

    /// <summary>
    /// Parses a complete vboot2 keyblock and validates every declared range before publishing metadata.
    /// <para>解析完整的 vboot2 keyblock，并在发布元数据前校验所有声明范围。</para>
    /// </summary>
    /// <param name="data">Bytes beginning at the keyblock magic and containing the declared keyblock.
    /// <para>从 keyblock 魔数开始、并包含声明的完整 keyblock 的字节。</para></param>
    /// <returns>Read-only keyblock metadata; key bytes and signatures remain opaque.
    /// <para>只读 keyblock 元数据；密钥字节和签名保持不透明。</para></returns>
    /// <exception cref="InvalidDataException">The header, declared size, or referenced range is invalid.
    /// <para>头部、声明长度或引用范围非法。</para></exception>
    public static ChromeOsKeyblockMetadata ParseKeyblock(ReadOnlySpan<byte> data)
        => ParseKeyblock(data, data.Length);

    /// <summary>
    /// Parses a keyblock header that may be a bounded prefix of a larger container: declared sizes are validated
    /// against <paramref name="containerLength"/> instead of the supplied span length.
    /// <para>解析可能只是更大容器有界前缀的 keyblock 头部：声明大小按 <paramref name="containerLength"/> 校验，
    /// 而不是按所提供切片的长度。</para>
    /// </summary>
    /// <param name="data">Prefix holding at least the keyblock header; only the fixed header fields are read.
    /// <para>至少包含 keyblock 头部的前缀；只读取定长头部字段。</para></param>
    /// <param name="containerLength">Total length of the container the prefix was read from.
    /// <para>前缀所在容器的总长度。</para></param>
    /// <returns>The validated keyblock metadata. <para>经过校验的 keyblock 元数据。</para></returns>
    /// <exception cref="InvalidDataException">The header, declared size, or referenced range is invalid.
    /// <para>头部、声明长度或引用范围非法。</para></exception>
    internal static ChromeOsKeyblockMetadata ParseKeyblock(ReadOnlySpan<byte> data, long containerLength)
    {
        if (data.Length < KeyblockHeaderSize || !data[..8].SequenceEqual("CHROMEOS"u8))
        {
            throw new InvalidDataException("ChromeOS keyblock header or magic is invalid.");
        }

        var headerVersionMajor = ReadUInt32(data, 8);
        if (headerVersionMajor != 2)
        {
            throw new InvalidDataException($"Unsupported ChromeOS keyblock version {headerVersionMajor}.");
        }

        var keyblockSize = ReadUInt32(data, 16);
        if (keyblockSize < KeyblockHeaderSize || keyblockSize > containerLength)
        {
            throw new InvalidDataException("ChromeOS keyblock size is outside the supplied data.");
        }

        var keyblockSignature = ReadSignature(data, 24);
        var keyblockHash = ReadSignature(data, 48);
        var dataKey = ReadPackedKey(data, 80);
        EnsureRange(keyblockSignature.Offset, keyblockSignature.Size, keyblockSize - 24, "keyblock signature");
        EnsureRange(keyblockHash.Offset, keyblockHash.Size, keyblockSize - 48, "keyblock hash");
        EnsureRange(dataKey.Offset, dataKey.Size, keyblockSize - 80, "data key");
        EnsureCoveredLength(keyblockSignature.DataSize, keyblockSize - 24, "keyblock signature data");
        EnsureCoveredLength(keyblockHash.DataSize, keyblockSize - 48, "keyblock hash data");

        return new ChromeOsKeyblockMetadata
        {
            HeaderVersionMajor = headerVersionMajor,
            HeaderVersionMinor = ReadUInt32(data, 12),
            KeyblockSize = keyblockSize,
            KeyblockSignature = keyblockSignature,
            KeyblockHash = keyblockHash,
            Flags = ReadUInt32(data, 72),
            DataKey = dataKey
        };
    }

    /// <summary>
    /// Parses a complete vboot2 kernel vblock consisting of a keyblock followed by a kernel preamble.
    /// <para>解析由 keyblock 和内核 preamble 组成的完整 vboot2 内核 vblock。</para>
    /// </summary>
    /// <param name="data">Bytes beginning at the keyblock magic and containing the declared preamble.
    /// <para>从 keyblock 魔数开始、并包含声明的完整 preamble 的字节。</para></param>
    /// <returns>Read-only vblock metadata without signature verification or kernel extraction.
    /// <para>不执行签名验证或内核提取的只读 vblock 元数据。</para></returns>
    /// <exception cref="InvalidDataException">The keyblock, preamble version, or preamble range is invalid.
    /// <para>keyblock、preamble 版本或 preamble 范围非法。</para></exception>
    public static ChromeOsVblockMetadata ParseVblock(ReadOnlySpan<byte> data)
    {
        var keyblock = ParseKeyblock(data);
        var preambleOffset = keyblock.KeyblockSize;
        if (data.Length - preambleOffset < 96)
        {
            throw new InvalidDataException("ChromeOS kernel preamble header is truncated.");
        }

        return ParsePreamble(keyblock, data[(int)preambleOffset..], data.Length - preambleOffset);
    }

    /// <summary>
    /// Parses a kernel preamble whose header was read separately from the keyblock, validating declared sizes
    /// against the total container length rather than the supplied span.
    /// <para>解析头部与 keyblock 分开读取的内核 preamble：声明大小按容器总长度校验，而不是按所提供切片的长度。</para>
    /// </summary>
    /// <param name="keyblock">Already validated keyblock that the preamble follows.
    /// <para>preamble 之前已校验的 keyblock。</para></param>
    /// <param name="preamble">Bytes starting at the preamble; at least the version-dependent header must be present.
    /// <para>从 preamble 起始的字节；至少包含与版本相关的头部。</para></param>
    /// <param name="availablePreambleLength">Bytes available from the preamble start to the end of the container.
    /// <para>从 preamble 起始到容器末尾的可用字节数。</para></param>
    /// <returns>The validated vblock metadata. <para>经过校验的 vblock 元数据。</para></returns>
    /// <exception cref="InvalidDataException">The preamble version or a declared range is invalid.
    /// <para>preamble 版本或声明范围非法。</para></exception>
    internal static ChromeOsVblockMetadata ParsePreamble(
        ChromeOsKeyblockMetadata keyblock, ReadOnlySpan<byte> preamble, long availablePreambleLength)
    {
        if (availablePreambleLength < 96 || preamble.Length < 96)
        {
            throw new InvalidDataException("ChromeOS kernel preamble header is truncated.");
        }

        var preambleSize = ReadUInt32(preamble, 0);
        var preambleMajor = ReadUInt32(preamble, 32);
        var preambleMinor = ReadUInt32(preamble, 36);
        var requiredPreambleSize = GetPreambleHeaderSize(preambleMajor, preambleMinor);
        if (preambleSize < requiredPreambleSize || preambleSize > availablePreambleLength)
        {
            throw new InvalidDataException("ChromeOS kernel preamble size is outside the supplied data.");
        }

        var preambleSignature = ReadSignature(preamble, 8);
        var bodySignature = ReadSignature(preamble, 72);
        EnsureRange(preambleSignature.Offset, preambleSignature.Size, preambleSize - 8, "preamble signature");
        EnsureRange(bodySignature.Offset, bodySignature.Size, preambleSize - 72, "body signature");
        EnsureCoveredLength(preambleSignature.DataSize, preambleSize - 8, "preamble signature data");

        return new ChromeOsVblockMetadata
        {
            Keyblock = keyblock,
            PreambleOffset = keyblock.KeyblockSize,
            PreambleSize = preambleSize,
            PreambleVersionMajor = preambleMajor,
            PreambleVersionMinor = preambleMinor,
            KernelVersion = ReadUInt32(preamble, 40),
            BodyLoadAddress = ReadUInt64(preamble, 48),
            BootloaderAddress = ReadUInt64(preamble, 56),
            BootloaderSize = ReadUInt32(preamble, 64),
            PreambleSignature = preambleSignature,
            BodySignature = bodySignature,
            VmlinuzHeaderAddress = preambleMinor >= 1 ? ReadUInt64(preamble, 96) : 0,
            VmlinuzHeaderSize = preambleMinor >= 1 ? ReadUInt32(preamble, 104) : 0,
            Flags = preambleMinor >= 2 ? ReadUInt32(preamble, 112) : 0
        };
    }

    private static ChromeOsSignatureMetadata ReadSignature(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || data.Length - offset < SignatureDescriptorSize)
        {
            throw new InvalidDataException("ChromeOS signature descriptor is truncated.");
        }

        return new ChromeOsSignatureMetadata
        {
            Offset = ReadUInt32(data, offset),
            Size = ReadUInt32(data, offset + 8),
            DataSize = ReadUInt32(data, offset + 16)
        };
    }

    private static ChromeOsPackedKeyMetadata ReadPackedKey(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || data.Length - offset < PackedKeyHeaderSize)
        {
            throw new InvalidDataException("ChromeOS packed-key descriptor is truncated.");
        }

        return new ChromeOsPackedKeyMetadata
        {
            Offset = ReadUInt32(data, offset),
            Size = ReadUInt32(data, offset + 8),
            Algorithm = ReadUInt32(data, offset + 16),
            Version = ReadUInt32(data, offset + 24)
        };
    }

    private static uint GetPreambleHeaderSize(uint major, uint minor)
    {
        if (major != 2 || minor > 2)
        {
            throw new InvalidDataException($"Unsupported ChromeOS kernel preamble version {major}.{minor}.");
        }

        return minor switch
        {
            0 => 96,
            1 => 112,
            _ => 116
        };
    }

    private static void EnsureRange(uint offset, uint size, uint containerSize, string label)
    {
        if (offset > containerSize || size > containerSize - offset)
        {
            throw new InvalidDataException($"ChromeOS {label} range exceeds its container.");
        }
    }

    private static void EnsureCoveredLength(uint size, uint containerSize, string label)
    {
        if (size > containerSize)
        {
            throw new InvalidDataException($"ChromeOS {label} exceeds its container.");
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || data.Length - offset < sizeof(uint))
        {
            throw new InvalidDataException("ChromeOS structure field is truncated.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || data.Length - offset < sizeof(ulong))
        {
            throw new InvalidDataException("ChromeOS structure field is truncated.");
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
    }

    /// <summary>
    /// The kind of ChromeOS image reported by detection.
    /// <para>检测得出的 ChromeOS 镜像种类。</para>
    /// </summary>
    internal enum ChromeOsImageType
    {
        /// <summary>
        /// No ChromeOS magic was matched. <para>未匹配到 ChromeOS 魔数。</para>
        /// </summary>
        Unknown,

        /// <summary>A validated standalone legacy vboot keyblock.
        /// <para>经过校验的独立 legacy vboot keyblock。</para></summary>
        Keyblock,

        /// <summary>A validated legacy vboot kernel image with a keyblock, preamble and body.
        /// <para>包含 keyblock、preamble 和 body 且经过校验的 legacy vboot 内核镜像。</para></summary>
        Kernel,

        /// <summary>Reserved compatibility value; no fixed-size signature block is inferred.
        /// <para>保留的兼容枚举值；不会推断固定大小的签名块。</para></summary>
        SignatureBlock,

        /// <summary>A validated legacy vboot keyblock followed by a kernel preamble.
        /// <para>keyblock 后紧接内核 preamble 且经过校验的 legacy vboot vblock。</para></summary>
        Vblock,

        /// <summary>
        /// A ChromeOS image that is not a kernel image; <see cref="Detect(byte[])"/> never assigns it.
        /// <para>非内核镜像的 ChromeOS 镜像；<see cref="Detect(byte[])"/> 不会赋值此项。</para>
        /// </summary>
        Other
    }

    /// <summary>
    /// The verdict of a ChromeOS detection together with a human-readable label.
    /// <para>ChromeOS 检测结论及其可读标签。</para>
    /// </summary>
    public class ChromeOsDetectionResult
    {
        /// <summary>
        /// Set when the buffer is at least 64 bytes and opens with the <c>CHROMEOS</c> magic.
        /// <para>缓冲区不少于 64 字节且以 <c>CHROMEOS</c> 魔数开头时为真。</para>
        /// </summary>
        /// <value>
        /// The outcome of the magic test for the inspected buffer.
        /// <para>被检查缓冲区的魔数测试结果。</para>
        /// </value>
        public bool IsChromeOs { get; set; }

        /// <summary>
        /// A validated <see cref="ChromeOsImageType.Keyblock"/>, <see cref="ChromeOsImageType.Vblock"/>
        /// or <see cref="ChromeOsImageType.Kernel"/>; malformed candidates are unknown.
        /// <para>经过校验的 <see cref="ChromeOsImageType.Keyblock"/>、<see cref="ChromeOsImageType.Vblock"/>
        /// 或 <see cref="ChromeOsImageType.Kernel"/>；结构非法的候选输入为 unknown。</para>
        /// </summary>
        /// <value>
        /// The classification produced after bounded keyblock and, when present, preamble checks.
        /// <para>在有界校验 keyblock 以及存在时的 preamble 后得出的分类。</para>
        /// </value>
        public ChromeOsImageType ImageType { get; set; }

        /// <summary>Fixed boundary size recognized by the read-only classifier, when known.
        /// <para>只读分类器确认的固定边界大小，未知时为零。</para></summary>
        public int StructureSize { get; set; }

        /// <summary>
        /// Static label for the verdict: "ChromeOS Kernel Image" or "Not a ChromeOS image".
        /// <para>结论的固定标签：“ChromeOS Kernel Image” 或 “Not a ChromeOS image”。</para>
        /// </summary>
        /// <value>
        /// Text intended for display as is; it is never localized and never <see langword="null"/>.
        /// <para>按原样展示的文本；不做本地化处理，也永不为 <see langword="null"/>。</para>
        /// </value>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Failure detail for a candidate whose magic is present but whose bounded structure is invalid.
        /// <para>候选输入存在魔数但有界结构非法时的失败详情。</para>
        /// </summary>
        /// <value>
        /// A human-readable reason, or <see langword="null"/> when nothing went wrong.
        /// <para>人类可读的原因；未发生错误时为 <see langword="null"/>。</para>
        /// </value>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Detects a ChromeOS image.
    /// <para>检测 ChromeOS 镜像。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate bytes to inspect. <para>待检查的候选字节。</para>
    /// </param>
    /// <returns>
    /// A populated result that is never <see langword="null"/>. A result is successful only after
    /// all declared keyblock and preamble ranges pass validation.
    /// <para>始终返回非 <see langword="null"/>。只有所有声明的 keyblock 和 preamble 范围通过校验时，
    /// 结果才表示成功。</para>
    /// </returns>
    public static ChromeOsDetectionResult Detect(byte[] data)
    {
        if (data is null)
        {
            return new ChromeOsDetectionResult
            {
                ImageType = ChromeOsImageType.Unknown,
                Description = "Not a ChromeOS image",
                ErrorMessage = "No data was supplied."
            };
        }

        return Detect(data.AsSpan(), data.LongLength);
    }

    /// <summary>
    /// Detects a ChromeOS image from a span that may hold only the keyblock header, using
    /// <paramref name="totalLength"/> for the length comparisons the classification performs.
    /// <para>从可能只包含 keyblock 头部的切片检测 ChromeOS 镜像；分类所需长度比较使用
    /// <paramref name="totalLength"/>。</para>
    /// </summary>
    /// <param name="data">Prefix starting at the container offset zero; it must contain the keyblock header and,
    /// for a vblock, the kernel preamble header at the declared keyblock size.
    /// <para>从容器零偏移开始的前缀；必须包含 keyblock 头部，vblock 还须包含声明 keyblock 大小处的内核 preamble 头部。</para>
    /// </param>
    /// <param name="totalLength">Full container length. <para>容器完整长度。</para></param>
    /// <returns>A populated result that is never <see langword="null"/>; malformed candidates are unknown.
    /// <para>始终返回非 <see langword="null"/>；结构非法的候选为 unknown。</para></returns>
    internal static ChromeOsDetectionResult Detect(ReadOnlySpan<byte> data, long totalLength)
    {
        if (!HasMagic(data))
        {
            return NotChromeOs();
        }

        try
        {
            var keyblock = ParseKeyblock(data, totalLength);
            if (totalLength == keyblock.KeyblockSize)
            {
                return CreateKeyblockResult(keyblock);
            }

            if (data.Length < keyblock.KeyblockSize)
            {
                throw new InvalidDataException("ChromeOS kernel preamble header is truncated.");
            }

            return CreateVblockResult(keyblock, data[(int)keyblock.KeyblockSize..], totalLength);
        }
        catch (InvalidDataException ex)
        {
            return CreateUnknownResult(ex);
        }
    }

    /// <summary>
    /// Detects a ChromeOS image from separately read keyblock and preamble headers, so neither the keyblock body
    /// nor the kernel body has to be materialized.
    /// <para>从分开读取的 keyblock 与 preamble 头部检测 ChromeOS 镜像，无需物化 keyblock 主体或内核主体。</para>
    /// </summary>
    /// <param name="keyblockHeader">Leading bytes of the container, at least <see cref="KeyblockHeaderSize"/> long.
    /// <para>容器起始字节，至少 <see cref="KeyblockHeaderSize"/> 字节。</para></param>
    /// <param name="preambleHeader">Bytes starting at the declared keyblock size, or empty when the container ends there.
    /// <para>从声明 keyblock 大小处开始的字节；容器在此结束时为空。</para></param>
    /// <param name="totalLength">Full container length. <para>容器完整长度。</para></param>
    /// <returns>A populated result that is never <see langword="null"/>; malformed candidates are unknown.
    /// <para>始终返回非 <see langword="null"/>；结构非法的候选为 unknown。</para></returns>
    internal static ChromeOsDetectionResult Detect(
        ReadOnlySpan<byte> keyblockHeader, ReadOnlySpan<byte> preambleHeader, long totalLength)
    {
        if (!HasMagic(keyblockHeader))
        {
            return NotChromeOs();
        }

        try
        {
            var keyblock = ParseKeyblock(keyblockHeader, totalLength);
            return totalLength == keyblock.KeyblockSize
                ? CreateKeyblockResult(keyblock)
                : CreateVblockResult(keyblock, preambleHeader, totalLength);
        }
        catch (InvalidDataException ex)
        {
            return CreateUnknownResult(ex);
        }
    }

    private static ChromeOsDetectionResult CreateVblockResult(
        ChromeOsKeyblockMetadata keyblock, ReadOnlySpan<byte> preamble, long totalLength)
    {
        var vblock = ParsePreamble(keyblock, preamble, totalLength - keyblock.KeyblockSize);
        var vblockEnd = checked((ulong)vblock.PreambleOffset + vblock.PreambleSize);
        var bodyEnd = checked(vblockEnd + vblock.BodySignature.DataSize);
        var hasBody = (ulong)totalLength >= bodyEnd;
        return CreateResult(
            hasBody ? ChromeOsImageType.Kernel : ChromeOsImageType.Vblock,
            checked((int)vblockEnd),
            hasBody
                ? "ChromeOS keyblock, kernel preamble and bounded body structure recognized; signature verification is not implemented."
                : "ChromeOS keyblock and kernel preamble structure recognized; kernel body is not present in the supplied data.");
    }

    private static ChromeOsDetectionResult CreateKeyblockResult(ChromeOsKeyblockMetadata keyblock)
        => CreateResult(
            ChromeOsImageType.Keyblock,
            checked((int)keyblock.KeyblockSize),
            "ChromeOS keyblock structure recognized; signature verification is not implemented.");

    private static ChromeOsDetectionResult NotChromeOs()
        => new()
        {
            ImageType = ChromeOsImageType.Unknown,
            Description = "Not a ChromeOS image"
        };

    private static ChromeOsDetectionResult CreateUnknownResult(InvalidDataException ex)
        => new()
        {
            ImageType = ChromeOsImageType.Unknown,
            Description = "Not a structurally recognized ChromeOS image",
            ErrorMessage = ex.Message
        };

    private static ChromeOsDetectionResult CreateResult(
        ChromeOsImageType imageType, int structureSize, string description)
        => new()
        {
            IsChromeOs = true,
            ImageType = imageType,
            StructureSize = structureSize,
            Description = description
        };

    internal static bool HasMagic(ReadOnlySpan<byte> data)
        => data.Length >= 8 && data[..8].SequenceEqual("CHROMEOS"u8);

    /// <summary>
    /// Extracts the kernel data. This entry point performs no extraction and rejects every call.
    /// <para>提取内核数据。此入口不执行任何提取，并拒绝所有调用。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate image buffer; it is not read before the call is rejected.
    /// <para>待处理的镜像缓冲区；在调用被拒绝前不会被读取。</para>
    /// </param>
    /// <returns>
    /// Nothing; the call always fails, so the declared kernel payload is never produced.
    /// <para>不返回任何内容；调用总是失败，因此不会产生所声明的内核数据。</para>
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Always thrown. <para>始终抛出。</para>
    /// </exception>
    public static byte[]? ExtractKernel(byte[] data)
    {
        throw new NotSupportedException(
            "ChromeOS kernel extraction is not inlined in this simplified port.");
    }

    /// <summary>
    /// Verifies the signature. This entry point performs no verification and rejects every call.
    /// <para>验证签名。此入口不执行任何验证，并拒绝所有调用。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate image buffer; it is not read before the call is rejected.
    /// <para>待处理的镜像缓冲区；在调用被拒绝前不会被读取。</para>
    /// </param>
    /// <returns>
    /// Nothing; the call always fails, so no verdict is produced.
    /// <para>不返回任何内容；调用总是失败，因此不会给出结论。</para>
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Always thrown. <para>始终抛出。</para>
    /// </exception>
    public static bool VerifySignature(byte[] data)
    {
        throw new NotSupportedException(
            "ChromeOS signature verification is not inlined in this simplified port.");
    }

    /// <summary>Describes one vboot2 signature descriptor.
    /// <para>描述一个 vboot2 签名描述符。</para></summary>
    public sealed class ChromeOsSignatureMetadata
    {
        /// <summary>Signature data offset relative to this descriptor.
        /// <para>相对于此描述符的签名数据偏移。</para></summary>
        public required uint Offset { get; init; }

        /// <summary>Signature data length in bytes.
        /// <para>签名数据长度（字节）。</para></summary>
        public required uint Size { get; init; }

        /// <summary>Length of the data covered by the signature descriptor.
        /// <para>签名描述符覆盖的数据长度。</para></summary>
        public required uint DataSize { get; init; }
    }

    /// <summary>Describes a vboot2 packed public-key header without decoding key bytes.
    /// <para>描述 vboot2 压缩公钥头，但不解码密钥字节。</para></summary>
    public sealed class ChromeOsPackedKeyMetadata
    {
        /// <summary>Key data offset relative to this packed-key descriptor.
        /// <para>相对于此压缩密钥描述符的密钥数据偏移。</para></summary>
        public required uint Offset { get; init; }

        /// <summary>Key data length in bytes.
        /// <para>密钥数据长度（字节）。</para></summary>
        public required uint Size { get; init; }

        /// <summary>vboot signature algorithm identifier.
        /// <para>vboot 签名算法标识。</para></summary>
        public required uint Algorithm { get; init; }

        /// <summary>Rollback key version.
        /// <para>回滚保护使用的密钥版本。</para></summary>
        public required uint Version { get; init; }
    }

    /// <summary>Validated read-only metadata for a vboot2 keyblock.
    /// <para>经过校验的 vboot2 keyblock 只读元数据。</para></summary>
    public sealed class ChromeOsKeyblockMetadata
    {
        /// <summary>Keyblock header major version.
        /// <para>keyblock 头部主版本。</para></summary>
        public required uint HeaderVersionMajor { get; init; }

        /// <summary>Keyblock header minor version.
        /// <para>keyblock 头部次版本。</para></summary>
        public required uint HeaderVersionMinor { get; init; }

        /// <summary>Total keyblock length including padding.
        /// <para>包含填充在内的 keyblock 总长度。</para></summary>
        public required uint KeyblockSize { get; init; }

        /// <summary>Signature descriptor for the keyblock, without cryptographic verification.
        /// <para>keyblock 签名描述符，但不执行密码学验证。</para></summary>
        public required ChromeOsSignatureMetadata KeyblockSignature { get; init; }

        /// <summary>Hash descriptor for the keyblock, without digest calculation.
        /// <para>keyblock 哈希描述符，但不计算摘要。</para></summary>
        public required ChromeOsSignatureMetadata KeyblockHash { get; init; }

        /// <summary>Keyblock flags.
        /// <para>keyblock 标志。</para></summary>
        public required uint Flags { get; init; }

        /// <summary>Packed key header used to verify the following data.
        /// <para>用于验证后续数据的压缩密钥头。</para></summary>
        public required ChromeOsPackedKeyMetadata DataKey { get; init; }
    }

    /// <summary>Validated read-only metadata for a vboot2 kernel vblock.
    /// <para>经过校验的 vboot2 内核 vblock 只读元数据。</para></summary>
    public sealed class ChromeOsVblockMetadata
    {
        /// <summary>Validated keyblock metadata at offset zero.
        /// <para>位于零偏移处的 keyblock 元数据。</para></summary>
        public required ChromeOsKeyblockMetadata Keyblock { get; init; }

        /// <summary>Kernel preamble offset, equal to <see cref="ChromeOsKeyblockMetadata.KeyblockSize"/>.
        /// <para>内核 preamble 偏移，等于 <see cref="ChromeOsKeyblockMetadata.KeyblockSize"/>。</para></summary>
        public required uint PreambleOffset { get; init; }

        /// <summary>Total kernel preamble length including padding.
        /// <para>包含填充在内的内核 preamble 总长度。</para></summary>
        public required uint PreambleSize { get; init; }

        /// <summary>Kernel preamble header major version.
        /// <para>内核 preamble 头部主版本。</para></summary>
        public required uint PreambleVersionMajor { get; init; }

        /// <summary>Kernel preamble header minor version.
        /// <para>内核 preamble 头部次版本。</para></summary>
        public required uint PreambleVersionMinor { get; init; }

        /// <summary>Rollback kernel version.
        /// <para>回滚保护使用的内核版本。</para></summary>
        public required uint KernelVersion { get; init; }

        /// <summary>Kernel body load address.
        /// <para>内核 body 加载地址。</para></summary>
        public required ulong BodyLoadAddress { get; init; }

        /// <summary>Bootloader address recorded in the preamble.
        /// <para>preamble 中记录的 bootloader 地址。</para></summary>
        public required ulong BootloaderAddress { get; init; }

        /// <summary>Bootloader length in bytes.
        /// <para>bootloader 长度（字节）。</para></summary>
        public required uint BootloaderSize { get; init; }

        /// <summary>Signature descriptor for the preamble.
        /// <para>preamble 签名描述符。</para></summary>
        public required ChromeOsSignatureMetadata PreambleSignature { get; init; }

        /// <summary>Signature descriptor for the kernel body.
        /// <para>内核 body 签名描述符。</para></summary>
        public required ChromeOsSignatureMetadata BodySignature { get; init; }

        /// <summary>vmlinuz header address for preamble version 2.1 and later.
        /// <para>preamble 2.1 及更高版本的 vmlinuz 头地址。</para></summary>
        public required ulong VmlinuzHeaderAddress { get; init; }

        /// <summary>vmlinuz header length for preamble version 2.1 and later.
        /// <para>preamble 2.1 及更高版本的 vmlinuz 头长度。</para></summary>
        public required uint VmlinuzHeaderSize { get; init; }

        /// <summary>Kernel image type flags for preamble version 2.2.
        /// <para>preamble 2.2 的内核镜像类型标志。</para></summary>
        public required uint Flags { get; init; }
    }

}