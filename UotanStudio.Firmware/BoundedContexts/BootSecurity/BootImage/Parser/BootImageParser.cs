using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;

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
        var version = ReadUInt32LE(prefix, 40);
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
            if (version >= 1) parser.Header.HeaderSize = ReadUInt32LE(bytes, 1644);
        }
        else if (version == 3) parser.ParseV3(bytes);
        else parser.ParseV4(bytes);
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
            SharedKernel.FileExtraction.ValidateRange(offset, length, input.Length, "Boot component");
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

        var headerVersionField = ReadUInt32LE(headerBuf, 40);
        if (headerVersionField <= 2)
        {
            ParseV0V2(headerBuf, headerVersionField, fs);
        }
        else if (headerVersionField == 3)
        {
            ParseV3(headerBuf, fs);
        }
        else if (headerVersionField == 4)
        {
            ParseV4(headerBuf, fs);
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

        var headerVersionField = ReadUInt32LE(data, 40);
        using var ms = new MemoryStream(data, writable: false);
        if (headerVersionField <= 2)
        {
            ParseV0V2(data, headerVersionField, ms);
        }
        else if (headerVersionField == 3)
        {
            ParseV3(data, ms);
        }
        else if (headerVersionField == 4)
        {
            ParseV4(data, ms);
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

    private void ParseV0V2(byte[] data, uint headerVersion)
    {
        Header.HeaderVersion = headerVersion switch
        {
            0 => BootImageVersion.V0,
            1 => BootImageVersion.V1,
            2 => BootImageVersion.V2,
            _ => BootImageVersion.V0
        };

        Header.KernelSize = ReadUInt32LE(data, 8);
        Header.KernelAddr = ReadUInt32LE(data, 12);
        Header.RamdiskSize = ReadUInt32LE(data, 16);
        Header.RamdiskAddr = ReadUInt32LE(data, 20);
        Header.SecondSize = ReadUInt32LE(data, 24);
        Header.SecondAddr = ReadUInt32LE(data, 28);
        Header.TagsAddr = ReadUInt32LE(data, 32);
        Header.PageSize = ReadUInt32LE(data, 36);
        Header.OsVersion = ReadUInt32LE(data, 44);
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
            Header.Ids[i] = ReadUInt32LE(data, 576 + (i * 4));
        }

        if (headerVersion >= 1 && data.Length >= 1644)
        {
            Header.RecoveryDtboSize = ReadUInt32LE(data, 1632);
            Header.RecoveryDtboOffset = ReadUInt64LE(data, 1636);
        }

        if (headerVersion >= 2 && data.Length >= 1660)
        {
            Header.DtbSize = ReadUInt32LE(data, 1648);
            Header.KernelLoadAddr = ReadUInt64LE(data, 1652);
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
        Header.KernelSize = ReadUInt32LE(data, 8);
        Header.RamdiskSize = ReadUInt32LE(data, 12);
        Header.OsVersion = ReadUInt32LE(data, 16);
        Header.HeaderSize = ReadUInt32LE(data, 20);
        Header.CommandLine = ReadString(data, 44, 1536).TrimEnd('\0');
        Header.PageSize = 4096;
        Header.SecondSize = 0;
        Header.DtbSize = 0;
        Header.RecoveryDtboSize = 0;
    }

    private void ParseV3(byte[] headerBuf, Stream stream)
    {
        ParseV3(headerBuf);
        ExtractComponentsV3Stream(stream);
    }

    private void ParseV4(byte[] data)
    {
        Header.HeaderVersion = BootImageVersion.V4;
        Header.KernelSize = ReadUInt32LE(data, 8);
        Header.RamdiskSize = ReadUInt32LE(data, 12);
        Header.OsVersion = ReadUInt32LE(data, 16);
        Header.HeaderSize = ReadUInt32LE(data, 20);
        Header.CommandLine = ReadString(data, 44, 1536).TrimEnd('\0');
        Header.PageSize = 4096;
        Header.SecondSize = 0;
        Header.DtbSize = 0;
        Header.RecoveryDtboSize = 0;

        if (data.Length >= 1584)
        {
            Header.SignatureSize = ReadUInt32LE(data, 1580);
        }
    }

    private void ParseV4(byte[] headerBuf, Stream stream)
    {
        ParseV4(headerBuf);
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
        var hash = SHA1.HashData(data);
        return Convert.ToHexStringLower(hash);
    }

    private static uint ReadUInt32LE(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);

    private static ulong ReadUInt64LE(byte[] data, int offset) => BitConverter.ToUInt64(data, offset);

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

    private static uint AlignToPage(uint offset, uint pageSize) => (offset + pageSize - 1) & ~(pageSize - 1);

    /// <summary>
    /// Releases all resources used by the parser.
    /// <para>释放资源。</para>
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Core method for releasing resources.
    /// <para>释放资源的核心方法。</para>
    /// </summary>
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