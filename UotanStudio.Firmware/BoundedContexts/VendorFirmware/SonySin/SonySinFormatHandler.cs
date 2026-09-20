using System.Buffers.Binary;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.SonySin;

/// <summary>Conservative Sony SIN header detector; chunk decoding is intentionally explicit and read-only.</summary>
public sealed class SonySinFormatHandler : IReadonlyFormatHandler
{
    public string FormatName => "Sony SIN";
    public IReadOnlyList<string> FileExtensions => ["sin"];

    public bool CanHandle(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            using var stream = File.OpenRead(filePath);
            Span<byte> header = stackalloc byte[32];
            if (stream.Read(header) != header.Length) return false;
            var version = header[0];
            if (version is < 1 or > 3) return false;
            return version != 3 || (header[1] == (byte)'S' && header[2] == (byte)'I' && header[3] == (byte)'N');
        }
        catch { return false; }
    }

    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(filePath)) return FirmwareOpResult.Fail("Not a content-confirmed Sony SIN file.");
        await using var stream = File.OpenRead(filePath);
        Span<byte> header = stackalloc byte[32];
        stream.ReadExactly(header);
        var version = header[0];
        var headerSize = version < 3 ? BinaryPrimitives.ReadUInt32BigEndian(header[2..6]) + 16u : BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        var fileLength = new FileInfo(filePath).Length;
        if (headerSize < (version < 3 ? 17u : 32u) || headerSize >= fileLength)
            return FirmwareOpResult.Fail("SIN header length is outside the file.");
        var dataSize = version < 3 ? checked((ulong)fileLength - headerSize) : BinaryPrimitives.ReadUInt32BigEndian(header[0x18..0x1C]);
        var metadata = new Dictionary<string, string> { ["Version"] = version.ToString(), ["HeaderSize"] = headerSize.ToString(), ["DataSize"] = dataSize.ToString(), ["Length"] = fileLength.ToString(), ["Packing"] = "unsupported" };
        stream.Position = headerSize;
        var mmcf = new byte[8];
        if (await stream.ReadAsync(mmcf, cancellationToken).ConfigureAwait(false) == 8 && mmcf.AsSpan(0, 4).SequenceEqual("MMCF"u8))
        {
            var gptpLength = BinaryPrimitives.ReadUInt32BigEndian(mmcf.AsSpan(4));
            if (gptpLength < 24 || gptpLength > fileLength - headerSize)
                return FirmwareOpResult.Fail("SIN MMCF GPTP length is outside the file.");
            metadata["Container"] = "MMCF";
            metadata["GptpLength"] = gptpLength.ToString();
            metadata["SegmentCount"] = (await CountSegmentsAsync(stream, gptpLength, cancellationToken).ConfigureAwait(false)).ToString();
        }
        return FirmwareOpResult.Ok($"Sony SIN v{version} header confirmed; chunk extraction remains read-only.", metadata: metadata);
    }

    private static async Task<int> CountSegmentsAsync(Stream stream, uint gptpLength, CancellationToken cancellationToken)
    {
        var remaining = checked((long)gptpLength - 24);
        var count = 0;
        var record = new byte[40];
        while (remaining >= record.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.ReadExactlyAsync(record, cancellationToken).ConfigureAwait(false);
            remaining -= record.Length;
            count++;
            var packedOffset = BinaryPrimitives.ReadUInt64BigEndian(record.AsSpan(8));
            var packedSize = BinaryPrimitives.ReadUInt64BigEndian(record.AsSpan(24));
            if (packedOffset == 0 && packedSize == 0) break;
        }
        return count;
    }

    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FirmwareOpResult.Fail("Sony SIN was detected, but chunk extraction is withheld until the segment specification is verified."));
}