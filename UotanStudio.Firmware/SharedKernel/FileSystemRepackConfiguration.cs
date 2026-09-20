using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Models;

namespace UotanStudio.Firmware.SharedKernel;

internal static class FileSystemRepackConfiguration
{
    internal static JsonObject Read(string path, bool sparse)
    {
        if (sparse)
        {
            using var image = SparseReader.FromImageFile(path);
            using var logical = new SparseReadStream(image);
            return Read(logical);
        }
        using var input = File.OpenRead(path);
        return Read(input);
    }

    private static JsonObject Read(Stream input)
    {
        if (input.Length < 1082) return [];
        var prefix = new byte[1082];
        input.ReadExactly(prefix);
        JsonObject superblock;
        JsonObject? compression = null;
        string format;
        if (BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(1080)) == Ext4Superblock.MagicValue)
        {
            input.Position = 0;
            var bytes = new byte[2048];
            input.ReadExactly(bytes);
            var block = Ext4Superblock.FromBytes(bytes);
            superblock = JsonSerializer.SerializeToNode(block, FileSystemRepackJsonContext.Default.Ext4Superblock)!.AsObject();
            superblock["uuid"] = Convert.ToHexStringLower(block.Uuid);
            format = "EXT4";
        }
        else if (BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(1024)) == ErofsSuperblock.Magic)
        {
            input.Position = ErofsSuperblock.SuperblockOffset;
            var bytes = new byte[ErofsSuperblock.SuperblockSize];
            input.ReadExactly(bytes);
            var block = ErofsSuperblock.FromBytes(bytes);
            if (block.SbExtSlots > 0)
            {
                bytes = new byte[checked(bytes.Length + block.SbExtSlots * ErofsSuperblock.ExtSlotSize)];
                input.Position = ErofsSuperblock.SuperblockOffset;
                input.ReadExactly(bytes);
                block = ErofsSuperblock.FromBytes(bytes);
            }
            superblock = JsonSerializer.SerializeToNode(block, FileSystemRepackJsonContext.Default.ErofsSuperblock)!.AsObject();
            superblock["uuid"] = Convert.ToHexStringLower(block.Uuid);
            format = "EROFS";
            input.Position = 0;
            using var parser = new ErofsParser(input);
            compression = parser.ReadCompressionMetadata();
        }
        else if (BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(1024)) == F2fsSuperblock.Magic)
        {
            input.Position = F2fsSuperblock.SuperblockOffset;
            var bytes = new byte[F2fsSuperblock.SuperblockSize];
            input.ReadExactly(bytes);
            var block = F2fsSuperblock.FromBytes(bytes);
            superblock = JsonSerializer.SerializeToNode(block, FileSystemRepackJsonContext.Default.F2fsSuperblock)!.AsObject();
            superblock["uuid"] = Convert.ToHexStringLower(block.Uuid);
            format = "F2FS";
        }
        else return [];

        var configuration = new JsonObject { ["fileSystem"] = format, ["superblock"] = superblock };
        if (compression is not null) configuration["compression"] = compression;
        return configuration;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(Ext4Superblock))]
[JsonSerializable(typeof(ErofsSuperblock))]
[JsonSerializable(typeof(F2fsSuperblock))]
internal partial class FileSystemRepackJsonContext : JsonSerializerContext;