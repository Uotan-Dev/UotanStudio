using System.Collections.Frozen;

namespace UotanStudio.Services.Delivery;

/// <summary>Validation shared by preview, script generation, and file publication.</summary>
internal static class DeliveryValidation
{
    /// <summary>Windows reserved device names, frozen with an OrdinalIgnoreCase comparer so repeated validation
    /// allocates neither an array nor a substring.
    /// <para>Windows 保留设备名；以 OrdinalIgnoreCase 冻结成集合，使逐次校验不再分配数组或子串。</para></summary>
    private static readonly FrozenSet<string> ReservedDeviceNames =
        new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether a name is a publishable script filename (updater-script, update-binary, or .sh/.bat/.txt).
    /// <para>判断名称是否为可发布的脚本文件名（updater-script、update-binary 或 .sh/.bat/.txt）。</para>
    /// </summary>
    internal static bool IsScriptName(string name) => IsName(name) && (name == "updater-script" || name == "update-binary"
        || name.EndsWith(".sh", StringComparison.Ordinal) || name.EndsWith(".bat", StringComparison.Ordinal)
        || name.EndsWith(".txt", StringComparison.Ordinal));

    /// <summary>
    /// Determines whether a value is a valid file/entry name: ASCII alphanumerics, underscore, hyphen, or dot, excluding Windows reserved device names.
    /// <para>判断数值是否为合法文件/项名称：ASCII 字母数字、下划线、连字符或点，不含 Windows 保留设备名。</para>
    /// </summary>
    internal static bool IsName(string value, int maximum = 100)
    {
        if (value is null || value.Length is 0 || value.Length > maximum || value is "." or ".."
            || value.StartsWith('.') || value.EndsWith('.')
            || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            return false;
        }

        var dot = value.IndexOf('.');
        var stem = value.AsSpan(0, dot < 0 ? value.Length : dot);
        return !ReservedDeviceNames.GetAlternateLookup<ReadOnlySpan<char>>().Contains(stem);
    }

    /// <summary>
    /// Determines whether a value is a valid partition name: up to 36 ASCII alphanumerics or underscores, plus hyphens in payload mode.
    /// <para>判断数值是否为合法分区名：最长 36 个 ASCII 字母数字或下划线，payload 模式额外允许连字符。</para>
    /// </summary>
    internal static bool IsPartition(string value, bool payload = false) => value is not null && value.Length is > 0 and <= 36
        && value[0] != '-'
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || payload && c == '-');

    /// <summary>
    /// Validates the plan's output mode, image membership, container, scripts, and generated entries.
    /// Super size must be entered explicitly because no guessed default can match every device: the value is baked into
    /// the LP geometry and the generated scripts compare it against the device reading.
    /// With Super_Empty only, Empty excludes Zstd and the sole generated entry is super_empty.img; a portable
    /// Zstd is an optional override whose provided paths must exist, otherwise the tool service downloads the
    /// pinned release.
    /// <para>校验交付计划的输出方式、镜像归属、容器、脚本与生成条目的一致性。
    /// 仅生成 Super_Empty 时 Empty 与 Zstd 互斥，且唯一生成条目是 super_empty.img；便携 Zstd 只作可选覆盖，
    /// 提供时路径必须存在，缺失时由工具服务自动下载钉住版本兜底。</para>
    /// </summary>
    /// <param name="plan">The delivery plan to validate. <para>要校验的交付计划。</para></param>
    /// <param name="sources">Whether to check source files for existence and size. <para>是否校验源文件的存在与大小。</para></param>
    internal static void Validate(DeliveryPlan plan, bool sources = true)
    {
        plan = plan.ForOutput();
        if (!plan.OrganizeDirectory && !plan.PackSuper && !plan.PackPayload) throw new InvalidDataException("请选择交付方式 / Select an output mode.");
        if ((plan.PackSuper || plan.PackPayload) && plan.OrganizeDirectory && !plan.ContainerConfirmed)
            throw new InvalidDataException("请先确认容器配置 / Confirm the container first.");
        if ((plan.PackSuper || plan.PackPayload) && !plan.IsSuperEmptyOnly && plan.Container.Count == 0) throw new InvalidDataException("Super 分区列表为空 / No Super partitions.");
        if (plan.OrganizeDirectory && plan.Files.Count == 0) throw new InvalidDataException("没有交付文件 / No delivery files.");
        if (plan.Files.Count > 10000 || plan.Container.Count > 10000) throw new InvalidDataException("Too many delivery entries.");
        if (plan.Scripts.Count > 10000 || plan.Scripts.Any(p => !IsScriptName(p.Key)
            || p.Value.Length > 1024 * 1024)) throw new InvalidDataException("Invalid delivery scripts.");
        if (plan.Slot is not ("" or "a" or "b" or "all")) throw new InvalidDataException("Invalid slot policy.");
        if (plan.Super.DeviceType is not ("" or "VAB" or "AB" or "AOnly")) throw new InvalidDataException("Invalid device type.");
        if (plan.DeviceCodename.Length > 0 && !IsName(plan.DeviceCodename, 64)) throw new InvalidDataException("机型代号无效 / Invalid device codename.");
        if (plan.OrganizeDirectory && plan.ArchiveName.Length > 0 && !IsName(plan.ArchiveName, 200))
            throw new InvalidDataException("压缩包名称无效 / Invalid archive name.");
        if (plan.Information.Length > 65536 || plan.Information.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
            || plan.Information.ReplaceLineEndings("\n").Split('\n').Any(line => line.Length > 1024))
            throw new InvalidDataException("作者信息过长或含无效控制字符 / Author information is too long or contains control characters.");
        if (plan.PackPayload && plan.PackSuper) throw new InvalidDataException("Choose one container format.");
        foreach (var file in plan.Files.Concat(plan.Container))
            if (file.FileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                || file.FileName.EndsWith(".br", StringComparison.OrdinalIgnoreCase)
                || file.FileName.EndsWith(".transfer.list", StringComparison.OrdinalIgnoreCase))
            {
                if (!DeliveryPackageBuilder.IsDatArtifact(file.FileName))
                    throw new NotSupportedException("DAT/BR 刷写须使用同组的 <分区>.transfer.list 与 <分区>.new.dat[.br] 文件。");
                if (!plan.Recovery || plan.PackSuper || plan.PackPayload)
                    throw new NotSupportedException("Android DAT 文件须选择 Recovery 卡刷；转换为 IMG 后方可用于 fastboot、Super 或 Payload。");
            }
        if (plan.PackPayload) DeliveryPayloadBuilder.Validate(plan);
        DeliveryPackageBuilder.Validate(plan);
        if (sources && DeliveryPackageBuilder.HasDat(plan)) DeliveryRecoveryUpdater.Resolve(plan);
        if (plan.PackSuper) DeliverySuperCapacity.ValidateDeviceSize(plan.Super);
        if (plan.PackSuper && plan.Super.Empty && plan.Super.Zstd)
            throw new InvalidDataException("仅生成Super_Empty 与压缩为ZST 不能同时选择 / Super_Empty only excludes ZST compression.");
        if (plan.Super.ZstdLevel is < 0 or > 22)
            throw new InvalidDataException("Super Zstd 压缩等级无效 / Invalid Super Zstd level.");
        var generated = plan.Files.Where(f => f.Generated).ToArray();
        if (generated.Any(f => !(plan.PackSuper || plan.PackPayload) || f.SourcePath.Length != 0 || f.Optional
            || f.Partition != (f.FileName == "super_empty.img" ? "super_empty" : plan.PackPayload ? "payload" : "super")
            || f.FileName != (f.Partition == "super_empty" ? "super_empty.img" : DeliveryOutputService.ContainerFileName(plan)))
            || plan.OrganizeDirectory && (plan.PackSuper || plan.PackPayload) && generated.Length != 1)
            throw new InvalidDataException("生成的 Super 条目与配置不一致 / Generated Super does not match the plan.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in plan.Files)
        {
            if (!IsName(file.FileName) || !names.Add(file.FileName))
                throw new InvalidDataException($"交付文件名无效或重名 / Invalid or duplicate output filename: {file.FileName}");
            if (!IsPartition(file.Partition)) throw new InvalidDataException($"分区名无效 / Invalid partition: {file.Partition}");
            if (sources && !file.Generated) CheckSource(file, DeliveryPackageBuilder.IsDatArtifact(file.FileName)
                && file.FileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase));
        }
        foreach (var group in plan.Files.GroupBy(f => f.Partition, StringComparer.Ordinal))
        {
            var isDatBundle = group.All(f => DeliveryPackageBuilder.IsDatArtifact(f.FileName))
                && group.Select(f => DeliveryPackageBuilder.DatStem(f.FileName)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
            if (group.Count() > 1 && !isDatBundle && (group.Any(f => !f.Optional) || group.Count(f => f.Default) != 1))
                throw new InvalidDataException($"同分区镜像须设为可选，并指定一个默认项 / Set alternatives and one default: {group.Key}");
        }
        foreach (var file in plan.Container)
        {
            if (!IsPartition(file.Partition, plan.PackPayload)) throw new InvalidDataException($"分区名无效 / Invalid partition: {file.Partition}");
            if (sources && file.SourcePath.Length > 0)
                CheckSource(file, plan.Super.AutoZero && file.Partition.EndsWith("_b", StringComparison.Ordinal));
        }
        if (plan.PackSuper && (!IsPartition(plan.Super.DeviceName) || !IsPartition(plan.Super.Group)))
            throw new InvalidDataException("Super 设备名或分组名无效 / Invalid Super device or group.");
        if (sources && plan.OrganizeDirectory && plan.Files.Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)))
            foreach (var platform in new[] { ("Windows", plan.Windows), ("Linux", plan.Linux), ("macOS", plan.MacOS) }.Where(p => p.Item2))
            {
                var path = plan.LocalZstd.GetValueOrDefault(platform.Item1, "");
                if (path.Length > 0 && !File.Exists(path))
                    throw new FileNotFoundException("便携 Zstd 路径无效 / Portable Zstd not found: " + platform.Item1, path);
            }
    }

    /// <summary>
    /// Validates a single source image exists, is not a symlink, matches its declared length, and is non-empty unless allowed.
    /// <para>校验单个源镜像存在、非符号链接、长度与声明一致且非空（除非允许空）。</para>
    /// </summary>
    internal static void CheckSource(DeliveryFile file, bool allowEmpty = false)
    {
        var info = new FileInfo(file.SourcePath);
        if (!info.Exists) throw new FileNotFoundException("源文件不存在 / Source is missing", file.SourcePath);
        DeliveryOutputService.CheckDirectory(info.DirectoryName!);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked source files are not supported: " + file.SourcePath);
        if (file.SourceLength is { } length && info.Length != length)
            throw new InvalidDataException("源文件已变化，请刷新 / Source changed; refresh: " + file.SourcePath);
        if (info.Length == 0 && !allowEmpty) throw new InvalidDataException("源镜像为空 / Empty image: " + file.SourcePath);
    }
}