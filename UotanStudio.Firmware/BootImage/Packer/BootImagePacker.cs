using System.Text;

namespace UotanStudio.Firmware.BootImage;

public class BootImagePacker
{
    private const string BootMagic = "ANDROID!";

    /// <summary>
    /// Creates a packer from an existing boot image parser.
    /// <para>从现有的 Boot 镜像解析器创建打包器。</para>
    /// </summary>
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

    public BootImageHeader Header { get; set; } = new BootImageHeader();

    public byte[] Kernel { get; set; } = [];

    public byte[] Ramdisk { get; set; } = [];

    public byte[] Second { get; set; } = [];

    public byte[] Dtb { get; set; } = [];

    public byte[] RecoveryDtbo { get; set; } = [];

    public byte[] Signature { get; set; } = [];

    /// <summary>
    /// Packs the boot image to a file.
    /// <para>打包 Boot 镜像到文件。</para>
    /// </summary>
    public void PackToFile(string outputPath)
    {

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65536, FileOptions.SequentialScan);
        PackToStream(fs);
    }

    /// <summary>
    /// Writes the image to the given stream.
    /// <para>将镜像写入指定流。</para>
    /// </summary>
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
    /// Packs the boot image to a byte array.
    /// <para>打包 Boot 镜像到字节数组。</para>
    /// </summary>
    [Obsolete("Use PackToStream(Stream) or PackToFile(string) for better memory efficiency. " +
              "This method allocates an intermediate byte array that duplicates the stream contents.")]
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


            BootImageVersion.V3 => throw new NotSupportedException("Boot image V3 packing is not supported by this packer."),
            BootImageVersion.V4 => throw new NotSupportedException("Boot image V4 packing is not supported by this packer."),
            BootImageVersion.Unknown => throw new ArgumentException("Unknown boot image version.", nameof(Header.HeaderVersion)),
            _ => 1632
        };

        if (Header.HeaderVersion >= BootImageVersion.V1)
        {


            Header.RecoveryDtboOffset = Header.RecoveryDtboSize > 0
                ? AlignToPage(headerSize, pageSize)
                    + AlignToPage(Header.KernelSize, pageSize)
                    + AlignToPage(Header.RamdiskSize, pageSize)
                    + AlignToPage(Header.SecondSize, pageSize)
                : 0;
            writer.Write(Header.RecoveryDtboSize);
            writer.Write(Header.RecoveryDtboOffset);
            writer.Write(headerSize);
        }

        if (Header.HeaderVersion >= BootImageVersion.V2)
        {
            writer.Write(Header.DtbSize);
            writer.Write(Header.KernelLoadAddr);
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
    /// Packs a boot image using the specified original image as a template.
    /// <para>打包 Boot 镜像，使用指定的原始镜像作为模板。</para>
    /// </summary>
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
