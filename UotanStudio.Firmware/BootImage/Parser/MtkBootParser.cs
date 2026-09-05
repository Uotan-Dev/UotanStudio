using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// MTK (MediaTek) Boot 镜像解析器。
/// <para>
/// 负责解析联发科（MTK）设备的专用 boot 镜像格式。</para>
/// <para>
/// Parses MediaTek-specific boot image formats.</para>
/// </summary>
public class MtkBootParser : IDisposable
{
    private const uint MtkMagic = 0x58881688;
    private const int MtkHeaderSize = 512;
    private const int MtkNameSize = 32;
    private bool _disposed;

    /// <summary>
    /// MTK 镜像头部信息。
    /// <para>The MTK image header information.</para>
    /// </summary>
    public MtkHeader Header { get; private set; } = new MtkHeader();

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
    /// 检查数据是否为 MTK 格式。
    /// <para>Checks if the data is in MTK format.</para>
    /// </summary>
    /// <param name="data">待检测的数据。<para>The data to check.</para></param>
    /// <returns>如果是 MTK 格式则为 true。<para>True if MTK format.</para></returns>
    public static bool IsMtkFormat(byte[] data) => data != null && data.Length >= 8 && ReadUInt32LE(data, 0) == MtkMagic;

    /// <summary>
    /// 检查数据是否为 MTK 压缩格式。
    /// <para>Checks if the data is in MTK compressed format.</para>
    /// </summary>
    /// <param name="data">待检测的数据。<para>The data to check.</para></param>
    /// <returns>如果是 MTK 压缩格式则为 true。<para>True if MTK compressed format.</para></returns>
    public static bool IsMtkCompressed(byte[] data) => data != null && data.Length >= 4 && ReadUInt32LE(data, 0) == MtkMagic;

    /// <summary>
    /// 从文件解析 MTK Boot 镜像。
    /// <para>Parses an MTK boot image from a file.</para>
    /// </summary>
    /// <param name="filePath">镜像文件路径。<para>The image file path.</para></param>
    /// <exception cref="ImageParseException">镜像格式无效时抛出。<para>Thrown when the image format is invalid.</para></exception>
    public void ParseFile(string filePath)
    {
        var data = File.ReadAllBytes(filePath);
        Parse(data);
    }

    /// <summary>
    /// 从字节数组解析 MTK Boot 镜像。
    /// <para>Parses an MTK boot image from a byte array.</para>
    /// </summary>
    /// <param name="data">镜像数据。<para>The image data.</para></param>
    /// <exception cref="ImageParseException">镜像格式无效时抛出。<para>Thrown when the image format is invalid.</para></exception>
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
    /// 解压 MTK 压缩的内核数据。
    /// <para>Decompresses MTK compressed kernel data.</para>
    /// </summary>
    /// <param name="data">压缩的数据。<para>The compressed data.</para></param>
    /// <returns>解压后的数据。<para>The decompressed data.</para></returns>
    public static byte[] DecompressKernel(byte[] data)
    {
        return data == null || data.Length < 4
            ? data ?? []
            : ReadUInt32LE(data, 0) == MtkMagic ? data : BootCompressor.DecompressOrReturn(data);
    }

    /// <summary>
    /// 检测 MTK 数据的压缩格式。
    /// <para>Detects the compression format of MTK data.</para>
    /// </summary>
    /// <param name="data">待检测的数据。<para>The data to check.</para></param>
    /// <returns>压缩类型。<para>The compression type.</para></returns>
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

    private static uint ReadUInt32LE(byte[] data, int offset) => offset + 4 > data.Length ? 0 : BitConverter.ToUInt32(data, offset);

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
        }
        _disposed = true;
    }
}

/// <summary>
/// MTK 镜像头部信息。
/// <para>MTK image header information.</para>
/// </summary>
public class MtkHeader
{
    /// <summary>
    /// 头部名称。
    /// <para>The header name.</para>
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 名称（32 字节）。
    /// <para>The name field (32 bytes).</para>
    /// </summary>
    public string HeaderName { get; set; } = string.Empty;

    /// <summary>
    /// 内容大小。
    /// <para>The content size.</para>
    /// </summary>
    public uint Size { get; set; }

    /// <summary>
    /// 内核大小。
    /// <para>The kernel size.</para>
    /// </summary>
    public uint KernelSize { get; set; }

    /// <summary>
    /// 内核偏移量。
    /// <para>The kernel offset.</para>
    /// </summary>
    public uint KernelOffset { get; set; }

    /// <summary>
    /// 内核加载地址。
    /// <para>The kernel load address.</para>
    /// </summary>
    public uint KernelAddr { get; set; }

    /// <summary>
    /// RAMDisk 大小。
    /// <para>The ramdisk size.</para>
    /// </summary>
    public uint RamdiskSize { get; set; }

    /// <summary>
    /// RAMDisk 偏移量。
    /// <para>The ramdisk offset.</para>
    /// </summary>
    public uint RamdiskOffset { get; set; }

    /// <summary>
    /// RAMDisk 加载地址。
    /// <para>The ramdisk load address.</para>
    /// </summary>
    public uint RamdiskAddr { get; set; }

    /// <summary>
    /// 第二阶段大小。
    /// <para>The second stage size.</para>
    /// </summary>
    public uint SecondSize { get; set; }

    /// <summary>
    /// 第二阶段偏移量。
    /// <para>The second stage offset.</para>
    /// </summary>
    public uint SecondOffset { get; set; }

    /// <summary>
    /// 第二阶段加载地址。
    /// <para>The second stage load address.</para>
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
    /// DTB 大小。
    /// <para>The DTB size.</para>
    /// </summary>
    public uint DtbSize { get; set; }

    /// <summary>
    /// DTB 偏移量。
    /// <para>The DTB offset.</para>
    /// </summary>
    public uint DtbOffset { get; set; }
}