using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Samsung PXA Boot 镜像解析器。
/// <para>
/// 负责解析三星（Samsung）设备的 PXA 专用 boot 镜像格式。</para>
/// <para>
/// Parses Samsung PXA-specific boot image formats.</para>
/// </summary>
public class SamsungBootParser : IDisposable
{
    private const string BootMagic = "ANDROID!";
    private const int SamsungHeaderSize = 1664;
    private bool _disposed;

    /// <summary>
    /// Samsung PXA 镜像头部信息。
    /// <para>The Samsung PXA image header information.</para>
    /// </summary>
    public SamsungHeader Header { get; private set; } = new SamsungHeader();

    /// <summary>
    /// 获取内核数据。
    /// <para>Gets the kernel data.</para>
    /// </summary>
    public byte[] Kernel { get; private set; } = [];

    /// <summary>
    /// 获取 RAMDisk 数据。
    /// <para>Gets the ramdisk data.</para>
    /// </summary>
    public byte[] Ramdisk { get; private set; } = [];

    /// <summary>
    /// 获取第二阶段引导程序数据。
    /// <para>Gets the second stage loader data.</para>
    /// </summary>
    public byte[] Second { get; private set; } = [];

    /// <summary>
    /// 获取 DTB 数据。
    /// <para>Gets the DTB data.</para>
    /// </summary>
    public byte[] Dtb { get; private set; } = [];

    /// <summary>
    /// 获取额外数据。
    /// <para>Gets the extra data.</para>
    /// </summary>
    public byte[] Extra { get; private set; } = [];

    /// <summary>
    /// 检查数据是否为 Samsung PXA 格式。
    /// <para>Checks if the data is in Samsung PXA format.</para>
    /// </summary>
    /// <param name="data">待检测的数据。<para>The data to check.</para></param>
    /// <returns>如果是 Samsung PXA 格式则为 true。<para>True if Samsung PXA format.</para></returns>
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

        // Samsung PXA 在头部偏移 40 存放 extra 段大小（通常大于 4），而非 header version
        var headerOrExtra = BitConverter.ToUInt32(data, 40);
        return headerOrExtra > 4;
    }

    /// <summary>
    /// 从文件解析 Samsung PXA Boot 镜像。
    /// <para>Parses a Samsung PXA boot image from a file.</para>
    /// </summary>
    /// <param name="filePath">镜像文件路径。<para>The image file path.</para></param>
    /// <exception cref="ImageParseException">镜像格式无效时抛出。<para>Thrown when the image format is invalid.</para></exception>
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
    /// 从字节数组解析 Samsung PXA Boot 镜像。
    /// <para>Parses a Samsung PXA boot image from a byte array.</para>
    /// </summary>
    /// <param name="data">镜像数据。<para>The image data.</para></param>
    /// <exception cref="ImageParseException">镜像格式无效时抛出。<para>Thrown when the image format is invalid.</para></exception>
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
    /// 解析 Samsung boot 镜像的 extra 数据块。
    /// <para>Parses the extra data block in Samsung boot images.</para>
    /// </summary>
    /// <param name="data">镜像数据。<para>The image data.</para></param>
    /// <returns>Extra 数据块。<para>The extra data block.</para></returns>
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
    /// 释放资源。
    /// <para>Releases all resources used by the parser.</para>
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放资源的核心方法。
    /// <para>Core method for releasing resources.</para>
    /// </summary>
    /// <param name="disposing">是否释放托管资源。<para>Whether to release managed resources.</para></param>
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
/// Samsung PXA 镜像头部信息。
/// <para>Samsung PXA image header information.</para>
/// </summary>
public class SamsungHeader
{
    /// <summary>
    /// 内核大小。
    /// <para>The kernel size.</para>
    /// </summary>
    public uint KernelSize { get; set; }

    /// <summary>
    /// 内核地址。
    /// <para>The kernel address.</para>
    /// </summary>
    public uint KernelAddr { get; set; }

    /// <summary>
    /// RAMDisk 大小。
    /// <para>The ramdisk size.</para>
    /// </summary>
    public uint RamdiskSize { get; set; }

    /// <summary>
    /// RAMDisk 地址。
    /// <para>The ramdisk address.</para>
    /// </summary>
    public uint RamdiskAddr { get; set; }

    /// <summary>
    /// 第二阶段大小。
    /// <para>The second stage size.</para>
    /// </summary>
    public uint SecondSize { get; set; }

    /// <summary>
    /// 第二阶段地址。
    /// <para>The second stage address.</para>
    /// </summary>
    public uint SecondAddr { get; set; }

    /// <summary>
    /// 标签地址。
    /// <para>The tags address.</para>
    /// </summary>
    public uint TagsAddr { get; set; }

    /// <summary>
    /// 页大小。
    /// <para>The page size.</para>
    /// </summary>
    public uint PageSize { get; set; }

    /// <summary>
    /// 头部版本。
    /// <para>The header version.</para>
    /// </summary>
    public uint HeaderVersion { get; set; }

    /// <summary>
    /// 是否有额外数据。
    /// <para>Whether there is extra data.</para>
    /// </summary>
    public bool HasExtra { get; set; }

    /// <summary>
    /// 额外数据大小。
    /// <para>The extra data size.</para>
    /// </summary>
    public uint ExtraSize { get; set; }

    /// <summary>
    /// 额外数据偏移量。
    /// <para>The extra data offset.</para>
    /// </summary>
    public uint ExtraOffset { get; set; }

    /// <summary>
    /// 操作系统版本。
    /// <para>The OS version.</para>
    /// </summary>
    public uint OsVersion { get; set; }

    /// <summary>
    /// 产品名称。
    /// <para>The product name.</para>
    /// </summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>
    /// 命令行参数。
    /// <para>The command line.</para>
    /// </summary>
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>
    /// 额外命令行参数。
    /// <para>The extra command line.</para>
    /// </summary>
    public string ExtraCmdLine { get; set; } = string.Empty;

    /// <summary>
    /// ID 数组。
    /// <para>The ID array.</para>
    /// </summary>
    public uint[] Ids { get; set; } = new uint[8];

    /// <summary>
    /// Recovery DTBO 大小。
    /// <para>The recovery DTBO size.</para>
    /// </summary>
    public uint RecoveryDtboSize { get; set; }

    /// <summary>
    /// Recovery DTBO 偏移量。
    /// <para>The recovery DTBO offset.</para>
    /// </summary>
    public ulong RecoveryDtboOffset { get; set; }

    /// <summary>
    /// 内核加载地址。
    /// <para>The kernel load address.</para>
    /// </summary>
    public ulong KernelLoadAddr { get; set; }
}