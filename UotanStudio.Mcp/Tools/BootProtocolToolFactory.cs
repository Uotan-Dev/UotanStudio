using System.ComponentModel;
using ModelContextProtocol.Server;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the read-only MediaTek boot-protocol MCP tools.
/// <para>构建只读的联发科 boot 协议 MCP 工具。</para>
/// Every tool here borrows one device transaction through <see cref="IBootProtocolGateway"/> and sends
/// only commands that leave target contents untouched: the read-only MediaTek boot handshake, and the
/// boot-stage read loop over the Boot ROM or preloader command stage. Nothing in this factory uploads a
/// MediaTek download agent, so no tool of this family can execute code on the target; a read that would
/// need one first is refused with a structured error instead of being driven forward.
/// <para>本工厂的每个工具都只经 <see cref="IBootProtocolGateway"/> 借用一次设备事务，且只发送不改动目标内容
/// 的命令：只读的联发科 boot 握手，以及沿 Boot ROM 或 preloader 命令阶段的 boot 阶段读取循环。本工厂
/// 绝不上传联发科下载代理，因此这一族的任何工具都无法在目标上执行代码；需要先前置这些动作的读取会被拒绝
/// 并返回结构化错误，而不是被继续推进。</para>
/// </summary>
internal static class BootProtocolToolFactory
{
    internal static McpServerTool CreateMtkIdentify(IDeviceManagerGateway manager) =>
        McpServerTool.Create(
            ([Description("MTK serial or device key from list_devices; required only when more than one MTK device is attached.")] string? target = null,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => BootProtocolToolHandlers.MtkIdentifyAsync(manager, target, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "mtk_identify",
                Description = "Run the MediaTek boot handshake and report the stage and identity it returns: "
                    + "the descriptor-level USB mode (boot ROM, preloader, DA runtime, logging-only), the "
                    + "hardware code and subcode, the hardware and software versions, the raw "
                    + "target-configuration word with its secure boot, serial-link authentication, "
                    + "download-agent authentication and root-certificate flags decoded, and the bootloader "
                    + "and Boot ROM version bytes together with the MEID and SoC identifier when the target "
                    + "answers them. No watchdog change, memory write, authentication bypass or reset is "
                    + "performed.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = true,
                Destructive = false
            });

    internal static McpServerTool CreateMtkPeek(IDeviceManagerGateway manager) =>
        McpServerTool.Create(
            ([Description("32-bit address to read, decimal or 0x hexadecimal.")] long address,
             [Description("Byte count, between 1 and 4096.")] int lengthBytes,
             [Description("MTK serial or device key from list_devices; required only when more than one MTK device is attached.")] string? target = null,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => BootProtocolToolHandlers.MtkPeekAsync(manager, target, address, lengthBytes, token),
                cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "mtk_peek",
                Description = "Read at most 4096 bytes through the Boot ROM or preloader command stage of a "
                    + "MediaTek device and return them as hexadecimal with their SHA-256. The window is "
                    + "bounds-checked before the first command byte is sent. This is the boot-stage read "
                    + "path only: a download-agent memory peek needs the agent uploaded and started first, "
                    + "which executes code on the target, so this server never performs it.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = true,
                Destructive = false
            });
}

/// <summary>
/// Implementations for the boot-protocol tool delegates; kept separate for direct unit testing.
/// <para>boot 协议工具委托的实现；独立存放便于直接单元测试。</para>
/// Every handler bounds-checks caller- and device-supplied numbers before allocating or reading, threads
/// the caller's <see cref="CancellationToken"/> through the transaction, and refuses a host gateway that
/// does not own boot-protocol access with a structured error. Device-access and protocol failures are
/// left to <see cref="ToolGuard"/>, which reports the cause on the protocol error channel and never
/// retries a transaction.
/// <para>所有处理器都会先对调用方与设备给出的数值做边界校验，再把调用方的 <see cref="CancellationToken"/>
/// 贯穿整个事务，并对未持有 boot 协议访问权的宿主网关返回结构化错误。设备访问与协议失败交由
/// <see cref="ToolGuard"/> 上报原因到协议错误通道，绝不重试事务。</para>
/// </summary>
internal static class BootProtocolToolHandlers
{
    /// <summary>
    /// Strict ceiling on one peek window returned inline as hexadecimal text.
    /// <para>单次以十六进制文本内联返回的 peek 窗口的严格字节上限。</para>
    /// </summary>
    /// <value>Four kibibytes. <para>4096 字节。</para></value>
    internal const int MaxPeekBytes = 4096;

    private const string UnsupportedGatewayNotice =
        "This MCP host's device gateway does not own EDL or MTK device access, so the boot-protocol tools "
        + "cannot lease the endpoint; run the standalone UotanStudio MCP server instead.";

    /// <summary>
    /// <para>执行联发科 boot 握手并回报阶段、硬件码与目标配置。</para>
    /// <para>Runs the MediaTek boot handshake and reports the stage, hardware code and target configuration.</para>
    /// </summary>
    /// <param name="manager"><para>提供端点租用的设备网关。</para>The device gateway offering the lease.</param>
    /// <param name="target"><para>调用方给出的 serial 或设备键。</para>The caller-supplied serial or device key.</param>
    /// <param name="cancellationToken"><para>贯穿每个命令字段的令牌。</para>Token threaded through every command field.</param>
    /// <returns><para>带身份与安全标志的结果信封。</para>The envelope carrying the identity and security flags.</returns>
    public static async Task<ToolResult> MtkIdentifyAsync(IDeviceManagerGateway manager, string? target,
        CancellationToken cancellationToken)
    {
        if (manager is not IBootProtocolGateway gateway)
        {
            return new ToolResult(false, UnsupportedGatewayNotice);
        }

        return await gateway.RunMtkAsync(target, async (session, token) =>
        {
            var device = await session.InitializeAsync(token).ConfigureAwait(false);
            if (device.Meid is { Length: > MaxPeekBytes } || device.Socid is { Length: > MaxPeekBytes })
            {
                return new ToolResult(false,
                    "The target declared an oversized boot identifier; no identifier is reported.");
            }

            return new ToolResult(true,
                $"MTK hardware 0x{device.HardwareCode:X4}, target configuration 0x{device.TargetConfiguration:X8}, "
                + $"stage {session.State} on a {session.DiscoveredUsbMode?.Mode.ToString() ?? "unknown"} endpoint.",
                ToolJson.Data(new MtkChipPayload(
                    target,
                    session.State.ToString(),
                    session.UploadStage.ToString(),
                    session.DiscoveredUsbMode?.Mode.ToString() ?? "Unknown",
                    session.DiscoveredUsbMode?.Reason,
                    device.HardwareCode,
                    device.HardwareSubCode,
                    device.HardwareVersion,
                    device.SoftwareVersion,
                    device.TargetConfiguration,
                    device.SecureBootEnabled,
                    device.SerialLinkAuthenticationRequired,
                    device.DownloadAgentAuthenticationRequired,
                    device.RootCertificateRequired,
                    device.BlVersion,
                    device.BromVersion,
                    ToHex(device.Meid),
                    ToHex(device.Socid))));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>在 boot 命令阶段读取有界内存窗口，不加载下载代理。</para>
    /// <para>Reads a bounded memory window over the boot command stage without loading a download agent.</para>
    /// </summary>
    /// <param name="manager"><para>提供端点租用的设备网关。</para>The device gateway offering the lease.</param>
    /// <param name="target"><para>调用方给出的 serial 或设备键。</para>The caller-supplied serial or device key.</param>
    /// <param name="address"><para>32 位窗口的起始地址。</para>The start address of the 32-bit window.</param>
    /// <param name="lengthBytes"><para>请求的字节数。</para>The requested byte count.</param>
    /// <param name="cancellationToken"><para>贯穿每条读取命令的令牌。</para>Token threaded through every read command.</param>
    /// <returns><para>带十六进制与摘要的结果信封；校验失败时不发送任何命令字节。</para>The envelope with the
    /// hexadecimal bytes and their digest; a validation failure sends no command byte at all.</returns>
    public static async Task<ToolResult> MtkPeekAsync(IDeviceManagerGateway manager, string? target,
        long address, int lengthBytes, CancellationToken cancellationToken)
    {
        if (manager is not IBootProtocolGateway gateway)
        {
            return new ToolResult(false, UnsupportedGatewayNotice);
        }

        if (lengthBytes is < 1 or > MaxPeekBytes)
        {
            return new ToolResult(false,
                $"The MTK peek length must be between 1 and {MaxPeekBytes} bytes; longer windows are not "
                + "returned inline.");
        }

        if (address is < 0 or > uint.MaxValue)
        {
            return new ToolResult(false, "The MTK peek address must stay inside the 32-bit address space.");
        }

        return await gateway.RunMtkAsync(target, async (session, token) =>
        {
            await session.InitializeAsync(token).ConfigureAwait(false);
            var data = await session.PeekPreloaderMemoryAsync((uint)address, lengthBytes, token)
                .ConfigureAwait(false);
            if (data.LongLength != lengthBytes)
            {
                return new ToolResult(false,
                    $"The boot stage returned {data.LongLength} of {lengthBytes} requested byte(s); the "
                    + "window is not reported.");
            }

            return new ToolResult(true,
                $"Peeked {lengthBytes} byte(s) at 0x{address:X8} over the boot command stage.",
                ToolJson.Data(new MtkPeekPayload(target, address, lengthBytes,
                    Convert.ToHexStringLower(data), Sha256.ComputeHexLower(data))));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string? ToHex(byte[]? value) => value is null ? null : Convert.ToHexStringLower(value);
}