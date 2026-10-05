using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Text;

namespace UotanStudio.Firmware.VendorFirmware.Dz;

internal sealed partial class DzFormatHandler
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        if (IsKdz(sourcePath))
        {
            var headerType = KdzExtractor.Parse(sourcePath).HeaderType;
            metadata.CanPackDirectory = headerType is >= 0 and <= 2;
            metadata.PackingNotes = metadata.CanPackDirectory
                ? $" KDZ v{headerType} outer reconstruction preserves member names, ordering and non-payload bytes; embedded signatures and OEM acceptance are not established."
                : " Unsupported KDZ header version.";
        }
        else
        {
            var header = DzExtractor.Parse(sourcePath).Header;
            metadata.CanPackDirectory = header.FormatMajor == 2 && header.FormatMinor == 1 && header.ChunkCount > 0;
            metadata.PackingNotes = metadata.CanPackDirectory
                ? " DZ 2.1 reconstruction preserves chunk headers and layout; without an explicit sector size, decoded chunks cannot grow. Compressed byte identity and OEM acceptance are not established."
                : " Only DZ 2.1 chunk-preserving reconstruction is supported.";
        }
    }

    Task IRepackMetadataProvider.CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath,
        string outputDirectory, string metadataDirectory, CancellationToken cancellationToken)
    {
        if (!metadata.CanPackDirectory)
        {
            return Task.CompletedTask;
        }
        if (!IsKdz(sourcePath))
        {
            CaptureDz(metadata, sourcePath, cancellationToken);
            return Task.CompletedTask;
        }
        using var source = File.OpenRead(sourcePath);
        var parsed = KdzExtractor.Parse(source, cancellationToken);
        var headerType = parsed.HeaderType;
        var partitions = parsed.Partitions;
        var names = partitions.Select(part => part.Name).ToArray();
        var sources = names.Select(name => new KdzSource(name, FileExtraction.ResolvePath(outputDirectory, name))).ToArray();
        KdzBuilder.ValidateSources(sources, sourcePath, cancellationToken);
        var order = Enumerable.Range(0, partitions.Count).OrderBy(index => partitions[index].Offset).ToArray();
        var records = new long[partitions.Count];
        source.Position = 8;
        for (var index = 0; index < partitions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var marker = source.ReadByte();
            if (marker != 3)
            {
                source.Position--;
            }
            records[index] = source.Position;
            var record = new byte[KdzHeader.Size];
            source.ReadExactly(record);
            var name = KdzBuilder.NameEncoding.GetStringTrimNul(record.AsSpan(0, 256));
            if (name != names[index])
            {
                throw new InvalidDataException("KDZ member names cannot be preserved as valid UTF-8.");
            }
        }

        var gaps = new long[partitions.Count + 1];
        var gapOffsets = new long[gaps.Length];
        long previousEnd = 0;
        for (var physical = 0; physical < order.Length; physical++)
        {
            var part = partitions[order[physical]];
            var offset = checked((long)part.Offset);
            if (offset < previousEnd)
            {
                metadata.CanPackDirectory = false;
                metadata.PackingNotes = " KDZ extraction is supported, but overlapping members cannot be reconstructed independently.";
                return Task.CompletedTask;
            }
            gapOffsets[physical] = previousEnd;
            gaps[physical] = offset - previousEnd;
            previousEnd = checked(offset + (long)part.Length);
        }
        gapOffsets[^1] = previousEnd;
        gaps[^1] = source.Length - previousEnd;
        if (gaps.Sum() > KdzBuilder.MaximumLayoutBytes)
        {
            metadata.CanPackDirectory = false;
            metadata.PackingNotes = " KDZ extraction is supported, but non-payload preservation data exceeds the 64 MiB rebuild limit.";
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(metadataDirectory);
        var artifact = $".kdz-layout-{Guid.NewGuid():N}.bin";
        var path = FileExtraction.ResolvePath(metadataDirectory, artifact);
        string digest = "";
        FileExtraction.WriteAtomic(path, output =>
        {
            var buffer = new byte[64 * 1024];
            for (var index = 0; index < gaps.Length; index++)
            {
                source.Position = gapOffsets[index];
                KdzBuilder.Copy(source, output, gaps[index], buffer, cancellationToken);
            }
            output.Position = 0;
            digest = Convert.ToHexString(KdzBuilder.HashRange(output, output.Length, cancellationToken));
        }, cancellationToken: cancellationToken);
        var entries = new JsonArray();
        for (var index = 0; index < names.Length; index++)
        {
            entries.Add((JsonNode)new JsonObject { ["name"] = names[index], ["recordOffset"] = records[index] });
        }
        var physicalOrder = new JsonArray();
        foreach (var index in order)
        {
            physicalOrder.Add((JsonNode)JsonValue.Create(index));
        }
        var preservedGaps = new JsonArray();
        foreach (var length in gaps)
        {
            preservedGaps.Add((JsonNode)JsonValue.Create(length));
        }
        metadata.Configuration["kdz"] = new JsonObject
        {
            ["version"] = headerType,
            ["entries"] = entries,
            ["order"] = physicalOrder,
            ["gaps"] = preservedGaps,
            ["layoutFile"] = artifact,
            ["layoutSha256"] = digest
        };
        return Task.CompletedTask;
    }

    private static FirmwareOpResult RepackKdz(string inputPath, string outputPath, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var metadata = RepackMetadata.Read(inputPath);
            if (metadata?.Configuration["dz"] is JsonObject dz)
            {
                if (metadata.Configuration["kdz"] is not null)
                {
                    throw new InvalidDataException("Conflicting DZ/KDZ repack metadata.");
                }
                return RepackDz(dz, inputPath, outputPath, token);
            }
            if (metadata?.Configuration["kdz"] is not JsonObject config
                || config["version"]?.GetValue<int>() is not (0 or 1 or 2))
            {
                return FirmwareOpResult.Fail("Complete DZ 2.1 or KDZ v0/v1/v2 repack metadata is required.");
            }

            if (config["entries"] is not JsonArray entries || entries.Count is < 1 or > KdzBuilder.MaximumEntries
                || config["order"] is not JsonArray orderArray || orderArray.Count != entries.Count
                || config["gaps"] is not JsonArray gapArray || gapArray.Count != entries.Count + 1)
            {
                throw new InvalidDataException("Invalid KDZ repack metadata arrays.");
            }
            var sources = new KdzSource[entries.Count];
            var records = new long[entries.Count];
            for (var index = 0; index < entries.Count; index++)
            {
                var name = entries[index]?["name"]?.GetValue<string>()
                    ?? throw new InvalidDataException("Missing KDZ member name.");
                sources[index] = new(name, FileExtraction.ResolvePath(inputPath, name));
                records[index] = entries[index]?["recordOffset"]?.GetValue<long>()
                    ?? throw new InvalidDataException("Missing KDZ record offset.");
            }
            var order = orderArray.Select(node => node?.GetValue<int>()
                ?? throw new InvalidDataException("Missing KDZ physical index.")).ToArray();
            var gaps = gapArray.Select(node => node?.GetValue<long>()
                ?? throw new InvalidDataException("Missing KDZ preserved gap length.")).ToArray();
            var metadataPath = RepackMetadata.ResolveActualMetadataPath(inputPath)
                ?? throw new InvalidDataException("KDZ metadata path could not be resolved.");
            var artifact = config["layoutFile"]?.GetValue<string>()
                ?? throw new InvalidDataException("Missing KDZ preservation file.");
            var path = FileExtraction.ResolvePath(Path.GetDirectoryName(metadataPath)!, artifact);
            if (string.Equals(Path.GetFullPath(outputPath), path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFullPath(outputPath), metadataPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("KDZ output cannot replace its preservation metadata.");
            }
            var headerType = config["version"]!.GetValue<int>();
            KdzBuilder.Write(sources, outputPath, order, records, gaps, path,
                config["layoutSha256"]?.GetValue<string>(), headerType, token);
            return FirmwareOpResult.Ok($"Rebuilt and verified KDZ v{headerType} outer container; embedded signing and device acceptance are unverified.",
                [.. sources.Select(source => source.Name)]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            return FirmwareOpResult.Fail(exception.Message);
        }
    }
}