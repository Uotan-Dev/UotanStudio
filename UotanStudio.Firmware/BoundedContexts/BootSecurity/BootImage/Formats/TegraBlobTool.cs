using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Formats;

/// <summary>
/// Parses the compact NVIDIA Tegra boot blob format.
/// <para>解析紧凑的 NVIDIA Tegra 启动 blob 格式。</para>
/// </summary>
public static class TegraBlobTool
{
    private const int HeaderSize = 256;
    private const uint Magic = 0x4144564Eu;

    /// <summary>
    /// Fixed-layout Tegra blob header decoded from little-endian uint32 fields.
    /// <para>由小端 uint32 字段解析出的固定布局 Tegra blob 头。</para>
    /// </summary>
    public sealed class Header
    {
        /// <summary>Header format version stored at offset 4. <para>偏移 4 处存储的头部格式版本。</para></summary>
        /// <value>Version number as recorded by the blob producer. <para>blob 生成方记录的版本号。</para></value>
        public uint Version { get; init; }

        /// <summary>Kernel compression code stored at offset 8. <para>偏移 8 处存储的内核压缩编码。</para></summary>
        /// <value>
        /// <c>0</c> raw, <c>1</c> gzip, <c>2</c> LZ4, <c>3</c> legacy LZ4, <c>4</c> LZMA; unknown codes leave the data untouched.
        /// <para><c>0</c> 原始、<c>1</c> gzip、<c>2</c> LZ4、<c>3</c> 传统 LZ4 帧、<c>4</c> LZMA；未知编码值保持数据原样。</para>
        /// </value>
        public uint CompressionType { get; init; }

        /// <summary>Kernel byte count stored at offset 12. <para>偏移 12 处存储的内核字节数。</para></summary>
        /// <value>
        /// Size of the kernel region; validated to lie inside the input before parsing succeeds.
        /// <para>内核区域大小；解析成功前已校验其位于输入范围内。</para>
        /// </value>
        public uint KernelSize { get; init; }

        /// <summary>Kernel region start offset stored at offset 16. <para>偏移 16 处存储的内核区域起始偏移。</para></summary>
        /// <value>
        /// Byte offset of the kernel from the blob start, bounded by the input length.
        /// <para>内核相对 blob 起始的字节偏移，受输入长度约束。</para>
        /// </value>
        public uint KernelOffset { get; init; }

        /// <summary>Ramdisk byte count stored at offset 20. <para>偏移 20 处存储的 ramdisk 字节数。</para></summary>
        /// <value>
        /// Size of the ramdisk region; validated to lie inside the input before parsing succeeds.
        /// <para>ramdisk 区域大小；解析成功前已校验其位于输入范围内。</para>
        /// </value>
        public uint RamdiskSize { get; init; }

        /// <summary>Ramdisk region start offset stored at offset 24. <para>偏移 24 处存储的 ramdisk 区域起始偏移。</para></summary>
        /// <value>
        /// Byte offset of the ramdisk from the blob start, bounded by the input length.
        /// <para>ramdisk 相对 blob 起始的字节偏移，受输入长度约束。</para>
        /// </value>
        public uint RamdiskOffset { get; init; }

        /// <summary>Kernel load address stored at offset 28. <para>偏移 28 处存储的内核加载地址。</para></summary>
        /// <value>
        /// Target physical address for the kernel, copied verbatim from the blob.
        /// <para>内核的目标物理地址，原样复制自 blob。</para>
        /// </value>
        public uint LoadAddress { get; init; }

        /// <summary>Kernel entry address stored at offset 32. <para>偏移 32 处存储的内核入口地址。</para></summary>
        /// <value>Execution start address, copied verbatim from the blob. <para>执行起始地址，原样复制自 blob。</para></value>
        public uint EntryAddress { get; init; }

        /// <summary>Declared header byte count stored at offset 36. <para>偏移 36 处存储的声明头部字节数。</para></summary>
        /// <value>
        /// Always at least 256 and no larger than the input, otherwise parsing fails.
        /// <para>至少为 256 且不超过输入长度，否则解析失败。</para>
        /// </value>
        public uint HeaderSize { get; init; }

        /// <summary>Declared total blob size stored at offset 40. <para>偏移 40 处存储的声明 blob 总大小。</para></summary>
        /// <value>
        /// Total size in bytes; <c>0</c> means unset and is accepted, any other value must not exceed the input.
        /// <para>总字节数；<c>0</c> 表示未设置且被接受，其他值不得超过输入长度。</para>
        /// </value>
        public uint TotalSize { get; init; }

        /// <summary>Description string drawn from bytes 64 to 256. <para>取自第 64 到 256 字节的描述字符串。</para></summary>
        /// <value>
        /// ASCII text truncated at the first NUL; empty when the window begins with NUL.
        /// <para>在首个 NUL 处截断的 ASCII 文本；窗口以 NUL 开头时为空。</para>
        /// </value>
        public string Description { get; init; } = string.Empty;
    }

    /// <summary>
    /// Outcome of a Tegra blob parse attempt, carrying either a decoded header with extracted regions or a failure message.
    /// <para>Tegra blob 解析尝试的结果，要么携带解码出的头部与提取的区域，要么携带失败消息。</para>
    /// </summary>
    public sealed class ParseResult
    {
        /// <summary>Whether every structural check passed. <para>所有结构校验是否全部通过。</para></summary>
        /// <value>
        /// <see langword="true"/> when magic, header size, total size and component ranges are all valid; <see langword="false"/> on any failure, with <see cref="ErrorMessage"/> set.
        /// <para>魔数、头部大小、总大小与组件范围全部有效时为 <see langword="true"/>；任一失败时为 <see langword="false"/>，并设置 <see cref="ErrorMessage"/>。</para>
        /// </value>
        public bool IsValid { get; init; }

        /// <summary>Decoded header for a successful parse. <para>解析成功时解码出的头部。</para></summary>
        /// <value>The parsed <see cref="Header"/>; <see langword="null"/> when <see cref="IsValid"/> is <see langword="false"/>.
        /// <para>解析后的 <see cref="Header"/>；<see cref="IsValid"/> 为 <see langword="false"/> 时为 <see langword="null"/>。</para></value>
        public Header? Header { get; init; }

        /// <summary>Kernel bytes copied out of the blob. <para>从 blob 复制出的内核字节。</para></summary>
        /// <value>
        /// Compressed kernel region exactly as stored; empty when the parse failed or the size is zero.
        /// <para>按存储原样复制的压缩内核区域；解析失败或大小为零时为空数组。</para>
        /// </value>
        public byte[] Kernel { get; init; } = [];

        /// <summary>Ramdisk bytes copied out of the blob. <para>从 blob 复制出的 ramdisk 字节。</para></summary>
        /// <value>
        /// Ramdisk region exactly as stored; empty when the parse failed or the size is zero.
        /// <para>按存储原样复制的 ramdisk 区域；解析失败或大小为零时为空数组。</para>
        /// </value>
        public byte[] Ramdisk { get; init; } = [];

        /// <summary>Human-readable reason for a failed parse. <para>解析失败的人可读原因。</para></summary>
        /// <value>English failure text without localization; <see langword="null"/> when <see cref="IsValid"/> is <see langword="true"/>.
        /// <para>不带本地化的英文失败文本；<see cref="IsValid"/> 为 <see langword="true"/> 时为 <see langword="null"/>。</para></value>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Checks whether the span starts with the Tegra blob little-endian magic.
    /// <para>检查该 Span 是否以 Tegra blob 的小端魔数开头。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate blob bytes; fewer than 4 bytes never match. <para>候选 blob 字节；不足 4 字节时永不匹配。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the first four bytes equal the Tegra magic; otherwise <see langword="false"/>.
    /// <para>前四个字节等于 Tegra 魔数时为 <see langword="true"/>，否则为 <see langword="false"/>。</para>
    /// </returns>
    public static bool IsTegraBlob(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    /// <summary>
    /// Validates and decodes a Tegra blob, treating the input as untrusted data.
    /// <para>校验并解码 Tegra blob，将输入视为不可信数据。</para>
    /// </summary>
    /// <param name="data">
    /// Whole blob contents; the caller owns the memory and the result copies the kernel and ramdisk regions out of it.
    /// <para>完整的 blob 内容；调用方持有内存所有权，结果会从中复制内核与 ramdisk 区域。</para>
    /// </param>
    /// <returns>
    /// A <see cref="ParseResult"/>. Bounds failures (short input, bad magic, header size outside the input, total size outside the input, component range outside the input) return an invalid result with <see cref="ParseResult.ErrorMessage"/> set instead of throwing.
    /// <para>返回 <see cref="ParseResult"/>。越界失败（输入过短、魔数错误、头部大小超出输入、总大小超出输入、组件范围超出输入）返回设置了 <see cref="ParseResult.ErrorMessage"/> 的无效结果而不抛出异常。</para>
    /// </returns>
    /// <example>
    /// <code>
    /// var result = TegraBlobTool.Parse(File.ReadAllBytes("blob.bin"));
    /// if (result.IsValid)
    ///     Console.WriteLine(result.Header!.Description);
    /// </code>
    /// </example>
    public static ParseResult Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            return Invalid("Data is shorter than the Tegra blob header.");
        if (!IsTegraBlob(data))
            return Invalid("Invalid Tegra blob magic.");

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]);
        var totalSize = BinaryPrimitives.ReadUInt32LittleEndian(data[40..]);
        if (headerSize < HeaderSize || headerSize > data.Length)
            return Invalid("Tegra header size is outside the input.");
        if (totalSize != 0 && totalSize > data.Length)
            return Invalid("Tegra total size is outside the input.");

        var kernelOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var kernelSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var ramdiskOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
        var ramdiskSize = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        if (!Within(data.Length, kernelOffset, kernelSize) || !Within(data.Length, ramdiskOffset, ramdiskSize))
            return Invalid("Tegra component range is outside the input.");

        var description = data[64..Math.Min(data.Length, 256)]
            .IndexOf((byte)0) is var nul && nul >= 0
            ? System.Text.Encoding.ASCII.GetString(data.Slice(64, nul))
            : System.Text.Encoding.ASCII.GetString(data[64..Math.Min(data.Length, 256)]);

        var header = new Header
        {
            Version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            CompressionType = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            KernelSize = kernelSize,
            KernelOffset = kernelOffset,
            RamdiskSize = ramdiskSize,
            RamdiskOffset = ramdiskOffset,
            LoadAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]),
            EntryAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[32..]),
            HeaderSize = headerSize,
            TotalSize = totalSize,
            Description = description
        };

        return new ParseResult
        {
            IsValid = true,
            Header = header,
            Kernel = data.Slice((int)kernelOffset, (int)kernelSize).ToArray(),
            Ramdisk = data.Slice((int)ramdiskOffset, (int)ramdiskSize).ToArray()
        };
    }

    /// <summary>
    /// Parses the blob and returns its kernel, optionally decompressed according to the header compression code.
    /// <para>解析 blob 并返回其内核，可选择依据头部的压缩编码进行解压。</para>
    /// </summary>
    /// <param name="data">Whole blob contents treated as untrusted input. <para>视为不可信输入的完整 blob 内容。</para></param>
    /// <param name="decompress">
    /// When <see langword="true"/>, inflates gzip, LZ4, legacy LZ4 or LZMA payloads per the header code; a failed decompression returns the compressed bytes.
    /// <para>为 <see langword="true"/> 时按头部编码解压 gzip、LZ4、传统 LZ4 帧或 LZMA 数据；解压失败时返回压缩字节。</para>
    /// </param>
    /// <returns>
    /// The kernel bytes, or an empty array when the blob does not parse. <para>内核字节；blob 无法解析时返回空数组。</para>
    /// </returns>
    public static byte[] ExtractKernel(ReadOnlySpan<byte> data, bool decompress = true)
    {
        var parsed = Parse(data);
        return !parsed.IsValid ? [] : decompress ? Decompress(parsed.Kernel, parsed.Header!.CompressionType) : parsed.Kernel;
    }

    /// <summary>
    /// Parses the blob and returns its ramdisk, optionally auto-detecting the compression container.
    /// <para>解析 blob 并返回其 ramdisk，可选择自动检测压缩容器。</para>
    /// </summary>
    /// <param name="data">Whole blob contents treated as untrusted input. <para>视为不可信输入的完整 blob 内容。</para></param>
    /// <param name="decompress">
    /// When <see langword="true"/>, runs auto-detection decompression; a failed or undetected decompression returns the original bytes.
    /// <para>为 <see langword="true"/> 时执行自动检测解压；解压失败或未识别时返回原始字节。</para>
    /// </param>
    /// <returns>
    /// The ramdisk bytes, or an empty array when the blob does not parse. <para>ramdisk 字节；blob 无法解析时返回空数组。</para>
    /// </returns>
    public static byte[] ExtractRamdisk(ReadOnlySpan<byte> data, bool decompress = true)
    {
        var parsed = Parse(data);
        return !parsed.IsValid ? [] : decompress ? BootCompressor.DecompressOrReturn(parsed.Ramdisk) : parsed.Ramdisk;
    }

    private static byte[] Decompress(byte[] data, uint compressionType) => compressionType switch
    {
        0 => data,
        1 => BootCompressor.TryDecompress(data, CompressionType.Gzip) ?? data,
        2 => BootCompressor.TryDecompress(data, CompressionType.Lz4) ?? data,
        3 => BootCompressor.TryDecompress(data, CompressionType.Lz4Legacy) ?? data,
        4 => BootCompressor.TryDecompress(data, CompressionType.Lzma) ?? data,
        _ => data
    };

    private static bool Within(int length, uint offset, uint size) =>
        offset <= (ulong)length && size <= (ulong)length - offset;

    private static ParseResult Invalid(string message) => new() { ErrorMessage = message };
}