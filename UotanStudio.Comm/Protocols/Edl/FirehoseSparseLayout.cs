using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BlockImage.Sparse.Models;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Applies Firehose sector constraints to a validated Firmware sparse image.</summary>
internal static class FirehoseSparseLayout
{
    internal static SparseFile Read(Stream source, long sectorSize, FirehoseSectorAddress startSector,
        long? maximumSectors, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sectorSize, 65536);
        if (maximumSectors.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSectors.Value);
        }

        var maximumLength = maximumSectors is { } sectors && sectors <= long.MaxValue / sectorSize
            ? sectors * sectorSize : long.MaxValue;
        var image = SparseReader.FromStream(source, true, false, 65536, cancellationToken, maximumLength);
        try
        {
            if (source.Position != source.Length)
            {
                throw new InvalidDataException("Sparse image contains trailing bytes beyond its declared chunks.");
            }

            var logicalLength = checked((long)image.Header.TotalBlocks * image.Header.BlockSize);
            if (logicalLength == 0 || logicalLength % sectorSize != 0)
            {
                throw new InvalidDataException("Sparse logical length must be a positive sector multiple.");
            }

            var totalSectors = logicalLength / sectorSize;
            startSector.ValidateRange(totalSectors);
            if (maximumSectors.HasValue && totalSectors > maximumSectors.Value)
            {
                throw new InvalidDataException("Sparse logical length exceeds the declared partition range.");
            }

            foreach (var chunk in image.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((long)chunk.StartBlock * image.Header.BlockSize % sectorSize != 0
                    || (long)chunk.Header.ChunkSize * image.Header.BlockSize % sectorSize != 0)
                {
                    throw new InvalidDataException("Every sparse chunk boundary must be sector-aligned.");
                }
            }

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }
}