using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using SharpCompress.Compressors.BZip2;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Core;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload;

public sealed partial class PayloadBuilder
{
    private static void CompressStream(Stream inputStream, Stream outputStream, CompressionType compressionType)
    {
        var format = compressionType switch
        {
            CompressionType.None => UotanStudio.Firmware.CrossCutting.Compress.CompressionFormat.None,
            CompressionType.BZip2 => UotanStudio.Firmware.CrossCutting.Compress.CompressionFormat.Bzip2,
            CompressionType.Xz => UotanStudio.Firmware.CrossCutting.Compress.CompressionFormat.Xz,
            _ => throw new PayloadGenerationException("Unsupported payload REPLACE compression.")
        };
        UotanStudio.Firmware.CrossCutting.Compress.CompressionService.Compress(inputStream, outputStream, format,
            // Operations contain at most 1 MiB and reset codec history. A larger dictionary
            // cannot reference additional input but multiplies each worker's match-finder memory.
            new UotanStudio.Firmware.CrossCutting.Compress.CompressionOptions
            {
                MaxDegreeOfParallelism = 1,
                DictionarySize = 1u << 20
            });
    }
}