namespace UotanStudio.Services.Firmware;

/// <summary>Shares service-based extraction while preserving each adapter's supported operations.</summary>
internal static class FirmwareUnpackDispatcher
{
    /// <summary>Dispatches cascade extraction; direct CLI handler extraction remains a separate operation.</summary>
    internal static Task<FirmwareOpResult> UnpackAsync(
        string format, string source, string output, string unsupportedMessage,
        bool allowSparse = false, bool runInBackground = false,
        string? partition = null, CancellationToken cancellationToken = default)
    {
        using var metadataScope = UotanStudio.Firmware.SharedKernel.RepackMetadata.UseContext(
            Projects.ProjectContextService.Instance.CaptureMetadataContext(output));
        var service = FirmwareService.Instance;
        Func<Task<FirmwareOpResult>>? unpack = format switch
        {
            "Android Boot / Vendor Boot Image" => () => service.UnpackBootAsync(source, output, cancellationToken),
            "CPIO/TAR Archive" => () => service.UnpackCpioAsync(source, output),
            "Android OTA Payload (payload.bin)" => () => service.ExtractPayloadAsync(source, output, partition),
            "Oppo Firmware (OFP/OPS/OZIP)" => () => service.ExtractOppoAsync(source, output, overwrite: true, cancellationToken),
            "Android Logical Partition (super)" => () => service.UnpackSuperAsync(source, output, cancellationToken),
            "Android Sparse Image (simg)" when allowSparse => () => service.UnpackSparseImageAsync(source, output, cancellationToken),
            "EXT4" or "EROFS" or "F2FS" => () => service.UnpackFileSystemImageAsync(source, output, cancellationToken: cancellationToken),
            _ => null
        };
        if (unpack is null) return Task.FromResult(FirmwareOpResult.Fail(unsupportedMessage));
        // Preserve UI scheduling and the existing cancellation support of each individual service.
        return runInBackground ? Task.Run(unpack) : unpack();
    }
}