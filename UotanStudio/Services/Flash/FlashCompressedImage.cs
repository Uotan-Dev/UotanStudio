using UotanStudio.Firmware.Compression.Compressors;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Services.Flash;

/// <summary>Stages a compressed image outside the source directory for Fastboot.</summary>
public static class FlashCompressedImage
{
    private const long MaximumImageBytes = 128L * 1024 * 1024 * 1024;

    /// <summary>Streams and bounds decompression; caller owns the returned temporary file.</summary>
    public static Task<string> PrepareAsync(string source, CancellationToken ct)
        => Task.Run(() =>
        {
            var temp = Path.Combine(Path.GetTempPath(), "uotan-flash-" + Guid.NewGuid().ToString("N") + ".img");
            try
            {
                using (var input = File.OpenRead(source))
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var bounded = new CountingWriteStream(output, MaximumImageBytes, leaveOpen: true, cancellationToken: ct))
                {
                    Span<byte> magic = stackalloc byte[4];
                    if (input.Read(magic) != 4 || !ZstdCompressor.IsZstdFormat(magic.ToArray()))
                    {
                        throw new InvalidDataException("Invalid zstd image.");
                    }
                    input.Position = 0;
                    ZstdCompressor.Decompress(input, bounded);
                    if (bounded.BytesWritten == 0)
                    {
                        throw new InvalidDataException("The decompressed image is empty.");
                    }
                    output.Flush(flushToDisk: true);
                }
                ct.ThrowIfCancellationRequested();
                return temp;
            }
            catch
            {
                if (File.Exists(temp)) File.Delete(temp);
                throw;
            }
        }, ct);
}