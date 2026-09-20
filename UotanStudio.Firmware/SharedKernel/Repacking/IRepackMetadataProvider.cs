namespace UotanStudio.Firmware.SharedKernel.Repacking;

/// <summary>
/// Supplies format-owned rebuild configuration and optional preservation artifacts.
/// The shared metadata lifecycle owns context resolution and publishes the JSON only after capture succeeds.
/// </summary>
public interface IRepackMetadataProvider
{
    /// <summary>
    /// Sets configuration, packing options and directory support from the source format.
    /// Packing notes supplied here are appended to the common capability description.
    /// </summary>
    void ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory);

    /// <summary>
    /// Captures format-specific artifacts before metadata publication, using the already resolved directory.
    /// Formats without preservation artifacts use the default implementation.
    /// </summary>
    Task CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;
}