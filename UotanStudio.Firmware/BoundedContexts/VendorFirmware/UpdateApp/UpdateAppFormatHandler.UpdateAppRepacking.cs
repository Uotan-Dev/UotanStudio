using System.Text.Json.Nodes;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.UpdateApp.Build;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.UpdateApp;

public sealed partial class UpdateAppFormatHandler
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        metadata.CanPackDirectory = UpdateAppFaithfulRepacker.TryValidateCapture(sourcePath, CancellationToken.None);
        metadata.PackingNotes = metadata.CanPackDirectory
            ? " Packing the extraction directory restores the source container byte for byte (entry order, raw headers, " +
              "non-payload gaps); packing any other directory builds the canonical unsigned container."
            : " This container exceeds the faithful-capture bounds; the directory packer can only build the canonical unsigned container.";
    }

    Task IRepackMetadataProvider.CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath,
        string outputDirectory, string metadataDirectory, CancellationToken cancellationToken)
    {
        if (!metadata.CanPackDirectory)
            return Task.CompletedTask;
        UpdateAppFaithfulRepacker.Capture(metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken);
        return Task.CompletedTask;
    }

    private static JsonObject? ReadFaithfulConfiguration(string inputPath)
        => RepackMetadata.Read(inputPath)?.Configuration["updateApp"] as JsonObject;
}