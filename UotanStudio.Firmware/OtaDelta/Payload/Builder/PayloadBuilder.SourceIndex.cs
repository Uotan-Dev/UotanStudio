using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.OtaDelta.Diff;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    private sealed class IncrementalSourceIndex : IDisposable
    {
        private readonly FileStream _records = BinaryDeltaPatch.CreateTemporaryStream();
        private readonly FileStream _snapshot = BinaryDeltaPatch.CreateTemporaryStream();
        private readonly long[] _heads = new long[65536];
        private readonly int _blockSize;
        private readonly byte[] _comparison;
        private readonly byte[] _record = new byte[48];

        public IncrementalSourceIndex(int blockSize)
        {
            _blockSize = blockSize;
            _comparison = new byte[blockSize];
            Array.Fill(_heads, -1);
        }

        [System.Runtime.CompilerServices.SkipLocalsInit]
        public void Add(ReadOnlySpan<byte> bytes, long block)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(bytes, digest);
            var bucket = BinaryPrimitives.ReadUInt16LittleEndian(digest);
            BinaryPrimitives.WriteInt64LittleEndian(_record, _heads[bucket]);
            BinaryPrimitives.WriteInt64LittleEndian(_record.AsSpan(8), block);
            digest.CopyTo(_record.AsSpan(16));
            _records.Position = _records.Length;
            _heads[bucket] = _records.Position;
            _records.Write(_record);
            _snapshot.Position = checked(block * _blockSize);
            _snapshot.Write(bytes);
        }

        [System.Runtime.CompilerServices.SkipLocalsInit]
        public long Find(ReadOnlySpan<byte> bytes, long preferred, CancellationToken token)
        {
            if (preferred >= 0 && preferred < _snapshot.Length / _blockSize)
            {
                Read(preferred, _comparison, token);
                if (bytes.SequenceEqual(_comparison))
                {
                    return preferred;
                }
            }
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(bytes, digest);
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
            _snapshot.Position = checked(block * _blockSize);
            ReadIncrementalChunk(_snapshot, bytes, token);
        }

        public void Dispose()
        {
            _records.Dispose();
            _snapshot.Dispose();
        }
    }
}