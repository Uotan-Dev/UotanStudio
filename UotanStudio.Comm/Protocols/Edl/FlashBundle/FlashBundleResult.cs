namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// Result of a flash bundle operation, containing processing statistics and error information.
/// <para>固件刷写包操作的执行结果，包含处理统计和错误信息。</para>
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
    /// Gets or sets the list of error messages that occurred during the flash process.
    /// <para>获取或设置刷写过程中发生的错误消息列表。</para>
    /// </summary>
    /// <value>Per-entry failure descriptions and the final fatal error text; a non-empty list marks the run as failed.
    /// <para>逐条目失败描述与最终致命错误文本；列表非空即表示本次运行失败。</para></value>
    public List<string> Errors { get; set; } = [];

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
    /// Returns a string representation of the result.
    /// <para>返回结果的字符串表示。</para>
    /// </summary>
    /// <returns>A string representation of the result. 结果的字符串表示。</returns>
    public override string ToString() =>
        $"FlashBundleResult(Success={IsSuccess}, Program={ProgramEntriesProcessed}/{ProgramEntriesProcessed + ProgramEntriesSkipped}, " +
        $"Erase={EraseEntriesProcessed}, Patch={PatchEntriesProcessed}/{PatchEntriesProcessed + PatchEntriesSkipped}, " +
        $"Wipe={WipeEntriesProcessed}, Ufs={UfsProvisioned}, Bytes={TotalBytesWritten}, Time={ElapsedTime}, Errors={Errors.Count})";
}