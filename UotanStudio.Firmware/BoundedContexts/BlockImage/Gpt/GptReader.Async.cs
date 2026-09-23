namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Gpt;

/// <summary>Fills an entire block-aligned buffer from the given absolute byte offset, or throws.</summary>
/// <remarks>The buffer must not be retained after completion. Reads are sequential, never concurrent.</remarks>
public delegate ValueTask GptBlockReader(long offset, Memory<byte> destination, CancellationToken cancellationToken);

public static partial class GptReader
{
    /// <summary>Reads and validates a GPT pair using bounded asynchronous block reads.</summary>
    /// <param name="readBlocks">Exact reader of the selected device address space; no ownership is transferred.</param>
    /// <param name="blockSize">Explicit logical block size, a power of two from 512 through 65536.</param>
    /// <param name="blockCount">Explicit capacity in blocks.</param>
    /// <param name="cancellationToken">Cancellation propagated through every read.</param>
    /// <returns>A metadata snapshot, not an atomic snapshot of externally changing storage.</returns>
    public static async Task<GptTable> ReadAsync(GptBlockReader readBlocks, int blockSize, ulong blockCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readBlocks);
        ValidateGeometry(blockSize, blockCount);
        cancellationToken.ThrowIfCancellationRequested();
        var header = new byte[blockSize];
        await readBlocks(blockSize, header, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var primary = ParseHeader(header, blockSize, blockCount, 1);
        await readBlocks(checked((long)((blockCount - 1) * (uint)blockSize)), header, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var backup = ParseHeader(header, blockSize, blockCount, blockCount - 1);
        ValidatePair(primary, backup);

        using var builder = new TableBuilder(primary, backup, blockSize, blockCount);
        var left = new byte[65536];
        var right = new byte[left.Length];
        var total = (long)primary.Count * primary.Size;
        for (long offset = 0; offset < total;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var used = (int)Math.Min(left.Length, total - offset);
            // Array CRC excludes sector padding, but block transports must read the final whole sector.
            var transfer = checked((used + blockSize - 1) / blockSize * blockSize);
            await readBlocks(checked((long)(primary.ArrayLba * (uint)blockSize) + offset),
                left.AsMemory(0, transfer), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await readBlocks(checked((long)(backup.ArrayLba * (uint)blockSize) + offset),
                right.AsMemory(0, transfer), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            for (var position = 0; position < used; position += (int)primary.Size)
            {
                builder.Append(checked((int)((offset + position) / primary.Size)),
                    left.AsSpan(position, (int)primary.Size), right.AsSpan(position, (int)primary.Size));
            }
            offset += used;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return builder.Finish();
    }

    private static void ValidateGeometry(int blockSize, ulong blockCount)
    {
        if (blockSize is < 512 or > 65536 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        }
        if (blockCount < 6 || blockCount > (ulong)long.MaxValue / (uint)blockSize)
        {
            throw new ArgumentOutOfRangeException(nameof(blockCount));
        }
    }

    private static void ValidatePair(Header primary, Header backup)
    {
        if (primary.DiskId != backup.DiskId || primary.First != backup.First || primary.Last != backup.Last
            || primary.Count != backup.Count || primary.Size != backup.Size || primary.ArrayCrc != backup.ArrayCrc)
        {
            throw new InvalidDataException("The primary and backup GPT headers disagree.");
        }
    }
}
