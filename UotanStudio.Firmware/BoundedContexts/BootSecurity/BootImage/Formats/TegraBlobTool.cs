using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Formats;

/// <summary>Parses the compact NVIDIA Tegra boot blob format.</summary>
public static class TegraBlobTool
{
    private const int HeaderSize = 256;
    private const uint Magic = 0x4144564Eu; // "NVDA" in little-endian order.

    public sealed class Header
    {
        public uint Version { get; init; }
        public uint CompressionType { get; init; }
        public uint KernelSize { get; init; }
        public uint KernelOffset { get; init; }
        public uint RamdiskSize { get; init; }
        public uint RamdiskOffset { get; init; }
        public uint LoadAddress { get; init; }
        public uint EntryAddress { get; init; }
        public uint HeaderSize { get; init; }
        public uint TotalSize { get; init; }
        public string Description { get; init; } = string.Empty;
    }

    public sealed class ParseResult
    {
        public bool IsValid { get; init; }
        public Header? Header { get; init; }
        public byte[] Kernel { get; init; } = [];
        public byte[] Ramdisk { get; init; } = [];
        public string? ErrorMessage { get; init; }
    }

    public static bool IsTegraBlob(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    public static ParseResult Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            return Invalid("Data is shorter than the Tegra blob header.");
        if (!IsTegraBlob(data))
            return Invalid("Invalid Tegra blob magic.");

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]);
        var totalSize = BinaryPrimitives.ReadUInt32LittleEndian(data[40..]);
        if (headerSize < HeaderSize || headerSize > data.Length)
            return Invalid("Tegra header size is outside the input.");
        if (totalSize != 0 && totalSize > data.Length)
            return Invalid("Tegra total size is outside the input.");

        var kernelOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var kernelSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var ramdiskOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
        var ramdiskSize = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        if (!Within(data.Length, kernelOffset, kernelSize) || !Within(data.Length, ramdiskOffset, ramdiskSize))
            return Invalid("Tegra component range is outside the input.");

        var description = data[64..Math.Min(data.Length, 256)]
            .IndexOf((byte)0) is var nul && nul >= 0
            ? System.Text.Encoding.ASCII.GetString(data.Slice(64, nul))
            : System.Text.Encoding.ASCII.GetString(data[64..Math.Min(data.Length, 256)]);

        var header = new Header
        {
            Version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            CompressionType = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            KernelSize = kernelSize,
            KernelOffset = kernelOffset,
            RamdiskSize = ramdiskSize,
            RamdiskOffset = ramdiskOffset,
            LoadAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]),
            EntryAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[32..]),
            HeaderSize = headerSize,
            TotalSize = totalSize,
            Description = description
        };

        return new ParseResult
        {
            IsValid = true,
            Header = header,
            Kernel = data.Slice((int)kernelOffset, (int)kernelSize).ToArray(),
            Ramdisk = data.Slice((int)ramdiskOffset, (int)ramdiskSize).ToArray()
        };
    }

    public static byte[] ExtractKernel(ReadOnlySpan<byte> data, bool decompress = true)
    {
        var parsed = Parse(data);
        return !parsed.IsValid ? [] : decompress ? Decompress(parsed.Kernel, parsed.Header!.CompressionType) : parsed.Kernel;
    }

    public static byte[] ExtractRamdisk(ReadOnlySpan<byte> data, bool decompress = true)
    {
        var parsed = Parse(data);
        return !parsed.IsValid ? [] : decompress ? BootCompressor.DecompressOrReturn(parsed.Ramdisk) : parsed.Ramdisk;
    }

    private static byte[] Decompress(byte[] data, uint compressionType) => compressionType switch
    {
        0 => data,
        1 => BootCompressor.TryDecompress(data, CompressionType.Gzip) ?? data,
        2 => BootCompressor.TryDecompress(data, CompressionType.Lz4) ?? data,
        3 => BootCompressor.TryDecompress(data, CompressionType.Lz4Legacy) ?? data,
        4 => BootCompressor.TryDecompress(data, CompressionType.Lzma) ?? data,
        _ => data
    };

    private static bool Within(int length, uint offset, uint size) =>
        offset <= (ulong)length && size <= (ulong)length - offset;

    private static ParseResult Invalid(string message) => new() { ErrorMessage = message };
}