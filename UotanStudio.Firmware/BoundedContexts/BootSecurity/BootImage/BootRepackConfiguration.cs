using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

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
                || header.HeaderVersion is < BootImageVersion.V0 or > BootImageVersion.V4
                || header.HeaderVersion >= BootImageVersion.V3 && header.HeaderSize > 4096)
                throw new InvalidDataException("Unsupported boot header version.");
            files.Add("Kernel", "kernel", header.KernelSize);
            files.Add("Ramdisk", "ramdisk", header.RamdiskSize);
            files.Add("Second", "second", header.SecondSize);
            files.Add("Dtb", "dtb", header.DtbSize);
            files.Add("RecoveryDtbo", "dtbo", header.RecoveryDtboSize);
            files.Add("Signature", "signature", header.SignatureSize);
            var packer = new BootImagePacker
            {
                Header = header,
                ComponentFiles = files
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