using ZstdSharp;
using ZstdSharp.Unsafe;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    private static void CompressStream(Stream inputStream, Stream outputStream, CompressionType compressionType)
    {
        var format = compressionType switch
        {
            CompressionType.None => UotanStudio.Firmware.Compression.CompressionFormat.None,
            CompressionType.BZip2 => UotanStudio.Firmware.Compression.CompressionFormat.Bzip2,
            CompressionType.Xz => UotanStudio.Firmware.Compression.CompressionFormat.Xz,
            CompressionType.Zstd => throw new ArgumentException("Zstd REPLACE compression requires the dictionary-aware overload.", nameof(compressionType)),
            _ => throw new PayloadGenerationException("Unsupported payload REPLACE compression.")
        };
        UotanStudio.Firmware.Compression.CompressionService.Compress(inputStream, outputStream, format,
            new UotanStudio.Firmware.Compression.CompressionOptions
            {
                MaxDegreeOfParallelism = 1,
                DictionarySize = 1u << 20
            });
    }

    /// <summary>
    /// Compresses one REPLACE chunk with Zstandard, optionally against an external raw content dictionary.
    /// Frames carry a content checksum so a wrong or missing dictionary fails loudly instead of
    /// silently yielding garbage.
    /// <para>使用 Zstandard 压缩单个 REPLACE 块，可选地带外部原始内容字典。帧内附带内容校验和，
    /// 使字典缺失或错误时明确失败而不是静默产出错误数据。</para>
    /// </summary>
    /// <param name="inputStream">Chunk plaintext, read to the end. <para>块明文，读取到末尾。</para></param>
    /// <param name="outputStream">Single-frame zstd output sink. <para>单帧 zstd 输出接收流。</para></param>
    /// <param name="dictionary">
    /// Exact raw dictionary bytes, or empty for a dictionary-free frame. Payloads that use a non-empty
    /// dictionary are gated by the AOSP <c>max_zstd_dict_raw_size</c> property in
    /// <c>payload_properties.txt</c>; callers must record that key when publishing such a payload.
    /// <para>完全一致的原始字典字节，空则表示无字典帧。使用非空字典的载荷由 AOSP 在
    /// <c>payload_properties.txt</c> 中的 <c>max_zstd_dict_raw_size</c> 属性管控；发布此类载荷的调用方必须记录该键。</para>
    /// </param>
    /// <param name="level">
    /// Zstandard compression level; the payload default is <c>3</c>. <para>Zstandard 压缩级别；载荷默认 <c>3</c>。</para>
    /// </param>
    /// <exception cref="PayloadGenerationException">
    /// The compressor rejected the dictionary or the frame could not be produced. <para>压缩器拒绝字典或无法生成帧。</para>
    /// </exception>
    internal static void CompressZstdChunk(Stream inputStream, Stream outputStream, ReadOnlyMemory<byte> dictionary, int level = 3)
    {
        using var buffer = new MemoryStream();
        inputStream.CopyTo(buffer);
        try
        {
            byte[] frame;
            using var compressor = new Compressor(level);
            compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            if (!dictionary.IsEmpty)
            {
                compressor.LoadDictionary(dictionary.Span);
            }

            frame = compressor.Wrap(buffer.ToArray()).ToArray();

            outputStream.Write(frame);
        }
        catch (ZstdException ex)
        {
            throw new PayloadGenerationException($"Zstd REPLACE compression failed: {ex.Message}", ex);
        }
    }
}