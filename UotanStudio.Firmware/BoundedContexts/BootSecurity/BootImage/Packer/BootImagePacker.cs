using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;

/// <summary>
/// Builds Android boot images with the <c>ANDROID!</c> magic for header versions 0 through 4, serializing the header,
/// kernel, ramdisk, second-stage, recovery DTBO, DTB and V4 signature as page-aligned segments to a stream.
/// <para>构建魔数为 <c>ANDROID!</c>、头部版本 0 至 4 的 Android Boot 镜像：将头部、kernel、ramdisk、second、recovery DTBO、DTB 与 V4 签名按页对齐序列化到流。</para>
/// </summary>
/// <example>
/// <code>
/// var packer = new BootImagePacker { Header = header, Kernel = kernelBytes };
/// packer.PackToFile("out.img");
/// </code>
/// </example>
public class BootImagePacker
{
    private const string BootMagic = "ANDROID!";
    internal BootComponentFiles? ComponentFiles { get; init; }

    private long ComponentLength(string name, byte[] data) => ComponentFiles?.Length(name) ?? data.LongLength;

    private void WriteComponent(BinaryWriter writer, string name, byte[] data)
    {
        if (ComponentFiles is { } files) files.Copy(name, writer.BaseStream);
        else writer.Write(data);
    }

    /// <summary>
    /// Creates a packer from an existing boot image parser.
    /// <para>从现有的 Boot 镜像解析器创建打包器。</para>
    /// </summary>
    /// <param name="parser">Parsed boot image whose header and component buffers are taken.
    /// <para>已解析的 Boot 镜像，直接取其头部与各组件缓冲区。</para></param>
    /// <returns>Packer sharing the parser's <see cref="BootImageHeader"/> instance and component arrays; packing
    /// rewrites fields on the shared header, which the parser then reports too.
    /// <para>返回的打包器与解析器共享 <see cref="BootImageHeader"/> 实例和组件数组；打包会重写共享头部的字段，解析器随后报告的也是这些新值。</para></returns>
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

    /// <summary>Header serialized at the start of the image; packing refills its derived fields in place.
    /// <para>写出在镜像开头的头部；打包过程会就地回填其中的派生字段。</para></summary>
    /// <value>Never <see langword="null"/>. <see cref="PackToStream"/> rewrites the component size fields for the
    /// packed version, and on the V0–V2 path also the header size, recovery DTBO offset (V1+) and image <c>Ids</c>;
    /// page size and command line are only read from it.
    /// <para>永不为 <see langword="null"/>。<see cref="PackToStream"/> 会按所打包版本重写对应组件大小字段，且 V0–V2 路径还会重写头部大小、recovery DTBO 偏移（V1 及以上）与镜像 <c>Ids</c>；页大小与命令行只读不写。</para></value>
    public BootImageHeader Header { get; set; } = new BootImageHeader();

    /// <summary>Kernel bytes written page-aligned after the header.
    /// <para>紧随头部按页对齐写出的 kernel 字节。</para></summary>
    /// <value>Stored as-is without recompression; an empty array omits the segment and records size <c>0</c>. When a
    /// backing component-file source is set, its entry for this component takes precedence over the array content.
    /// <para>原样写出、不重新压缩；空数组时省略该区段并记录大小 <c>0</c>。当设置了组件文件后备来源时，该组件对应的文件条目优先于数组内容。</para></value>
    public byte[] Kernel { get; set; } = [];

    /// <summary>Ramdisk bytes written page-aligned after the kernel.
    /// <para>紧随 kernel 按页对齐写出的 ramdisk 字节。</para></summary>
    /// <value>Stored as-is; an empty array omits the segment and records size <c>0</c>. Component-file precedence
    /// applies as for <see cref="Kernel"/>.
    /// <para>原样写出；空数组时省略该区段并记录大小 <c>0</c>。组件文件优先规则与 <see cref="Kernel"/> 相同。</para></value>
    public byte[] Ramdisk { get; set; } = [];

    /// <summary>Second-stage bootloader bytes written page-aligned after the ramdisk (versions 0–2 only).
    /// <para>紧随 ramdisk 按页对齐写出的 second-stage 引导字节（仅版本 0–2）。</para></summary>
    /// <value>Stored as-is; an empty array omits the segment and records size <c>0</c>. Version 3 and 4 packing ignores
    /// this array. Component-file precedence applies as for <see cref="Kernel"/>.
    /// <para>原样写出；空数组时省略该区段并记录大小 <c>0</c>。版本 3/4 打包忽略该数组。组件文件优先规则与 <see cref="Kernel"/> 相同。</para></value>
    public byte[] Second { get; set; } = [];

    /// <summary>Device tree blob bytes written last among V0/V2 components (version 2 and above only).
    /// <para>在 V0/V2 组件中最后写出的设备树 blob 字节（仅版本 2 及以上）。</para></summary>
    /// <value>Stored as-is; an empty array omits the segment and records size <c>0</c>. Version 0/1 and V3/V4 packing
    /// ignores this array. Component-file precedence applies as for <see cref="Kernel"/>.
    /// <para>原样写出；空数组时省略该区段并记录大小 <c>0</c>。版本 0/1 与 V3/V4 打包忽略该数组。组件文件优先规则与 <see cref="Kernel"/> 相同。</para></value>
    public byte[] Dtb { get; set; } = [];

    /// <summary>Recovery DTBO bytes written page-aligned after the second-stage section (version 1 and above only).
    /// <para>紧随 second 区段按页对齐写出的 recovery DTBO 字节（仅版本 1 及以上）。</para></summary>
    /// <value>Stored as-is; an empty array omits the segment, records size <c>0</c> and leaves the header offset at
    /// <c>0</c>. Version 0 and V3/V4 packing ignores this array. Component-file precedence applies as for
    /// <see cref="Kernel"/>.
    /// <para>原样写出；空数组时省略该区段、记录大小 <c>0</c>，头部偏移保持 <c>0</c>。版本 0 与 V3/V4 打包忽略该数组。组件文件优先规则与 <see cref="Kernel"/> 相同。</para></value>
    public byte[] RecoveryDtbo { get; set; } = [];

    /// <summary>Signature bytes appended page-aligned after the ramdisk, written only for version 4.
    /// <para>紧随 ramdisk 按页对齐追加的签名字节，仅在版本 4 时写出。</para></summary>
    /// <value>Stored as-is; an empty array omits the segment and records signature size <c>0</c>. Other versions never
    /// write this array. Component-file precedence applies as for <see cref="Kernel"/>.
    /// <para>原样写出；空数组时省略该区段并记录签名大小 <c>0</c>。其他版本从不写出该数组。组件文件优先规则与 <see cref="Kernel"/> 相同。</para></value>
    public byte[] Signature { get; set; } = [];

    /// <summary>
    /// Packs the boot image to a file.
    /// <para>打包 Boot 镜像到文件。</para>
    /// </summary>
    /// <param name="outputPath">
    /// Destination path; the image is first written to a temporary file and the target is replaced only after the write completes.
    /// <para>目标文件路径；先写入临时文件，写入完成后才替换目标文件。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="outputPath"/> is <see langword="null"/>.
    /// <para><paramref name="outputPath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The target or one of its parent directories is a symbolic link or reparse point.
    /// <para>目标或其任一上级目录为符号链接或重解析点。</para></exception>
    public void PackToFile(string outputPath)
    {
        FileExtraction.WriteAtomic(outputPath, PackToStream);
    }

    /// <summary>
    /// Writes the image to the given stream.
    /// <para>将镜像写入指定流。</para>
    /// </summary>
    /// <param name="output">
    /// Writable stream positioned at the start of the image so that page alignment is measured from it; it is left open
    /// and not flushed beyond the writer's own buffering.
    /// <para>可写流，需定位在镜像起始处以正确计算页对齐；写入后保持打开，除 BinaryWriter 自身缓冲外不做额外刷新。</para></param>
    /// <exception cref="ArgumentException">The header version is unknown, the page size for V0–V2 is not a power of two
    /// from 2048 to 65536, or the command line exceeds the header field or contains a NUL character.
    /// <para>头部版本未知、V0–V2 页大小不是 2048 至 65536 之间的 2 的幂，或命令行超出头部字段长度或包含 NUL 字符。</para></exception>
    /// <exception cref="OverflowException">A component length or aligned offset on the V3/V4 path exceeds the stored
    /// <c>uint32</c> field range.
    /// <para>V3/V4 路径上的组件长度或对齐偏移超出 <c>uint32</c> 字段范围。</para></exception>
    public void PackToStream(Stream output)
    {
        if (Header.HeaderVersion is < BootImageVersion.V0 or > BootImageVersion.V4)
            throw new ArgumentException("Unknown boot image version.", nameof(Header.HeaderVersion));
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
    /// <returns>Complete image bytes, identical to what <see cref="PackToStream"/> would write to an empty stream.
    /// <para>完整镜像字节，与 <see cref="PackToStream"/> 写入空流所得内容一致。</para></returns>
    /// <exception cref="ArgumentException">The header version is unknown, the page size or command line is invalid; see <see cref="PackToStream"/>.
    /// <para>头部版本未知、页大小或命令行无效；参见 <see cref="PackToStream"/>。</para></exception>
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
        if (pageSize < 2048 || (pageSize & (pageSize - 1)) != 0 || pageSize > 65536)
            throw new ArgumentException("Boot page size must be a power of two from 2048 to 65536.");
        var commandLine = EncodeCommandLine(1534);
        Header.KernelSize = (uint)ComponentLength(nameof(Kernel), Kernel);
        Header.RamdiskSize = (uint)ComponentLength(nameof(Ramdisk), Ramdisk);
        Header.SecondSize = (uint)ComponentLength(nameof(Second), Second);
        Header.RecoveryDtboSize = Header.HeaderVersion >= BootImageVersion.V1 ? (uint)ComponentLength(nameof(RecoveryDtbo), RecoveryDtbo) : 0;
        Header.DtbSize = Header.HeaderVersion >= BootImageVersion.V2 ? (uint)ComponentLength(nameof(Dtb), Dtb) : 0;

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
        commandLine.AsSpan(0, Math.Min(commandLine.Length, 511)).CopyTo(cmdLine);
        writer.Write(cmdLine);

        var imageId = ComputeImageId();
        Header.Ids = new uint[8];
        for (var i = 0; i < 8; i++)
        {
            Header.Ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(imageId.AsSpan(i * 4, 4));
        }
        writer.Write(imageId);

        var extraCmdLine = new byte[1024];
        if (commandLine.Length > 511)
            commandLine.AsSpan(511).CopyTo(extraCmdLine);
        writer.Write(extraCmdLine);

        uint headerSize = Header.HeaderVersion switch
        {
            BootImageVersion.V0 => 1632,
            BootImageVersion.V1 => 1648,
            BootImageVersion.V2 => 1660,


            BootImageVersion.V3 => throw new NotSupportedException("Boot image V3 packing is not supported by this packer."),
            BootImageVersion.V4 => throw new NotSupportedException("Boot image V4 packing is not supported by this packer."),
            BootImageVersion.Unknown => throw new ArgumentException("Unknown boot image version.", nameof(Header.HeaderVersion)),
            _ => 1632
        };

        Header.HeaderSize = headerSize;
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

        if (ComponentLength(nameof(Kernel), Kernel) > 0)
        {
            WriteComponent(writer, nameof(Kernel), Kernel);
            WritePadding(writer, pageSize);
        }

        if (ComponentLength(nameof(Ramdisk), Ramdisk) > 0)
        {
            WriteComponent(writer, nameof(Ramdisk), Ramdisk);
            WritePadding(writer, pageSize);
        }

        if (ComponentLength(nameof(Second), Second) > 0)
        {
            WriteComponent(writer, nameof(Second), Second);
            WritePadding(writer, pageSize);
        }

        if (Header.HeaderVersion >= BootImageVersion.V1 && ComponentLength(nameof(RecoveryDtbo), RecoveryDtbo) > 0)
        {
            WriteComponent(writer, nameof(RecoveryDtbo), RecoveryDtbo);
            WritePadding(writer, pageSize);
        }

        if (Header.HeaderVersion >= BootImageVersion.V2 && ComponentLength(nameof(Dtb), Dtb) > 0)
        {
            WriteComponent(writer, nameof(Dtb), Dtb);
            WritePadding(writer, pageSize);
        }
    }

    private void PackV3OrV4(Stream output)
    {
        var commandLine = EncodeCommandLine(1535);
        var isV4 = Header.HeaderVersion == BootImageVersion.V4;
        uint pageSize = 4096;
        Header.KernelSize = checked((uint)ComponentLength(nameof(Kernel), Kernel));
        Header.RamdiskSize = checked((uint)ComponentLength(nameof(Ramdisk), Ramdisk));

        if (isV4)
        {
            Header.SignatureSize = checked((uint)ComponentLength(nameof(Signature), Signature));
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
        commandLine.CopyTo(cmdLine, 0);
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

        if (ComponentLength(nameof(Kernel), Kernel) > 0)
        {
            WriteComponent(writer, nameof(Kernel), Kernel);
            WritePadding(writer, pageSize);
        }

        if (ComponentLength(nameof(Ramdisk), Ramdisk) > 0)
        {
            WriteComponent(writer, nameof(Ramdisk), Ramdisk);
            WritePadding(writer, pageSize);
        }

        if (isV4 && ComponentLength(nameof(Signature), Signature) > 0)
        {
            WriteComponent(writer, nameof(Signature), Signature);
            WritePadding(writer, pageSize);
        }
    }

    /// <summary>
    /// Packs a boot image using the specified original image as a template.
    /// <para>打包 Boot 镜像，使用指定的原始镜像作为模板。</para>
    /// </summary>
    /// <param name="original">Parsed boot image supplying the header and every component that is not replaced.
    /// <para>提供头部以及所有未被替换组件的已解析 Boot 镜像。</para></param>
    /// <param name="kernel">Replacement kernel bytes, or <see langword="null"/> to keep the parsed kernel.
    /// <para>替换的 kernel 字节；为 <see langword="null"/> 时保留解析所得的 kernel。</para></param>
    /// <param name="ramdisk">Replacement ramdisk bytes, or <see langword="null"/> to keep the parsed ramdisk.
    /// <para>替换的 ramdisk 字节；为 <see langword="null"/> 时保留解析所得的 ramdisk。</para></param>
    /// <param name="second">Replacement second-stage bytes, or <see langword="null"/> to keep the parsed section.
    /// <para>替换的 second-stage 字节；为 <see langword="null"/> 时保留解析所得区段。</para></param>
    /// <param name="dtb">Replacement DTB bytes, or <see langword="null"/> to keep the parsed DTB.
    /// <para>替换的 DTB 字节；为 <see langword="null"/> 时保留解析所得 DTB。</para></param>
    /// <returns>Complete packed image bytes; replaced components also update their header size fields.
    /// <para>完整打包镜像字节；被替换的组件同时更新其头部大小字段。</para></returns>
    /// <exception cref="ArgumentException">The header version, page size or command line is invalid; see <see cref="PackToStream"/>.
    /// <para>头部版本、页大小或命令行无效；参见 <see cref="PackToStream"/>。</para></exception>
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

        using var ms = new MemoryStream();
        packer.PackToStream(ms);
        return ms.ToArray();
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

    private byte[] EncodeCommandLine(int maximumBytes)
    {
        var value = Header.CommandLine ?? string.Empty;
        var bytes = new UTF8Encoding(false, true).GetBytes(value);
        if (value.Contains('\0') || bytes.Length > maximumBytes)
            throw new ArgumentException($"Boot command line must contain at most {maximumBytes} UTF-8 bytes and no NUL.");
        return bytes;
    }

    private byte[] ComputeImageId()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        Append(nameof(Kernel), Kernel);
        Append(nameof(Ramdisk), Ramdisk);
        Append(nameof(Second), Second);
        if (Header.HeaderVersion >= BootImageVersion.V1) Append(nameof(RecoveryDtbo), RecoveryDtbo);
        if (Header.HeaderVersion >= BootImageVersion.V2) Append(nameof(Dtb), Dtb);
        var id = new byte[32];
        hash.GetHashAndReset().CopyTo(id, 0);
        return id;

        void Append(string name, byte[] component)
        {
            if (ComponentFiles is { } files)
            {
                files.AppendHash(name, hash);
                return;
            }
            hash.AppendData(component);
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)component.Length));
            hash.AppendData(size);
        }
    }

    private static uint AlignToPage(uint value, uint pageSize) => checked((value + pageSize - 1) / pageSize * pageSize);
}