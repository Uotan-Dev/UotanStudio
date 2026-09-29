using System.Text.Json.Serialization;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

internal sealed class PreservedInodeEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public ulong Inode { get; set; }
    public uint Flags { get; set; }
    public uint DeviceId { get; set; }
    public ushort Mode { get; set; }
    public uint Uid { get; set; }
    public uint Gid { get; set; }
    public string? SymlinkTarget { get; set; }
    public Dictionary<string, byte[]>? Xattrs { get; set; }
    public FileSystemInodeMetadata? Metadata { get; set; }
}

internal sealed class FileSystemPreservationManifest
{
    public int Version { get; set; } = 1;
    public string Format { get; set; } = "";
    public long SourceLength { get; set; }
    public string SourceHash { get; set; } = "";
    public string ConfigurationHash { get; set; } = "";
    public string ConfigurationJson { get; set; } = "";
    public Dictionary<string, string> Projection { get; set; } = [];
    public Dictionary<string, string> Sidecars { get; set; } = [];
    public Dictionary<string, string> ImagePaths { get; set; } = [];
    public List<PreservedInodeEntry> ParsedEntries { get; set; } = [];
    public Dictionary<string, PreservedContentObject> ContentObjects { get; set; } = new(StringComparer.Ordinal);
}

internal sealed record PreservedContentObject(string Hash, long Length);

[JsonSerializable(typeof(FileSystemPreservationManifest))]
internal partial class FileSystemPreservationJsonContext : JsonSerializerContext;