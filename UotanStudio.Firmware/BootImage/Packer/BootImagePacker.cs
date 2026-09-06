using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Boot 镜像打包器。
/// <para>
/// 负责将各个组件重新打包为 Boot 镜像文件，支持头部版本 V0 至 V4。</para>
/// </summary>
public class BootImagePacker
{
    private const string BootMagic = "ANDROID!";

    /// <summary>
    /// 从现有的 Boot 镜像解析器创建打包器。
    /// <para>Creates a packer from an existing boot image parser.</para>
    /// </summary>
    /// <param name="parser">镜像解析器。<para>The image parser.</para></param>
    /// <returns>打包器实例。<para>The packer instance.</para></returns>
    public static BootImagePacker FromParser(BootImageParser parser)
    {
        return new BootImagePacker
        {
            Header = parser.Header,
            Kernel = parser.Kernel,
            Ramdisk = parser.Ramdisk,
            Second = parser.Second,
            Dtb = parser.Dtb,
            RecoveryDtbo = parser.RecoveryDtbo,
            Signature = parser.Signature
        };
    }

    /// <summary>
    /// 头部信息。
    /// <para>The header information.</para>
    /// </summary>
    public BootImageHeader Header { get; set; } = new BootImageHeader();

    /// <summary>
    /// 内核数据。
    /// <para>The kernel data.</para>
    /// </summary>
    public byte[] Kernel { get; set; } = [];

    /// <summary>
    /// RAMDisk 数据。
    /// <para>The ramdisk data.</para>
    /// </summary>
    public byte[] Ramdisk { get; set; } = [];

    /// <summary>
    /// 第二阶段引导程序数据。
    /// <para>The second stage loader data.</para>
    /// </summary>
    public byte[] Second { get; set; } = [];

    /// <summary>
    /// DTB 数据。
    /// <para>The DTB data.</para>
    /// </summary>
    public byte[] Dtb { get; set; } = [];

    /// <summary>
    /// Recovery DTBO 数据。
    /// <para>The recovery DTBO data.</para>
    /// </summary>
    public byte[] RecoveryDtbo { get; set; } = [];

    /// <summary>
    /// V4 签名数据。
    /// <para>The V4 signature data.</para>
    /// </summary>
    public byte[] Signature { get; set; } = [];

    /// <summary>
    /// 打包 Boot 镜像到文件。
    /// <para>Packs the boot image to a file.</para>
    /// </summary>
    /// <param name="outputPath">输出文件路径。<para>The output file path.</para></param>
    public void PackToFile(string outputPath)
    {
        // 直接写文件流，省掉 Pack() 的 MemoryStream.ToArray() 完整镜像拷贝
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65536, FileOptions.SequentialScan);
        PackToStream(fs);
    }

    /// <summary>
    /// 将镜像写入指定流。调用方可传入 FileStream 直接落盘，避免内存中保留完整镜像。
    /// <para>Writes the image to the given stream. Pass a FileStream to spill directly to disk.</para>
    /// </summary>
    /// <param name="output">目标输出流（调用方负责释放）。<para>The target stream (caller-owned).</para></param>
    public void PackToStream(Stream output)
    {
        if (Header.HeaderVersion >= BootImageVersion.V3)
        {
            PackV3OrV4(output);
        }
        else
        {
            PackV0V2(output);
        }
    }

    /// <summary>
    /// 打包 Boot 镜像到字节数组。
    /// <para>Packs the boot image to a byte array.</para>
    /// </summary>
    /// <returns>打包后的镜像数据。<para>The packed image data.</para></returns>
    [Obsolete("Use PackToStream(Stream) or PackToFile(string) for better memory efficiency. " +
              "This method allocates an intermediate byte array that duplicates the stream contents.")]
    public byte[] Pack()
    {
        using var ms = new MemoryStream();
        PackToStream(ms);
        return ms.ToArray(); // unavoidable allocation for byte[] return type
    }

    private void PackV0V2(Stream output)
    {
        var pageSize = Header.PageSize > 0 ? Header.PageSize : 2048;
        Header.KernelSize = (uint)Kernel.Length;
        Header.RamdiskSize = (uint)Ramdisk.Length;
        Header.SecondSize = (uint)Second.Length;
        Header.RecoveryDtboSize = Header.HeaderVersion >= BootImageVersion.V1 ? (uint)RecoveryDtbo.Length : 0;
        Header.DtbSize = Header.HeaderVersion >= BootImageVersion.V2 ? (uint)Dtb.Length : 0;

        var writer = new BinaryWriter(output);

        writer.Write(Encoding.ASCII.GetBytes(BootMagic));
        writer.Write(Header.KernelSize);
        writer.Write((uint)Header.KernelAddr);
        writer.Write(Header.RamdiskSize);
        writer.Write((uint)Header.RamdiskAddr);
        writer.Write(Header.SecondSize);
        writer.Write((uint)Header.SecondAddr);
        writer.Write((uint)Header.TagsAddr);
        writer.Write(pageSize);
        writer.Write(Header.HeaderVersion switch
        {
            BootImageVersion.V0 => 0u,
            BootImageVersion.V1 => 1u,
            BootImageVersion.V2 => 2u,
            // V3/V4 使用不同的 header 结构，不支持旧式打包；Unknown 为无效版本。
            // V3/V4 use a different header structure and are not supported by this legacy packer; Unknown is invalid.
            BootImageVersion.V3 => throw new NotSupportedException("Boot image V3 packing is not supported by this packer."),
            BootImageVersion.V4 => throw new NotSupportedException("Boot image V4 packing is not supported by this packer."),
            BootImageVersion.Unknown => throw new ArgumentException("Unknown boot image version.", nameof(Header.HeaderVersion)),
            _ => 0u
        });
        writer.Write(Header.OsVersion);

        var nameBytes = new byte[16];
        var nameSrc = Encoding.ASCII.GetBytes(Header.ImageName ?? string.Empty);
        Array.Copy(nameSrc, nameBytes, Math.Min(nameSrc.Length, 16));
        writer.Write(nameBytes);

        var cmdLine = new byte[512];
        var cmdSrc = Encoding.ASCII.GetBytes(Header.CommandLine ?? string.Empty);
        Array.Copy(cmdSrc, cmdLine, Math.Min(cmdSrc.Length, 512));
        writer.Write(cmdLine);

        for (var i = 0; i < 8; i++)
        {
            writer.Write(Header.Ids?[i] ?? 0u);
        }

        var extraCmdLine = new byte[1024];
        writer.Write(extraCmdLine);

        uint headerSize = Header.HeaderVersion switch
        {
            BootImageVersion.V0 => 1632,
            BootImageVersion.V1 => 1648,
            BootImageVersion.V2 => 1664,
            // V3/V4 使用不同的 header 结构，不支持旧式打包；Unknown 为无效版本。
            // V3/V4 use a different header structure and are not supported by this legacy packer; Unknown is invalid.
            BootImageVersion.V3 => throw new NotSupportedException("Boot image V3 packing is not supported by this packer."),
            BootImageVersion.V4 => throw new NotSupportedException("Boot image V4 packing is not supported by this packer."),
            BootImageVersion.Unknown => throw new ArgumentException("Unknown boot image version.", nameof(Header.HeaderVersion)),
            _ => 1632
        };

        if (Header.HeaderVersion >= BootImageVersion.V1)
        {
            // recovery_dtbo 紧跟 second（页对齐），dtb（v2）在其后——与 mkbootimg 布局一致；
            // 头部写入真实偏移，解析器按该偏移读取。
            // recovery_dtbo follows second (page-aligned) with dtb (v2) after it — matching mkbootimg;
            // the real offset is stored in the header and used by the parser.
            Header.RecoveryDtboOffset = Header.RecoveryDtboSize > 0
                ? AlignToPage(headerSize, pageSize)
                    + AlignToPage(Header.KernelSize, pageSize)
                    + AlignToPage(Header.RamdiskSize, pageSize)
                    + AlignToPage(Header.SecondSize, pageSize)
                : 0;
            writer.Write(Header.RecoveryDtboSize);
            writer.Write(Header.RecoveryDtboOffset);
            writer.Write(headerSize); // v1/v2：header_size 字段（偏移 1644）
        }

        if (Header.HeaderVersion >= BootImageVersion.V2)
        {
            writer.Write(Header.DtbSize); // 偏移 1648
            writer.Write(Header.KernelLoadAddr); // 偏移 1652
        }

        while (output.Position < headerSize)
        {
            writer.Write((byte)0);
        }

        WritePadding(writer, pageSize);

        if (Kernel.Length > 0)
        {
            writer.Write(Kernel);
            WritePadding(writer, pageSize);
        }

        if (Ramdisk.Length > 0)
        {
            writer.Write(Ramdisk);
            WritePadding(writer, pageSize);
        }

        if (Second.Length > 0)
        {
            writer.Write(Second);
            WritePadding(writer, pageSize);
        }

        if (Header.HeaderVersion >= BootImageVersion.V1 && RecoveryDtbo.Length > 0)
        {
            writer.Write(RecoveryDtbo);
            WritePadding(writer, pageSize);
        }

        if (Header.HeaderVersion >= BootImageVersion.V2 && Dtb.Length > 0)
        {
            writer.Write(Dtb);
            WritePadding(writer, pageSize);
        }
    }

    private void PackV3OrV4(Stream output)
    {
        var isV4 = Header.HeaderVersion == BootImageVersion.V4;
        uint pageSize = 4096;
        Header.KernelSize = checked((uint)Kernel.Length);
        Header.RamdiskSize = checked((uint)Ramdisk.Length);

        if (isV4)
        {
            Header.SignatureSize = checked((uint)Signature.Length);
        }

        // V3 头为 1580 字节、V4 头为 1584 字节；header_size 字段与实际 padding 保持一致，
        // 组件偏移按该值页对齐，与官方 mkbootimg / BootImageParser 的布局一致。
        // The V3 header is 1580 bytes and the V4 header is 1584 bytes; the header_size field is
        // kept consistent with the actual padding, and component offsets are page-aligned from it
        // (matching the official mkbootimg layout and BootImageParser).
        var headerSize = isV4 ? 1584u : 1580u;
        var effectiveHeaderSize = Header.HeaderSize >= headerSize ? Header.HeaderSize : headerSize;

        var writer = new BinaryWriter(output);

        writer.Write(Encoding.ASCII.GetBytes(BootMagic));
        writer.Write(Header.KernelSize);
        writer.Write(Header.RamdiskSize);
        writer.Write(Header.OsVersion);
        writer.Write(effectiveHeaderSize);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(isV4 ? 4u : 3u);

        var cmdLine = new byte[1536];
        var cmdSrc = Encoding.ASCII.GetBytes(Header.CommandLine ?? string.Empty);
        Array.Copy(cmdSrc, cmdLine, Math.Min(cmdSrc.Length, 1536));
        writer.Write(cmdLine);

        if (isV4)
        {
            writer.Write(Header.SignatureSize);
        }

        while (output.Position < effectiveHeaderSize)
        {
            writer.Write((byte)0);
        }

        WritePadding(writer, pageSize);

        if (Kernel.Length > 0)
        {
            writer.Write(Kernel);
            WritePadding(writer, pageSize);
        }

        if (Ramdisk.Length > 0)
        {
            writer.Write(Ramdisk);
            WritePadding(writer, pageSize);
        }

        if (isV4 && Signature.Length > 0)
        {
            writer.Write(Signature);
            WritePadding(writer, pageSize);
        }
    }

    /// <summary>
    /// 打包 Boot 镜像，使用指定的原始镜像作为模板。
    /// <para>Packs a boot image using the specified original image as a template.</para>
    /// </summary>
    /// <param name="original">原始镜像解析器。<para>The original image parser.</para></param>
    /// <param name="kernel">可选的替换内核数据。<para>Optional replacement kernel data.</para></param>
    /// <param name="ramdisk">可选的替换 RAMDisk 数据。<para>Optional replacement ramdisk data.</para></param>
    /// <param name="second">可选的替换第二阶段引导程序数据。<para>Optional replacement second stage data.</para></param>
    /// <param name="dtb">可选的替换 DTB 数据。<para>Optional replacement DTB data.</para></param>
    /// <returns>打包后的镜像数据。<para>The packed image data.</para></returns>
    public static byte[] Repack(
        BootImageParser original,
        byte[]? kernel = null,
        byte[]? ramdisk = null,
        byte[]? second = null,
        byte[]? dtb = null)
    {
        var packer = FromParser(original);

        if (kernel != null)
        {
            packer.Kernel = kernel;
            packer.Header.KernelSize = (uint)kernel.Length;
        }

        if (ramdisk != null)
        {
            packer.Ramdisk = ramdisk;
            packer.Header.RamdiskSize = (uint)ramdisk.Length;
        }

        if (second != null)
        {
            packer.Second = second;
            packer.Header.SecondSize = (uint)second.Length;
        }

        if (dtb != null)
        {
            packer.Dtb = dtb;
            packer.Header.DtbSize = (uint)dtb.Length;
        }

        return packer.Pack();
    }

    private void WritePadding(BinaryWriter writer, uint alignment)
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

    private static uint AlignToPage(uint value, uint pageSize) => checked((value + pageSize - 1) / pageSize * pageSize);
}