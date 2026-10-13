namespace UotanStudio.Services.Patching;

/// <summary>Explicit directory-layout mapping; it does not infer partition identity from filenames.<para>显式的目录布局映射，不从文件名推断分区身份。</para></summary>
public static class PatchPartitionPath
{
    /// <summary>Resolves a partition-local file into the selected workspace without following links.<para>将分区内文件解析到所选工作区，且不跟随链接。</para></summary>
    /// <param name="workspace">The selected workspace directory.<para>所选工作区目录。</para></param>
    /// <param name="partition">The partition name to map.<para>要映射的分区名。</para></param>
    /// <param name="layout">The directory layout selector.<para>目录布局选择器。</para></param>
    /// <param name="path">The partition-local file path.<para>分区内的文件路径。</para></param>
    /// <returns>The mapped workspace-relative path.<para>映射后的工作区相对路径。</para></returns>
    public static string Resolve(PatchWorkspace workspace, string partition, string layout, string path)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (partition is not ("system" or "vendor" or "product" or "system_ext" or "odm"
            or "vendor_dlkm" or "odm_dlkm" or "system_dlkm"))
        {
            throw new InvalidDataException(LanguageService.Instance.GetString("Node_Patch_InvalidPartition"));
        }
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\\') || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException(LanguageService.Instance.GetString("Node_Patch_InvalidPartitionPath"));
        }
        var relative = path.StartsWith('/') ? path[1..] : path;
        PatchPath.ValidateRelative(relative);
        var mapped = layout switch
        {
            "PartitionDirectories" => partition + "/" + relative,
            "PartitionRoot" => relative,
            "SystemAsRoot" when partition == "system" => "system/" + relative,
            _ => throw new InvalidDataException(LanguageService.Instance.GetString("Node_Patch_InvalidPartitionLayout"))
        };
        workspace.Resolve(mapped);
        return mapped;
    }
}