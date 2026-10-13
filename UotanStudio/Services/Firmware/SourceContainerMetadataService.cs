using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// Bounded, source-generated project configuration storage for Super and Payload containers.
/// <para>面向 Super 与 Payload 容器的有界源生成工程配置存储。</para>
/// </summary>
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
        var settings = JsonSerializer.Deserialize(input, SourceContainerJsonContext.Default.DeliverySourceContainerSettings);
        return settings is null ? null : Normalize(settings);
    }

    /// <summary>
    /// Replaces the null string and list fields a deserialized configuration can carry with empty or
    /// default values.
    /// <para>把反序列化配置中可能为 null 的字符串与列表字段替换为空值或默认值。</para>
    /// </summary>
    /// <param name="settings">The deserialized configuration to normalize. <para>待归一化的反序列化配置。</para></param>
    /// <returns>A configuration whose reference fields are safe to read. <para>引用字段可安全读取的配置。</para></returns>
    private static DeliverySourceContainerSettings Normalize(DeliverySourceContainerSettings settings) => settings with
    {
        SourceName = settings.SourceName ?? string.Empty,
        Kind = settings.Kind ?? string.Empty,
        DeviceName = settings.DeviceName ?? string.Empty,
        Storage = settings.Storage ?? "RAW",
        DeviceType = settings.DeviceType ?? "Unknown",
        SecurityPatchLevel = settings.SecurityPatchLevel ?? string.Empty,
        BuildFingerprint = settings.BuildFingerprint ?? string.Empty,
        Incremental = settings.Incremental ?? string.Empty,
        GroupNames = settings.GroupNames ?? [],
        GroupSizes = settings.GroupSizes ?? [],
        PartitionNames = settings.PartitionNames ?? [],
        PartitionSizes = settings.PartitionSizes ?? [],
        OriginalStructures = settings.OriginalStructures ?? []
    };

    internal static DeliverySourceContainerSettings Capture(string source, ContainerKind kind)
    {
        if (kind == ContainerKind.Super)
        {
            var metadata = MetadataReader.ReadFromImageFile(source);
            var header = UotanStudio.Firmware.Core.FormatDetector.ReadHeader(source, 4);
            return new DeliverySourceContainerSettings
            {
                SourceName = Path.GetFileName(source),
                Kind = "super",
                DeviceName = metadata.BlockDevices.Count > 0
                    ? Encoding.UTF8.GetString(metadata.BlockDevices[0].PartitionName.AsSpan().TrimEnd((byte)0)) : "",
                Storage = header is [0x3a, 0xff, 0x26, 0xed] ? "SPARSE" : "RAW",
                DeviceType = (metadata.Header.Flags & 1) != 0 ? "VAB"
                    : metadata.PartitionNames.Any(name => name.EndsWith("_b", StringComparison.Ordinal)) ? "AB" : "AOnly",
                Size = metadata.BlockDevices.Count > 0 ? metadata.BlockDevices[0].Size : 0,
                BlockSize = metadata.Geometry.LogicalBlockSize,
                MetadataSlots = metadata.Geometry.MetadataSlotCount,
                MetadataMaxSize = metadata.Geometry.MetadataMaxSize,
                MinorVersion = metadata.Header.MinorVersion,
                GroupNames = [.. metadata.Groups.Select(group => group.NameString)],
                GroupSizes = [.. metadata.Groups.Select(group => group.MaximumSize)],
                PartitionNames = [.. metadata.PartitionNames],
                OriginalStructures = [metadata.Geometry.ToBytes(), metadata.Header.ToBytes(),
                    .. metadata.Partitions.Select(part => part.ToBytes()),
                    .. metadata.Extents.Select(extent => extent.ToBytes()),
                    .. metadata.Groups.Select(group => group.ToBytes()),
                    .. metadata.BlockDevices.Select(device => device.ToBytes())]
            };
        }

        var payload = PayloadParser.GetPayloadMetadata(source);
        var ota = TryReadOtaMetadata(source);
        return new DeliverySourceContainerSettings
        {
            SourceName = Path.GetFileName(source),
            Kind = "payload",
            DeviceType = payload.DynamicPartitionMetadata.SnapshotEnabled ? "VAB" : "AB",
            BlockSize = payload.BlockSize,
            MinorVersion = payload.Manifest.MinorVersion,
            MaxTimestamp = payload.MaxTimestamp,
            SecurityPatchLevel = payload.Manifest.SecurityPatchLevel,
            BuildFingerprint = ota.GetValueOrDefault("post-build", ""),
            Incremental = ota.GetValueOrDefault("post-build-incremental", ""),
            SdkLevel = uint.TryParse(ota.GetValueOrDefault("post-sdk-level"), NumberStyles.None, CultureInfo.InvariantCulture, out var sdk) ? sdk : 0,
            PartitionSizes = [.. payload.Manifest.Partitions.Select(partition => partition.NewPartitionInfo?.Size ?? 0)],
            PartitionNames = [.. payload.PartitionNames],
            GroupNames = [.. payload.DynamicPartitionMetadata.Groups.Select(group => group.Name)],
            GroupSizes = [.. payload.DynamicPartitionMetadata.Groups.Select(group => group.Size)],
            OriginalStructures = [payload.DynamicPartitionMetadata.ToByteArray()]
        };
    }

    /// <summary>
    /// Reads <c>META-INF/com/android/metadata</c> from a ZIP-wrapped payload source; returns an empty
    /// dictionary for raw payloads, missing members, oversized metadata or unreadable archives.
    /// <para>从 ZIP 包裹的 payload 来源读取 <c>META-INF/com/android/metadata</c>；
    /// 原始 payload、缺失成员、超限元数据或无法读取的归档返回空字典。</para>
    /// </summary>
    /// <param name="source">Path of the payload or archive source. <para>payload 或归档来源的路径。</para></param>
    /// <returns>The parsed key-value metadata, empty when unavailable. <para>解析出的键值元数据，不可用时为空。</para></returns>
    private static Dictionary<string, string> TryReadOtaMetadata(string source)
    {
        try
        {
            using var stream = File.OpenRead(source);
            Span<byte> magic = stackalloc byte[2];
            if (stream.Length < magic.Length) return [];
            stream.ReadExactly(magic);
            stream.Position = 0;
            if (magic[0] != 0x50 || magic[1] != 0x4B) return [];
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(
                e.FullName.Replace('\\', '/'), "META-INF/com/android/metadata", StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Length > 1024 * 1024) return [];
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.ReadLine() is { } line)
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return [];
        }
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
            var temporary = TempFileHelper.CreateAdjacentPath(path);
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally { TempFileHelper.SafeDelete(temporary); }
        }).ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DeliverySourceContainerSettings))]
internal partial class SourceContainerJsonContext : JsonSerializerContext;