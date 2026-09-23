using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Native;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;

/// <summary>Checks, optionally repairs, and minimizes an existing raw EXT4 image using e2fsprogs on a private copy.</summary>
public static class Ext4ImageMaintenance
{
    public static async Task MinimizeAsync(string inputPath, string outputPath, NativeFileSystemTools tools,
        bool repairBeforeResize = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var source = Path.GetFullPath(inputPath);
        var destination = Path.GetFullPath(outputPath);
        if (string.Equals(source, destination, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The source and output paths must differ.", nameof(outputPath));
        }

        bool sparseInput;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
        {
            if (input.Length < 4)
            {
                throw new InvalidDataException("Image is too small.");
            }
            var magic = new byte[4];
            await input.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
            sparseInput = BinaryPrimitives.ReadUInt32LittleEndian(magic) == 0xED26FF3A;
        }

        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (sparseInput)
            {
                SparseImageConverter.ConvertSparseToRawCancellable([source], temporary, cancellationToken);
            }
            else
            {
                await using var input = File.OpenRead(source);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                await input.CopyToAsync(output, 65536, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var staged = File.OpenRead(temporary))
            {
                if (staged.Length < 2048)
                {
                    throw new InvalidDataException("EXT4 image is too small.");
                }
                staged.Position = 1024 + 56;
                var magic = new byte[2];
                await staged.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
                if (BinaryPrimitives.ReadUInt16LittleEndian(magic) != 0xEF53)
                {
                    throw new InvalidDataException("Input does not contain a raw EXT4 filesystem.");
                }
            }

            var image = tools.PathForTool(temporary, cancellationToken);
            if (repairBeforeResize)
            {
                var repaired = await tools.RunAsync("e2fsck", ["-fy", image], cancellationToken).ConfigureAwait(false);
                if (repaired.ExitCode is not (0 or 1))
                {
                    throw new InvalidDataException($"e2fsck repair failed ({repaired.ExitCode}): {repaired.Output}");
                }
            }

            await CheckAsync(tools, image, cancellationToken).ConfigureAwait(false);
            NativeFileSystemTools.RequireSuccess("resize2fs",
                await tools.RunAsync("resize2fs", ["-M", image], cancellationToken).ConfigureAwait(false));
            await CheckAsync(tools, image, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await using (var result = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                result.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task CheckAsync(NativeFileSystemTools tools, string image, CancellationToken cancellationToken)
        => NativeFileSystemTools.RequireSuccess("e2fsck",
            await tools.RunAsync("e2fsck", ["-fn", image], cancellationToken).ConfigureAwait(false));
}