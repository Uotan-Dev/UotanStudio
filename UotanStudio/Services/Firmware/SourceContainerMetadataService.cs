using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using UotanStudio.Firmware.BoundedContexts.LogicalPartition.Lp;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Firmware;

/// <summary>Bounded, source-generated project configuration storage for Super and Payload.</summary>
internal static class SourceContainerMetadataService
{
    private const long MaximumBytes = 32 * 1024 * 1024;

    internal static string GetPath(string projectRoot, string kind) =>
        Path.Combine(projectRoot, ".uotan", "source-" + kind + ".json");

    internal static DeliverySourceContainerSettings? Read(string projectRoot, string kind)
    {
        var path = GetPath(projectRoot, kind);
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        if (input.Length > MaximumBytes) throw new InvalidDataException("Source configuration exceeds its size limit.");
        return JsonSerializer.Deserialize(input, SourceContainerJsonContext.Default.DeliverySourceContainerSettings);
    }

    internal static DeliverySourceContainerSettings Capture(string source, ContainerKind kind)
    {
        if (kind == ContainerKind.Super)
        {
            var metadata = MetadataReader.ReadFromImageFile(source);
            var header = UotanStudio.Firmware.SharedKernel.FormatDetector.ReadHeader(source, 4);
            return new DeliverySourceContainerSettings
            {
                SourceName = Path.GetFileName(source),
                Kind = "super",
                Storage = header.AsSpan().SequenceEqual(new byte[] { 0x3a, 0xff, 0x26, 0xed }) ? "SPARSE" : "RAW",
                DeviceType = (metadata.Header.Flags & 1) != 0 ? "VAB"
                    : metadata.PartitionNames.Any(name => name.EndsWith("_b", StringComparison.Ordinal)) ? "AB" : "AOnly",
                Size = metadata.BlockDevices.Count > 0 ? metadata.BlockDevices[0].Size : 0,
                BlockSize = metadata.Geometry.LogicalBlockSize,
                MetadataSlots = metadata.Geometry.MetadataSlotCount,
                MetadataMaxSize = metadata.Geometry.MetadataMaxSize,
                MinorVersion = metadata.Header.MinorVersion,
                GroupNames = metadata.Groups.Select(group => group.NameString).ToArray(),
                GroupSizes = metadata.Groups.Select(group => group.MaximumSize).ToArray(),
                PartitionNames = metadata.PartitionNames.ToArray(),
                OriginalStructures = [metadata.Geometry.ToBytes(), metadata.Header.ToBytes(),
                    .. metadata.Partitions.Select(part => part.ToBytes()),
                    .. metadata.Extents.Select(extent => extent.ToBytes()),
                    .. metadata.Groups.Select(group => group.ToBytes()),
                    .. metadata.BlockDevices.Select(device => device.ToBytes())]
            };
        }

        var payload = PayloadParser.GetPayloadMetadata(source);
        // Save configuration, not the potentially enormous per-operation delta manifest.
        return new DeliverySourceContainerSettings
        {
            SourceName = Path.GetFileName(source),
            Kind = "payload",
            DeviceType = payload.DynamicPartitionMetadata.SnapshotEnabled ? "VAB" : "AB",
            BlockSize = payload.BlockSize,
            MinorVersion = payload.Manifest.MinorVersion,
            MaxTimestamp = payload.MaxTimestamp,
            SecurityPatchLevel = payload.Manifest.SecurityPatchLevel,
            PartitionSizes = payload.Manifest.Partitions.Select(partition => partition.NewPartitionInfo?.Size ?? 0).ToArray(),
            PartitionNames = payload.PartitionNames.ToArray(),
            GroupNames = payload.DynamicPartitionMetadata.Groups.Select(group => group.Name).ToArray(),
            GroupSizes = payload.DynamicPartitionMetadata.Groups.Select(group => group.Size).ToArray(),
            OriginalStructures = [payload.DynamicPartitionMetadata.ToByteArray()]
        };
    }

    internal static async Task SaveAsync(string source, ContainerKind kind)
    {
        var root = ProjectContextService.Instance.ActiveSubProject?.RootPath
            ?? throw new InvalidOperationException("No active sub-project.");
        if (kind is not (ContainerKind.Super or ContainerKind.Payload or ContainerKind.Zip)) return;
        await Task.Run(async () =>
        {
            if (kind == ContainerKind.Zip && !ContainerStructureAnalyzer.Analyze(source).Entries.Any(entry => entry.FromPayload)) return;
            var settings = Capture(source, kind);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, SourceContainerJsonContext.Default.DeliverySourceContainerSettings);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Source configuration exceeds its size limit.");
            var path = GetPath(root, settings.Kind);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }).ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DeliverySourceContainerSettings))]
internal partial class SourceContainerJsonContext : JsonSerializerContext;