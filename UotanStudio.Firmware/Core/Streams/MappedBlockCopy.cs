using System.Buffers;

namespace UotanStudio.Firmware.Core.Streams;

/// <summary>Copies physically contiguous block runs with a bounded buffer; holes remain zero filled.</summary>
internal static class MappedBlockCopy
{
    internal static void Copy(Stream source, Stream destination, long size, int blockSize, Func<uint, long?> mapBlock,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (size == 0) return;
        const int maximumBuffer = 1024 * 1024;
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSize, maximumBuffer);
        var blocks = checked((uint)((size - 1) / blockSize + 1));
        var capacity = checked((int)Math.Min(blocks, maximumBuffer / blockSize) * blockSize);
        var buffer = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            uint logical = 0;
            var remaining = size;
            var pending = mapBlock(0);
            while (logical < blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = pending;
                var run = 1;
                while (run < capacity / blockSize && logical + (uint)run < blocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pending = mapBlock(logical + (uint)run);
                    if (first is null ? pending is not null : pending != checked(first + run)) break;
                    run++;
                }
                var readLength = checked(run * blockSize);
                if (first is { } physical)
                {
                    var offset = checked(physical * blockSize);
                    if (offset < 0 || offset > source.Length || readLength > source.Length - offset)
                        throw new InvalidDataException("Mapped block run lies outside the image.");
                    source.Position = offset;
                    source.ReadExactly(buffer.AsSpan(0, readLength));
                }
                else
                {
                    buffer.AsSpan(0, readLength).Clear();
                }
                var written = (int)Math.Min(remaining, readLength);
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, written);
                cancellationToken.ThrowIfCancellationRequested();
                remaining -= written;
                logical += (uint)run;
                if (logical < blocks && run == capacity / blockSize) pending = mapBlock(logical);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}