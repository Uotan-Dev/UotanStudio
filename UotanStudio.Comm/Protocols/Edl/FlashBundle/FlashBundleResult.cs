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
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Gets or sets the number of program entries that were successfully processed.
    /// <para>获取或设置已成功处理的 program 条目数。</para>
    /// </summary>
    public int ProgramEntriesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the number of program entries that were skipped or failed.
    /// <para>获取或设置被跳过或失败的 program 条目数。</para>
    /// </summary>
    public int ProgramEntriesSkipped { get; set; }

    /// <summary>
    /// Gets or sets the number of erase entries that were successfully processed.
    /// <para>获取或设置已成功处理的 erase 条目数。</para>
    /// </summary>
    public int EraseEntriesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the number of patch entries that were successfully processed.
    /// <para>获取或设置已成功处理的 patch 条目数。</para>
    /// </summary>
    public int PatchEntriesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the number of patch entries that were skipped or failed.
    /// <para>获取或设置被跳过或失败的 patch 条目数。</para>
    /// </summary>
    public int PatchEntriesSkipped { get; set; }

    /// <summary>
    /// Gets or sets the number of wipe (program-zeros) operations that were successfully
    /// processed.
    /// <para>获取或设置已成功处理的 wipe（零字节编程）操作数。</para>
    /// </summary>
    public int WipeEntriesProcessed { get; set; }

    /// <summary>
    /// Gets or sets whether UFS provisioning was successfully completed.
    /// <para>获取或设置是否已成功完成 UFS 配置。</para>
    /// </summary>
    public bool UfsProvisioned { get; set; }

    /// <summary>
    /// Gets or sets the list of error messages that occurred during the flash process.
    /// <para>获取或设置刷写过程中发生的错误消息列表。</para>
    /// </summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>
    /// Gets or sets the total number of bytes written to the device.
    /// <para>获取或设置写入设备的总字节数。</para>
    /// </summary>
    public long TotalBytesWritten { get; set; }

    /// <summary>
    /// Gets or sets the total elapsed Time of the flash operation.
    /// <para>获取或设置刷写操作的总耗时。</para>
    /// </summary>
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