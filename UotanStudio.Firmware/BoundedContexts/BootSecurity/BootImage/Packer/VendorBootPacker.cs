using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;

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

    public VendorBootHeader Header { get; set; } = new VendorBootHeader();

    public byte[] VendorRamdisk { get; set; } = [];

    public byte[] Dtb { get; set; } = [];

    public byte[] Bootconfig { get; set; } = [];

    public List<VendorRamdiskTableEntry> RamdiskTable { get; } = [];

    /// <summary>
    /// Creates a packer from an existing vendor boot image parser.
    /// <para>从现有的 Vendor Boot 镜像解析器创建打包器。</para>
    /// </summary>
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