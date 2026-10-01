using System.Text;
using SharpCompress.Compressors.LZMA;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.VendorFirmware.NTPi.Models;

namespace UotanStudio.Firmware.VendorFirmware.NTPi;

/// <summary>
/// Compresses and decompresses NTDECOMP-framed LZMA payloads, either as whole byte arrays or bounded stream-to-stream.
/// <para>压缩与解压以 NTDECOMP 头封装的 LZMA 数据，支持整字节数组和受限的流到流两种方式。</para>
/// </summary>
public interface ILzma2Compressor
{

    private const int NtDecompressHeaderSize = 112;

    /// <summary>
    /// Decompresses data that includes the NtDecompressHeader.
    /// <para>解压包含 NtDecompressHeader 的压缩数据。</para>
    /// </summary>
    /// <param name="data">
    /// The 112-byte header, the compressed payload and any trailing zero AES padding.
    /// <para>依次为 112 字节头部、压缩负载以及末尾的零值 AES 填充。</para>
    /// </param>
    /// <returns>The decompressed bytes, sized as declared by the header. <para>解压后的字节，长度为头部声明的大小。</para></returns>
    byte[] Decompress(byte[] data);

    /// <summary>
    /// Compresses data into a format that includes the NtDecompressHeader.
    /// <para>将数据压缩为包含 NtDecompressHeader 的格式。</para>
    /// </summary>
    /// <param name="data">The plaintext bytes to compress. <para>待压缩的明文字节。</para></param>
    /// <param name="originalSize">
    /// The declared original size; it must equal <paramref name="data"/>.Length.
    /// <para>声明的原始大小，必须等于 <paramref name="data"/>.Length。</para>
    /// </param>
    /// <returns>The header followed by the compressed payload. <para>头部后接压缩负载。</para></returns>
    byte[] Compress(byte[] data, ulong originalSize);

    /// <summary>
    /// Streams-decompresses a stream containing the NtDecompressHeader, bounding the number of output bytes by maximumOutputBytes.
    /// <para>流式解压包含 NtDecompressHeader 的压缩流,输出字节数受 maximumOutputBytes 限制。</para>
    /// </summary>
    /// <param name="input">
    /// Readable stream positioned at the header start; left open.
    /// <para>定位在头部起始处的可读流，调用后保持打开。</para>
    /// </param>
    /// <param name="output">
    /// Writable stream receiving the decompressed bytes; left open.
    /// <para>接收解压字节的可写流，调用后保持打开。</para>
    /// </param>
    /// <param name="maximumOutputBytes">
    /// Hard upper bound on the declared decompressed size; must not be negative.
    /// <para>声明解压大小的硬上限；不得为负数。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumOutputBytes"/> is negative. <para><paramref name="maximumOutputBytes"/> 为负数。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The input ends before the header or declared payload is consumed.
    /// <para>输入流在头部或声明的负载读完前结束。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The header is invalid, its declared size exceeds the limit, trailing padding is not all zero,
    /// or the output length disagrees with the header.
    /// <para>头部无效、声明大小超限、末尾填充非全零，或输出长度与头部不一致。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The byte-array fallback would exceed the CLR byte-array limit.
    /// <para>字节数组回退路径将超出 CLR 数组上限。</para>
    /// </exception>
    void Decompress(Stream input, Stream output, long maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);

        var header = new byte[NtDecompressHeaderSize];
        input.ReadExactly(header);
        var parsed = StructHelper.ReadNtDecompressHeader(header);
        if (!parsed.IsValid())
        {
            throw new InvalidDataException("Invalid NTDECOMP header in legacy decompression fallback.");
        }
        if (parsed.OriginalSize > (ulong)maximumOutputBytes)
        {
            throw new InvalidDataException(
                $"Declared decompressed size {parsed.OriginalSize} exceeds the {maximumOutputBytes}-byte limit.");
        }
        if (parsed.OriginalSize > (ulong)Array.MaxLength || parsed.ProcessedSize == 0 ||
            parsed.ProcessedSize > (ulong)(Array.MaxLength - NtDecompressHeaderSize))
        {
            throw new NotSupportedException(
                "The legacy byte-array decompressor fallback exceeds the CLR byte-array limit; override the stream overload.");
        }

        var totalLength = checked(NtDecompressHeaderSize + (int)parsed.ProcessedSize);
        var compressed = new byte[totalLength];
        header.CopyTo(compressed, 0);
        input.ReadExactly(compressed.AsSpan(NtDecompressHeaderSize));
        for (var paddingLength = 0; ; paddingLength++)
        {
            var trailing = input.ReadByte();
            if (trailing == -1)
            {
                break;
            }
            if (trailing != 0 || paddingLength == 15)
            {
                throw new InvalidDataException(
                    "Compressed input trailing data must be fewer than 16 bytes of zero AES padding.");
            }
        }

        var result = Decompress(compressed);
        if ((ulong)result.LongLength != parsed.OriginalSize)
        {
            throw new InvalidDataException(
                $"Decompressed output length {result.LongLength} does not match declared size {parsed.OriginalSize}.");
        }
        output.Write(result, 0, result.Length);
    }

    /// <summary>
    /// Streams-compresses the input stream into a format that includes the NtDecompressHeader.
    /// <para>流式压缩输入流为包含 NtDecompressHeader 的格式。</para>
    /// </summary>
    /// <param name="input">
    /// Readable stream providing exactly <paramref name="originalSize"/> bytes from its current
    /// position; left open.
    /// <para>从当前位置提供恰好 <paramref name="originalSize"/> 字节的可读流，调用后保持打开。</para>
    /// </param>
    /// <param name="output">
    /// Writable stream receiving header and payload; left open.
    /// <para>接收头部与负载的可写流，调用后保持打开。</para>
    /// </param>
    /// <param name="originalSize">Declared plaintext size in bytes. <para>声明的明文大小（字节）。</para></param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The byte-array fallback would exceed the CLR byte-array limit.
    /// <para>字节数组回退路径将超出 CLR 数组上限。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The input ends before <paramref name="originalSize"/> bytes are consumed.
    /// <para>输入流在读完 <paramref name="originalSize"/> 字节前结束。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The input still has bytes after the declared size was consumed.
    /// <para>读完声明大小后输入流仍有剩余字节。</para>
    /// </exception>
    void Compress(Stream input, Stream output, ulong originalSize)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (originalSize > (ulong)Array.MaxLength)
        {
            throw new NotSupportedException(
                "The legacy byte-array compressor fallback exceeds the CLR byte-array limit; override the stream overload.");
        }

        using var buffer = new MemoryStream((int)originalSize);
        var copyBuffer = new byte[81920];
        ulong remaining = originalSize;
        while (remaining != 0)
        {
            var read = input.Read(copyBuffer, 0, (int)Math.Min((ulong)copyBuffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("Input ended before the declared original size was consumed.");
            }
            buffer.Write(copyBuffer, 0, read);
            remaining -= (ulong)read;
        }
        if (input.ReadByte() != -1)
        {
            throw new InvalidDataException("Input contains data beyond the declared original size.");
        }

        var result = Compress(buffer.ToArray(), originalSize);
        output.Write(result, 0, result.Length);
    }
}

/// <summary>
/// Frames payloads with the 112-byte <c>NTDECOMP</c> header and encodes them with LZMA;
/// decomposition reads the header subtype to select the LZMA1 or LZMA2 decoder.
/// <para>使用 112 字节的 <c>NTDECOMP</c> 头封装数据并以 LZMA 编码；解压时根据头部子类型选择 LZMA1 或 LZMA2 解码器。</para>
/// </summary>
internal sealed class Lzma2Compressor : ILzma2Compressor
{
    private const int HeaderSize = 112;
    private const uint Lzma1Subtype = 2;
    private static readonly byte[] ZeroPadding2 = new byte[72];
    private static readonly byte[] NtDecompMagic = Encoding.ASCII.GetBytes("NTDECOMP");

    /// <summary>
    /// Decompresses the input data.
    /// <para>解压输入数据。</para>
    /// </summary>
    /// <param name="data">
    /// Header, compressed payload and at most 15 trailing zero AES-padding bytes, read from index 0.
    /// <para>从索引 0 起依次为头部、压缩负载，以及至多 15 字节的零值 AES 填充。</para>
    /// </param>
    /// <returns>
    /// The decompressed bytes, exactly as long as the header's declared original size.
    /// <para>解压后的字节，长度严格等于头部声明的原始大小。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is no longer than the 112-byte header, or the header magic is not <c>NTDECOMP</c>.
    /// <para><paramref name="data"/> 不长于 112 字节头部，或头部魔数不是 <c>NTDECOMP</c>。</para>
    /// </exception>
    /// <exception cref="Lzma2CompressionException">
    /// A declared size is out of bounds, trailing padding is invalid, decoding fails, or the
    /// produced length disagrees with the header.
    /// <para>声明大小越界、末尾填充无效、解码失败，或产出长度与头部声明不一致。</para>
    /// </exception>
    public byte[] Decompress(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length <= HeaderSize)
        {
            throw new ArgumentException(
                $"Data length ({data.Length}) must be greater than the NtDecompressHeader size ({HeaderSize} bytes).",
                nameof(data));
        }

        var header = ReadAndValidateHeader(data);


        if (header.OriginalSize > (ulong)Array.MaxLength)
        {
            throw new Lzma2CompressionException(
                $"Declared decompressed size {header.OriginalSize} exceeds the byte-array output limit.");
        }

        if (header.ProcessedSize is 0 or > int.MaxValue)
        {
            throw new Lzma2CompressionException("Invalid ProcessedSize in NtDecompressHeader.");
        }
        var payloadSize = (int)header.ProcessedSize;
        if (payloadSize > data.Length - HeaderSize)
        {
            throw new Lzma2CompressionException(
                $"ProcessedSize {payloadSize} exceeds the remaining compressed input {data.Length - HeaderSize}.");
        }

        ValidateTrailingAesPadding(data.AsSpan(HeaderSize + payloadSize));

        byte[] result;
        var originalSize = (long)header.OriginalSize;

        if (originalSize > 0)
        {
            result = new byte[(int)originalSize];
            using var preallocatedStream = new MemoryStream(result, 0, (int)originalSize, true);
            try
            {
                if (header.DecompressSubtype == Lzma1Subtype)
                {
                    DecompressLzma1(data, payloadSize, header.OriginalSize, preallocatedStream);
                }
                else
                {
                    DecompressLzma2(data, payloadSize, preallocatedStream);
                }
            }
            catch (Exception ex) when (ex is not ArgumentNullException and not ArgumentException and not Lzma2CompressionException)
            {
                throw new Lzma2CompressionException("Failed to decompress LZMA2 data. The compressed data may be corrupted.", ex);
            }

            if (preallocatedStream.Position != originalSize)
            {
                throw new Lzma2CompressionException(
                    $"Decompressed output length {preallocatedStream.Position} does not match declared size {originalSize}.");
            }
        }
        else
        {
            using var outputStream = new MemoryStream();
            try
            {
                if (header.DecompressSubtype == Lzma1Subtype)
                {
                    DecompressLzma1(data, payloadSize, header.OriginalSize, outputStream);
                }
                else
                {
                    DecompressLzma2(data, payloadSize, outputStream);
                }
            }
            catch (Exception ex) when (ex is not ArgumentNullException and not ArgumentException and not Lzma2CompressionException)
            {
                throw new Lzma2CompressionException("Failed to decompress LZMA2 data. The compressed data may be corrupted.", ex);
            }
            result = outputStream.ToArray();
        }

        if ((ulong)result.LongLength != header.OriginalSize)
        {
            throw new Lzma2CompressionException(
                $"Decompressed output length {result.LongLength} does not match declared size {header.OriginalSize}.");
        }

        return result;
    }

    /// <summary>
    /// Decompresses the input data.
    /// <para>解压输入数据。</para>
    /// </summary>
    /// <param name="input">
    /// Readable stream positioned at the header start; consumed through the payload and trailing
    /// padding, and left open.
    /// <para>定位在头部起始处的可读流；读取负载与末尾填充后保持打开。</para>
    /// </param>
    /// <param name="output">
    /// Writable stream receiving exactly the declared number of bytes; left open.
    /// <para>接收声明字节数的可写流，调用后保持打开。</para>
    /// </param>
    /// <param name="maximumOutputBytes">
    /// Hard upper bound on the declared decompressed size; must not be negative.
    /// <para>声明解压大小的硬上限；不得为负数。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumOutputBytes"/> is negative. <para><paramref name="maximumOutputBytes"/> 为负数。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The input ends before the header or declared payload is consumed.
    /// <para>输入流在头部或声明的负载读完前结束。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The header magic is not <c>NTDECOMP</c>. <para>头部魔数不是 <c>NTDECOMP</c>。</para>
    /// </exception>
    /// <exception cref="Lzma2CompressionException">
    /// A declared size exceeds the output limit or the remaining input, decoding fails, the payload
    /// is not fully consumed, the produced length disagrees with the header, or trailing padding is invalid.
    /// <para>声明大小超出输出上限或剩余输入、解码失败、负载未读完、产出长度与头部不一致，或末尾填充无效。</para>
    /// </exception>
    public void Decompress(Stream input, Stream output, long maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);

        var headerBuffer = new byte[HeaderSize];
        input.ReadExactly(headerBuffer);
        var header = StructHelper.ReadNtDecompressHeader(headerBuffer);
        if (!header.IsValid())
        {
            throw new ArgumentException(
                $"Invalid NtDecompressHeader Magic: expected 'NTDECOMP', got '{header.GetMagicString()}'.");
        }

        if (header.OriginalSize > long.MaxValue || header.OriginalSize > (ulong)maximumOutputBytes)
        {
            throw new Lzma2CompressionException(
                $"Declared decompressed size {header.OriginalSize} exceeds the {maximumOutputBytes}-byte output limit.");
        }
        if (header.ProcessedSize is 0 or > long.MaxValue)
        {
            throw new Lzma2CompressionException("Invalid ProcessedSize in NtDecompressHeader.");
        }
        var payloadSize = (long)header.ProcessedSize;
        if (input.CanSeek && payloadSize > input.Length - input.Position)
        {
            throw new Lzma2CompressionException(
                $"ProcessedSize {payloadSize} exceeds the remaining compressed input {input.Length - input.Position}.");
        }

        var originalSize = (long)header.OriginalSize;

        using var limitedOutput = new CountingWriteStream(output, originalSize, leaveOpen: true);
        try
        {
            if (header.DecompressSubtype == Lzma1Subtype)
            {
                if (payloadSize <= 5)
                {
                    throw new Lzma2CompressionException("LZMA1 data is too short to contain valid properties.");
                }

                var properties = new byte[5];
                input.ReadExactly(properties);

                var compressedDataSize = payloadSize - 5;
                using var boundedInput = new LengthLimitedReadStream(input, compressedDataSize, leaveOpen: true);
                using var lzma = LzmaStream.Create(properties, boundedInput, compressedDataSize, originalSize);
                lzma.CopyTo(limitedOutput);
                if (boundedInput.Position != compressedDataSize)
                {
                    throw new Lzma2CompressionException(
                        $"Compressed payload was not fully consumed ({boundedInput.Position}/{compressedDataSize} bytes).");
                }
            }
            else
            {
                using var boundedInput = new LengthLimitedReadStream(input, payloadSize, leaveOpen: true);
                using var lzma = LzmaStream.Create(new byte[1], boundedInput);
                lzma.CopyTo(limitedOutput);
                if (boundedInput.Position != payloadSize)
                {
                    throw new Lzma2CompressionException(
                        $"Compressed payload was not fully consumed ({boundedInput.Position}/{payloadSize} bytes).");
                }
            }
        }
        catch (Exception ex) when (ex is not ArgumentNullException and not ArgumentException and not Lzma2CompressionException)
        {
            throw new Lzma2CompressionException("Failed to decompress LZMA2 data. The compressed data may be corrupted.", ex);
        }

        if (limitedOutput.BytesWritten != originalSize)
        {
            throw new Lzma2CompressionException(
                $"Decompressed output length {limitedOutput.BytesWritten} does not match declared size {originalSize}.");
        }

        ValidateTrailingAesPadding(input);
    }

    private static void ValidateTrailingAesPadding(ReadOnlySpan<byte> padding)
    {
        if (padding.Length > 15 || padding.IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new Lzma2CompressionException(
                "Compressed input trailing data must be fewer than 16 bytes of zero AES padding.");
        }
    }

    private static void ValidateTrailingAesPadding(Stream input)
    {
        for (var paddingLength = 0; ; paddingLength++)
        {
            var value = input.ReadByte();
            if (value == -1)
            {
                return;
            }
            if (value != 0 || paddingLength == 15)
            {
                throw new Lzma2CompressionException(
                    "Compressed input trailing data must be fewer than 16 bytes of zero AES padding.");
            }
        }
    }

    private static void DecompressLzma2(byte[] data, int payloadSize, MemoryStream outputStream)
    {
        using var inputStream = new MemoryStream(data, HeaderSize, payloadSize);
        using var lzma = LzmaStream.Create(new byte[1], inputStream);
        lzma.CopyTo(outputStream);
    }

    private static void DecompressLzma1(byte[] data, int payloadSize, ulong originalSize, MemoryStream outputStream)
    {
        if (payloadSize <= 5)
        {
            throw new Lzma2CompressionException("LZMA1 data is too short to contain valid properties.");
        }

        var properties = new byte[5];
        Array.Copy(data, HeaderSize, properties, 0, 5);

        var compressedDataSize = payloadSize - 5;
        using var inputStream = new MemoryStream(data, HeaderSize + 5, compressedDataSize);
        using var lzma = LzmaStream.Create(properties, inputStream, compressedDataSize, (long)originalSize);
        lzma.CopyTo(outputStream);
    }

    /// <summary>
    /// Compresses the input data.
    /// <para>压缩输入数据。</para>
    /// </summary>
    /// <param name="data">Plaintext bytes to compress; must not be empty. <para>待压缩的明文字节；不得为空。</para></param>
    /// <param name="originalSize">
    /// Declared original size; must equal <paramref name="data"/>.Length.
    /// <para>声明的原始大小，必须等于 <paramref name="data"/>.Length。</para>
    /// </param>
    /// <returns>
    /// The <c>NTDECOMP</c> header followed by the 5-byte LZMA1 properties and the LZMA1 payload;
    /// no trailing padding is added.
    /// <para><c>NTDECOMP</c> 头部，其后为 5 字节 LZMA1 属性与 LZMA1 负载；不追加末尾填充。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is empty or its length disagrees with <paramref name="originalSize"/>.
    /// <para><paramref name="data"/> 为空，或其长度与 <paramref name="originalSize"/> 不一致。</para>
    /// </exception>
    /// <exception cref="Lzma2CompressionException">
    /// The LZMA encoder failed. <para>LZMA 编码器执行失败。</para>
    /// </exception>
    public byte[] Compress(byte[] data, ulong originalSize)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
        {
            throw new ArgumentException("Data cannot be empty.", nameof(data));
        }
        if ((ulong)data.Length != originalSize)
        {
            throw new ArgumentException(
                $"Data length ({data.Length}) does not match originalSize ({originalSize}).",
                nameof(data));
        }

        byte[] properties;
        int compressedLength;
        byte[] compressedBuffer;
        try
        {
            var estimatedCapacity = Math.Max(256, (int)(originalSize / 2));
            using var compressedStream = new MemoryStream(estimatedCapacity);
            using (var lzma = LzmaStream.Create(LzmaEncoderProperties.Default, false, compressedStream))
            {
                lzma.Write(data, 0, data.Length);
                properties = lzma.Properties;
            }

            compressedLength = (int)compressedStream.Length;
            compressedBuffer = compressedStream.GetBuffer();
        }
        catch (Exception ex) when (ex is not ArgumentNullException and not ArgumentException)
        {
            throw new Lzma2CompressionException("Failed to compress data using LZMA.", ex);
        }

        var payloadLength = 5 + compressedLength;
        var result = new byte[HeaderSize + payloadLength];

        var header = new NtDecompressHeader
        {
            Magic = NtDecompMagic,
            PrimaryType = 1,
            DecompressSubtype = Lzma1Subtype,
            Padding = 0,
            ProcessedSize = (ulong)payloadLength,
            OriginalSize = originalSize,
            Padding2 = ZeroPadding2
        };

        StructHelper.WriteNtDecompressHeader(header, result);

        var propLen = Math.Min(properties.Length, 5);
        Buffer.BlockCopy(properties, 0, result, HeaderSize, propLen);
        Buffer.BlockCopy(compressedBuffer, 0, result, HeaderSize + 5, compressedLength);

        return result;
    }

    /// <summary>
    /// Compresses the input data.
    /// <para>压缩输入数据。</para>
    /// </summary>
    /// <param name="input">
    /// Readable stream providing exactly <paramref name="originalSize"/> bytes from its current
    /// position; left open and never repositioned.
    /// <para>从当前位置提供恰好 <paramref name="originalSize"/> 字节的可读流；调用后保持打开且不会被重新定位。</para>
    /// </param>
    /// <param name="output">
    /// Writable stream receiving header, LZMA1 properties and payload; left open. The encoder
    /// spools through a temporary file that is always deleted.
    /// <para>接收头部、LZMA1 属性与负载的可写流，调用后保持打开。编码过程经临时文件中转，且该文件必定被删除。</para>
    /// </param>
    /// <param name="originalSize">
    /// Declared plaintext size in bytes; must be positive and no greater than <see cref="long.MaxValue"/>.
    /// <para>声明的明文字节数；必须为正数且不超过 <see cref="long.MaxValue"/>。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="originalSize"/> is zero. <para><paramref name="originalSize"/> 为 0。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="originalSize"/> exceeds <see cref="long.MaxValue"/>.
    /// <para><paramref name="originalSize"/> 超过 <see cref="long.MaxValue"/>。</para>
    /// </exception>
    /// <exception cref="Lzma2CompressionException">
    /// The input ends early or carries bytes beyond the declared size, or the LZMA encoder failed;
    /// a partial write to <paramref name="output"/> is possible when the encoder faults.
    /// <para>输入提前结束或超出声明大小仍有剩余字节，或 LZMA 编码失败；编码器出错时 <paramref name="output"/> 可能已被部分写入。</para>
    /// </exception>
    public void Compress(Stream input, Stream output, ulong originalSize)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (originalSize == 0)
        {
            throw new ArgumentException("Data cannot be empty.", nameof(originalSize));
        }
        if (originalSize > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(originalSize));
        }

        var spoolPath = Path.Combine(Path.GetTempPath(), $"ntpi-lzma-{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] properties;
            long compressedLength;
            using (var compressedStream = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.ReadWrite,
                       FileShare.None, 81920, FileOptions.SequentialScan | FileOptions.DeleteOnClose))
            {
                try
                {
                    using (var lzma = LzmaStream.Create(LzmaEncoderProperties.Default, false, compressedStream))
                    {
                        var remaining = (long)originalSize;
                        var buffer = new byte[81920];
                        while (remaining > 0)
                        {
                            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (read == 0)
                            {
                                throw new Lzma2CompressionException(
                                    $"Input ended before the declared original size of {originalSize} bytes was consumed.");
                            }

                            lzma.Write(buffer, 0, read);
                            remaining -= read;
                        }

                        if (input.ReadByte() != -1)
                        {
                            throw new Lzma2CompressionException(
                                $"Input contains data beyond the declared original size of {originalSize} bytes; pass a bounded stream for a subrange.");
                        }

                        properties = lzma.Properties;
                    }
                    compressedLength = compressedStream.Length;

                    var payloadLength = checked(5UL + (ulong)compressedLength);
                    var headerBuffer = new byte[HeaderSize];
                    var header = new NtDecompressHeader
                    {
                        Magic = NtDecompMagic,
                        PrimaryType = 1,
                        DecompressSubtype = Lzma1Subtype,
                        Padding = 0,
                        ProcessedSize = payloadLength,
                        OriginalSize = originalSize,
                        Padding2 = ZeroPadding2
                    };

                    StructHelper.WriteNtDecompressHeader(header, headerBuffer);
                    output.Write(headerBuffer, 0, HeaderSize);
                    output.Write(properties, 0, Math.Min(properties.Length, 5));
                    compressedStream.Position = 0;
                    compressedStream.CopyTo(output);
                }
                catch (Exception ex) when (ex is not ArgumentNullException and not ArgumentException and not Lzma2CompressionException)
                {
                    throw new Lzma2CompressionException("Failed to compress data using LZMA.", ex);
                }
            }
        }
        finally
        {
            try { File.Delete(spoolPath); } catch { }
        }
    }

    private static NtDecompressHeader ReadAndValidateHeader(byte[] data)
    {
        var header = StructHelper.ReadNtDecompressHeader(data);
        return !header.IsValid()
            ? throw new ArgumentException(
                $"Invalid NtDecompressHeader Magic: expected 'NTDECOMP', got '{header.GetMagicString()}'.",
                nameof(data))
            : header;
    }
}