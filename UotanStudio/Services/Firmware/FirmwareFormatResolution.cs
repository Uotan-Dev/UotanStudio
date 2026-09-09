using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Services.Firmware;

/// <summary>Explicit adapter policies for names, aliases and packing defaults.</summary>
internal static class FirmwareFormatResolution
{
    /// <summary>Preserves the MCP adapter's existing format resolution policy.</summary>
    internal static IReadonlyFormatHandler? ResolveHint(string? format, string filePath)
    {
        FirmwareFormatRegistry.RegisterAll();
        if (string.IsNullOrWhiteSpace(format))
        {
            return FormatDetector.Detect(filePath);
        }

        var key = format.Trim().TrimStart('.').ToLowerInvariant();
        foreach (var handler in FormatDetector.RegisteredHandlers)
        {
            if (handler.FormatName.Equals(format, StringComparison.OrdinalIgnoreCase))
            {
                return handler;
            }

            foreach (var ext in handler.FileExtensions)
            {
                if (ext.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return handler;
                }
            }
        }

        var canonical = key switch
        {
            "boot" or "bootimg" or "vendor_boot" or "vendorboot" => "Android Boot / Vendor Boot Image",
            "cpio" or "tar" or "cpiogz" => "CPIO/TAR Archive",
            "payload" or "payloadbin" or "payload.bin" or "ota" => "Android OTA Payload (payload.bin)",
            "oppo" or "ofp" or "ops" or "ozip" => "Oppo Firmware (OFP/OPS/OZIP)",
            "super" or "lp" or "superimage" => "Android Logical Partition (super)",
            _ => null,
        };
        if (canonical is null)
        {
            return null;
        }

        return RegisteredFirmwareFormats.FindByName(canonical);
    }

    /// <summary>Preserves the MCP adapter's existing format resolution policy.</summary>
    internal static string? ResolveMcpPackFormat(string? format, string sourceDir, string outputPath)
    {
        if (!string.IsNullOrWhiteSpace(format))
        {
            var f = format.Trim().ToLowerInvariant();
            return f is "cpio" or "boot" or "ext4" or "erofs" ? f : null;
        }

        var metadata = RepackMetadata.Read(sourceDir);
        if (metadata is not null)
        {
            if (!metadata.CanPackDirectory) throw new InvalidDataException(metadata.PackingNotes);
            return metadata.FormatName;
        }
        var ext = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "cpio" => "cpio",
            "erofs" => "erofs",
            "ext4" => "ext4",
            _ => "cpio",
        };
    }
}
