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
        List<PatchEntry>? filteredPatches, List<WipeManifestEntry> filteredWipes,
        UfsProvisionPlan? ufsProvision, FlashBundleSources? sources, long sectorSize)
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
                        if (!options.ContinueOnFailure || firehose.RequiresReconnect)
                        {
                            return false;
                        }
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
                    var programXml = await FlashProgramEntryAsync(entry, source, sectorSize, result,
                        bytes =>
                        {
                            itemAcked = checked(itemAcked + bytes);
                            result.TotalBytesWritten = checked(result.TotalBytesWritten + bytes);
                        }, cancellationToken).ConfigureAwait(false);
                    if (options.Verify)
                    {
                        await VerifyProgramEntryAsync(options, progress, firehose, entry, source, sectorSize,
                            displayName, i, total, result, cancellationToken).ConfigureAwait(false);
                    }

                    result.ProgramEntriesProcessed++;
                    touchedPartitions.Add(entry.PhysicalPartitionNumber);
                    RecordItem(result, FlashBundleItemKind.Program, entry, sectorSize, CommandOutcomeKind.Completed,
                        firehose, programStarted, itemAcked, programXml);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RecordFailureItem(result, FlashBundleItemKind.Program, entry, sectorSize, firehose, ex, programStarted, itemAcked);
                    result.FailureKind ??= FailureKindOf(ex, firehose);
                    result.RequiresReconnect = firehose.RequiresReconnect;
                    result.Errors.Add($"Program failed for '{displayName}': {ex.Message} 编程失败。");
                    result.ProgramEntriesSkipped++;
                    RaiseProgress(progress, FlashBundlePhase.Error, ex.Message, i, total);
                    if (!options.ContinueOnFailure || firehose.RequiresReconnect)
                    {
                        return false;
                    }
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
                    if (!options.ContinueOnFailure || firehose.RequiresReconnect)
                    {
                        return false;
                    }
                }
                catch (OperationCanceledException ex)
                {
                    RecordPatchItem(result, entry, sectorSize, OutcomeOf(ex), firehose, ex, patchStarted);
                    throw;
                }
            }
        }

        if (!options.SkipWipe && filteredWipes.Count > 0)
        {
            var total = filteredWipes.Count;
            for (var i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = filteredWipes[i];
                var displayName = string.IsNullOrEmpty(entry.Label)
                    ? $"partition {entry.PhysicalPartitionNumber}"
                    : entry.Label;

                RaiseProgress(progress, FlashBundlePhase.Wiping,
                    $"Wiping {displayName}... 擦写 {displayName}...", i, total);
                var wipeStarted = Stopwatch.GetTimestamp();
                try
                {
                    var geometry = await ResolveWipeGeometryAsync(firehose, entry, cancellationToken).ConfigureAwait(false);
                    var authorized = FlashBundleWipeCommand.TryPrepare(entry, options, geometry, out var built, out var refusal);
                    if (!authorized || built is null)
                    {
                        result.RecordWipePreview(new FlashWipePreview
                        {
                            Name = displayName,
                            PhysicalPartitionNumber = entry.PhysicalPartitionNumber,
                            RefusalReason = refusal ?? FlashBundleWipeCommand.CommandBuildReason
                        });
                        throw new EdlException(EdlErrorCode.InvalidParameter,
                            $"Wipe refused for '{displayName}': {refusal ?? FlashBundleWipeCommand.CommandBuildReason}")
                        {
                            FailureKind = EdlFailureKind.Rejected
                        };
                    }

                    var command = built;
                    result.RecordWipePreview(new FlashWipePreview
                    {
                        Name = displayName,
                        PhysicalPartitionNumber = entry.PhysicalPartitionNumber,
                        StartSector = command.StartSector,
                        SectorCount = command.SectorCount,
                        BytesToOverwrite = command.ByteCount,
                        CommandXml = command.CommandXml
                    });
                    if (!options.DryRun)
                    {
                        var response = await firehose.WipeOwnedAsync(command.StartSector, command.SectorCount,
                            command.PhysicalPartitionNumber, command.ZeroFilename, cancellationToken).ConfigureAwait(false);
                        if (!response.IsAck)
                        {
                            throw new EdlException(EdlErrorCode.ProgramFailed,
                                $"Wipe failed for '{displayName}'. Response: {response.RawValue} 擦写失败。")
                            {
                                FailureKind = EdlFailureKind.Rejected
                            };
                        }

                        result.WipeEntriesProcessed++;
                        result.TotalBytesWritten = checked(result.TotalBytesWritten + command.ByteCount);
                    }

                    RecordManifestWipeItem(result, displayName, command, CommandOutcomeKind.Completed, firehose, wipeStarted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RecordManifestWipeFailure(result, displayName, entry, firehose, ex, wipeStarted);
                    result.FailureKind ??= FailureKindOf(ex, firehose);
                    result.RequiresReconnect = firehose.RequiresReconnect;
                    result.Errors.Add($"Wipe failed for '{displayName}': {ex.Message} 擦写失败。");
                    RaiseProgress(progress, FlashBundlePhase.Error, ex.Message, i, total);
                    if (!options.ContinueOnFailure || firehose.RequiresReconnect)
                    {
                        return false;
                    }
                }
                catch (OperationCanceledException ex)
                {
                    RecordManifestWipeFailure(result, displayName, entry, firehose, ex, wipeStarted);
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
                    if (!options.ContinueOnFailure || firehose.RequiresReconnect)
                    {
                        return false;
                    }
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
                if (!options.ContinueOnFailure || firehose.RequiresReconnect)
                {
                    return false;
                }
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
        long sectorSize, CommandOutcomeKind outcome, FirehoseProtocol firehose, long startedTimestamp, long ackedBytes = 0,
        string? commandXml = null)
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
            CommandXml = commandXml,
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

    /// <summary>
    /// Records one manifest wipe request that reached its terminal observation, with the command document the
    /// gated builder produced for it so the emitted wipe stays inspectable in the evidence.
    /// <para>记录一条到达终态观测的清单擦写请求，并附带受门控构建器为其生成的命令文档，使发出的擦写命令
    /// 在证据中仍可检视。</para>
    /// </summary>
    /// <param name="result">The result collecting the evidence item. <para>收集证据条目的结果对象。</para></param>
    /// <param name="displayName">The label or partition name shown to the caller.
    /// <para>向调用方展示的标签或分区名称。</para></param>
    /// <param name="command">The gated wipe command. <para>受门控的擦写命令。</para></param>
    /// <param name="outcome">The terminal observation. <para>终态观测。</para></param>
    /// <param name="firehose">The protocol supplying the reconnect flag.
    /// <para>提供重连标记的协议。</para></param>
    /// <param name="startedTimestamp">The stopwatch timestamp taken before the attempt.
    /// <para>尝试前取得的计时时间戳。</para></param>
    private static void RecordManifestWipeItem(FlashBundleResult result, string displayName,
        FlashBundleWipeCommand command, CommandOutcomeKind outcome, FirehoseProtocol firehose, long startedTimestamp)
    {
        result.Record(new FlashItemEvidence
        {
            Kind = FlashBundleItemKind.Wipe,
            Label = displayName,
            Range = new EdlPhysicalRange(command.StartSector, command.SectorCount,
                command.PhysicalPartitionNumber, command.SectorSize, StorageSlot: command.Slot),
            Outcome = outcome,
            RequiresReconnect = firehose.RequiresReconnect,
            AcknowledgedBytes = outcome == CommandOutcomeKind.Completed ? command.ByteCount : 0,
            CommandXml = command.CommandXml,
            ElapsedMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds
        });
    }

    /// <summary>
    /// Records a refused or failed manifest wipe request. A refusal carries no command document, because no
    /// document was built and no byte was written.
    /// <para>记录被拒绝或失败的清单擦写请求。拒绝不携带命令文档，因为既未构造文档也未写入任何字节。</para>
    /// </summary>
    /// <param name="result">The result collecting the evidence item. <para>收集证据条目的结果对象。</para></param>
    /// <param name="displayName">The label or partition name shown to the caller.
    /// <para>向调用方展示的标签或分区名称。</para></param>
    /// <param name="entry">The parsed wipe request. <para>已解析的擦写请求。</para></param>
    /// <param name="firehose">The protocol supplying the reconnect flag.
    /// <para>提供重连标记的协议。</para></param>
    /// <param name="error">The refusal or transport error. <para>拒绝或传输错误。</para></param>
    /// <param name="startedTimestamp">The stopwatch timestamp taken before the attempt.
    /// <para>尝试前取得的计时时间戳。</para></param>
    private static void RecordManifestWipeFailure(FlashBundleResult result, string displayName,
        WipeManifestEntry entry, FirehoseProtocol firehose, Exception error, long startedTimestamp)
    {
        result.Record(new FlashItemEvidence
        {
            Kind = FlashBundleItemKind.Wipe,
            Label = displayName,
            Range = TryWipeRange(entry, firehose.Configuration.SectorSize),
            Outcome = OutcomeOf(error),
            FailureKind = FailureKindOf(error, firehose),
            RequiresReconnect = firehose.RequiresReconnect,
            ElapsedMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds
        });
    }

    /// <summary>
    /// Queries the geometry one wipe request needs while the caller holds the operation lease. A partition whose
    /// geometry was never reported yields <see langword="null"/> so the wipe gates refuse it with a stated reason
    /// instead of the bundle inventing a target region.
    /// <para>在调用方持有操作租约时查询单条擦写请求所需的几何信息。从未上报几何信息的分区返回
    /// <see langword="null"/>，由擦写门控以明确理由拒绝，而不是由本包臆造目标区域。</para>
    /// </summary>
    /// <param name="firehose">The protocol holding the lease. <para>持有租约的协议。</para></param>
    /// <param name="entry">The parsed wipe request. <para>已解析的擦写请求。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The reported geometry, or <see langword="null"/> when none was reported.
    /// <para>上报的几何信息；未上报时为 <see langword="null"/>。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
    private static async Task<FirehoseStorageGeometry?> ResolveWipeGeometryAsync(FirehoseProtocol firehose,
        WipeManifestEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            return await firehose.GetStorageGeometryOwnedAsync(entry.PhysicalPartitionNumber, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EdlException)
        {
            return null;
        }
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

    /// <summary>
    /// Records one preview per manifest wipe request without touching the device, so a dry run reports the
    /// region each request would overwrite, or the gate that refused it, before any write happens.
    /// <para>在不接触设备的情况下为每条清单擦写请求记录一条预览，使 dry-run 在任何写入之前就报告各请求
    /// 将要覆盖的区域，或拒绝它的门控。</para>
    /// </summary>
    /// <param name="wipes">The parsed wipe requests in document order.
    /// <para>按文档顺序排列的已解析擦写请求。</para></param>
    /// <param name="options">The bundle policy snapshot supplying the confirmation gates.
    /// <para>提供确认门控的本包策略快照。</para></param>
    /// <param name="result">The result collecting the previews. <para>收集预览的结果对象。</para></param>
    /// <param name="geometries">The reported geometry per partition, or <see langword="null"/> when nothing was
    /// reported. <para>按分区上报的几何信息；未上报任何信息时为 <see langword="null"/>。</para></param>
    private static void RecordWipePreviews(List<WipeManifestEntry> wipes, FlashBundleOptions options,
        FlashBundleResult result, IReadOnlyDictionary<int, FirehoseStorageGeometry>? geometries)
    {
        foreach (var entry in wipes)
        {
            var geometry = geometries is not null && geometries.TryGetValue(entry.PhysicalPartitionNumber, out var reported)
                ? reported
                : null;
            result.RecordWipePreview(FlashBundleWipeCommand.Preview(entry, options, geometry));
        }
    }

    /// <summary>
    /// Collects the geometry each wipe partition reports while the caller holds the operation lease. A partition
    /// that reports none keeps its wipes refused, and the reason the query failed is recorded as a warning so
    /// the refusal is traceable.
    /// <para>在调用方持有操作租约时收集各擦写分区上报的几何信息。未上报的分区其擦写保持被拒绝，且查询
    /// 失败原因会记为警告，使拒绝可追溯。</para>
    /// </summary>
    /// <param name="firehose">The protocol holding the lease. <para>持有租约的协议。</para></param>
    /// <param name="wipes">The parsed wipe requests in document order.
    /// <para>按文档顺序排列的已解析擦写请求。</para></param>
    /// <param name="result">The result collecting the warnings. <para>收集警告的结果对象。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The reported geometry keyed by partition number.
    /// <para>以分区号为键组织的上报几何信息。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled. <para>令牌被取消。</para></exception>
    private static async Task<Dictionary<int, FirehoseStorageGeometry>> CollectWipeGeometriesAsync(
        FirehoseProtocol firehose, List<WipeManifestEntry> wipes, FlashBundleResult result,
        CancellationToken cancellationToken)
    {
        var geometries = new Dictionary<int, FirehoseStorageGeometry>();
        foreach (var partition in wipes.Select(static entry => entry.PhysicalPartitionNumber).Distinct().Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                geometries[partition] = await firehose.GetStorageGeometryOwnedAsync(partition, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (EdlException exception)
            {
                result.Warnings.Add($"No storage geometry was reported for partition {partition}: {exception.Message}");
            }
        }

        return geometries;
    }

    private static EdlPhysicalRange? TryWipeRange(WipeManifestEntry entry, long sectorSize)
    {
        if (entry.EntireStorage)
        {
            return null;
        }

        try
        {
            return entry.ToPlanRange(sectorSize);
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

    private async Task<string?> FlashProgramEntryAsync(
        RawProgramEntry entry, Stream stream, long sectorSize, FlashBundleResult result,
        Action<long> rangeCompleted, CancellationToken cancellationToken)
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
            if (entry.IsDigestBound)
            {
                throw new EdlException(EdlErrorCode.InvalidParameter,
                    $"A digest-bound program entry cannot be sparse because its digest covers one contiguous range: '{entry.Label ?? entry.Filename}'.")
                {
                    FailureKind = EdlFailureKind.Rejected
                };
            }

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
            return null;
        }

        var sourceOffset = checked(entry.FileSectorOffset * sectorSize);
        if (sourceOffset < 0 || sourceOffset >= stream.Length)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "The source-sector offset is outside the image.");
        }

        stream.Position = sourceOffset;
        var available = stream.Length - stream.Position;
        var numSectors = FlashProgramLength.ResolveSectors(available, entry.NumPartitionSectors, sectorSize);

        if (numSectors <= 0)
        {
            return null;
        }

        if (FlashProgramLength.IsTruncatedByDeclaration(available, entry.NumPartitionSectors, sectorSize))
        {
            result.Warnings.Add($"Program entry '{entry.Label ?? entry.Filename}' declares {entry.NumPartitionSectors} sectors but its source needs {(available / sectorSize) + (available % sectorSize == 0 ? 0 : 1)}; the tail is not programmed.");
        }

        var digest = entry.IsDigestBound
            ? await PrepareDigestCommandAsync(entry, stream, sectorSize, numSectors, startSector, cancellationToken).ConfigureAwait(false)
            : null;
        var responseRaw = await RequireFirehose().ProgramCoreAsync(
            stream, startSector, numSectors, entry.Filename, entry.PhysicalPartitionNumber, cancellationToken).ConfigureAwait(false);
        if (!responseRaw.IsAck)
        {
            throw new EdlException(EdlErrorCode.ProgramFailed,
                $"Flash failed for '{entry.Label ?? entry.Filename}'. Response: {responseRaw.RawValue} 刷写失败。");
        }
        rangeCompleted(checked(numSectors * sectorSize));
        if (digest is null)
        {
            return null;
        }

        await VerifyWrittenDigestAsync(digest, startSector.Value, numSectors, entry.PhysicalPartitionNumber,
            entry.Filename, cancellationToken).ConfigureAwait(false);
        return digest.CommandXml;
    }

    /// <summary>
    /// Turns a manifest program entry that declares a write digest into the gated command whose digest covers
    /// exactly the bytes the program path is about to send. Disk-relative addresses and slot values that
    /// contradict the negotiated configuration are refused, because a digest only binds one exact range.
    /// <para>把声明了写入摘要的清单 program 条目转换为受门控的命令，其摘要恰好覆盖编程路径即将发送的
    /// 字节。磁盘相对地址以及与协商配置矛盾的槽位取值会被拒绝，因为摘要只能绑定一个精确范围。</para>
    /// </summary>
    /// <param name="entry">The digest-bound program entry. <para>摘要绑定的 program 条目。</para></param>
    /// <param name="stream">The source positioned at the first programmed byte.
    /// <para>定位在首个待编程字节的源流。</para></param>
    /// <param name="sectorSize">The sector size in effect. <para>当前生效的扇区大小。</para></param>
    /// <param name="numSectors">The resolved sector count. <para>解析出的扇区数。</para></param>
    /// <param name="startSector">The parsed start address. <para>已解析的起始地址。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The prepared digest command. <para>已准备的摘要命令。</para></returns>
    /// <exception cref="EdlException">The request was refused before any byte was written.
    /// <para>在写入任何字节之前本请求被拒绝。</para></exception>
    private async Task<FlashBundleDigestCommand> PrepareDigestCommandAsync(RawProgramEntry entry, Stream stream,
        long sectorSize, long numSectors, FirehoseSectorAddress startSector, CancellationToken cancellationToken)
    {
        var firehose = RequireFirehose();
        if (startSector.RelativeToEnd)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter,
                $"A digest-bound program entry needs an absolute start sector because its digest binds one exact range: '{entry.Label ?? entry.Filename}'.")
            {
                FailureKind = EdlFailureKind.Rejected
            };
        }

        var slot = firehose.Configuration.Slot != 0 ? firehose.Configuration.Slot : (int?)null;
        if (entry.Slot is { } declaredSlot && declaredSlot != 0 && slot is { } configuredSlot
            && declaredSlot != configuredSlot)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter,
                $"The slot declared by digest-bound program entry '{entry.Label ?? entry.Filename}' disagrees with the negotiated slot.")
            {
                FailureKind = EdlFailureKind.Rejected
            };
        }

        long chunkBytes;
        try
        {
            chunkBytes = FlashBundleDigestCommand.EffectiveChunkBytes(firehose.Configuration);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter,
                $"The write chunk of digest-bound program entry '{entry.Label ?? entry.Filename}' is unusable: {exception.Message}")
            {
                FailureKind = EdlFailureKind.Rejected
            };
        }

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(entry, stream, sectorSize,
            numSectors, startSector.Value, chunkBytes, slot, cancellationToken).ConfigureAwait(false);
        if (!prepared || command is null)
        {
            throw new EdlException(EdlErrorCode.InvalidParameter,
                $"Digest-bound program refused for '{entry.Label ?? entry.Filename}': {refusal ?? FlashBundleDigestCommand.RangeReason}")
            {
                FailureKind = EdlFailureKind.Rejected
            };
        }

        return command;
    }

    /// <summary>
    /// Completes the write-digest pairing by asking the target to hash the range that was just programmed and
    /// comparing it with the declared digest, the same query the protocol's digest fast path uses. A target that
    /// reports no digest leaves the write unverified and fails the item rather than passing it silently.
    /// <para>通过请求目标对刚编程的范围计算哈希并与声明摘要比对，完成写入-摘要配对，使用的正是协议摘要
    /// 快速路径的同一查询。目标未上报摘要时，本次写入视为未校验并使该条目失败，而不是静默通过。</para>
    /// </summary>
    /// <param name="digest">The prepared digest command. <para>已准备的摘要命令。</para></param>
    /// <param name="startSector">The absolute starting sector that was programmed.
    /// <para>已编程的绝对起始扇区。</para></param>
    /// <param name="numSectors">The number of programmed sectors. <para>已编程的扇区数。</para></param>
    /// <param name="physicalPartition">The physical partition or LUN. <para>物理分区或 LUN。</para></param>
    /// <param name="displayName">The name reported with a failure. <para>失败时报告的名称。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task completing once the target digest has been compared.
    /// <para>目标摘要比对完成后结束的任务。</para></returns>
    /// <exception cref="EdlException">The target reported no digest, or a digest that differs from the declared
    /// one. <para>目标未上报摘要，或上报的摘要与声明值不同。</para></exception>
    private async Task VerifyWrittenDigestAsync(FlashBundleDigestCommand digest, long startSector, long numSectors,
        int physicalPartition, string displayName, CancellationToken cancellationToken)
    {
        var reported = await RequireFirehose().GetSha256DigestCoreAsync(startSector, numSectors, physicalPartition,
            cancellationToken).ConfigureAwait(false);
        if (reported is null)
        {
            throw new EdlException(EdlErrorCode.ProgramFailed,
                $"The target reported no digest for the digest-bound program '{displayName}'; the write is unverified.")
            {
                FailureKind = EdlFailureKind.CompletionLost
            };
        }

        if (!reported.AsSpan().SequenceEqual(digest.Digest))
        {
            throw new EdlException(EdlErrorCode.ProgramFailed,
                $"The target digest for '{displayName}' does not match the declared write digest.")
            {
                FailureKind = EdlFailureKind.Rejected
            };
        }
    }


    private static async Task VerifyProgramEntryAsync(
        FlashBundleOptions options, IProgress<FlashBundleProgress>? progress, FirehoseProtocol firehose,
        RawProgramEntry entry, Stream source, long sectorSize, string displayName, int index, int total,
        FlashBundleResult result, CancellationToken cancellationToken)
    {
        var address = FirehoseSectorAddress.Parse(entry.StartSector);
        source.Position = 0;
        if (entry.IsSparse || IsAndroidSparse(source) || entry.FileSectorOffset != 0
            || entry.NumPartitionSectors <= 0 || sectorSize <= 0 || source.Length % sectorSize != 0
            || source.Length / sectorSize != entry.NumPartitionSectors || address.RelativeToEnd)
        {
            result.Warnings.Add(
                $"Read-back verification skipped for '{displayName}': its source does not exactly cover an absolute declared sector range.");
            return;
        }

        RaiseProgress(progress, FlashBundlePhase.Verifying,
            $"Verifying {displayName}... 校验 {displayName}...", index, total);
        source.Position = 0;
        await firehose.VerifyProgramOwnedAsync(source, address.Value,
            entry.NumPartitionSectors, entry.Filename, entry.PhysicalPartitionNumber, cancellationToken).ConfigureAwait(false);
        result.VerifiedEntries++;
    }

    private static bool IsAndroidSparse(Stream stream)
    {
        var position = stream.Position;
        try
        {
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(magic)
                == Firmware.BlockImage.Sparse.SparseFormat.SparseHeaderMagic;
        }
        finally
        {
            stream.Position = position;
        }
    }
}