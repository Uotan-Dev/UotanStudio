namespace UotanStudio.Services.Delivery;

/// <summary>Locates sub-projects that contain Super/Payload source settings. <para>定位包含 Super/Payload 源配置的子项目。</para></summary>
internal static class DeliverySourceSettings
{
    /// <summary>
    /// Enumerates direct sub-directories of the solution root and returns only sub-projects containing .uotan/source-super.json or source-payload.json; hidden directories are skipped with a limit of 1000.
    /// <para>枚举解决方案根目录的直接子目录，仅返回包含 .uotan/source-super.json 或 source-payload.json 的子项目；隐藏目录被跳过，上限 1000 个。</para>
    /// </summary>
    internal static IReadOnlyList<(string ProjectRoot, string DisplayName)> FindProjectsWithSettings(string solutionRoot)
    {
        var result = new List<(string ProjectRoot, string DisplayName)>();
        if (string.IsNullOrWhiteSpace(solutionRoot) || !Directory.Exists(solutionRoot)) return result;
        foreach (var directory in Directory.EnumerateDirectories(solutionRoot).Take(1000))
        {
            var name = Path.GetFileName(directory);
            if (name.Length == 0 || name.StartsWith('.')) continue;
            var metadata = Path.Combine(directory, ".uotan");
            if (File.Exists(Path.Combine(metadata, "source-super.json")) || File.Exists(Path.Combine(metadata, "source-payload.json")))
                result.Add((directory, name));
        }
        return result;
    }
}