using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;

/// <summary>
/// Parses Samsung PXA boot images: a little-endian header (up to 1680 bytes) followed by
/// page-aligned kernel, ramdisk, second-stage and optional extra blocks.
/// <para>解析 Samsung PXA Boot 镜像：最长 1680 字节的小端头部，其后是按页对齐的 kernel、ramdisk、second 与可选 extra 块。</para>
/// </summary>
/// <remarks>
/// Component extraction skips any region whose declared range exceeds the supplied data instead of failing.
/// <para>组件提取时，声明范围超出输入数据的区域会被跳过而不报错。</para>
/// </remarks>
public class SamsungBootParser : IDisposable
{
    private const string BootMagic = "ANDROID!";
    private const int SamsungHeaderSize = 1664;
    private bool _disposed;

    /// <summary>
    /// The parsed Samsung boot header.
    /// <para>已解析的 Samsung Boot 头部。</para>
    /// </summary>
    /// <value>
    /// A zero-initialized header before parsing; a successful header read replaces the whole instance.
    /// <para>解析前为全零默认头部；每次成功读取头部都会整体替换实例。</para>
    /// </value>
    public SamsungHeader Header { get; private set; } = new SamsungHeader();

    /// <summary>
    /// Kernel payload bytes copied from the page-aligned region after the header.
    /// <para>从头部之后页对齐区域复制出的 kernel 字节。</para>
    /// </summary>
    /// <value>
    /// Empty when nothing was parsed yet or the declared range exceeds the input; otherwise exactly
    /// <see cref="SamsungHeader.KernelSize"/> bytes. Cleared to empty by <see cref="Dispose()"/>.
    /// <para>尚未解析或声明范围超出输入时为空；否则恰为 <see cref="SamsungHeader.KernelSize"/> 字节。<see cref="Dispose()"/> 会将其清空。</para>
    /// </value>
    public byte[] Kernel { get; private set; } = [];

    /// <summary>
    /// Ramdisk payload bytes copied from the page-aligned region after the kernel.
    /// <para>从 kernel 之后页对齐区域复制出的 ramdisk 字节。</para>
    /// </summary>
    /// <value>
    /// Empty when nothing was parsed yet, the declared size is <c>0</c>, or the range exceeds the input;
    /// otherwise exactly <see cref="SamsungHeader.RamdiskSize"/> bytes. Cleared to empty by <see cref="Dispose()"/>.
    /// <para>尚未解析、声明大小为 <c>0</c> 或范围超出输入时为空；否则恰为 <see cref="SamsungHeader.RamdiskSize"/> 字节。<see cref="Dispose()"/> 会将其清空。</para>
    /// </value>
    public byte[] Ramdisk { get; private set; } = [];

    /// <summary>
    /// Second-stage payload bytes copied from the page-aligned region after the ramdisk.
    /// <para>从 ramdisk 之后页对齐区域复制出的 second 阶段字节。</para>
    /// </summary>
    /// <value>
    /// Empty when nothing was parsed yet, the declared size is <c>0</c>, or the range exceeds the input;
    /// otherwise exactly <see cref="SamsungHeader.SecondSize"/> bytes. Cleared to empty by <see cref="Dispose()"/>.
    /// <para>尚未解析、声明大小为 <c>0</c> 或范围超出输入时为空；否则恰为 <see cref="SamsungHeader.SecondSize"/> 字节。<see cref="Dispose()"/> 会将其清空。</para>
    /// </value>
    public byte[] Second { get; private set; } = [];

    /// <summary>
    /// DTB payload placeholder for the Samsung PXA layout.
    /// <para>Samsung PXA 布局下 DTB 字节的占位属性。</para>
    /// </summary>
    /// <value>
    /// Always empty: the Samsung PXA parsing paths do not populate a standalone DTB region.
    /// <para>始终为空：Samsung PXA 解析流程不会填充独立的 DTB 区域。</para>
    /// </value>
    public byte[] Dtb { get; private set; } = [];

    /// <summary>
    /// Extra header block bytes copied during component extraction.
    /// <para>组件提取阶段复制出的 extra 头部块字节。</para>
    /// </summary>
    /// <value>
    /// Empty unless <see cref="SamsungHeader.ExtraSize"/> is non-zero and the calculated range fits the input;
    /// otherwise <see cref="ParseExtra"/> provides the block from a raw buffer instead.
    /// <para>除非 <see cref="SamsungHeader.ExtraSize"/> 非零且计算范围落在输入内，否则为空；原始缓冲区方式可改用 <see cref="ParseExtra"/> 获取该块。</para>
    /// </value>
    public byte[] Extra { get; private set; } = [];

    /// <summary>
    /// Checks if the data is in Samsung PXA format.
    /// <para>检查数据是否为 Samsung PXA 格式。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate image bytes. <see langword="null"/> or fewer than 44 bytes is reported as a non-match.
    /// <para>候选镜像字节。<see langword="null"/> 或少于 44 字节时按不匹配处理。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the bytes carry the <c>ANDROID!</c> magic and the little-endian word at
    /// offset 40 exceeds <c>4</c>, which marks an extra block size instead of a header version.
    /// <para>当字节带有 <c>ANDROID!</c> 魔数且偏移 40 处的小端字大于 <c>4</c>（表示 extra 块大小而非头部版本号）时返回 <see langword="true"/>。</para>
    /// </returns>
    public static bool IsSamsungFormat(byte[] data)
    {
        if (data == null || data.Length < 44)
        {
            return false;
        }

        var magic = Encoding.ASCII.GetString(data, 0, 8);
        if (magic != BootMagic)
        {
            return false;
        }


        var headerOrExtra = BitConverter.ToUInt32(data, 40);
        return headerOrExtra > 4;
    }

    /// <summary>
    /// Parses a Samsung PXA boot image from a file.
    /// <para>从文件解析 Samsung PXA Boot 镜像。</para>
    /// </summary>
    /// <param name="filePath">
    /// Existing file opened for shared reading; at most the first 1680 bytes are buffered for the header and
    /// components are streamed from the same handle, which is closed before returning.
    /// <para>以共享读方式打开的已存在文件；头部最多缓存前 1680 字节，其余组件从同一句柄流式读取，句柄在返回前关闭。</para>
    /// </param>
    /// <exception cref="ImageParseException">
    /// The file is shorter than 8 bytes or its first 8 bytes are not the <c>ANDROID!</c> magic.
    /// <para>文件不足 8 字节，或前 8 字节不是 <c>ANDROID!</c> 魔数。</para>
    /// </exception>
    public void ParseFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var headerBufSize = (int)Math.Min(stream.Length, 1680);
        var headerBuf = new byte[headerBufSize];
        stream.ReadExactly(headerBuf);
        ParseHeader(headerBuf);
        ExtractComponentsFromStream(stream);
    }

    /// <summary>
    /// Parses a Samsung PXA boot image from a byte array.
    /// <para>从字节数组解析 Samsung PXA Boot 镜像。</para>
    /// </summary>
    /// <param name="data">
    /// Complete image bytes to read in place; the array is not copied and header fields beyond its length
    /// fall back to <c>0</c> instead of throwing.
    /// <para>就地读取的完整镜像字节；数组不会被复制，超出其长度的头部字段回退为 <c>0</c> 而非抛出异常。</para>
    /// </param>
    /// <exception cref="ImageParseException">
    /// <paramref name="data"/> is shorter than 8 bytes or does not start with the <c>ANDROID!</c> magic.
    /// <para><paramref name="data"/> 不足 8 字节，或开头不是 <c>ANDROID!</c> 魔数。</para>
    /// </exception>
    public void Parse(byte[] data)
    {
        ParseHeader(data);
        ExtractComponents(data);
    }

    private void ParseHeader(byte[] data)
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

        Header = new SamsungHeader
        {
            KernelSize = ReadUInt32LE(data, 8),
            KernelAddr = ReadUInt32LE(data, 12),
            RamdiskSize = ReadUInt32LE(data, 16),
            RamdiskAddr = ReadUInt32LE(data, 20),
            SecondSize = ReadUInt32LE(data, 24),
            SecondAddr = ReadUInt32LE(data, 28),
            TagsAddr = ReadUInt32LE(data, 32),
            PageSize = ReadUInt32LE(data, 36)
        };

        var headerOrExtra = ReadUInt32LE(data, 40);
        if (headerOrExtra == 0)
        {
            Header.HeaderVersion = 0;
            Header.HasExtra = false;
        }
        else if (headerOrExtra <= 4)
        {
            Header.HeaderVersion = headerOrExtra;
            Header.HasExtra = false;
        }
        else
        {
            Header.HeaderVersion = 0;
            Header.ExtraSize = headerOrExtra;
            Header.HasExtra = true;
        }

        Header.OsVersion = ReadUInt32LE(data, 44);
        Header.ProductName = ReadString(data, 48, 16).TrimEnd('\0');
        Header.CommandLine = ReadString(data, 64, 512).TrimEnd('\0');

        for (var i = 0; i < 8; i++)
        {
            Header.Ids[i] = ReadUInt32LE(data, 576 + (i * 4));
        }

        Header.ExtraCmdLine = ReadString(data, 608, 1024).TrimEnd('\0');

        if (Header.HasExtra && data.Length >= 1664)
        {
            Header.RecoveryDtboSize = ReadUInt32LE(data, 1632);
            Header.RecoveryDtboOffset = ReadUInt64LE(data, 1636);
        }

        if (data.Length >= 1680)
        {
            Header.KernelLoadAddr = ReadUInt64LE(data, 1652);
        }
    }

    /// <summary>
    /// Parses the extra data block in Samsung boot images.
    /// <para>解析 Samsung boot 镜像的 extra 数据块。</para>
    /// </summary>
    /// <param name="data">
    /// Full image bytes; the block starts right after the fixed 1664-byte header region.
    /// <para>完整镜像字节；该块紧随固定的 1664 字节头部区域之后。</para>
    /// </param>
    /// <returns>
    /// An empty array when the parsed header has no extra block, <see cref="SamsungHeader.ExtraSize"/> is
    /// <c>0</c>, or <paramref name="data"/> is shorter than 1664 bytes; otherwise up to
    /// <see cref="SamsungHeader.ExtraSize"/> bytes, truncated at the end of <paramref name="data"/>.
    /// <para>当已解析头部没有 extra 块、<see cref="SamsungHeader.ExtraSize"/> 为 <c>0</c> 或 <paramref name="data"/> 短于 1664 字节时返回空数组；否则最多返回 <see cref="SamsungHeader.ExtraSize"/> 字节，并在 <paramref name="data"/> 末尾处截断。</para>
    /// </returns>
    public byte[] ParseExtra(byte[] data)
    {
        if (!Header.HasExtra || Header.ExtraSize == 0)
        {
            return [];
        }

        if (data.Length < SamsungHeaderSize)
        {
            return [];
        }

        var extra = new byte[Header.ExtraSize];
        Array.Copy(data, SamsungHeaderSize, extra, 0, Math.Min(Header.ExtraSize, (uint)(data.Length - SamsungHeaderSize)));
        return extra;
    }

    private void ExtractComponents(byte[] data)
    {
        var pageSize = Header.PageSize > 0 ? Header.PageSize : 2048;
        var headerSize = Header.HasExtra ? Header.ExtraSize : SamsungHeaderSize;

        var kernelOffset = AlignToPage(headerSize, pageSize);
        var ramdiskOffset = AlignToPage(kernelOffset + Header.KernelSize, pageSize);
        var secondOffset = AlignToPage(ramdiskOffset + Header.RamdiskSize, pageSize);

        if (kernelOffset + Header.KernelSize <= data.Length)
        {
            Kernel = new byte[Header.KernelSize];
            Array.Copy(data, kernelOffset, Kernel, 0, Header.KernelSize);
        }

        if (Header.RamdiskSize > 0 && ramdiskOffset + Header.RamdiskSize <= data.Length)
        {
            Ramdisk = new byte[Header.RamdiskSize];
            Array.Copy(data, ramdiskOffset, Ramdisk, 0, Header.RamdiskSize);
        }

        if (Header.SecondSize > 0 && secondOffset + Header.SecondSize <= data.Length)
        {
            Second = new byte[Header.SecondSize];
            Array.Copy(data, secondOffset, Second, 0, Header.SecondSize);
        }

        if (Header.ExtraSize > 0)
        {
            var extraOffset = AlignToPage(secondOffset + Header.SecondSize, pageSize);
            if (extraOffset + Header.ExtraSize <= data.Length)
            {
                Extra = new byte[Header.ExtraSize];
                Array.Copy(data, extraOffset, Extra, 0, Header.ExtraSize);
            }
        }
    }

    private void ExtractComponentsFromStream(Stream stream)
    {
        var pageSize = Header.PageSize > 0 ? Header.PageSize : 2048;
        var headerSize = Header.HasExtra ? Header.ExtraSize : SamsungHeaderSize;

        var kernelOffset = AlignToPage(headerSize, pageSize);
        var ramdiskOffset = AlignToPage(kernelOffset + Header.KernelSize, pageSize);
        var secondOffset = AlignToPage(ramdiskOffset + Header.RamdiskSize, pageSize);

        var fileLength = stream.Length;

        if (Header.KernelSize > 0 && kernelOffset + Header.KernelSize <= fileLength)
        {
            stream.Seek(kernelOffset, SeekOrigin.Begin);
            Kernel = new byte[Header.KernelSize];
            stream.ReadExactly(Kernel);
        }

        if (Header.RamdiskSize > 0 && ramdiskOffset + Header.RamdiskSize <= fileLength)
        {
            stream.Seek(ramdiskOffset, SeekOrigin.Begin);
            Ramdisk = new byte[Header.RamdiskSize];
            stream.ReadExactly(Ramdisk);
        }

        if (Header.SecondSize > 0 && secondOffset + Header.SecondSize <= fileLength)
        {
            stream.Seek(secondOffset, SeekOrigin.Begin);
            Second = new byte[Header.SecondSize];
            stream.ReadExactly(Second);
        }

        if (Header.ExtraSize > 0)
        {
            var extraOffset = AlignToPage(secondOffset + Header.SecondSize, pageSize);
            if (extraOffset + Header.ExtraSize <= fileLength)
            {
                stream.Seek(extraOffset, SeekOrigin.Begin);
                Extra = new byte[Header.ExtraSize];
                stream.ReadExactly(Extra);
            }
        }
    }

    private static uint ReadUInt32LE(byte[] data, int offset) => offset + 4 > data.Length ? 0 : BitConverter.ToUInt32(data, offset);

    private static ulong ReadUInt64LE(byte[] data, int offset) => offset + 8 > data.Length ? 0 : BitConverter.ToUInt64(data, offset);

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
        }
        _disposed = true;
    }
}

/// <summary>
/// Fixed-layout header fields of a Samsung PXA boot image, all read little-endian from the first bytes.
/// <para>Samsung PXA Boot 镜像的固定布局头部字段，均按小端序从起始字节读取。</para>
/// </summary>
public class SamsungHeader
{
    /// <summary>Declared kernel payload size in bytes, read at offset 8. <para>偏移 8 处读取的 kernel 载荷声明大小（字节）。</para></summary>
    /// <value>Byte count; <c>0</c> when the field is absent or zero. <para>字节数；字段缺失或为零时为 <c>0</c>。</para></value>
    public uint KernelSize { get; set; }

    /// <summary>Declared kernel load address, read at offset 12. <para>偏移 12 处读取的 kernel 加载地址。</para></summary>
    /// <value>Physical target address as stored; <c>0</c> when the field is zero. <para>按原样存储的物理目标地址；字段为零时为 <c>0</c>。</para></value>
    public uint KernelAddr { get; set; }

    /// <summary>Declared ramdisk payload size in bytes, read at offset 16. <para>偏移 16 处读取的 ramdisk 载荷声明大小（字节）。</para></summary>
    /// <value>Byte count; <c>0</c> means no ramdisk region is extracted. <para>字节数；<c>0</c> 表示不提取 ramdisk 区域。</para></value>
    public uint RamdiskSize { get; set; }

    /// <summary>Declared ramdisk load address, read at offset 20. <para>偏移 20 处读取的 ramdisk 加载地址。</para></summary>
    /// <value>Physical target address as stored; <c>0</c> when the field is zero. <para>按原样存储的物理目标地址；字段为零时为 <c>0</c>。</para></value>
    public uint RamdiskAddr { get; set; }

    /// <summary>Declared second-stage payload size in bytes, read at offset 24. <para>偏移 24 处读取的 second 阶段载荷声明大小（字节）。</para></summary>
    /// <value>Byte count; <c>0</c> means no second-stage region is extracted. <para>字节数；<c>0</c> 表示不提取 second 阶段区域。</para></value>
    public uint SecondSize { get; set; }

    /// <summary>Declared second-stage load address, read at offset 28. <para>偏移 28 处读取的 second 阶段加载地址。</para></summary>
    /// <value>Physical target address as stored; <c>0</c> when the field is zero. <para>按原样存储的物理目标地址；字段为零时为 <c>0</c>。</para></value>
    public uint SecondAddr { get; set; }

    /// <summary>ATAGs (tags) load address, read at offset 32. <para>ATAG（tags）加载地址，偏移 32 处读取。</para></summary>
    /// <value>Physical address as stored; <c>0</c> when the field is zero. <para>按原样存储的物理地址；字段为零时为 <c>0</c>。</para></value>
    public uint TagsAddr { get; set; }

    /// <summary>Page alignment size in bytes, read at offset 36. <para>偏移 36 处读取的页对齐大小（字节）。</para></summary>
    /// <value>Alignment used to locate component regions; the extractor falls back to <c>2048</c> when this is <c>0</c>. <para>用于定位各组件区域的对齐值；为 <c>0</c> 时提取器按 <c>2048</c> 处理。</para></value>
    public uint PageSize { get; set; }

    /// <summary>Header version taken from the little-endian word at offset 40. <para>头部版本号，取自偏移 40 处的小端字。</para></summary>
    /// <value><c>1</c>–<c>4</c> when that word is between <c>1</c> and <c>4</c>; <c>0</c> when the word is <c>0</c> or is interpreted as <see cref="ExtraSize"/>. <para>该字位于 <c>1</c>–<c>4</c> 时即为其值；该字为 <c>0</c> 或被解释为 <see cref="ExtraSize"/> 时为 <c>0</c>。</para></value>
    public uint HeaderVersion { get; set; }

    /// <summary>Whether the word at offset 40 is read as an extra block size rather than a header version. <para>偏移 40 处的字是否被解释为 extra 块大小而非头部版本号。</para></summary>
    /// <value><see langword="true"/> when that word exceeds <c>4</c>. <para>该字大于 <c>4</c> 时为 <see langword="true"/>。</para></value>
    public bool HasExtra { get; set; }

    /// <summary>Declared extra block size, equal to the word at offset 40 when <see cref="HasExtra"/> is set. <para>extra 块声明大小：<see cref="HasExtra"/> 为真时即偏移 40 处的字。</para></summary>
    /// <value>Byte count of the block after the fixed header; <c>0</c> when there is no extra block. <para>固定头部之后块的字节数；无 extra 块时为 <c>0</c>。</para></value>
    public uint ExtraSize { get; set; }

    /// <summary>Declared extra block offset. <para>extra 块声明偏移。</para></summary>
    /// <value>Not populated by <see cref="SamsungBootParser"/>, which places the block right after the 1664-byte header region; stays <c>0</c>. <para><see cref="SamsungBootParser"/> 不填充该值（其块紧随 1664 字节头部区域之后）；保持 <c>0</c>。</para></value>
    public uint ExtraOffset { get; set; }

    /// <summary>Raw packed OS version word, read at offset 44. <para>偏移 44 处读取的原始打包 OS 版本字。</para></summary>
    /// <value>Unpacked value; the parser applies no decoding. <para>未经拆解的原始值；解析器不做解码。</para></value>
    public uint OsVersion { get; set; }

    /// <summary>Product name read as 16 ASCII bytes at offset 48. <para>以偏移 48 处的 16 个 ASCII 字节读取的产品名。</para></summary>
    /// <value>Trailing NUL characters trimmed; <see cref="string.Empty"/> when the field is empty or out of range. <para>已去除结尾 NUL；字段为空或超出范围时为 <see cref="string.Empty"/>。</para></value>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Main kernel command line read as 512 ASCII bytes at offset 64. <para>以偏移 64 处的 512 个 ASCII 字节读取的主内核命令行。</para></summary>
    /// <value>Trailing NUL characters trimmed; may be <see cref="string.Empty"/>. <para>已去除结尾 NUL；可能为 <see cref="string.Empty"/>。</para></value>
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>Additional command line read as 1024 ASCII bytes at offset 608. <para>以偏移 608 处的 1024 个 ASCII 字节读取的附加命令行。</para></summary>
    /// <value>Trailing NUL characters trimmed; may be <see cref="string.Empty"/>. <para>已去除结尾 NUL；可能为 <see cref="string.Empty"/>。</para></value>
    public string ExtraCmdLine { get; set; } = string.Empty;

    /// <summary>Eight little-endian ID words read from offset 576. <para>从偏移 576 处读取的 8 个小端 ID 字。</para></summary>
    /// <value>Always length <c>8</c>; entries the image does not carry stay <c>0</c>. <para>长度恒为 <c>8</c>；镜像未提供的条目保持 <c>0</c>。</para></value>
    public uint[] Ids { get; set; } = new uint[8];

    /// <summary>Recovery DTBO size, a little-endian word at offset 1632. <para>偏移 1632 处小端字读取的 Recovery DTBO 大小。</para></summary>
    /// <value>Only read when <see cref="HasExtra"/> is set and the input is at least 1664 bytes; otherwise <c>0</c>. <para>仅当 <see cref="HasExtra"/> 为真且输入不少于 1664 字节时读取；否则为 <c>0</c>。</para></value>
    public uint RecoveryDtboSize { get; set; }

    /// <summary>Recovery DTBO offset, a little-endian 64-bit value at offset 1636. <para>偏移 1636 处小端 64 位值读取的 Recovery DTBO 偏移。</para></summary>
    /// <value>Only read when <see cref="HasExtra"/> is set and the input is at least 1664 bytes; otherwise <c>0</c>. <para>仅当 <see cref="HasExtra"/> 为真且输入不少于 1664 字节时读取；否则为 <c>0</c>。</para></value>
    public ulong RecoveryDtboOffset { get; set; }

    /// <summary>64-bit kernel load address, a little-endian value at offset 1652. <para>偏移 1652 处小端 64 位值读取的内核加载地址。</para></summary>
    /// <value>Only read when the input is at least 1680 bytes; otherwise <c>0</c>. <para>仅当输入不少于 1680 字节时读取；否则为 <c>0</c>。</para></value>
    public ulong KernelLoadAddr { get; set; }
}