using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using SharpCompress.Compressors.BZip2;
using UotanStudio.Firmware.Payload.Core;
using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Proto;

namespace UotanStudio.Firmware.Payload;

public sealed partial class PayloadBuilder
{
    private static void CompressStream(Stream inputStream, Stream outputStream, CompressionType compressionType)
    {
        switch (compressionType)
        {
            case CompressionType.None:
                inputStream.CopyTo(outputStream, 81920);
                break;
            case CompressionType.Brotli:
                {
                    // There is no Brotli full-partition REPLACE operation in the payload
                    // proto (brotli only appears in BROTLI_BSDIFF delta operations), so a
                    // brotli-compressed blob would be written as a raw REPLACE and come out
                    // corrupted on extraction. Store the data uncompressed instead.
                    // <para>payload 协议中没有 Brotli 整分区 REPLACE 操作（brotli 仅用于 BROTLI_BSDIFF
                    // 增量操作），若按 REPLACE 写入 brotli 压缩数据会在提取时损坏。因此此处按未压缩存储。</para>
                    inputStream.CopyTo(outputStream, 81920);
                }
                break;
            case CompressionType.BZip2:
                {
                    var bzData = CompressBZip2(ReadStreamFully(inputStream));
                    if (bzData.Array != null)
                    {
                        outputStream.Write(bzData.Array, bzData.Offset, bzData.Count);
                    }
                }
                break;
            case CompressionType.Xz:
                {
                    var xzData = CompressXz(ReadStreamFully(inputStream));
                    outputStream.Write(xzData, 0, xzData.Length);
                }
                break;
            case CompressionType.Zstd:
                {
                    var zstdData = CompressZstd(ReadStreamFully(inputStream));
                    outputStream.Write(zstdData, 0, zstdData.Length);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(compressionType), $"Compression type {compressionType} is not supported.");
        }
    }

    private static byte[] ReadStreamFully(Stream stream)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            var result = new byte[buffer.Count];
            Array.Copy(buffer.Array!, buffer.Offset, result, 0, buffer.Count);
            return result;
        }

        using var copyMs = new MemoryStream();
        stream.CopyTo(copyMs, 81920);
        return copyMs.ToArray();
    }

    private static ArraySegment<byte> CompressBZip2(byte[] data)
    {
        using var inputStream = new MemoryStream(data);
        var outputStream = new MemoryStream();
        using (var bzStream = BZip2Stream.Create(outputStream, SharpCompress.Compressors.CompressionMode.Compress, false))
        {
            inputStream.CopyTo(bzStream, 81920);
        }

        return outputStream.TryGetBuffer(out var buffer) ? buffer : new ArraySegment<byte>(outputStream.ToArray());
    }

    private static byte[] CompressXz(byte[] data)
    {
        var xzCompressor = new XzCompressor();
        return xzCompressor.Compress(data);
    }

    private static byte[] CompressZstd(byte[] data)
    {
        using var compressor = new ZstdSharp.Compressor();
        return compressor.Wrap(data).ToArray();
    }
}
