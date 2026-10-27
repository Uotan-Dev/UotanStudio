using System.Text;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.Core;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.BootSecurity.BootImage.Parser;

/// <summary>
/// Parses Android boot images carrying the <c>ANDROID!</c> magic for header versions 0 through 4,
/// extracting kernel, ramdisk, second-stage, DTB, recovery DTBO and V4 signature segments.
/// <para>解析带 <c>ANDROID!</c> 魔数的 Android Boot 镜像（头部版本 0 至 4），提取 kernel、ramdisk、second、DTB、recovery DTBO 与 V4 签名区段。</para>
/// </summary>
/// <example>
/// <code>
/// using var parser = new BootImageParser();
/// parser.ParseFile("boot.img");
/// byte[] kernel = parser.Kernel;
/// </code>
/// </example>
public class BootImageParser : IDisposable
{
    private const string BootMagic = "ANDROID!";
    private const uint CanonicalV3HeaderSize = 1580;
    private const uint CanonicalV4HeaderSize = 1584;
    private const int MaximumHeaderBlobLength = 65536;
    private static readonly uint[] V3V4PageSizeCandidates = [16384, 8192, 4096, 2048];
    private bool _disposed;

    /// <summary>The header fields produced by the most recent parse call.
    /// <para>最近一次解析得到的头部字段。</para></summary>
    /// <value>A default <see cref="BootImageHeader"/> before any parse; each parse replaces the instance entirely.
    /// <para>未解析时为默认构造的 <see cref="BootImageHeader"/>；每次解析整体替换实例。</para></value>
    public BootImageHeader Header { get; private set; } = new BootImageHeader();

    /// <summary>The kernel segment exactly as stored in the image, still compressed when the image compresses it.
    /// <para>镜像中原样存放的 kernel 区段；镜像为压缩格式时仍保持压缩态。</para></summary>
    /// <value>Bytes read from the page-aligned kernel offset; empty when the size is <c>0</c> or the stored range exceeds the image.
    /// <para>从页对齐的 kernel 偏移读取的字节；大小为 <c>0</c> 或存储范围超出镜像时为空数组。</para></value>
    public byte[] Kernel { get; private set; } = [];

    /// <summary>The ramdisk segment exactly as stored in the image, still compressed when the image compresses it.
    /// <para>镜像中原样存放的 ramdisk 区段；镜像为压缩格式时仍保持压缩态。</para></summary>
    /// <value>Bytes read from the page-aligned ramdisk offset; empty when the size is <c>0</c> or the stored range exceeds the image.
    /// <para>从页对齐的 ramdisk 偏移读取的字节；大小为 <c>0</c> 或存储范围超出镜像时为空数组。</para></value>
    public byte[] Ramdisk { get; private set; } = [];

    /// <summary>The second-stage bootloader segment of V0/V2 images, read from the page-aligned second offset.
    /// <para>V0/V2 镜像的 second-stage 引导区段，从页对齐的 second 偏移读取。</para></summary>
    /// <value>Raw segment bytes; empty when the size is <c>0</c>, on V3/V4 images, or when the stored range exceeds the image.
    /// <para>原始字节；大小为 <c>0</c>、V3/V4 镜像，或存储范围超出镜像时为空数组。</para></value>
    public byte[] Second { get; private set; } = [];

    /// <summary>The device tree blob segment of images whose header declares a DTB region.
    /// <para>头部声明了 DTB 区域的镜像中的设备树 blob 区段。</para></summary>
    /// <value>Raw DTB bytes; empty when the size is <c>0</c>, on V3/V4 images, or when the stored range exceeds the image.
    /// <para>原始 DTB 字节；大小为 <c>0</c>、V3/V4 镜像，或存储范围超出镜像时为空数组。</para></value>
    public byte[] Dtb { get; private set; } = [];

    /// <summary>A component buffer that the parse paths do not populate.
    /// <para>各解析路径均不填充的组件缓冲区。</para></summary>
    /// <value>Always an empty array after parsing; cleared again on dispose.
    /// <para>解析后始终为空数组；释放时同样被清空。</para></value>
    public byte[] Extra { get; private set; } = [];

    /// <summary>The recovery DTBO segment located at the header-declared absolute offset.
    /// <para>按头部声明的绝对偏移定位的 recovery DTBO 区段。</para></summary>
    /// <value>Raw DTBO bytes on V1/V2 images with a non-zero size and offset; empty otherwise or when the range exceeds the image.
    /// <para>仅在 V1/V2 镜像且大小与非零偏移均存在时返回原始字节；其余情况或范围超出镜像时为空数组。</para></value>
    public byte[] RecoveryDtbo { get; private set; } = [];

    /// <summary>The V4 boot image signature segment.
    /// <para>V4 Boot 镜像的签名区段。</para></summary>
    /// <value>Bytes read from the page-aligned signature region when the signature size is non-zero; empty for all other versions or sizes of <c>0</c>.
    /// <para>签名大小非零时从页对齐的签名区域读取；其他版本或大小为 <c>0</c> 时为空数组。</para></value>
    public byte[] Signature { get; private set; } = [];

    internal static BootImageHeader ReadHeaderFile(string path)
    {
        using var input = File.OpenRead(path);
        var prefix = new byte[44];
        input.ReadExactly(prefix);
        if (!prefix.AsSpan(0, 8).SequenceEqual("ANDROID!"u8))
            throw new InvalidDataException("Invalid boot image magic.");
        var version = prefix.ReadUInt32LE(40);
        var size = version switch
        {
            0 => 1632,
            1 => 1648,
            2 => 1660,
            3 => 1580,
            4 => 1584,
            _ => throw new InvalidDataException("Unsupported boot image version.")
        };
        var bytes = new byte[size];
        input.Position = 0;
        input.ReadExactly(bytes);
        using var parser = new BootImageParser();
        if (version <= 2)
        {
            parser.ParseV0V2(bytes, version);
            if (version >= 1) parser.Header.HeaderSize = bytes.ReadUInt32LE(1644);
        }
        else if (version == 3)
        {
            parser.ParseV3(bytes);
            parser.ApplyV3V4PageLayout(input, input.Length, isV4: false);
        }
        else
        {
            parser.ParseV4(bytes);
            parser.ApplyV3V4PageLayout(input, input.Length, isV4: true);
        }
        var header = parser.Header;
        if (header.PageSize is < 2048 or > 65536 || (header.PageSize & (header.PageSize - 1)) != 0
            || header.HeaderSize < size || header.HeaderSize > header.PageSize)
            throw new InvalidDataException("Invalid boot header or page size.");
        long Align(long value) => checked((value + header.PageSize - 1) / header.PageSize * header.PageSize);
        var kernelOffset = Align(header.HeaderSize);
        var ramdiskOffset = Align(kernelOffset + header.KernelSize);
        header.KernelCompression = DetectAt(kernelOffset, header.KernelSize);
        header.RamdiskCompression = DetectAt(ramdiskOffset, header.RamdiskSize);
        return header;

        CompressionType DetectAt(long offset, uint length)
        {
            if (length == 0) return CompressionType.None;
            FileExtraction.ValidateRange(offset, length, input.Length, "Boot component");
            input.Position = offset;
            var signature = new byte[(int)Math.Min(16u, length)];
            input.ReadExactly(signature);
            return BootCompressor.Detect(signature);
        }
    }

    /// <summary>
    /// Parses a boot image from a file.
    /// <para>从文件解析 Boot 镜像。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to an openable, seek-readable boot image file; the file is closed when parsing completes.
    /// <para>可打开、可寻址读取的 Boot 镜像文件路径；解析完成后文件即被关闭。</para></param>
    /// <exception cref="ImageParseException">The file is shorter than the magic or version field, or the first 8 bytes are not <c>ANDROID!</c>.
    /// <para>文件长度不足魔数或版本字段所在范围，或前 8 字节不是 <c>ANDROID!</c>。</para></exception>
    public void ParseFile(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, FileOptions.SequentialScan);


        var headerBuf = new byte[2048];
        var read = fs.Read(headerBuf, 0, headerBuf.Length);
        if (read < 8)
        {
            throw new ImageParseException($"文件太小，不是有效的 Boot 镜像: {read} 字节");
        }

        var magic = Encoding.ASCII.GetString(headerBuf, 0, 8);
        if (magic != BootMagic)
        {
            throw new ImageParseException($"无效的 Boot 镜像 magic: {magic}");
        }

        Header = new BootImageHeader();
        if (read < 44)
        {
            throw new ImageParseException($"文件太小，无法读取头部版本字段: {read} 字节");
        }

        var headerVersionField = headerBuf.ReadUInt32LE(40);
        if (headerVersionField <= 2)
        {
            ParseV0V2(headerBuf, headerVersionField, fs);
        }
        else if (headerVersionField == 3)
        {
            ParseV3(headerBuf, fs, fs.Length);
        }
        else if (headerVersionField == 4)
        {
            ParseV4(headerBuf, fs, fs.Length);
        }
        else
        {
            throw new ImageParseException($"Unsupported Android boot image header version: {headerVersionField}.");
        }

        DetectCompression();
    }

    /// <summary>
    /// Parses a boot image from a byte array.
    /// <para>从字节数组解析 Boot 镜像。</para>
    /// </summary>
    /// <param name="data">
    /// Full image bytes beginning with the <c>ANDROID!</c> magic; a header version field greater than <c>4</c> is parsed as version 0.
    /// <para>以 <c>ANDROID!</c> 魔数开头的完整镜像字节；头部版本字段大于 <c>4</c> 时按版本 0 解析。</para></param>
    /// <exception cref="ImageParseException">The array is shorter than the magic or version field, or the first 8 bytes are not <c>ANDROID!</c>.
    /// <para>数组长度不足魔数或版本字段所在范围，或前 8 字节不是 <c>ANDROID!</c>。</para></exception>
    public void Parse(byte[] data)
    {
        if (data.Length < 8)
        {
            throw new ImageParseException($"文件太小，不是有效的 Boot 镜像: {data.Length} 字节");
        }

        var magic = Encoding.ASCII.GetString(data, 0, 8);
        if (magic != BootMagic)
        {
            throw new ImageParseException($"无效的 Boot 镜像 magic: {magic}");
        }

        Header = new BootImageHeader();
        if (data.Length < 44)
        {
            throw new ImageParseException($"文件太小，无法读取头部版本字段: {data.Length} 字节");
        }

        var headerVersionField = data.ReadUInt32LE(40);
        using var ms = new MemoryStream(data, writable: false);
        if (headerVersionField <= 2)
        {
            ParseV0V2(data, headerVersionField, ms);
        }
        else if (headerVersionField == 3)
        {
            ParseV3(data, ms, data.Length);
        }
        else if (headerVersionField == 4)
        {
            ParseV4(data, ms, data.Length);
        }
        else
        {
            throw new ImageParseException($"Unsupported Android boot image header version: {headerVersionField}.");
        }

        DetectCompression();
    }

    private void DetectCompression()
    {
        if (Kernel.Length > 0)
        {
            Header.KernelCompression = BootCompressor.Detect(Kernel);
        }

        if (Ramdisk.Length > 0)
        {
            Header.RamdiskCompression = BootCompressor.Detect(Ramdisk);
        }
    }

    /// <summary>
    /// Resolves the partition role of the parsed image from its header version, the emptiness of the kernel and
    /// ramdisk segments, and an optional partition-name hint.
    /// <para>依据头部版本、kernel 与 ramdisk 区段的空性以及可选的分区名提示，解析已加载镜像的分区角色。</para>
    /// </summary>
    /// <param name="partitionName">Partition-name hint such as <c>init_boot</c> or <c>recovery_a</c>;
    /// <see langword="null"/> disables the hint path and falls back to the content heuristic alone.
    /// <para>分区名提示（如 <c>init_boot</c>、<c>recovery_a</c>）；为 <see langword="null"/> 时仅使用内容启发式。</para></param>
    /// <returns>The detected <see cref="BootImageRole"/>; see <see cref="BootImageRoleResolver.Detect"/> for the
    /// precedence rules.
    /// <para>检测出的 <see cref="BootImageRole"/>；优先级规则见 <see cref="BootImageRoleResolver.Detect"/>。</para></returns>
    public BootImageRole DetectRole(string? partitionName = null)
        => BootImageRoleResolver.Detect(Header.HeaderVersion, Header.KernelSize, Header.RamdiskSize, partitionName);

    private void ParseV0V2(byte[] data, uint headerVersion)
    {
        Header.HeaderVersion = headerVersion switch
        {
            0 => BootImageVersion.V0,
            1 => BootImageVersion.V1,
            2 => BootImageVersion.V2,
            _ => BootImageVersion.V0
        };

        Header.KernelSize = data.ReadUInt32LE(8);
        Header.KernelAddr = data.ReadUInt32LE(12);
        Header.RamdiskSize = data.ReadUInt32LE(16);
        Header.RamdiskAddr = data.ReadUInt32LE(20);
        Header.SecondSize = data.ReadUInt32LE(24);
        Header.SecondAddr = data.ReadUInt32LE(28);
        Header.TagsAddr = data.ReadUInt32LE(32);
        Header.PageSize = data.ReadUInt32LE(36);
        Header.OsVersion = data.ReadUInt32LE(44);
        Header.ImageName = ReadString(data, 48, 16).TrimEnd('\0');
        var commandLine = new byte[1536];
        var first = data.AsSpan(64, 512);
        var extra = data.AsSpan(608, 1024);
        var firstLength = first.IndexOf((byte)0);
        var extraLength = extra.IndexOf((byte)0);
        if (firstLength < 0) firstLength = first.Length;
        if (extraLength < 0) extraLength = extra.Length;
        first[..firstLength].CopyTo(commandLine);
        extra[..extraLength].CopyTo(commandLine.AsSpan(firstLength));
        Header.CommandLine = Encoding.UTF8.GetString(commandLine, 0, firstLength + extraLength);
        Header.ProductName = Header.ImageName;

        for (var i = 0; i < 8; i++)
        {
            Header.Ids[i] = data.ReadUInt32LE(576 + (i * 4));
        }

        if (headerVersion >= 1 && data.Length >= 1644)
        {
            Header.RecoveryDtboSize = data.ReadUInt32LE(1632);
            Header.RecoveryDtboOffset = data.ReadUInt64LE(1636);
        }

        if (headerVersion >= 2 && data.Length >= 1660)
        {
            Header.DtbSize = data.ReadUInt32LE(1648);
            Header.KernelLoadAddr = data.ReadUInt64LE(1652);
        }

        Header.PageSize = Header.PageSize > 0 ? Header.PageSize : 2048;
        Header.HeaderSize = headerVersion switch
        {
            0 => 1632,
            1 => 1648,
            2 => 1664,
            _ => 1632
        };
    }

    private void ParseV0V2(byte[] headerBuf, uint headerVersion, Stream stream)
    {
        ParseV0V2(headerBuf, headerVersion);
        ExtractComponentsV0V2Stream(stream);
    }

    private void ParseV3(byte[] data)
    {
        Header.HeaderVersion = BootImageVersion.V3;
        Header.KernelSize = data.ReadUInt32LE(8);
        Header.RamdiskSize = data.ReadUInt32LE(12);
        Header.OsVersion = data.ReadUInt32LE(16);
        Header.HeaderSize = Math.Max(data.ReadUInt32LE(20), CanonicalV3HeaderSize);
        Header.CommandLine = ReadString(data, 44, 1536).TrimEnd('\0');
        Header.PageSize = 4096;
        Header.SecondSize = 0;
        Header.DtbSize = 0;
        Header.RecoveryDtboSize = 0;
    }

    private void ParseV3(byte[] headerBuf, Stream stream, long imageLength)
    {
        ParseV3(headerBuf);
        ApplyV3V4PageLayout(stream, imageLength, isV4: false);
        ExtractComponentsV3Stream(stream);
    }

    private void ParseV4(byte[] data)
    {
        Header.HeaderVersion = BootImageVersion.V4;
        Header.KernelSize = data.ReadUInt32LE(8);
        Header.RamdiskSize = data.ReadUInt32LE(12);
        Header.OsVersion = data.ReadUInt32LE(16);
        Header.HeaderSize = Math.Max(data.ReadUInt32LE(20), CanonicalV4HeaderSize);
        Header.CommandLine = ReadString(data, 44, 1536).TrimEnd('\0');
        Header.PageSize = 4096;
        Header.SecondSize = 0;
        Header.DtbSize = 0;
        Header.RecoveryDtboSize = 0;

        if (data.Length >= 1584)
        {
            Header.SignatureSize = data.ReadUInt32LE(1580);
        }
    }

    private void ParseV4(byte[] headerBuf, Stream stream, long imageLength)
    {
        ParseV4(headerBuf);
        ApplyV3V4PageLayout(stream, imageLength, isV4: true);
        ExtractComponentsV3Stream(stream);

        if (Header.SignatureSize > 0)
        {
            var pageSize = Header.PageSize;
            var kernelOffset = AlignToPage(Header.HeaderSize, pageSize);
            var ramdiskOffset = AlignToPage(kernelOffset + Header.KernelSize, pageSize);
            var signatureOffset = AlignToPage(ramdiskOffset + Header.RamdiskSize, pageSize);

            Signature = ReadSegment(stream, signatureOffset, Header.SignatureSize);
        }
    }

    /// <summary>
    /// Infers the page size for version 3/4 images (whose header does not store it), normalizes the declared header
    /// size to at least the canonical size for the version, and captures the raw header block for byte-identical
    /// repacking.
    /// <para>为版本 3/4 镜像（头部不存页大小）推断页大小，将声明的头部大小规范化为不小于该版本的规范尺寸，
    /// 并捕获原始头部块以支持逐字节一致的重打包。</para>
    /// </summary>
    /// <param name="image">Seekable image stream; the header block is re-read from its start.
    /// <para>可查找的镜像流；头部块从其起始处重新读取。</para></param>
    /// <param name="imageLength">Total image length in bytes used as the alignment evidence.
    /// <para>作为对齐证据的镜像总长度（字节）。</para></param>
    /// <param name="isV4">Whether the parsed header is version 4; version 4 contributes its signature size to the
    /// layout evidence and field capture.
    /// <para>是否为版本 4；版本 4 的签名大小参与布局证据与字段捕获。</para></param>
    private void ApplyV3V4PageLayout(Stream image, long imageLength, bool isV4)
    {
        var canonicalSize = isV4 ? CanonicalV4HeaderSize : CanonicalV3HeaderSize;
        var effectiveHeaderSize = Math.Max(Header.HeaderSize, canonicalSize);
        if (effectiveHeaderSize > MaximumHeaderBlobLength)
        {
            throw new ImageParseException(
                $"Boot image header size exceeds the supported bound: {effectiveHeaderSize}.");
        }

        Header.HeaderSize = effectiveHeaderSize;
        var signatureSize = isV4 ? Header.SignatureSize : 0u;
        var pageSize = DetectV3V4PageSize(effectiveHeaderSize, Header.KernelSize, Header.RamdiskSize, signatureSize, imageLength);
        if (image.CanSeek && Header.RamdiskSize > 0)
        {
            pageSize = RefinePageSizeByContent(image, effectiveHeaderSize, Header.KernelSize, Header.RamdiskSize, pageSize, imageLength);
        }

        Header.PageSize = pageSize;

        var alignedHeaderLength = ((long)effectiveHeaderSize + pageSize - 1) / pageSize * pageSize;
        if (image.CanSeek && alignedHeaderLength <= imageLength && alignedHeaderLength <= MaximumHeaderBlobLength)
        {
            image.Seek(0, SeekOrigin.Begin);
            var blob = new byte[(int)alignedHeaderLength];
            image.ReadExactly(blob);
            Header.HeaderBlob = blob;
        }
    }

    /// <summary>
    /// Infers the page size of a version 3 or 4 boot image, whose header stores no page size, by probing the
    /// candidate sizes against the image length and the declared section sizes.
    /// <para>V3/V4 头部不存储页大小；本方法以候选页大小对照镜像长度与声明的区段大小进行推断。</para>
    /// </summary>
    /// <param name="headerSize">Effective (canonical-clamped) header size in bytes.
    /// <para>已按规范尺寸钳制后的头部大小（字节）。</para></param>
    /// <param name="kernelSize">Declared kernel section size; <c>0</c> contributes no aligned block.
    /// <para>声明的 kernel 区段大小；为 <c>0</c> 时不贡献对齐块。</para></param>
    /// <param name="ramdiskSize">Declared ramdisk section size; <c>0</c> contributes no aligned block.
    /// <para>声明的 ramdisk 区段大小；为 <c>0</c> 时不贡献对齐块。</para></param>
    /// <param name="signatureSize">Declared version 4 signature size; pass <c>0</c> for version 3.
    /// <para>声明的 V4 签名大小；版本 3 传 <c>0</c>。</para></param>
    /// <param name="imageLength">Total image length in bytes.
    /// <para>镜像总长度（字节）。</para></param>
    /// <returns>The largest candidate in {16384, 8192, 4096, 2048} whose fully page-aligned layout equals the image
    /// length; failing that, the largest candidate whose layout fits within and divides the image length exactly;
    /// otherwise the default <c>4096</c>.
    /// <para>返回页对齐总长恰好等于镜像长度的最大候选（集合 {16384, 8192, 4096, 2048}）；否则返回布局可容纳且能整除
    /// 镜像长度的最大候选；再否则返回默认值 <c>4096</c>。</para></returns>
    internal static uint DetectV3V4PageSize(uint headerSize, uint kernelSize, uint ramdiskSize, uint signatureSize, long imageLength)
    {
        foreach (var candidate in V3V4PageSizeCandidates)
        {
            if (headerSize <= candidate && ComputePackedLength(candidate) == imageLength)
            {
                return candidate;
            }
        }

        foreach (var candidate in V3V4PageSizeCandidates)
        {
            if (headerSize <= candidate && imageLength % candidate == 0 && ComputePackedLength(candidate) <= imageLength)
            {
                return candidate;
            }
        }

        return 4096;

        long ComputePackedLength(uint pageSize)
        {
            var length = AlignUp(headerSize, pageSize);
            if (kernelSize > 0)
            {
                length += AlignUp(kernelSize, pageSize);
            }

            if (ramdiskSize > 0)
            {
                length += AlignUp(ramdiskSize, pageSize);
            }

            if (signatureSize > 0)
            {
                length += AlignUp(signatureSize, pageSize);
            }

            return length;
        }
    }

    private static long AlignUp(uint value, uint pageSize) => ((long)value + pageSize - 1) / pageSize * pageSize;

    /// <summary>
    /// Refines an arithmetic page-size guess with content evidence for version 3/4 images.
    /// <para>用内容证据校正 V3/V4 镜像的算术页大小推断。</para>
    /// </summary>
    /// <param name="image">Seekable image stream. <para>可寻址的镜像流。</para></param>
    /// <param name="headerSize">Effective (canonical-clamped) header size in bytes. <para>已钳制的头部大小（字节）。</para></param>
    /// <param name="kernelSize">Declared kernel section size. <para>声明的 kernel 区段大小。</para></param>
    /// <param name="ramdiskSize">Declared ramdisk section size; must be non-zero. <para>声明的 ramdisk 区段大小；必须非零。</para></param>
    /// <param name="arithmeticPageSize">Result of <see cref="DetectV3V4PageSize"/>. <para><see cref="DetectV3V4PageSize"/> 的推断结果。</para></param>
    /// <param name="imageLength">Total image length in bytes. <para>镜像总长度（字节）。</para></param>
    /// <returns>
    /// <paramref name="arithmeticPageSize"/> when its own ramdisk offset already carries a recognizable kernel/ramdisk
    /// container signature or no smaller candidate does; otherwise the largest candidate whose ramdisk content is a
    /// known container.
    /// <para>当算术推断自身对应偏移的 ramdisk 已是可识别容器、或没有更小候选命中时返回
    /// <paramref name="arithmeticPageSize"/>；否则返回 ramdisk 内容为已知容器的最大候选。</para>
    /// </returns>
    private static uint RefinePageSizeByContent(Stream image, uint headerSize, uint kernelSize, uint ramdiskSize, uint arithmeticPageSize, long imageLength)
    {
        if (ContentHasContainerSignature(image, headerSize, kernelSize, ramdiskSize, arithmeticPageSize, imageLength))
        {
            return arithmeticPageSize;
        }

        foreach (var candidate in V3V4PageSizeCandidates)
        {
            if (candidate == arithmeticPageSize)
            {
                continue;
            }

            if (candidate < headerSize)
            {
                break;
            }

            if (ContentHasContainerSignature(image, headerSize, kernelSize, ramdiskSize, candidate, imageLength))
            {
                return candidate;
            }
        }

        return arithmeticPageSize;
    }

    /// <summary>
    /// Checks whether the ramdisk section of a candidate page size starts with a recognizable kernel/ramdisk container signature.
    /// <para>检查某个候选页大小下 ramdisk 区段的起始字节是否为可识别的内核/ramdisk 容器魔数。</para>
    /// </summary>
    /// <param name="image">Seekable image stream. <para>可寻址的镜像流。</para></param>
    /// <param name="headerSize">Effective header size in bytes. <para>已钳制的头部大小（字节）。</para></param>
    /// <param name="kernelSize">Declared kernel section size. <para>声明的 kernel 区段大小。</para></param>
    /// <param name="ramdiskSize">Declared ramdisk section size. <para>声明的 ramdisk 区段大小。</para></param>
    /// <param name="pageSize">Candidate page size to evaluate. <para>要评估的候选页大小。</para></param>
    /// <param name="imageLength">Total image length in bytes. <para>镜像总长度（字节）。</para></param>
    /// <returns>
    /// <see langword="true"/> when the ramdisk section is in range and its leading bytes match a known container signature.
    /// <para>ramdisk 区段在镜像范围内且起始字节匹配已知容器魔数时为 <see langword="true"/>。</para>
    /// </returns>
    private static bool ContentHasContainerSignature(Stream image, uint headerSize, uint kernelSize, uint ramdiskSize, uint pageSize, long imageLength)
    {
        long AlignPage(long value, uint page) => (value + page - 1) / page * page;
        var kernelOffset = AlignPage(headerSize, pageSize);
        var ramdiskOffset = AlignPage(kernelOffset + kernelSize, pageSize);
        if (ramdiskOffset > imageLength || ramdiskSize > imageLength - ramdiskOffset)
        {
            return false;
        }

        var available = (int)Math.Min(ramdiskSize, imageLength - ramdiskOffset);
        image.Position = ramdiskOffset;
        Span<byte> probe = stackalloc byte[8];
        var read = image.ReadAtLeast(probe, Math.Min(6, available), throwOnEndOfStream: false);
        return HasRecognizableContainerSignature(probe[..read]);
    }

    /// <summary>
    /// Recognizes the leading magic of the container layouts used for boot kernel/ramdisk components.
    /// <para>识别 boot 内核/ramdisk 组件所用容器布局的首部魔数。</para>
    /// </summary>
    /// <param name="probe">Leading bytes of a component; a shorter probe is checked against the signatures that fit.
    /// <para>组件的起始字节；更短的探测按能容纳的签名逐一比对。</para></param>
    /// <returns>
    /// <see langword="true"/> for CPIO (newc/crc/odc), gzip, LZ4 frame, LZ4 legacy, Zstd, XZ, BZip2, Brotli, raw LZMA or ELF magic.
    /// <para>命中 CPIO（newc/crc/odc）、gzip、LZ4 帧、LZ4 传统、Zstd、XZ、BZip2、Brotli、原始 LZMA 或 ELF 魔数时为 <see langword="true"/>。</para>
    /// </returns>
    private static bool HasRecognizableContainerSignature(ReadOnlySpan<byte> probe)
    {
        if (probe.Length >= 6 && probe[..6].SequenceEqual("070701"u8)) return true;
        if (probe.Length >= 6 && probe[..6].SequenceEqual("070702"u8)) return true;
        if (probe.Length >= 6 && probe[..6].SequenceEqual("070707"u8)) return true;
        if (probe.Length >= 2 && probe[0] == 0x1F && probe[1] == 0x8B) return true;
        if (probe.Length >= 4 && probe[0] == 0x04 && probe[1] == 0x22 && probe[2] == 0x4D && probe[3] == 0x18) return true;
        if (probe.Length >= 4 && probe[0] == 0x02 && probe[1] == 0x21 && probe[2] == 0x4C && probe[3] == 0x18) return true;
        if (probe.Length >= 4 && probe[0] == 0x28 && probe[1] == 0xB5 && probe[2] == 0x2F && probe[3] == 0xFD) return true;
        if (probe.Length >= 6 && probe[0] == 0xFD && probe[1] == 0x37 && probe[2] == 0x7A && probe[3] == 0x58 && probe[4] == 0x5A && probe[5] == 0x00) return true;
        if (probe.Length >= 3 && probe[0] == 0x42 && probe[1] == 0x5A && probe[2] == 0x68) return true;
        if (probe.Length >= 2 && probe[0] == 0x0B && probe[1] == 0x0A) return true;
        if (probe.Length >= 3 && probe[0] == 0x5D && probe[1] == 0x00 && probe[2] == 0x00) return true;
        if (probe.Length >= 4 && probe[0] == 0x7F && probe[1] == 0x45 && probe[2] == 0x4C && probe[3] == 0x46) return true;
        return false;
    }

    /// <summary>
    /// Streaming V0/V2 component extraction by offset from stream.
    /// <para>流式提取 V0/V2 组件：从 stream 按偏移分段读取 kernel/ramdisk/second/dtb/dtbo。</para>
    /// </summary>
    private void ExtractComponentsV0V2Stream(Stream stream)
    {
        var pageSize = Header.PageSize;
        var kernelOffset = AlignToPage(Header.HeaderSize, pageSize);
        var ramdiskOffset = AlignToPage(kernelOffset + Header.KernelSize, pageSize);
        var secondOffset = AlignToPage(ramdiskOffset + Header.RamdiskSize, pageSize);

        Kernel = ReadSegment(stream, kernelOffset, Header.KernelSize);
        Ramdisk = Header.RamdiskSize > 0 ? ReadSegment(stream, ramdiskOffset, Header.RamdiskSize) : [];
        Second = Header.SecondSize > 0 ? ReadSegment(stream, secondOffset, Header.SecondSize) : [];

        if (Header.DtbSize > 0)
        {


            var afterSecond = secondOffset + Header.SecondSize;
            if (Header.RecoveryDtboSize > 0 && Header.RecoveryDtboOffset > 0)
            {
                afterSecond = checked((uint)Header.RecoveryDtboOffset + Header.RecoveryDtboSize);
            }
            var dtbOffset = AlignToPage(afterSecond, pageSize);
            Dtb = ReadSegment(stream, dtbOffset, Header.DtbSize);
        }

        if (Header.RecoveryDtboSize > 0)
        {
            var dtboOffset = (uint)Header.RecoveryDtboOffset;
            if (dtboOffset > 0)
            {
                RecoveryDtbo = ReadSegment(stream, dtboOffset, Header.RecoveryDtboSize);
            }
        }
    }

    /// <summary>
    /// Streaming V3/V4 component extraction by offset from stream.
    /// <para>流式提取 V3/V4 组件：kernel/ramdisk 从 stream 分段读。</para>
    /// </summary>
    private void ExtractComponentsV3Stream(Stream stream)
    {
        var pageSize = Header.PageSize;
        var kernelOffset = AlignToPage(Header.HeaderSize, pageSize);
        var ramdiskOffset = AlignToPage(kernelOffset + Header.KernelSize, pageSize);

        Kernel = ReadSegment(stream, kernelOffset, Header.KernelSize);
        Ramdisk = Header.RamdiskSize > 0 ? ReadSegment(stream, ramdiskOffset, Header.RamdiskSize) : [];
    }

    /// <summary>
    /// Reads a length-bounded segment from stream at the given offset without retaining the full image.
    /// <para>从 Stream 指定偏移读取指定长度区段（Seek + ReadExactly，不留全量驻留）。</para>
    /// </summary>
    private static byte[] ReadSegment(Stream stream, uint offset, uint size)
    {
        if (size == 0)
        {
            return [];
        }

        if ((long)offset > stream.Length || size > stream.Length - offset)
        {
            throw new ImageParseException(
                $"Boot image component range exceeds the input: offset={offset}, size={size}, length={stream.Length}.");
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[size];
        stream.ReadExactly(buf);
        return buf;
    }

    /// <summary>
    /// Calculates the SHA1 checksum.
    /// <para>获取 SHA1 校验和。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to hash; an empty array hashes to the empty-input SHA-1 value.
    /// <para>待哈希的字节；空数组得到空输入的 SHA-1 值。</para></param>
    /// <returns>The 20-byte SHA-1 digest rendered as 40 lowercase hexadecimal characters.
    /// <para>20 字节 SHA-1 摘要，呈现为 40 个小写十六进制字符。</para></returns>
    public static string CalculateSha1(byte[] data)
    {
        var hash = Sha1.ComputeHash(data);
        return Convert.ToHexStringLower(hash);
    }

    private static string ReadString(byte[] data, int offset, int length)
    {
        var actualLength = Math.Min(length, data.Length - offset);
        if (actualLength <= 0)
        {
            return string.Empty;
        }

        var bytes = new byte[actualLength];
        Array.Copy(data, offset, bytes, 0, actualLength);
        return Encoding.UTF8.GetString(bytes);
    }

    private static uint AlignToPage(uint offset, uint pageSize) => Alignment.Up(offset, pageSize);

    /// <summary>
    /// Releases all resources used by the parser.
    /// <para>释放资源。</para>
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Core method for releasing resources.<para>释放资源的核心方法。</para></summary>
    /// <param name="disposing"><see langword="true"/> to replace the parsed component buffers (<c>Kernel</c>, <c>Ramdisk</c>, <c>Second</c>, <c>Dtb</c>, <c>Extra</c>, <c>RecoveryDtbo</c> and <c>Signature</c>) with empty arrays; <see langword="false"/> only marks the instance as disposed. An already disposed instance returns immediately, and this parser has no finalizer, so <see cref="Dispose()"/> is the only caller.
    /// <para><see langword="true"/> 表示把已解析的组件缓冲区（<c>Kernel</c>、<c>Ramdisk</c>、<c>Second</c>、 <c>Dtb</c>、<c>Extra</c>、<c>RecoveryDtbo</c> 与 <c>Signature</c>）替换为空数组；<see langword="false"/> 仅将实例标记为已释放。已释放的实例会立即返回，且该解析器没有终结器，因此 <see cref="Dispose()"/> 是唯一调用方。</para></param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            Kernel = [];
            Ramdisk = [];
            Second = [];
            Dtb = [];
            Extra = [];
            RecoveryDtbo = [];
            Signature = [];
        }
        _disposed = true;
    }
}