using System.Buffers.Binary;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Extraction;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>
/// Retains source semantics and resolves edited host files into filesystem builders with original or inherited Linux attributes.
/// <para>保留源镜像语义，并把编辑后的宿主文件树解析为使用原始或继承 Linux 属性的文件系统构建器。</para>
/// </summary>
public static partial class FileSystemPreservation
{
    private static RebuildParameters? BuildRebuildParameters(string source, string format, CancellationToken token)
    {
        if (format == "Android Sparse Image (simg)")
        {
            using var container = SparseContainerAccess.Resolve().OpenLogicalView(source);
            var logical = container.Logical;
            var inner = BuildRebuildParameters(logical, DetectFilesystem(logical), token);
            if (inner is null) return null;
            inner.InnerFormat = inner.Format;
            inner.Format = "Android Sparse Image (simg)";
            inner.SparseBlockSize = checked((int)container.BlockSize);
            return inner;
        }

        using var stream = File.OpenRead(source);
        return BuildRebuildParameters(stream, format, token);
    }

    private static RebuildParameters? BuildRebuildParameters(Stream source, string format, CancellationToken token) => format switch
    {
        "EXT4" => ReadExt4Rebuild(source),
        "EROFS" => ReadErofsRebuild(source, token),
        "F2FS" => ReadF2fsRebuild(source),
        _ => null
    };

    private static RebuildParameters ReadExt4Rebuild(Stream source)
    {
        using var parser = new Ext4Parser(source);
        return new RebuildParameters
        {
            Format = "EXT4",
            BlockSize = checked((int)parser.BlockSize),
            InodeSize = parser.Superblock.InodeSize,
            Ext4Encoding = (parser.Superblock.FeatureIncompat & 0x20000) != 0 ? parser.Superblock.Encoding : null,
            Ext4EncodingFlags = parser.Superblock.EncodingFlags,
            Ext4ReadOnly = (parser.Superblock.FeatureRoCompat & 0x1000) != 0,
            Ext4SharedBlocks = (parser.Superblock.FeatureRoCompat & 0x4000) != 0,
            Uuid = parser.SourceUuid,
            VolumeName = parser.SourceVolumeName,
            SourceLength = source.Length,
            SourceFilesystemBytes = parser.PartitionSize
        };
    }

    private static RebuildParameters ReadErofsRebuild(Stream source, CancellationToken token)
    {
        using var parser = OpenErofsParser(source, requireCompleteMetadata: true);
        var options = parser.ReadRebuildOptions();
        var extent = checked((long)parser.Superblock.BlockCount64 * options.BlockSize);
        return new RebuildParameters
        {
            Format = "EROFS",
            BlockSize = options.BlockSize,
            Uuid = options.Uuid,
            VolumeName = options.VolumeName,
            SourceLength = source.CanSeek ? source.Length : null,
            SourceFilesystemBytes = extent,
            Erofs = ErofsRebuildMetadata.Capture(options, parser, extent, token)
        };
    }

    private static RebuildParameters ReadF2fsRebuild(Stream source)
    {
        using var parser = new F2fsParser(source);
        var algorithms = parser.ReadCompressionAlgorithms();
        return new RebuildParameters
        {
            Format = "F2FS",
            BlockSize = checked((int)parser.BlockSize),
            SegmentSize = parser.SegmentSize,
            Uuid = parser.SourceUuid,
            VolumeName = parser.SourceVolumeName,
            SourceLength = source.Length,
            SourceFilesystemBytes = checked((long)parser.Superblock.BlockCount * parser.BlockSize),
            F2fsCompressionEnabled = algorithms.Count > 0,
            F2fsCompressionAlgorithm = algorithms.Count == 1 ? algorithms.Single().ToString() : null
        };
    }

    private static string DetectFilesystem(Stream stream)
    {
        var probe = new byte[2048];
        stream.Position = 0;
        stream.ReadExactly(probe);
        stream.Position = 0;
        if (BinaryPrimitives.ReadUInt16LittleEndian(probe.AsSpan(1080)) == 0xEF53) return "EXT4";
        return BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(1024)) switch
        {
            0xE0F5E1E2 => "EROFS",
            0xF2F52010 => "F2FS",
            _ => throw new FileSystemUnsupportedFeatureException("Sparse image does not contain a supported editable filesystem.")
        };
    }

    private static string ReadSymlink(Action<Stream> read)
    {
        using var bytes = new MemoryStream();
        using var bounded = new CountingWriteStream(bytes, 1024 * 1024, leaveOpen: true);
        read(bounded);
        return PortableExtractionPaths.NameEncoding.GetString(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
    }

    private static List<PreservedInodeEntry> ReadEntries(string source, string format)
    {
        if (format == "Android Sparse Image (simg)")
        {
            using var container = SparseContainerAccess.Resolve().OpenLogicalView(source);
            var logical = container.Logical;
            return ReadEntries(logical, DetectFilesystem(logical));
        }
        using var stream = File.OpenRead(source);
        return ReadEntries(stream, format);
    }

    private static List<PreservedInodeEntry> ReadEntries(Stream source, string format)
    {
        if (format == "EXT4")
        {
            using var parser = new Ext4Parser(source);
            return [.. parser.ListEntriesWithRoot().Select(e => new PreservedInodeEntry
            {
                Path = e.Path,
                Size = e.Size,
                Inode = e.Inode,
                Flags = e.Flags,
                DeviceId = e.DeviceId,
                Mode = e.Mode,
                Uid = e.Uid,
                Gid = e.Gid,
                Metadata = e.Metadata,
                SymlinkTarget = (e.Mode & 0xF000) == 0xA000 ? ReadSymlink(output => parser.ReadFile(e.Path, output)) : null,
                Xattrs = e.Xattrs?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            })];
        }
        if (format == "EROFS")
        {
            using var parser = OpenErofsParser(source, requireCompleteMetadata: true);
            return [.. parser.EnumerateEntries().Select(e => new PreservedInodeEntry
            {
                Path = e.Path,
                Size = e.Size,
                Inode = e.Nid,
                DeviceId = e.DeviceId,
                Mode = e.Mode,
                Uid = e.Uid,
                Gid = e.Gid,
                Metadata = new FileSystemInodeMetadata
                {
                    ModificationTime = new(checked((long)e.MTime), e.MTimeNanoseconds),
                    LinkCount = checked((uint)e.LinkCount)
                },
                SymlinkTarget = (e.Mode & 0xF000) == 0xA000 ? ReadSymlink(output => parser.ReadFile(e.Path, output)) : null,
                Xattrs = e.Xattrs?.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal)
            })];
        }
        if (format == "F2FS")
        {
            using var parser = new F2fsParser(source);
            return [.. parser.ListEntriesWithRoot().Select(e => new PreservedInodeEntry
            {
                Path = e.Path,
                Size = e.Size,
                Inode = e.Inode,
                Flags = e.Flags,
                DeviceId = e.DeviceId,
                Mode = e.Mode,
                Uid = e.Uid,
                Gid = e.Gid,
                Metadata = e.Metadata,
                SymlinkTarget = (e.Mode & 0xF000) == 0xA000 ? ReadSymlink(output => parser.ReadFile(e.Path, output)) : null,
                Xattrs = e.Xattrs?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            })];
        }
        return [];
    }
}