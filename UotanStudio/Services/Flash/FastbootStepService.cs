using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>Executes non-image steps from a validated plan on a selected Fastboot device.<para>在所选 Fastboot 设备上执行已校验计划中的非镜像步骤。</para></summary>
public static class FastbootStepService
{
    /// <summary>Deletes only device-reported logical COW partitions in Fastbootd.<para>在 Fastbootd 中只删除设备上报的逻辑 COW 分区。</para></summary>
    /// <param name="device">The Fastboot device to change.<para>要改动的 Fastboot 设备。</para></param>
    /// <param name="ct">Token cancelling the removal.<para>用于取消删除的令牌。</para></param>
    /// <returns>The number of logical partitions deleted.<para>被删除的逻辑分区数量。</para></returns>
    public static Task<int> DeleteCowAsync(DetectedDevice device, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Fastboot)
        {
            throw new ArgumentException("A Fastboot device is required.", nameof(device));
        }

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return App.DeviceMonitor.RunFastboot(device, session =>
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

    /// <summary>Filters getvar:all to specifically reported logical COW partitions.<para>从 getvar:all 结果中筛出明确上报的逻辑 COW 分区。</para></summary>
    /// <param name="variables">Reported device variables to filter.<para>要筛选的设备上报变量。</para></param>
    /// <returns>The distinct logical COW partition names.<para>去重后的逻辑 COW 分区名。</para></returns>
    public static IReadOnlyList<string> ParseCowPartitions(IReadOnlyDictionary<string, string> variables)
    {
        const string prefix = "is-logical:";
        return [.. variables.Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                       pair.Value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key[prefix.Length..])
            .Where(name => name.Length is > 4 and <= 128 &&
                           name.EndsWith("-cow", StringComparison.OrdinalIgnoreCase) &&
                           name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Returns failure as an exception; no external process or shell is used.<para>失败以异常返回，不使用外部进程或 shell。</para></summary>
    /// <param name="device">The Fastboot device to drive.<para>要驱动的 Fastboot 设备。</para></param>
    /// <param name="step">The validated non-image step to run.<para>要执行的已校验非镜像步骤。</para></param>
    /// <param name="ct">Token cancelling the step.<para>用于取消步骤的令牌。</para></param>
    /// <remarks>
    /// <para>A <c>create</c> step uses <see cref="FlashStep.ImageLength"/> as the logical partition
    /// size, creates the partition with it and then resizes it to the same size. A create step that
    /// carries no size fails explicitly: <c>create-logical-partition</c> is never sent with size 0.</para>
    /// <para><c>create</c> 步骤把 <see cref="FlashStep.ImageLength"/> 作为逻辑分区尺寸，以该尺寸创建
    /// 分区后再调整到同一尺寸。未携带尺寸的 create 步骤会明确失败：绝不会以 size 0 发送
    /// <c>create-logical-partition</c>。</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">A <c>create</c> step carries no logical partition size.
    /// <para><c>create</c> 步骤未携带逻辑分区尺寸。</para></exception>
    public static Task ExecuteAsync(DetectedDevice device, FlashStep step, CancellationToken ct)
    {
        if (device.Protocol != DeviceProtocolKind.Fastboot)
        {
            throw new ArgumentException("A Fastboot device is required.", nameof(device));
        }

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            App.DeviceMonitor.RunFastboot(device, session =>
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
                            if (step.ImageLength <= 0)
                            {
                                throw new NotSupportedException(
                                    "The flash step carries no logical partition size, so create-logical-partition is not sent with size 0. 刷写步骤未携带逻辑分区尺寸，不会以 size 0 发送 create-logical-partition。");
                            }

                            session.CreateLogicalPartition(name, step.ImageLength).ThrowIfFailed();
                            session.ResizeLogicalPartition(name, step.ImageLength).ThrowIfFailed();
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