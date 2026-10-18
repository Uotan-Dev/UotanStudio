using System.Text;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;

using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.BootSecurity.BootImage.Parser;

/// <summary>
/// Parses MTK boot images whose header starts with the little-endian magic <c>0x58881688</c> and spans 512 bytes,
/// extracting kernel, ramdisk, second and DTB sections at page-aligned offsets.
/// <para>解析以 MTK 小端魔数 <c>0x58881688</c> 开头、头部共 512 字节的 MTK Boot 镜像，并按页对齐偏移提取 kernel、ramdisk、second 与 DTB 区段。</para>
/// </summary>
public class MtkBootParser : IDisposable
{
    private const uint MtkMagic = 0x58881688;
    private const int MtkHeaderSize = 512;
    private const int MtkNameSize = 32;
    private bool _disposed;

    /// <summary>Header fields filled by the most recent successful parse.
    /// <para>由最近一次成功解析填充的头部字段。</para></summary>
    /// <value>Never <see langword="null"/>; each successful parse replaces the instance entirely.
    /// <para>永不为 <see langword="null"/>；每次成功解析都会整体替换实例。</para></value>
    public MtkHeader Header { get; private set; } = new MtkHeader();

    /// <summary>Raw kernel section extracted from the image.
    /// <para>从镜像提取的原始 kernel 区段。</para></summary>
    /// <value>Bytes exactly as stored (compressed or plain); empty when the section is absent or its computed range
    /// falls outside the input.
    /// <para>按镜像中原样返回（压缩或未压缩）；区段缺失或计算出的范围超出输入时为空。</para></value>
    public byte[] Kernel { get; private set; } = [];

    /// <summary>Raw ramdisk section extracted from the image.
    /// <para>从镜像提取的原始 ramdisk 区段。</para></summary>
    /// <value>Bytes exactly as stored; empty when the section is absent or its computed range falls outside the input.
    /// <para>按镜像中原样返回；区段缺失或计算出的范围超出输入时为空。</para></value>
    public byte[] Ramdisk { get; private set; } = [];

    /// <summary>Raw second-stage bootloader section extracted from the image.
    /// <para>从镜像提取的原始 second 阶段引导区段。</para></summary>
    /// <value>Bytes exactly as stored; empty when the section is absent or its computed range falls outside the input.
    /// <para>按镜像中原样返回；区段缺失或计算出的范围超出输入时为空。</para></value>
    public byte[] Second { get; private set; } = [];

    /// <summary>Raw DTB section extracted from the image.
    /// <para>从镜像提取的原始 DTB 区段。</para></summary>
    /// <value>Bytes exactly as stored; empty when the section is absent or its computed range falls outside the input.
    /// <para>按镜像中原样返回；区段缺失或计算出的范围超出输入时为空。</para></value>
    public byte[] Dtb { get; private set; } = [];

    /// <summary>
    /// Checks if the data is in MTK format.
    /// <para>检查数据是否为 MTK 格式。</para>
    /// </summary>
    /// <param name="data">Candidate image bytes; <see langword="null"/> is accepted.
    /// <para>候选镜像字节；允许传入 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when <paramref name="data"/> has at least 8 bytes and its first little-endian
    /// <c>uint32</c> equals the MTK magic <c>0x58881688</c>.
    /// <para>当 <paramref name="data"/> 至少 8 字节且前 4 字节按小端解释等于 MTK 魔数 <c>0x58881688</c> 时返回 <see langword="true"/>。</para></returns>
    public static bool IsMtkFormat(byte[] data) => data != null && data.Length >= 8 && ReadUInt32LE(data, 0) == MtkMagic;

    /// <summary>
    /// Checks if the data is in MTK compressed format.
    /// <para>检查数据是否为 MTK 压缩格式。</para>
    /// </summary>
    /// <param name="data">Candidate compressed payload; <see langword="null"/> is accepted.
    /// <para>待检测的压缩数据；允许传入 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when <paramref name="data"/> has at least 4 bytes and its first little-endian
    /// <c>uint32</c> equals the MTK magic <c>0x58881688</c>.
    /// <para>当 <paramref name="data"/> 至少 4 字节且前 4 字节按小端解释等于 MTK 魔数 <c>0x58881688</c> 时返回 <see langword="true"/>。</para></returns>
    public static bool IsMtkCompressed(byte[] data) => data != null && data.Length >= 4 && ReadUInt32LE(data, 0) == MtkMagic;

    /// <summary>
    /// Parses an MTK boot image from a file.
    /// <para>从文件解析 MTK Boot 镜像。</para>
    /// </summary>
    /// <param name="filePath">Image path, opened read-only with <c>FileShare.Read</c> and closed before returning.
    /// <para>镜像文件路径，以 <c>FileShare.Read</c> 只读方式打开，返回前即关闭。</para></param>
    /// <exception cref="ImageParseException">The file is shorter than the 512-byte header or its magic does not match.
    /// <para>文件小于 512 字节头部或魔数不匹配。</para></exception>
    /// <exception cref="FileNotFoundException">The file does not exist.
    /// <para>文件不存在。</para></exception>
    public void ParseFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Parse(stream);
    }

    /// <summary>
    /// Parses an MTK boot image from a stream using streaming reads.
    /// <para>从流解析 MTK Boot 镜像（流式读取，避免一次性加载整个文件）。</para>
    /// </summary>
    /// <param name="stream">Readable, seekable stream positioned at the start of the image; it stays open and is left
    /// at the end of the last component read.
    /// <para>定位在镜像起始处、可读且可寻址的流；解析后保持打开，位置停在最后一次组件读取之后。</para></param>
    /// <exception cref="ImageParseException">The stream is shorter than the 512-byte header or its magic does not match.
    /// <para>流长度小于 512 字节头部或魔数不匹配。</para></exception>
    /// <exception cref="NotSupportedException">The stream does not support reading or seeking.
    /// <para>流不支持读取或寻址。</para></exception>
    public void Parse(Stream stream)
    {
        if (stream.Length < MtkHeaderSize)
        {
            throw new ImageParseException($"文件太小，不是有效的 MTK Boot 镜像: {stream.Length} 字节");
        }

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        var magic = reader.ReadUInt32();
        if (magic != MtkMagic)
        {
            throw new ImageParseException($"无效的 MTK Boot 镜像 magic: 0x{magic:X8}");
        }

        Header = new MtkHeader
        {
            Name = ReadStringFromStream(reader, MtkNameSize).TrimEnd('\0')
        };
        Header.HeaderName = Header.Name;
        Header.Size = reader.ReadUInt32();
        Header.KernelSize = reader.ReadUInt32();
        Header.KernelOffset = reader.ReadUInt32();
        Header.RamdiskSize = reader.ReadUInt32();
        Header.RamdiskOffset = reader.ReadUInt32();
        Header.SecondSize = reader.ReadUInt32();
        Header.SecondOffset = reader.ReadUInt32();
        Header.TagsAddr = reader.ReadUInt32();
        Header.PageSize = reader.ReadUInt32();
        Header.HeaderVersion = reader.ReadUInt32();
        Header.KernelAddr = reader.ReadUInt32();
        Header.RamdiskAddr = reader.ReadUInt32();
        Header.SecondAddr = reader.ReadUInt32();
        Header.DtbSize = reader.ReadUInt32();
        Header.DtbOffset = reader.ReadUInt32();

        ExtractComponentsFromStream(stream);
    }

    /// <summary>
    /// Parses an MTK boot image from a byte array.
    /// <para>从字节数组解析 MTK Boot 镜像。</para>
    /// </summary>
    /// <param name="data">Complete image bytes, at least 512 bytes; read in place without copying the whole input.
    /// <para>完整镜像字节，至少 512 字节；原地读取，不复制整个输入。</para></param>
    /// <exception cref="ImageParseException">The array is shorter than the 512-byte header or its magic does not match.
    /// <para>数组小于 512 字节头部或魔数不匹配。</para></exception>
    /// <exception cref="NullReferenceException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    public void Parse(byte[] data)
    {
        if (data.Length < MtkHeaderSize)
        {
            throw new ImageParseException($"文件太小，不是有效的 MTK Boot 镜像: {data.Length} 字节");
        }

        var magic = ReadUInt32LE(data, 0);
        if (magic != MtkMagic)
        {
            throw new ImageParseException($"无效的 MTK Boot 镜像 magic: 0x{magic:X8}");
        }

        Header = new MtkHeader
        {
            Name = ReadString(data, 4, MtkNameSize).TrimEnd('\0')
        };
        Header.HeaderName = Header.Name;
        Header.Size = ReadUInt32LE(data, 36);
        Header.KernelSize = ReadUInt32LE(data, 40);
        Header.KernelOffset = ReadUInt32LE(data, 44);
        Header.RamdiskSize = ReadUInt32LE(data, 48);
        Header.RamdiskOffset = ReadUInt32LE(data, 52);
        Header.SecondSize = ReadUInt32LE(data, 56);
        Header.SecondOffset = ReadUInt32LE(data, 60);
        Header.TagsAddr = ReadUInt32LE(data, 64);
        Header.PageSize = ReadUInt32LE(data, 68);
        Header.HeaderVersion = ReadUInt32LE(data, 72);
        Header.KernelAddr = ReadUInt32LE(data, 76);
        Header.RamdiskAddr = ReadUInt32LE(data, 80);
        Header.SecondAddr = ReadUInt32LE(data, 84);
        Header.DtbSize = ReadUInt32LE(data, 88);
        Header.DtbOffset = ReadUInt32LE(data, 92);

        ExtractComponents(data);
    }

    /// <summary>
    /// Decompresses MTK compressed kernel data.
    /// <para>解压 MTK 压缩的内核数据。</para>
    /// </summary>
    /// <param name="data">Kernel section bytes; <see langword="null"/> is accepted.
    /// <para>kernel 区段字节；允许传入 <see langword="null"/>。</para></param>
    /// <returns>The input unchanged when it is <see langword="null"/>/empty, shorter than 4 bytes, or still carries the
    /// MTK magic; otherwise the decompressed payload, falling back to the input when <see cref="BootCompressor"/> sees
    /// no compression magic or decompression fails.
    /// <para>输入为 <see langword="null"/>/空、不足 4 字节或仍带 MTK 魔数时原样返回；否则返回解压后的数据，当 <see cref="BootCompressor"/> 未识别到压缩魔数或解压失败时回退为输入本身。</para></returns>
    public static byte[] DecompressKernel(byte[] data)
    {
        return data == null || data.Length < 4
            ? data ?? []
            : ReadUInt32LE(data, 0) == MtkMagic ? data : BootCompressor.DecompressOrReturn(data);
    }

    /// <summary>
    /// Detects the compression format of MTK data.
    /// <para>检测 MTK 数据的压缩格式。</para>
    /// </summary>
    /// <param name="data">Candidate compressed bytes; <see langword="null"/> is accepted.
    /// <para>待检测的候选字节；允许传入 <see langword="null"/>。</para></param>
    /// <returns><see cref="CompressionType.None"/> when the input is <see langword="null"/>, shorter than 4 bytes, or
    /// still carries the MTK magic; otherwise the result of <see cref="BootCompressor.Detect(byte[])"/>.
    /// <para>输入为 <see langword="null"/>、不足 4 字节或仍带 MTK 魔数时返回 <see cref="CompressionType.None"/>；否则返回 <see cref="BootCompressor.Detect(byte[])"/> 的结果。</para></returns>
    public static CompressionType DetectCompression(byte[] data)
    {
        return data == null || data.Length < 4
            ? CompressionType.None
            : ReadUInt32LE(data, 0) == MtkMagic ? CompressionType.None : BootCompressor.Detect(data);
    }

    private void ExtractComponents(byte[] data)
    {
        var pageSize = Header.PageSize > 0 ? Header.PageSize : 2048;

        if (Header.KernelSize > 0 && Header.KernelOffset > 0)
        {
            var kernelOffset = AlignToPage(MtkHeaderSize + Header.KernelOffset, pageSize);
            if (kernelOffset + Header.KernelSize <= data.Length)
            {
                Kernel = new byte[Header.KernelSize];
                Array.Copy(data, kernelOffset, Kernel, 0, Header.KernelSize);
            }
        }

        if (Header.RamdiskSize > 0 && Header.RamdiskOffset > 0)
        {
            var ramdiskOffset = AlignToPage(MtkHeaderSize + Header.RamdiskOffset, pageSize);
            if (ramdiskOffset + Header.RamdiskSize <= data.Length)
            {
                Ramdisk = new byte[Header.RamdiskSize];
                Array.Copy(data, ramdiskOffset, Ramdisk, 0, Header.RamdiskSize);
            }
        }

        if (Header.SecondSize > 0 && Header.SecondOffset > 0)
        {
            var secondOffset = AlignToPage(MtkHeaderSize + Header.SecondOffset, pageSize);
            if (secondOffset + Header.SecondSize <= data.Length)
            {
                Second = new byte[Header.SecondSize];
                Array.Copy(data, secondOffset, Second, 0, Header.SecondSize);
            }
        }

        if (Header.DtbSize > 0 && Header.DtbOffset > 0)
        {
            var dtbOffset = AlignToPage(MtkHeaderSize + Header.DtbOffset, pageSize);
            if (dtbOffset + Header.DtbSize <= data.Length)
            {
                Dtb = new byte[Header.DtbSize];
                Array.Copy(data, dtbOffset, Dtb, 0, Header.DtbSize);
            }
        }
    }

    private void ExtractComponentsFromStream(Stream stream)
    {
        var pageSize = Header.PageSize > 0 ? Header.PageSize : 2048;
        var fileLength = (uint)stream.Length;

        if (Header.KernelSize > 0 && Header.KernelOffset > 0)
        {
            var kernelOffset = AlignToPage(MtkHeaderSize + Header.KernelOffset, pageSize);
            if (kernelOffset + Header.KernelSize <= fileLength)
            {
                Kernel = new byte[Header.KernelSize];
                stream.Seek(kernelOffset, SeekOrigin.Begin);
                stream.ReadExactly(Kernel);
            }
        }

        if (Header.RamdiskSize > 0 && Header.RamdiskOffset > 0)
        {
            var ramdiskOffset = AlignToPage(MtkHeaderSize + Header.RamdiskOffset, pageSize);
            if (ramdiskOffset + Header.RamdiskSize <= fileLength)
            {
                Ramdisk = new byte[Header.RamdiskSize];
                stream.Seek(ramdiskOffset, SeekOrigin.Begin);
                stream.ReadExactly(Ramdisk);
            }
        }

        if (Header.SecondSize > 0 && Header.SecondOffset > 0)
        {
            var secondOffset = AlignToPage(MtkHeaderSize + Header.SecondOffset, pageSize);
            if (secondOffset + Header.SecondSize <= fileLength)
            {
                Second = new byte[Header.SecondSize];
                stream.Seek(secondOffset, SeekOrigin.Begin);
                stream.ReadExactly(Second);
            }
        }

        if (Header.DtbSize > 0 && Header.DtbOffset > 0)
        {
            var dtbOffset = AlignToPage(MtkHeaderSize + Header.DtbOffset, pageSize);
            if (dtbOffset + Header.DtbSize <= fileLength)
            {
                Dtb = new byte[Header.DtbSize];
                stream.Seek(dtbOffset, SeekOrigin.Begin);
                stream.ReadExactly(Dtb);
            }
        }
    }

    private static uint ReadUInt32LE(byte[] data, int offset) => offset + 4 > data.Length ? 0 : data.ReadUInt32LE(offset);

    private static string ReadString(byte[] data, int offset, int length)
    {
        var actualLength = Math.Min(length, data.Length - offset);
        if (actualLength <= 0)
        {
            return string.Empty;
        }

        var bytes = new byte[actualLength];
        Array.Copy(data, offset, bytes, 0, actualLength);
        return Encoding.ASCII.GetString(bytes);
    }

    private static string ReadStringFromStream(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        return Encoding.ASCII.GetString(bytes);
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
    /// <param name="disposing"><see langword="true"/> to replace the parsed component buffers (<c>Kernel</c>, <c>Ramdisk</c>, <c>Second</c> and <c>Dtb</c>) with empty arrays; <see langword="false"/> only marks the instance as disposed. An already disposed instance returns immediately, and this parser has no finalizer, so <see cref="Dispose()"/> is the only caller.
    /// <para><see langword="true"/> 表示把已解析的组件缓冲区（<c>Kernel</c>、<c>Ramdisk</c>、<c>Second</c> 与 <c>Dtb</c>）替换为空数组；<see langword="false"/> 仅将实例标记为已释放。已释放的实例会立即返回， 且该解析器没有终结器，因此 <see cref="Dispose()"/> 是唯一调用方。</para></param>
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
        }
        _disposed = true;
    }
}

/// <summary>
/// Fixed-layout header of an MTK boot image: a 32-byte ASCII name at offset 4 followed by little-endian
/// <c>uint32</c> fields that <see cref="MtkBootParser"/> reads at offsets 36 through 92.
/// <para>MTK Boot 镜像的固定布局头部：偏移 4 处为 32 字节 ASCII 名称，随后是 <see cref="MtkBootParser"/> 在偏移 36 至 92 读取的小端 <c>uint32</c> 字段。</para>
/// </summary>
public class MtkHeader
{
    /// <summary>Image name read from bytes 4 through 35 with trailing NUL characters trimmed.
    /// <para>从字节 4 至 35 读取并去除尾部 NUL 的镜像名称。</para></summary>
    /// <value>Empty string when the name field is all NUL.
    /// <para>名称字段全为 NUL 时为空字符串。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>Mirror of <see cref="Name"/> assigned by the parser after each successful read.
    /// <para>解析器每次成功读取后赋值为 <see cref="Name"/> 的镜像字段。</para></summary>
    /// <value>Always equal to <see cref="Name"/> on headers produced by <see cref="MtkBootParser"/>.
    /// <para>由 <see cref="MtkBootParser"/> 生成的头部中始终等于 <see cref="Name"/>。</para></value>
    public string HeaderName { get; set; } = string.Empty;

    /// <summary>Raw size field stored at header offset 36.
    /// <para>存于头部偏移 36 的原始 size 字段。</para></summary>
    /// <value>Value as read; component extraction does not consult it.
    /// <para>按读取原值保存；区段提取不会引用该字段。</para></value>
    public uint Size { get; set; }

    /// <summary>Kernel section size in bytes (header offset 40).
    /// <para>kernel 区段字节数（头部偏移 40）。</para></summary>
    /// <value><c>0</c> means no kernel section is extracted.
    /// <para>为 <c>0</c> 时不提取 kernel 区段。</para></value>
    public uint KernelSize { get; set; }

    /// <summary>Kernel section offset field (header offset 44), relative to the end of the 512-byte header and
    /// page-aligned before extraction.
    /// <para>kernel 区段偏移字段（头部偏移 44），相对 512 字节头部末尾，提取前先按页对齐。</para></summary>
    /// <value><c>0</c> means no kernel section is extracted.
    /// <para>为 <c>0</c> 时不提取 kernel 区段。</para></value>
    public uint KernelOffset { get; set; }

    /// <summary>Device memory load address of the kernel (header offset 76).
    /// <para>kernel 在设备内存中的加载地址（头部偏移 76）。</para></summary>
    /// <value>Address as stored; the parser performs no validation.
    /// <para>按存储原值保存；解析器不做校验。</para></value>
    public uint KernelAddr { get; set; }

    /// <summary>Ramdisk section size in bytes (header offset 48).
    /// <para>ramdisk 区段字节数（头部偏移 48）。</para></summary>
    /// <value><c>0</c> means no ramdisk section is extracted.
    /// <para>为 <c>0</c> 时不提取 ramdisk 区段。</para></value>
    public uint RamdiskSize { get; set; }

    /// <summary>Ramdisk section offset field (header offset 52), relative to the end of the 512-byte header and
    /// page-aligned before extraction.
    /// <para>ramdisk 区段偏移字段（头部偏移 52），相对 512 字节头部末尾，提取前先按页对齐。</para></summary>
    /// <value><c>0</c> means no ramdisk section is extracted.
    /// <para>为 <c>0</c> 时不提取 ramdisk 区段。</para></value>
    public uint RamdiskOffset { get; set; }

    /// <summary>Device memory load address of the ramdisk (header offset 80).
    /// <para>ramdisk 在设备内存中的加载地址（头部偏移 80）。</para></summary>
    /// <value>Address as stored; the parser performs no validation.
    /// <para>按存储原值保存；解析器不做校验。</para></value>
    public uint RamdiskAddr { get; set; }

    /// <summary>Second-stage section size in bytes (header offset 56).
    /// <para>second 阶段区段字节数（头部偏移 56）。</para></summary>
    /// <value><c>0</c> means no second-stage section is extracted.
    /// <para>为 <c>0</c> 时不提取 second 阶段区段。</para></value>
    public uint SecondSize { get; set; }

    /// <summary>Second-stage section offset field (header offset 60), relative to the end of the 512-byte header and
    /// page-aligned before extraction.
    /// <para>second 阶段区段偏移字段（头部偏移 60），相对 512 字节头部末尾，提取前先按页对齐。</para></summary>
    /// <value><c>0</c> means no second-stage section is extracted.
    /// <para>为 <c>0</c> 时不提取 second 阶段区段。</para></value>
    public uint SecondOffset { get; set; }

    /// <summary>Device memory load address of the second-stage bootloader (header offset 84).
    /// <para>second 阶段引导程序在设备内存中的加载地址（头部偏移 84）。</para></summary>
    /// <value>Address as stored; the parser performs no validation.
    /// <para>按存储原值保存；解析器不做校验。</para></value>
    public uint SecondAddr { get; set; }

    /// <summary>Device memory address of the ATAG list (header offset 64).
    /// <para>ATAG 列表在设备内存中的地址（头部偏移 64）。</para></summary>
    /// <value>Address as stored; the parser performs no validation.
    /// <para>按存储原值保存；解析器不做校验。</para></value>
    public uint TagsAddr { get; set; }

    /// <summary>Page size used to align section offsets (header offset 68).
    /// <para>用于对齐区段偏移的页大小（头部偏移 68）。</para></summary>
    /// <value>Raw field value; extraction treats <c>0</c> as 2048 bytes.
    /// <para>按原始字段值保存；区段提取把 <c>0</c> 视为 2048 字节。</para></value>
    public uint PageSize { get; set; }

    /// <summary>Raw header version field stored at header offset 72.
    /// <para>存于头部偏移 72 的原始版本字段。</para></summary>
    /// <value>Value as read; extraction does not branch on it.
    /// <para>按读取原值保存；区段提取不依据它分支。</para></value>
    public uint HeaderVersion { get; set; }

    /// <summary>DTB section size in bytes (header offset 88).
    /// <para>DTB 区段字节数（头部偏移 88）。</para></summary>
    /// <value><c>0</c> means no DTB section is extracted.
    /// <para>为 <c>0</c> 时不提取 DTB 区段。</para></value>
    public uint DtbSize { get; set; }

    /// <summary>DTB section offset field (header offset 92), relative to the end of the 512-byte header and
    /// page-aligned before extraction.
    /// <para>DTB 区段偏移字段（头部偏移 92），相对 512 字节头部末尾，提取前先按页对齐。</para></summary>
    /// <value><c>0</c> means no DTB section is extracted.
    /// <para>为 <c>0</c> 时不提取 DTB 区段。</para></value>
    public uint DtbOffset { get; set; }
}