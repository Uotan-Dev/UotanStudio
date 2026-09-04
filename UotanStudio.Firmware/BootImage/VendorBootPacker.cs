using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Vendor Boot 镜像打包器（V3/V4）。
/// <para>
/// 将 vendor ramdisk、DTB、vendor ramdisk 表（仅 V4）与 vendor bootconfig（仅 V4）按官方
/// mkbootimg 的头部布局、页对齐与 padding 语义重新打包为 vendor_boot 镜像；输出布局与
/// <see cref="VendorBootParser"/> 的回读假设严格对称。</para>
/// <para>
/// Packs vendor ramdisk, DTB, the vendor ramdisk table (V4 only) and vendor bootconfig
/// (V4 only) back into a vendor_boot image using the official mkbootimg header layout, page
/// alignment and padding semantics; the emitted layout is strictly symmetric with the reads
/// performed by <see cref="VendorBootParser"/>.</para>
/// </summary>
public class VendorBootPacker
{
    private const string VendorBootMagic = "VNDRBOOT";
    private const int V3HeaderSize = 2112;
    private const int V4HeaderSize = 2128;
    private const int VendorCmdlineSize = 2048;
    private const int VendorNameSize = 16;
    private const int RamdiskNameSize = 32;
    private const int RamdiskBoardIdCount = 16;
    // AOSP vendor_boot v4 entry: size/offset/type (12) + name (32) + board_id[16] (64).
    private const uint RamdiskTableEntrySize = 108;
    private const uint DefaultPageSize = 4096;

    /// <summary>
    /// 头部信息。
    /// <para>The header information.</para>
    /// </summary>
    public VendorBootHeader Header { get; set; } = new VendorBootHeader();

    /// <summary>
    /// Vendor RAMDisk 区域数据（V4 下为 ramdisk 表内各条目偏移所指向的完整拼接区域）。
    /// <para>The vendor ramdisk region data (for V4, the concatenated region referenced by the
    /// ramdisk table entry offsets).</para>
    /// </summary>
    public byte[] VendorRamdisk { get; set; } = [];

    /// <summary>
    /// DTB 数据。
    /// <para>The DTB data.</para>
    /// </summary>
    public byte[] Dtb { get; set; } = [];

    /// <summary>
    /// Vendor Bootconfig 数据（仅 V4）。
    /// <para>The vendor bootconfig data (V4 only).</para>
    /// </summary>
    public byte[] Bootconfig { get; set; } = [];

    /// <summary>
    /// Vendor RAMDisk 表条目（仅 V4）。
    /// <para>The vendor ramdisk table entries (V4 only).</para>
    /// </summary>
    public List<VendorRamdiskTableEntry> RamdiskTable { get; } = [];

    /// <summary>
    /// 从现有的 Vendor Boot 镜像解析器创建打包器。
    /// <para>Creates a packer from an existing vendor boot image parser.</para>
    /// </summary>
    /// <param name="parser">镜像解析器。<para>The image parser.</para></param>
    /// <returns>打包器实例。<para>The packer instance.</para></returns>
    public static VendorBootPacker FromParser(VendorBootParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        var packer = new VendorBootPacker
        {
            Header = parser.Header,
            VendorRamdisk = parser.VendorRamdisk,
            Dtb = parser.Dtb,
            Bootconfig = parser.Bootconfig
        };
        foreach (var entry in parser.RamdiskTable)
        {
            var copy = new VendorRamdiskTableEntry
            {
                RamdiskSize = entry.RamdiskSize,
                RamdiskOffset = entry.RamdiskOffset,
                RamdiskType = entry.RamdiskType,
                RamdiskName = entry.RamdiskName
            };
            Array.Copy(entry.RamdiskBoardId, copy.RamdiskBoardId, entry.RamdiskBoardId.Length);
            packer.RamdiskTable.Add(copy);
        }
        return packer;
    }

    /// <summary>
    /// 打包 Vendor Boot 镜像到文件（直接写文件流，避免内存中保留完整镜像）。
    /// <para>Packs the vendor boot image to a file (writes the file stream directly to avoid
    /// keeping the full image in memory).</para>
    /// </summary>
    /// <param name="outputPath">输出文件路径。<para>The output file path.</para></param>
    public void PackToFile(string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65536, FileOptions.SequentialScan);
        PackToStream(fs);
    }

    /// <summary>
    /// 将 Vendor Boot 镜像写入指定流。调用方可传入 FileStream 直接落盘，避免内存中保留完整镜像。
    /// <para>Writes the vendor boot image to the given stream. Pass a FileStream to spill directly
    /// to disk without keeping the full image in memory.</para>
    /// </summary>
    /// <param name="output">目标输出流（调用方负责释放）。<para>The target stream (caller-owned).</para></param>
    public void PackToStream(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var version = Header.HeaderVersion;
        if (version != 3 && version != 4)
        {
            throw new NotSupportedException(
                "Vendor boot packing supports header versions 3 and 4 only. 仅支持 V3/V4 的 vendor boot 打包。");
        }

        var pageSize = Header.PageSize > 0 ? Header.PageSize : DefaultPageSize;
        if (pageSize == 0 || (pageSize & (pageSize - 1)) != 0)
        {
            throw new ArgumentException(
                "Vendor boot page size must be a non-zero power of two. vendor boot 页大小必须为非零的 2 的幂。",
                nameof(Header));
        }

        var minimumHeaderSize = version == 4 ? V4HeaderSize : V3HeaderSize;
        var headerSize = Header.VendorHeaderSize >= minimumHeaderSize
            ? Header.VendorHeaderSize
            : (uint)minimumHeaderSize;

        var vendorRamdiskSize = checked((uint)VendorRamdisk.Length);
        var dtbSize = checked((uint)Dtb.Length);
        var bootconfigSize = version == 4 ? checked((uint)Bootconfig.Length) : 0u;

        var tableEntryNum = version == 4 ? checked((uint)RamdiskTable.Count) : 0u;
        foreach (var entry in RamdiskTable)
        {
            if (entry.RamdiskOffset > vendorRamdiskSize ||
                entry.RamdiskSize > vendorRamdiskSize - entry.RamdiskOffset)
            {
                throw new ArgumentException(
                    "Vendor ramdisk table entry exceeds the vendor ramdisk region. vendor ramdisk 表条目超出 ramdisk 区域。",
                    nameof(RamdiskTable));
            }
        }

        // V4 的 ramdisk 表区域按页对齐，确保 bootconfig 紧跟表区域之后。
        // The V4 ramdisk table region is page-aligned so bootconfig directly follows the table.
        var tableBytes = checked((ulong)tableEntryNum * RamdiskTableEntrySize);
        var tableSize = tableBytes == 0 ? 0u : AlignToPage(tableBytes, pageSize);

        // 以实际数据重新计算组件偏移（与 VendorBootParser.ExtractComponents 的推导一致）。
        // Offsets are recomputed from the actual data (consistent with VendorBootParser.ExtractComponents).
        var ramdiskOffset = AlignToPage(checked(headerSize), pageSize);
        var dtbOffset = AlignToPage(checked(ramdiskOffset + vendorRamdiskSize), pageSize);
        var tableOffset = version == 4
            ? AlignToPage(checked(dtbOffset + dtbSize), pageSize)
            : 0;
        var bootconfigOffset = version == 4
            ? AlignToPage(checked(tableOffset + tableSize), pageSize)
            : 0;
        if (version == 4 && (tableOffset > uint.MaxValue || bootconfigOffset > uint.MaxValue))
        {
            throw new ArgumentException(
                "Vendor boot component offsets exceed the supported range. vendor boot 组件偏移超出支持范围。");
        }

        // 回填头部，保证打包产物可由 VendorBootParser 原样读回。
        // Backfill the header so the packed image can be read back by VendorBootParser as-is.
        Header.PageSize = pageSize;
        Header.VendorHeaderSize = headerSize;
        Header.VendorRamdiskSize = vendorRamdiskSize;
        Header.DtbSize = dtbSize;
        if (version == 4)
        {
            Header.VendorRamdiskTableSize = tableSize;
            Header.VendorRamdiskTableEntryNum = tableEntryNum;
            Header.VendorRamdiskTableEntrySize = RamdiskTableEntrySize;
            Header.VendorBootconfigSize = bootconfigSize;
            Header.VendorRamdiskTableOffset = (uint)tableOffset;
        }

        var writer = new BinaryWriter(output);

        // 固定头部（V3/V4 字段布局，含 name 预留区；V4 追加表与 bootconfig 字段）。
        // Fixed header (V3/V4 field layout including the name reservation; V4 appends the table
        // and bootconfig fields).
        writer.Write(Encoding.ASCII.GetBytes(VendorBootMagic));
        writer.Write(version);
        writer.Write(pageSize);
        writer.Write(Header.KernelLoadAddress);
        writer.Write(Header.RamdiskLoadAddress);
        writer.Write(vendorRamdiskSize);
        WriteFixedAscii(writer, Header.VendorCmdline, VendorCmdlineSize);
        writer.Write(Header.TagsLoadAddress);
        WriteZeroes(writer, VendorNameSize);
        writer.Write(headerSize);
        writer.Write(dtbSize);
        writer.Write(Header.DtbAddress);
        if (version == 4)
        {
            writer.Write(tableSize);
            writer.Write(tableEntryNum);
            writer.Write(RamdiskTableEntrySize);
            writer.Write(bootconfigSize);
        }

        // 头部 padding 到 header_size，再对齐到页。
        // Pad the header to header_size, then align to the page boundary.
        while (output.Position < headerSize)
        {
            writer.Write((byte)0);
        }
        WritePadding(writer, pageSize);

        if (vendorRamdiskSize > 0)
        {
            writer.Write(VendorRamdisk);
            WritePadding(writer, pageSize);
        }

        if (dtbSize > 0)
        {
            writer.Write(Dtb);
            WritePadding(writer, pageSize);
        }

        if (version == 4 && tableSize > 0)
        {
            foreach (var entry in RamdiskTable)
            {
                writer.Write(entry.RamdiskSize);
                writer.Write(entry.RamdiskOffset);
                writer.Write(entry.RamdiskType);
                WriteFixedAscii(writer, entry.RamdiskName, RamdiskNameSize);
                for (var j = 0; j < RamdiskBoardIdCount; j++)
                {
                    writer.Write(entry.RamdiskBoardId[j]);
                }
            }

            // 补齐表区域到声明的页对齐大小，使 bootconfig 落在解析器推导的位置。
            // Pad the table region to the declared page-aligned size so bootconfig lands where
            // the parser derives it.
            var tableEnd = checked(tableOffset + tableSize);
            while (output.Position < tableEnd)
            {
                writer.Write((byte)0);
            }
        }

        if (version == 4 && bootconfigSize > 0)
        {
            writer.Write(Bootconfig);
            WritePadding(writer, pageSize);
        }
    }

    /// <summary>
    /// 打包 Vendor Boot 镜像到字节数组（仅适合小镜像；大镜像请使用
    /// <see cref="PackToFile"/> 或 <see cref="PackToStream"/> 流式输出）。
    /// <para>Packs the vendor boot image to a byte array (only for small images; prefer
    /// <see cref="PackToFile"/> or <see cref="PackToStream"/> for large images).</para>
    /// </summary>
    /// <returns>打包后的镜像数据。<para>The packed image data.</para></returns>
    public byte[] Pack()
    {
        using var ms = new MemoryStream();
        PackToStream(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 以现有 Vendor Boot 镜像作为模板重新打包（不修改解析器）。
    /// <para>Repacks an existing vendor boot image as the template (without modifying the parser).</para>
    /// </summary>
    /// <param name="original">原始镜像解析器。<para>The original image parser.</para></param>
    /// <returns>重新打包后的镜像数据。<para>The repacked image data.</para></returns>
    public static byte[] Repack(VendorBootParser original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return FromParser(original).Pack();
    }

    private static void WriteFixedAscii(BinaryWriter writer, string? value, int length)
    {
        var src = Encoding.ASCII.GetBytes(value ?? string.Empty);
        var count = Math.Min(src.Length, length);
        writer.Write(src, 0, count);
        for (var i = count; i < length; i++)
        {
            writer.Write((byte)0);
        }
    }

    private static void WriteZeroes(BinaryWriter writer, int length)
    {
        for (var i = 0; i < length; i++)
        {
            writer.Write((byte)0);
        }
    }

    private static void WritePadding(BinaryWriter writer, uint alignment)
    {
        var pos = writer.BaseStream.Position;
        var remainder = (uint)(pos % alignment);
        if (remainder == 0)
        {
            return;
        }

        var padding = alignment - remainder;
        for (var i = 0; i < padding; i++)
        {
            writer.Write((byte)0);
        }
    }

    private static long AlignToPage(long offset, uint pageSize)
    {
        var mask = (long)pageSize - 1;
        return (offset + mask) & ~mask;
    }

    private static uint AlignToPage(ulong offset, uint pageSize)
    {
        var mask = (ulong)pageSize - 1;
        return (uint)((offset + mask) & ~mask);
    }
}