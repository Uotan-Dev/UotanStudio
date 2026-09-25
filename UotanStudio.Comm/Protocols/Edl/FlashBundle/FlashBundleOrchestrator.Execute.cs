using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

public sealed partial class FlashBundleOrchestrator
{
    private async Task<bool> ExecuteBundleAsync(
        FlashBundleOptions options, IProgress<FlashBundleProgress>? progress, CancellationToken cancellationToken,
        FlashBundleResult result, FirehoseProtocol firehose, List<RawProgramEntry> filteredPrograms,
        List<PatchEntry>? filteredPatches, UfsProvisionPlan? ufsProvision, FlashBundleSources? sources, long sectorSize)
    {
        var touchedPartitions = new HashSet<int>();

        // ---- Program / Erase phase ---------------------------------------------
        if (!options.SkipProgram)
        {
            var total = filteredPrograms.Count;
            for (var i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = filteredPrograms[i];
                var displayName = string.IsNullOrEmpty(entry.Label) ? entry.Filename : entry.Label;

                if (string.IsNullOrEmpty(entry.Filename))
                {
                    if (entry.NumPartitionSectors <= 0)
                    {
                        result.ProgramEntriesSkipped++;
                        continue;
                    }

                    RaiseProgress(progress, FlashBundlePhase.Erasing, $"Erasing {displayName}... 擦除 {displayName}...", i, total);
                    try
                    {
                        if (!options.DryRun)
                        {
                            var response = await firehose.EraseOwnedAsync(
                                FirehoseSectorAddress.Parse(entry.StartSector),
                                entry.NumPartitionSectors,
                                entry.PhysicalPartitionNumber,
                                cancellationToken).ConfigureAwait(false);
                            if (!response.IsAck)
                            {
                                throw new EdlException(EdlErrorCode.EraseFailed,
                                    $"Erase failed for '{displayName}'. Response: {response.RawValue} 擦除失败。");
                            }

                            touchedPartitions.Add(entry.PhysicalPartitionNumber);
                        }

                        result.EraseEntriesProcessed++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result.Errors.Add($"Erase failed for '{displayName}': {ex.Message} 擦除失败。");
                        result.ProgramEntriesSkipped++;
                        RaiseProgress(progress, FlashBundlePhase.Error, ex.Message, i, total);
                        return false;
                    }

                    continue;
                }

                RaiseProgress(progress, FlashBundlePhase.Programming, $"Programming {displayName}... 编程 {displayName}...", i, total);
                try
                {
                    if (options.DryRun)
                    {
                        result.ProgramEntriesSkipped++;
                        continue;
                    }

                    var source = sources?.Get(entry) ?? throw new InvalidOperationException("The bundle source was not retained.");
                    await FlashProgramEntryAsync(entry, source, sectorSize,
                        bytes => result.TotalBytesWritten = checked(result.TotalBytesWritten + bytes), cancellationToken).ConfigureAwait(false);
                    result.ProgramEntriesProcessed++;
                    touchedPartitions.Add(entry.PhysicalPartitionNumber);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Errors.Add($"Program failed for '{displayName}': {ex.Message} 编程失败。");
                    result.ProgramEntriesSkipped++;
                    RaiseProgress(progress, FlashBundlePhase.Error, ex.Message, i, total);
                    return false;
                }
            }
        }
        else
        {
            result.ProgramEntriesSkipped = filteredPrograms.Count;
        }

        // ---- Patch phase --------------------------------------------------------
        if (!options.SkipPatch && filteredPatches is { Count: > 0 })
        {
            var total = filteredPatches.Count;
            for (var i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = filteredPatches[i];
                var displayName = string.IsNullOrEmpty(entry.Label) ? entry.Filename : entry.Label;

                RaiseProgress(progress, FlashBundlePhase.Patching, $"Patching {displayName}... 修补 {displayName}...", i, total);
                try
                {
                    if (!options.DryRun)
                    {
                        var response = await firehose.PatchOwnedAsync(
                            entry.Filename,
                            entry.PhysicalPartitionNumber,
                            entry.StartSector,
                            entry.ByteOffset,
                            entry.Value,
                            entry.SizeInBytes,
                            cancellationToken).ConfigureAwait(false);
                        if (!response.IsAck)
                        {
                            throw new EdlException(EdlErrorCode.ProgramFailed,
                                $"Patch failed for '{displayName}'. Response: {response.RawValue} 修补失败。");
                        }
                    }

                    result.PatchEntriesProcessed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Errors.Add($"Patch failed for '{displayName}': {ex.Message} 修补失败。");
                    result.PatchEntriesSkipped++;
                    RaiseProgress(progress, FlashBundlePhase.Error, ex.Message, i, total);
                    return false;
                }
            }
        }

        // ---- Wipe phase (program-zeros over the first N sectors) -----------------
        if (!options.SkipWipe && options.WipeFirstSectors > 0 && touchedPartitions.Count > 0)
        {
            var targets = options.WipePhysicalPartitions is { Length: > 0 }
                ? options.WipePhysicalPartitions.Distinct().Order().ToArray()
                : [.. touchedPartitions.Order()];

            for (var i = 0; i < targets.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var partition = targets[i];
                RaiseProgress(progress, FlashBundlePhase.Wiping,
                    $"Wiping first {options.WipeFirstSectors} sectors of partition {partition}... 擦写分区 {partition} 前 {options.WipeFirstSectors} 个扇区...", i, targets.Length);
                try
                {
                    if (!options.DryRun)
                    {
                        var response = await firehose.WipeOwnedAsync(
                            0, options.WipeFirstSectors, partition, "Zeros16KB.bin", cancellationToken).ConfigureAwait(false);
                        if (!response.IsAck)
                        {
                            throw new EdlException(EdlErrorCode.ProgramFailed,
                                $"Wipe failed for partition {partition}. Response: {response.RawValue} 擦写失败。");
                        }
                    }

                    result.WipeEntriesProcessed++;
                    result.TotalBytesWritten = checked(result.TotalBytesWritten + checked(options.WipeFirstSectors * sectorSize));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Errors.Add($"Wipe failed for partition {partition}: {ex.Message} 擦写失败。");
                    RaiseProgress(progress, FlashBundlePhase.Error, ex.Message, i, targets.Length);
                    return false;
                }
            }
        }

        // ---- UFS provisioning phase ----------------------------------------------
        if (ufsProvision != null)
        {
            RaiseProgress(progress, FlashBundlePhase.UfsProvisioning, "Provisioning UFS... 配置 UFS...", 0, 1);
            try
            {
                if (!options.DryRun)
                {
                    var response = await firehose.ProvisionUfsOwnedAsync(ufsProvision, cancellationToken).ConfigureAwait(false);
                    if (!response.IsAck)
                    {
                        throw new EdlException(EdlErrorCode.UfsProvisioningFailed,
                            $"UFS provisioning failed. Response: {response.RawValue} UFS 配置失败。");
                    }
                }

                result.UfsProvisioned = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add($"UFS provisioning failed: {ex.Message} UFS 配置失败。");
                RaiseProgress(progress, FlashBundlePhase.Error, ex.Message, 0, 1);
                return false;
            }
        }
        return true;
    }

    private async Task FlashProgramEntryAsync(
        RawProgramEntry entry, Stream stream, long sectorSize, Action<long> rangeCompleted, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(entry.SourceFile))
        {
            throw new EdlException(EdlErrorCode.InvalidParameter,
                $"No source file for program entry '{entry.Label ?? entry.Filename}'. program 条目缺少源文件。");
        }

        var startSector = FirehoseSectorAddress.Parse(entry.StartSector);

        stream.Position = 0;
        if (entry.IsSparse || IsAndroidSparse(stream))
        {
            stream.Position = 0;
            if (entry.FileSectorOffset != 0)
            {
                throw new EdlException(EdlErrorCode.InvalidParameter, "Sparse program entries cannot use a raw source-sector offset.");
            }

            var response = await RequireFirehose().ProgramSparseOwnedAsync(
                stream, startSector, entry.Filename, entry.PhysicalPartitionNumber,
                entry.NumPartitionSectors > 0 ? entry.NumPartitionSectors : null, cancellationToken, rangeCompleted).ConfigureAwait(false);
            if (!response.IsAck)
            {
                throw new EdlException(EdlErrorCode.ProgramFailed,
                    $"Sparse flash failed for '{entry.Label ?? entry.Filename}'. Response: {response.RawValue} 稀疏刷写失败。");
            }
            return;
        }

        var sourceOffset = checked(entry.FileSectorOffset * sectorSize);
        if (sourceOffset < 0 || sourceOffset >= stream.Length)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "The source-sector offset is outside the image.");
        }

        stream.Position = sourceOffset;
        var lengthFromOffset = stream.Length - stream.Position;
        var numSectors = entry.NumPartitionSectors > 0
            ? entry.NumPartitionSectors
            : (lengthFromOffset / sectorSize) + (lengthFromOffset % sectorSize != 0 ? 1 : 0);

        if (numSectors <= 0)
        {
            return;
        }

        var responseRaw = await RequireFirehose().ProgramCoreAsync(
            stream, startSector, numSectors, entry.Filename, entry.PhysicalPartitionNumber, cancellationToken).ConfigureAwait(false);
        if (!responseRaw.IsAck)
        {
            throw new EdlException(EdlErrorCode.ProgramFailed,
                $"Flash failed for '{entry.Label ?? entry.Filename}'. Response: {responseRaw.RawValue} 刷写失败。");
        }
        rangeCompleted(checked(numSectors * sectorSize));
    }

    private static bool IsAndroidSparse(Stream stream)
    {
        var position = stream.Position;
        try
        {
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(magic)
                == UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.SparseFormat.SparseHeaderMagic;
        }
        finally
        {
            stream.Position = position;
        }
    }
}