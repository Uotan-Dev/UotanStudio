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
    public byte[] Pack()
    {
        using var ms = new MemoryStream();
        PackToStream(ms);
        return ms.ToArray();
    }

    private void PackV0V2(Stream output)
    {
        var pageSize = Header.PageSize > 0 ? Header.PageSize : 2048;
        Header.KernelSize = (uint)Kernel.Length;
        Header.RamdiskSize = (uint)Ramdisk.Length;
        Header.SecondSize = (uint)Second.Length;
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
            BootImageVersion.Unknown => throw new NotImplementedException(),
            BootImageVersion.V3 => throw new NotImplementedException(),
            BootImageVersion.V4 => throw new NotImplementedException(),
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
            BootImageVersion.Unknown => throw new NotImplementedException(),
            BootImageVersion.V3 => throw new NotImplementedException(),
            BootImageVersion.V4 => throw new NotImplementedException(),
            _ => 1632
        };

        if (Header.HeaderVersion >= BootImageVersion.V1)
        {
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
        Header.KernelSize = (uint)Kernel.Length;
        Header.RamdiskSize = (uint)Ramdisk.Length;

        if (isV4)
        {
            Header.SignatureSize = (uint)Signature.Length;
        }

        var writer = new BinaryWriter(output);

        writer.Write(Encoding.ASCII.GetBytes(BootMagic));
        writer.Write(Header.KernelSize);
        writer.Write(Header.RamdiskSize);
        writer.Write(Header.OsVersion);
        writer.Write(Header.HeaderSize > 0 ? Header.HeaderSize : 1580u);
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

        var headerSize = isV4 ? 1584u : 1580u;
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
}