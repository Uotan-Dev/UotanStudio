using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Result of a flash bundle operation, containing processing statistics, error information, and
/// structured per-item execution evidence.
/// <para>固件刷写包操作的执行结果，包含处理统计、错误信息与逐条目结构化执行证据。</para>
/// </summary>
public sealed class FlashBundleResult
{
    /// <summary>
    /// Gets or sets whether the flash operation completed successfully (no errors).
    /// <para>获取或设置刷写操作是否成功完成（无错误）。</para>
    /// </summary>
    /// <value>True when the run finished with an empty <see cref="Errors"/> list; preflight and offline inspection results set it once validation passes.
    /// <para>运行结束时 <see cref="Errors"/> 列表为空则为 true；预检与离线检查结果在校验通过后即置为 true。</para></value>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Gets or sets the number of program entries that were successfully processed.
    /// <para>获取或设置已成功处理的 program 条目数。</para>
    /// </summary>
    /// <value>Program entries whose transfer completed with a device ACK; counting stops at the first failure because the bundle aborts.
    /// <para>传输以设备 ACK 完成的 program 条目数；出现首个失败即中止本包，计数随之停止。</para></value>
    public int ProgramEntriesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the number of program entries that were skipped or failed.
    /// <para>获取或设置被跳过或失败的 program 条目数。</para>
    /// </summary>
    /// <value>Selected program entries that were not programmed: skipped by policy or dry run, empty erase requests, or the entry whose program or erase failed. Preflight results report the full selected count.
    /// <para>未执行编程的已选 program 条目：因策略或 dry run 被跳过、空的擦除请求，或 program/erase 失败的那一条。预检结果报告完整的已选数量。</para></value>
    public int ProgramEntriesSkipped { get; set; }

    /// <summary>
    /// Gets or sets the number of program entries dropped before execution because their source image
    /// is not present in the package.
    /// <para>获取或设置在执行前因源镜像不在包内而被丢弃的 program 条目数。</para>
    /// </summary>
    /// <value>Count of absent-source entries; only non-zero when <see cref="FlashBundleOptions.SkipAbsentSources"/> is set.
    /// <para>缺失源镜像条目数；仅在设置 <see cref="FlashBundleOptions.SkipAbsentSources"/> 时非零。</para></value>
    public int ProgramEntriesAbsent { get; set; }

    /// <summary>
    /// Gets or sets the number of erase entries that were successfully processed.
    /// <para>获取或设置已成功处理的 erase 条目数。</para>
    /// </summary>
    /// <value>Filename-less program entries erased with a device ACK before the first failure.
    /// <para>在首次失败前以设备 ACK 完成擦除的无文件名 program 条目数。</para></value>
    public int EraseEntriesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the number of patch entries that were successfully processed.
    /// <para>获取或设置已成功处理的 patch 条目数。</para>
    /// </summary>
    /// <value>Patch entries acknowledged by the device before the first failure.
    /// <para>在首次失败前获得设备确认的 patch 条目数。</para></value>
    public int PatchEntriesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the number of patch entries that were skipped or failed.
    /// <para>获取或设置被跳过或失败的 patch 条目数。</para>
    /// </summary>
    /// <value>Selected patch entries that were not applied: skipped by policy or the offline dry run, or the entry whose patch failed. Preflight results report the full selected count.
    /// <para>未应用的已选 patch 条目：因策略或离线 dry run 被跳过，或修补失败的那一条。预检结果报告完整的已选数量。</para></value>
    public int PatchEntriesSkipped { get; set; }

    /// <summary>
    /// Gets or sets the number of wipe (program-zeros) operations that were successfully
    /// processed.
    /// <para>获取或设置已成功处理的 wipe（零字节编程）操作数。</para>
    /// </summary>
    /// <value>Leading-sector zeroing operations acknowledged by the device, one per targeted physical partition.
    /// <para>获得设备确认的前导扇区清零操作数，每个目标物理分区一次。</para></value>
    public int WipeEntriesProcessed { get; set; }

    /// <summary>
    /// Gets or sets whether UFS provisioning was successfully completed.
    /// <para>获取或设置是否已成功完成 UFS 配置。</para>
    /// </summary>
    /// <value>True once the provisioning sequence receives a device ACK; stays false when no provisioning XML was found or selected.
    /// <para>配置序列收到设备 ACK 后为 true；未发现或未选择配置 XML 时保持 false。</para></value>
    public bool UfsProvisioned { get; set; }

    /// <summary>
    /// Gets or sets the number of program entries whose read-back verification completed.
    /// <para>获取或设置回读校验完成的 program 条目数。</para>
    /// </summary>
    /// <value>Entries compared byte by byte against their sources after programming when
    /// <see cref="FlashBundleOptions.Verify"/> is set; entries skipped by the verification gate
    /// are not counted.
    /// <para>设置 <see cref="FlashBundleOptions.Verify"/> 后与源逐字节比对完成的条目数；
    /// 被校验门控跳过的条目不计入。</para></value>
    public int VerifiedEntries { get; set; }

    /// <summary>
    /// Gets or sets the list of error messages that occurred during the flash process.
    /// <para>获取或设置刷写过程中发生的错误消息列表。</para>
    /// </summary>
    /// <value>Per-entry failure descriptions and the final fatal error text; a non-empty list marks the run as failed.
    /// <para>逐条目失败描述与最终致命错误文本；列表非空即表示本次运行失败。</para></value>
    public List<string> Errors { get; set; } = [];

    /// <summary>
    /// Gets or sets the list of non-fatal deviations recorded while the bundle ran, such as a source
    /// longer than its declared sector range.
    /// <para>获取或设置刷写包运行期间记录的非致命偏差，例如源文件长于其声明扇区范围。</para>
    /// </summary>
    /// <value>Descriptions of writes that proceeded with less data than the manifest names; a non-empty
    /// list does not mark the run as failed.
    /// <para>按清单少于其声明数据量完成写入的描述；列表非空不表示本次运行失败。</para></value>
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    /// Gets or sets program payload bytes with a final device ACK, including completed sparse
    /// extents of a later-failed entry and successful wipes. Excludes sparse holes, metadata,
    /// erase/patch commands and unacknowledged transfers. This is not readback verification;
    /// an interrupted transfer may have changed additional device bytes.
    /// <para>获取或设置已获得设备最终 ACK 的 program 载荷字节数，包含后续失败条目中已完成的
    /// sparse 区段以及成功的 wipe。不包含 sparse 空洞、元数据、erase/patch 命令以及未确认的
    /// 传输。这不是回读校验；被中断的传输可能已改变更多设备字节。</para>
    /// </summary>
    /// <value>Acknowledged payload bytes accumulated with checked arithmetic; zero for offline inspection results.
    /// <para>以 checked 算术累加的已确认载荷字节数；离线检查结果为零。</para></value>
    public long TotalBytesWritten { get; set; }

    /// <summary>
    /// Gets or sets the total elapsed Time of the flash operation.
    /// <para>获取或设置刷写操作的总耗时。</para>
    /// </summary>
    /// <value>Wall-clock time from bundle entry to exit, recorded even when the run failed.
    /// <para>从进入本包到退出的挂钟时间，运行失败时同样记录。</para></value>
    public TimeSpan ElapsedTime { get; set; }

    /// <summary>
    /// Gets the structured per-item execution evidence produced by this run.
    /// <para>获取本次运行产出的逐条目结构化执行证据。</para>
    /// </summary>
    /// <value>One entry per attempted storage item and per device preflight partition; consumers
    /// distinguish cancellation, first failure, and partial completion from the recorded
    /// <see cref="FlashItemEvidence.Outcome"/> rather than from <see cref="Errors"/> text.
    /// <para>每个已尝试的存储条目与每个设备预检分区各一条；消费者依据记录的
    /// <see cref="FlashItemEvidence.Outcome"/> 区分取消、首个失败与部分完成，而不是
    /// <see cref="Errors"/> 文本。</para></value>
    public IReadOnlyList<FlashItemEvidence> Items => ItemsSource;

    /// <summary>
    /// Gets the outcome of the last recorded storage item, or <see cref="CommandOutcomeKind.Unknown"/>
    /// when nothing was attempted.
    /// <para>获取最后一个已记录存储条目的结果；未尝试任何条目时为
    /// <see cref="CommandOutcomeKind.Unknown"/>。</para>
    /// </summary>
    public CommandOutcomeKind FinalOutcome => ItemsSource.Count == 0
        ? CommandOutcomeKind.Unknown
        : ItemsSource[^1].Outcome;

    /// <summary>
    /// Gets whether every recorded storage item completed with a device acknowledgement.
    /// <para>获取是否每个已记录存储条目均以设备确认完成。</para>
    /// </summary>
    /// <value><see langword="true"/> only for a non-empty item list whose every outcome is
    /// <see cref="CommandOutcomeKind.Completed"/>. <para>仅当条目列表非空且每项结果均为
    /// <see cref="CommandOutcomeKind.Completed"/> 时为 <see langword="true"/>。</para></value>
    public bool AllPhasesCompleted => ItemsSource.Count > 0
        && ItemsSource.TrueForAll(static item => item.Outcome == CommandOutcomeKind.Completed);

    /// <summary>
    /// Gets or sets whether the run ended with device-side state that requires a reconnect before
    /// any retry.
    /// <para>获取或设置本次运行是否结束于需先重连才能重试的设备端状态。</para>
    /// </summary>
    /// <value><see langword="true"/> after a cancelled mid-transfer, a lost completion, or a faulted
    /// transport; <see langword="false"/> for clean success and plain device rejections.
    /// <para>传输中途取消、完成确认丢失或传输故障后为 <see langword="true"/>；完全成功与单纯的
    /// 设备拒绝均为 <see langword="false"/>。</para></value>
    public bool RequiresReconnect { get; set; }

    /// <summary>
    /// Gets or sets the typed classification of the first fatal failure of this run.
    /// <para>获取或设置本次运行首个致命失败的类型化分类。</para>
    /// </summary>
    /// <value>The failure kind, or <see langword="null"/> when nothing failed.
    /// <para>失败种类；未失败时为 <see langword="null"/>。</para></value>
    public EdlFailureKind? FailureKind { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 identity of the programmer the run executed against, when the session
    /// recorded one.
    /// <para>获取或设置本次运行所针对 programmer 的 SHA-256 身份（会话已记录时）。</para>
    /// </summary>
    public string? ResourceSha256Hex { get; set; }

    /// <summary>
    /// Gets or sets the transport generation the capability snapshot was negotiated on, when known.
    /// <para>获取或设置能力快照协商所在的传输代际（已知时）。</para>
    /// </summary>
    public ulong? TransportGeneration { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant at which the run was entered.
    /// <para>获取或设置进入本次运行的 UTC 时刻。</para>
    /// </summary>
    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant at which the run finished, failed, or was cancelled.
    /// <para>获取或设置本次运行完成、失败或取消的 UTC 时刻。</para>
    /// </summary>
    public DateTimeOffset CompletedAtUtc { get; set; }

    /// <summary>
    /// Gets the shared-vocabulary evidence projected from the structured items.
    /// <para>获取由结构化条目投影出的共享词汇证据。</para>
    /// </summary>
    /// <value>One <see cref="ProtocolEvidence"/> per recorded item, in execution order.
    /// <para>每个已记录条目一条 <see cref="ProtocolEvidence"/>，按执行顺序排列。</para></value>
    public IReadOnlyList<ProtocolEvidence> Evidence => [.. ItemsSource.Select(static item => item.ToEvidence())];

    internal List<FlashItemEvidence> ItemsSource { get; } = [];

    internal void Record(FlashItemEvidence item) => ItemsSource.Add(item);

    /// <summary>
    /// Returns a string representation of the result.
    /// <para>返回结果的字符串表示。</para>
    /// </summary>
    /// <returns>A string representation of the result. 结果的字符串表示。</returns>
    public override string ToString() =>
        $"FlashBundleResult(Success={IsSuccess}, Program={ProgramEntriesProcessed}/{ProgramEntriesProcessed + ProgramEntriesSkipped}, " +
        $"Erase={EraseEntriesProcessed}, Patch={PatchEntriesProcessed}/{PatchEntriesProcessed + PatchEntriesSkipped}, " +
        $"Wipe={WipeEntriesProcessed}, Ufs={UfsProvisioned}, Bytes={TotalBytesWritten}, Time={ElapsedTime}, Errors={Errors.Count})";
}