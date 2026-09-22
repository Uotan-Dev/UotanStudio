using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Extraction;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Edits existing configuration content and inode attributes through a private, metadata-preserving rebuild.</summary>
public static class ImageConfigurationEditor
{
    /// <summary>
    /// Rebuilds raw EXT4/EROFS, checks the replaced bytes and publishes atomically. The replacement is
    /// limited to 16 MiB. Extraction and output capacities are explicit; caller streams remain open.
    /// </summary>
    public static Task RebuildAsync(string sourcePath, string format, string imagePath, Stream replacement,
        string outputPath, long capacity, long maximumExtractionBytes, CancellationToken token)
        => RebuildManyAsync(sourcePath, format, [new ImageConfigurationReplacement(imagePath, replacement)],
            outputPath, capacity, maximumExtractionBytes, token);

    /// <summary>Applies up to 256 content and 256 attribute requests in one tree, verifying requested bytes and attributes before publication.</summary>
    public static async Task RebuildManyAsync(string sourcePath, string format,
        IReadOnlyList<ImageConfigurationReplacement> replacements, string outputPath, long capacity,
        long maximumExtractionBytes, CancellationToken token, IReadOnlyList<FileSystemAttributeEdit>? attributeEdits = null,
        IReadOnlyList<FileSystemXattrEdit>? xattrEdits = null)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count > 256 || attributeEdits?.Count > 256 || xattrEdits?.Count > 256
            || replacements.Count == 0 && attributeEdits is not { Count: > 0 } && xattrEdits is not { Count: > 0 })
        {
            throw new InvalidDataException("An image edit batch requires at least one request and at most 256 requests of each kind.");
        }
        var edits = replacements.ToArray();
        var attributes = attributeEdits?.ToArray() ?? [];
        var extended = xattrEdits?.Select(edit =>
        {
            ArgumentNullException.ThrowIfNull(edit);
            if (edit.Value?.Length > 65536 || edit.ExpectedValue?.Length > 65536)
            {
                throw new InvalidDataException("Extended attribute value exceeds 64 KiB.");
            }
            return edit with { Value = edit.Value?.ToArray(), ExpectedValue = edit.ExpectedValue?.ToArray() };
        }).ToArray() ?? [];
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            ArgumentNullException.ThrowIfNull(edit);
            ArgumentNullException.ThrowIfNull(edit.Path);
            ArgumentNullException.ThrowIfNull(edit.Content);
            if (!edit.Content.CanRead || !identities.Add(edit.Path.TrimStart('/')))
            {
                throw new InvalidDataException("Image replacement paths must be distinct and streams readable.");
            }
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExtractionBytes);
        IFormatHandler handler = format switch
        {
            "EXT4" => new Ext4FormatHandler(),
            "EROFS" => new ErofsFormatHandler(),
            _ => throw new InvalidDataException("Only raw EXT4 and EROFS configuration edits are supported.")
        };
        token.ThrowIfCancellationRequested();
        var temporary = Path.Combine(Path.GetTempPath(), "uotan-image-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var frozen = Path.Combine(temporary, "source.img");
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            await using (var output = new FileStream(frozen, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var limited = new CountingWriteStream(output, 64L * 1024 * 1024 * 1024, leaveOpen: true, cancellationToken: token);
                await input.CopyToAsync(limited, 65536, token).ConfigureAwait(false);
            }
            // Use the same strict literal-path and regular-file checks as the reading node.
            foreach (var edit in edits)
            {
                using var image = File.OpenRead(frozen);
                ImageConfigurationReader.Extract(image, format, edit.Path, Stream.Null, token);
            }
            var tree = Path.Combine(temporary, "source");
            var originalAttributes = new List<ImageFileAttributes>();
            foreach (var edit in attributes)
            {
                ArgumentNullException.ThrowIfNull(edit);
                using var image = File.OpenRead(frozen);
                originalAttributes.Add(ImageAttributeReader.Read(image, format, edit.Path, token));
            }
            var paths = ReadPaths(frozen, format, maximumExtractionBytes, token);
            var mapping = new PortableExtractionPaths(tree, paths);
            using var scope = RepackMetadata.UseContext(RepackMetadataContext.FromProjectRoot(Path.Combine(temporary, "metadata")));
            var unpacked = await handler.UnpackAsync(frozen, tree, cancellationToken: token).ConfigureAwait(false);
            if (!unpacked.Success) { throw new InvalidDataException(unpacked.Summary); }
            foreach (var edit in edits)
            {
                var mapped = mapping.Resolve(edit.Path.TrimStart('/'));
                await using var output = new FileStream(mapped, FileMode.Truncate, FileAccess.Write, FileShare.None, 65536, true);
                using var limited = new CountingWriteStream(output, 16 * 1024 * 1024, leaveOpen: true, cancellationToken: token);
                await edit.Content.CopyToAsync(limited, 65536, token).ConfigureAwait(false);
            }
            var rebuilt = Path.Combine(temporary, "rebuilt.img");
            if (!FileSystemPreservation.TryRestore(tree, rebuilt, format, token, maximumOutputBytes: capacity,
                attributeEdits: attributes, xattrEdits: extended))
            {
                throw new InvalidDataException("Image preservation metadata is missing.");
            }
            var extracted = Path.Combine(temporary, "verified-content");
            foreach (var edit in edits)
            {
                using (var image = File.OpenRead(rebuilt))
                using (var output = new FileStream(extracted, FileMode.Create, FileAccess.Write))
                {
                    ImageConfigurationReader.Extract(image, format, edit.Path, output, token);
                }
                await using var expected = File.OpenRead(mapping.Resolve(edit.Path.TrimStart('/')));
                await using var actual = File.OpenRead(extracted);
                var expectedHash = await SHA256.HashDataAsync(expected, token).ConfigureAwait(false);
                var actualHash = await SHA256.HashDataAsync(actual, token).ConfigureAwait(false);
                if (expected.Length != actual.Length || !expectedHash.AsSpan().SequenceEqual(actualHash))
                {
                    throw new InvalidDataException("Rebuilt image configuration verification failed.");
                }
            }
            for (var index = 0; index < attributes.Length; index++)
            {
                var edit = attributes[index];
                using var image = File.OpenRead(rebuilt);
                var actual = ImageAttributeReader.Read(image, format, edit.Path, token);
                if (actual.Uid != edit.Uid || actual.Gid != edit.Gid || (actual.Mode & 0xFFF) != edit.Permissions
                    || (actual.Mode & 0xF000) != (originalAttributes[index].Mode & 0xF000))
                {
                    throw new InvalidDataException("Rebuilt inode attribute verification failed.");
                }
            }
            foreach (var edit in extended)
            {
                using var image = File.OpenRead(rebuilt);
                var actual = ImageAttributeReader.ReadXattr(image, format, edit.Path, edit.Name, token);
                if (actual is null ? edit.Value is not null : edit.Value is null || !actual.AsSpan().SequenceEqual(edit.Value))
                {
                    throw new InvalidDataException("Rebuilt extended attribute verification failed.");
                }
            }
            FileExtraction.WriteAtomic(outputPath, output =>
            {
                using var input = File.OpenRead(rebuilt);
                using var limited = new CountingWriteStream(output, capacity, leaveOpen: true, cancellationToken: token);
                input.CopyTo(limited, 65536);
                token.ThrowIfCancellationRequested();
            }, overwrite: true);
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    private static string[] ReadPaths(string source, string format, long maximumBytes, CancellationToken token)
    {
        using var stream = File.OpenRead(source);
        long total = 0;
        if (format == "EXT4")
        {
            using var parser = new Ext4Parser(stream, 100000, 16 * 1024 * 1024, token);
            return parser.ListFiles().Select(entry => Check(entry.Path, entry.Size)).ToArray();
        }
        using var erofs = new ErofsParser(stream, 100000, 16 * 1024 * 1024, token);
        return erofs.EnumerateFiles().Select(entry => Check(entry.Path, entry.Size)).ToArray();

        string Check(string path, long size)
        {
            token.ThrowIfCancellationRequested();
            if (size < 0 || size > maximumBytes - total)
            {
                throw new InvalidDataException("Image exceeds the extraction byte budget.");
            }
            total += size;
            return path;
        }
    }
}

/// <summary>One literal image path and replacement bytes, consumed from the stream's current position without disposing it.</summary>
public sealed record ImageConfigurationReplacement(string Path, Stream Content);