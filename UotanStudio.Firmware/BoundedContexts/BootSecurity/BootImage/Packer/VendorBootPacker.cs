using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;

/// <summary>
/// Builds vendor boot images with the <c>VNDRBOOT</c> magic for header versions 3 and 4, writing the header, vendor
/// ramdisk, DTB, optional ramdisk table and bootconfig as page-aligned segments directly to the output stream.
/// <para>构建魔数为 <c>VNDRBOOT</c>、头部版本 3/4 的 Vendor Boot 镜像：将头部、vendor ramdisk、DTB、可选 ramdisk 表和 bootconfig 按页对齐直接写出到输出流。</para>
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

    private const uint RamdiskTableEntrySize = 108;
    private const uint DefaultPageSize = 4096;
    internal BootComponentFiles? ComponentFiles { get; init; }

    private long ComponentLength(string name, byte[] data) => ComponentFiles?.Length(name) ?? data.LongLength;

    private void WriteComponent(BinaryWriter writer, string name, byte[] data)
    {
        if (ComponentFiles is { } files) files.Copy(name, writer.BaseStream);
        else writer.Write(data);
    }

    /// <summary>Vendor boot header written to the output; packing normalizes and refills its derived fields in place.
    /// <para>写出到输出的 vendor boot 头部；打包过程会就地规范化并回填其中的派生字段。</para></summary>
    /// <value>Never <see langword="null"/>; <see cref="PackToStream"/> rewrites page size, header size, section sizes,
    /// and the V4 table fields, and its field values are also read back by <see cref="Repack"/>.
    /// <para>永不为 <see langword="null"/>；<see cref="PackToStream"/> 会重写页大小、头部大小、各区段大小以及 V4 表字段，其字段值也会被 <see cref="Repack"/> 读取。</para></value>
    public VendorBootHeader Header { get; set; } = new VendorBootHeader();

    /// <summary>Vendor ramdisk content written page-aligned right after the header.
    /// <para>紧随头部按页对齐写出的 vendor ramdisk 内容。</para></summary>
    /// <value>Raw bytes as provided; an empty array omits the segment. When a backing component-file source is set, its
    /// entry for this component takes precedence over the array content.
    /// <para>按提供的原始字节写出；空数组时省略该区段。当设置了组件文件后备来源时，该组件对应的文件条目优先于数组内容。</para></value>
    public byte[] VendorRamdisk { get; set; } = [];

    /// <summary>Device tree blob written page-aligned after the vendor ramdisk.
    /// <para>紧随 vendor ramdisk 按页对齐写出的设备树 blob。</para></summary>
    /// <value>Raw bytes as provided; an empty array omits the segment. Component-file precedence applies as for
    /// <see cref="VendorRamdisk"/>.
    /// <para>按提供的原始字节写出；空数组时省略该区段。组件文件优先规则与 <see cref="VendorRamdisk"/> 相同。</para></value>
    public byte[] Dtb { get; set; } = [];

    /// <summary>Bootconfig content written last, only for version 4.
    /// <para>最后写出的 bootconfig 内容，仅在版本 4 时写入。</para></summary>
    /// <value>Raw bytes as provided; ignored (size <c>0</c> in the header) for version 3. Component-file precedence
    /// applies as for <see cref="VendorRamdisk"/>.
    /// <para>按提供的原始字节写出；版本 3 时被忽略（头部中大小为 <c>0</c>）。组件文件优先规则与 <see cref="VendorRamdisk"/> 相同。</para></value>
    public byte[] Bootconfig { get; set; } = [];

    /// <summary>Vendor ramdisk table entries serialized one after another on version 4 images.
    /// <para>在版本 4 镜像中顺序序列化写出的 vendor ramdisk 表条目。</para></summary>
    /// <value>Mutable list that is never reassigned; empty by default. Entries are validated against the vendor ramdisk
    /// size and ignored for version 3, where the table size is always <c>0</c>.
    /// <para>永不整体重新赋值的可变列表；默认为空。条目会与 vendor ramdisk 大小做校验，版本 3 时忽略该表，表大小恒为 <c>0</c>。</para></value>
    public List<VendorRamdiskTableEntry> RamdiskTable { get; } = [];

    /// <summary>
    /// Creates a packer from an existing vendor boot image parser.
    /// <para>从现有的 Vendor Boot 镜像解析器创建打包器。</para>
    /// </summary>
    /// <param name="parser">Parsed vendor boot image whose header and component buffers are taken.
    /// <para>已解析的 Vendor Boot 镜像，直接取其头部与各组件缓冲区。</para></param>
    /// <returns>Packer sharing the parser's <see cref="VendorBootHeader"/> instance and component arrays, with copied
    /// ramdisk table entries; later parser mutations are visible through the shared header.
    /// <para>返回的打包器与解析器共享 <see cref="VendorBootHeader"/> 实例和组件数组，ramdisk 表条目为逐条拷贝；解析器后续对头部的修改对其可见。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="parser"/> is <see langword="null"/>.
    /// <para><paramref name="parser"/> 为 <see langword="null"/>。</para></exception>
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
    /// Packs the vendor boot image to a file (writes the file stream directly to avoid keeping the full image in memory).
    /// <para>打包 Vendor Boot 镜像到文件（直接写文件流，避免内存中保留完整镜像）。</para>
    /// </summary>
    /// <param name="outputPath">Destination path, created or truncated in place; a failure mid-write leaves a partial
    /// file behind.
    /// <para>目标路径，就地创建或截断；写入中途失败会残留不完整文件。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="outputPath"/> is <see langword="null"/>.
    /// <para><paramref name="outputPath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="outputPath"/> is empty, or a check in
    /// <see cref="PackToStream(Stream)"/> fails.
    /// <para><paramref name="outputPath"/> 为空字符串，或 <see cref="PackToStream(Stream)"/> 中的校验失败。</para></exception>
    public void PackToFile(string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65536, FileOptions.SequentialScan);
        PackToStream(fs);
    }

    /// <summary>
    /// Writes the vendor boot image to the given stream.
    /// <para>将 Vendor Boot 镜像写入指定流。</para>
    /// </summary>
    /// <param name="output">Writable stream positioned where the image starts; it stays open and ends at the tail of
    /// the written image.
    /// <para>定位在镜像起始处的可写流；写入后保持打开，位置停在已写镜像末尾。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="NotSupportedException"><see cref="VendorBootHeader.HeaderVersion"/> is neither 3 nor 4.
    /// <para><see cref="VendorBootHeader.HeaderVersion"/> 既不是 3 也不是 4。</para></exception>
    /// <exception cref="ArgumentException">The page size is not a non-zero power of two, a ramdisk table entry exceeds
    /// the vendor ramdisk region, or a V4 component offset exceeds the supported range.
    /// <para>页大小不是非零的 2 的幂、ramdisk 表条目超出 vendor ramdisk 区域，或 V4 组件偏移超出支持范围。</para></exception>
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

        var vendorRamdiskSize = checked((uint)ComponentLength(nameof(VendorRamdisk), VendorRamdisk));
        var dtbSize = checked((uint)ComponentLength(nameof(Dtb), Dtb));
        var bootconfigSize = version == 4 ? checked((uint)ComponentLength(nameof(Bootconfig), Bootconfig)) : 0u;

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


        var tableBytes = checked((ulong)tableEntryNum * RamdiskTableEntrySize);
        var tableSize = tableBytes == 0 ? 0u : AlignToPage(tableBytes, pageSize);


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


        writer.Write(Encoding.ASCII.GetBytes(VendorBootMagic));
        writer.Write(version);
        writer.Write(pageSize);
        writer.Write(Header.KernelLoadAddress);
        writer.Write(Header.RamdiskLoadAddress);
        writer.Write(vendorRamdiskSize);
        WriteFixedAscii(writer, Header.VendorCmdline, VendorCmdlineSize);
        writer.Write(Header.TagsLoadAddress);
        WriteFixedAscii(writer, Header.VendorName, VendorNameSize);
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


        while (output.Position < headerSize)
        {
            writer.Write((byte)0);
        }
        WritePadding(writer, pageSize);

        if (vendorRamdiskSize > 0)
        {
            WriteComponent(writer, nameof(VendorRamdisk), VendorRamdisk);
            WritePadding(writer, pageSize);
        }

        if (dtbSize > 0)
        {
            WriteComponent(writer, nameof(Dtb), Dtb);
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


            var tableEnd = checked(tableOffset + tableSize);
            while (output.Position < tableEnd)
            {
                writer.Write((byte)0);
            }
        }

        if (version == 4 && bootconfigSize > 0)
        {
            WriteComponent(writer, nameof(Bootconfig), Bootconfig);
            WritePadding(writer, pageSize);
        }
    }

    /// <summary>
    /// Packs the vendor boot image to a byte array (only for small images; prefer PackToFile or PackToStream for large images).
    /// <para>打包 Vendor Boot 镜像到字节数组（仅适合小镜像；大镜像请使用 PackToFile 或 PackToStream 流式输出）。</para>
    /// </summary>
    /// <returns>Complete image bytes held fully in memory; every exception of <see cref="PackToStream(Stream)"/> can
    /// escape from here as well.
    /// <para>完整驻留内存的镜像字节；<see cref="PackToStream(Stream)"/> 的所有异常同样可能在此抛出。</para></returns>
    public byte[] Pack()
    {
        using var ms = new MemoryStream();
        PackToStream(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Repacks an existing vendor boot image as the template (without modifying the parser).
    /// <para>以现有 Vendor Boot 镜像作为模板重新打包（不修改解析器）。</para>
    /// </summary>
    /// <param name="original">Parsed vendor boot image supplying header, ramdisk, DTB, bootconfig and table content.
    /// <para>提供头部、ramdisk、DTB、bootconfig 与表内容的已解析 Vendor Boot 镜像。</para></param>
    /// <returns>Complete image bytes re-serialized from the parser state through <see cref="FromParser"/> and
    /// <see cref="Pack"/>; header sizes and offsets are recomputed, so the result need not match the source file byte
    /// for byte.
    /// <para>由解析器状态经 <see cref="FromParser"/> 与 <see cref="Pack"/> 重新序列化得到的完整镜像字节；头部大小与偏移会重新计算，因此结果不必然与源文件逐字节相同。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="original"/> is <see langword="null"/>.
    /// <para><paramref name="original"/> 为 <see langword="null"/>。</para></exception>
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