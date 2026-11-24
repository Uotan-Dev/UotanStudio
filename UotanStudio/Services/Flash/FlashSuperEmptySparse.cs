using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.BlockImage.Sparse.Models;

namespace UotanStudio.Services.Flash;

/// <summary>Builds a fully zero-filled physical Super image with the validated empty LP metadata.
/// <para>以已校验的空 LP 元数据构建全零填充的物理 Super 镜像。</para></summary>
internal static class FlashSuperEmptySparse
{
    private const int BlockSize = 4096;

    internal static void Build(PreparedSuperEmpty prepared, string outputPath, CancellationToken ct)
    {
        using var sparse = BuildModel(prepared, ct);
        try
        {
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

    /// <summary>
    /// Builds the sparse model of the physical empty Super image in memory. Metadata segments are copied into
    /// RAW chunks and all remaining space is a zero FILL chunk, so no full-size sparse file has to be staged on disk.
    /// <para>在内存中构建物理空 Super 镜像的稀疏模型。元数据段复制为 RAW 块，其余空间为零 FILL 块，
    /// 因此无需在磁盘上暂存整份稀疏镜像。</para>
    /// </summary>
    /// <param name="prepared">Validated metadata. <para>已校验的元数据。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The sparse model owned by the caller. <para>由调用方拥有的稀疏模型。</para></returns>
    internal static SparseFile BuildModel(PreparedSuperEmpty prepared, CancellationToken ct)
    {
        if (prepared.DeviceSize <= 0 || prepared.DeviceSize % BlockSize != 0)
            throw new InvalidDataException("The Super capacity must be a multiple of 4096 bytes.");
        using var source = new MemoryStream(prepared.MetadataImage, writable: false);
        var metadata = MetadataReader.ReadFromImageStream(source);
        var geometry = prepared.MetadataImage.AsSpan(0, BlockSize).ToArray();
        var table = prepared.MetadataImage.AsSpan(BlockSize).ToArray();
        var paddedTable = new byte[checked((table.Length + BlockSize - 1) / BlockSize * BlockSize)];
        table.CopyTo(paddedTable, 0);
        List<(long Offset, byte[] Bytes)> segments = [
            (BlockSize, geometry),
            (BlockSize * 2L, geometry)
        ];
        for (uint slot = 0; slot < metadata.Geometry.MetadataSlotCount; slot++)
        {
            segments.Add((MetadataReader.GetPrimaryMetadataOffset(metadata.Geometry, slot), paddedTable));
            segments.Add((MetadataReader.GetBackupMetadataOffset(metadata.Geometry, (ulong)prepared.DeviceSize, slot), paddedTable));
        }

        var sparse = new SparseFile(BlockSize, prepared.DeviceSize);
        try
        {
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
            return sparse;
        }
        catch
        {
            sparse.Dispose();
            throw;
        }
    }
}