using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    private static void CompressStream(Stream inputStream, Stream outputStream, CompressionType compressionType)
    {
        var format = compressionType switch
        {
            CompressionType.None => UotanStudio.Firmware.BoundedContexts.Compression.CompressionFormat.None,
            CompressionType.BZip2 => UotanStudio.Firmware.BoundedContexts.Compression.CompressionFormat.Bzip2,
            CompressionType.Xz => UotanStudio.Firmware.BoundedContexts.Compression.CompressionFormat.Xz,
            _ => throw new PayloadGenerationException("Unsupported payload REPLACE compression.")
        };
        UotanStudio.Firmware.BoundedContexts.Compression.CompressionService.Compress(inputStream, outputStream, format,
            new UotanStudio.Firmware.BoundedContexts.Compression.CompressionOptions
            {
                MaxDegreeOfParallelism = 1,
                DictionarySize = 1u << 20
            });
    }
}