using System.Buffers.Binary;
using UotanStudio.Firmware.OtaDelta.Diff;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    private sealed class IncrementalSourceIndex : IDisposable
    {
        private readonly FileStream _records = BinaryDeltaPatch.CreateTemporaryStream();
        private readonly Stream _source;
        private readonly long _sourceOrigin;
        private readonly long[] _heads = new long[65536];
        private readonly int _blockSize;
        private readonly byte[] _comparison;
        private readonly byte[] _record = new byte[48];
        private long _indexedBlocks;

        public IncrementalSourceIndex(Stream source, int blockSize)
        {
            _source = source;
            _sourceOrigin = source.Position;
            _blockSize = blockSize;
            _comparison = new byte[blockSize];
            Array.Fill(_heads, -1);
        }

        [System.Runtime.CompilerServices.SkipLocalsInit]
        public void Add(ReadOnlySpan<byte> bytes, long block)
        {
            Span<byte> digest = stackalloc byte[32];
            Sha256.ComputeHash(bytes, digest);
            var bucket = BinaryPrimitives.ReadUInt16LittleEndian(digest);
            BinaryPrimitives.WriteInt64LittleEndian(_record, _heads[bucket]);
            BinaryPrimitives.WriteInt64LittleEndian(_record.AsSpan(8), block);
            digest.CopyTo(_record.AsSpan(16));
            _records.Position = _records.Length;
            _heads[bucket] = _records.Position;
            _records.Write(_record);
            if (block >= _indexedBlocks)
            {
                _indexedBlocks = block + 1;
            }
        }

        [System.Runtime.CompilerServices.SkipLocalsInit]
        public long Find(ReadOnlySpan<byte> bytes, long preferred, CancellationToken token)
        {
            if (preferred >= 0 && preferred < _indexedBlocks)
            {
                Read(preferred, _comparison, token);
                if (bytes.SequenceEqual(_comparison))
                {
                    return preferred;
                }
            }
            Span<byte> digest = stackalloc byte[32];
            Sha256.ComputeHash(bytes, digest);
            var position = _heads[BinaryPrimitives.ReadUInt16LittleEndian(digest)];
            while (position >= 0)
            {
                token.ThrowIfCancellationRequested();
                _records.Position = position;
                _records.ReadExactly(_record);
                var block = BinaryPrimitives.ReadInt64LittleEndian(_record.AsSpan(8));
                if (digest.SequenceEqual(_record.AsSpan(16)))
                {
                    Read(block, _comparison, token);
                    if (bytes.SequenceEqual(_comparison))
                    {
                        return block;
                    }
                }
                position = BinaryPrimitives.ReadInt64LittleEndian(_record);
            }
            return -1;
        }

        public void Read(long block, Span<byte> bytes, CancellationToken token)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(block);
            if (block >= _indexedBlocks)
            {
                throw new PayloadGenerationException("Incremental source index cannot read an unindexed block.");
            }

            _source.Position = _sourceOrigin + checked(block * _blockSize);
            ReadIncrementalChunk(_source, bytes, token);
        }

        public void Dispose()
        {
            _records.Dispose();
        }
    }
}