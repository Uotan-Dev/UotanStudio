using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Android Vendor Boot 镜像解析器。
/// <para>
/// 负责解析、提取和验证 Android Vendor Boot 镜像文件，支持头部版本 V3 和 V4。</para>
/// <para>
/// Parses, extracts, and verifies Android vendor boot image files, supporting header versions V3 and V4.</para>
/// </summary>
public class VendorBootParser : IDisposable
{
    private const string VendorBootMagic = "VNDRBOOT";
    private const int V3HeaderSize = 2112;
    private const int V4HeaderSize = 2128;
    private const int VendorCmdlineSize = 2048;
    private const int RamdiskNameSize = 256;
    private const int RamdiskBoardIdCount = 16;
    private const int DefaultRamdiskTableEntrySize = 332;
    private bool _disposed;

    /// <summary>
    /// 解析后的 Vendor Boot 头部信息。
    /// <para>The parsed vendor boot header information.</para>
    /// </summary>
    public VendorBootHeader Header { get; private set; } = new VendorBootHeader();

    /// <summary>
    /// 获取 Vendor RAMDisk 数据。
    /// <para>Gets the vendor ramdisk data.</para>
    /// </summary>
    public byte[] VendorRamdisk { get; private set; } = [];

    /// <summary>
    /// 获取 DTB 数据。
    /// <para>Gets the DTB data.</para>
    /// </summary>
    public byte[] Dtb { get; private set; } = [];

    /// <summary>
    /// 获取 Bootconfig 数据（仅 V4）。
    /// <para>Gets the bootconfig data (V4 only).</para>
    /// </summary>
    public byte[] Bootconfig { get; private set; } = [];

    /// <summary>
    /// 获取 Vendor RAMDisk 表条目列表（仅 V4）。
    /// <para>Gets the vendor ramdisk table entry list (V4 only).</para>
    /// </summary>
    public List<VendorRamdiskTableEntry> RamdiskTable { get; } = [];

    /// <summary>
    /// 从文件解析 Vendor Boot 镜像。
    /// <para>Parses a vendor boot image from a file.</para>
    /// </summary>
    /// <param name="filePath">镜像文件路径。<para>The image file path.</para></param>
    /// <exception cref="ImageParseException">镜像格式无效时抛出。<para>Thrown when the image format is invalid.</para></exception>
    public void ParseFile(string filePath)
    {
        var data = File.ReadAllBytes(filePath);
        Parse(data);
    }

    /// <summary>
    /// 从字节数组解析 Vendor Boot 镜像。
    /// <para>Parses a vendor boot image from a byte array.</para>
    /// </summary>
    /// <param name="data">镜像数据。<para>The image data.</para></param>
    /// <exception cref="ImageParseException">镜像格式无效时抛出。<para>Thrown when the image format is invalid.</para></exception>
    public void Parse(byte[] data)
    {
        if (data.Length < 8)
        {
            throw new ImageParseException($"文件太小，不是有效的 Vendor Boot 镜像: {data.Length} 字节");
        }

        var magic = Encoding.ASCII.GetString(data, 0, 8);
        if (magic != VendorBootMagic)
        {
            throw new ImageParseException($"无效的 Vendor Boot 镜像 magic: {magic}");
        }

        Header = new VendorBootHeader();

        if (data.Length < 12)
        {
            throw new ImageParseException($"文件太小，无法读取头部版本字段: {data.Length} 字节");
        }

        var headerVersion = ReadUInt32LE(data, 8);
        Header.HeaderVersion = headerVersion;

        if (headerVersion == 3)
        {
            ParseV3(data);
        }
        else if (headerVersion == 4)
        {
            ParseV4(data);
        }
        else
        {
            throw new ImageParseException($"不支持的 Vendor Boot 镜像版本: {headerVersion}");
        }

        if (VendorRamdisk.Length > 0)
        {
            Header.VendorRamdiskCompression = BootCompressor.Detect(VendorRamdisk);
        }
    }

    private void ParseV3(byte[] data)
    {
        if (data.Length < 560)
        {
            throw new ImageParseException($"文件太小，无法读取 V3 Vendor Boot 头部: {data.Length} 字节");
        }

        Header.PageSize = ReadUInt32LE(data, 12);
        Header.KernelLoadAddress = ReadUInt32LE(data, 16);
        Header.RamdiskLoadAddress = ReadUInt32LE(data, 20);
        Header.VendorRamdiskSize = ReadUInt32LE(data, 24);
        Header.VendorCmdline = ReadString(data, 28, VendorCmdlineSize).TrimEnd('\0');
        Header.TagsLoadAddress = ReadUInt32LE(data, 2076);
        Header.VendorHeaderSize = ReadUInt32LE(data, 2096);
        Header.DtbSize = ReadUInt32LE(data, 2100);
        Header.DtbAddress = ReadUInt64LE(data, 2104);

        var headerSize = Header.VendorHeaderSize > 0 ? Header.VendorHeaderSize : V3HeaderSize;
        var pageSize = Header.PageSize > 0 ? Header.PageSize : 4096;

        ExtractComponents(data, headerSize, pageSize);
    }

    private void ParseV4(byte[] data)
    {
        if (data.Length < 2128)
        {
            throw new ImageParseException($"文件太小，无法读取 V4 Vendor Boot 头部: {data.Length} 字节");
        }

        Header.PageSize = ReadUInt32LE(data, 12);
        Header.KernelLoadAddress = ReadUInt32LE(data, 16);
        Header.RamdiskLoadAddress = ReadUInt32LE(data, 20);
        Header.VendorRamdiskSize = ReadUInt32LE(data, 24);
        Header.VendorCmdline = ReadString(data, 28, VendorCmdlineSize).TrimEnd('\0');
        Header.TagsLoadAddress = ReadUInt32LE(data, 2076);
        Header.VendorHeaderSize = ReadUInt32LE(data, 2096);
        Header.DtbSize = ReadUInt32LE(data, 2100);
        Header.DtbAddress = ReadUInt64LE(data, 2104);

        Header.VendorRamdiskTableSize = ReadUInt32LE(data, 2112);
        Header.VendorRamdiskTableEntryNum = ReadUInt32LE(data, 2116);
        Header.VendorRamdiskTableEntrySize = ReadUInt32LE(data, 2120);
        Header.VendorBootconfigSize = ReadUInt32LE(data, 2124);

        var headerSize = Header.VendorHeaderSize > 0 ? Header.VendorHeaderSize : V4HeaderSize;
        var pageSize = Header.PageSize > 0 ? Header.PageSize : 4096;

        // AOSP vendor_boot v4 头中没有 table 偏移字段，按布局计算：
        // header → vendor ramdisk → dtb → vendor ramdisk table，各块页对齐。
        var ramdiskOffset = AlignToPage(headerSize, pageSize);
        var dtbOffset = AlignToPage(ramdiskOffset + Header.VendorRamdiskSize, pageSize);
        Header.VendorRamdiskTableOffset = AlignToPage(dtbOffset + Header.DtbSize, pageSize);

        ExtractComponents(data, headerSize, pageSize);
        ExtractRamdiskTable(data);
        ExtractBootconfig(data);
    }

    private void ExtractComponents(byte[] data, uint headerSize, uint pageSize)
    {
        var ramdiskOffset = AlignToPage(headerSize, pageSize);

        if (Header.VendorRamdiskSize > 0 && ramdiskOffset + Header.VendorRamdiskSize <= data.Length)
        {
            VendorRamdisk = new byte[Header.VendorRamdiskSize];
            Array.Copy(data, ramdiskOffset, VendorRamdisk, 0, Header.VendorRamdiskSize);
        }

        if (Header.DtbSize > 0)
        {
            var dtbOffset = AlignToPage(ramdiskOffset + Header.VendorRamdiskSize, pageSize);
            if (dtbOffset + Header.DtbSize <= data.Length)
            {
                Dtb = new byte[Header.DtbSize];
                Array.Copy(data, dtbOffset, Dtb, 0, Header.DtbSize);
            }
        }
    }

    private void ExtractRamdiskTable(byte[] data)
    {
        RamdiskTable.Clear();

        if (Header.VendorRamdiskTableEntryNum == 0 || Header.VendorRamdiskTableOffset == 0)
        {
            return;
        }

        var entrySize = Header.VendorRamdiskTableEntrySize > 0
            ? Header.VendorRamdiskTableEntrySize
            : DefaultRamdiskTableEntrySize;

        for (var i = 0; i < Header.VendorRamdiskTableEntryNum; i++)
        {
            var offset = (int)(Header.VendorRamdiskTableOffset + ((uint)i * entrySize));
            if (offset + entrySize > data.Length)
            {
                break;
            }

            var entry = new VendorRamdiskTableEntry
            {
                RamdiskSize = ReadUInt32LE(data, offset),
                RamdiskOffset = ReadUInt32LE(data, offset + 4),
                RamdiskType = ReadUInt32LE(data, offset + 8),
                RamdiskName = ReadString(data, offset + 12, RamdiskNameSize).TrimEnd('\0')
            };

            for (var j = 0; j < RamdiskBoardIdCount; j++)
            {
                entry.RamdiskBoardId[j] = ReadUInt32LE(data, offset + 12 + RamdiskNameSize + (j * 4));
            }

            RamdiskTable.Add(entry);
        }
    }

    private void ExtractBootconfig(byte[] data)
    {
        if (Header.VendorBootconfigSize == 0)
        {
            return;
        }

        var pageSize = Header.PageSize > 0 ? Header.PageSize : 4096;
        var bootconfigOffset = AlignToPage(
            Header.VendorRamdiskTableOffset + Header.VendorRamdiskTableSize, pageSize);

        if (bootconfigOffset + Header.VendorBootconfigSize <= data.Length)
        {
            Bootconfig = new byte[Header.VendorBootconfigSize];
            Array.Copy(data, bootconfigOffset, Bootconfig, 0, Header.VendorBootconfigSize);
        }
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
            VendorRamdisk = [];
            Dtb = [];
            Bootconfig = [];
            RamdiskTable.Clear();
        }
        _disposed = true;
    }
}