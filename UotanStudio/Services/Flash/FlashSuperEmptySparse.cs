using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Reading;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;

namespace UotanStudio.Services.Flash;

/// <summary>Builds a fully zero-filled physical Super image with the validated empty LP metadata.</summary>
internal static class FlashSuperEmptySparse
{
    private const int BlockSize = 4096;

    internal static void Build(PreparedSuperEmpty prepared, string outputPath, CancellationToken ct)
    {
        if (prepared.DeviceSize <= 0 || prepared.DeviceSize % BlockSize != 0)
            throw new InvalidDataException("The Super capacity must be a multiple of 4096 bytes.");
        using var source = new MemoryStream(prepared.MetadataImage, writable: false);
        var metadata = MetadataReader.ReadFromImageStream(source);
        var geometry = prepared.MetadataImage.AsSpan(0, BlockSize).ToArray();
        var table = prepared.MetadataImage.AsSpan(BlockSize).ToArray();
        var paddedTable = new byte[checked((table.Length + BlockSize - 1) / BlockSize * BlockSize)];
        table.CopyTo(paddedTable, 0);
        var segments = new List<(long Offset, byte[] Bytes)>
        {
            (BlockSize, geometry),
            (BlockSize * 2L, geometry)
        };
        for (uint slot = 0; slot < metadata.Geometry.MetadataSlotCount; slot++)
        {
            segments.Add((MetadataReader.GetPrimaryMetadataOffset(metadata.Geometry, slot), paddedTable));
            segments.Add((MetadataReader.GetBackupMetadataOffset(metadata.Geometry, (ulong)prepared.DeviceSize, slot), paddedTable));
        }

        try
        {
            using var sparse = new SparseFile(BlockSize, prepared.DeviceSize);
            long position = 0;
            foreach (var (offset, bytes) in segments.OrderBy(segment => segment.Offset))
            {
                ct.ThrowIfCancellationRequested();
                if (offset < position || offset % BlockSize != 0 || offset > prepared.DeviceSize - bytes.Length)
                    throw new InvalidDataException("LP metadata segments overlap or exceed the Super capacity.");
                if (offset > position) sparse.AddFillChunk(0, offset - position);
                sparse.AddRawChunk(bytes);
                position = offset + bytes.Length;
            }
            if (position < prepared.DeviceSize) sparse.AddFillChunk(0, prepared.DeviceSize - position);
            ct.ThrowIfCancellationRequested();
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            sparse.WriteToStream(output);
            output.Flush(flushToDisk: true);
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            throw;
        }
    }
}
