using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;

namespace UotanStudio.Firmware.BootSecurity.BootImage;

internal sealed class BootRepackConfiguration
{
    public string Magic { get; set; } = string.Empty;
    public BootImageHeader? Header { get; set; }
    public VendorBootHeader? VendorHeader { get; set; }
    public List<VendorRamdiskTableEntry> RamdiskTable { get; set; } = [];

    /// <summary>
    /// Non-null when the source image carries a trailing AVB footer. Rebuilding would drop
    /// that footer and its vbmeta, breaking verified boot, so packing is refused.
    /// <para>源镜像尾部含 AVB footer 时非空。重打包会丢弃该 footer 及其 vbmeta，破坏验证启动，因此拒绝打包。</para>
    /// </summary>
    public AvbFooterState? Footer { get; set; }

    internal static JsonObject FromImage(string path)
    {
        using var input = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[8];
        input.ReadExactly(magic);
        BootRepackConfiguration configuration;
        if (magic.SequenceEqual("ANDROID!"u8))
        {
            configuration = new() { Magic = "ANDROID!", Header = BootImageParser.ReadHeaderFile(path) };
        }
        else if (magic.SequenceEqual("VNDRBOOT"u8))
        {
            using var parser = new VendorBootParser();
            parser.ParseFile(path);
            configuration = new() { Magic = "VNDRBOOT", VendorHeader = parser.Header, RamdiskTable = [.. parser.RamdiskTable] };
        }
        else throw new InvalidDataException("Unsupported boot image magic.");
        if (AvbFooter.TryReadFile(path) is { } footer)
            configuration.Footer = new AvbFooterState
            {
                VersionMajor = footer.VersionMajor,
                VersionMinor = footer.VersionMinor,
                OriginalImageSize = footer.OriginalImageSize,
                VbmetaOffset = footer.VbmetaOffset,
                VbmetaSize = footer.VbmetaSize
            };
        return (JsonObject)JsonSerializer.SerializeToNode(configuration, BootRepackJsonContext.Default.BootRepackConfiguration)!;
    }

    /// <summary>
    /// Validates the header geometry of a version 3/4 boot configuration against its page size and the parser's
    /// capture bound, replacing the former flat <c>16384</c> ceiling: the declared header size must be at least the
    /// canonical size for the version and its page-aligned extent must stay within the 64 KiB header block the
    /// unpack pipeline can persist, so 16 KiB-page devices pack while corrupt size words are refused.
    /// <para>按页大小与解析器捕获上限校验版本 3/4 配置的头部几何信息，取代原先固定的 <c>16384</c> 上限：声明的头部大小
    /// 不得小于该版本的规范尺寸，且其页对齐范围必须落在解包管道可持久化的 64 KiB 头部块内，从而支持 16 KiB 页设备，同时拒绝损坏的大小字段。</para>
    /// </summary>
    /// <param name="header">Header restored from the stored repack configuration.
    /// <para>从已保存的重打包配置恢复的头部。</para></param>
    /// <exception cref="InvalidDataException">The page size is not a supported V3/V4 size, the header size is below
    /// the canonical minimum, or the page-aligned header extent exceeds the 64 KiB capture bound.
    /// <para>页大小不属于 V3/V4 支持值、头部大小低于规范最小值，或页对齐的头部范围超过 64 KiB 捕获上限。</para></exception>
    private static void ValidateV3V4HeaderGeometry(BootImageHeader header)
    {
        const uint MaximumHeaderBlobSize = 65536;
        var pageSize = header.PageSize == 0 ? 4096u : header.PageSize;
        if (header.PageSize != 0 && header.PageSize is not (2048u or 4096u or 8192u or 16384u))
            throw new InvalidDataException(
                $"Boot V3/V4 page size must be 2048, 4096, 8192 or 16384; found {header.PageSize}.");
        var canonicalHeaderSize = header.HeaderVersion == BootImageVersion.V4 ? 1584u : 1580u;
        var alignedHeaderExtent = checked(((long)header.HeaderSize + pageSize - 1) / pageSize * pageSize);
        if (header.HeaderSize < canonicalHeaderSize
            || header.HeaderSize > MaximumHeaderBlobSize
            || alignedHeaderExtent > MaximumHeaderBlobSize)
            throw new InvalidDataException(
                $"Boot header size {header.HeaderSize} is invalid for version {(int)header.HeaderVersion} with page size {pageSize}; " +
                $"expected at least {canonicalHeaderSize} within a {MaximumHeaderBlobSize}-byte page-aligned header block.");
    }

    internal static void Pack(string directory, string outputPath, CancellationToken cancellationToken, RepackMetadataContext context)
    {
        var metadata = RepackMetadata.Read(directory, context);
        if (metadata?.FormatName != new BootImageFormatHandler().FormatName)
            throw new InvalidDataException("Missing Boot repack metadata.");
        var configuration = metadata.Configuration.Deserialize(BootRepackJsonContext.Default.BootRepackConfiguration)
            ?? throw new InvalidDataException("Missing Boot configuration.");
        if (configuration.Footer is not null)
            throw new InvalidDataException(
                "The source boot image carries an AVB footer; rebuilding this directory would drop the footer and its vbmeta and can break verified boot. Use an AVB-aware re-signing workflow.");
        if (Path.GetFullPath(outputPath).Equals(Path.GetFullPath(Path.Combine(directory, RepackMetadata.FileName)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Boot output must not replace its configuration.");

        using var files = new BootComponentFiles(directory, outputPath, cancellationToken);

        Action<Stream> pack;
        if (configuration is { Magic: "ANDROID!", Header: { } header, VendorHeader: null })
        {
            if (metadata.Configuration["header"]?["headerVersion"] is null
                || header.HeaderVersion is < BootImageVersion.V0 or > BootImageVersion.V4)
                throw new InvalidDataException("Unsupported boot header version.");
            if (header.HeaderVersion >= BootImageVersion.V3)
                ValidateV3V4HeaderGeometry(header);
            var role = BootImageRoleResolver.Detect(header.HeaderVersion, header.KernelSize, header.RamdiskSize,
                RepackMetadata.DetectPartitionName(metadata.SourceFileName));
            files.Add("Kernel", "kernel", header.KernelSize);
            files.Add("Ramdisk", "ramdisk", header.RamdiskSize);
            files.Add("Second", "second", header.SecondSize);
            files.Add("Dtb", "dtb", header.DtbSize);
            files.Add("RecoveryDtbo", "dtbo", header.RecoveryDtboSize);
            files.Add("Signature", "signature", header.SignatureSize);
            var packer = new BootImagePacker
            {
                Header = header,
                ComponentFiles = files,
                Role = role
            };
            pack = packer.PackToStream;
        }
        else if (configuration is { Magic: "VNDRBOOT", VendorHeader: { } vendor, Header: null })
        {
            if (vendor.HeaderVersion is not (3 or 4) || vendor.PageSize is < 4096 or > 65536
                || (vendor.PageSize & (vendor.PageSize - 1)) != 0 || vendor.VendorHeaderSize > 65536
                || configuration.RamdiskTable is null || configuration.RamdiskTable.Count > 65536
                || configuration.RamdiskTable.Any(e => e is null || e.RamdiskBoardId is not { Length: 16 }))
                throw new InvalidDataException("Invalid vendor boot configuration.");
            files.Add("VendorRamdisk", "vendor_ramdisk", vendor.VendorRamdiskSize);
            files.Add("Dtb", "dtb", vendor.DtbSize);
            files.Add("Bootconfig", "bootconfig", vendor.VendorBootconfigSize);
            var packer = new VendorBootPacker
            {
                Header = vendor,
                ComponentFiles = files
            };
            packer.RamdiskTable.AddRange(configuration.RamdiskTable);
            pack = packer.PackToStream;
        }
        else throw new InvalidDataException("Invalid boot configuration magic or header.");
        FileExtraction.WriteAtomic(outputPath, output =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            pack(output);
            cancellationToken.ThrowIfCancellationRequested();
        });
    }
}

/// <summary>
/// Persisted description of a source image's trailing AVB footer, kept for inspection and to
/// gate packing. Only the footer fields are recorded; the vbmeta payload itself is never rebuilt.
/// <para>记录源镜像尾部 AVB footer 的字段，供检视并约束打包。仅保存 footer 字段，绝不重建 vbmeta 载荷。</para>
/// </summary>
internal sealed class AvbFooterState
{
    public uint VersionMajor { get; set; }
    public uint VersionMinor { get; set; }
    public ulong OriginalImageSize { get; set; }
    public ulong VbmetaOffset { get; set; }
    public ulong VbmetaSize { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    IgnoreReadOnlyProperties = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BootRepackConfiguration))]
[JsonSerializable(typeof(AvbFooterState))]
internal partial class BootRepackJsonContext : JsonSerializerContext;