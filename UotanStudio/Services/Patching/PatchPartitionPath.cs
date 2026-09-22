namespace UotanStudio.Services.Patching;

/// <summary>Explicit directory-layout mapping; it does not infer partition identity from filenames.</summary>
public static class PatchPartitionPath
{
    /// <summary>Resolves a partition-local file into the selected workspace without following links.</summary>
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
        // Validate before adding a prefix so an absolute path cannot become an apparently relative one.
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