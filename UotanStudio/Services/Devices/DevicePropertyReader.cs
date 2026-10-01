using System.Globalization;

namespace UotanStudio.Services.Devices;

/// <summary>Queries and parses system properties using caller-owned command execution and localization.</summary>
internal static class DevicePropertyReader
{
    /// <summary>
    /// <para>清理 shell 输出：去除空行并合并空白，错误标记归一化为 "--"。</para>
    /// Cleans shell output: strips blank lines, collapses whitespace, normalizes error
    /// markers to "--".
    /// </summary>
    private static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "--";
        }

        var trimmed = string.Join(" ", raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return trimmed.Length == 0
            || trimmed.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("device offline", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("fail", StringComparison.OrdinalIgnoreCase)
                ? "--"
                : trimmed;
    }

    /// <summary>
    /// <para>从冒号分隔的输出中取冒号后的值（如 "Physical size: 1080x2340" -> "1080x2340"）。</para>
    /// Extracts the last colon-delimited segment (e.g. "Physical size: 1080x2340" -> "1080x2340").
    /// </summary>
    private static string ColonValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "--";
        }

        var parts = raw.Split(['\r', '\n', ':'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1].Trim() : "--";
    }

    /// <summary>
    /// <para>批量查询 ADB 设备属性并解析电池、内存、磁盘信息。</para>
    /// Queries ADB device properties and parses battery, memory and disk details.
    /// </summary>
    internal static Dictionary<string, string> ReadAdbProperties(Func<string, string> run, Func<string, string> getString)
    {
        string Get(string cmd)
        {
            return Clean(run(cmd));
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["brand"] = Get("getprop ro.product.brand"),
            ["model"] = Get("getprop ro.product.model"),
            ["android"] = Get("getprop ro.build.version.release"),
            ["sdk"] = Get("getprop ro.build.version.sdk"),
            ["codename"] = Get("getprop ro.product.device"),
            ["cpuabi"] = Get("getprop ro.product.cpu.abi"),
            ["cpucode"] = Get("getprop ro.board.platform"),
            ["kernel"] = Get("uname -r"),
            ["resolution"] = ColonValue(run("wm size")),
            ["density"] = ColonValue(run("wm density")),
            ["boardid"] = Get("cat /sys/devices/soc0/serial_number"),
            ["slot"] = Get("getprop ro.boot.slot_suffix"),
            ["compile"] = Get("getprop ro.system.build.version.incremental"),
            ["vndk"] = Get("getprop ro.vndk.version"),
        };

        map["bl"] = ReadBlState(Get, getString);
        map["uptime"] = FormatUptime(Get("cat /proc/uptime"), getString);

        var (bLevel, bText) = ParseBattery(run("dumpsys battery"));
        map["batteryLevel"] = bLevel.ToString(CultureInfo.InvariantCulture);
        map["batteryText"] = bText;

        var (mLevel, mText) = ParseMem(run("cat /proc/meminfo | grep -E 'MemTotal|MemAvailable'"));
        map["memLevel"] = mLevel.ToString(CultureInfo.InvariantCulture);
        map["memText"] = mText;

        var (dLevel, dText) = ParseDisk(run("echo; df /storage/emulated 2>/dev/null || df /data 2>/dev/null || df /sdcard 2>/dev/null"));
        map["diskLevel"] = dLevel.ToString(CultureInfo.InvariantCulture);
        map["diskText"] = dText;

        return map;
    }

    /// <summary>
    /// <para>汇总 ADB 侧的 Bootloader 锁状态信号：任一权威属性证明已解锁即判为已解锁，否则存在上锁证据时判为上锁，
    /// 全部缺失时返回 "--"。解锁证据优先合并，因为部分 OEM 设备解锁后仍继续上报 locked 的 <c>ro.boot.vbmeta.device_state</c>。
    /// <c>ro.boot.verifiedbootstate</c> 的 red 表示设备已被破坏，不构成锁状态结论；华为设备上报的
    /// <c>ro.fastboot.unlock</c> 只有 true 表示解锁，其余取值按上锁处理。</para>
    /// Aggregates the ADB bootloader lock-state signals: any authoritative unlocked signal wins, otherwise
    /// existing locked evidence means locked, and no evidence at all yields "--". Unlocked evidence is merged
    /// first because some OEM devices keep reporting ro.boot.vbmeta.device_state as locked after an unlock.
    /// verifiedbootstate "red" means a corrupt device and yields no lock-state verdict; the Huawei
    /// ro.fastboot.unlock signal means unlocked only for "true" and every other reported value means locked.
    /// </summary>
    private static string ReadBlState(Func<string, string> get, Func<string, string> getString)
    {
        var lockstate = get("getprop ro.secureboot.lockstate").ToLowerInvariant();
        var deviceState = get("getprop ro.boot.vbmeta.device_state").ToLowerInvariant();
        var verifiedboot = get("getprop ro.boot.verifiedbootstate").ToLowerInvariant();
        var oemUnlock = get("getprop ro.fastboot.unlock").ToLowerInvariant();

        if (lockstate == "unlocked" || deviceState == "unlocked" || verifiedboot == "orange" || oemUnlock == "true")
        {
            return getString("Device_Unlocked");
        }

        return lockstate == "locked" || deviceState == "locked" || verifiedboot is "green" or "yellow" || oemUnlock != "--"
            ? getString("Device_Locked")
            : "--";
    }

    /// <summary>
    /// <para>解析 A/B 槽位号为可读字符串。</para>
    /// Parses an A/B slot id into a human-readable value.
    /// </summary>
    internal static string ParseSlot(string slot)
    {
        if (string.IsNullOrEmpty(slot))
        {
            return "--";
        }

        var normalized = slot.Trim().ToLowerInvariant();
        return normalized.EndsWith("_a") || normalized.Equals("a") || normalized.Contains("current-slot: a")
            ? "Slot A"
            : normalized.EndsWith("_b") || normalized.Equals("b") || normalized.Contains("current-slot: b") ? "Slot B" : "--";
    }

    /// <summary>
    /// <para>将 /proc/uptime 的秒数格式化为可读的开机时长。</para>
    /// Formats the /proc/uptime seconds into a human-readable power-on duration.
    /// </summary>
    internal static string FormatUptime(string raw, Func<string, string> getString)
    {
        if (raw is null or "--")
        {
            return "--";
        }

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return "--";
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Days}{getString("Device_UptimeDay")}"
             + $"{ts.Hours}{getString("Device_UptimeHour")}"
             + $"{ts.Minutes}{getString("Device_UptimeMinute")}"
             + $"{ts.Seconds}{getString("Device_UptimeSecond")}";
    }

    /// <summary>
    /// <para>解析 dumpsys battery 输出，返回（电量百分比, 电压/温度文本）。</para>
    /// Parses dumpsys battery output; returns (level percent, voltage/temperature text).
    /// </summary>
    internal static (int Level, string Text) ParseBattery(string info)
    {
        var values = new List<string>();
        var lines = info.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.Contains("Max charging voltage", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Charger voltage", StringComparison.OrdinalIgnoreCase)
                || line.Contains("the voltage was", StringComparison.OrdinalIgnoreCase)
                || line.Contains("last voltage value", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Contains("level", StringComparison.OrdinalIgnoreCase)
                || line.Contains("voltage", StringComparison.OrdinalIgnoreCase)
                || line.Contains("temperature", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 0)
                {
                    values.Add(tokens[^1]);
                }
            }
        }

        if (values.Count < 3)
        {
            return (-1, "--");
        }

        int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level);
        double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var millivolts);
        double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var tenthsCelsius);
        return (Math.Clamp(level, 0, 100), $"{millivolts / 1000.0:0.00}V {tenthsCelsius / 10.0:0.0}℃");
    }

    /// <summary>
    /// <para>解析 /proc/meminfo 中 MemTotal/MemAvailable，返回（占用百分比, 已用/总量文本）。</para>
    /// Parses MemTotal/MemAvailable from /proc/meminfo; returns (usage percent, used/total text).
    /// </summary>
    internal static (int Level, string Text) ParseMem(string info)
    {
        long total = 0, available = 0;
        foreach (var line in info.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(tokens[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out total);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(tokens[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out available);
            }
        }

        if (total <= 0)
        {
            return (-1, "--");
        }

        if (available <= 0)
        {
            available = total;
        }

        double usedKb = total - available;
        var totalGb = Math.Round(total * 1.024 / 1_000_000, 1);
        var usedGb = Math.Round(usedKb * 1.024 / 1_000_000, 1);
        var percent = (int)Math.Round(usedGb / (totalGb <= 0 ? 1 : totalGb) * 100);
        return (Math.Clamp(percent, 0, 100), $"{usedGb:0.0}GB/{totalGb:0.0}GB");
    }

    /// <summary>
    /// <para>解析 df 输出中目标挂载点所在行，返回（占用百分比, 已用/总量文本）。</para>
    /// Parses the target mount line from df output; returns (usage percent, used/total text).
    /// </summary>
    internal static (int Level, string Text) ParseDisk(string info)
    {
        string? target = null;
        foreach (var line in info.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("/storage/emulated")
                || line.StartsWith("/data")
                || line.StartsWith("/sdcard"))
            {
                target = line;
                break;
            }
        }

        if (target is null)
        {
            return (-1, "--");
        }

        // 列顺序：Filesystem Size Used Avail Use% Mounted
        var columns = target.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (columns.Length < 5
            || !double.TryParse(columns[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var blocks)
            || !double.TryParse(columns[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var usedBlocks))
        {
            return (-1, "--");
        }

        var sizeGb = Math.Round(blocks / 1024.0 / 1024.0, 2);
        var usedGb = Math.Round(usedBlocks / 1024.0 / 1024.0, 2);
        var percent = (int)Math.Round(usedBlocks / (blocks <= 0 ? 1 : blocks) * 100);
        return (Math.Clamp(percent, 0, 100), $"{usedGb:0.00}GB/{sizeGb:0.00}GB");
    }
}