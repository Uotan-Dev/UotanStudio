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
