using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>Executes non-image steps from a validated plan on a selected Fastboot device.</summary>
public static class FastbootStepService
{
    /// <summary>Deletes only device-reported logical COW partitions in Fastbootd.</summary>
    public static Task<int> DeleteCowAsync(DetectedDevice device, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Fastboot)
        {
            throw new ArgumentException("A Fastboot device is required.", nameof(device));
        }

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return DeviceMonitorService.Instance.RunFastboot(device, session =>
            {
                if (session.GetVariable("is-userspace", useCache: false) != "yes")
                {
                    throw new InvalidOperationException("COW partition removal requires fastbootd.");
                }
                var names = ParseCowPartitions(session.GetAllVariables());
                foreach (var name in names)
                {
                    ct.ThrowIfCancellationRequested();
                    session.DeleteLogicalPartition(name).ThrowIfFailed();
                }
                return names.Count;
            }, ct);
        }, ct);
    }

    /// <summary>Filters getvar:all to specifically reported logical COW partitions.</summary>
    public static IReadOnlyList<string> ParseCowPartitions(IReadOnlyDictionary<string, string> variables)
    {
        const string prefix = "is-logical:";
        return variables.Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                       pair.Value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key[prefix.Length..])
            .Where(name => name.Length is > 4 and <= 128 &&
                           name.EndsWith("-cow", StringComparison.OrdinalIgnoreCase) &&
                           name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Returns failure as an exception; no external process or shell is used.</summary>
    public static Task ExecuteAsync(DetectedDevice device, FlashStep step, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Fastboot)
        {
            throw new ArgumentException("A Fastboot device is required.", nameof(device));
        }

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            DeviceMonitorService.Instance.RunFastboot(device, session =>
            {
                switch (step.Command)
                {
                    case "create":
                    case "delete":
                        if (session.GetVariable("is-userspace", useCache: false) != "yes")
                        {
                            throw new InvalidOperationException("Logical partition operations require fastbootd.");
                        }
                        var slot = session.GetVariable("current-slot", useCache: false);
                        var name = slot is "a" or "b" && !step.Partition.EndsWith("_a", StringComparison.Ordinal) &&
                            !step.Partition.EndsWith("_b", StringComparison.Ordinal)
                            ? step.Partition + "_" + slot
                            : step.Partition;
                        ct.ThrowIfCancellationRequested();
                        if (step.Command == "create")
                        {
                            session.CreateLogicalPartition(name, 0).ThrowIfFailed();
                        }
                        else
                        {
                            session.DeleteLogicalPartition(name).ThrowIfFailed();
                        }
                        break;
                    case "set_active":
                        var target = step.Partition;
                        if (target == "other")
                        {
                            target = session.GetVariable("current-slot", useCache: false) switch
                            {
                                "a" => "b",
                                "b" => "a",
                                _ => throw new InvalidOperationException("Cannot determine the other active slot.")
                            };
                        }
                        if (target is not ("a" or "b"))
                        {
                            throw new InvalidDataException("Invalid slot.");
                        }
                        ct.ThrowIfCancellationRequested();
                        session.SetActiveSlot(target).ThrowIfFailed();
                        break;
                    case "reboot":
                        ct.ThrowIfCancellationRequested();
                        session.Reboot().ThrowIfFailed();
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported step: {step.Command}");
                }
            }, ct);
        }, ct);
    }
}
