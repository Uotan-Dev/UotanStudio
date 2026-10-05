using System.Buffers;
using System.IO.Compression;

namespace UotanStudio.Firmware.OtaDelta.Payload;

internal static class ZstdReplaceDecoder
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumWindowLog = 26;
    private const long MaximumDictionaryBytes = 64L * 1024 * 1024;

    internal static void Decode(Stream input, Stream output, CancellationToken cancellationToken)
        => DecodeCore(input, output, null, cancellationToken);

    /// <summary>
    /// Decodes a Zstandard REPLACE frame compressed against an external raw content dictionary.
    /// <para>解压基于外部原始内容字典压缩的 Zstandard REPLACE 帧。</para>
    /// </summary>
    /// <param name="input">Frame bytes to decode. <para>待解压的帧字节。</para></param>
    /// <param name="output">Plaintext sink. <para>明文字节接收流。</para></param>
    /// <param name="dictionary">
    /// Exact raw dictionary bytes registered with the compressor; empty reproduces the dictionary-free path.
    /// <para>与压缩器登记的完全一致的原始字典字节；为空时复现无字典路径。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels between buffer refills. <para>在缓冲区重填之间响应取消。</para></param>
    /// <remarks>
    /// AOSP gates external dictionaries with the payload properties <c>max_zstd_dict_raw_size</c> (byte ceiling
    /// for the dictionary a frame may use) and <c>max_zstd_dict_trust</c>; those keys live in
    /// <c>payload_properties.txt</c> and are parsed into <see cref="Payload.Models.PayloadProperties"/>.
    /// <see cref="PayloadExtractor"/> resolves a per-partition dictionary, rejects one above its configured ceiling
    /// and threads it into REPLACE_ZSTD decoding; callers without a manifest ceiling keep the 64 MiB bound here.
    /// <para>AOSP 通过载荷属性 <c>max_zstd_dict_raw_size</c>（一帧可用字典的字节上限）与 <c>max_zstd_dict_trust</c>
    /// 管控外部字典；这些键位于 <c>payload_properties.txt</c>，由 <see cref="Payload.Models.PayloadProperties"/> 解析。
    /// <see cref="PayloadExtractor"/> 按分区解析字典、拒绝超出所配置上限的字典，并把字典贯穿到 REPLACE_ZSTD 解码；
    /// 未声明清单上限的调用方沿用本解码器的 64 MiB 上界。</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="dictionary"/> exceeds the 64 MiB ceiling. <para><paramref name="dictionary"/> 超过 64 MiB 上限。</para>
    /// </exception>
    internal static void Decode(Stream input, Stream output, ReadOnlyMemory<byte> dictionary, CancellationToken cancellationToken)
    {
        if (dictionary.Length > MaximumDictionaryBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(dictionary), "Zstd external dictionary exceeds the 64 MiB ceiling.");
        }

        DecodeCore(input, output, dictionary.IsEmpty ? null : dictionary, cancellationToken);
    }

    private static void DecodeCore(Stream input, Stream output, ReadOnlyMemory<byte>? dictionary, CancellationToken cancellationToken)
    {
        var inputBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var outputBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using ZstandardDictionary? contentDictionary = dictionary is { Length: > 0 } dict
                ? ZstandardDictionary.Create(dict.Span)
                : null;
            using var decoder = contentDictionary is null
                ? new ZstandardDecoder(MaximumWindowLog)
                : new ZstandardDecoder(contentDictionary, MaximumWindowLog);

            var inputOffset = 0;
            var inputLength = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (inputOffset == inputLength)
                {
                    inputLength = input.Read(inputBuffer, 0, BufferSize);
                    inputOffset = 0;
                    if (inputLength == 0)
                    {
                        throw new PartitionExtractionException("Zstd REPLACE frame is truncated.", string.Empty);
                    }
                }

                var status = decoder.Decompress(
                    inputBuffer.AsSpan(inputOffset, inputLength - inputOffset),
                    outputBuffer,
                    out var consumed,
                    out var written);
                inputOffset = checked(inputOffset + consumed);
                cancellationToken.ThrowIfCancellationRequested();
                if (written > 0)
                {
                    output.Write(outputBuffer, 0, written);
                }

                if (status == OperationStatus.Done)
                {
                    if (inputOffset != inputLength || input.ReadByte() != -1)
                    {
                        throw new PartitionExtractionException(
                            "Zstd REPLACE accepts exactly one frame without trailing data.", string.Empty);
                    }

                    return;
                }

                if (status == OperationStatus.InvalidData)
                {
                    throw new PartitionExtractionException(
                        dictionary is null
                            ? "Invalid Zstd REPLACE frame. External dictionaries are not supported."
                            : "Invalid Zstd REPLACE frame for the supplied external dictionary.", string.Empty);
                }

                if (consumed == 0 && written == 0)
                {
                    throw new PartitionExtractionException("Zstd REPLACE decoder made no progress on the frame.", string.Empty);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new PartitionExtractionException(
                "Zstd REPLACE frame exceeds the 64 MiB window limit or is invalid.", string.Empty, ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuffer);
            ArrayPool<byte>.Shared.Return(outputBuffer);
        }
    }
}