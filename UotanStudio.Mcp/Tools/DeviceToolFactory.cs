using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UotanStudio.Comm.Devices.Discovery;
using UotanStudio.Comm.Protocols.Fastboot;
using IOException = System.IO.IOException;
using Path = System.IO.Path;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the device-domain MCP tools: discovery and read-only operations by default, with
/// flashing/wiping/rebooting tools registered only under <c>--allow-flash</c>.
/// <para>构建设备域 MCP 工具：默认仅注册发现与只读操作；刷写/擦除/重启类工具仅在
/// <c>--allow-flash</c> 下注册。</para>
/// ADB operations go through the local adb server (<see cref="UotanStudio.Comm.Protocols.Adb.AdbServerServices"/>)
/// so the MCP process and the desktop app never fight over direct USB sessions for the same device.
/// <para>ADB 操作走本地 adb server（<see cref="UotanStudio.Comm.Protocols.Adb.AdbServerServices"/>），MCP 进程与桌面应用不会
/// 争抢同一设备的原生 USB 会话。</para>
/// Every delegate ends with the ambient <see cref="CancellationToken"/> the MCP server binds itself, and
/// the flashing tool adds the ambient <see cref="IProgress{T}"/> because its Fastboot download reports byte
/// progress; none of these appear in the emitted <c>inputSchema</c>, so cancellation and progress still
/// reach the communication layer that supports them.
/// <para>每个委托末尾都是 MCP 服务器自行绑定的环境参数 <see cref="CancellationToken"/>，刷写工具因其 Fastboot
/// 下载会上报字节进度而额外带有 <see cref="IProgress{T}"/>；这些参数都不出现在生成的 <c>inputSchema</c> 中，
/// 但取消与进度仍能抵达支持它们的通信层。</para>
/// </summary>
internal static class DeviceToolFactory
{
    internal static IReadOnlyList<McpServerTool> CreateTools(PathSandbox sandbox, IDeviceManagerGateway manager)
    {
        var list = McpServerTool.Create(
            ([Description("Return every endpoint, including ones with no known protocol, plus each OS device path and stable device key.")] bool detailed = false,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => DeviceToolHandlers.ListDevicesAsync(manager, detailed, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "list_devices",
                Description = "List USB endpoints UotanStudio can drive directly, each labelled with the "
                    + "protocol its descriptors identify (adb, fastboot, edl, mtk, rkdl, hdc) and VID/PID, "
                    + "serial and interface codes. The summary carries a per-protocol count and how many "
                    + "endpoints were suppressed. Endpoints the classifier cannot identify (hubs, keyboards, "
                    + "webcams, mice, other console or host-controller interfaces) are omitted by default; "
                    + "pass detailed=true to get the full inventory, which also adds each device path and "
                    + "stable device key. Reuse a listed serial or device key as the target parameter of the "
                    + "fastboot tools when more than one fastboot device is attached.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = true,
                Destructive = false
            });

        var adbDevices = McpServerTool.Create(
            (CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => DeviceToolHandlers.ListAdbDevicesAsync(manager, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "adb_devices",
                Description = "List targets known to the local adb server (serial and state), "
                    + "including offline/unauthorized entries.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = true,
                Destructive = false
            });

        var adbShell = McpServerTool.Create(
            ([Description("Target serial from adb_devices.")] string serial,
             [Description("Shell command to execute on the device.")] string command,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => DeviceToolHandlers.AdbShellAsync(manager, serial, command, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "adb_shell",
                Description = "Run a shell command on an adb target via the local adb server. "
                    + "Prefer read-only commands; the command is NOT safety-filtered, so verify "
                    + "it before calling. Reboot/wipe/flash commands need the --allow-flash tools. "
                    + "Cancelling the request aborts it before the service stream is opened.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = true,
                Destructive = false
            });

        var adbPull = McpServerTool.Create(
            ([Description("Target serial from adb_devices.")] string serial,
             [Description("Remote path on the device.")] string remotePath,
             [Description("Local file path inside the sandbox.")] string localPath,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => DeviceToolHandlers.AdbPullAsync(manager, sandbox, serial, remotePath, localPath, token),
                cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "adb_pull",
                Description = "Pull a file from an adb target into the sandbox via adb sync. The bytes land "
                    + "in a temporary file that is published only after the DONE marker, so cancelling the "
                    + "request leaves no truncated output behind.",
                ReadOnly = true,
                Idempotent = false,
                OpenWorld = true,
                Destructive = false
            });

        var adbPush = McpServerTool.Create(
            ([Description("Target serial from adb_devices.")] string serial,
             [Description("Local file path inside the sandbox.")] string localPath,
             [Description("Remote path on the device.")] string remotePath,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => DeviceToolHandlers.AdbPushAsync(manager, sandbox, serial, localPath, remotePath, token),
                cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "adb_push",
                Description = "Push a local sandbox file to an adb target via adb sync. Cancelling the "
                    + "request before the session opens aborts the upload.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = true,
                Destructive = false
            });

        var fastbootVariables = McpServerTool.Create(
            ([Description("Fastboot serial or device key from list_devices; required only when more than one fastboot device is attached.")] string? target = null,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => DeviceToolHandlers.FastbootVariablesAsync(manager, target, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "fastboot_variables",
                Description = "Open a fastboot session over direct USB and read all variables "
                    + "(product, bootloader version, max download size, ...). With one fastboot device "
                    + "attached the target is chosen automatically; otherwise pass target.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = true,
                Destructive = false
            });

        var qrPairingCode = McpServerTool.Create(
            (CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => DeviceToolHandlers.QrPairingCodeAsync(manager, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "adb_qr_pairing_code",
                Description = "Generate fresh one-time wireless-debugging QR pairing credentials and return "
                    + "the WIFI:T:ADB;... payload text. Render that text as a QR code yourself, have the phone "
                    + "scan it (Settings -> Developer options -> Wireless debugging -> Pair device with QR code), "
                    + "then call adb_pair_qr with the returned serviceName and password. Every call mints new "
                    + "credentials, and a code is only usable by the pairing that follows it.",
                ReadOnly = true,
                Idempotent = false,
                OpenWorld = true,
                Destructive = false
            });

        var adbPairQr = McpServerTool.Create(
            ([Description("Service name returned by adb_qr_pairing_code.")] string serviceName,
             [Description("Pairing password returned by adb_qr_pairing_code.")] string password,
             [Description("Seconds to wait for the phone to appear and pair.")] int timeoutSeconds = 180,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => DeviceToolHandlers.AdbPairQrAsync(manager, serviceName, password, timeoutSeconds, token),
                cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "adb_pair_qr",
                Description = "Wait for the phone to scan the QR code built from adb_qr_pairing_code "
                    + "credentials, then pair and connect it. While a local adb server listens, pairing and "
                    + "connecting both go through that server, so the device appears in adb_devices and the "
                    + "other adb_* tools can address it. With no server listening the toolbox pairs directly "
                    + "over TLS and only reports the host:port: the headless adb_* tools address server-managed "
                    + "or USB endpoints, so that secure direct endpoint is driven by the desktop app instead. "
                    + "This blocks until the phone scans, so call it only after the code is displayed.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = true,
                Destructive = false
            });

        return [list, adbDevices, adbShell, adbPull, adbPush, fastbootVariables, qrPairingCode, adbPairQr];
    }

    /// <summary>
    /// Flash/wipe/reboot tools: only invoked by <see cref="McpServerHost.BuildTools"/> when
    /// <c>--allow-flash</c> is set.
    /// <para>刷写/擦除/重启类工具：仅在 <c>--allow-flash</c> 时由
    /// <see cref="McpServerHost.BuildTools"/> 调用。</para>
    /// </summary>
    internal static IReadOnlyList<McpServerTool> CreateFlashTools(PathSandbox sandbox, IDeviceManagerGateway manager)
    {
        var reboot = McpServerTool.Create(
            ([Description("Target serial from adb_devices.")] string serial,
             [Description("Reboot mode: empty, bootloader, recovery, fastboot, sideload.")] string? mode = null,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => DeviceToolHandlers.AdbRebootAsync(manager, serial, mode, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "adb_reboot",
                Description = "Reboot an adb target (optionally into bootloader/recovery/fastboot/sideload). "
                    + "Requires --allow-flash.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = true,
                Destructive = true
            });

        var fastbootFlash = McpServerTool.Create(
            ([Description("Partition name, e.g. boot, system.")] string partition,
             [Description("Image file path inside the sandbox.")] string imagePath,
             [Description("Wipe the partition before flashing.")] bool wipe = false,
             [Description("Fastboot serial or device key from list_devices; required only when more than one fastboot device is attached.")] string? target = null,
             IProgress<ProgressNotificationValue>? progress = null,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => DeviceToolHandlers.FastbootFlashAsync(manager, sandbox, partition, imagePath, wipe,
                    target, progress, token),
                cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "fastboot_flash",
                Description = "Flash an image to a fastboot device over direct USB. Requires --allow-flash. "
                    + "Send a progress token to receive the download byte count and cancel the request to "
                    + "abort the transfer between chunks; with more than one fastboot device attached the "
                    + "target parameter selects which one is written.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = true,
                Destructive = true
            });

        var fastbootErase = McpServerTool.Create(
            ([Description("Partition name.")] string partition,
             [Description("Fastboot serial or device key from list_devices; required only when more than one fastboot device is attached.")] string? target = null,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => DeviceToolHandlers.FastbootEraseAsync(manager, partition, target, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "fastboot_erase",
                Description = "Erase a fastboot partition over direct USB. Requires --allow-flash. With more "
                    + "than one fastboot device attached the target parameter selects which one is erased.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = true,
                Destructive = true
            });

        return [reboot, fastbootFlash, fastbootErase];
    }
}

/// <summary>
/// Implementations for the device tool delegates; kept separate for direct unit testing.
/// <para>设备工具委托的实现；独立存放便于直接单元测试。</para>
/// Every handler returns the <see cref="ToolResult"/> envelope and accepts the caller's
/// <see cref="CancellationToken"/>, which <see cref="ToolGuard"/> maps onto the protocol result.
/// <para>所有处理器返回 <see cref="ToolResult"/> 信封并接受调用方的 <see cref="CancellationToken"/>，
/// 由 <see cref="ToolGuard"/> 映射为协议结果。</para>
/// Protocol file APIs are synchronous; heavy work is pushed to the thread pool so the MCP
/// transport thread is never blocked.
/// <para>协议层文件 API 是同步的；重活推到线程池执行，避免阻塞 MCP 传输线程。</para>
/// </summary>
internal static class DeviceToolHandlers
{
    /// <summary>
    /// <para>构建经过协议分类的 USB 端点清单；默认丢弃无法分类的端点并统计数量，只有
    /// <paramref name="detailed"/> 为 <see langword="true"/> 时才返回全部端点及设备路径与设备键。</para>
    /// Builds the protocol-classified USB inventory, dropping endpoints the classifier cannot identify by
    /// default and counting them in the summary; only a <paramref name="detailed"/> request returns every
    /// endpoint together with its OS device path and stable device key.
    /// </summary>
    /// <param name="manager"><para>提供清单的设备网关。</para>The device gateway supplying the inventory.</param>
    /// <param name="detailed"><para>是否返回完整清单与路径字段。</para>Whether the full inventory including path fields is requested.</param>
    /// <param name="cancellationToken"><para>每次取用下一个端点前检查的令牌。</para>Token checked before each further endpoint is read.</param>
    /// <returns><para>带按协议计数与丢弃数量的清单结果。</para>The inventory result carrying the per-protocol
    /// counts and the suppressed endpoint count.</returns>
    public static async Task<ToolResult> ListDevicesAsync(
        IDeviceManagerGateway manager, bool detailed, CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            var inventoried = manager.ListUsbDevices();
            var devices = new List<UsbDevicePayload>(inventoried.Count);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var suppressed = 0;
            foreach (var device in inventoried)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var protocol = UsbProtocolClassifier.Classify(device) ?? "unknown";
                if (!detailed && protocol == "unknown")
                {
                    suppressed++;
                    continue;
                }

                devices.Add(new UsbDevicePayload(
                    device.ApiName,
                    device.SourceApiKind.ToString(),
                    device.VendorId,
                    device.ProductId,
                    device.SerialNumber,
                    protocol,
                    device.InterfaceClass,
                    device.InterfaceSubClass,
                    device.InterfaceProtocol,
                    detailed ? device.DevicePath : null,
                    detailed ? device.DeviceKey : null));
                counts[protocol] = counts.TryGetValue(protocol, out var seen) ? seen + 1 : 1;
            }

            var breakdown = counts.Count == 0
                ? string.Empty
                : " " + string.Join(", ", counts.Select(pair => $"{pair.Key}:{pair.Value}").Order());
            string note;
            if (suppressed > 0)
            {
                note = $" {suppressed} unclassifiable endpoint(s) (hubs, keyboards, webcams and "
                    + "similar interfaces) suppressed; pass detailed=true for the full inventory.";
            }
            else if (detailed)
            {
                note = " Full inventory including device paths and keys.";
            }
            else
            {
                note = string.Empty;
            }

            return new ToolResult(true,
                $"{devices.Count} USB device(s).{breakdown}{note}", ToolJson.Data(devices));
        }, cancellationToken).ConfigureAwait(false);

    public static async Task<ToolResult> ListAdbDevicesAsync(IDeviceManagerGateway manager,
        CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var devices = manager.ListAdbTargets()
                .Select(target => new AdbTargetPayload(target.Serial, target.State)).ToList();
            return new ToolResult(true, $"{devices.Count} adb target(s).", ToolJson.Data(devices));
        }, cancellationToken).ConfigureAwait(false);

    public static async Task<ToolResult> AdbShellAsync(IDeviceManagerGateway manager, string serial,
        string command, CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            var output = manager.RunAdbService(serial, "shell:" + command, stream =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return System.Text.Encoding.UTF8.GetString(memory.ToArray());
            }, cancellationToken);
            return new ToolResult(true, "Command completed.",
                ToolJson.Data(new AdbShellPayload(serial, command, output)));
        }, cancellationToken).ConfigureAwait(false);

    public static async Task<ToolResult> AdbPullAsync(IDeviceManagerGateway manager, PathSandbox sandbox,
        string serial, string remotePath, string localPath, CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
            var resolvedLocal = sandbox.ResolveOutput(localPath);
            var directory = Path.GetDirectoryName(resolvedLocal)!;
            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(directory, $"{Path.GetFileName(resolvedLocal)}.{Guid.NewGuid():N}.part");
            try
            {
                manager.PullAdbFile(serial, remotePath, tempPath, cancellationToken);
                File.Move(tempPath, resolvedLocal, overwrite: true);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                throw;
            }

            var info = new FileInfo(resolvedLocal);
            return new ToolResult(true, $"Pulled {remotePath}.",
                ToolJson.Data(new AdbFileSyncPayload(serial, remotePath, resolvedLocal, info.Length)));
        }, cancellationToken).ConfigureAwait(false);

    public static async Task<ToolResult> AdbPushAsync(IDeviceManagerGateway manager, PathSandbox sandbox,
        string serial, string localPath, string remotePath, CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
            var resolvedLocal = sandbox.Resolve(localPath, mustExist: true);
            manager.PushAdbFile(serial, resolvedLocal, remotePath, cancellationToken);
            var info = new FileInfo(resolvedLocal);
            return new ToolResult(true, $"Pushed {remotePath}.",
                ToolJson.Data(new AdbFileSyncPayload(serial, remotePath, resolvedLocal, info.Length)));
        }, cancellationToken).ConfigureAwait(false);

    public static async Task<ToolResult> QrPairingCodeAsync(IDeviceManagerGateway manager,
        CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (serviceName, password, payload) = manager.CreateQrPairingSession();
            return new ToolResult(true, payload, ToolJson.Data(new QrPairingCodePayload(
                serviceName, password, payload)));
        }, cancellationToken).ConfigureAwait(false);

    public static async Task<ToolResult> AdbPairQrAsync(IDeviceManagerGateway manager, string serviceName,
        string password, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var (route, address) = await manager.PairQrWirelessAsync(serviceName, password, timeoutSeconds,
            cancellationToken).ConfigureAwait(false);
        return new ToolResult(true, $"Paired through the {route} route; connected to {address}.",
            ToolJson.Data(new QrPairingResultPayload(route, address)));
    }

    public static async Task<ToolResult> FastbootVariablesAsync(IDeviceManagerGateway manager, string? target,
        CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            var variables = manager.RunFastboot(target, session => session.GetAllVariables(), cancellationToken);
            return new ToolResult(true,
                $"{variables.Count} fastboot variable(s).", ToolJson.Data(variables));
        }, cancellationToken).ConfigureAwait(false);

    public static async Task<ToolResult> AdbRebootAsync(IDeviceManagerGateway manager, string serial,
        string? mode, CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            manager.RunAdbService(serial, mode is { Length: > 0 } ? $"reboot:{mode}" : "reboot:",
                _ => true, cancellationToken);
            return new ToolResult(true,
                string.IsNullOrWhiteSpace(mode) ? "Reboot requested." : $"Reboot to {mode} requested.",
                ToolJson.Data(new AdbRebootPayload(serial, mode)));
        }, cancellationToken).ConfigureAwait(false);

    public static async Task<ToolResult> FastbootFlashAsync(IDeviceManagerGateway manager, PathSandbox sandbox,
        string partition, string imagePath, bool wipe, string? target,
        IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(partition);
            var resolvedImage = sandbox.Resolve(imagePath, mustExist: true);
            return manager.RunFastboot(target, session =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (wipe)
                {
                    session.ExecuteCommand($"erase:{partition}").ThrowIfFailed();
                }

                var response = session.DownloadFile(resolvedImage, ToByteProgress(progress), cancellationToken);
                if (response.State != FastbootResponseState.Success)
                {
                    return new ToolResult(false, $"Download failed: {response.State} {response.Text}");
                }

                var flash = session.ExecuteCommand($"flash:{partition}");
                return flash.State == FastbootResponseState.Success
                    ? new ToolResult(true, $"Flashed {partition}.",
                        ToolJson.Data(new FlashPayload(partition, resolvedImage)))
                    : new ToolResult(false, $"Flash failed: {flash.State} {flash.Text}");
            }, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);

    public static async Task<ToolResult> FastbootEraseAsync(IDeviceManagerGateway manager, string partition,
        string? target, CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(partition);
            return manager.RunFastboot(target, session =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = session.ExecuteCommand($"erase:{partition}");
                return response.State == FastbootResponseState.Success
                    ? new ToolResult(true, $"Erased {partition}.",
                        ToolJson.Data(new PartitionPayload(partition)))
                    : new ToolResult(false, $"Erase failed: {response.State} {response.Text}");
            }, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// <para>把协议进度目标转为 Fastboot 下载的字节进度接收端；没有进度令牌时返回
    /// <see langword="null"/>，下载路径因此不产生上报开销。</para>
    /// Converts the protocol progress sink into the Fastboot download's byte-count sink; without a progress
    /// token this returns <see langword="null"/> so the download skips reporting overhead.
    /// </summary>
    /// <param name="progress"><para>MCP 绑定的进度实例，可能为 <see langword="null"/>。</para>The MCP-bound progress instance, possibly <see langword="null"/>.</param>
    /// <returns><para>供 Fastboot 下载上报累计字节数的接收端。</para>A cumulative byte sink for the Fastboot download.</returns>
    private static IProgress<long>? ToByteProgress(IProgress<ProgressNotificationValue>? progress) =>
        progress is null
            ? null
            : new Progress<long>(bytes => progress.Report(new ProgressNotificationValue
            {
                Progress = bytes,
                Message = $"{bytes} byte(s) sent"
            }));
}