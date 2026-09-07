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
        var format = compressionType switch
        {
            CompressionType.None => UotanStudio.Firmware.Compress.CompressionFormat.None,
            CompressionType.BZip2 => UotanStudio.Firmware.Compress.CompressionFormat.Bzip2,
            CompressionType.Xz => UotanStudio.Firmware.Compress.CompressionFormat.Xz,
            _ => throw new PayloadGenerationException("Unsupported payload REPLACE compression.")
        };
        UotanStudio.Firmware.Compress.CompressionService.Compress(inputStream, outputStream, format,
            new UotanStudio.Firmware.Compress.CompressionOptions { MaxDegreeOfParallelism = 1 });
    }
}