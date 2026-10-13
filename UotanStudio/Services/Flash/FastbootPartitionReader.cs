using System.Globalization;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>Reads validated partition sizes reported by a selected Fastboot device.<para>读取所选 Fastboot 设备上报并通过校验的分区容量。</para></summary>
public static class FastbootPartitionReader
{
    /// <summary>Opens a managed session and reads the partition-size variables.<para>打开受管会话并读取分区容量变量。</para></summary>
    /// <param name="device">The Fastboot device to query.<para>要查询的 Fastboot 设备。</para></param>
    /// <param name="ct">Token cancelling the read.<para>用于取消读取的令牌。</para></param>
    /// <returns>The accepted partition names and sizes.<para>被接受的分区名称与容量。</para></returns>
    public static Task<IReadOnlyList<(string Name, long Size)>> ReadAsync(DetectedDevice device, CancellationToken ct)
        => ReadAsync(device, showAll: false, ct);

    /// <summary>Fastbootd defaults to logical partitions; bootloader Fastboot always shows all reported partitions.<para>Fastbootd 默认只列逻辑分区；bootloader 阶段的 Fastboot 列出全部上报分区。</para></summary>
    /// <param name="device">The Fastboot device to query.<para>要查询的 Fastboot 设备。</para></param>
    /// <param name="showAll">When true, include physical partitions as well.<para>为真时同时包含物理分区。</para></param>
    /// <param name="ct">Token cancelling the read.<para>用于取消读取的令牌。</para></param>
    /// <returns>The accepted partition names and sizes.<para>被接受的分区名称与容量。</para></returns>
    public static Task<IReadOnlyList<(string Name, long Size)>> ReadAsync(
        DetectedDevice device, bool showAll, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Fastboot)
        {
            throw new ArgumentException("A Fastboot device is required.", nameof(device));
        }

        return Task.Run<IReadOnlyList<(string Name, long Size)>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            return App.DeviceMonitor.RunFastboot(device, session =>
            {
                var variables = session.GetAllVariables();
                ct.ThrowIfCancellationRequested();
                var userspace = variables.TryGetValue("is-userspace", out var reported)
                    ? reported : session.GetVariable("is-userspace", useCache: false);
                ct.ThrowIfCancellationRequested();
                return Parse(variables, logicalOnly: !showAll &&
                    userspace.Equals("yes", StringComparison.OrdinalIgnoreCase));
            }, ct);
        }, ct);
    }

    /// <summary>Parses partition-size keys, ignoring malformed device-provided values.<para>解析分区容量键，忽略格式错误的设备上报值。</para></summary>
    /// <param name="variables">Reported device variables to scan.<para>要扫描的设备上报变量。</para></param>
    /// <param name="logicalOnly">Keeps only partitions reported as logical when true.<para>为真时只保留上报为逻辑的分区。</para></param>
    /// <returns>The accepted partition names and sizes.<para>被接受的分区名称与容量。</para></returns>
    public static IReadOnlyList<(string Name, long Size)> Parse(
        IReadOnlyDictionary<string, string> variables, bool logicalOnly = false)
    {
        var partitions = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        const string prefix = "partition-size:";
        foreach (var (key, value) in variables)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = key[prefix.Length..];
            var hex = value.Trim();
            if (name.Length is < 1 or > 128 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ||
                logicalOnly && (!variables.TryGetValue("is-logical:" + name, out var logical) ||
                    !logical.Equals("yes", StringComparison.OrdinalIgnoreCase)) ||
                !hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) ||
                size <= 0)
            {
                continue;
            }

            partitions[name] = size;
        }

        return [.. partitions.Select(pair => (pair.Key, pair.Value))];
    }
}