using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using SharpCompress.Compressors.LZMA;
using UotanStudio.Hash.Crc32Ieee;
using UotanStudio.Hash.Digests;
using UotanStudio.Hash.XzCrc64;

namespace UotanStudio.Compress.Codecs.Xz;

/// <summary>
/// Streaming <c>.xz</c> decoder that parses the container itself and decodes each block's LZMA2 payload through the
/// LZMA decoder, applying the declared branch or delta filter with correct chunk-boundary carry handling. Only one
/// XZ stream (one header, blocks, index and footer) is consumed per instance.
/// <para>自行解析容器的流式 <c>.xz</c> 解码器：每个块的 LZMA2 载荷经 LZMA 解码器还原，并按正确的块边界进位处理应用
/// 声明的分支或差分过滤器。每个实例只消费一个 XZ 流（一个头部、若干块、索引与尾部）。</para>
/// </summary>
internal sealed class XzDecoderStream : Stream
{
    private const int RawChunkBytes = 256 * 1024;
    private const int MaximumBlockHeaderBytes = 1024;
    private const int MaximumIndexRecords = 1 << 20;
    private const int MaximumDictionaryBytes = 512 * 1024 * 1024;
    private const ulong FilterIdLzma2 = 0x21;

    private static ReadOnlySpan<byte> HeaderMagic => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    private static ReadOnlySpan<byte> FooterMagic => [0x59, 0x5A];

    private static ReadOnlySpan<byte> IndexIndicator => [0x00];

    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly List<(ulong Unpadded, ulong Uncompressed)> _records = [];
    private CountingStream? _counter;
    private XzBlock? _block;
    private byte[]? _streamFlags;
    private byte _checkType;
    private DecoderState _state;
    private bool _disposed;
    private Task<int> _completedReadTask = Task.FromResult(0);
    private int _completedReadValue;

    internal XzDecoderStream(Stream input, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(input));
        }

        _input = input;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The buffer range is invalid.");
        }

        return Read(buffer.AsSpan(offset, count));
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = Read(buffer, offset, count);
        if (read != _completedReadValue || _completedReadTask.Status != TaskStatus.RanToCompletion)
        {
            _completedReadValue = read;
            _completedReadTask = Task.FromResult(read);
        }

        return _completedReadTask;
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            if (_block is { } block)
            {
                var read = block.Read(buffer);
                if (read > 0)
                {
                    return read;
                }

                var consumed = block.Complete();
                _records.Add((block.UnpaddedSize, consumed));
                block.Dispose();
                _block = null;
                continue;
            }

            switch (_state)
            {
                case DecoderState.StreamHeader:
                    ReadStreamHeader();
                    _state = DecoderState.BlockHeader;
                    continue;
                case DecoderState.BlockHeader:
                    var sizeByte = ReadByteOrThrow();
                    if (sizeByte == 0)
                    {
                        var indexCrc = new Crc32IeeeAccumulator();
                        indexCrc.Append(IndexIndicator);
                        ReadIndexAndFooter(indexCrc);
                        _state = DecoderState.Done;
                        continue;
                    }

                    _block = ReadBlockHeader(sizeByte);
                    continue;
                default:
                    return 0;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _block?.Dispose();
            _block = null;
            if (!_leaveOpen)
            {
                _input.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void ReadStreamHeader()
    {
        _counter = new CountingStream(_input, leaveOpen: true);
        Span<byte> header = stackalloc byte[12];
        _counter.ReadExactly(header);
        if (!header[..6].SequenceEqual(HeaderMagic))
        {
            throw new InvalidDataException("The XZ stream header magic is invalid.");
        }

        if (header[6] != 0)
        {
            throw new InvalidDataException("The XZ stream header uses reserved flag bits.");
        }

        if (!IsSupportedCheck(header[7]))
        {
            throw new CompressionException($"Unsupported XZ check type: {header[7]}.");
        }

        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (Crc32Ieee.Compute(header[6..8]) != expectedCrc)
        {
            throw new InvalidDataException("The XZ stream header CRC-32 is invalid.");
        }

        _streamFlags = header[6..8].ToArray();
        _checkType = header[7];
    }

    private XzBlock ReadBlockHeader(byte sizeByte)
    {
        var totalBytes = (sizeByte + 1) * 4;
        if (totalBytes > MaximumBlockHeaderBytes)
        {
            throw new InvalidDataException("The XZ block header is too large.");
        }

        var headerBytes = ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            var header = headerBytes.AsSpan(0, totalBytes);
            header[0] = sizeByte;
            _counter!.ReadExactly(header[1..]);
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[(totalBytes - 4)..]);
            if (Crc32Ieee.Compute(header[..(totalBytes - 4)]) != expectedCrc)
            {
                throw new InvalidDataException("The XZ block header CRC-32 is invalid.");
            }

            var flags = header[1];
            if ((flags & 0x3C) != 0)
            {
                throw new InvalidDataException("The XZ block header uses reserved flag bits.");
            }

            var filterCount = (flags & 0x03) + 1;
            var limit = totalBytes - 4;
            var position = 2;
            ulong? compressedSize = null;
            ulong? uncompressedSize = null;
            if ((flags & 0x40) != 0)
            {
                compressedSize = ReadVli(header, ref position, limit);
            }

            if ((flags & 0x80) != 0)
            {
                uncompressedSize = ReadVli(header, ref position, limit);
            }

            XzBlockFilter? filter = null;
            byte lzma2Properties = 0;
            for (var filterIndex = 0; filterIndex < filterCount; filterIndex++)
            {
                var filterId = ReadVli(header, ref position, limit);
                var propertyLength = ReadVli(header, ref position, limit);
                if (propertyLength > 64 || position + (int)propertyLength > limit)
                {
                    throw new InvalidDataException("The XZ block filter properties are invalid.");
                }

                var properties = header.Slice(position, (int)propertyLength);
                position += (int)propertyLength;
                if (filterIndex == filterCount - 1)
                {
                    if (filterId != FilterIdLzma2)
                    {
                        throw new CompressionException($"Unsupported XZ terminal filter: {filterId}.");
                    }

                    if (properties.Length != 1 || properties[0] > 40)
                    {
                        throw new InvalidDataException("The XZ LZMA2 filter properties are invalid.");
                    }

                    lzma2Properties = properties[0];
                }
                else
                {
                    if (filter is not null || filterIndex != 0)
                    {
                        throw new CompressionException("XZ filter chains with more than one non-LZMA2 filter are unsupported.");
                    }

                    filter = XzBlockFilter.Create(filterId, properties);
                }
            }

            for (var i = position; i < limit; i++)
            {
                if (header[i] != 0)
                {
                    throw new InvalidDataException("The XZ block header padding is not zero.");
                }
            }

            if (compressedSize > long.MaxValue || uncompressedSize > long.MaxValue)
            {
                throw new InvalidDataException("The XZ block declares a size outside the supported range.");
            }

            var dictionarySize = DictionarySizeFromProperties(lzma2Properties);
            if (dictionarySize > MaximumDictionaryBytes)
            {
                throw new CompressionException($"XZ dictionary size {dictionarySize} exceeds the supported {MaximumDictionaryBytes} byte ceiling.");
            }

            return new XzBlock(
                _counter,
                _checkType,
                filter,
                lzma2Properties,
                compressedSize,
                uncompressedSize,
                totalBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBytes);
        }
    }

    private void ReadIndexAndFooter(Crc32IeeeAccumulator indexCrc)
    {
        var indexStart = _counter!.BytesRead - 1;
        var recordCount = ReadIndexVli(indexCrc);
        if (recordCount > MaximumIndexRecords)
        {
            throw new InvalidDataException($"The XZ index declares {recordCount} records, above the supported ceiling.");
        }

        if ((ulong)_records.Count != recordCount)
        {
            throw new InvalidDataException("The XZ index record count does not match the decoded blocks.");
        }

        for (var i = 0; i < _records.Count; i++)
        {
            var unpadded = ReadIndexVli(indexCrc);
            var uncompressed = ReadIndexVli(indexCrc);
            if (unpadded != _records[i].Unpadded || uncompressed != _records[i].Uncompressed)
            {
                throw new InvalidDataException("The XZ index record does not match its block.");
            }
        }

        var indexBodyBytes = _counter.BytesRead - indexStart;
        var padding = (int)((4 - ((indexBodyBytes + 4) % 4)) % 4);
        for (var i = 0; i < padding; i++)
        {
            if (ReadIndexByte(indexCrc) != 0)
            {
                throw new InvalidDataException("The XZ index padding is not zero.");
            }
        }

        Span<byte> crcBytes = stackalloc byte[4];
        _counter.ReadExactly(crcBytes);
        var expectedIndexCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes);
        if (indexCrc.GetCurrentHash() != expectedIndexCrc)
        {
            throw new InvalidDataException("The XZ index CRC-32 is invalid.");
        }

        var indexBytes = _counter.BytesRead - indexStart;

        Span<byte> footer = stackalloc byte[12];
        _counter.ReadExactly(footer);
        var expectedFooterCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        if (Crc32Ieee.Compute(footer[4..10]) != expectedFooterCrc)
        {
            throw new InvalidDataException("The XZ footer CRC-32 is invalid.");
        }

        var backwardSize = BinaryPrimitives.ReadUInt32LittleEndian(footer[4..8]);
        var indexLength = (long)(backwardSize + 1) * 4;
        if (indexLength != indexBytes)
        {
            throw new InvalidDataException("The XZ footer backward size does not match the index.");
        }

        if (!footer[10..12].SequenceEqual(FooterMagic))
        {
            throw new InvalidDataException("The XZ footer magic is invalid.");
        }

        if (!footer[8..10].SequenceEqual(_streamFlags))
        {
            throw new InvalidDataException("The XZ footer stream flags do not match the stream header.");
        }
    }

    private byte ReadIndexByte(Crc32IeeeAccumulator crc)
    {
        var value = ReadByteOrThrow();
        Span<byte> single = stackalloc byte[1];
        single[0] = value;
        crc.Append(single);
        return value;
    }

    private ulong ReadIndexVli(Crc32IeeeAccumulator crc)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var current = ReadIndexByte(crc);
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift > 56)
            {
                throw new InvalidDataException("The XZ index variable-length integer is invalid.");
            }
        }
    }

    private byte ReadByteOrThrow()
    {
        var value = _counter!.ReadByte();
        return value >= 0 ? (byte)value : throw new EndOfStreamException("The XZ stream ended unexpectedly.");
    }

    private static ulong ReadVli(ReadOnlySpan<byte> header, ref int position, int limit)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            if (position >= limit || shift > 56)
            {
                throw new InvalidDataException("The XZ variable-length integer is invalid.");
            }

            var current = header[position++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }

    private static int DictionarySizeFromProperties(byte properties)
    {
        if (properties > 40)
        {
            throw new InvalidDataException("The XZ LZMA2 dictionary property is invalid.");
        }

        return (2 | (properties & 1)) << ((properties / 2) + 11);
    }

    private static bool IsSupportedCheck(byte checkType) => checkType is 0x00 or 0x01 or 0x04 or 0x0A;

    private enum DecoderState
    {
        StreamHeader,
        BlockHeader,
        Done
    }

    /// <summary>
    /// One decoded XZ block: LZMA2 payload, optional inverse filter, integrity check and declared sizes.
    /// <para>单个已解码的 XZ 块：LZMA2 载荷、可选逆过滤器、完整性校验与声明大小。</para>
    /// </summary>
    private sealed class XzBlock : IDisposable
    {
        private readonly CountingStream _input;
        private readonly XzBlockFilter? _filter;
        private readonly LzmaStream _lzma;
        private readonly XzBlockCheck _check;
        private readonly byte[] _carry = new byte[16];
        private readonly long _dataStart;
        private readonly int _headerLength;
        private readonly int _checkLength;
        private readonly ulong? _declaredCompressed;
        private readonly ulong? _declaredUncompressed;
        private byte[]? _buffer;
        private int _carryLength;
        private int _pendingOffset;
        private int _pendingCount;
        private long _absoluteOffset;
        private long _decodedTotal;
        private ulong _unpaddedSize;
        private bool _lzmaEnded;
        private bool _disposed;

        internal XzBlock(
            CountingStream input,
            byte checkType,
            XzBlockFilter? filter,
            byte lzma2Properties,
            ulong? declaredCompressed,
            ulong? declaredUncompressed,
            int headerLength)
        {
            _input = input;
            _filter = filter;
            _declaredCompressed = declaredCompressed;
            _declaredUncompressed = declaredUncompressed;
            _headerLength = headerLength;
            _check = XzBlockCheck.Create(checkType);
            _checkLength = XzBlockCheck.GetLength(checkType);
            _dataStart = input.BytesRead;
            var property = new[] { lzma2Properties };
            _lzma = LzmaStream.Create(
                property,
                input,
                declaredCompressed is { } compressed ? (long)compressed : -1,
                declaredUncompressed is { } uncompressed ? (long)uncompressed : -1,
                leaveOpen: true);
        }

        internal ulong UnpaddedSize => _unpaddedSize;

        internal int Read(Span<byte> destination)
        {
            while (true)
            {
                if (_pendingCount > 0)
                {
                    var copy = Math.Min(destination.Length, _pendingCount);
                    _buffer!.AsSpan(_pendingOffset, copy).CopyTo(destination);
                    _pendingOffset += copy;
                    _pendingCount -= copy;
                    _check.Append(destination[..copy]);
                    _decodedTotal += copy;
                    return copy;
                }

                if (_lzmaEnded)
                {
                    if (_carryLength == 0)
                    {
                        return 0;
                    }

                    if (destination.Length >= _carryLength)
                    {
                        _carry.AsSpan(0, _carryLength).CopyTo(destination);
                        _check.Append(destination[.._carryLength]);
                        _decodedTotal += _carryLength;
                        var carried = _carryLength;
                        _carryLength = 0;
                        return carried;
                    }

                    EnsureBuffer();
                    _carry.AsSpan(0, _carryLength).CopyTo(_buffer);
                    _pendingOffset = 0;
                    _pendingCount = _carryLength;
                    _carryLength = 0;
                    continue;
                }

                if (destination.Length >= RawChunkBytes)
                {
                    var carry = _carryLength;
                    if (carry > 0)
                    {
                        _carry.AsSpan(0, carry).CopyTo(destination);
                    }

                    var directRaw = _lzma.Read(destination[carry..]);
                    if (directRaw == 0)
                    {
                        _lzmaEnded = true;
                        continue;
                    }

                    var directLength = carry + directRaw;
                    var directProcessed = _filter is null
                        ? directLength
                        : _filter.Transform(destination, directLength, _absoluteOffset);
                    _carryLength = directLength - directProcessed;
                    if (_carryLength > 0)
                    {
                        destination.Slice(directProcessed, _carryLength).CopyTo(_carry);
                    }

                    _absoluteOffset += directProcessed;
                    _check.Append(destination[..directProcessed]);
                    _decodedTotal += directProcessed;
                    return directProcessed;
                }

                EnsureBuffer();
                _carry.AsSpan(0, _carryLength).CopyTo(_buffer);
                var raw = _lzma.Read(_buffer.AsSpan(_carryLength));
                if (raw == 0)
                {
                    _lzmaEnded = true;
                    continue;
                }

                var length = _carryLength + raw;
                var processed = _filter is null ? length : _filter.Transform(_buffer, length, _absoluteOffset);
                _carryLength = length - processed;
                if (_carryLength > 0)
                {
                    _buffer.AsSpan(processed, _carryLength).CopyTo(_carry);
                }

                _absoluteOffset += processed;
                _pendingOffset = 0;
                _pendingCount = processed;
            }
        }

        private void EnsureBuffer()
        {
            _buffer ??= ArrayPool<byte>.Shared.Rent(RawChunkBytes + 16);
        }

        internal ulong Complete()
        {
            var consumed = _input.BytesRead - _dataStart;
            _unpaddedSize = checked((ulong)_headerLength + (ulong)consumed + (ulong)_checkLength);
            if (_declaredCompressed is { } compressed && (ulong)consumed != compressed)
            {
                throw new InvalidDataException("The XZ block compressed size does not match its header.");
            }

            if (_declaredUncompressed is { } uncompressed && (ulong)_decodedTotal != uncompressed)
            {
                throw new InvalidDataException("The XZ block uncompressed size does not match its header.");
            }

            var padding = (int)((4 - ((_headerLength + consumed) % 4)) % 4);
            Span<byte> paddingBytes = stackalloc byte[4];
            _input.ReadExactly(paddingBytes[..padding]);
            for (var i = 0; i < padding; i++)
            {
                if (paddingBytes[i] != 0)
                {
                    throw new InvalidDataException("The XZ block padding is not zero.");
                }
            }

            Span<byte> storedCheck = stackalloc byte[32];
            _input.ReadExactly(storedCheck[.._checkLength]);
            if (!_check.Verify(storedCheck[.._checkLength]))
            {
                throw new InvalidDataException("The XZ block integrity check does not match the decoded data.");
            }

            return (ulong)_decodedTotal;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lzma.Dispose();
            if (_buffer is { } buffer)
            {
                _buffer = null;
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Counting forward-only view over the container stream; it never seeks and reports the exact byte count consumed.
    /// <para>容器流上的只进计数视图；不定位，并报告精确的已消费字节数。</para>
    /// </summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveOpen;

        internal CountingStream(Stream inner, bool leaveOpen)
        {
            _inner = inner;
            _leaveOpen = leaveOpen;
        }

        internal long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            BytesRead = checked(BytesRead + read);
            return read;
        }

        public override int ReadByte()
        {
            var value = _inner.ReadByte();
            if (value >= 0)
            {
                BytesRead = checked(BytesRead + 1);
            }

            return value;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Incremental integrity check matching one XZ check type.
    /// <para>与某种 XZ 校验类型匹配的增量完整性校验。</para>
    /// </summary>
    private abstract class XzBlockCheck
    {
        internal abstract void Append(ReadOnlySpan<byte> data);

        internal abstract bool Verify(ReadOnlySpan<byte> stored);

        internal static XzBlockCheck Create(byte checkType) => checkType switch
        {
            0x00 => new NoneCheck(),
            0x01 => new Crc32Check(),
            0x04 => new Crc64Check(),
            0x0A => new Sha256Check(),
            _ => throw new CompressionException($"Unsupported XZ check type: {checkType}.")
        };

        internal static int GetLength(byte checkType) => checkType switch
        {
            0x00 => 0,
            0x01 => 4,
            0x04 => 8,
            0x0A => 32,
            _ => throw new CompressionException($"Unsupported XZ check type: {checkType}.")
        };
    }

    private sealed class NoneCheck : XzBlockCheck
    {
        internal override void Append(ReadOnlySpan<byte> data)
        {
        }

        internal override bool Verify(ReadOnlySpan<byte> stored) => stored.IsEmpty;
    }

    private sealed class Crc32Check : XzBlockCheck
    {
        private readonly Crc32IeeeAccumulator _hash = new();

        internal override void Append(ReadOnlySpan<byte> data) => _hash.Append(data);

        internal override bool Verify(ReadOnlySpan<byte> stored) =>
            stored.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(stored) == _hash.GetCurrentHash();
    }

    private sealed class Crc64Check : XzBlockCheck
    {
        private readonly XzCrc64 _hash = new();

        internal override void Append(ReadOnlySpan<byte> data) => _hash.Append(data);

        internal override bool Verify(ReadOnlySpan<byte> stored) =>
            stored.Length == 8 && BinaryPrimitives.ReadUInt64LittleEndian(stored) == _hash.GetCurrentHashAsUInt64();
    }

    private sealed class Sha256Check : XzBlockCheck
    {
        private readonly DigestAccumulator _hash = Sha256.CreateAccumulator();

        internal override void Append(ReadOnlySpan<byte> data) => _hash.Append(data);

        internal override bool Verify(ReadOnlySpan<byte> stored) =>
            stored.Length == 32 && CryptographicOperations.FixedTimeEquals(_hash.GetHashAndReset(), stored);
    }
}