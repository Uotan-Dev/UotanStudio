using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Mirroring;
using UotanStudio.Services.Network;

namespace UotanStudio.Services.Scripting;

/// <summary>
/// Builds the data-driven tool table exposed to a plugin script as <c>tool</c>, naming each entry like
/// its workflow-node key. <para>构建以 <c>tool</c> 暴露给插件脚本的数据驱动工具表，条目名与对应工作流节点键一致。</para>
/// </summary>
public static class ScriptToolCatalog
{
    /// <summary>
    /// Creates the tool table for one script execution.
    /// <para>为一次脚本执行创建工具表。</para>
    /// </summary>
    /// <param name="context">
    /// Execution context that owns resources registered by the tools.
    /// <para>持有工具所注册资源的执行上下文。</para>
    /// </param>
    /// <param name="gateway">
    /// Gateway dispatching device and flash operations for the tools.
    /// <para>为工具分发设备与刷写操作的网关。</para>
    /// </param>
    /// <param name="httpClient">
    /// Client that download entries call to fetch remote content.
    /// <para>下载条目获取远端内容所调用的客户端。</para>
    /// </param>
    /// <returns>
    /// The tool table keyed by hierarchical tool name.
    /// <para>以层级工具名为键的工具表。</para>
    /// </returns>
    public static IReadOnlyDictionary<string, ScriptTool> Create(
        INodeExecutionContext context,
        IScriptToolGateway gateway,
        HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(httpClient);
        var sessions = new Dictionary<string, ManagedMirrorSession>(StringComparer.Ordinal);

        DetectedDevice Target(ScriptToolInvocation invocation)
        {
            var devices = gateway.Scan();
            var serial = Arg(invocation, "serial");
            if (!string.IsNullOrWhiteSpace(serial))
            {
                var matches = devices.Where(device => string.Equals(device.Serial, serial, StringComparison.Ordinal)
                    || string.Equals(device.Key, serial, StringComparison.Ordinal)).Take(2).ToArray();
                if (matches.Length != 1)
                {
                    throw new InvalidOperationException($"Device '{serial}' is missing or ambiguous.");
                }
                return matches[0];
            }
            if (devices.Count == 1)
            {
                return devices[0];
            }
            throw new InvalidOperationException("The 'serial' argument is required when the device list is not a single target.");
        }

        return new Dictionary<string, ScriptTool>(StringComparer.Ordinal)
        {
            ["Device.Target.Select"] = new("Device.Target.Select", invocation =>
            {
                var device = Target(invocation);
                return Task.FromResult(Result(
                    ("serial", device.Serial),
                    ("key", device.Key),
                    ("protocol", ProtocolText(device)),
                    ("state", device.State)));
            }),
            ["Device.State.Query"] = new("Device.State.Query", invocation =>
            {
                var device = Target(invocation);
                return Task.FromResult(Result(
                    ("protocol", ProtocolText(device)),
                    ("state", device.State)));
            }),
            ["Device.Property.Read"] = new("Device.Property.Read", async invocation =>
            {
                var device = Target(invocation);
                var property = Required(invocation, "name");
                var result = await gateway.GetDevicePropertyAsync(device, property, invocation.CancellationToken)
                    .ConfigureAwait(false);
                Require(result.Success, result.Summary);
                return Result(("value", result.Summary));
            }),
            ["Device.Power.Reboot"] = new("Device.Power.Reboot", async invocation =>
            {
                var device = Target(invocation);
                var mode = Arg(invocation, "mode")?.Trim() ?? string.Empty;
                await gateway.RebootAsync(device, mode, invocation.CancellationToken).ConfigureAwait(false);
                return Result(("mode", mode), ("serial", device.Serial));
            }, RequiresConfirmation: true, ConfirmationPrompt: "Reboot the selected device?"),
            ["Device.Partition.Flash"] = new("Device.Partition.Flash", async invocation =>
            {
                var device = Target(invocation);
                var partition = Required(invocation, "partition");
                var imagePath = Required(invocation, "image");
                var result = await gateway.FlashImageAsync(device, partition, imagePath, invocation.CancellationToken)
                    .ConfigureAwait(false);
                Require(result.Success, result.Summary);
                return Result(("partition", partition), ("summary", result.Summary));
            }, RequiresConfirmation: true, ConfirmationPrompt: "Flash the selected partition?"),
            ["Device.Partition.Erase"] = new("Device.Partition.Erase", async invocation =>
            {
                var device = Target(invocation);
                var partition = Required(invocation, "partition");
                var result = await gateway.ErasePartitionAsync(device, partition, invocation.CancellationToken)
                    .ConfigureAwait(false);
                Require(result.Success, result.Summary);
                return Result(("partition", partition), ("summary", result.Summary));
            }, RequiresConfirmation: true, ConfirmationPrompt: "Erase the selected partition?"),
            ["Device.Partition.Table.Read"] = new("Device.Partition.Table.Read", async invocation =>
            {
                var device = Target(invocation);
                var showAll = Bool(Arg(invocation, "showall"));
                var partitions = await gateway.ReadPartitionTableAsync(device, showAll, invocation.CancellationToken)
                    .ConfigureAwait(false);
                return Result(
                    ("count", partitions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("partitions", string.Join('\n', partitions.Select(entry => $"{entry.Name}={entry.Size}"))));
            }),
            ["Device.Slot.Query"] = new("Device.Slot.Query", async invocation =>
            {
                var device = Target(invocation);
                var slot = await gateway.GetCurrentSlotAsync(device, invocation.CancellationToken).ConfigureAwait(false);
                return Result(("slot", slot));
            }),
            ["Device.Slot.Switch"] = new("Device.Slot.Switch", async invocation =>
            {
                var device = Target(invocation);
                var slot = await gateway.SetOtherSlotAsync(device, invocation.CancellationToken).ConfigureAwait(false);
                return Result(("slot", slot));
            }, RequiresConfirmation: true, ConfirmationPrompt: "Switch the active A/B slot?"),
            ["Device.Recovery.Clean"] = new("Device.Recovery.Clean", async invocation =>
            {
                var device = Target(invocation);
                var mode = Required(invocation, "mode");
                var output = await gateway.RecoveryCleanAsync(device, mode, invocation.CancellationToken)
                    .ConfigureAwait(false);
                return Result(("mode", mode), ("output", output));
            }, RequiresConfirmation: true, ConfirmationPrompt: "Wipe data through recovery?"),
            ["Device.Shell.Command"] = new("Device.Shell.Command", async invocation =>
            {
                var device = Target(invocation);
                var command = Required(invocation, "command");
                var output = await gateway.ShellAsync(device, command, invocation.CancellationToken)
                    .ConfigureAwait(false);
                return Result(("command", command), ("output", output));
            }, RequiresConfirmation: true, ConfirmationPrompt: "Run the shell command on the selected device?"),
            ["Network.Download.File"] = new("Network.Download.File", async invocation =>
            {
                var urlText = Required(invocation, "url");
                if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    throw new InvalidOperationException("URL must be an absolute HTTP or HTTPS address.");
                }
                var request = new FileDownloadRequest(
                    uri,
                    Required(invocation, "path"),
                    Bool(Arg(invocation, "mirror")),
                    Arg(invocation, "headers"),
                    Arg(invocation, "sha256"),
                    ParseTimeout(Arg(invocation, "timeout")));
                var result = await FileDownloadService.DownloadAsync(httpClient, request, null,
                    invocation.CancellationToken).ConfigureAwait(false);
                return Result(
                    ("path", result.Path),
                    ("source", result.Source.ToString()),
                    ("bytes", result.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }),
            ["Mirroring.Session.Start"] = new("Mirroring.Session.Start", invocation =>
            {
                var device = Target(invocation);
                var session = gateway.StartMirror(device);
                var id = Guid.NewGuid().ToString("N");
                sessions[id] = session;
                context.RegisterResource(new SessionDisposer(session));
                return Task.FromResult(Result(("id", id), ("serial", device.Serial)));
            }),
            ["Mirroring.Session.Stop"] = new("Mirroring.Session.Stop", invocation =>
            {
                var id = Required(invocation, "id");
                if (!sessions.Remove(id, out var session))
                {
                    throw new InvalidOperationException($"Mirroring session '{id}' is not active.");
                }
                session.Dispose();
                return Task.FromResult(Result(("id", id)));
            })
        };
    }

    private static string ProtocolText(DetectedDevice device) => device.Protocol switch
    {
        DeviceProtocolKind.Adb => "adb",
        DeviceProtocolKind.Fastboot => "fastboot",
        DeviceProtocolKind.Edl => "edl",
        DeviceProtocolKind.Rkdl => "rkdl",
        DeviceProtocolKind.Mtk => "mtk",
        DeviceProtocolKind.Hdc => "hdc",
        _ => "unknown"
    };

    private static string? Arg(ScriptToolInvocation invocation, string name)
        => invocation.Arguments.TryGetValue(name, out var value) ? value : null;

    private static string Required(ScriptToolInvocation invocation, string name)
    {
        var value = Arg(invocation, name);
        return !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException($"The '{name}' argument is required.");
    }

    private static bool Bool(string? value) => bool.TryParse(value, out var parsed) && parsed;

    private static double? ParseTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            throw new InvalidOperationException("The 'timeout' argument must be a number of seconds.");
        }
        return seconds;
    }

    private static void Require(bool success, string summary)
    {
        if (!success)
        {
            throw new InvalidOperationException(summary);
        }
    }

    private static IReadOnlyDictionary<string, string?> Result(params (string Key, string? Value)[] pairs)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            result[key] = value;
        }
        return result;
    }

    private sealed class SessionDisposer(ManagedMirrorSession session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            session.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}