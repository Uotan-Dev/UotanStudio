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
    /// Resolves the first property that reports a value.
    /// <para>解析第一个有值的属性。</para>
    /// </summary>
    /// <param name="getProperty">Resolves one property name and returns the cleaned value; a missing key resolves to "--".<para>解析单个属性名并返回清理后的值；缺失的键解析为 "--"。</para></param>
    /// <param name="names">Property names in fallback order.<para>按回退顺序排列的属性名。</para></param>
    /// <returns>The first available value, or "--" when every property is missing.<para>第一个有值的属性值；全部缺失时返回 "--"。</para></returns>
    private static string FirstAvailable(Func<string, string> getProperty, params string[] names)
    {
        foreach (var name in names)
        {
            var value = getProperty(name);
            if (value != "--")
            {
                return value;
            }
        }

        return "--";
    }

    /// <summary>
    /// Resolves the SoC platform, preferring a specific platform name over a vendor-wide hardware marker.
    /// <para>解析 SoC 平台；优先具体平台名而不是厂商级硬件标记。</para>
    /// </summary>
    /// <param name="getProperty">Resolves one property name and returns the cleaned value; a missing key resolves to "--".<para>解析单个属性名并返回清理后的值；缺失的键解析为 "--"。</para></param>
    /// <returns>The SoC platform value, or "--" when no hardware signal is present.<para>SoC 平台值；无硬件信号时返回 "--"。</para></returns>
    private static string ReadSocPlatform(Func<string, string> getProperty)
    {
        var soc = getProperty("ro.soc.model");
        if (soc != "--")
        {
            return soc;
        }

        var platform = getProperty("ro.board.platform");
        var hardware = getProperty("ro.hardware");
        if (platform != "--" && !string.Equals(platform, hardware, StringComparison.OrdinalIgnoreCase))
        {
            return platform;
        }

        var bootHardware = getProperty("ro.boot.hardware");
        return bootHardware != "--" ? bootHardware : platform != "--" ? platform : hardware;
    }

    /// <summary>
    /// Sections that stay stable while the same physical device is connected. They are skipped on a refresh
    /// whose device-wide static properties all came back from the cache, so a repeat detail pass only runs
    /// the live queries.
    /// <para>同一物理设备连接期间保持稳定的段。刷新时若整组静态属性都来自缓存，这些段就被跳过，
    /// 使重复的详情拉取只执行实时查询。</para>
    /// </summary>
    private static readonly (string Key, string Command)[] StaticSections =
    [
        ("K", "uname -r"),
        ("B", "cat /sys/devices/soc0/serial_number"),
    ];

    /// <summary>
    /// Sections whose values change while the device runs; every detail pass re-reads them.
    /// <para>设备运行期间会变化的段；每次详情拉取都重新读取。</para>
    /// </summary>
    private static readonly (string Key, string Command)[] DynamicSections =
    [
        ("R", "wm size"),
        ("D", "wm density"),
        ("P", "cat /proc/uptime"),
        ("Y", "dumpsys battery"),
        ("M", "cat /proc/meminfo"),
        ("F", "df -k /storage/emulated 2>/dev/null || df -k /data 2>/dev/null || df -k /sdcard 2>/dev/null"),
    ];

    /// <summary>
    /// Builds one shell invocation that runs the given sections in order, each preceded by a marker line.
    /// Device-side work dominates the detail fetch — on a real target one stream per command measured
    /// 87–337 ms while six of the same queries in a single shell measured 638 ms — so one fork with markers
    /// is what the reader asks for first. Device stderr is discarded just as the per-command calls
    /// discard it.
    /// <para>构造一次 shell 调用，按顺序执行给定各段，并在每段之前输出一行标记。设备侧耗时才是详情拉取的大头
    /// ——真机上每条命令各开一流实测 87–337 ms，而同样六条合并在一个 shell 内实测 638 ms——所以读取器先尝试
    /// 一次 fork 加标记。设备 stderr 与逐条调用一样被丢弃。</para>
    /// </summary>
    private static string BuildBundleCommand((string Key, string Command)[] sections)
    {
        var command = new System.Text.StringBuilder("{ ");
        foreach (var (key, sectionCommand) in sections)
        {
            command.Append("echo '@").Append(key).Append("'; ").Append(sectionCommand).Append("; ");
        }

        return command.Append("echo '@Z'; } 2>/dev/null").ToString();
    }

    /// <summary>
    /// Runs the bundled detail query and splits the reply by marker. A marker counts only when it occupies a
    /// line on its own, so a command reply that merely contains an at sign followed by one of the marker
    /// letters cannot open or close a section. The reply is accepted only when every expected section came
    /// back, so a shell that cannot run the grouped command or a truncated reply falls back to the
    /// per-command path instead of reporting a missing section as empty.
    /// <para>执行批量详情查询并按标记切分应答。只有独占整行的标记才算标记，因此输出里偶然出现的「at 号加标记
    /// 字母」不会被当成段的边界。只有每一段都回来时才采信，因此无法运行分组命令的 shell 或被截断的应答会回退到
    /// 逐条路径，而不是把缺失的段当作空段上报。</para>
    /// </summary>
    /// <param name="run">Runs a shell command on the device and returns its raw output.
    /// <para>在设备上执行 shell 命令并返回原始输出。</para></param>
    /// <param name="includeStatic">Whether the known-static sections are part of this pass.
    /// <para>本次是否包含已知静态段。</para></param>
    /// <returns>The per-section raw outputs, or <see langword="null"/> when the bundle is unusable.
    /// <para>各段的原始输出；批量结果不可用时为 <see langword="null"/>。</para></returns>
    private static Dictionary<string, string>? TryReadBundle(Func<string, string> run, bool includeStatic)
    {
        (string Key, string Command)[] expected = includeStatic
            ? [.. StaticSections, .. DynamicSections]
            : DynamicSections;
        var raw = run(BuildBundleCommand(expected));
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;
        foreach (var line in raw.Split('\r', '\n'))
        {
            var marker = line.Trim();
            if (marker.Length != 2 || marker[0] != '@' || marker[1] is not ('K' or 'R' or 'D' or 'B' or 'P'
                    or 'Y' or 'M' or 'F' or 'Z'))
            {
                if (current is not null)
                {
                    sections[current].Add(line);
                }

                continue;
            }

            current = marker[1] == 'Z' ? null : marker[1].ToString();
            if (current is not null)
            {
                sections[current] = [];
            }
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, _) in expected)
        {
            if (!sections.TryGetValue(key, out var lines))
            {
                return null;
            }

            result[key] = string.Join('\n', lines).Trim();
        }

        return result;
    }

    /// <summary>
    /// Keys that stay stable while the same physical device is connected; a refresh whose cache holds every
    /// one of them skips both <c>getprop</c> and the static bundle sections.
    /// <para>同一物理设备连接期间保持稳定的键；缓存中这些键齐全时，刷新会跳过 <c>getprop</c> 与静态批量段。</para>
    /// </summary>
    internal static readonly string[] StaticKeys =
    [
        "brand", "model", "android", "sdk", "codename", "cpuabi", "cpucode", "kernel",
        "boardid", "slot", "compile", "vndk", "bl",
    ];

    /// <summary>Keys every device answers; their error placeholders mark a pass that is not worth caching.
    /// <para>每台设备都应答的键；它们仍为错误占位符说明本次拉取不值得缓存。</para>
    /// </summary>
    private static readonly string[] RequiredStaticKeys = ["brand", "model", "codename", "kernel", "bl"];

    /// <summary>Picks the stable subset out of a parsed property map so it can be cached.
    /// <para>从解析后的属性映射中挑出稳定子集，以便写入缓存。</para>
    /// </summary>
    /// <param name="properties">Parsed properties from one detail pass.<para>一次详情拉取解析出的属性。</para></param>
    /// <returns>A new map holding only the keys listed in <see cref="StaticKeys"/>, or <see langword="null"/>
    /// when a property that every device reports is still an error placeholder, so a pass that raced a
    /// still-waking device is retried instead of being cached as its final answer.
    /// <para>仅包含 <see cref="StaticKeys"/> 所列键的新映射；若每台设备都会报告的键仍是错误占位符则返回
    /// <see langword="null"/>，使与尚未就绪设备抢跑的那次拉取被重试而不是当作最终结果缓存。</para></returns>
    internal static Dictionary<string, string>? SelectStaticProperties(IReadOnlyDictionary<string, string> properties)
    {
        foreach (var key in RequiredStaticKeys)
        {
            if (!properties.TryGetValue(key, out var value) || value == "--")
            {
                return null;
            }
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in StaticKeys)
        {
            if (properties.TryGetValue(key, out var value))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static bool HasEveryStaticProperty(IReadOnlyDictionary<string, string> cached)
    {
        foreach (var key in StaticKeys)
        {
            if (!cached.ContainsKey(key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Queries ADB device properties and parses battery, memory and disk details. All getprop keys are read
    /// with one argument-less <c>getprop</c> call, which lists every set property; a key absent from that
    /// table therefore resolves to "--". When the call yields no parsable table every key is queried
    /// individually instead. The detail queries come from one bundled shell command when all its sections
    /// come back, and one command each otherwise. A cache holding every stable key replaces <c>getprop</c>
    /// and the static sections, so a repeat pass only runs the live queries.
    /// <para>批量查询 ADB 设备属性并解析电池、内存、磁盘信息。全部 getprop 键经一次无参 <c>getprop</c> 读取，
    /// 该命令会列出所有已设置的属性，因此表中缺失的键解析为 "--"；命令没有可解析输出时改为逐键查询。
    /// 详情查询在各段齐全时经一次批量 shell 取回，否则逐条执行。缓存中稳定键齐全时用它替代 <c>getprop</c>
    /// 与静态段，使重复的拉取只执行实时查询。</para>
    /// </summary>
    /// <remarks>
    /// Each detail section takes the bundled result when it came back; otherwise the same command is
    /// issued on its own.
    /// <para>各详情段在批量结果返回时取批量结果；否则单独执行同一条命令。</para>
    /// </remarks>
    /// <param name="run">Runs a shell command on the device and returns its raw output.<para>在设备上执行 shell 命令并返回原始输出。</para></param>
    /// <param name="getString">Resolves a localized resource key into display text.<para>将本地化资源键解析为显示文本。</para></param>
    /// <param name="cached">Stable properties from a previous pass, or <see langword="null"/> for a full query.
    /// <para>上一次拉取得到的稳定属性；为 <see langword="null"/> 时执行完整查询。</para></param>
    /// <returns>A key-to-value map of device properties and derived detail strings.<para>设备属性与派生详情字符串的键值映射。</para></returns>
    internal static Dictionary<string, string> ReadAdbProperties(Func<string, string> run, Func<string, string> getString,
        IReadOnlyDictionary<string, string>? cached = null)
    {
        var staticCache = cached is not null && HasEveryStaticProperty(cached) ? cached : null;
        var propertyTable = staticCache is null ? TryReadPropertyTable(run) : null;
        var bundle = TryReadBundle(run, includeStatic: staticCache is null);

        string GetProperty(string name)
        {
            if (propertyTable is null)
            {
                return Clean(run("getprop " + name));
            }

            return propertyTable.TryGetValue(name, out var value) ? Clean(value) : Clean(string.Empty);
        }

        string Section(string key, string command) =>
            bundle is not null && bundle.TryGetValue(key, out var section) ? section : run(command);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (staticCache is not null)
        {
            foreach (var key in StaticKeys)
            {
                map[key] = staticCache[key];
            }
        }
        else
        {
            map["brand"] = GetProperty("ro.product.brand");
            map["model"] = GetProperty("ro.product.model");
            map["android"] = GetProperty("ro.build.version.release");
            map["sdk"] = GetProperty("ro.build.version.sdk");
            map["codename"] = GetProperty("ro.product.device");
            map["cpuabi"] = GetProperty("ro.product.cpu.abi");
            map["cpucode"] = ReadSocPlatform(GetProperty);
            map["kernel"] = Clean(Section("K", "uname -r"));
            map["boardid"] = Clean(Section("B", "cat /sys/devices/soc0/serial_number"));
            map["slot"] = GetProperty("ro.boot.slot_suffix");
            map["compile"] = FirstAvailable(GetProperty,
                "ro.system.build.version.incremental", "ro.build.version.incremental", "ro.build.display.id");
            map["vndk"] = FirstAvailable(GetProperty, "ro.vndk.version", "ro.product.vndk.version");
            map["bl"] = ReadBlState(GetProperty, getString);
        }

        map["resolution"] = ParseWmSize(Section("R", "wm size"));
        map["density"] = ParseWmDensity(Section("D", "wm density"));
        map["uptime"] = FormatUptime(Clean(Section("P", "cat /proc/uptime")), getString);

        var (bLevel, bText) = ParseBattery(Section("Y", "dumpsys battery"));
        map["batteryLevel"] = bLevel.ToString(CultureInfo.InvariantCulture);
        map["batteryText"] = bText;

        var (mLevel, mText) = ParseMem(Section("M", "cat /proc/meminfo"));
        map["memLevel"] = mLevel.ToString(CultureInfo.InvariantCulture);
        map["memText"] = mText;

        var (dLevel, dText) = ParseDisk(Section("F",
            "df -k /storage/emulated 2>/dev/null || df -k /data 2>/dev/null || df -k /sdcard 2>/dev/null"));
        map["diskLevel"] = dLevel.ToString(CultureInfo.InvariantCulture);
        map["diskText"] = dText;

        return map;
    }

    /// <summary>
    /// Reads the whole property table with a single argument-less <c>getprop</c> call and parses its
    /// <c>[name]: [value]</c> lines. Returns <c>null</c> when the output is blank or contains no parsable
    /// line so the caller can fall back to one query per key.
    /// <para>用一次无参 <c>getprop</c> 读取整张属性表并解析 <c>[名称]: [值]</c> 行；输出为空或没有任何可解析行时
    /// 返回 <c>null</c>，供调用方回退到逐键查询。</para>
    /// </summary>
    /// <param name="run">Runs a shell command on the device and returns its raw output.<para>在设备上执行 shell 命令并返回原始输出。</para></param>
    /// <returns>The parsed property table, or <c>null</c> when the batch output is unusable.<para>解析出的属性表；批量输出不可用时为 <c>null</c>。</para></returns>
    private static Dictionary<string, string>? TryReadPropertyTable(Func<string, string> run)
    {
        var raw = run("getprop");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 7 || trimmed[0] != '[')
            {
                continue;
            }

            var separator = trimmed.IndexOf("]: [", StringComparison.Ordinal);
            if (separator <= 1 || trimmed[^1] != ']' || trimmed.Length < separator + 5)
            {
                continue;
            }

            table[trimmed[1..separator]] = trimmed[(separator + 4)..^1];
        }

        return table.Count == 0 ? null : table;
    }

    /// <summary>
    /// Aggregates ADB bootloader lock-state signals into an unlocked, locked, or unknown verdict. Unlocked evidence
    /// wins because some devices keep reporting a locked vbmeta state after unlocking.
    /// <para>汇总 ADB 侧的 Bootloader 锁状态信号，得出已解锁、已上锁或未知的结论。解锁证据优先，因为部分设备解锁后仍上报 locked 的 vbmeta 状态。</para>
    /// </summary>
    /// <param name="getProperty">Resolves one property name and returns the cleaned value; a missing key resolves to "--".<para>解析单个属性名并返回清理后的值；缺失的键解析为 "--"。</para></param>
    /// <param name="getString">Resolves a localized resource key into display text.<para>将本地化资源键解析为显示文本。</para></param>
    /// <returns>The localized unlocked or locked label, or "--" when no signal is present.<para>本地化的解锁或上锁标签；无信号时返回 "--"。</para></returns>
    private static string ReadBlState(Func<string, string> getProperty, Func<string, string> getString)
    {
        var lockstate = getProperty("ro.secureboot.lockstate").ToLowerInvariant();
        var deviceState = getProperty("ro.boot.vbmeta.device_state").ToLowerInvariant();
        var verifiedboot = getProperty("ro.boot.verifiedbootstate").ToLowerInvariant();
        var oemUnlock = getProperty("ro.fastboot.unlock").ToLowerInvariant();

        if (lockstate == "unlocked" || deviceState == "unlocked" || verifiedboot == "orange" || oemUnlock == "true")
        {
            return getString("Device_Unlocked");
        }

        return lockstate == "locked" || deviceState == "locked" || verifiedboot is "green" or "yellow" || oemUnlock != "--"
            ? getString("Device_Locked")
            : "--";
    }

    /// <summary>
    /// Parses <c>wm size</c> output, preferring the override size over the physical size.
    /// <para>解析 <c>wm size</c> 输出，优先使用覆盖分辨率而不是物理分辨率。</para>
    /// </summary>
    /// <param name="raw">Raw <c>wm size</c> output text.<para>原始 <c>wm size</c> 输出文本。</para></param>
    /// <returns>The override or physical size value, or "--" when neither is present.<para>覆盖或物理分辨率值；两者都缺失时返回 "--"。</para></returns>
    internal static string ParseWmSize(string raw) => ParseWmValue(raw, "Override size", "Physical size");

    /// <summary>
    /// Parses <c>wm density</c> output, preferring the override density over the physical density.
    /// <para>解析 <c>wm density</c> 输出，优先使用覆盖密度而不是物理密度。</para>
    /// </summary>
    /// <param name="raw">Raw <c>wm density</c> output text.<para>原始 <c>wm density</c> 输出文本。</para></param>
    /// <returns>The override or physical density value, or "--" when neither is present.<para>覆盖或物理密度值；两者都缺失时返回 "--"。</para></returns>
    internal static string ParseWmDensity(string raw) => ParseWmValue(raw, "Override density", "Physical density");

    private static string ParseWmValue(string raw, string overrideLabel, string physicalLabel)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "--";
        }

        string? physical = null;
        foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            if (name.Equals(overrideLabel, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (name.Equals(physicalLabel, StringComparison.OrdinalIgnoreCase))
            {
                physical = value;
            }
        }

        return physical ?? "--";
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
    /// Formats <c>/proc/uptime</c> seconds or <c>uptime</c> command output into a human-readable duration.
    /// <para>将 <c>/proc/uptime</c> 的秒数或 <c>uptime</c> 命令输出格式化为可读时长。</para>
    /// </summary>
    /// <param name="raw">The uptime output text.<para>uptime 的输出文本。</para></param>
    /// <param name="getString">Resolves a localized resource key into display text.<para>将本地化资源键解析为显示文本。</para></param>
    /// <returns>A day, hour, minute and second duration string, or "--" when parsing fails.<para>按天时分秒拼接的时长字符串；解析失败时返回 "--"。</para></returns>
    internal static string FormatUptime(string raw, Func<string, string> getString)
    {
        var duration = ParseUptime(raw);
        if (duration is null)
        {
            return "--";
        }

        var ts = duration.Value;
        return $"{ts.Days}{getString("Device_UptimeDay")}"
             + $"{ts.Hours}{getString("Device_UptimeHour")}"
             + $"{ts.Minutes}{getString("Device_UptimeMinute")}"
             + $"{ts.Seconds}{getString("Device_UptimeSecond")}";
    }

    /// <summary>
    /// Parses either seconds from <c>/proc/uptime</c> or the clock text printed by <c>uptime</c>.
    /// <para>解析 <c>/proc/uptime</c> 的秒数或 <c>uptime</c> 打印的时钟文本。</para>
    /// </summary>
    /// <param name="raw">The uptime output text.<para>uptime 的输出文本。</para></param>
    /// <returns>The parsed duration, or <c>null</c> when the text is not recognized.<para>解析出的时长；无法识别时为 <c>null</c>。</para></returns>
    internal static TimeSpan? ParseUptime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "--")
        {
            return null;
        }

        var text = raw.Trim();
        var firstSpace = text.IndexOf(' ');
        var head = firstSpace < 0 ? text : text[..firstSpace];
        if (double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        var upIndex = text.IndexOf(" up ", StringComparison.OrdinalIgnoreCase);
        var remainder = upIndex >= 0
            ? text[(upIndex + 4)..].Trim()
            : text.StartsWith("up ", StringComparison.OrdinalIgnoreCase) ? text[3..].Trim() : null;
        return remainder is null ? null : ParseUptimeRemainder(remainder);
    }

    private static TimeSpan? ParseUptimeRemainder(string remainder)
    {
        var sections = remainder.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (sections.Length == 0)
        {
            return null;
        }

        var days = 0;
        var clock = sections[0];
        if (clock.EndsWith("days", StringComparison.OrdinalIgnoreCase)
            || clock.EndsWith("day", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(clock.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
                NumberStyles.Integer, CultureInfo.InvariantCulture, out days))
            {
                return null;
            }

            if (sections.Length < 2)
            {
                return TimeSpan.FromDays(days);
            }

            clock = sections[1];
        }
        else if (clock.EndsWith("mins", StringComparison.OrdinalIgnoreCase)
            || clock.EndsWith("min", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(clock.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                ? TimeSpan.FromMinutes(minutes)
                : null;
        }

        var parts = clock.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutePart))
        {
            return null;
        }

        var secondPart = 0;
        if (parts.Length == 3
            && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out secondPart))
        {
            return null;
        }

        return TimeSpan.FromSeconds(days * 86400L + hours * 3600L + minutePart * 60L + secondPart);
    }

    /// <summary>
    /// Parses the keyed level, voltage and temperature values from dumpsys battery output.
    /// <para>解析 dumpsys battery 输出中带键的电量、电压与温度值。</para>
    /// </summary>
    /// <param name="info">dumpsys battery output text.<para>dumpsys battery 的输出文本。</para></param>
    /// <returns>The battery level percentage together with the voltage and temperature text.<para>电量百分比，以及电压与温度组成的说明文本。</para></returns>
    internal static (int Level, string Text) ParseBattery(string info)
    {
        int? level = null;
        int? voltage = null;
        int? temperature = null;
        foreach (var line in info.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("level", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    level = parsed;
                }
            }
            else if (name.Equals("voltage", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    voltage = parsed;
                }
            }
            else if (name.Equals("temperature", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    temperature = parsed;
                }
            }
        }

        if (level is null || voltage is null || temperature is null)
        {
            return (-1, "--");
        }

        return (Math.Clamp(level.Value, 0, 100),
            $"{voltage.Value / 1000.0:0.00}V {temperature.Value / 10.0:0.0}℃");
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
    /// Parses a one-kilobyte-block df row into a usage percentage and text.
    /// <para>解析以 1 KB 为块的 df 数据行，得到占用百分比与说明文本。</para>
    /// </summary>
    /// <param name="info">One-kilobyte-block df output text.<para>以 1 KB 为块的 df 输出文本。</para></param>
    /// <returns>The disk usage percentage together with the used and total text.<para>磁盘占用百分比，以及已用量与总量的说明文本。</para></returns>
    internal static (int Level, string Text) ParseDisk(string info)
    {
        foreach (var line in info.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 6
                || !columns[^1].StartsWith("/", StringComparison.Ordinal)
                || !long.TryParse(columns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var blocks)
                || !long.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedBlocks)
                || blocks <= 0)
            {
                continue;
            }

            var sizeGb = Math.Round(blocks / 1024.0 / 1024.0, 2);
            var usedGb = Math.Round(usedBlocks / 1024.0 / 1024.0, 2);
            var percent = (int)Math.Round(usedBlocks / (double)blocks * 100);
            return (Math.Clamp(percent, 0, 100), $"{usedGb:0.00}GB/{sizeGb:0.00}GB");
        }

        return (-1, "--");
    }

    /// <summary>
    /// HarmonyOS boot details resolved from a base-cluster dump.
    /// <para>从基础信息转储解析出的 HarmonyOS 启动详情。</para>
    /// </summary>
    /// <param name="SocPlatform">SoC platform name.<para>SoC 平台名称。</para></param>
    /// <param name="BoardId">Board identifier.<para>主板标识。</para></param>
    /// <param name="LockState">Localized bootloader lock state.<para>本地化的 Bootloader 锁定状态。</para></param>
    /// <param name="Slot">Current A/B slot label.<para>当前 A/B 槽位标签。</para></param>
    internal readonly record struct HarmonyBootDetails(string SocPlatform, string BoardId, string LockState, string Slot);

    /// <summary>
    /// Parses HarmonyOS boot parameters from a <c>hidumper -c base</c> dump.
    /// <para>从 <c>hidumper -c base</c> 输出解析 HarmonyOS 启动参数。</para>
    /// </summary>
    /// <param name="dump">Base-cluster dump containing a <c>/proc/cmdline</c> block.<para>包含 <c>/proc/cmdline</c> 段的基础信息转储。</para></param>
    /// <param name="getString">Resolves a localized resource key into display text.<para>将本地化资源键解析为显示文本。</para></param>
    /// <returns>SoC, board id, lock state and slot; absent values resolve to "--".<para>SoC、主板 ID、锁状态与槽位；缺失值解析为 "--"。</para></returns>
    internal static HarmonyBootDetails ParseHarmonyBootDetails(string dump, Func<string, string> getString)
    {
        var parameters = ParseCommandLine(ExtractHarmonyCommandLine(dump));
        string Value(string name) =>
            parameters.TryGetValue(name, out var value) && value.Length > 0 ? value : "--";

        var soc = FirstAvailable(Value, "ohos.boot.hardware", "ohos.boot.chiptype", "hardware");
        var boardId = FirstAvailable(Value, "ohos.boot.board.boardid", "boardid");
        var deviceState = Value("ohos.boot.hvb.device_state").ToLowerInvariant();
        var verifiedState = Value("ohos.boot.hvb.enable").ToLowerInvariant();
        var lockState = deviceState == "unlocked" || verifiedState == "orange"
            ? getString("Device_Unlocked")
            : deviceState == "locked" || verifiedState is "green" or "yellow"
                ? getString("Device_Locked")
                : "--";
        var slot = FirstAvailable(Value, "ohos.boot.slot_suffix", "ohos.boot.slot", "ohos.boot.currentslot");
        var slotLabel = slot switch
        {
            "1" => "Slot A",
            "2" => "Slot B",
            _ => ParseSlot(slot),
        };
        return new HarmonyBootDetails(soc, boardId, lockState, slotLabel);
    }

    /// <summary>
    /// Reads the main display resolution from a DisplayManagerService dump.
    /// <para>从 DisplayManagerService 转储读取主显示屏分辨率。</para>
    /// </summary>
    /// <param name="dump">DisplayManagerService dump text.<para>DisplayManagerService 转储文本。</para></param>
    /// <returns>Width x height, or "--" when the display block is absent.<para>宽 x 高；缺少显示屏段时返回 "--"。</para></returns>
    internal static string ParseHarmonyResolution(string dump)
    {
        int? width = null;
        foreach (var line in dump.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("Width", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWidth))
            {
                width = parsedWidth;
            }
            else if (name.Equals("Height", StringComparison.OrdinalIgnoreCase)
                && width is > 0
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHeight)
                && parsedHeight > 0)
            {
                return $"{width.Value}x{parsedHeight}";
            }
        }

        return "--";
    }

    /// <summary>
    /// Converts the HarmonyOS display density ratio into a dots-per-inch value.
    /// <para>将 HarmonyOS 显示密度比例换算为 DPI 数值。</para>
    /// </summary>
    /// <param name="dump">DisplayManagerService dump text.<para>DisplayManagerService 转储文本。</para></param>
    /// <returns>Density in dots per inch, or "--" when no density is reported.<para>以 DPI 表示的密度；未报告密度时返回 "--"。</para></returns>
    internal static string ParseHarmonyDensity(string dump)
    {
        foreach (var line in dump.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0
                || !line[..separator].Trim().Equals("Density", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (double.TryParse(line[(separator + 1)..].Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var density) && density > 0)
            {
                return ((int)Math.Round(density * 160)).ToString(CultureInfo.InvariantCulture);
            }
        }

        return "--";
    }

    /// <summary>
    /// Parses HarmonyOS battery service output into a level and a voltage with temperature text.
    /// <para>解析 HarmonyOS 电池服务输出，得到电量百分比与电压温度文本。</para>
    /// </summary>
    /// <param name="info">BatteryService <c>-i</c> output text.<para>BatteryService <c>-i</c> 的输出文本。</para></param>
    /// <returns>The battery level percentage together with the voltage and temperature text.<para>电量百分比，以及电压与温度组成的说明文本。</para></returns>
    internal static (int Level, string Text) ParseHarmonyBattery(string info)
    {
        int? capacity = null;
        long? voltageMicrovolts = null;
        int? temperatureTenths = null;
        foreach (var line in info.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("capacity", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    capacity = parsed;
                }
            }
            else if (name.Equals("voltage", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    voltageMicrovolts = parsed;
                }
            }
            else if (name.Equals("temperature", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    temperatureTenths = parsed;
                }
            }
        }

        if (capacity is null || capacity < 0 || voltageMicrovolts is null || voltageMicrovolts <= 0
            || temperatureTenths is null)
        {
            return (-1, "--");
        }

        return (Math.Clamp(capacity.Value, 0, 100),
            $"{voltageMicrovolts.Value / 1_000_000.0:0.00}V {temperatureTenths.Value / 10.0:0.0}℃");
    }

    private static string? ExtractHarmonyCommandLine(string dump)
    {
        var lines = dump.Split(['\r', '\n'], StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Trim().Equals("/proc/cmdline", StringComparison.Ordinal))
            {
                continue;
            }

            for (var j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                return line.StartsWith("cmd is:", StringComparison.Ordinal) ? null : line;
            }
        }

        return null;
    }

    private static Dictionary<string, string> ParseCommandLine(string? commandLine)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return parameters;
        }

        foreach (var token in commandLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            parameters[token[..separator]] = token[(separator + 1)..];
        }

        return parameters;
    }
}