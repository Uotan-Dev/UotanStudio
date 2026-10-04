using System.IO.Compression;
using UotanStudio.Firmware.OtaDelta.Diff;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Strict Brotli decompression: round-trips valid streams and rejects trailing, truncated and
/// corrupt streams with <see cref="InvalidDataException"/>.
/// <para>严格 Brotli 解压：完整往返有效流，并以 <see cref="InvalidDataException"/> 拒绝尾随、截断与损坏的流。</para>
/// </summary>
public class StrictBrotliReadStreamTests
{
    [Fact]
    public void Read_CompressedRoundTripData_ReturnsOriginalBytes()
    {
        var original = BuildRepeatedPayload(5, 7777);
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionMode.Compress, true))
        {
            brotli.Write(original);
        }

        using var strict = new StrictBrotliReadStream(new MemoryStream(compressed.ToArray()));
        using var decoded = new MemoryStream();
        strict.CopyTo(decoded);

        Assert.Equal(original, decoded.ToArray());
    }

    [Fact]
    public void Read_TrailingGarbageByte_ThrowsInvalidDataException()
    {
        var compressed = Compress(BuildRepeatedPayload(3, 256));

        using var strict = new StrictBrotliReadStream(new MemoryStream([.. compressed, 0xAB]));
        var destination = new byte[64];

        Assert.Throws<InvalidDataException>(() =>
        {
            while (strict.Read(destination) > 0)
            {
            }
        });
    }

    [Fact]
    public void Read_TruncatedCompressedStream_ThrowsInvalidDataException()
    {
        var compressed = Compress(BuildRepeatedPayload(3, 4096));

        using var strict = new StrictBrotliReadStream(new MemoryStream(compressed[..^8]));
        var destination = new byte[256];

        Assert.Throws<InvalidDataException>(() =>
        {
            while (strict.Read(destination) > 0)
            {
            }
        });
    }

    private static byte[] BuildRepeatedPayload(int rounds, int chunkSize)
    {
        var chunk = new byte[chunkSize];
        for (var i = 0; i < chunkSize; i++)
        {
            chunk[i] = (byte)(i % 251);
        }

        var payload = new byte[chunkSize * rounds];
        for (var round = 0; round < rounds; round++)
        {
            chunk.CopyTo(payload, round * chunkSize);
        }

        return payload;
    }

    private static byte[] Compress(byte[] payload)
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionMode.Compress, true))
        {
            brotli.Write(payload);
        }

        return compressed.ToArray();
    }
}