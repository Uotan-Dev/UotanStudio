using System.Text.Json.Nodes;
using UotanStudio.Firmware.BoundedContexts.Compression;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz.Build;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz.Extraction;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz;

public sealed partial class DzFormatHandler
{
    private static void CaptureDz(RepackMetadata metadata, string sourcePath, CancellationToken token)
    {
        using var source = File.OpenRead(sourcePath);
        var parsed = DzExtractor.Parse(source, token);
        var last = parsed.Chunks[^1];
        if (last.DataOffset + last.Header.DataSize != source.Length)
        {
            metadata.CanPackDirectory = false;
            metadata.PackingNotes = " DZ extraction is supported, but unmodelled trailing data prevents verified reconstruction.";
            return;
        }
        source.Position = 0;
        var header = new byte[512];
        source.ReadExactly(header);
        DzBuilder.ValidateHeader(header);
        var chunks = new JsonArray();
        var magic = new byte[4];
        foreach (var chunk in parsed.Chunks)
        {
            token.ThrowIfCancellationRequested();
            source.Position = chunk.DataOffset - 512;
            var chunkHeader = new byte[512];
            source.ReadExactly(chunkHeader);
            DzBuilder.ValidateChunkHeader(chunkHeader);
            if (chunk.Header.DataSize < 4)
            {
                throw new InvalidDataException("Truncated DZ compressed chunk.");
            }
            source.ReadExactly(magic);
            var compression = magic.AsSpan().SequenceEqual(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }) ? "zstd"
                : (magic[0] & 15) == 8 && ((magic[0] << 8) | magic[1]) % 31 == 0 ? "zlib"
                : throw new InvalidDataException("Unsupported DZ compression.");
            chunks.Add(new JsonObject
            {
                ["header"] = Convert.ToBase64String(chunkHeader),
                ["compression"] = compression
            });
        }
        metadata.Configuration["dz"] = new JsonObject
        {
            ["version"] = 1,
            ["header"] = Convert.ToBase64String(header),
            ["chunks"] = chunks
        };
    }

    private static FirmwareOpResult RepackDz(JsonObject config, string inputPath, string outputPath, CancellationToken token)
    {
        if (config["version"]?.GetValue<int>() != 1 || config["chunks"] is not JsonArray entries
            || entries.Count is < 1 or > DzBuilder.MaximumChunks)
        {
            throw new InvalidDataException("Invalid DZ reconstruction metadata.");
        }
        var fileHeader = DecodeDzHeader(config["header"]);
        var chunks = new DzChunkSource[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[index] ?? throw new InvalidDataException("Missing DZ entry.");
            var header = DecodeDzHeader(entry["header"]);
            var name = DzBuilder.ValidateChunkHeader(header);
            var compression = entry["compression"]?.GetValue<string>() switch
            {
                "zlib" => CompressionFormat.Zlib,
                "zstd" => CompressionFormat.Zstd,
                _ => throw new InvalidDataException("Unsupported DZ compression metadata.")
            };
            chunks[index] = new(header, FileExtraction.ResolvePath(inputPath, name), compression);
        }
        var metadataPath = RepackMetadata.ResolveActualMetadataPath(inputPath);
        if (string.Equals(Path.GetFullPath(outputPath), metadataPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("DZ output cannot replace its metadata.");
        }
        var blockSize = config["logicalBlockSize"]?.GetValue<int>();
        DzBuilder.Write(fileHeader, chunks, outputPath, blockSize, token);
        return FirmwareOpResult.Ok("Rebuilt and verified DZ 2.1 chunks; compressed byte identity and OEM acceptance are unverified.",
            [.. chunks.Select(chunk => DzBuilder.ValidateChunkHeader(chunk.Header))]);
    }

    private static byte[] DecodeDzHeader(JsonNode? node)
    {
        var encoded = node?.GetValue<string>();
        if (encoded?.Length != 684)
        {
            throw new InvalidDataException("Missing or oversized DZ header metadata.");
        }
        var decoded = Convert.FromBase64String(encoded);
        if (decoded.Length != 512)
        {
            throw new InvalidDataException("Invalid DZ header metadata length.");
        }
        return decoded;
    }
}