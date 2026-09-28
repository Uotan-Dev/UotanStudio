using System.Buffers.Binary;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>
/// Preservation part that verifies results: it checks inode flag support per format, then re-reads a rebuilt or captured image — sparse containers are expanded first — and fails on any attribute, xattr, timestamp, hardlink or content mismatch.
/// <para>保留语义的组成部分，负责校验结果：先按格式检查 inode 标志是否受支持，再重新读取重建或采集得到的镜像（稀疏容器先展开），
/// 任何属性、扩展属性、时间戳、硬链接或内容不一致都会失败。</para>
/// </summary>
public static partial class FileSystemPreservation
{
    internal static void ValidateInodeFlags(IEnumerable<PreservedInodeEntry> entries, string format)
    {
        var supported = format switch
        {
            "EXT4" => FileSystemEditPlan.Ext4SemanticFlagMask | 0x102C1000u,
            "F2FS" => FileSystemEditPlan.F2fsSemanticFlagMask | 0x4u,
            "EROFS" => 0u,
            _ => throw new InvalidDataException($"Unknown filesystem format '{format}'.")
        };
        foreach (var entry in entries)
        {
            if (format == "EXT4" && (entry.Flags & 0x40000000) != 0 && (entry.Mode & 0xF000) != 0x4000)
            {
                throw new InvalidDataException($"EXT4 casefold flag belongs only on directories: '{entry.Path}'.");
            }
            var unsupported = entry.Flags & ~supported;
            if (unsupported != 0)
            {
                throw new NotSupportedException(
                    $"{format} inode '{entry.Path}' has flags 0x{unsupported:X8} without a semantic replay writer.");
            }
        }
    }

    private static void VerifyRebuilt(Stream output, FileSystemEditPlan plan, CancellationToken token)
    {
        using var container = new BoundedReadStream(output, 0, output.Length, leaveOpen: true);
        Span<byte> magic = stackalloc byte[4];
        container.ReadExactly(magic);
        container.Position = 0;
        if (BinaryPrimitives.ReadUInt32LittleEndian(magic) == 0xED26FF3A)
        {
            using var sparse = SparseContainerAccess.Resolve().OpenLogicalView(container);
            VerifyRebuiltFilesystem(sparse.Logical, plan, token);
        }
        else
        {
            VerifyRebuiltFilesystem(container, plan, token);
        }
    }

    private static void VerifyRebuiltFilesystem(Stream image, FileSystemEditPlan plan, CancellationToken token)
    {
        var format = DetectFilesystem(image);
        Dictionary<string, PreservedInodeEntry> actual;
        using (var view = new BoundedReadStream(image, 0, image.Length, leaveOpen: true))
        {
            actual = ReadEntries(view, format).ToDictionary(entry => FileSystemEditPlan.Normalize(entry.Path), StringComparer.Ordinal);
        }
        if (actual.Count != plan.Entries.Count)
        {
            throw new InvalidDataException("Rebuilt filesystem entry count differs from the edit plan.");
        }
        var sourceLinks = new Dictionary<ulong, ulong>();
        var outputLinks = new Dictionary<ulong, ulong>();
        var semanticFlags = format switch
        {
            "EXT4" => FileSystemEditPlan.Ext4SemanticFlagMask,
            "F2FS" => FileSystemEditPlan.F2fsSemanticFlagMask,
            _ => 0u
        };
        foreach (var (path, expected) in plan.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!actual.TryGetValue(path, out var found)
                || expected.Mode != found.Mode || expected.Uid != found.Uid || expected.Gid != found.Gid
                || (expected.Flags & semanticFlags) != (found.Flags & semanticFlags)
                || expected.SymlinkTarget != found.SymlinkTarget
                || (expected.Mode & 0xF000) is 0x2000 or 0x6000 && expected.DeviceId != found.DeviceId)
            {
                throw new InvalidDataException($"Rebuilt inode attributes differ for '{path}'.");
            }
            var xattrs = expected.Xattrs?.Where(pair => pair.Key != "system.data").ToArray() ?? [];
            var actualXattrs = found.Xattrs?.Where(pair => pair.Key != "system.data").ToDictionary(StringComparer.Ordinal) ?? [];
            if (xattrs.Length != actualXattrs.Count || xattrs.Any(pair =>
                    !actualXattrs.TryGetValue(pair.Key, out var value) || !pair.Value.AsSpan().SequenceEqual(value)))
            {
                throw new InvalidDataException($"Rebuilt extended attributes differ for '{path}'.");
            }
            VerifyInodeMetadata(expected.Metadata, found.Metadata, path);
            if (expected.Inode != 0 && (expected.Mode & 0xF000) != 0x4000)
            {
                if (sourceLinks.TryGetValue(expected.Inode, out var target) && target != found.Inode
                    || outputLinks.TryGetValue(found.Inode, out var original) && original != expected.Inode)
                {
                    throw new InvalidDataException($"Rebuilt hardlink relationships differ for '{path}'.");
                }
                sourceLinks[expected.Inode] = found.Inode;
                outputLinks[found.Inode] = expected.Inode;
            }
        }
        WithContentReader(image, format, read => VerifyContents(plan, read, token), token);
    }

    private static void WithContentReader(Stream image, string format, Action<Action<string, Stream>> verify, CancellationToken token)
    {
        using var contentView = new BoundedReadStream(image, 0, image.Length, leaveOpen: true);
        switch (format)
        {
            case "EXT4":
                using (var parser = new Ext4Parser(contentView, int.MaxValue, long.MaxValue, token))
                {
                    verify(parser.ReadFile);
                }
                break;
            case "EROFS":
                using (var parser = OpenErofsParser(contentView, token, requireCompleteMetadata: true))
                {
                    verify(parser.ReadFile);
                }
                break;
            case "F2FS":
                using (var parser = new F2fsParser(contentView))
                {
                    verify(parser.ReadFile);
                }
                break;
            default:
                throw new InvalidDataException("Rebuilt filesystem format is not recognized.");
        }
    }

    private static void VerifyCapturedContents(string source, FileSystemPreservationManifest manifest, CancellationToken token)
    {
        using var input = File.OpenRead(source);
        if (manifest.Format == "Android Sparse Image (simg)")
        {
            using var sparse = SparseContainerAccess.Resolve().OpenLogicalView(input);
            VerifyCapturedFilesystem(sparse.Logical, manifest, token);
        }
        else
        {
            VerifyCapturedFilesystem(input, manifest, token);
        }
    }

    private static void VerifyCapturedFilesystem(Stream image, FileSystemPreservationManifest manifest, CancellationToken token)
    {
        WithContentReader(image, DetectFilesystem(image), read =>
        {
            foreach (var entry in manifest.ParsedEntries.Where(entry => (entry.Mode & 0xF000) == 0x8000))
            {
                token.ThrowIfCancellationRequested();
                var host = manifest.ImagePaths.GetValueOrDefault(entry.Path, entry.Path);
                if (!manifest.Projection.TryGetValue(host, out var projection))
                {
                    throw new InvalidDataException($"Extracted file is missing from the snapshot: '{entry.Path}'.");
                }
                using var hash = new HashingWriteStream(Stream.Null, leaveOpen: true);
                using var bounded = new CountingWriteStream(hash, entry.Size,
                    leaveOpen: true, cancellationToken: token);
                read(entry.Path, bounded);
                if (!string.Equals("file:" + hash.GetHashHex(), ProjectionIdentity(projection), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Extracted content does not match the source image: '{entry.Path}'.");
                }
            }
        }, token);
    }

    private static void VerifyInodeMetadata(FileSystemInodeMetadata? expected, FileSystemInodeMetadata? actual, string path)
    {
        if (expected is null)
        {
            return;
        }
        if (actual is null
            || expected.AccessTime is not null && expected.AccessTime != actual.AccessTime
            || expected.ChangeTime is not null && expected.ChangeTime != actual.ChangeTime
            || expected.ModificationTime is not null && expected.ModificationTime != actual.ModificationTime
            || expected.BirthTime is not null && expected.BirthTime != actual.BirthTime
            || expected.Generation != actual.Generation
            || expected.ProjectId is not null && expected.ProjectId != actual.ProjectId
            || expected.Version is not null && expected.Version != actual.Version
            || expected.F2fsAdvise != actual.F2fsAdvise
            || expected.F2fsCompression != actual.F2fsCompression)
        {
            throw new InvalidDataException($"Rebuilt inode timestamps or identity differ for '{path}'.");
        }
    }

    private static void VerifyContents(FileSystemEditPlan plan, Action<string, Stream> read, CancellationToken token)
    {
        foreach (var (host, entry) in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            if ((entry.Mode & 0xF000) != 0x8000)
            {
                continue;
            }
            var source = plan.ContentSource(host);
            using var hash = new HashingWriteStream(Stream.Null, leaveOpen: true);
            using var bounded = new CountingWriteStream(hash, new FileInfo(source).Length, leaveOpen: true, cancellationToken: token);
            read(entry.Path, bounded);
            if (!string.Equals(hash.GetHashHex(), HashFile(source, token), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Rebuilt file content differs for '{entry.Path}'.");
            }
        }
    }
}