using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip.Models;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip.Packing;

/// <summary>Writes bounded RKAF and RKFW containers from an extracted directory.</summary>
public static class RockchipPacker
{
    private const int RkafHeaderSize = 140;
    private const int RkafDescriptorSize = 112;
    private const int DefaultAlignment = 512;

    /// <summary>Writes either RKAF or RKFW based on the extracted directory contents.</summary>
    public static Task PackAsync(string sourceDirectory, string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException(sourceDirectory);

        var hasEmbedded = File.Exists(Path.Combine(sourceDirectory, "embedded-update.img"));
        return hasEmbedded
            ? PackRkfwAsync(sourceDirectory, outputPath, cancellationToken)
            : PackRkafAsync(sourceDirectory, outputPath, cancellationToken);
    }

    private static Task PackRkafAsync(string sourceDirectory, string outputPath, CancellationToken cancellationToken)
        => FileExtraction.WriteAtomicAsync(outputPath, async output =>
        {
            var files = EnumerateFiles(sourceDirectory, includePackageFile: false);
            if (files.Count == 0)
                throw new InvalidDataException("RKAF source directory does not contain payload files.");
            var tableLength = checked(RkafHeaderSize + files.Count * RkafDescriptorSize);
            var currentOffset = Align(tableLength, DefaultAlignment);
            var descriptors = new byte[checked(files.Count * RkafDescriptorSize)];
            var entries = new List<PackedEntry>(files.Count);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = new FileInfo(file.FullPath).Length;
                var payloadLength = file.RelativePath.Equals("parameter.txt", StringComparison.OrdinalIgnoreCase)
                    ? checked(length + 12)
                    : length;
                if (payloadLength > uint.MaxValue)
                    throw new InvalidDataException($"RKAF entry is too large: {file.RelativePath}");
                entries.Add(new(file, currentOffset, payloadLength));
                currentOffset = Align(checked(currentOffset + payloadLength), DefaultAlignment);
            }

            var header = new byte[RkafHeaderSize];
            "RKAF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)(currentOffset - 4)));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(136), checked((uint)entries.Count));
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

            var descriptorIndex = 0;
            foreach (var entry in entries)
            {
                var descriptor = descriptors.AsSpan(descriptorIndex, RkafDescriptorSize);
                var descriptorName = entry.File.RelativePath.Equals("parameter.txt", StringComparison.OrdinalIgnoreCase)
                    ? "parameter"
                    : Path.GetFileName(entry.File.RelativePath);
                WriteUtf8Field(descriptor[..32], descriptorName);
                WriteUtf8Field(descriptor.Slice(32, 64), entry.File.RelativePath);
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor[96..], checked((uint)entry.Offset));
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor[104..], checked((uint)entry.Length));
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor[108..], checked((uint)entry.Length));
                descriptorIndex += RkafDescriptorSize;
            }
            await output.WriteAsync(descriptors, cancellationToken).ConfigureAwait(false);
            await WriteZerosAsync(output, Align(tableLength, DefaultAlignment) - tableLength, cancellationToken).ConfigureAwait(false);

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output.Position < entry.Offset)
                    await WriteZerosAsync(output, entry.Offset - output.Position, cancellationToken).ConfigureAwait(false);
                await using var input = new FileStream(entry.File.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (entry.File.RelativePath.Equals("parameter.txt", StringComparison.OrdinalIgnoreCase))
                {
                    await output.WriteAsync("PARM"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                    await WriteUInt32Async(output, checked((uint)input.Length), cancellationToken).ConfigureAwait(false);
                }
                await input.CopyToAsync(output, 65536, cancellationToken).ConfigureAwait(false);
                if (entry.File.RelativePath.Equals("parameter.txt", StringComparison.OrdinalIgnoreCase))
                    await WriteZerosAsync(output, 4, cancellationToken).ConfigureAwait(false);
                var padding = Align(entry.Offset + entry.Length, DefaultAlignment) - output.Position;
                await WriteZerosAsync(output, padding, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken: cancellationToken);

    private static Task PackRkfwAsync(string sourceDirectory, string outputPath, CancellationToken cancellationToken)
        => FileExtraction.WriteAtomicAsync(outputPath, async output =>
        {
            var loaderPath = Path.Combine(sourceDirectory, "BOOT");
            if (!File.Exists(loaderPath))
                loaderPath = Path.Combine(sourceDirectory, "LDR");
            var imagePath = Path.Combine(sourceDirectory, "embedded-update.img");
            if (!File.Exists(loaderPath) || !File.Exists(imagePath))
                throw new InvalidDataException("RKFW requires BOOT or LDR and embedded-update.img.");

            var loaderLength = new FileInfo(loaderPath).Length;
            var imageLength = new FileInfo(imagePath).Length;
            if (loaderLength > uint.MaxValue || imageLength > uint.MaxValue)
                throw new InvalidDataException("RKFW payload is too large.");
            var header = new RkFwHeader
            {
                BootOffset = RkFwHeader.StructSize,
                BootSize = checked((uint)loaderLength),
                ImageOffset = checked((uint)(RkFwHeader.StructSize + loaderLength)),
                ImageSize = checked((uint)imageLength)
            }.ToBytes();
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await CopyFileAsync(loaderPath, output, cancellationToken).ConfigureAwait(false);
            await CopyFileAsync(imagePath, output, cancellationToken).ConfigureAwait(false);
        }, cancellationToken: cancellationToken);

    private static List<SourceFile> EnumerateFiles(string sourceDirectory, bool includePackageFile)
        => Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new SourceFile(path, NormalizeRelativePath(sourceDirectory, path)))
            .Where(file => includePackageFile || !file.RelativePath.Equals("package-file", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();

    private static string NormalizeRelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            relative = relative.Replace(Path.AltDirectorySeparatorChar, '/');
        _ = FileExtraction.ResolvePath(root, relative);
        return relative;
    }

    private static long Align(long value, int alignment)
        => checked((value + alignment - 1) / alignment * alignment);

    private static void WriteUtf8Field(Span<byte> destination, string value)
    {
        destination.Clear();
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length >= destination.Length)
            throw new InvalidDataException($"Rockchip entry name is too long: {value}");
        bytes.CopyTo(destination);
    }

    private static async Task CopyFileAsync(string path, FileStream output, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 65536, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteZerosAsync(FileStream output, long count, CancellationToken cancellationToken)
    {
        if (count < 0)
            throw new InvalidDataException("Negative Rockchip padding.");
        var zeros = new byte[(int)Math.Min(65536, Math.Max(1, count))];
        while (count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = (int)Math.Min(zeros.Length, count);
            await output.WriteAsync(zeros.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            count -= length;
        }
    }

    private static async Task WriteUInt32Async(FileStream output, uint value, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private sealed record SourceFile(string FullPath, string RelativePath);
    private sealed record PackedEntry(SourceFile File, long Offset, long Length);
}