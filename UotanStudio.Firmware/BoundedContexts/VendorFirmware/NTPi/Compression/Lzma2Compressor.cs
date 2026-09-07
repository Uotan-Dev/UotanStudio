using System.Text;
using SharpCompress.Compressors.LZMA;
using UotanStudio.Firmware.SharedKernel.Streams;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Exceptions;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Helpers;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Compression;

public interface ILzma2Compressor
{

    private const int NtDecompressHeaderSize = 112;

    /// <summary>
    /// Decompresses data that includes the NtDecompressHeader.
    /// <para>解压包含 NtDecompressHeader 的压缩数据。</para>
    /// </summary>
    byte[] Decompress(byte[] data);

    /// <summary>
    /// Compresses data into a format that includes the NtDecompressHeader.
    /// <para>将数据压缩为包含 NtDecompressHeader 的格式。</para>
    /// </summary>
    byte[] Compress(byte[] data, ulong originalSize);

    /// <summary>
    /// Streams-decompresses a stream containing the NtDecompressHeader, bounding the number of output bytes by maximumOutputBytes.
    /// <para>流式解压包含 NtDecompressHeader 的压缩流,输出字节数受 maximumOutputBytes 限制。</para>
    /// </summary>
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

public sealed class Lzma2Compressor : ILzma2Compressor
{
    private const int HeaderSize = 112;
    private const uint Lzma1Subtype = 2;
    private static readonly byte[] ZeroPadding2 = new byte[72];
    private static readonly byte[] NtDecompMagic = Encoding.ASCII.GetBytes("NTDECOMP");

    /// <summary>
    /// Decompresses the input data.
    /// <para>解压输入数据。</para>
    /// </summary>
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
                using var boundedInput = new ReadLimitedStream(input, compressedDataSize);
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
                using var boundedInput = new ReadLimitedStream(input, payloadSize);
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
            try { File.Delete(spoolPath); } catch {  }
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
