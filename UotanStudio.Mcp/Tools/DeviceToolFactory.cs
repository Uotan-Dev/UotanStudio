using System.ComponentModel;
using ModelContextProtocol.Server;
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
/// </summary>
internal static class DeviceToolFactory
{
    internal static IReadOnlyList<McpServerTool> CreateTools(PathSandbox sandbox, IDeviceManagerGateway manager)
    {
        var list = McpServerTool.Create(
            () => DeviceToolHandlers.ListDevicesAsync(manager),
            new McpServerToolCreateOptions
            {
                Name = "list_devices",
                Description = "List USB devices visible to UotanStudio's communication layer "
                    + "(VID/PID, serial, device path, interface info).",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = true,
                Destructive = false
            });

        var adbDevices = McpServerTool.Create(
            () => DeviceToolHandlers.ListAdbDevicesAsync(manager),
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
             [Description("Shell command to execute on the device.")] string command) => DeviceToolHandlers.AdbShellAsync(manager, serial, command),
            new McpServerToolCreateOptions
            {
                Name = "adb_shell",
                Description = "Run a shell command on an adb target via the local adb server. "
                    + "Prefer read-only commands; the command is NOT safety-filtered, so verify "
                    + "it before calling. Reboot/wipe/flash commands need the --allow-flash tools.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = true,
                Destructive = false
            });

        var adbPull = McpServerTool.Create(
            ([Description("Target serial from adb_devices.")] string serial,
             [Description("Remote path on the device.")] string remotePath,
             [Description("Local file path inside the sandbox.")] string localPath) => DeviceToolHandlers.AdbPullAsync(manager, sandbox, serial, remotePath, localPath),
            new McpServerToolCreateOptions
            {
                Name = "adb_pull",
                Description = "Pull a file from an adb target into the sandbox via adb sync.",
                ReadOnly = true,
                Idempotent = false,
                OpenWorld = true,
                Destructive = false
            });

        var adbPush = McpServerTool.Create(
            ([Description("Target serial from adb_devices.")] string serial,
             [Description("Local file path inside the sandbox.")] string localPath,
             [Description("Remote path on the device.")] string remotePath) => DeviceToolHandlers.AdbPushAsync(manager, sandbox, serial, localPath, remotePath),
            new McpServerToolCreateOptions
            {
                Name = "adb_push",
                Description = "Push a local sandbox file to an adb target via adb sync.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = true,
                Destructive = false
            });

        var fastbootVariables = McpServerTool.Create(
            () => DeviceToolHandlers.FastbootVariablesAsync(manager),
            new McpServerToolCreateOptions
            {
                Name = "fastboot_variables",
                Description = "Open a fastboot session over direct USB and read all variables "
                    + "(product, bootloader version, max download size, ...).",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = true,
                Destructive = false
            });

        return [list, adbDevices, adbShell, adbPull, adbPush, fastbootVariables];
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
             [Description("Reboot mode: empty, bootloader, recovery, fastboot, sideload.")] string? mode = null) => DeviceToolHandlers.AdbRebootAsync(manager, serial, mode),
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
             [Description("Wipe the partition before flashing.")] bool wipe = false) => DeviceToolHandlers.FastbootFlashAsync(manager, sandbox, partition, imagePath, wipe),
            new McpServerToolCreateOptions
            {
                Name = "fastboot_flash",
                Description = "Flash an image to a fastboot device over direct USB. Requires --allow-flash.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = true,
                Destructive = true
            });

        var fastbootErase = McpServerTool.Create(
            ([Description("Partition name.")] string partition) => DeviceToolHandlers.FastbootEraseAsync(manager, partition),
            new McpServerToolCreateOptions
            {
                Name = "fastboot_erase",
                Description = "Erase a fastboot partition over direct USB. Requires --allow-flash.",
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
/// Protocol file APIs are synchronous; heavy work is pushed to the thread pool so the MCP
/// transport thread is never blocked.
/// <para>协议层文件 API 是同步的；重活推到线程池执行，避免阻塞 MCP 传输线程。</para>
/// </summary>
internal static class DeviceToolHandlers
{
    public static Task<string> ListDevicesAsync(IDeviceManagerGateway manager)
        => Task.Run(() =>
        {
            var devices = manager.ListUsbDevices()
                .Select(d => new UsbDevicePayload(
                    d.ApiName,
                    d.SourceApiKind.ToString(),
                    d.VendorId,
                    d.ProductId,
                    d.SerialNumber,
                    d.DevicePath,
                    d.DeviceKey,
                    d.InterfaceClass,
                    d.InterfaceSubClass,
                    d.InterfaceProtocol))
                .ToList();
            return ToolJson.Serialize(new ToolResult(true, $"{devices.Count} USB device(s).", ToolJson.Data(devices)));
        });

    public static Task<string> ListAdbDevicesAsync(IDeviceManagerGateway manager)
        => Task.Run(() =>
        {
            var devices = manager.ListAdbTargets()
                .Select(target => new AdbTargetPayload(target.Serial, target.State)).ToList();
            return ToolJson.Serialize(new ToolResult(true, $"{devices.Count} adb target(s).", ToolJson.Data(devices)));
        });

    public static Task<string> AdbShellAsync(IDeviceManagerGateway manager, string serial, string command)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            var output = manager.RunAdbService(serial, "shell:" + command, stream =>
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return System.Text.Encoding.UTF8.GetString(memory.ToArray());
            });
            return ToolJson.Serialize(new ToolResult(true, "Command completed.",
                ToolJson.Data(new AdbShellPayload(serial, command, output))));
        });

    public static Task<string> AdbPullAsync(IDeviceManagerGateway manager, PathSandbox sandbox, string serial, string remotePath, string localPath)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
            var resolvedLocal = sandbox.ResolveOutput(localPath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resolvedLocal))!);
            manager.RunAdbService(serial, "sync:", stream =>
            {
                SendSync(stream, "RECV", System.Text.Encoding.UTF8.GetBytes(remotePath));
                using var output = File.Create(resolvedLocal);
                var header = new byte[8];
                while (true)
                {
                    stream.ReadExactly(header);
                    var id = System.Text.Encoding.ASCII.GetString(header, 0, 4);
                    var length = BitConverter.ToUInt32(header, 4);
                    if (id == "DONE")
                    {
                        break;
                    }
                    if (id == "FAIL")
                    {
                        if (length > 65536) throw new IOException("ADB sync failure message is too large.");
                        var message = new byte[length];
                        stream.ReadExactly(message);
                        throw new IOException("ADB sync pull failed: " + System.Text.Encoding.UTF8.GetString(message));
                    }
                    if (id != "DATA")
                    {
                        throw new IOException("Invalid ADB sync response.");
                    }
                    if (length > 65536) throw new IOException("ADB sync data chunk is too large.");
                    var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)length);
                    try
                    {
                        stream.ReadExactly(buffer.AsSpan(0, (int)length));
                        output.Write(buffer.AsSpan(0, (int)length));
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                return true;
            });
            var info = new FileInfo(resolvedLocal);
            return ToolJson.Serialize(new ToolResult(true, $"Pulled {remotePath}.",
                ToolJson.Data(new AdbFileSyncPayload(serial, remotePath, resolvedLocal, info.Length))));
        });

    public static Task<string> AdbPushAsync(IDeviceManagerGateway manager, PathSandbox sandbox, string serial, string localPath, string remotePath)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
            var resolvedLocal = sandbox.Resolve(localPath, mustExist: true);
            manager.PushAdbFile(serial, resolvedLocal, remotePath);
            var info = new FileInfo(resolvedLocal);
            return ToolJson.Serialize(new ToolResult(true, $"Pushed {remotePath}.",
                ToolJson.Data(new AdbFileSyncPayload(serial, remotePath, resolvedLocal, info.Length))));
        });

    /// <summary>
    /// Writes one adb sync packet (command id, payload length, payload) to the service stream.
    /// <para>向服务流写入一个 adb sync 包（命令 ID、负载长度、负载）。</para>
    /// </summary>
    private static void SendSync(Stream stream, string id, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        System.Text.Encoding.ASCII.GetBytes(id, header);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)data.Length);
        stream.Write(header);
        stream.Write(data);
    }

    public static Task<string> FastbootVariablesAsync(IDeviceManagerGateway manager)
        => Task.Run(() =>
        {
            var variables = manager.RunFastboot(session => session.GetAllVariables());
            return ToolJson.Serialize(new ToolResult(true,
                $"{variables.Count} fastboot variable(s).", ToolJson.Data(variables)));
        });

    public static Task<string> AdbRebootAsync(IDeviceManagerGateway manager, string serial, string? mode)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            manager.RunAdbService(serial, mode is { Length: > 0 } ? $"reboot:{mode}" : "reboot:", _ => true);
            return ToolJson.Serialize(new ToolResult(true,
                string.IsNullOrWhiteSpace(mode) ? "Reboot requested." : $"Reboot to {mode} requested.",
                ToolJson.Data(new AdbRebootPayload(serial, mode))));
        });

    public static Task<string> FastbootFlashAsync(IDeviceManagerGateway manager, PathSandbox sandbox, string partition, string imagePath, bool wipe)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(partition);
            var resolvedImage = sandbox.Resolve(imagePath, mustExist: true);
            return manager.RunFastboot(session =>
            {
                if (wipe) session.ExecuteCommand($"erase:{partition}").ThrowIfFailed();
                var response = session.DownloadFile(resolvedImage);
                if (response.State != FastbootResponseState.Success)
                    return ToolJson.Serialize(new ToolResult(false,
                        $"Download failed: {response.State} {response.Text}"));
                var flash = session.ExecuteCommand($"flash:{partition}");
                return flash.State == FastbootResponseState.Success
                    ? ToolJson.Serialize(new ToolResult(true, $"Flashed {partition}.",
                        ToolJson.Data(new FlashPayload(partition, resolvedImage))))
                    : ToolJson.Serialize(new ToolResult(false,
                        $"Flash failed: {flash.State} {flash.Text}"));
            });
        });

    public static Task<string> FastbootEraseAsync(IDeviceManagerGateway manager, string partition)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(partition);
            return manager.RunFastboot(session =>
            {
                var response = session.ExecuteCommand($"erase:{partition}");
                return response.State == FastbootResponseState.Success
                    ? ToolJson.Serialize(new ToolResult(true, $"Erased {partition}.",
                        ToolJson.Data(new PartitionPayload(partition))))
                    : ToolJson.Serialize(new ToolResult(false,
                        $"Erase failed: {response.State} {response.Text}"));
            });
        });
}