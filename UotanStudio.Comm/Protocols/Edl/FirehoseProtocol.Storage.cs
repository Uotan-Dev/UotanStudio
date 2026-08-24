using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Firehose protocol health-probe, power and storage-management operations.
/// <para>Firehose 协议的健康探测、电源与存储管理操作。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    /// <summary>
    /// Sends a &lt;nop value="ping"/&gt; health probe to the programmer.
    /// <para>向 programmer 发送 &lt;nop value="ping"/&gt; 健康探测。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> NopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await SendCommandAndWaitResponseAsync(FirehoseXmlBuilder.BuildNop(), cancellationToken);
    }

    /// <summary>
    /// Resets the device (reboot into the system). A best-effort
    /// &lt;setbootablestoragedrive&gt; (UFS→1, eMMC→0) is sent before the reset, and the reset
    /// is verified by reading target logs until the device disappears.
    /// <para>复位设备（重启进入系统）。复位前尽力发送 &lt;setbootablestoragedrive&gt;（UFS→1，
    /// eMMC→0），并通过读取目标日志直到设备消失来验证复位生效。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public Task<FirehoseResponse> ResetAsync(CancellationToken cancellationToken = default)
        => PowerAsync(RebootOption.Reset, cancellationToken);

    /// <summary>
    /// Powers off the device.
    /// <para>关闭设备电源。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public Task<FirehoseResponse> PowerOffAsync(CancellationToken cancellationToken = default)
        => PowerAsync(RebootOption.PowerOff, cancellationToken);

    /// <summary>
    /// Resets the device back into EDL (emergency download) mode.
    /// <para>将设备复位回 EDL（紧急下载）模式。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public Task<FirehoseResponse> ResetToEdlAsync(CancellationToken cancellationToken = default)
        => PowerAsync(RebootOption.ResetToEdl, cancellationToken);

    /// <summary>
    /// Sends a &lt;power&gt; command and verifies that the reset took effect.
    /// <para>发送 &lt;power&gt; 命令并验证复位是否生效。</para>
    /// </summary>
    /// <param name="option">The reboot option. 复位选项。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> PowerAsync(RebootOption option, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Before resetting to the system, best-effort mark the bootable storage drive (mirrors
        // qdl firehose_run: set_bootable → reset). UFS→1, eMMC→0. Failure does not block the
        // reset — just logged. PowerOff/ResetToEdl do not involve a normal boot, so they are
        // skipped.
        // 复位到系统前先尽力标记可启动存储驱动（对齐 qdl firehose_run：set_bootable →
        // reset）。UFS→1，eMMC→0。失败不阻断复位——仅记录日志。PowerOff/ResetToEdl 不涉及
        // 正常启动，跳过。
        if (option == RebootOption.Reset)
        {
            try
            {
                var bootableValue = Configuration.StorageType == FirehoseStorageType.Ufs ? 1 : 0;
                await SetBootableStorageDriveAsync(bootableValue, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"setbootablestoragedrive best-effort failed ({ex.Message}); continuing with reset.");
            }
        }

        var xml = FirehoseXmlBuilder.BuildPower(option);
        await WriteDataAsync(Encoding.UTF8.GetBytes(xml), GetZlpTimeoutMs(), cancellationToken);

        // The device reboots right away and usually never sends an ACK XML; a short wait
        // window catches the rare device that ACKs before rebooting, and a timeout means
        // "device reset".
        // 设备收到后立即复位，通常不会回 ACK XML；短等待窗口捕获极少数先回 ACK 再重启的
        // 设备，超时即视为"设备已重置"。
        FirehoseResponse response;
        try
        {
            response = await ReceiveResponseAsync(cancellationToken, EdlConstants.PowerResponseTimeoutMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Read timeout / connection lost / device gone caused by the reboot are all
            // treated as "device reset".
            // 复位导致的读超时 / 连接断开 / 设备消失均视为"设备已重置"。
            Log($"No response after power command ({ex.Message}); assuming device reset.");
            response = new FirehoseResponse { IsAck = true, RawValue = "ACK (device reset)" };
        }

        // Verify the reset actually took effect: keep reading device output and surface the
        // target logs emitted around the reset until the device disappears (read throws /
        // times out after reset signals) or the verification budget elapses. Still responding
        // within the budget = reset did not fire — warn clearly.
        // 验证复位是否真正生效：持续读取设备输出并上报复位前后的目标日志，直到设备消失
        // （读抛异常/收到复位信号后超时）或验证预算耗尽。预算内仍在响应 = 复位未生效——
        // 明确告警。
        var verifyBudgetMs = Configuration.PowerResetVerifyTimeoutMs;
        if (verifyBudgetMs > 0)
        {
            var deadline = Environment.TickCount64 + verifyBudgetMs;
            var resetConfirmed = false;
            var sawResetSignal = false;
            var readBuffer = new byte[EdlConstants.ResponseReadChunkBytes];
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var chunk = await ReadChunkAsync(readBuffer.Length, 500, cancellationToken);
                    if (chunk.Length == 0)
                    {
                        // Device silent. After a reset-execution log the device is rebooting,
                        // so confirm immediately instead of idling to the budget.
                        // 设备静默。收到复位执行日志后设备正在重启，立即确认而非空等预算。
                        if (sawResetSignal)
                        {
                            Log("Device stopped responding after reset logs; reset confirmed.");
                            resetConfirmed = true;
                            break;
                        }
                        continue;
                    }

                    var text = Encoding.UTF8.GetString(chunk).Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    // Extract and print the log values; fall back to the raw text when the
                    // chunk is not a complete XML block.
                    // 提取并打印 log 值；非完整 XML 块时回退打印原文。
                    var logs = FirehoseResponseParser.ParseDataBlock(text).Logs;
                    if (logs.Count == 0)
                    {
                        logs.Add(text);
                    }

                    foreach (var logValue in logs)
                    {
                        if (logValue.Length > 0)
                        {
                            Log($"TARGET: {logValue}");
                        }
                    }

                    // Reset-execution signal — the device is rebooting; a subsequent silence
                    // confirms the reset.
                    // 复位执行信号——设备正在重启；随后的静默即确认复位。
                    if (text.Contains("Will issue reset", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("bsp_target_reset", StringComparison.OrdinalIgnoreCase))
                    {
                        sawResetSignal = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Device disappeared (USB dropped / connection error) → reset confirmed.
                    // 设备消失（USB 断开 / 连接错误）→ 复位已生效。
                    Log($"Device disconnected after power command ({ex.Message}); reset confirmed.");
                    resetConfirmed = true;
                    break;
                }
            }

            if (!resetConfirmed)
            {
                Log("WARNING: Device still responds after <power>; reset did not take effect. " +
                    "The device may stay in Firehose mode — power-cycle it manually.");
            }
        }

        return response;
    }

    /// <summary>
    /// Marks the specified drive as bootable via &lt;setbootablestoragedrive&gt;.
    /// <para>通过 &lt;setbootablestoragedrive&gt; 标记指定驱动器为可启动。</para>
    /// </summary>
    /// <param name="value">The bootable storage drive number (UFS→1, eMMC→0). 可启动存储驱动器编号（UFS→1，eMMC→0）。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> SetBootableStorageDriveAsync(int value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var xml = FirehoseXmlBuilder.BuildSetBootableStorageDrive(value);
        return await SendCommandAndWaitResponseAsync(xml, cancellationToken);
    }

    /// <summary>
    /// Retrieves storage information via &lt;getstorageinfo&gt; (e.g. the sector_size
    /// attribute used for sector-size auto-detection).
    /// <para>通过 &lt;getstorageinfo&gt; 获取存储信息（如用于扇区大小自动检测的 sector_size
    /// 属性）。</para>
    /// </summary>
    /// <param name="physicalPartitionNumber">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> GetStorageInfoAsync(int physicalPartitionNumber = 0, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var xml = FirehoseXmlBuilder.BuildGetStorageInfo(physicalPartitionNumber);
        return await SendCommandAndWaitResponseAsync(xml, cancellationToken);
    }

    /// <summary>
    /// Sets the active A/B slot via &lt;setactivepartition&gt;.
    /// <para>通过 &lt;setactivepartition&gt; 设置活跃 A/B 槽位。</para>
    /// </summary>
    /// <param name="slot">The slot identifier ("a" or "b"). 槽位标识（"a" 或 "b"）。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> SetActivePartitionAsync(string slot, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(slot))
        {
            throw new ArgumentException("Slot cannot be null or empty.", nameof(slot));
        }

        Log($"Sending <setactivepartition> slot={slot}");
        var response = await SendCommandAndWaitResponseAsync(FirehoseXmlBuilder.BuildSetActivePartition(slot), cancellationToken);

        if (!response.IsAck)
        {
            throw new EdlException(EdlErrorCode.SetActivePartitionFailed,
                $"Set active partition failed for slot '{slot}'. Response: {response.RawValue}");
        }

        Log($"Active partition set to slot '{slot}' successfully.");
        return response;
    }
}