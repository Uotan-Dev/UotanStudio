using System.Text;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Models;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Reading;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Parser;

namespace UotanStudio.Services.Delivery;

/// <summary>Options for converting a Super image into a full update_engine Payload.</summary>
public sealed record SuperPayloadConversionOptions
{
    public uint BlockSize { get; init; } = 4096;
    public CompressionType Compression { get; init; } = CompressionType.None;
    public long MaxTimestamp { get; init; }
    public string SecurityPatchLevel { get; init; } = string.Empty;
    public string SigningKeyPath { get; init; } = string.Empty;
    public string SigningKeyFingerprint { get; init; } = string.Empty;
}

/// <summary>Describes the atomically published output of a Super-to-Payload conversion.</summary>
public sealed record SuperPayloadConversionResult(
    string PayloadPath,
    string PropertiesPath,
    IReadOnlyList<string> Partitions,
    bool VirtualAb);

/// <summary>Transactional Super-to-Payload conversion used by desktop and headless hosts.</summary>
public static class SuperPayloadConversionService
{
    public static Task<SuperPayloadConversionResult> ConvertAsync(
        string superPath,
        string payloadPath,
        SuperPayloadConversionOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => ConvertAsync(superPath, payloadPath, options,
            progress is null ? null : new TextProgressAdapter(progress), cancellationToken, signingKeys: null);

    internal static async Task<SuperPayloadConversionResult> ConvertAsync(
        string superPath,
        string payloadPath,
        SuperPayloadConversionOptions? options,
        IProgress<DeliveryProgress>? progress,
        CancellationToken cancellationToken,
        IDeliverySigningKeyProvider? signingKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(superPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        options ??= new SuperPayloadConversionOptions();
        if (!File.Exists(superPath))
        {
            throw new FileNotFoundException("Super image was not found.", superPath);
        }
        if (options.BlockSize is < 512 or > 1024 * 1024 || (options.BlockSize & (options.BlockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Payload block size must be a power of two from 512 to 1048576.");
        }

        var output = Path.GetFullPath(payloadPath);
        var outputDirectory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(outputDirectory);
        var properties = Path.Combine(outputDirectory, "payload_properties.txt");
        var source = Path.GetFullPath(superPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(output, properties, comparison))
        {
            throw new InvalidOperationException("The Payload output path cannot be payload_properties.txt.");
        }
        if (string.Equals(source, output, comparison) || string.Equals(source, properties, comparison))
        {
            throw new InvalidOperationException("Super-to-Payload conversion cannot replace its source Super image.");
        }
        var transactionId = Guid.NewGuid().ToString("N");
        var transaction = Path.Combine(outputDirectory, $".super-payload-{transactionId}.staging");
        var images = Path.Combine(transaction, "images");
        var stagedPayload = Path.Combine(transaction, "payload.bin");
        var stagedProperties = Path.Combine(transaction, "payload_properties.txt");
        var backup = Path.Combine(outputDirectory, $".super-payload-{transactionId}.backup");
        Directory.CreateDirectory(images);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            LpMetadata metadata;
            await using (var super = MetadataReader.OpenImage(superPath))
            {
                metadata = MetadataReader.ReadFromImageStream(super);
            }

            var unpack = await new LpFormatHandler().UnpackAsync(superPath, images,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!unpack.Success)
            {
                throw new InvalidDataException("Super extraction failed: " + unpack.Summary);
            }

            var selected = metadata.Partitions
                .Select(partition => new
                {
                    Metadata = partition,
                    Name = partition.NameString,
                    Path = Path.Combine(images, partition.NameString + ".img")
                })
                .Where(item => File.Exists(item.Path) && new FileInfo(item.Path).Length > 0)
                .ToArray();
            if (selected.Length == 0)
            {
                throw new InvalidDataException("Super contains no non-empty partitions that can be placed in a Payload.");
            }
            if (selected.Any(item => new FileInfo(item.Path).Length % options.BlockSize != 0))
            {
                throw new InvalidDataException("Every extracted Super partition must be aligned to the Payload block size.");
            }

            var dynamicConfig = Path.Combine(transaction, "dynamic_partitions_info.txt");
            await File.WriteAllTextAsync(dynamicConfig, BuildDynamicConfiguration(metadata, selected.Select(item => item.Metadata).ToArray()),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);

            var plan = new DeliveryPlan
            {
                PackPayload = true,
                ContainerConfirmed = true,
                Payload = new DeliveryPayloadOptions
                {
                    BlockSize = options.BlockSize,
                    Compression = options.Compression.ToString(),
                    DynamicPartitionsFile = dynamicConfig,
                    MaxTimestamp = options.MaxTimestamp,
                    SecurityPatchLevel = options.SecurityPatchLevel,
                    SigningKeyPath = options.SigningKeyPath,
                    SigningKeyFingerprint = options.SigningKeyFingerprint
                },
                Container = selected.Select(item => new DeliveryFile
                {
                    Partition = item.Name,
                    FileName = item.Name + ".img",
                    SourcePath = item.Path,
                    SourceLength = new FileInfo(item.Path).Length,
                    IncludeInSuper = true
                }).ToList()
            };
            await DeliveryPayloadBuilder.BuildAsync(plan, stagedPayload, progress, cancellationToken, signingKeys)
                .ConfigureAwait(false);
            if (!File.Exists(stagedProperties))
            {
                throw new InvalidDataException("Payload builder did not produce payload_properties.txt.");
            }

            await using (var payload = File.OpenRead(stagedPayload))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parsed = PayloadParser.GetPayloadMetadata(payload);
                var names = parsed.Manifest.Partitions.Select(partition => partition.PartitionName).ToHashSet(StringComparer.Ordinal);
                if (selected.Any(item => !names.Contains(item.Name)))
                {
                    throw new InvalidDataException("The generated Payload is missing an extracted Super partition.");
                }
            }

            PublishPair(stagedPayload, output, stagedProperties, properties, backup, cancellationToken);
            return new(output, properties, selected.Select(item => item.Name).ToArray(), (metadata.Header.Flags & 1) != 0);
        }
        finally
        {
            TryDeleteDirectory(transaction);
            TryDeleteEmptyDirectory(backup);
        }
    }

    private static string BuildDynamicConfiguration(LpMetadata metadata, IReadOnlyCollection<LpMetadataPartition> selected)
    {
        var selectedNames = selected.Select(partition => partition.NameString).ToHashSet(StringComparer.Ordinal);
        var groups = metadata.Groups.Select((group, index) => new
        {
            Group = group,
            Index = index,
            Partitions = selected.Where(partition => partition.GroupIndex == index).Select(partition => partition.NameString).ToArray()
        }).Where(item => item.Partitions.Length > 0).ToArray();
        if (groups.Length == 0 || selectedNames.Count == 0)
        {
            throw new InvalidDataException("Super dynamic partition groups are empty or inconsistent.");
        }

        var text = new StringBuilder();
        text.Append("super_partition_groups=").AppendJoin(' ', groups.Select(item => item.Group.NameString)).Append('\n');
        foreach (var item in groups)
        {
            if (string.IsNullOrWhiteSpace(item.Group.NameString))
            {
                throw new InvalidDataException("Super contains an unnamed dynamic partition group.");
            }
            ulong used = 0;
            foreach (var partition in selected.Where(partition => partition.GroupIndex == item.Index))
            {
                foreach (var extent in metadata.Extents.GetRange(checked((int)partition.FirstExtentIndex), checked((int)partition.NumExtents)))
                {
                    used = checked(used + extent.NumSectors * 512);
                }
            }
            var size = Math.Max(item.Group.MaximumSize, Math.Max(used, metadata.Geometry.LogicalBlockSize));
            text.Append("super_").Append(item.Group.NameString).Append("_group_size=").Append(size).Append('\n');
            text.Append("super_").Append(item.Group.NameString).Append("_partition_list=")
                .AppendJoin(' ', item.Partitions).Append('\n');
        }
        text.Append("virtual_ab=").Append(((metadata.Header.Flags & 1) != 0).ToString().ToLowerInvariant()).Append('\n');
        return text.ToString();
    }

    private static void PublishPair(string stagedPayload, string payload, string stagedProperties, string properties,
        string backupDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(backupDirectory);
        var pairs = new[] { (Staged: stagedPayload, Target: payload), (Staged: stagedProperties, Target: properties) };
        var published = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var pair in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? saved = null;
                if (File.Exists(pair.Target))
                {
                    saved = Path.Combine(backupDirectory, Path.GetFileName(pair.Target));
                    File.Move(pair.Target, saved, overwrite: false);
                }
                try
                {
                    File.Move(pair.Staged, pair.Target, overwrite: false);
                }
                catch
                {
                    if (saved is not null)
                    {
                        File.Move(saved, pair.Target, overwrite: false);
                    }
                    throw;
                }
                published.Add((pair.Target, saved));
            }
            TryDeleteDirectory(backupDirectory);
        }
        catch (Exception publicationError)
        {
            List<Exception>? rollbackErrors = null;
            for (var i = published.Count - 1; i >= 0; i--)
            {
                try
                {
                    File.Delete(published[i].Target);
                    if (published[i].Backup is { } saved)
                    {
                        File.Move(saved, published[i].Target, overwrite: false);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors ??= [];
                    rollbackErrors.Add(rollbackError);
                }
            }
            if (rollbackErrors is not null)
            {
                rollbackErrors.Insert(0, publicationError);
                throw new AggregateException("Payload publication failed and rollback was incomplete.", rollbackErrors);
            }
            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class TextProgressAdapter(IProgress<string> progress) : IProgress<DeliveryProgress>
    {
        public void Report(DeliveryProgress value) => progress.Report($"{value.Percent:0}% {value.Message}");
    }
}
