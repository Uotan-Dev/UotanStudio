using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Sunxi;

namespace UotanStudio.Services.Devices;

/// <summary>
/// One Allwinner endpoint as the identity handshake reported it, plus the service answers that are only available
/// in FES mode. <para>身份握手上报的一个全志端点，以及仅在 FES 模式下可得到的服务应答。</para>
/// </summary>
/// <param name="Identity">Identity the device reported during the handshake.
/// <para>设备在握手期间上报的身份信息。</para></param>
/// <param name="StorageType">Storage type word, or <see langword="null"/> in FEL mode.
/// <para>存储类型字；FEL 模式下为 <see langword="null"/>。</para></param>
/// <param name="FlashSectors">Flash capacity in 512-byte sectors, or <see langword="null"/> when the probe failed.
/// <para>闪存容量（512 字节扇区数）；探测失败时为 <see langword="null"/>。</para></param>
/// <param name="SecureWord">Secure-boot and boot-media mode word, or <see langword="null"/> in FEL mode.
/// <para>安全启动与启动介质模式字；FEL 模式下为 <see langword="null"/>。</para></param>
public sealed record SunxiDeviceStatus(SunxiDeviceIdentity Identity, uint? StorageType, uint? FlashSectors,
    uint? SecureWord)
{
    /// <summary>Chip family resolved from the reported SoC id, or its hexadecimal id when the table does not know it.
    /// <para>由上报的 SoC id 解析出的芯片家族；表内没有该 id 时为其十六进制值。</para></summary>
    public string ChipName => Identity.ChipName ?? $"0x{Identity.Id:X8}";

    /// <summary>Mode the device currently runs. <para>设备当前运行的模式。</para></summary>
    public SunxiDeviceMode Mode => Identity.Mode;

    /// <summary>Whether the device runs the FES service. <para>设备是否运行 FES 服务。</para></summary>
    public bool IsFes => Mode == SunxiDeviceMode.Fes;

    /// <summary>Flash capacity in bytes, or <see langword="null"/> when the probe did not answer.
    /// <para>闪存容量（字节）；探测未应答时为 <see langword="null"/>。</para></summary>
    public long? CapacityBytes => FlashSectors is { } sectors ? sectors * 512L : null;
}

public sealed partial class DeviceMonitorService
{
    /// <summary>Cooperative bound for one Sunxi status query, so a silent endpoint never holds the page.
    /// <para>单次全志状态查询的协作式时限，使无应答端点无法拖住页面。</para></summary>
    private const int SunxiStatusTimeoutMs = 5000;

    /// <summary>How long the service switch waits for the re-enumerated device to answer the FES handshake.
    /// <para>服务切换等待重枚举设备应答 FES 握手的时长。</para></summary>
    private const int SunxiServiceReconnectTimeoutMs = 60_000;

    /// <summary>Delay between two reconnect attempts while the device leaves FEL and comes back as FES.
    /// <para>设备离开 FEL 并以 FES 返回期间两次重连尝试之间的等待。</para></summary>
    private const int SunxiServiceReconnectIntervalMs = 500;

    /// <summary>Reconnect attempts the handoff makes before it reports the service never came up.
    /// <para>切换在报告服务未启动之前进行的重连尝试次数。</para></summary>
    private const int SunxiServiceAttempts = 20;

    /// <summary>
    /// Runs one operation on a leased Sunxi endpoint; the session resolves FEL or FES from the handshake, and a
    /// mode that does not match the operation fails before any command.
    /// <para>在已租用的全志端点上执行一次操作；会话由握手确定 FEL 或 FES，模式不匹配的操作在发出任何命令之前失败。</para>
    /// </summary>
    /// <typeparam name="TResult">Result of the operation. <para>操作的结果。</para></typeparam>
    /// <param name="device">The selected Sunxi endpoint. <para>所选全志端点。</para></param>
    /// <param name="operation">Work to run over the session and its lease. <para>经会话及其租约执行的工作。</para></param>
    /// <param name="cancellationToken">Cancels discovery, opening and the operation.
    /// <para>取消发现、打开与操作。</para></param>
    /// <returns>The value produced by <paramref name="operation"/>. <para><paramref name="operation"/> 产生的值。</para></returns>
    /// <exception cref="ArgumentException">The device is not a Sunxi endpoint.
    /// <para>设备不是全志端点。</para></exception>
    /// <exception cref="IOException">The endpoint does not expose a Sunxi session.
    /// <para>端点未提供全志会话。</para></exception>
    internal async Task<TResult> RunSunxiAsync<TResult>(DetectedDevice device,
        Func<SunxiDeviceSession, DeviceLease, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Protocol != DeviceProtocolKind.Sunxi)
        {
            throw new ArgumentException("A Sunxi target is required.", nameof(device));
        }

        var lease = LeaseEndpoint(device, sessionKind: DeviceSessionKind.Sunxi,
            cancellationToken: cancellationToken);
        try
        {
            if (!lease.TryGetSession<SunxiDeviceSession>(out var session))
            {
                throw new IOException("The selected device does not expose a Sunxi session.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await operation(session, lease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CloseLease(lease);
        }
    }

    /// <summary>
    /// Reads the identity, and in FES mode the storage and security answers the status row shows. A failed flash-size
    /// probe only leaves that value empty, because some media cannot answer it without a flash-controller switch.
    /// <para>读取身份；FES 模式下再读取状态行显示的存储与安全应答。闪存容量探测失败只会让该值留空，因为部分介质
    /// 在未切换闪存控制器前无法应答。</para>
    /// </summary>
    /// <param name="device">The selected Sunxi endpoint. <para>所选全志端点。</para></param>
    /// <param name="cancellationToken">Cancels the query. <para>取消本次查询。</para></param>
    /// <returns>The reported identity and service answers. <para>上报的身份与服务应答。</para></returns>
    internal Task<SunxiDeviceStatus> QuerySunxiStatusAsync(DetectedDevice device,
        CancellationToken cancellationToken = default) =>
        RunSunxiAsync(device, async (session, _, token) =>
        {
            if (!session.IsFes)
            {
                return new SunxiDeviceStatus(session.Identity, null, null, null);
            }

            var fes = session.Fes;
            var storage = await fes.QueryStorageAsync(token).ConfigureAwait(false);
            uint? secure = null;
            uint? sectors = null;
            try
            {
                secure = await fes.QuerySecureAsync(token).ConfigureAwait(false);
            }
            catch (SunxiEfexException)
            {
            }

            try
            {
                sectors = await fes.ProbeFlashSizeAsync(token).ConfigureAwait(false);
            }
            catch (SunxiEfexException)
            {
            }

            return new SunxiDeviceStatus(session.Identity, storage, sectors, secure);
        }, cancellationToken);

    /// <summary>
    /// Reads device memory through the FEL session and returns the bytes.
    /// <para>经 FEL 会话读取设备内存并返回字节。</para>
    /// </summary>
    /// <param name="device">The selected Sunxi endpoint. <para>所选全志端点。</para></param>
    /// <param name="address">First source address. <para>首个源地址。</para></param>
    /// <param name="length">Byte count to read; must be positive. <para>要读取的字节数；必须为正。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>The bytes read from <paramref name="address"/>. <para>从 <paramref name="address"/> 读到的字节。</para></returns>
    /// <exception cref="InvalidOperationException">The device is not in FEL mode.
    /// <para>设备不在 FEL 模式。</para></exception>
    internal Task<byte[]> ReadSunxiMemoryAsync(DetectedDevice device, uint address, int length,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return RunSunxiAsync(device, async (session, _, token) =>
        {
            RequireFel(session);
            var buffer = new byte[length];
            await session.Fel.ReadAsync(address, buffer, token).ConfigureAwait(false);
            return buffer;
        }, cancellationToken);
    }

    /// <summary>
    /// Writes host bytes into device memory through the FEL session.
    /// <para>经 FEL 会话把主机字节写入设备内存。</para>
    /// </summary>
    /// <param name="device">The selected Sunxi endpoint. <para>所选全志端点。</para></param>
    /// <param name="address">First destination address. <para>首个目标地址。</para></param>
    /// <param name="data">Bytes to write; must not be empty. <para>待写入的字节；不能为空。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消本次写入。</para></param>
    /// <returns>A task that completes when every stage is acknowledged.
    /// <para>所有阶段都被确认后完成的任务。</para></returns>
    /// <exception cref="InvalidOperationException">The device is not in FEL mode.
    /// <para>设备不在 FEL 模式。</para></exception>
    internal Task WriteSunxiMemoryAsync(DetectedDevice device, uint address, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("A memory write needs at least one byte.", nameof(data));
        }

        return RunSunxiAsync(device, async (session, _, token) =>
        {
            RequireFel(session);
            await session.Fel.WriteAsync(address, data, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Calls an address through the FEL session; the caller is responsible for writing the code first.
    /// <para>经 FEL 会话调用一个地址；调用方负责先写入代码。</para>
    /// </summary>
    /// <param name="device">The selected Sunxi endpoint. <para>所选全志端点。</para></param>
    /// <param name="address">Entry address. <para>入口地址。</para></param>
    /// <param name="cancellationToken">Cancels the command. <para>取消本次命令。</para></param>
    /// <returns>A task that completes when the device acknowledged the call.
    /// <para>设备确认调用后完成的任务。</para></returns>
    /// <exception cref="InvalidOperationException">The device is not in FEL mode.
    /// <para>设备不在 FEL 模式。</para></exception>
    internal Task ExecuteSunxiAsync(DetectedDevice device, uint address, CancellationToken cancellationToken = default) =>
        RunSunxiAsync(device, async (session, _, token) =>
        {
            RequireFel(session);
            await session.Fel.ExecuteAsync(address, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Uploads the first-stage DRAM loader and the service payload, waits for the re-enumerated device and returns
    /// the status the FES service reports. The FEL lease is released before the reconnection wait; a device that
    /// comes back in any other mode than FES fails the switch.
    /// <para>上传一级 DRAM 加载器与服务负载，等待重枚举设备并返回 FES 服务上报的状态。重连等待之前先释放 FEL 租约；
    /// 设备以 FES 之外的任何模式返回都会使切换失败。</para>
    /// </summary>
    /// <param name="device">The selected Sunxi endpoint; it must be in FEL mode.
    /// <para>所选全志端点；必须处于 FEL 模式。</para></param>
    /// <param name="fes1Image">First-stage loader image, including its <c>eGON.BT0</c> header.
    /// <para>一级加载器镜像，含其 <c>eGON.BT0</c> 头部。</para></param>
    /// <param name="ubootImage">Service payload, including its <c>u-boot</c> header.
    /// <para>服务负载，含其 <c>u-boot</c> 头部。</para></param>
    /// <param name="progress">Optional phase text sink. <para>可选的阶段文本接收器。</para></param>
    /// <param name="cancellationToken">Cancels the uploads, the waits and the status query.
    /// <para>取消上传、等待与状态查询。</para></param>
    /// <returns>The status of the freshly opened FES session.
    /// <para>新打开的 FES 会话的状态。</para></returns>
    /// <exception cref="InvalidOperationException">The device is not in FEL mode.
    /// <para>设备不在 FEL 模式。</para></exception>
    /// <exception cref="TimeoutException">The device did not answer the FES handshake in time.
    /// <para>设备未及时应答 FES 握手。</para></exception>
    internal async Task<SunxiDeviceStatus> RunSunxiBootServiceAsync(DetectedDevice device,
        ReadOnlyMemory<byte> fes1Image, ReadOnlyMemory<byte> ubootImage, IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        if (device.Protocol != DeviceProtocolKind.Sunxi)
        {
            throw new ArgumentException("A Sunxi target is required.", nameof(device));
        }

        CancelBackgroundEndpointWork(device);
        var previous = device;
        var lease = LeaseEndpoint(device, sessionKind: DeviceSessionKind.Sunxi,
            cancellationToken: cancellationToken);
        DeviceLease? switched = null;
        SunxiFesSession? fesSession = null;
        try
        {
            if (!lease.TryGetSession<SunxiDeviceSession>(out var session) || !session.IsFel)
            {
                throw new InvalidOperationException("The device must be in FEL mode to start the FES service.");
            }

            var chain = lease.Endpoint.UsbInfo?.PhysicalPortPath;
            var handoff = new SunxiFesHandoff(async token =>
            {
                CloseLease(lease);
                var fresh = await ReopenSunxiFesAsync(previous, chain, token).ConfigureAwait(false);
                switched = fresh.Lease;
                fesSession = fresh.Session;
                return fresh.Session;
            });
            progress?.Report("fes1");
            await handoff.InitialiseDramAsync(session.Fel, fes1Image, cancellationToken).ConfigureAwait(false);
            progress?.Report("service");
            await handoff.StartServiceAsync(session.Fel, ubootImage, SunxiServiceAttempts, cancellationToken)
                .ConfigureAwait(false);
            if (fesSession is null)
            {
                throw new TimeoutException("The device did not answer the FES handshake.");
            }

            progress?.Report("status");
            var storage = await fesSession.QueryStorageAsync(cancellationToken).ConfigureAwait(false);
            uint? sectors = null;
            try
            {
                sectors = await fesSession.ProbeFlashSizeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SunxiEfexException)
            {
            }

            uint? secure = null;
            try
            {
                secure = await fesSession.QuerySecureAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SunxiEfexException)
            {
            }

            return new SunxiDeviceStatus(session.Identity, storage, sectors, secure);
        }
        finally
        {
            CloseLease(lease);
            if (switched is { } freshLease)
            {
                CloseLease(freshLease);
            }
        }
    }

    private async Task<(DeviceLease Lease, SunxiFesSession Session)> ReopenSunxiFesAsync(DetectedDevice previous,
        string? physicalPortPath, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SunxiServiceReconnectTimeoutMs);
        var token = deadline.Token;
        Exception? lastFailure = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var candidate = FindSunxiCandidate(previous, physicalPortPath);
                if (candidate is not null)
                {
                    DeviceLease? lease = null;
                    try
                    {
                        lease = await Task.Run(() => OpenLeaseByKey(candidate.Key, DeviceSessionKind.Sunxi), token)
                            .ConfigureAwait(false);
                        if (lease is not null && lease.TryGetSession<SunxiDeviceSession>(out var session))
                        {
                            if (session.IsFes)
                            {
                                return (lease, session.Fes);
                            }

                            lastFailure = new InvalidOperationException(
                                $"The device reports mode {session.Mode} instead of FES.");
                        }
                    }
                    catch (Exception exception) when (IsSunxiReconnectFailure(exception))
                    {
                        lastFailure = exception;
                    }
                    finally
                    {
                        if (lease is not null)
                        {
                            CloseLease(lease);
                        }
                    }
                }

                await Task.Delay(SunxiServiceReconnectIntervalMs, token).ConfigureAwait(false);
                await ForceRefreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The device did not answer the FES handshake within " + $"{SunxiServiceReconnectTimeoutMs} ms.",
                new AggregateException(lastFailure ?? new TimeoutException(), error));
        }
    }

    private DetectedDevice? FindSunxiCandidate(DetectedDevice previous, string? physicalPortPath)
    {
        var devices = Scan().Where(candidate => candidate.Protocol == DeviceProtocolKind.Sunxi).ToArray();
        var byKey = devices.FirstOrDefault(candidate => string.Equals(candidate.Key, previous.Key,
            StringComparison.Ordinal));
        if (byKey is not null)
        {
            return byKey;
        }

        if (physicalPortPath is { Length: > 0 })
        {
            var byPort = devices.Where(candidate => string.Equals(candidate.Endpoint?.UsbInfo?.PhysicalPortPath,
                physicalPortPath, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (byPort.Length == 1)
            {
                return byPort[0];
            }
        }

        return devices.Length == 1 ? devices[0] : null;
    }

    private static void RequireFel(SunxiDeviceSession session)
    {
        if (!session.IsFel)
        {
            throw new InvalidOperationException(
                $"The device reports mode {session.Mode} but this operation requires FEL mode.");
        }
    }

    private static bool IsSunxiReconnectFailure(Exception exception) =>
        exception is IOException or TimeoutException or EndOfStreamException or SunxiEfexException
            or InvalidOperationException or NotSupportedException or KeyNotFoundException;
}