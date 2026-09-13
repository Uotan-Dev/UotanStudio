namespace UotanStudio.Services.Delivery;

/// <summary>Validation shared by preview, script generation, and file publication.</summary>
internal static class DeliveryValidation
{
    internal static bool IsName(string value, int maximum = 100) => value is not null && value.Length is > 0 && value.Length <= maximum
        && value is not "." and not ".." && !value.StartsWith('.') && !value.EndsWith('.')
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
        && !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(value.Split('.')[0], StringComparer.OrdinalIgnoreCase);

    internal static bool IsPartition(string value) => value is not null && value.Length is > 0 and <= 36
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    internal static void Validate(DeliveryPlan plan, bool sources = true)
    {
        plan = plan.ForOutput();
        if (!plan.OrganizeDirectory && !plan.PackSuper && !plan.PackPayload) throw new InvalidDataException("请选择交付方式 / Select an output mode.");
        if ((plan.PackSuper || plan.PackPayload) && plan.OrganizeDirectory && !plan.ContainerConfirmed)
            throw new InvalidDataException("请先确认容器配置 / Confirm the container first.");
        if ((plan.PackSuper || plan.PackPayload) && !plan.IsSuperEmptyOnly && plan.Container.Count == 0) throw new InvalidDataException("Super 分区列表为空 / No Super partitions.");
        if (plan.OrganizeDirectory && plan.Files.Count == 0) throw new InvalidDataException("没有交付文件 / No delivery files.");
        if (plan.Files.Count > 10000 || plan.Container.Count > 10000) throw new InvalidDataException("Too many delivery entries.");
        if (plan.Scripts.Count > 10000 || plan.Scripts.Any(p => !IsName(p.Key)
            || !(p.Key.EndsWith(".sh", StringComparison.Ordinal) || p.Key.EndsWith(".bat", StringComparison.Ordinal) || p.Key.EndsWith(".txt", StringComparison.Ordinal))
            || p.Value.Length > 1024 * 1024)) throw new InvalidDataException("Invalid delivery scripts.");
        if (plan.Slot is not ("" or "a" or "b" or "all")) throw new InvalidDataException("Invalid slot policy.");
        if (plan.Super.DeviceType is not ("" or "VAB" or "AB" or "AOnly")) throw new InvalidDataException("Invalid device type.");
        if (plan.DeviceCodename.Length > 0 && !IsName(plan.DeviceCodename, 64)) throw new InvalidDataException("机型代号无效 / Invalid device codename.");
        if (plan.PackPayload && plan.PackSuper) throw new InvalidDataException("Choose one container format.");
        if (plan.PackPayload) DeliveryPayloadBuilder.Validate(plan);
        DeliveryPackageBuilder.Validate(plan);
        // 仅生成Super_Empty：Empty 与 Zstd 互斥，且唯一生成条目是 super_empty.img。
        // Super_Empty only: Empty excludes Zstd, and the single generated entry must be super_empty.img.
        if (plan.PackSuper && plan.Super.Empty && plan.Super.Zstd)
            throw new InvalidDataException("仅生成Super_Empty 与压缩为ZST 不能同时选择 / Super_Empty only excludes ZST compression.");
        var generated = plan.Files.Where(f => f.Generated).ToArray();
        // 新语义：Empty 时唯一生成条目是 super_empty.img，不再同时生成完整 super。
        // New semantics: with Empty, the only generated entry is super_empty.img; the full super is no longer produced.
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
            if (sources && !file.Generated) CheckSource(file);
        }
        foreach (var group in plan.Files.GroupBy(f => f.Partition, StringComparer.Ordinal))
        {
            if (group.Count() > 1 && (group.Any(f => !f.Optional) || group.Count(f => f.Default) != 1))
                throw new InvalidDataException($"同分区镜像须设为可选，并指定一个默认项 / Set alternatives and one default: {group.Key}");
        }
        foreach (var file in plan.Container)
        {
            if (!IsPartition(file.Partition)) throw new InvalidDataException($"分区名无效 / Invalid partition: {file.Partition}");
            if (sources && file.SourcePath.Length > 0)
                CheckSource(file, plan.Super.AutoZero && file.Partition.EndsWith("_b", StringComparison.Ordinal));
        }
        if (plan.PackSuper && (!IsPartition(plan.Super.DeviceName) || !IsPartition(plan.Super.Group)))
            throw new InvalidDataException("Super 设备名或分组名无效 / Invalid Super device or group.");
        // 便携 Zstd 仅作可选覆盖：有提供且路径存在时通过，缺失时由 DeliveryToolService 自动下载钉住版本兜底。
        // A portable Zstd is an optional override: provided paths must exist; otherwise DeliveryToolService
        // falls back to downloading the pinned release, so nothing is required here.
        if (sources && plan.OrganizeDirectory && plan.Files.Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)))
            foreach (var platform in new[] { ("Windows", plan.Windows), ("Linux", plan.Linux), ("macOS", plan.MacOS) }.Where(p => p.Item2))
            {
                var path = plan.LocalZstd.GetValueOrDefault(platform.Item1, "");
                if (path.Length > 0 && !File.Exists(path))
                    throw new FileNotFoundException("便携 Zstd 路径无效 / Portable Zstd not found: " + platform.Item1, path);
            }
    }

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
