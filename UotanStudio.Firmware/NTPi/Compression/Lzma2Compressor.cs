using System.Text;
using SharpCompress.Compressors.LZMA;
using UotanStudio.Firmware.NTPi.Exceptions;
using UotanStudio.Firmware.NTPi.Helpers;
using UotanStudio.Firmware.NTPi.Models;

namespace UotanStudio.Firmware.NTPi.Compression;

/// <summary>
/// <para>LZMA2 压缩解压服务接口。</para>
/// Interface for LZMA2 compression and decompression services.
/// </summary>
public interface ILzma2Compressor
{
    /// <summary>
    /// <para>解压包含 NtDecompressHeader 的压缩数据。</para>
    /// Decompresses data that includes the NtDecompressHeader.
    /// </summary>
    byte[] Decompress(byte[] data);

    /// <summary>
    /// <para>将数据压缩为包含 NtDecompressHeader 的格式。</para>
    /// Compresses data into a format that includes the NtDecompressHeader.
    /// </summary>
    byte[] Compress(byte[] data, ulong originalSize);
}

/// <summary>
/// <para>LZMA2 压缩解压服务实现。</para>
/// Implementation of LZMA2 compression and decompression services.
/// </summary>
public sealed class Lzma2Compressor : ILzma2Compressor
{
    private const int HeaderSize = 112;
    private const uint Lzma1Subtype = 2;
    private static readonly byte[] ZeroPadding2 = new byte[72];
    private static readonly byte[] NtDecompMagic = Encoding.ASCII.GetBytes("NTDECOMP");

    /// <inheritdoc />
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

        var payloadSize = (int)header.ProcessedSize;
        if (payloadSize <= 0 || HeaderSize + payloadSize > data.Length)
        {
            payloadSize = data.Length - HeaderSize;
        }

        byte[] result;
        var originalSize = (long)header.OriginalSize;

        if (originalSize is > 0 and <= int.MaxValue)
        {
            result = new byte[originalSize];
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

            if (preallocatedStream.Length != originalSize)
            {
                result = result.AsSpan(0, (int)preallocatedStream.Length).ToArray();
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

        return result;
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

    /// <inheritdoc />
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