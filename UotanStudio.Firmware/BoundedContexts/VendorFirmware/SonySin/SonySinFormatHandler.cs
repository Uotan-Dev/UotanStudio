using System.Buffers.Binary;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.SonySin;

/// <summary>Reads Sony SIN v1-v3 images and extracts MMCF/GPTP ADDR and LZ4A segments.</summary>
public sealed class SonySinFormatHandler : IReadonlyFormatHandler
{
    private const int MaximumSegments = 65_536;
    private const long MaximumPackedSegmentBytes = 16L * 1024 * 1024 * 1024;

    public string FormatName => "Sony SIN";
    public IReadOnlyList<string> FileExtensions => ["sin"];

    public bool CanHandle(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }
        try
        {
            using var stream = File.OpenRead(filePath);
            Span<byte> header = stackalloc byte[4];
            if (stream.Read(header) != header.Length || header[0] is < 1 or > 3)
            {
                return false;
            }
            return header[0] != 3 || header[1] == 'S' && header[2] == 'I' && header[3] == 'N';
        }
        catch
        {
            return false;
        }
    }

    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var image = await ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
            return FirmwareOpResult.Ok(
                $"Sony SIN v{image.Version} MMCF/GPTP metadata confirmed ({image.Segments.Count} segments).",
                entries: [.. image.Segments.Select((segment, index) => $"segment-{index:D4}.{segment.Type.ToString().ToLowerInvariant()}")],
                metadata: new Dictionary<string, string>
                {
                    ["Version"] = image.Version.ToString(),
                    ["HeaderSize"] = image.HeaderSize.ToString(),
                    ["DataSize"] = image.DataSize.ToString(),
                    ["Container"] = "MMCF",
                    ["GptpLength"] = image.GptpLength.ToString(),
                    ["GptpUuid"] = Convert.ToHexStringLower(image.GptpUuid),
                    ["SegmentCount"] = image.Segments.Count.ToString(),
                    ["OutputSize"] = image.OutputSize.ToString()
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    public async Task<FirmwareOpResult> UnpackAsync(
        string filePath,
        string outputDirectory,
        UnpackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new UnpackOptions();
        try
        {
            var image = await ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (image.OutputSize > options.MaximumOutputBytes)
            {
                throw new InvalidDataException(
                    $"Sony SIN output requires {image.OutputSize} bytes, exceeding the {options.MaximumOutputBytes}-byte budget.");
            }

            var outputName = Path.GetFileNameWithoutExtension(filePath) + ".img";
            var outputPath = FileExtraction.ResolvePath(outputDirectory, outputName);
            await FileExtraction.WriteAtomicAsync(outputPath, async output =>
            {
                await using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
                long outputPosition = 0;
                foreach (var segment in image.Segments.OrderBy(segment => segment.OutputOffset))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (outputPosition < segment.OutputOffset)
                    {
                        await WriteFillAsync(output, 0xFF, segment.OutputOffset - outputPosition, cancellationToken)
                            .ConfigureAwait(false);
                        outputPosition = segment.OutputOffset;
                    }

                    input.Position = segment.PackedOffset;
                    if (segment.Type == SonySinSegmentType.Addr)
                    {
                        await CopyExactlyAsync(input, output, segment.PackedSize, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Lz4RawDecoder.DecodeAsync(input, segment.PackedSize, output, segment.OutputSize, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    outputPosition = checked(segment.OutputOffset + segment.OutputSize);
                }
            }, options.Overwrite, cancellationToken).ConfigureAwait(false);

            return FirmwareOpResult.Ok(
                $"Extracted {image.Segments.Count} Sony SIN segments to {outputName}.",
                entries: [outputName],
                metadata: new Dictionary<string, string>
                {
                    ["OutputSize"] = image.OutputSize.ToString(),
                    ["GptpUuid"] = Convert.ToHexStringLower(image.GptpUuid)
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    private static async Task<SonySinImage> ParseAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length < 32)
        {
            throw new InvalidDataException("Sony SIN header is truncated.");
        }
        var header = new byte[32];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var version = header[0];
        if (version is < 1 or > 3 || version == 3 && (header[1] != 'S' || header[2] != 'I' || header[3] != 'N'))
        {
            throw new InvalidDataException("Sony SIN signature or version is invalid.");
        }

        var headerSize = version < 3
            ? checked((long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2)) + 16)
            : BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        var dataSize = version < 3
            ? checked(stream.Length - headerSize)
            : BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18));
        if (headerSize < (version < 3 ? 17 : 32) || dataSize <= 0)
        {
            throw new InvalidDataException("Sony SIN header or data length is invalid.");
        }
        FileExtraction.ValidateRange(headerSize, dataSize, stream.Length, "Sony SIN data");

        stream.Position = headerSize;
        var mmcf = new byte[8];
        await stream.ReadExactlyAsync(mmcf, cancellationToken).ConfigureAwait(false);
        if (!mmcf.AsSpan(0, 4).SequenceEqual("MMCF"u8))
        {
            throw new InvalidDataException("Sony SIN does not contain a supported MMCF payload.");
        }
        var gptpLength = BinaryPrimitives.ReadUInt32BigEndian(mmcf.AsSpan(4));
        if (gptpLength < 24 || gptpLength > dataSize - 8)
        {
            throw new InvalidDataException("Sony SIN GPTP metadata length is outside the data region.");
        }

        var gptp = new byte[24];
        await stream.ReadExactlyAsync(gptp, cancellationToken).ConfigureAwait(false);
        if (!gptp.AsSpan(0, 4).SequenceEqual("GPTP"u8))
        {
            throw new InvalidDataException("Sony SIN GPTP signature is invalid.");
        }
        var declaredGptpSize = BinaryPrimitives.ReadUInt32BigEndian(gptp.AsSpan(4));
        if (declaredGptpSize is not 0 && declaredGptpSize != gptpLength)
        {
            throw new InvalidDataException("Sony SIN GPTP size fields disagree.");
        }

        var dataBase = checked(headerSize + 8 + gptpLength);
        FileExtraction.ValidateRange(dataBase, 0, stream.Length, "Sony SIN segment data");
        var metadataEnd = checked(headerSize + 8 + gptpLength);
        var remaining = checked((long)gptpLength - 24);
        var segments = new List<SonySinSegment>();
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segments.Count == MaximumSegments || remaining < 8)
            {
                throw new InvalidDataException("Sony SIN GPTP segment table is malformed or too large.");
            }
            var prefix = new byte[8];
            await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
            var typeBytes = prefix.AsSpan(0, 4);
            var type = typeBytes.SequenceEqual("LZ4A"u8)
                ? SonySinSegmentType.Lz4a
                : typeBytes.SequenceEqual("ADDR"u8)
                    ? SonySinSegmentType.Addr
                    : throw new InvalidDataException($"Unsupported Sony SIN GPTP segment type: {Convert.ToHexString(typeBytes)}.");
            var recordSize = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4));
            var minimumSize = type == SonySinSegmentType.Lz4a ? 40u : 32u;
            if (recordSize < minimumSize || recordSize > remaining)
            {
                throw new InvalidDataException("Sony SIN GPTP segment record size is invalid.");
            }

            var record = new byte[checked((int)recordSize - 8)];
            await stream.ReadExactlyAsync(record, cancellationToken).ConfigureAwait(false);
            var packedRelative = BinaryPrimitives.ReadUInt64BigEndian(record);
            ulong outputSize;
            ulong packedSize;
            ulong outputOffset;
            if (type == SonySinSegmentType.Lz4a)
            {
                outputSize = BinaryPrimitives.ReadUInt64BigEndian(record.AsSpan(8));
                packedSize = BinaryPrimitives.ReadUInt64BigEndian(record.AsSpan(16));
                outputOffset = BinaryPrimitives.ReadUInt64BigEndian(record.AsSpan(24));
            }
            else
            {
                packedSize = BinaryPrimitives.ReadUInt64BigEndian(record.AsSpan(8));
                outputSize = packedSize;
                outputOffset = BinaryPrimitives.ReadUInt64BigEndian(record.AsSpan(16));
            }
            if (packedSize == 0 || outputSize == 0 || packedSize > MaximumPackedSegmentBytes)
            {
                throw new InvalidDataException("Sony SIN GPTP segment sizes are outside the supported range.");
            }
            var absolutePackedOffset = checked(dataBase + checked((long)packedRelative));
            var packedSizeLong = checked((long)packedSize);
            var outputSizeLong = checked((long)outputSize);
            var outputOffsetLong = checked((long)outputOffset);
            FileExtraction.ValidateRange(
                absolutePackedOffset - headerSize,
                packedSizeLong,
                dataSize,
                "Sony SIN packed segment");
            segments.Add(new SonySinSegment(type, absolutePackedOffset, packedSizeLong, outputOffsetLong, outputSizeLong));
            remaining -= recordSize;
        }
        if (stream.Position != metadataEnd || segments.Count == 0)
        {
            throw new InvalidDataException("Sony SIN GPTP table length is inconsistent.");
        }

        var ordered = segments.OrderBy(segment => segment.OutputOffset).ToArray();
        long outputEnd = 0;
        foreach (var segment in ordered)
        {
            if (segment.OutputOffset < outputEnd)
            {
                throw new InvalidDataException("Sony SIN output segments overlap.");
            }
            outputEnd = checked(segment.OutputOffset + segment.OutputSize);
        }

        return new SonySinImage(version, headerSize, dataSize, gptpLength, gptp.AsSpan(8, 16).ToArray(), segments, outputEnd);
    }

    private static async Task CopyExactlyAsync(Stream input, Stream output, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        while (length > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Sony SIN segment data is truncated.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            length -= read;
        }
    }

    private static async Task WriteFillAsync(Stream output, byte value, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        buffer.AsSpan().Fill(value);
        while (length > 0)
        {
            var count = (int)Math.Min(buffer.Length, length);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            length -= count;
        }
    }

    private enum SonySinSegmentType
    {
        Addr,
        Lz4a
    }

    private sealed record SonySinSegment(
        SonySinSegmentType Type,
        long PackedOffset,
        long PackedSize,
        long OutputOffset,
        long OutputSize);

    private sealed record SonySinImage(
        int Version,
        long HeaderSize,
        long DataSize,
        uint GptpLength,
        byte[] GptpUuid,
        List<SonySinSegment> Segments,
        long OutputSize);

    private static class Lz4RawDecoder
    {
        private const int HistorySize = 65_536;

        public static async Task DecodeAsync(
            Stream input,
            long packedLength,
            Stream output,
            long expectedLength,
            CancellationToken cancellationToken)
        {
            var reader = new BoundedByteReader(input, packedLength);
            var writer = new HistoryWriter(output);
            while (reader.Remaining > 0)
            {
                var token = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                var literalLength = await ReadLengthAsync(reader, token >> 4, cancellationToken).ConfigureAwait(false);
                if (literalLength > expectedLength - writer.Written)
                {
                    throw new InvalidDataException("Sony SIN LZ4 literal length exceeds the declared output size.");
                }
                await writer.CopyLiteralsAsync(reader, literalLength, cancellationToken).ConfigureAwait(false);
                if (reader.Remaining == 0)
                {
                    break;
                }

                var offsetLow = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                var offsetHigh = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                var offset = offsetLow | offsetHigh << 8;
                if (offset == 0 || offset > writer.Written || offset > HistorySize - 1)
                {
                    throw new InvalidDataException("Sony SIN LZ4 match offset is invalid.");
                }
                var matchLength = checked(await ReadLengthAsync(reader, token & 0x0F, cancellationToken).ConfigureAwait(false) + 4);
                if (matchLength > expectedLength - writer.Written)
                {
                    throw new InvalidDataException("Sony SIN LZ4 match length exceeds the declared output size.");
                }
                await writer.CopyMatchAsync(offset, matchLength, cancellationToken).ConfigureAwait(false);
            }

            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (reader.Remaining != 0 || writer.Written != expectedLength)
            {
                throw new InvalidDataException(
                    $"Sony SIN LZ4 decoded {writer.Written} bytes; expected {expectedLength} bytes.");
            }
        }

        private static async Task<long> ReadLengthAsync(
            BoundedByteReader reader,
            int initial,
            CancellationToken cancellationToken)
        {
            var length = (long)initial;
            if (initial != 15)
            {
                return length;
            }
            byte extension;
            do
            {
                extension = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                length = checked(length + extension);
            }
            while (extension == byte.MaxValue);
            return length;
        }

        private sealed class BoundedByteReader(Stream input, long remaining)
        {
            private readonly byte[] _buffer = new byte[64 * 1024];
            private int _offset;
            private int _count;
            public long Remaining { get; private set; } = remaining;

            public async ValueTask<byte> ReadByteAsync(CancellationToken cancellationToken)
            {
                if (Remaining == 0)
                {
                    throw new InvalidDataException("Sony SIN LZ4 block is truncated.");
                }
                if (_offset == _count)
                {
                    _count = await input.ReadAsync(_buffer.AsMemory(0, (int)Math.Min(_buffer.Length, Remaining)), cancellationToken)
                        .ConfigureAwait(false);
                    _offset = 0;
                    if (_count == 0)
                    {
                        throw new EndOfStreamException("Sony SIN LZ4 block is truncated.");
                    }
                }
                Remaining--;
                return _buffer[_offset++];
            }
        }

        private sealed class HistoryWriter(Stream output)
        {
            private readonly byte[] _history = new byte[HistorySize];
            private readonly byte[] _outputBuffer = new byte[128 * 1024];
            private int _historyPosition;
            private int _bufferCount;
            public long Written { get; private set; }

            public async Task CopyLiteralsAsync(BoundedByteReader reader, long length, CancellationToken cancellationToken)
            {
                while (length-- > 0)
                {
                    await WriteByteAsync(await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            public async Task CopyMatchAsync(int offset, long length, CancellationToken cancellationToken)
            {
                while (length-- > 0)
                {
                    var value = _history[(_historyPosition - offset) & (HistorySize - 1)];
                    await WriteByteAsync(value, cancellationToken).ConfigureAwait(false);
                }
            }

            public async Task FlushAsync(CancellationToken cancellationToken)
            {
                if (_bufferCount > 0)
                {
                    await output.WriteAsync(_outputBuffer.AsMemory(0, _bufferCount), cancellationToken).ConfigureAwait(false);
                    _bufferCount = 0;
                }
            }

            private async ValueTask WriteByteAsync(byte value, CancellationToken cancellationToken)
            {
                _history[_historyPosition] = value;
                _historyPosition = (_historyPosition + 1) & (HistorySize - 1);
                _outputBuffer[_bufferCount++] = value;
                Written++;
                if (_bufferCount == _outputBuffer.Length)
                {
                    await output.WriteAsync(_outputBuffer, cancellationToken).ConfigureAwait(false);
                    _bufferCount = 0;
                }
            }
        }
    }
}