using UotanStudio.Compress.Compressors;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Services.Flash;

/// <summary>Stages a compressed image outside the source directory for Fastboot.<para>在源目录之外暂存解压后的镜像，用于 Fastboot 写入。</para></summary>
public static class FlashCompressedImage
{
    private const long MaximumImageBytes = 128L * 1024 * 1024 * 1024;

    /// <summary>Streams decompression under a size limit into a temporary file the receiver owns.<para>按大小上限流式解压，写入由接收方持有的临时文件。</para></summary>
    /// <param name="source">Compressed image file to stage.<para>要暂存的压缩镜像文件。</para></param>
    /// <param name="ct">Token cancelling the staging.<para>用于取消暂存的令牌。</para></param>
    /// <returns>Path of the decompressed temporary image.<para>解压后临时镜像的路径。</para></returns>
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