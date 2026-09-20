using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Services.Firmware;

/// <summary>Shared registered-format lookup and directory packing, without adapter-specific defaults.</summary>
internal static class RegisteredFirmwareFormats
{
    /// <summary>Looks up a name without implicitly registering formats or normalizing the caller's input.</summary>
    internal static IReadonlyFormatHandler? FindByName(string name, StringComparison comparison = StringComparison.Ordinal)
        => FormatDetector.RegisteredHandlers.FirstOrDefault(handler => string.Equals(handler.FormatName, name, comparison));

    /// <summary>Packs a metadata-selected directory with the existing exact-name matching contract.</summary>
    internal static async Task<FirmwareOpResult> PackDirectoryAsync(
        string format, string source, string output, CancellationToken cancellationToken = default,
        bool throwIfUnsupported = false, IProgress<FileSystemByteProgress>? progress = null)
    {
        FirmwareFormatRegistry.RegisterAll();
        if (FindByName(format) is not IWritableFormatHandler writer)
        {
            var error = $"No directory packer is registered for {format}.";
            if (throwIfUnsupported) throw new InvalidDataException(error);
            return FirmwareOpResult.Fail(error);
        }
        var result = await writer.PackAsync(source, output, progress, cancellationToken).ConfigureAwait(false);
        return new FirmwareOpResult { Success = result.Success, Summary = result.Summary };
    }
}