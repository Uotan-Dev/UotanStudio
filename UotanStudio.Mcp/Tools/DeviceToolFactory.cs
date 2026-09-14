using System.ComponentModel;
using ModelContextProtocol.Server;
using UotanStudio.Comm;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Core;
using IOException = System.IO.IOException;
using Path = System.IO.Path;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the device-domain MCP tools: discovery and read-only operations by default, with
/// flashing/wiping/rebooting tools registered only under <c>--allow-flash</c>.
/// <para>构建设备域 MCP 工具：默认仅注册发现与只读操作；刷写/擦除/重启类工具仅在
/// <c>--allow-flash</c> 下注册。</para>
/// ADB operations go through the local adb server (<see cref="AdbServerServices"/>) so the MCP
/// process and the desktop app never fight over direct USB sessions for the same device.
/// <para>ADB 操作走本地 adb server（<see cref="AdbServerServices"/>），MCP 进程与桌面应用不会
/// 争抢同一设备的原生 USB 会话。</para>
/// </summary>
internal static class DeviceToolFactory
{
    internal static IReadOnlyList<McpServerTool> CreateTools(PathSandbox sandbox)
    {
        var list = McpServerTool.Create(
            () => DeviceToolHandlers.ListDevicesAsync(),
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
            () => DeviceToolHandlers.ListAdbDevicesAsync(),
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
             [Description("Shell command to execute on the device.")] string command => DeviceToolHandlers.AdbShellAsync(serial, command),
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
             [Description("Local file path inside the sandbox.")] string localPath => DeviceToolHandlers.AdbPullAsync(sandbox, serial, remotePath, localPath),
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
             [Description("Remote path on the device.")] string remotePath => DeviceToolHandlers.AdbPushAsync(sandbox, serial, localPath, remotePath),
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
            () => DeviceToolHandlers.FastbootVariablesAsync(),
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
    internal static IReadOnlyList<McpServerTool> CreateFlashTools(PathSandbox sandbox)
    {
        var reboot = McpServerTool.Create(
            ([Description("Target serial from adb_devices.")] string serial,
             [Description("Reboot mode: empty, bootloader, recovery, fastboot, sideload.")] string? mode = null) => DeviceToolHandlers.AdbRebootAsync(serial, mode),
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
             [Description("Wipe the partition before flashing.")] bool wipe = false) => DeviceToolHandlers.FastbootFlashAsync(sandbox, partition, imagePath, wipe),
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
            ([Description("Partition name.")] string partition => DeviceToolHandlers.FastbootEraseAsync(partition),
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
    private static readonly CommKit Comm = new();

    public static Task<string> ListDevicesAsync()
        => Task.Run(() =>
        {
            var devices = Comm.EnumerateUsbDevices(UsbApiKind.Auto)
                .Select(d => new
                {
                    api = d.ApiName,
                    sourceApiKind = d.SourceApiKind.ToString(),
                    vendorId = d.VendorId,
                    productId = d.ProductId,
                    serial = d.SerialNumber,
                    devicePath = d.DevicePath,
                    deviceKey = d.DeviceKey,
                    interfaceClass = d.InterfaceClass,
                    interfaceSubClass = d.InterfaceSubClass,
                    interfaceProtocol = d.InterfaceProtocol
                })
                .ToList();
            return ToolJson.Serialize(new ToolResult(true, $"{devices.Count} USB device(s).", devices));
        });

    public static Task<string> ListAdbDevicesAsync()
        => Task.Run(() =>
        {
            // host:devices-l needs no target selection; states include offline/unauthorized.
            // host:devices-l 无需选择目标；状态包含 offline/unauthorized。
            var devices = new List<object>();
            using (var stream = new AdbServerServices("unused").Open("host:devices-l"))
            using (var reader = new StreamReader(stream))
            {
                // First line is the hex length header emitted by the adb server.
                // 第一行是 adb server 输出的十六进制长度头。
                if (string.IsNullOrWhiteSpace(reader.ReadLine()))
                {
                    return ToolJson.Serialize(new ToolResult(true, "0 adb target(s).", devices));
                }

                while (reader.ReadLine() is { } line)
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]))
                    {
                        devices.Add(new { serial = parts[0], state = parts[1] });
                    }
                }
            }
            return ToolJson.Serialize(new ToolResult(true, $"{devices.Count} adb target(s).", devices));
        });

    public static Task<string> AdbShellAsync(string serial, string command)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            var services = new AdbServerServices(serial);
            services.ValidateTarget();
            using var stream = services.Open("shell:" + command);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var output = System.Text.Encoding.UTF8.GetString(memory.ToArray());
            return ToolJson.Serialize(new ToolResult(true, "Command completed.", new { serial, command, output }));
        });

    public static Task<string> AdbPullAsync(PathSandbox sandbox, string serial, string remotePath, string localPath)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
            var resolvedLocal = sandbox.ResolveOutput(localPath);
            var services = new AdbServerServices(serial);
            services.ValidateTarget();
            // Inline adb sync RECV over the service stream (mirrors AdbServerServices.Push).
            // 在服务流上内联实现 adb sync RECV（与 AdbServerServices.Push 对称）。
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resolvedLocal))!);
            using (var stream = services.Open("sync:"))
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
                        var message = new byte[length];
                        stream.ReadExactly(message);
                        throw new IOException("ADB sync pull failed: " + System.Text.Encoding.UTF8.GetString(message));
                    }
                    if (id != "DATA")
                    {
                        throw new IOException("Invalid ADB sync response.");
                    }
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
            }
            var info = new FileInfo(resolvedLocal);
            return ToolJson.Serialize(new ToolResult(true, $"Pulled {remotePath}.",
                new { serial, remotePath, local = resolvedLocal, bytes = info.Length }));
        });

    public static Task<string> AdbPushAsync(PathSandbox sandbox, string serial, string localPath, string remotePath)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
            var resolvedLocal = sandbox.Resolve(localPath, mustExist: true);
            var services = new AdbServerServices(serial);
            services.ValidateTarget();
            services.Push(resolvedLocal, remotePath);
            var info = new FileInfo(resolvedLocal);
            return ToolJson.Serialize(new ToolResult(true, $"Pushed {remotePath}.",
                new { serial, remotePath, local = resolvedLocal, bytes = info.Length }));
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

    public static Task<string> FastbootVariablesAsync()
        => Task.Run(() =>
        {
            using var session = OpenFastbootSession();
            var variables = session.GetAllVariables();
            return ToolJson.Serialize(new ToolResult(true,
                $"{variables.Count} fastboot variable(s).", variables));
        });

    public static Task<string> AdbRebootAsync(string serial, string? mode)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            var services = new AdbServerServices(serial);
            services.ValidateTarget();
            using var stream = services.Open(mode is { Length: > 0 } ? $"reboot:{mode}" : "reboot:");
            return ToolJson.Serialize(new ToolResult(true,
                string.IsNullOrWhiteSpace(mode) ? "Reboot requested." : $"Reboot to {mode} requested.",
                new { serial, mode }));
        });

    public static Task<string> FastbootFlashAsync(PathSandbox sandbox, string partition, string imagePath, bool wipe)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(partition);
            var resolvedImage = sandbox.Resolve(imagePath, mustExist: true);
            using var session = OpenFastbootSession();
            if (wipe)
            {
                session.ExecuteCommand($"erase:{partition}");
            }
            var response = session.DownloadFile(resolvedImage);
            if (response.State != FastbootResponseState.Success)
            {
                return ToolJson.Serialize(new ToolResult(false,
                    $"Download failed: {response.State} {response.Text}"));
            }
            var flash = session.ExecuteCommand($"flash:{partition}");
            return flash.State == FastbootResponseState.Success
                ? ToolJson.Serialize(new ToolResult(true, $"Flashed {partition}.",
                    new { partition, image = resolvedImage }))
                : ToolJson.Serialize(new ToolResult(false,
                    $"Flash failed: {flash.State} {flash.Text}"));
        });

    public static Task<string> FastbootEraseAsync(string partition)
        => Task.Run(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(partition);
            using var session = OpenFastbootSession();
            var response = session.ExecuteCommand($"erase:{partition}");
            return response.State == FastbootResponseState.Success
                ? ToolJson.Serialize(new ToolResult(true, $"Erased {partition}.", new { partition }))
                : ToolJson.Serialize(new ToolResult(false,
                    $"Erase failed: {response.State} {response.Text}"));
        });

    /// <summary>
    /// Opens the first matching fastboot USB device as a session; released via dispose.
    /// <para>打开第一个匹配的 fastboot USB 设备会话；通过 Dispose 释放。</para>
    /// </summary>
    private static FastbootSession OpenFastbootSession()
    {
        var factory = new FastbootFactory();
        if (!factory.TryCreate(Comm.OpenUsbDeviceSession(UsbApiKind.Auto, FastbootFilter()), out var session)
            || session is not FastbootSession fastboot)
        {
            throw new InvalidOperationException("No fastboot device found.");
        }
        return fastboot;
    }

    /// <summary>
    /// Fastboot matches the Google interface class/subclass/protocol triplet.
    /// <para>fastboot 按 Google 接口 class/subclass/protocol 三元组匹配。</para>
    /// </summary>
    private static UsbDeviceFilter FastbootFilter() => new()
    {
        InterfaceClass = 0xFF,
        InterfaceSubClass = 0x42,
        InterfaceProtocol = 0x03
    };
}