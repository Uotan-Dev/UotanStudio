using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Unisoc;

namespace UotanStudio.Services.Devices;

/// <summary>
/// One UNISOC download endpoint as the loader-stage handshake reported it.
/// <para>加载阶段握手上报的一个紫光展锐下载端点。</para>
/// </summary>
/// <param name="Stage">Loader stage the handshake identified. <para>握手识别出的加载阶段。</para></param>
/// <param name="Version">Raw version reply payload, when one was received.
/// <para>收到的版本应答原文；未收到时为 <see langword="null"/>。</para></param>
/// <param name="SecureBoot">Reports an unprotected Boot ROM reply (<c>SPRD3</c>) as <see langword="false"/>.
/// <para>未锁定 Boot ROM 的应答（<c>SPRD3</c>）为 <see langword="false"/>。</para></param>
public sealed record UnisocDeviceStatus(UnisocBootStage Stage, byte[]? Version, bool SecureBoot)
{
    /// <summary>Version text decoded from the reply, or the stage name when no reply arrived.
    /// <para>由应答解码的版本文字；没有应答时为阶段名。</para></summary>
    public string VersionText => Version is { } bytes && bytes.Length > 0
        ? System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0')
        : Stage.ToString();
}

/// <summary>
/// One partition a UNISOC read-back pulls: its name, byte range and the destination file the bytes land in.
/// <para>紫光展锐回读拉取的一个分区：名称、字节范围与字节落盘的目标文件。</para>
/// </summary>
/// <param name="Name">Partition name the progress reports carry. <para>进度上报携带的分区名。</para></param>
/// <param name="Offset">Start offset inside the partition. <para>分区内的起始偏移。</para></param>
/// <param name="Length">Byte count to read. <para>要读取的字节数。</para></param>
/// <param name="DestinationPath">File the read bytes are moved into. <para>读取字节最终移动到的文件。</para></param>
public sealed record UnisocPartitionReadRequest(string Name, long Offset, long Length, string DestinationPath);

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// Runs one operation on a leased UNISOC endpoint; the session resolves the loader stage from the handshake.
    /// <para>在已租用的紫光展锐端点上执行一次操作；会话由握手确定加载阶段。</para>
    /// </summary>
    /// <typeparam name="TResult">Result of the operation. <para>操作的结果。</para></typeparam>
    /// <param name="device">The selected UNISOC endpoint. <para>所选紫光展锐端点。</para></param>
    /// <param name="operation">Work to run over the session and its lease. <para>经会话及其租约执行的工作。</para></param>
    /// <param name="cancellationToken">Cancels discovery, opening and the operation.
    /// <para>取消发现、打开与操作。</para></param>
    /// <returns>The value produced by <paramref name="operation"/>. <para><paramref name="operation"/> 产生的值。</para></returns>
    /// <exception cref="ArgumentException">The device is not a UNISOC endpoint.
    /// <para>设备不是紫光展锐端点。</para></exception>
    /// <exception cref="IOException">The endpoint does not expose a UNISOC session.
    /// <para>端点未提供紫光展锐会话。</para></exception>
    internal async Task<TResult> RunUnisocAsync<TResult>(DetectedDevice device,
        Func<UnisocDeviceSession, DeviceLease, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Protocol != DeviceProtocolKind.Unisoc)
        {
            throw new ArgumentException("A UNISOC target is required.", nameof(device));
        }

        var lease = LeaseEndpoint(device, sessionKind: DeviceSessionKind.Unisoc,
            cancellationToken: cancellationToken);
        try
        {
            if (!lease.TryGetSession<UnisocDeviceSession>(out var session))
            {
                throw new IOException("The selected device does not expose a UNISOC session.");
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
    /// Reports the loader-stage identity the session handshake captured; no extra device command is issued.
    /// <para>上报会话握手捕获的加载阶段身份；不再发出额外设备命令。</para>
    /// </summary>
    /// <param name="device">The selected UNISOC endpoint. <para>所选紫光展锐端点。</para></param>
    /// <param name="cancellationToken">Cancels the query. <para>取消本次查询。</para></param>
    /// <returns>The handshake identity. <para>握手身份。</para></returns>
    internal Task<UnisocDeviceStatus> QueryUnisocStatusAsync(DetectedDevice device,
        CancellationToken cancellationToken = default) =>
        RunUnisocAsync(device, (session, _, _) =>
            Task.FromResult(new UnisocDeviceStatus(session.Stage, session.Identity.Version,
                session.Identity.SecureBoot)), cancellationToken);

    /// <summary>
    /// Reads the chip UID. <para>读取芯片 UID。</para>
    /// </summary>
    /// <param name="device">The selected UNISOC endpoint. <para>所选紫光展锐端点。</para></param>
    /// <param name="cancellationToken">Cancels the query. <para>取消本次查询。</para></param>
    /// <returns>The raw UID bytes. <para>原始 UID 字节。</para></returns>
    internal Task<byte[]> ReadUnisocChipUidAsync(DetectedDevice device,
        CancellationToken cancellationToken = default) =>
        RunUnisocAsync(device, (session, _, token) => session.ReadChipUidAsync(token), cancellationToken);

    /// <summary>
    /// Resolves the partitions the device exposes: the validated <c>user_partition</c> GPT when present, otherwise
    /// the native table whose size values carry the device's own unit.
    /// <para>解析设备暴露的分区：有校验通过的 <c>user_partition</c> GPT 时用它，否则用原生表（其大小字段单位由
    /// 设备定义）。</para>
    /// </summary>
    /// <param name="device">The selected UNISOC endpoint; it must be connected to FDL2.
    /// <para>所选紫光展锐端点；必须已连接到 FDL2。</para></param>
    /// <param name="cancellationToken">Cancels the resolution. <para>取消解析。</para></param>
    /// <returns>The resolved partition targets. <para>解析出的分区目标。</para></returns>
    internal Task<IReadOnlyList<UnisocPartitionTarget>> ResolveUnisocPartitionsAsync(DetectedDevice device,
        CancellationToken cancellationToken = default) =>
        RunUnisocAsync(device, (session, _, token) => new UnisocFlasher(session).ResolvePartitionsAsync(token),
            cancellationToken);

    /// <summary>
    /// Reads the requested partitions in one lease: every request streams its range into a temporary sibling of
    /// its destination file and moves the file into place only after the whole range arrived, so a cancelled or
    /// failed run never leaves a partial image behind.
    /// <para>在同一次租约内读取请求的分区：每个请求把区间流式写入目标文件的临时同目录文件，整段到达后才移动
    /// 到位，因此取消或失败的运行不会留下半截镜像。</para>
    /// </summary>
    /// <param name="device">The selected UNISOC endpoint; it must be connected to FDL2.
    /// <para>所选紫光展锐端点；必须已连接到 FDL2。</para></param>
    /// <param name="requests">Partitions to pull, with their byte range and destination file.
    /// <para>待拉取的分区，含字节范围与目标文件。</para></param>
    /// <param name="progress">Optional per-partition byte progress. <para>可选的逐分区字节进度。</para></param>
    /// <param name="cancellationToken">Cancels the reads and the moves. <para>取消读取与移动。</para></param>
    internal async Task ReadUnisocPartitionsAsync(DetectedDevice device,
        IReadOnlyList<UnisocPartitionReadRequest> requests, IProgress<UnisocFlashProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return;
        }

        await RunUnisocAsync(device, async (session, _, token) =>
        {
            var storage = session.OpenStorage();
            foreach (var request in requests)
            {
                token.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath)) ?? string.Empty;
                var temporary = Path.Combine(directory,
                    $".{Path.GetFileName(request.DestinationPath)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    progress?.Report(new UnisocFlashProgress(UnisocFlashPhase.Flash, request.Name, 0,
                        request.Length));
                    await using (var output = File.Create(temporary))
                    {
                        var read = await storage.ReadPartitionAsync(request.Name, request.Offset, request.Length,
                            output, token).ConfigureAwait(false);
                        progress?.Report(new UnisocFlashProgress(UnisocFlashPhase.Flash, request.Name, read,
                            request.Length));
                        if (read < request.Length)
                        {
                            progress?.Report(new UnisocFlashProgress(UnisocFlashPhase.Skip, request.Name, read,
                                request.Length, $"Partition '{request.Name}' ended after {read} bytes."));
                        }
                    }

                    File.Move(temporary, request.DestinationPath, overwrite: true);
                }
                catch
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                    {
                    }

                    throw;
                }
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one PAC package against the endpoint: the plan's loaders bring the link to FDL2, the selected erase and
    /// partition scope is applied, and the completion mode runs at the end.
    /// <para>在端点上运行一个 PAC 包：计划的加载器把链路提升到 FDL2，应用所选的擦除与分区范围，最后执行收尾模式。</para>
    /// </summary>
    /// <param name="device">The selected UNISOC endpoint. <para>所选紫光展锐端点。</para></param>
    /// <param name="packagePath">PAC package to write. <para>待写入的 PAC 包。</para></param>
    /// <param name="options">Flash scope, confirmation and completion mode.
    /// <para>烧录范围、确认短语与完成模式。</para></param>
    /// <param name="selectedNames">Partition names to write; <see langword="null"/> writes every matching image.
    /// <para>要写入的分区名；<see langword="null"/> 表示写入全部匹配镜像。</para></param>
    /// <param name="progress">Optional progress sink. <para>可选的进度接收器。</para></param>
    /// <param name="cancellationToken">Cancels opening, the plan load and the run.
    /// <para>取消打开、计划加载与运行。</para></param>
    /// <exception cref="FileNotFoundException">The package does not exist. <para>包不存在。</para></exception>
    internal async Task FlashUnisocAsync(DetectedDevice device, string packagePath, UnisocFlashOptions options,
        IReadOnlyList<string>? selectedNames, IProgress<UnisocFlashProgress>? progress,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? externalImages = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        if (device.Protocol != DeviceProtocolKind.Unisoc)
        {
            throw new ArgumentException("A UNISOC target is required.", nameof(device));
        }

        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException($"The UNISOC package '{packagePath}' does not exist.", packagePath);
        }

        CancelBackgroundEndpointWork(device);
        await RunUnisocAsync(device, async (session, _, token) =>
        {
            using var plan = await Task.Run(() => UnisocFlashPlan.Load(packagePath, token), token)
                .ConfigureAwait(false);
            await new UnisocFlasher(session, progress)
                .ExecuteAsync(plan, options, selectedNames, token, externalImages)
                .ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }
}