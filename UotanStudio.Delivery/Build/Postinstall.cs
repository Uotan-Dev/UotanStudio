using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;

namespace UotanStudio.Services.Delivery;

/// <summary>Reads update_engine postinstall configuration without executing programs on the host.</summary>
internal static class DeliveryPostinstall
{
    internal static Dictionary<string, PartitionUpdate> Read(string path, IEnumerable<string> partitions)
    {
        var result = new Dictionary<string, PartitionUpdate>(StringComparer.Ordinal);
        if (path.Length == 0) return result;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 1024 * 1024) throw new InvalidDataException("Postinstall 配置不存在或超过 1 MiB。");
        var names = partitions.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string[] prefixes = ["RUN_POSTINSTALL_", "POSTINSTALL_PATH_", "FILESYSTEM_TYPE_", "POSTINSTALL_OPTIONAL_"];
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var separator = text.IndexOf('=');
            if (separator < 1) throw new InvalidDataException("Postinstall 配置需要 key=value。");
            var key = text[..separator].Trim();
            var value = text[(separator + 1)..].Trim();
            var prefix = prefixes.FirstOrDefault(p => key.StartsWith(p, StringComparison.Ordinal));
            if (prefix is null || !seen.Add(key) || !names.Contains(key[prefix.Length..]))
                throw new InvalidDataException("未知、重复或不属于当前分区的 Postinstall 配置：" + key);
            var name = key[prefix.Length..];
            if (!result.TryGetValue(name, out var config)) result.Add(name, config = new() { PartitionName = name });
            switch (prefix)
            {
                case "RUN_POSTINSTALL_": config.RunPostinstall = Boolean(value); break;
                case "POSTINSTALL_OPTIONAL_": config.PostinstallOptional = Boolean(value); break;
                case "POSTINSTALL_PATH_":
                    if (value.Length is 0 or > 4096 || value.StartsWith('/') || value.Contains('\\')
                        || value.Any(char.IsControl) || value.Split('/').Any(p => p is "" or "." or ".."))
                        throw new InvalidDataException("Postinstall 路径必须相对于分区根目录，不能包含父目录跳转。");
                    config.PostinstallPath = value; break;
                case "FILESYSTEM_TYPE_":
                    if (value is not ("ext4" or "erofs" or "f2fs")) throw new InvalidDataException("Postinstall 文件系统需要 ext4、erofs 或 f2fs。");
                    config.FilesystemType = value; break;
            }
        }
        foreach (var config in result.Values)
            if (config.RunPostinstall is null) throw new InvalidDataException("请显式指定 RUN_POSTINSTALL_" + config.PartitionName + "=true/false。");
        return result;
    }

    private static bool Boolean(string value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw new InvalidDataException("Postinstall 布尔值需要 true 或 false。")
    };
}