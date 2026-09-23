namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Firehose protocol health-probe, power and storage-management operations.
/// <para>Firehose 协议的健康探测、电源与存储管理操作。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    private volatile FirehosePowerStatus _powerStatus;

    /// <summary>Gets the evidence observed for the most recent power command, without claiming a successful boot.</summary>
    public FirehosePowerStatus LastPowerStatus => _powerStatus;

    /// <summary>
    /// Sends a &lt;nop value="ping"/&gt; health probe to the programmer.
    /// <para>向 programmer 发送 &lt;nop value="ping"/&gt; 健康探测。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> NopAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await SendCommandAndWaitResponseAsync(FirehoseXmlBuilder.BuildNop(), cancellationToken);
    }

    /// <summary>
    /// Resets the device (reboot into the system). A best-effort
    /// &lt;setbootablestoragedrive&gt; (UFS→1, eMMC→0) is sent before the reset.
    /// A NAK of that preparation is tolerated, but an uncertain response stops the sequence.
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
    /// Sends a &lt;power&gt; command and returns only its actual ACK or NAK.
    /// An ACK retires the session even if transport disconnection cannot be observed.
    /// Missing, malformed or raw-mode responses fail with an uncertain outcome; they are
    /// never converted to a successful acknowledgement.
    /// </summary>
    /// <param name="option">The reboot option. 复位选项。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
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
            var bootableValue = Configuration.StorageType == FirehoseStorageType.Ufs ? 1 : 0;
            var preparation = await SetBootableStorageDriveCoreAsync(bootableValue, cancellationToken);
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
        // Acknowledgement ends the protocol session. Observe only transport state: empty
        // reads, reset log text and XML parse errors cannot establish a disconnection.
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

    /// <summary>
    /// Marks the specified drive as bootable via &lt;setbootablestoragedrive&gt;.
    /// <para>通过 &lt;setbootablestoragedrive&gt; 标记指定驱动器为可启动。</para>
    /// </summary>
    /// <param name="value">The bootable storage drive number (UFS→1, eMMC→0). 可启动存储驱动器编号（UFS→1，eMMC→0）。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
    public async Task<FirehoseResponse> SetBootableStorageDriveAsync(int value, CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await SetBootableStorageDriveCoreAsync(value, cancellationToken).ConfigureAwait(false);
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
    /// <param name="physicalPartitionNumber">The physical partition number. 物理分区编号。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
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
        }
        return response;
    }

    /// <summary>Queries validated physical capacity; missing or rejected geometry is never replaced with a default.</summary>
    /// <param name="physicalPartitionNumber">The physical region or LUN to query.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The target-reported geometry associated with this query.</returns>
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
    /// <param name="slot">The slot identifier ("a" or "b"). 槽位标识（"a" 或 "b"）。</param>
    /// <param name="cancellationToken">Cancellation token. 取消令牌。</param>
    /// <returns>The Firehose response. Firehose 响应。</returns>
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