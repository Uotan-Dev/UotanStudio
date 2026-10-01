using System.Globalization;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>Reads validated partition sizes reported by a selected Fastboot device.</summary>
public static class FastbootPartitionReader
{
    /// <summary>Opens a managed session and reads the partition-size variables.</summary>
    public static Task<IReadOnlyList<(string Name, long Size)>> ReadAsync(DetectedDevice device, CancellationToken ct)
        => ReadAsync(device, showAll: false, ct);

    /// <summary>Fastbootd defaults to logical partitions; bootloader Fastboot always shows all reported partitions.</summary>
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

    /// <summary>Parses partition-size keys, ignoring malformed device-provided values.</summary>
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

        return partitions.Select(pair => (pair.Key, pair.Value)).ToArray();
    }
}