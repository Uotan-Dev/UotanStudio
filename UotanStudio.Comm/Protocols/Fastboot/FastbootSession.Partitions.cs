using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Fastboot.Images;
using UotanStudio.Comm.Transports.Abstractions;


namespace UotanStudio.Comm.Protocols.Fastboot;

public sealed partial class FastbootSession
{
    /// <summary>
    /// Checks whether the specified partition has A/B slots.
    /// <para>检查指定分区是否具有 A/B 槽位。</para>
    /// </summary>
    /// <param name="partition">Partition name to probe. <para>要探测的分区名。</para></param>
    /// <returns><see langword="true"/> when the device reports <c>yes</c>; empty <paramref name="partition"/> returns <see langword="false"/>. Results are cached per partition.
    /// <para>设备报告 <c>yes</c> 时返回 <see langword="true"/>；<paramref name="partition"/> 为空时返回 <see langword="false"/>。结果按分区缓存。</para></returns>
    public bool HasSlot(string partition)
    {
        if (string.IsNullOrEmpty(partition))
        {
            return false;
        }

        if (_hasSlotCache.TryGetValue(partition, out var has))
        {
            return has;
        }

        has = GetVariable(FastbootConstants.VarHasSlotPrefix + partition) == "yes";
        _hasSlotCache[partition] = has;
        return has;
    }

    /// <summary>
    /// Gets the current active slot suffix (e.g. "a" or "b").
    /// <para>获取当前活跃槽位后缀（如 "a" 或 "b"）。</para>
    /// </summary>
    /// <returns>The active slot suffix with any leading underscore removed; empty when the device reports none.
    /// <para>去除前导下划线后的活跃槽位后缀；设备未上报时为空。</para></returns>
    public string GetCurrentSlot()
    {
        var slot = GetVariable(FastbootConstants.VarCurrentSlot);
        return slot.StartsWith('_') ? slot[1..] : slot;
    }

    /// <summary>
    /// Resolves a partition name against A/B slots with exactly the rules used by
    /// <see cref="FlashPartition"/>: an explicit slot suffix is authoritative and returned
    /// unchanged, a slot-capable partition gets the current active slot appended, and any other
    /// name is returned as-is. Callers that issue several commands for the same target (for
    /// example <c>is-logical</c> / <c>resize-logical-partition</c> before <c>flash:</c>) use this
    /// so every command addresses the identical partition.
    /// <para>使用与 <see cref="FlashPartition"/> 完全相同的规则按 A/B 槽位解析分区名：显式槽位后缀
    /// 权威且原样返回，支持槽位的分区追加当前活跃槽位，其余名称原样返回。需要对同一目标连发多条
    /// 命令的调用方（如 <c>flash:</c> 前的 <c>is-logical</c> / <c>resize-logical-partition</c>）
    /// 借此保证各条命令指向完全相同的分区。</para>
    /// </summary>
    /// <param name="partition"><para>待解析的分区名。</para>The partition name to resolve.</param>
    /// <returns><para>槽位解析后的分区名。</para>The slot-resolved partition name.</returns>
    public string ResolvePartition(string partition)
    {
        ArgumentException.ThrowIfNullOrEmpty(partition);
        return ResolvePartitionWithSlot(partition);
    }

    /// <summary>
    /// Resolves the target partition name against A/B slots: a name that already carries an explicit
    /// slot suffix (e.g. <c>system_ext_a</c>) is flashed verbatim, matching upstream fastboot, so the
    /// compound base name is never re-split or re-suffixed; other slot-capable partitions get the
    /// current active slot appended. Non-slot partitions are returned unchanged.
    /// <para>按 A/B 槽位解析目标分区名：已带显式槽位后缀的名（如 <c>system_ext_a</c>）按原样刷写，
    /// 与上游 fastboot 一致，绝不拆分或追加第二个后缀；其余支持槽位的分区追加当前活跃槽位；
    /// 非槽位分区原样返回。</para>
    /// </summary>
    private string ResolvePartitionWithSlot(string partition)
    {
        if (string.IsNullOrEmpty(partition) || HasSlotSuffix(partition))
        {
            return partition;
        }

        if (!HasSlot(partition))
        {
            return partition;
        }

        var suffix = GetCurrentSlot();
        return string.IsNullOrEmpty(suffix) ? partition : partition + "_" + suffix;
    }

    /// <summary>
    /// Determines whether a partition name already ends with an A/B slot suffix (e.g. "boot_a").
    /// <para>判断分区名是否已带 A/B 槽位后缀（如 "boot_a"）。</para>
    /// </summary>
    private static bool HasSlotSuffix(string partition)
    {
        var underscore = partition.LastIndexOf('_');
        if (underscore <= 0 || underscore == partition.Length - 1)
        {
            return false;
        }

        var suffix = partition[(underscore + 1)..];
        return suffix.Length == 1 && (suffix[0] is 'a' or 'b' or 'A' or 'B');
    }

    /// <summary>
    /// Gets the size of the specified partition in bytes (decimal or 0x-prefixed hex).
    /// Returns 0 when the device does not report the partition size.
    /// <para>以字节获取指定分区大小（十进制或 0x 前缀十六进制）。设备未报告时返回 0。</para>
    /// </summary>
    /// <param name="partition">Partition name to size. <para>要获取大小的分区名。</para></param>
    /// <returns>The parsed size in bytes, or 0 when unavailable. <para>解析出的字节大小；不可用时为 0。</para></returns>
    public long GetPartitionSizeLong(string partition)
    {
        var value = GetVariable(FastbootConstants.VarPartitionSizePrefix + partition);
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        value = value.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(value, 16)
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Gets the maximum fetch size reported by the device (fastbootd max-fetch-size variable),
    /// or -1 when unavailable.
    /// <para>获取设备报告的最大 fetch 大小（fastbootd 的 max-fetch-size 变量），不可用时返回 -1。</para>
    /// </summary>
    private long GetMaxFetchSize()
    {
        var value = GetVariable(FastbootConstants.VarMaxFetchSize);
        if (string.IsNullOrEmpty(value))
        {
            return -1;
        }

        value = value.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : -1
            : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : -1;
    }

    private static readonly HashSet<string> VbmetaPartitionSet = new(
        ["vbmeta", "vbmeta_system", "vbmeta_vendor", "vbmeta_custom"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Queries a single device variable and returns a detailed result including error information.
    /// <para>按键查询单个设备变量并返回包含错误信息的详细结果。</para>
    /// </summary>
    /// <param name="key">The variable key. <para>变量名。</para></param>
    /// <param name="useCache">Whether to use the cached value. <para>是否使用缓存值。</para></param>
    /// <returns>A tuple of value, success flag, and error message. <para>值、成功标志与错误信息的三元组。</para></returns>
    public (string Value, bool Success, string? ErrorMessage) GetVarWithResult(string key, bool useCache = true)
    {
        if (useCache && _varCache.TryGetValue(key, out var cached))
        {
            return (cached, true, null);
        }

        var response = ExecuteCommand(FastbootConstants.CommandGetVar + key);
        if (response.State is FastbootResponseState.Fail)
        {
            return (string.Empty, false, response.Info);
        }

        if (response.State is FastbootResponseState.Timeout)
        {
            return (string.Empty, false, "Timeout waiting for device response");
        }

        if (useCache)
        {
            _varCache[key] = response.Info;
        }

        return (response.Info, true, null);
    }

    /// <summary>
    /// Gets the number of A/B slots on the device.
    /// <para>获取设备上的 A/B 槽位数量。</para>
    /// </summary>
    /// <returns>The parsed slot count, or 1 when the device does not report a valid value.
    /// <para>解析出的槽位数量；设备未报告有效值时返回 1。</para></returns>
    public int GetSlotCount()
        => int.TryParse(GetVariable(FastbootConstants.VarSlotCount), out var count) ? count : 1;

    /// <summary>
    /// Checks whether the specified partition exists on the device.
    /// <para>检查指定分区在设备上是否存在。</para>
    /// </summary>
    /// <param name="partition">Partition name to probe. <para>要探测的分区名。</para></param>
    /// <returns><see langword="true"/> when the device reports a non-zero partition size. <para>设备上报非零分区大小时返回 <see langword="true"/>。</para></returns>
    public bool PartitionExists(string partition)
        => TryGetVar(FastbootConstants.VarPartitionSizePrefix + partition, out var res)
           && !string.IsNullOrEmpty(res) && res != "0" && res != "0x0";

    /// <summary>
    /// Gets the raw partition size string reported by the device (e.g. "0x4000000").
    /// <para>获取设备上报的分区大小原始字符串（如 "0x4000000"）。</para>
    /// </summary>
    /// <param name="partition">Partition name to size. <para>要获取大小的分区名。</para></param>
    /// <returns>The trimmed size string, or empty when unavailable. <para>去除首尾空白后的大小字符串；不可用时为空。</para></returns>
    public string GetPartitionSize(string partition)
        => TryGetVar(FastbootConstants.VarPartitionSizePrefix + partition, out var v) ? v.Trim() : string.Empty;

    /// <summary>
    /// Gets the filesystem type of the specified partition.
    /// <para>获取指定分区的文件系统类型。</para>
    /// </summary>
    /// <param name="partition">Partition name to classify. <para>要判定的分区名。</para></param>
    /// <returns>The trimmed filesystem type, or empty when the device does not report it.
    /// <para>去除首尾空白后的文件系统类型；设备未上报时为空。</para></returns>
    public string GetPartitionType(string partition)
        => TryGetVar(FastbootConstants.VarPartitionTypePrefix + partition, out var v) ? v.Trim() : string.Empty;

    /// <summary>
    /// Checks whether the device is in userspace fastboot mode (fastbootd).
    /// <para>检查设备是否处于用户空间 fastboot 模式（fastbootd）。</para>
    /// </summary>
    /// <returns><see langword="true"/> when the is-userspace variable reports <c>yes</c>; any query error yields <see langword="false"/>.
    /// <para>is-userspace 变量报告 <c>yes</c> 时返回 <see langword="true"/>；查询出错一律返回 <see langword="false"/>。</para></returns>
    public bool IsUserspace()
    {
        try
        {
            return GetVariable(FastbootConstants.VarIsUserspace, useCache: false) == "yes";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether a partition is a logical partition on the device.
    /// <para>检查指定分区是否为设备上的逻辑分区。</para>
    /// </summary>
    /// <param name="partition">Partition name to probe. <para>要探测的分区名。</para></param>
    /// <returns><see langword="true"/> when the is-logical variable reports <c>yes</c>. <para>is-logical 变量报告 <c>yes</c> 时返回 <see langword="true"/>。</para></returns>
    public bool IsLogical(string partition)
        => TryGetVar(FastbootConstants.VarIsLogicalPrefix + partition, out var v) && v == "yes";

    /// <summary>
    /// Checks whether a partition is logical, using preloaded metadata if available,
    /// otherwise querying the device.
    /// <para>检查分区是否为逻辑分区，优先使用预加载的元数据，否则查询设备。</para>
    /// </summary>
    /// <param name="partition">Partition name to probe. <para>要探测的分区名。</para></param>
    /// <returns><see langword="true"/> when the partition is known logical. <para>该分区为已知逻辑分区时返回 <see langword="true"/>。</para></returns>
    public bool IsLogicalOptimized(string partition)
        => _logicalPartitionsFromMetadata?.Contains(partition) ?? IsLogical(partition);

    /// <summary>
    /// Loads logical partition names from a super image metadata file for optimized flashing.
    /// <para>从 super 镜像元数据文件加载逻辑分区名称，用于优化刷写。</para>
    /// </summary>
    /// <param name="superImagePath">
    /// Path to the super image. A missing or unreadable file clears the preloaded set, so later checks fall back to querying the device.
    /// <para>super 镜像路径。文件缺失或不可读时会清除预加载集合，后续检查回退到查询设备。</para>
    /// </param>
    public void LoadLogicalPartitionsFromMetadata(string superImagePath)
    {
        if (!File.Exists(superImagePath))
        {
            _logicalPartitionsFromMetadata = null;
            return;
        }

        try
        {
            var partitions = new HashSet<string>(
                new SuperImageBuilder().GetLogicalPartitions(superImagePath)
                    .Select(p => p.Name)
                    .Where(n => !string.IsNullOrEmpty(n)),
                StringComparer.Ordinal);
            _logicalPartitionsFromMetadata = partitions;
        }
        catch
        {
            _logicalPartitionsFromMetadata = null;
        }
    }

    /// <summary>
    /// Checks whether the fastboot-info.txt version is supported (version ≤ 2).
    /// <para>检查 fastboot-info.txt 版本是否受支持（版本 ≤ 2）。</para>
    /// </summary>
    /// <param name="version">Version string to test; must parse as an unsigned integer. <para>待测的版本字符串；必须能解析为无符号整数。</para></param>
    /// <returns><see langword="true"/> when <paramref name="version"/> parses and is at most 2. <para><paramref name="version"/> 可解析且不超过 2 时返回 <see langword="true"/>。</para></returns>
    public bool CheckFastbootInfoRequirements(string version)
        => uint.TryParse(version, out var v) && v <= 2;

    /// <summary>
    /// Checks whether the specified partition name is a known vbmeta partition.
    /// <para>检查指定分区名称是否为已知的 vbmeta 分区。</para>
    /// </summary>
    /// <param name="partition">Partition name, optionally carrying an <c>_a</c>/<c>_b</c> slot suffix. <para>分区名，可带 <c>_a</c>/<c>_b</c> 槽位后缀。</para></param>
    /// <returns><see langword="true"/> when the base name matches a known vbmeta partition (case-insensitive).
    /// <para>基础名匹配已知 vbmeta 分区（不区分大小写）时返回 <see langword="true"/>。</para></returns>
    public bool IsVbmetaPartition(string partition)
    {
        if (string.IsNullOrWhiteSpace(partition))
        {
            return false;
        }

        var partName = partition;
        var underscoreIndex = partition.LastIndexOf('_');
        if (underscoreIndex > 0)
        {
            var suffix = partition[(underscoreIndex + 1)..];
            if (suffix.Equals("a", StringComparison.OrdinalIgnoreCase) ||
                suffix.Equals("b", StringComparison.OrdinalIgnoreCase))
            {
                partName = partition[..underscoreIndex];
            }
        }

        return VbmetaPartitionSet.Contains(partName);
    }

}