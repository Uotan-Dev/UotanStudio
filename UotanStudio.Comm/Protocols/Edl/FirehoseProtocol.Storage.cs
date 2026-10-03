namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Firehose protocol health-probe, power and storage-management operations.
/// <para>Firehose 协议的健康探测、电源与存储管理操作。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    private volatile FirehosePowerStatus _powerStatus;

    /// <summary>
    /// Gets the evidence observed for the most recent power command, without claiming a successful boot.
    /// <para>获取最近一次电源命令的观测证据，不声称已成功引导。</para>
    /// </summary>
    /// <value>
    /// The status tracked by the latest <see cref="PowerAsync"/> attempt on this instance;
    /// <see cref="FirehosePowerStatus.NotRequested"/> when none has been attempted.
    /// <para>本实例最近一次 <see cref="PowerAsync"/> 尝试所跟踪的状态；从未尝试时为
    /// <see cref="FirehosePowerStatus.NotRequested"/>。</para>
    /// </value>
    public FirehosePowerStatus LastPowerStatus => _powerStatus;

    /// <summary>
    /// Sends a &lt;nop value="ping"/&gt; health probe to the programmer.
    /// <para>向 programmer 发送 &lt;nop value="ping"/&gt; 健康探测。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    public async Task<FirehoseResponse> NopAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await SendCommandAndWaitResponseAsync(FirehoseXmlBuilder.BuildNop(), cancellationToken);
    }

    /// <summary>
    /// Resets the device (reboot into the system). A best-effort
    /// &lt;setbootablestoragedrive&gt; (UFS→1, eMMC→0) is sent before the reset, and only for the
    /// storage types that own a bootable logical unit: NAND, SPI NOR and undetected storage skip the
    /// preparation entirely. A NAK of that preparation is tolerated, but an uncertain response stops
    /// the sequence.
    /// <para>复位设备（重启进入系统）。复位前会尽力发送 &lt;setbootablestoragedrive&gt;
    /// （UFS→1，eMMC→0），且仅针对拥有可引导逻辑单元的存储类型：NAND、SPI NOR 与未探测到的存储
    /// 完全跳过该准备命令。该准备命令返回 NAK 可以容忍，但结果不确定的响应会中止整个序列。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>
    /// The Firehose response.
    /// <para>Firehose 响应。</para>
    /// </returns>
    public Task<FirehoseResponse> ResetAsync(CancellationToken cancellationToken = default)
        => PowerAsync(RebootOption.Reset, cancellationToken);

    /// <summary>
    /// Powers off the device.
    /// <para>关闭设备电源。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    public Task<FirehoseResponse> PowerOffAsync(CancellationToken cancellationToken = default)
        => PowerAsync(RebootOption.PowerOff, cancellationToken);

    /// <summary>
    /// Resets the device back into EDL (emergency download) mode.
    /// <para>将设备复位回 EDL（紧急下载）模式。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    public Task<FirehoseResponse> ResetToEdlAsync(CancellationToken cancellationToken = default)
        => PowerAsync(RebootOption.ResetToEdl, cancellationToken);

    /// <summary>
    /// Sends a &lt;power&gt; command and returns only its actual ACK or NAK.
    /// An ACK retires the session even if transport disconnection cannot be observed.
    /// Missing, malformed or raw-mode responses fail with an uncertain outcome; they are
    /// never converted to a successful acknowledgement.
    /// <para>发送 &lt;power&gt; 命令，且只返回其真实的 ACK 或 NAK。即使无法观测到传输层断开，
    /// ACK 也会使本会话退役。缺失、格式错误或处于原始模式的响应会以不确定结果失败，绝不
    /// 被转换成成功确认。</para>
    /// </summary>
    /// <param name="option">The reboot option. <para>复位选项。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>
    /// The Firehose response.
    /// <para>Firehose 响应。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="option"/> is not a defined
    /// <see cref="RebootOption"/>, or the configured power timeouts are out of range.
    /// <para><paramref name="option"/> 不是已定义的 <see cref="RebootOption"/>，或配置的电源超时越界。</para></exception>
    /// <exception cref="FirehoseException">The boot-drive preparation or the power response is raw-mode or otherwise
    /// uncertain, leaving the outcome unknown.
    /// <para>启动驱动器准备或电源响应处于原始模式或结果无法判定，命令结果不确定。</para></exception>
    /// <exception cref="EdlException">Waiting for the complete response times out or the transport fails.
    /// <para>等待完整响应超时，或传输层失败。</para></exception>
    public async Task<FirehoseResponse> PowerAsync(RebootOption option, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        if (!Enum.IsDefined(option))
        {
            throw new ArgumentOutOfRangeException(nameof(option));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Configuration.PowerResponseTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegative(Configuration.PowerResetVerifyTimeoutMs);

        if (option == RebootOption.Reset)
        {
            int? bootableValue = Configuration.StorageType switch
            {
                FirehoseStorageType.Ufs => 1,
                FirehoseStorageType.Emmc => 0,
                _ => null
            };
            if (bootableValue is { } value)
            {
                var preparation = await SetBootableStorageDriveCoreAsync(value, cancellationToken).ConfigureAwait(false);
                if (preparation.IsRawMode || (!preparation.IsAck && !preparation.IsNak))
                {
                    _faulted = true;
                    throw new FirehoseException("Invalid response to boot-drive preparation; reset was not sent.");
                }

                if (preparation.IsNak)
                {
                    Log("Target rejected boot-drive preparation; continuing with the requested reset.");
                }
            }
        }

        var xml = FirehoseXmlBuilder.BuildPower(option);
        cancellationToken.ThrowIfCancellationRequested();
        _powerStatus = FirehosePowerStatus.AwaitingResponse;
        FirehoseResponse response;
        try
        {
            response = await SendCommandAndWaitResponseAsync(xml, cancellationToken, Configuration.PowerResponseTimeoutMs).ConfigureAwait(false);
            if (response.IsRawMode || (!response.IsAck && !response.IsNak))
            {
                throw new FirehoseException("Power returned neither a normal-mode ACK nor a normal-mode NAK.");
            }
        }
        catch
        {
            _powerStatus = FirehosePowerStatus.Unknown;
            _faulted = true;
            throw;
        }

        if (response.IsNak)
        {
            _powerStatus = FirehosePowerStatus.Rejected;
            return response;
        }

        _powerStatus = FirehosePowerStatus.Acknowledged;
        _faulted = true;
        var start = Environment.TickCount64;
        var budget = Configuration.PowerResetVerifyTimeoutMs;
        while (_transport.IsConnected && Environment.TickCount64 - start < budget)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = budget - (Environment.TickCount64 - start);
            if (remaining > 0)
            {
                await Task.Delay((int)Math.Min(25, remaining), cancellationToken).ConfigureAwait(false);
            }
        }

        if (!_transport.IsConnected)
        {
            _powerStatus = FirehosePowerStatus.AcknowledgedAndDisconnected;
        }

        Log(_powerStatus == FirehosePowerStatus.AcknowledgedAndDisconnected
            ? "Power acknowledged and transport disconnected; successful boot is unverified."
            : "Power acknowledged; transport disconnection and successful boot are unverified.");
        return response;
    }

    private async Task<FirehoseResponse> SetBootableStorageDriveCoreAsync(int value, CancellationToken cancellationToken)
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
    /// <param name="physicalPartitionNumber">The physical partition number. <para>物理分区编号。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    public async Task<FirehoseResponse> GetStorageInfoAsync(int physicalPartitionNumber = 0, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await GetStorageInfoCoreAsync(physicalPartitionNumber, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FirehoseResponse> GetStorageInfoCoreAsync(int physicalPartitionNumber, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(physicalPartitionNumber);
        var report = new FirehoseStorageReport();
        var xml = FirehoseXmlBuilder.BuildGetStorageInfo(physicalPartitionNumber);
        var response = await SendCommandAndWaitResponseAsync(xml, cancellationToken, logObserver: report.Observe).ConfigureAwait(false);
        if (response.IsRawMode)
        {
            _faulted = true;
            throw new FirehoseException("Storage query unexpectedly entered raw mode.");
        }
        if (response.IsAck)
        {
            response.StorageGeometry = report.Parse(physicalPartitionNumber);
            if (response.StorageGeometry is { } geometry)
            {
                _lastGeometry = geometry;
            }
            _lastStorageDetails = report.ParseDetails() ?? _lastStorageDetails;
        }
        return response;
    }

    /// <summary>
    /// Queries validated physical capacity; missing or rejected geometry is never replaced with a default.
    /// <para>查询经过校验的物理容量；缺失或被拒绝的几何信息绝不会被默认值替代。</para>
    /// </summary>
    /// <param name="physicalPartitionNumber">
    /// The physical region or LUN to query.
    /// <para>要查询的物理区域或 LUN。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The operation cancellation token.
    /// <para>操作取消令牌。</para>
    /// </param>
    /// <returns>
    /// The target-reported geometry associated with this query.
    /// <para>与本次查询相关联的、由目标上报的几何信息。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="physicalPartitionNumber"/> is negative.
    /// <para><paramref name="physicalPartitionNumber"/> 为负数。</para></exception>
    /// <exception cref="FirehoseException">The target NAKs the query, enters raw mode, or reports no usable geometry.
    /// <para>目标对该查询返回 NAK、进入原始模式，或未能报告可用几何信息。</para></exception>
    /// <exception cref="OperationCanceledException">The lease wait or response reception is cancelled.
    /// <para>租约等待或响应接收被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The protocol has been disposed.
    /// <para>协议已被释放。</para></exception>
    public async Task<FirehoseStorageGeometry> GetStorageGeometryAsync(int physicalPartitionNumber = 0,
        CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await GetStorageGeometryOwnedAsync(physicalPartitionNumber, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queries geometry while the caller holds the operation lease.</summary>
    internal async Task<FirehoseStorageGeometry> GetStorageGeometryOwnedAsync(int physicalPartitionNumber,
        CancellationToken cancellationToken)
    {
        var response = await GetStorageInfoCoreAsync(physicalPartitionNumber, cancellationToken).ConfigureAwait(false);
        if (!response.IsAck || response.StorageGeometry is null)
        {
            throw new FirehoseException("The target did not acknowledge usable storage geometry for the requested partition.");
        }
        return response.StorageGeometry;
    }

    /// <summary>
    /// Sets the active A/B slot via &lt;setactivepartition&gt;.
    /// <para>通过 &lt;setactivepartition&gt; 设置活跃 A/B 槽位。</para>
    /// </summary>
    /// <param name="slot">The slot identifier ("a" or "b"). <para>槽位标识（"a" 或 "b"）。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The Firehose response. <para>Firehose 响应。</para></returns>
    public async Task<FirehoseResponse> SetActivePartitionAsync(string slot, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
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