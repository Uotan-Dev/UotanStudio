using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

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
    private const uint CanonicalV3HeaderSize = 1580;
    private const uint CanonicalV4HeaderSize = 1584;
    private const int MaximumHeaderBlobLength = 65536;
    internal BootComponentFiles? ComponentFiles { get; init; }

    private long ComponentLength(string name, byte[] data) => ComponentFiles?.Length(name) ?? data.LongLength;

    private void WriteComponent(Stream output, string name, byte[] data)
    {
        if (ComponentFiles is { } files) files.Copy(name, output);
        else output.Write(data);
    }

    private async Task WriteComponentAsync(Stream output, string name, byte[] data, CancellationToken cancellationToken)
    {
        if (ComponentFiles is { } files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Copy(name, output);
            return;
        }

        await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
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
    /// on the V3/V4 path it also rewrites the header size, OS version, header version, command line and the V4
    /// signature-size field. When <see cref="BootImageHeader.HeaderBlob"/> matches the packed layout, the V3/V4
    /// header is rebuilt from a copy of that blob so opaque reserved, hash and padding bytes survive a repack.
    /// The page size is read from it: V0–V2 must be a power of two from 2048 to 65536 and V3/V4 must be one of
    /// 2048, 4096, 8192 or 16384 (a value of <c>0</c> packs as 4096).
    /// <para>永不为 <see langword="null"/>。<see cref="PackToStream"/> 会按所打包版本重写对应组件大小字段，且 V0–V2 路径还会
    /// 重写头部大小、recovery DTBO 偏移（V1 及以上）与镜像 <c>Ids</c>；V3/V4 路径还会重写头部大小、OS 版本、头部版本与
    /// V4 签名大小字段。当 <see cref="BootImageHeader.HeaderBlob"/> 与本次打包布局匹配时，V3/V4 头部以该块的副本重建，
    /// 使不透明的 reserved、hash 与填充字节在重打包后保持不变。页大小取自该头部：V0–V2 必须是 2048 至 65536 之间
    /// 2 的幂，V3/V4 必须是 2048、4096、8192 或 16384（为 <c>0</c> 时按 4096 打包）。</para></value>
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

    /// <summary>Optional partition-role assertion enforced before packing.
    /// <para>打包前执行的可选分区角色约束断言。</para></summary>
    /// <value><see langword="null"/> (default) skips role enforcement and keeps byte-identity repacking unrestricted.
    /// <see cref="BootImageRole.InitBoot"/> requires header version 4 and an empty kernel segment: the generic ramdisk
    /// lives in init_boot while the kernel belongs to the boot image, so a non-empty kernel fails with
    /// <see cref="ArgumentException"/>. <see cref="BootImageRole.Recovery"/> requires header versions 0 through 2
    /// (the classic recovery layouts; recovery resources in the vendor-boot variant are packed by
    /// <c>VendorBootPacker</c> instead). <see cref="BootImageRole.Boot"/> adds no constraints.
    /// <para>默认 <see langword="null"/> 时不执行角色约束，保持逐字节一致重打包不受限。<see cref="BootImageRole.InitBoot"/>
    /// 要求头部版本 4 且 kernel 区段为空：generic ramdisk 位于 init_boot，而 kernel 属于 boot 镜像，因此非空 kernel 会以
    /// <see cref="ArgumentException"/> 失败。<see cref="BootImageRole.Recovery"/> 要求头部版本 0 至 2（经典 recovery 布局；
    /// vendor boot 变体的 recovery 资源由 <c>VendorBootPacker</c> 打包）。<see cref="BootImageRole.Boot"/> 不附加约束。</para></value>
    public BootImageRole? Role { get; set; }

    /// <summary>
    /// Enforces the optional <see cref="Role"/> assertion against the header version and the effective component
    /// lengths (component-file-backed lengths included).
    /// <para>依据头部版本与实际组件长度（含组件文件后备来源）执行可选的 <see cref="Role"/> 约束。</para>
    /// </summary>
    private void ValidateRole()
    {
        switch (Role)
        {
            case BootImageRole.InitBoot:
                if (Header.HeaderVersion != BootImageVersion.V4)
                    throw new ArgumentException(
                        "init_boot images require boot image header version 4. init_boot 镜像要求头部版本 4。",
                        nameof(Role));
                if (ComponentLength(nameof(Kernel), Kernel) > 0)
                    throw new ArgumentException(
                        "init_boot images must have an empty kernel segment: the generic ramdisk lives in init_boot " +
                        "while the kernel belongs to boot.img. init_boot 镜像的 kernel 区段必须为空。",
                        nameof(Kernel));
                break;
            case BootImageRole.Recovery when Header.HeaderVersion is > BootImageVersion.V2 or BootImageVersion.Unknown:
                throw new ArgumentException(
                    "Recovery boot images use header versions 0 through 2 or the vendor-boot variant; version " +
                    $"{(int)Header.HeaderVersion} is not packable under the recovery role. recovery 镜像使用头部版本 0 至 2 或 vendor boot 变体。",
                    nameof(Role));
            default:
                break;
        }
    }

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
    /// Asynchronously packs the boot image to a file, honoring cancellation during the write.
    /// <para>异步打包 Boot 镜像到文件，写入期间响应取消。</para>
    /// </summary>
    /// <param name="outputPath">
    /// Destination path; the image is first written to a temporary file and the target is replaced only after the write completes.
    /// <para>目标文件路径；先写入临时文件，写入完成后才替换目标文件。</para></param>
    /// <param name="cancellationToken">Token checked before the publish, during padding and between component transfers.
    /// <para>在发布前、填充期间与组件传输之间检查的取消令牌。</para></param>
    /// <returns>A task that completes once the image has been published to <paramref name="outputPath"/>.
    /// <para>在镜像成功发布到 <paramref name="outputPath"/> 后完成的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="outputPath"/> is <see langword="null"/>.
    /// <para><paramref name="outputPath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The target or one of its parent directories is a symbolic link or reparse point.
    /// <para>目标或其任一上级目录为符号链接或重解析点。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled; the existing
    /// target file, if any, is left untouched.
    /// <para><paramref name="cancellationToken"/> 已被触发；已有目标文件（如存在）保持不变。</para></exception>
    public Task PackToFileAsync(string outputPath, CancellationToken cancellationToken = default)
        => FileExtraction.WriteAtomicAsync(outputPath, output => PackToStreamAsync(output, cancellationToken),
            cancellationToken: cancellationToken);

    /// <summary>
    /// Writes the image to the given stream.
    /// <para>将镜像写入指定流。</para>
    /// </summary>
    /// <param name="output">
    /// Writable stream positioned at the start of the image so that page alignment is measured from it; it is left
    /// open and not flushed.
    /// <para>可写流，需定位在镜像起始处以正确计算页对齐；写入后保持打开且不刷新。</para></param>
    /// <exception cref="ArgumentException">The header version is unknown, the page size for V0–V2 is not a power of two
    /// from 2048 to 65536, the page size for V3/V4 is neither <c>0</c> nor one of 2048, 4096, 8192, 16384, the
    /// command line exceeds the header field or contains a NUL character, or the <see cref="Role"/> assertion fails
    /// (init_boot with a non-empty kernel or wrong version, recovery above version 2).
    /// <para>头部版本未知、V0–V2 页大小不是 2048 至 65536 之间的 2 的幂、V3/V4 页大小既不是 <c>0</c> 也不属于
    /// 2048、4096、8192、16384、命令行超出头部字段长度或包含 NUL 字符，或 <see cref="Role"/> 约束失败
    /// （init_boot 携带非空 kernel 或版本不符、recovery 高于版本 2）。</para></exception>
    /// <exception cref="OverflowException">A component length or aligned offset on the V3/V4 path exceeds the stored
    /// <c>uint32</c> field range.
    /// <para>V3/V4 路径上的组件长度或对齐偏移超出 <c>uint32</c> 字段范围。</para></exception>
    public void PackToStream(Stream output)
    {
        if (Header.HeaderVersion is < BootImageVersion.V0 or > BootImageVersion.V4)
            throw new ArgumentException("Unknown boot image version.", nameof(Header.HeaderVersion));
        ValidateRole();
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
    /// Asynchronously writes the image to the given stream, checking cancellation before each section and during
    /// padding and component transfers.
    /// <para>异步将镜像写入指定流，在每个区段开始前以及填充与组件传输期间响应取消。</para>
    /// </summary>
    /// <param name="output">
    /// Writable stream positioned at the start of the image so that page alignment is measured from it; it is left
    /// open and not flushed.
    /// <para>可写流，需定位在镜像起始处以正确计算页对齐；写入后保持打开且不刷新。</para></param>
    /// <param name="cancellationToken">Token forwarded to the asynchronous writes; components backed by component
    /// files are copied through bounded synchronous transfers, so the token is only checked around those sections.
    /// <para>传递给异步写入的取消令牌；由组件文件后备来源提供的组件以有界同步方式复制，令牌仅在这些区段前后检查。</para></param>
    /// <returns>A task that completes once the full image has been written.
    /// <para>在完整镜像写入完成后结束的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The header version is unknown, the page size for V0–V2 is not a power of two
    /// from 2048 to 65536, the page size for V3/V4 is neither <c>0</c> nor one of 2048, 4096, 8192, 16384, the
    /// command line exceeds the header field or contains a NUL character, or the <see cref="Role"/> assertion fails
    /// (init_boot with a non-empty kernel or wrong version, recovery above version 2).
    /// <para>头部版本未知、V0–V2 页大小不是 2048 至 65536 之间的 2 的幂、V3/V4 页大小既不是 <c>0</c> 也不属于
    /// 2048、4096、8192、16384、命令行超出头部字段长度或包含 NUL 字符，或 <see cref="Role"/> 约束失败
    /// （init_boot 携带非空 kernel 或版本不符、recovery 高于版本 2）。</para></exception>
    /// <exception cref="OverflowException">A component length or aligned offset on the V3/V4 path exceeds the stored
    /// <c>uint32</c> field range.
    /// <para>V3/V4 路径上的组件长度或对齐偏移超出 <c>uint32</c> 字段范围。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled; bytes already
    /// written stay written.
    /// <para><paramref name="cancellationToken"/> 已被触发；已写入的字节仍保留。</para></exception>
    public async Task PackToStreamAsync(Stream output, CancellationToken cancellationToken = default)
    {
        if (Header.HeaderVersion is < BootImageVersion.V0 or > BootImageVersion.V4)
            throw new ArgumentException("Unknown boot image version.", nameof(Header.HeaderVersion));
        ValidateRole();
        if (Header.HeaderVersion >= BootImageVersion.V3)
        {
            await PackV3OrV4Async(output, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await PackV0V2Async(output, cancellationToken).ConfigureAwait(false);
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
        var (header, headerSize, pageSize) = BuildV0V2Header();
        ArgumentNullException.ThrowIfNull(output);
        output.Write(header);
        ZeroFill.FillTo(output, headerSize);
        ZeroFill.AlignToPage(output, output.Position, pageSize);

        if (ComponentLength(nameof(Kernel), Kernel) > 0)
        {
            WriteComponent(output, nameof(Kernel), Kernel);
            ZeroFill.AlignToPage(output, output.Position, pageSize);
        }

        if (ComponentLength(nameof(Ramdisk), Ramdisk) > 0)
        {
            WriteComponent(output, nameof(Ramdisk), Ramdisk);
            ZeroFill.AlignToPage(output, output.Position, pageSize);
        }

        if (ComponentLength(nameof(Second), Second) > 0)
        {
            WriteComponent(output, nameof(Second), Second);
            ZeroFill.AlignToPage(output, output.Position, pageSize);
        }

        if (Header.HeaderVersion >= BootImageVersion.V1 && ComponentLength(nameof(RecoveryDtbo), RecoveryDtbo) > 0)
        {
            WriteComponent(output, nameof(RecoveryDtbo), RecoveryDtbo);
            ZeroFill.AlignToPage(output, output.Position, pageSize);
        }

        if (Header.HeaderVersion >= BootImageVersion.V2 && ComponentLength(nameof(Dtb), Dtb) > 0)
        {
            WriteComponent(output, nameof(Dtb), Dtb);
            ZeroFill.AlignToPage(output, output.Position, pageSize);
        }
    }

    private async Task PackV0V2Async(Stream output, CancellationToken cancellationToken)
    {
        var (header, headerSize, pageSize) = BuildV0V2Header();
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await ZeroFill.FillToAsync(output, headerSize, cancellationToken).ConfigureAwait(false);
        await ZeroFill.AlignToPageAsync(output, output.Position, pageSize, cancellationToken).ConfigureAwait(false);

        if (ComponentLength(nameof(Kernel), Kernel) > 0)
        {
            await WriteComponentAsync(output, nameof(Kernel), Kernel, cancellationToken).ConfigureAwait(false);
            await ZeroFill.AlignToPageAsync(output, output.Position, pageSize, cancellationToken).ConfigureAwait(false);
        }

        if (ComponentLength(nameof(Ramdisk), Ramdisk) > 0)
        {
            await WriteComponentAsync(output, nameof(Ramdisk), Ramdisk, cancellationToken).ConfigureAwait(false);
            await ZeroFill.AlignToPageAsync(output, output.Position, pageSize, cancellationToken).ConfigureAwait(false);
        }

        if (ComponentLength(nameof(Second), Second) > 0)
        {
            await WriteComponentAsync(output, nameof(Second), Second, cancellationToken).ConfigureAwait(false);
            await ZeroFill.AlignToPageAsync(output, output.Position, pageSize, cancellationToken).ConfigureAwait(false);
        }

        if (Header.HeaderVersion >= BootImageVersion.V1 && ComponentLength(nameof(RecoveryDtbo), RecoveryDtbo) > 0)
        {
            await WriteComponentAsync(output, nameof(RecoveryDtbo), RecoveryDtbo, cancellationToken).ConfigureAwait(false);
            await ZeroFill.AlignToPageAsync(output, output.Position, pageSize, cancellationToken).ConfigureAwait(false);
        }

        if (Header.HeaderVersion >= BootImageVersion.V2 && ComponentLength(nameof(Dtb), Dtb) > 0)
        {
            await WriteComponentAsync(output, nameof(Dtb), Dtb, cancellationToken).ConfigureAwait(false);
            await ZeroFill.AlignToPageAsync(output, output.Position, pageSize, cancellationToken).ConfigureAwait(false);
        }
    }

    private (byte[] HeaderBytes, uint HeaderSize, uint PageSize) BuildV0V2Header()
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

        using var buffer = new MemoryStream();
        var writer = new BinaryWriter(buffer);

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

        writer.Flush();
        return (buffer.ToArray(), headerSize, pageSize);
    }

    private void PackV3OrV4(Stream output)
    {
        var (header, effectiveHeaderSize, pageSize, isV4) = BuildV3OrV4Header();
        ArgumentNullException.ThrowIfNull(output);
        output.Write(header);
        ZeroFill.FillTo(output, effectiveHeaderSize);
        ZeroFill.AlignToPage(output, output.Position, pageSize);

        if (ComponentLength(nameof(Kernel), Kernel) > 0)
        {
            WriteComponent(output, nameof(Kernel), Kernel);
            ZeroFill.AlignToPage(output, output.Position, pageSize);
        }

        if (ComponentLength(nameof(Ramdisk), Ramdisk) > 0)
        {
            WriteComponent(output, nameof(Ramdisk), Ramdisk);
            ZeroFill.AlignToPage(output, output.Position, pageSize);
        }

        if (isV4 && ComponentLength(nameof(Signature), Signature) > 0)
        {
            WriteComponent(output, nameof(Signature), Signature);
            ZeroFill.AlignToPage(output, output.Position, pageSize);
        }
    }

    private async Task PackV3OrV4Async(Stream output, CancellationToken cancellationToken)
    {
        var (header, effectiveHeaderSize, pageSize, isV4) = BuildV3OrV4Header();
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await ZeroFill.FillToAsync(output, effectiveHeaderSize, cancellationToken).ConfigureAwait(false);
        await ZeroFill.AlignToPageAsync(output, output.Position, pageSize, cancellationToken).ConfigureAwait(false);

        if (ComponentLength(nameof(Kernel), Kernel) > 0)
        {
            await WriteComponentAsync(output, nameof(Kernel), Kernel, cancellationToken).ConfigureAwait(false);
            await ZeroFill.AlignToPageAsync(output, output.Position, pageSize, cancellationToken).ConfigureAwait(false);
        }

        if (ComponentLength(nameof(Ramdisk), Ramdisk) > 0)
        {
            await WriteComponentAsync(output, nameof(Ramdisk), Ramdisk, cancellationToken).ConfigureAwait(false);
            await ZeroFill.AlignToPageAsync(output, output.Position, pageSize, cancellationToken).ConfigureAwait(false);
        }

        if (isV4 && ComponentLength(nameof(Signature), Signature) > 0)
        {
            await WriteComponentAsync(output, nameof(Signature), Signature, cancellationToken).ConfigureAwait(false);
            await ZeroFill.AlignToPageAsync(output, output.Position, pageSize, cancellationToken).ConfigureAwait(false);
        }
    }

    private (byte[] HeaderBytes, uint HeaderSize, uint PageSize, bool IsV4) BuildV3OrV4Header()
    {
        var commandLine = EncodeCommandLine(1535);
        var isV4 = Header.HeaderVersion == BootImageVersion.V4;
        var canonicalHeaderSize = isV4 ? CanonicalV4HeaderSize : CanonicalV3HeaderSize;
        var pageSize = ResolveV3V4PageSize();
        Header.KernelSize = checked((uint)ComponentLength(nameof(Kernel), Kernel));
        Header.RamdiskSize = checked((uint)ComponentLength(nameof(Ramdisk), Ramdisk));

        if (isV4)
        {
            Header.SignatureSize = checked((uint)ComponentLength(nameof(Signature), Signature));
        }

        var effectiveHeaderSize = Header.HeaderSize >= canonicalHeaderSize ? Header.HeaderSize : canonicalHeaderSize;

        if (TryBuildHeaderFromBlob(isV4, effectiveHeaderSize, pageSize, commandLine, out var blobHeaderBytes))
        {
            return (blobHeaderBytes!, effectiveHeaderSize, pageSize, isV4);
        }

        using var buffer = new MemoryStream();
        var writer = new BinaryWriter(buffer);

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

        writer.Flush();
        return (buffer.ToArray(), effectiveHeaderSize, pageSize, isV4);
    }

    /// <summary>
    /// Resolves the page size honored by the V3/V4 packing path, defaulting unset headers to 4096 and rejecting
    /// sizes the parser cannot re-detect (the V3/V4 header itself stores no page size).
    /// <para>解析 V3/V4 打包路径使用的页大小：未设置时默认 4096，并拒绝解析器无法重新检测的尺寸
    /// （V3/V4 头部本身不存储页大小）。</para>
    /// </summary>
    private uint ResolveV3V4PageSize()
    {
        if (Header.PageSize == 0)
        {
            return 4096;
        }

        if (Header.PageSize is not (2048u or 4096u or 8192u or 16384u))
        {
            throw new ArgumentException("Boot V3/V4 page size must be 2048, 4096, 8192 or 16384.");
        }

        return Header.PageSize;
    }

    /// <summary>
    /// Rebuilds the V3/V4 header block from the parser-captured raw bytes, overwriting only the fields the packer
    /// owns at their exact offsets so that opaque reserved, hash and padding regions stay byte-identical.
    /// <para>以解析器捕获的原始字节重建 V3/V4 头部块，仅在其精确偏移处覆写打包器自有的字段，
    /// 使不透明的 reserved、hash 与填充区域逐字节保持不变。</para>
    /// </summary>
    /// <param name="isV4">Whether the packed version is 4; the V4 signature-size field is owned by the packer.
    /// <para>打包版本是否为 4；V4 签名大小字段归打包器所有。</para></param>
    /// <param name="effectiveHeaderSize">Header size written at the declared offset, already clamped to the canonical
    /// size for the version.
    /// <para>写入声明偏移处的头部大小，已按该版本的规范尺寸钳制。</para></param>
    /// <param name="pageSize">Page size of the packed layout; the blob must cover exactly the page-aligned header
    /// region.
    /// <para>打包布局的页大小；blob 必须恰好覆盖页对齐的头部区域。</para></param>
    /// <param name="commandLine">Encoded command-line bytes written into the 1536-byte field.
    /// <para>写入 1536 字节命令行字段的编码字节。</para></param>
    /// <param name="headerBytes">On success, the patched copy of the blob; <see langword="null"/> when the blob is
    /// absent or does not match this packing.
    /// <para>成功时为 blob 的修改副本；blob 缺失或与本次打包不匹配时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the header was rebuilt from the blob.
    /// <para>头部确实由 blob 重建时返回 <see langword="true"/>。</para></returns>
    private bool TryBuildHeaderFromBlob(bool isV4, uint effectiveHeaderSize, uint pageSize, byte[] commandLine, out byte[]? headerBytes)
    {
        headerBytes = null;
        var blob = Header.HeaderBlob;
        if (blob is null || blob.Length == 0)
        {
            return false;
        }

        var canonicalHeaderSize = isV4 ? CanonicalV4HeaderSize : CanonicalV3HeaderSize;
        var alignedHeaderLength = ((long)effectiveHeaderSize + pageSize - 1) / pageSize * pageSize;
        if (blob.Length != alignedHeaderLength || blob.Length > MaximumHeaderBlobLength || blob.Length < canonicalHeaderSize
            || !blob.AsSpan(0, 8).SequenceEqual("ANDROID!"u8))
        {
            return false;
        }

        var header = (byte[])blob.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Header.KernelSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), Header.RamdiskSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), Header.OsVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), effectiveHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), isV4 ? 4u : 3u);
        header.AsSpan(44, 1536).Clear();
        commandLine.CopyTo(header.AsSpan(44));
        if (isV4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1580), Header.SignatureSize);
        }

        headerBytes = header;
        return true;
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
    /// <returns>Complete packed image bytes; replaced components also update their header size fields. For version
    /// 3/4 images parsed from a source, an unmodified repack reproduces the source bytes because the parser-captured
    /// header block is re-emitted with only the owned fields re-patched.
    /// <para>完整打包镜像字节；被替换的组件同时更新其头部大小字段。对于从源镜像解析得到的版本 3/4 镜像，
    /// 未修改的重打包会复现源字节，因为解析器捕获的头部块仅在其自有字段被重新回填后原样写出。</para></returns>
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