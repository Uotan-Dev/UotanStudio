using UotanStudio.Firmware.CompoundFile;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// In-place patcher for Qualcomm QCN NV backup files. Every NV record is a fixed 136 bytes, so only the 128-byte data region of changed records is rewritten at offsets derived from the FAT chains. The patch is staged to a temporary file, re-parsed, then atomically published.
/// <para>Qualcomm QCN NV 备份文件的原位补丁器。每条 NV 记录定长 136 字节，故仅重写被修改记录的 128 字节数据区（偏移由 FAT 链换算）。补丁先写入临时文件、重新解析校验，再原子发布。</para>
/// </summary>
public static class QcnWriter
{
    /// <summary>
    /// Patches the NV records of a QCN file with the data held by the supplied items.
    /// <para>用给定条目持有的数据修补 QCN 文件的 NV 记录。</para>
    /// </summary>
    /// <param name="path">
    /// The QCN file path to patch.
    /// <para>要补丁的 QCN 文件路径。</para>
    /// </param>
    /// <param name="items">
    /// The complete item set originating from this file (e.g. an edited QcnReader.ParseAsync result).
    /// <para>来自该文件的完整条目集合（可为编辑后的 QcnReader.ParseAsync 结果）。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token carried through verification, the temporary file write and publication.
    /// <para>取消令牌，贯穿校验、临时文件写入与发布。</para>
    /// </param>
    /// <returns>
    /// The number of data regions actually rewritten (0 when the content already matches and the file is untouched).
    /// <para>实际重写的数据区数量（0 表示内容已一致，文件未改动）。</para>
    /// </returns>
    /// <exception cref="QcnPatchMismatchException">
    /// The item set does not match the file (record count, stream paths, indices, ids or flags differ).
    /// <para>条目集合与文件不匹配（记录数、流路径、索引、id 或 flags 不一致）。</para>
    /// </exception>
    /// <exception cref="QcnException">
    /// Post-patch verification failed; the source file is left untouched.
    /// <para>补丁后的校验失败；源文件保持原样。</para>
    /// </exception>
    /// <exception cref="CompoundFileException">
    /// The container structure is corrupted or unsupported.
    /// <para>容器结构损坏或不受支持。</para>
    /// </exception>
    public static async Task<int> PatchAsync(string path, IReadOnlyList<QcnNvItem> items, CancellationToken cancellationToken = default)
    {
        List<PatchedRecordLayout> layout;
        {
            using var reader = await CompoundFileReader.OpenAsync(path, cancellationToken).ConfigureAwait(false);
            layout = await BuildLayoutAsync(reader, items, cancellationToken).ConfigureAwait(false);
        }

        var patches = 0;
        foreach (var record in layout)
        {
            var item = items[record.Ordinal];
            if (!record.OriginalData.AsSpan().SequenceEqual(item.Data))
            {
                patches++;
            }
        }

        if (patches == 0)
        {
            return 0;
        }

        var stagedPath = TempFileHelper.CreateAdjacentPath(path);
        try
        {
            File.Copy(path, stagedPath, overwrite: true);
            await using (var staged = new FileStream(stagedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                foreach (var record in layout)
                {
                    var item = items[record.Ordinal];
                    if (record.OriginalData.AsSpan().SequenceEqual(item.Data))
                    {
                        continue;
                    }

                    await WriteLogicalRangeAsync(staged, record.DataBlockOffsets, record.BlockSize, record.DataLogicalOffset, item.Data, cancellationToken).ConfigureAwait(false);
                }

                await staged.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await VerifyAsync(stagedPath, items, cancellationToken).ConfigureAwait(false);
            File.Move(stagedPath, path, overwrite: true);
            return patches;
        }
        catch
        {
            TempFileHelper.SafeDelete(stagedPath);
            throw;
        }
    }

    private static async Task<List<PatchedRecordLayout>> BuildLayoutAsync(CompoundFileReader reader, IReadOnlyList<QcnNvItem> items, CancellationToken cancellationToken)
    {
        var parsed = await QcnReader.ParseAsync(reader, cancellationToken).ConfigureAwait(false);
        if (parsed.Items.Count != items.Count)
        {
            throw new QcnPatchMismatchException($"The file carries {parsed.Items.Count} NV records but {items.Count} items were supplied.");
        }

        var layout = new List<PatchedRecordLayout>(items.Count);
        foreach (var entry in reader.Entries)
        {
            if (entry.Type != CompoundFileEntryType.Stream || !entry.Name.Equals("NV_ITEM_ARRAY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var streamLayout = reader.GetStreamBlockLayout(entry);
            var blockOffsets = streamLayout.BlockOffsets;
            var records = parsed.Items.Where(item => item.SourceStreamPath == entry.FullPath).ToList();
            for (var i = 0; i < records.Count; i++)
            {
                var item = items[records[i].Ordinal];
                if (item.SourceStreamPath != records[i].SourceStreamPath
                    || item.RecordIndex != records[i].RecordIndex
                    || item.Id != records[i].Id
                    || item.Flags != records[i].Flags)
                {
                    throw new QcnPatchMismatchException($"Item {i} (stream '{item.SourceStreamPath}', record {item.RecordIndex}, id {item.Id}) does not match record {i} of the file (stream '{records[i].SourceStreamPath}', record {records[i].RecordIndex}, id {records[i].Id}).");
                }

                layout.Add(new PatchedRecordLayout(records[i].Ordinal, records[i].Data, (long)records[i].RecordIndex * QcnNvItem.RecordLength + 8, blockOffsets, streamLayout.BlockSize));
            }
        }

        return layout;
    }

    private static async Task VerifyAsync(string stagedPath, IReadOnlyList<QcnNvItem> items, CancellationToken cancellationToken)
    {
        using var reader = await CompoundFileReader.OpenAsync(stagedPath, cancellationToken).ConfigureAwait(false);
        var parsed = await QcnReader.ParseAsync(reader, cancellationToken).ConfigureAwait(false);
        if (parsed.Items.Count != items.Count)
        {
            throw new QcnException($"Post-patch verification failed: the staged file carries {parsed.Items.Count} records instead of {items.Count}.");
        }

        for (var i = 0; i < items.Count; i++)
        {
            var record = parsed.Items[i];
            var item = items[i];
            if (record.Id != item.Id || record.Flags != item.Flags || !record.Data.AsSpan().SequenceEqual(item.Data))
            {
                throw new QcnException($"Post-patch verification failed at record {i} (id {record.Id}).");
            }
        }
    }

    private static async Task WriteLogicalRangeAsync(FileStream stream, long[] blockOffsets, long blockSize, long logicalOffset, byte[] data, CancellationToken cancellationToken)
    {
        var written = 0;
        while (written < data.Length)
        {
            var blockIndex = (int)((logicalOffset + written) / blockSize);
            var within = (logicalOffset + written) % blockSize;
            var chunk = (int)Math.Min(blockSize - within, data.Length - written);
            if (blockIndex >= blockOffsets.Length)
            {
                throw new QcnException($"Record data at logical offset {logicalOffset} extends beyond the stream's physical block map.");
            }

            stream.Seek(blockOffsets[blockIndex] + within, SeekOrigin.Begin);
            await stream.WriteAsync(data.AsMemory(written, chunk), cancellationToken).ConfigureAwait(false);
            written += chunk;
        }
    }

    private sealed record PatchedRecordLayout(int Ordinal, byte[] OriginalData, long DataLogicalOffset, long[] DataBlockOffsets, long BlockSize);
}