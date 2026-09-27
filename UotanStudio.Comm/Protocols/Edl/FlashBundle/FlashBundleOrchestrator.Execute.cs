using System.Buffers.Binary;
using System.Diagnostics;
using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Flash-bundle execution stage: drives program, erase, patch, wipe and UFS provisioning commands for the preflighted manifests through the Firehose protocol owned by the session.
/// <para>刷写包执行阶段：借助会话持有的 Firehose 协议，为已通过预检的清单驱动 program、erase、patch、wipe 与 UFS provisioning 命令。</para>
/// </summary>
public sealed partial class FlashBundleOrchestrator
{
    private async Task<bool> ExecuteBundleAsync(
        FlashBundleOptions options, IProgress<FlashBundleProgress>? progress, CancellationToken cancellationToken,
        FlashBundleResult result, FirehoseProtocol firehose, List<RawProgramEntry> filteredPrograms,
        List<PatchEntry>? filteredPatches, UfsProvisionPlan? ufsProvision, FlashBundleSources? sources, long sectorSize)
    {
        var touchedPartitions = new HashSet<int>();

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
                    var eraseStarted = Stopwatch.GetTimestamp();
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
                                    $"Erase failed for '{displayName}'. Response: {response.RawValue} 擦除失败。")
                                {
                                    FailureKind = EdlFailureKind.Rejected
                                };
                            }

                            touchedPartitions.Add(entry.PhysicalPartitionNumber);
                        }

                        result.EraseEntriesProcessed++;
                        RecordItem(result, FlashBundleItemKind.Erase, entry, sectorSize,
                            CommandOutcomeKind.Completed, firehose, eraseStarted);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        RecordFailureItem(result, FlashBundleItemKind.Erase, entry, sectorSize, firehose, ex, eraseStarted);
                        result.FailureKind ??= FailureKindOf(ex, firehose);
                        result.RequiresReconnect = firehose.RequiresReconnect;
                        result.Errors.Add($"Erase failed for '{displayName}': {ex.Message} 擦除失败。");
                        result.ProgramEntriesSkipped++;
                        RaiseProgress(progress, FlashBundlePhase.Error, ex.Message, i, total);
                        return false;
                    }
                    catch (OperationCanceledException ex)
                    {
                        RecordFailureItem(result, FlashBundleItemKind.Erase, entry, sectorSize, firehose, ex, eraseStarted);
                        throw;
                    }

                    continue;
                }

                RaiseProgress(progress, FlashBundlePhase.Programming, $"Programming {displayName}... 编程 {displayName}...", i, total);
                var programStarted = Stopwatch.GetTimestamp();
                long itemAcked = 0;
                try
                {
                    if (options.DryRun)
                    {
                        result.ProgramEntriesSkipped++;
                        continue;
                    }

                    var source = sources?.Get(entry) ?? throw new InvalidOperationException("The bundle source was not retained.");
                    await FlashProgramEntryAsync(entry, source, sectorSize,
                        bytes =>
                        {
                            itemAcked = checked(itemAcked + bytes);
                            result.TotalBytesWritten = checked(result.TotalBytesWritten + bytes);
                        }, cancellationToken).ConfigureAwait(false);
                    result.ProgramEntriesProcessed++;
                    touchedPartitions.Add(entry.PhysicalPartitionNumber);
                    RecordItem(result, FlashBundleItemKind.Program, entry, sectorSize, CommandOutcomeKind.Completed,
                        firehose, programStarted, itemAcked);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RecordFailureItem(result, FlashBundleItemKind.Program, entry, sectorSize, firehose, ex, programStarted, itemAcked);
                    result.FailureKind ??= FailureKindOf(ex, firehose);
                    result.RequiresReconnect = firehose.RequiresReconnect;
                    result.Errors.Add($"Program failed for '{displayName}': {ex.Message} 编程失败。");
                    result.ProgramEntriesSkipped++;
                    RaiseProgress(progress, FlashBundlePhase.Error, ex.Message, i, total);
                    return false;
                }
                catch (OperationCanceledException ex)
                {
                    RecordFailureItem(result, FlashBundleItemKind.Program, entry, sectorSize, firehose, ex, programStarted, itemAcked);
                    throw;
                }
            }
        }
        else
        {
            result.ProgramEntriesSkipped = filteredPrograms.Count;
        }

        if (!options.SkipPatch && filteredPatches is { Count: > 0 })
        {
            var total = filteredPatches.Count;
            for (var i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = filteredPatches[i];
                var displayName = string.IsNullOrEmpty(entry.Label) ? entry.Filename : entry.Label;

                RaiseProgress(progress, FlashBundlePhase.Patching, $"Patching {displayName}... 修补 {displayName}...", i, total);
                var patchStarted = Stopwatch.GetTimestamp();
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
                                $"Patch failed for '{displayName}'. Response: {response.RawValue} 修补失败。")
                            {
                                FailureKind = EdlFailureKind.Rejected
                            };
                        }
                    }

                    result.PatchEntriesProcessed++;
                    RecordPatchItem(result, entry, sectorSize, CommandOutcomeKind.Completed, firehose, patchStarted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RecordPatchItem(result, entry, sectorSize, OutcomeOf(ex), firehose, ex, patchStarted);
                    result.FailureKind ??= FailureKindOf(ex, firehose);
                    result.RequiresReconnect = firehose.RequiresReconnect;
                    result.Errors.Add($"Patch failed for '{displayName}': {ex.Message} 修补失败。");
                    result.PatchEntriesSkipped++;
                    RaiseProgress(progress, FlashBundlePhase.Error, ex.Message, i, total);
                    return false;
                }
                catch (OperationCanceledException ex)
                {
                    RecordPatchItem(result, entry, sectorSize, OutcomeOf(ex), firehose, ex, patchStarted);
                    throw;
                }
            }
        }

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
                var wipeStarted = Stopwatch.GetTimestamp();
                try
                {
                    if (!options.DryRun)
                    {
                        var response = await firehose.WipeOwnedAsync(
                            0, options.WipeFirstSectors, partition, "Zeros16KB.bin", cancellationToken).ConfigureAwait(false);
                        if (!response.IsAck)
                        {
                            throw new EdlException(EdlErrorCode.ProgramFailed,
                                $"Wipe failed for partition {partition}. Response: {response.RawValue} 擦写失败。")
                            {
                                FailureKind = EdlFailureKind.Rejected
                            };
                        }
                    }

                    result.WipeEntriesProcessed++;
                    result.TotalBytesWritten = checked(result.TotalBytesWritten + checked(options.WipeFirstSectors * sectorSize));
                    RecordWipeItem(result, partition, options.WipeFirstSectors, sectorSize, CommandOutcomeKind.Completed,
                        firehose, wipeStarted, options.Slot);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RecordWipeItem(result, partition, options.WipeFirstSectors, sectorSize, OutcomeOf(ex), firehose, ex, wipeStarted, options.Slot);
                    result.FailureKind ??= FailureKindOf(ex, firehose);
                    result.RequiresReconnect = firehose.RequiresReconnect;
                    result.Errors.Add($"Wipe failed for partition {partition}: {ex.Message} 擦写失败。");
                    RaiseProgress(progress, FlashBundlePhase.Error, ex.Message, i, targets.Length);
                    return false;
                }
                catch (OperationCanceledException ex)
                {
                    RecordWipeItem(result, partition, options.WipeFirstSectors, sectorSize, OutcomeOf(ex), firehose, ex, wipeStarted, options.Slot);
                    throw;
                }
            }
        }

        if (ufsProvision != null)
        {
            RaiseProgress(progress, FlashBundlePhase.UfsProvisioning, "Provisioning UFS... 配置 UFS...", 0, 1);
            var ufsStarted = Stopwatch.GetTimestamp();
            try
            {
                if (!options.DryRun)
                {
                    var response = await firehose.ProvisionUfsOwnedAsync(ufsProvision, cancellationToken).ConfigureAwait(false);
                    if (!response.IsAck)
                    {
                        throw new EdlException(EdlErrorCode.UfsProvisioningFailed,
                            $"UFS provisioning failed. Response: {response.RawValue} UFS 配置失败。")
                        {
                            FailureKind = EdlFailureKind.Rejected
                        };
                    }
                }

                result.UfsProvisioned = true;
                result.Record(new FlashItemEvidence
                {
                    Kind = FlashBundleItemKind.UfsProvision,
                    Outcome = CommandOutcomeKind.Completed,
                    RequiresReconnect = firehose.RequiresReconnect,
                    ElapsedMs = (long)Stopwatch.GetElapsedTime(ufsStarted).TotalMilliseconds
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Record(new FlashItemEvidence
                {
                    Kind = FlashBundleItemKind.UfsProvision,
                    Outcome = OutcomeOf(ex),
                    FailureKind = FailureKindOf(ex, firehose),
                    RequiresReconnect = firehose.RequiresReconnect,
                    ElapsedMs = (long)Stopwatch.GetElapsedTime(ufsStarted).TotalMilliseconds
                });
                result.FailureKind ??= FailureKindOf(ex, firehose);
                result.RequiresReconnect = firehose.RequiresReconnect;
                result.Errors.Add($"UFS provisioning failed: {ex.Message} UFS 配置失败。");
                RaiseProgress(progress, FlashBundlePhase.Error, ex.Message, 0, 1);
                return false;
            }
            catch (OperationCanceledException ex)
            {
                result.Record(new FlashItemEvidence
                {
                    Kind = FlashBundleItemKind.UfsProvision,
                    Outcome = CommandOutcomeKind.Cancelled,
                    FailureKind = FailureKindOf(ex, firehose),
                    RequiresReconnect = firehose.RequiresReconnect,
                    ElapsedMs = (long)Stopwatch.GetElapsedTime(ufsStarted).TotalMilliseconds
                });
                throw;
            }
        }
        return true;
    }

    private static CommandOutcomeKind OutcomeOf(Exception error) => error switch
    {
        OperationCanceledException => CommandOutcomeKind.Cancelled,
        TimeoutException => CommandOutcomeKind.Timeout,
        EdlException { FailureKind: EdlFailureKind.CompletionLost or EdlFailureKind.ExecutionUnknown } => CommandOutcomeKind.Unknown,
        EdlException { ErrorCode: EdlErrorCode.ConnectionLost } => CommandOutcomeKind.ConnectionLost,
        EdlException { ErrorCode: EdlErrorCode.ReadPortTimeout or EdlErrorCode.WritePortTimeout or EdlErrorCode.TransportTimeout } => CommandOutcomeKind.Timeout,
        EdlException => CommandOutcomeKind.Rejected,
        _ => CommandOutcomeKind.Rejected
    };

    private static EdlFailureKind FailureKindOf(Exception error, FirehoseProtocol firehose)
    {
        var classified = EdlFailureClassification.Classify(error);
        return classified == EdlFailureKind.Cancelled && firehose.LastFailureKind is not EdlFailureKind.None
            ? firehose.LastFailureKind
            : classified;
    }

    private static void RecordItem(FlashBundleResult result, FlashBundleItemKind kind, RawProgramEntry entry,
        long sectorSize, CommandOutcomeKind outcome, FirehoseProtocol firehose, long startedTimestamp, long ackedBytes = 0)
    {
        result.Record(new FlashItemEvidence
        {
            Kind = kind,
            FileName = entry.Filename,
            Label = entry.Label,
            Range = TryProgramRange(entry, sectorSize),
            Outcome = outcome,
            RequiresReconnect = firehose.RequiresReconnect,
            AcknowledgedBytes = ackedBytes,
            ElapsedMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds
        });
    }

    private static void RecordFailureItem(FlashBundleResult result, FlashBundleItemKind kind, RawProgramEntry entry,
        long sectorSize, FirehoseProtocol firehose, Exception error, long startedTimestamp, long ackedBytes = 0)
    {
        result.Record(new FlashItemEvidence
        {
            Kind = kind,
            FileName = entry.Filename,
            Label = entry.Label,
            Range = TryProgramRange(entry, sectorSize),
            Outcome = OutcomeOf(error),
            FailureKind = FailureKindOf(error, firehose),
            RequiresReconnect = firehose.RequiresReconnect,
            AcknowledgedBytes = ackedBytes,
            ElapsedMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds
        });
    }

    private static void RecordPatchItem(FlashBundleResult result, PatchEntry entry, long sectorSize,
        CommandOutcomeKind outcome, FirehoseProtocol firehose, long startedTimestamp)
    {
        result.Record(new FlashItemEvidence
        {
            Kind = FlashBundleItemKind.Patch,
            FileName = entry.Filename,
            Label = entry.Label,
            Range = TryPatchRange(entry, sectorSize),
            Outcome = outcome,
            RequiresReconnect = firehose.RequiresReconnect,
            ElapsedMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds
        });
    }

    private static void RecordPatchItem(FlashBundleResult result, PatchEntry entry, long sectorSize,
        CommandOutcomeKind outcome, FirehoseProtocol firehose, Exception error, long startedTimestamp)
    {
        result.Record(new FlashItemEvidence
        {
            Kind = FlashBundleItemKind.Patch,
            FileName = entry.Filename,
            Label = entry.Label,
            Range = TryPatchRange(entry, sectorSize),
            Outcome = outcome,
            FailureKind = FailureKindOf(error, firehose),
            RequiresReconnect = firehose.RequiresReconnect,
            ElapsedMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds
        });
    }

    private static void RecordWipeItem(FlashBundleResult result, int partition, long sectors, long sectorSize,
        CommandOutcomeKind outcome, FirehoseProtocol firehose, long startedTimestamp, int? slot)
    {
        result.Record(new FlashItemEvidence
        {
            Kind = FlashBundleItemKind.Wipe,
            Range = new EdlPhysicalRange(0, sectors, partition, sectorSize, StorageSlot: slot, StartSectorExpression: "0"),
            Outcome = outcome,
            RequiresReconnect = firehose.RequiresReconnect,
            AcknowledgedBytes = outcome == CommandOutcomeKind.Completed ? checked(sectors * sectorSize) : 0,
            ElapsedMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds
        });
    }

    private static void RecordWipeItem(FlashBundleResult result, int partition, long sectors, long sectorSize,
        CommandOutcomeKind outcome, FirehoseProtocol firehose, Exception error, long startedTimestamp, int? slot)
    {
        result.Record(new FlashItemEvidence
        {
            Kind = FlashBundleItemKind.Wipe,
            Range = Try(() => new EdlPhysicalRange(0, sectors, partition, sectorSize, StorageSlot: slot, StartSectorExpression: "0")),
            Outcome = outcome,
            FailureKind = FailureKindOf(error, firehose),
            RequiresReconnect = firehose.RequiresReconnect,
            ElapsedMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds
        });
    }

    private static EdlPhysicalRange? TryProgramRange(RawProgramEntry entry, long sectorSize) => Try(() =>
        EdlPhysicalRange.Parse(entry.StartSector, entry.NumPartitionSectors > 0 ? entry.NumPartitionSectors : 1,
            entry.PhysicalPartitionNumber, sectorSize, entry.Slot));

    private static EdlPhysicalRange? TryPatchRange(PatchEntry entry, long sectorSize) => Try(() =>
        EdlPhysicalRange.Parse(entry.StartSector, 1, entry.PhysicalPartitionNumber, sectorSize, entry.Slot));

    private static EdlPhysicalRange? Try(Func<EdlPhysicalRange> build)
    {
        try
        {
            return build();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
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