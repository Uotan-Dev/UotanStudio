using System.Globalization;

namespace UotanStudio.Services.Devices;

/// <summary>Queries and parses device system properties using injected command execution and text lookup.<para>使用注入的命令执行与文本查询委托读取并解析设备系统属性。</para></summary>
internal static class DevicePropertyReader
{
    /// <summary>
    /// Cleans shell output: strips blank lines, collapses whitespace, normalizes error markers to "--".
    /// <para>清理 shell 输出：去除空行、合并空白，并把错误标记归一化为 "--"。</para>
    /// </summary>
    /// <param name="raw">Raw command output text.<para>原始命令输出文本。</para></param>
    /// <returns>The collapsed single-line value, or "--" when the output is empty or reports an error.<para>折叠后的单行值；输出为空或报错时返回 "--"。</para></returns>
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
            || trimmed.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("fail", StringComparison.OrdinalIgnoreCase)
                ? "--"
                : trimmed;
    }

    /// <summary>
    /// Extracts the value after the last colon in colon-delimited output.
    /// <para>从冒号分隔的输出中提取最后一个冒号之后的值。</para>
    /// </summary>
    /// <param name="raw">Raw command output text.<para>原始命令输出文本。</para></param>
    /// <returns>The trailing segment after the final colon, or "--" when the input is blank.<para>最后一个冒号之后的片段；输入为空时返回 "--"。</para></returns>
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
    /// Queries ADB device properties and parses battery, memory and disk details.
    /// <para>批量查询 ADB 设备属性并解析电池、内存、磁盘信息。</para>
    /// </summary>
    /// <param name="run">Runs a shell command on the device and returns its raw output.<para>在设备上执行 shell 命令并返回原始输出。</para></param>
    /// <param name="getString">Resolves a localized resource key into display text.<para>将本地化资源键解析为显示文本。</para></param>
    /// <returns>A key-to-value map of device properties and derived detail strings.<para>设备属性与派生详情字符串的键值映射。</para></returns>
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
    /// Aggregates ADB bootloader lock-state signals into an unlocked, locked, or unknown verdict. Unlocked evidence
    /// wins because some devices keep reporting a locked vbmeta state after unlocking.
    /// <para>汇总 ADB 侧的 Bootloader 锁状态信号，得出已解锁、已上锁或未知的结论。解锁证据优先，因为部分设备解锁后仍上报 locked 的 vbmeta 状态。</para>
    /// </summary>
    /// <param name="get">Runs a property query and returns the cleaned value.<para>执行属性查询并返回清理后的值。</para></param>
    /// <param name="getString">Resolves a localized resource key into display text.<para>将本地化资源键解析为显示文本。</para></param>
    /// <returns>The localized unlocked or locked label, or "--" when no signal is present.<para>本地化的解锁或上锁标签；无信号时返回 "--"。</para></returns>
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
    /// Parses an A/B slot id into a human-readable value.
    /// <para>解析 A/B 槽位号为可读字符串。</para>
    /// </summary>
    /// <param name="slot">Raw slot suffix value.<para>原始槽位后缀值。</para></param>
    /// <returns>Slot A or Slot B, and "--" when the slot cannot be recognised.<para>识别出的 A 或 B 槽位；无法识别时返回 "--"。</para></returns>
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
    /// Formats the /proc/uptime seconds into a human-readable power-on duration.
    /// <para>将 /proc/uptime 的秒数格式化为可读的开机时长。</para>
    /// </summary>
    /// <param name="raw">The uptime output text.<para>uptime 的输出文本。</para></param>
    /// <param name="getString">Resolves a localized resource key into display text.<para>将本地化资源键解析为显示文本。</para></param>
    /// <returns>A day, hour, minute and second duration string, or "--" when parsing fails.<para>按时分秒拼接的时长字符串；解析失败时返回 "--"。</para></returns>
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
    /// Parses dumpsys battery output into a level and a voltage with temperature text.
    /// <para>解析 dumpsys battery 输出，得到电量百分比与电压温度文本。</para>
    /// </summary>
    /// <param name="info">dumpsys battery output text.<para>dumpsys battery 的输出文本。</para></param>
    /// <returns>The battery level percentage together with the voltage and temperature text.<para>电量百分比，以及电压与温度组成的说明文本。</para></returns>
    internal static (int Level, string Text) ParseBattery(string info)
    {
        List<string> values = [];
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
    /// Parses MemTotal and MemAvailable from /proc/meminfo into a usage percentage and text.
    /// <para>解析 /proc/meminfo 的 MemTotal 与 MemAvailable，得到占用百分比与说明文本。</para>
    /// </summary>
    /// <param name="info">/proc/meminfo output text.<para>/proc/meminfo 的输出文本。</para></param>
    /// <returns>The memory usage percentage together with the used and total text.<para>内存占用百分比，以及已用量与总量的说明文本。</para></returns>
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
    /// Parses the target mount line from df output into a usage percentage and text.
    /// <para>解析 df 输出中的目标挂载行，得到占用百分比与说明文本。</para>
    /// </summary>
    /// <param name="info">df output text for the candidate mount points.<para>候选挂载点相关的 df 输出文本。</para></param>
    /// <returns>The disk usage percentage together with the used and total text.<para>磁盘占用百分比，以及已用量与总量的说明文本。</para></returns>
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