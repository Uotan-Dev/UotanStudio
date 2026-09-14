namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

/// <summary>Reads the ARM zImage header and locates its compressed payload.</summary>
public static class ZImageTool
{
    private const int MagicOffset = 0x24;
    private const int HeaderSize = 0x40;

    public sealed class ParseResult
    {
        public bool IsValid { get; init; }
        public bool IsBigEndian { get; init; }
        public CompressionType CompressionType { get; init; }
        public ulong EntryAddress { get; init; }
        public ulong KernelStart { get; init; }
        public ulong KernelEnd { get; init; }
        public int CompressedOffset { get; init; }
        public int CompressedSize { get; init; }
        public byte[] CompressedPayload { get; init; } = [];
        public string? ErrorMessage { get; init; }
    }

    public static bool IsZImage(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            return false;
        var magic = data.Slice(MagicOffset, 4);
        return magic.SequenceEqual(new byte[] { 0xED, 0xFE, 0x0F, 0x00 }) ||
            magic.SequenceEqual(new byte[] { 0x00, 0x0F, 0xFE, 0xED }) ||
            magic.SequenceEqual(new byte[] { 0x18, 0x28, 0x6F, 0x01 }) ||
            magic.SequenceEqual(new byte[] { 0x01, 0x6F, 0x28, 0x18 });
    }

    public static ParseResult Parse(ReadOnlySpan<byte> data)
    {
        if (!IsZImage(data))
            return new() { ErrorMessage = "Invalid zImage magic." };

        var littleMagic = ReadUInt32(data[MagicOffset..], false);
        var bigEndian = data.Slice(MagicOffset, 4).SequenceEqual(new byte[] { 0x00, 0x0F, 0xFE, 0xED }) ||
            data.Slice(MagicOffset, 4).SequenceEqual(new byte[] { 0x01, 0x6F, 0x28, 0x18 });
        var magic = ReadUInt32(data[MagicOffset..], bigEndian);
        if (littleMagic is not (0x000FFEEDu or 0x016F2818u or 0x18286F01u or 0xEDFE0F00u))
            return new() { ErrorMessage = "Unsupported zImage magic." };

        var payloadOffset = FindPayloadOffset(data);
        if (payloadOffset < HeaderSize || payloadOffset >= data.Length)
            return new() { ErrorMessage = "Compressed zImage payload was not found." };

        var payload = data[payloadOffset..].ToArray();
        return new ParseResult
        {
            IsValid = true,
            IsBigEndian = bigEndian,
            CompressionType = BootCompressor.Detect(payload),
            EntryAddress = ReadUInt32(data[0x28..], bigEndian),
            KernelStart = ReadUInt32(data[0x30..], bigEndian),
            KernelEnd = ReadUInt32(data[0x34..], bigEndian),
            CompressedOffset = payloadOffset,
            CompressedSize = payload.Length,
            CompressedPayload = payload
        };
    }

    public static byte[] ExtractKernel(ReadOnlySpan<byte> data, bool decompress = true)
    {
        var parsed = Parse(data);
        if (!parsed.IsValid)
            return [];
        return decompress ? BootCompressor.DecompressOrReturn(parsed.CompressedPayload) : parsed.CompressedPayload;
    }

    private static int FindPayloadOffset(ReadOnlySpan<byte> data)
    {
        for (var i = HeaderSize; i + 4 <= data.Length; i++)
        {
            var probeLength = Math.Min(8, data.Length - i);
            if (BootCompressor.Detect(data.Slice(i, probeLength).ToArray()) != CompressionType.None)
                return i;
        }
        return HeaderSize;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool bigEndian) =>
        bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data) :
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data);
}