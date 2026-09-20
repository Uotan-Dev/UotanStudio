using UotanStudio.Firmware.BoundedContexts.FileSystem.Metadata;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;

/// <summary>Resolves filesystem packing options and sidecars under the captured metadata context.</summary>
internal static class FileSystemPackingOptions
{
    internal static IReadOnlyDictionary<string, string> OptionsFor(string directory, string formatName)
    {
        var metadata = RepackMetadata.Read(directory);
        if (metadata is null
            || (metadata.FormatName != formatName && metadata.FormatName is not ("EXT4" or "EROFS" or "F2FS")))
        {
            return new Dictionary<string, string>();
        }
        var options = new Dictionary<string, string>(metadata.PackingOptions, StringComparer.Ordinal);
        if (RepackMetadata.PackingOverrides is { } overrides)
        {
            foreach (var pair in overrides)
            {
                options[pair.Key] = pair.Value;
            }
        }
        try
        {
            var metadataDirectory = Path.GetDirectoryName(RepackMetadata.ResolveActualMetadataPath(directory)) ?? directory;
            var document = FileSystemMetadataOverrides.TryLoadDocument(metadataDirectory, metadata.SourceFileName, metadata.FileSystemPreservationId);
            if (document is { PartitionInfo.Count: > 0 })
            {
                foreach (var (key, value) in document.PartitionInfo)
                {
                    if (!options.ContainsKey(key))
                    {
                        options[key] = value;
                    }
                }
            }
            else
            {
                var infoPath = RepackMetadata.DiscoverMetadataFile(directory, "_info", metadata.SourceFileName);
                if (infoPath is not null)
                {
                    foreach (var (key, value) in FileSystemMetadataOverrides.LoadPartitionInfo(infoPath))
                    {
                        if (!options.ContainsKey(key))
                        {
                            options[key] = value;
                        }
                    }
                }
            }

            if (!options.ContainsKey("FsConfigPath"))
            {
                options["FsConfigPath"] = RepackMetadata.DiscoverMetadataFile(directory, "_fs_config", metadata.SourceFileName) ?? string.Empty;
            }
            if (!options.ContainsKey("FileContextsPath"))
            {
                options["FileContextsPath"] = RepackMetadata.DiscoverMetadataFile(directory, "_file_contexts", metadata.SourceFileName) ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return options;
    }

    internal static int IntegerOption(IReadOnlyDictionary<string, string> options, string key, int fallback)
        => options.TryGetValue(key, out var value)
            ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    /// <summary>Gets an option as a nullable path: empty values (sidecar file absent) become null.
    /// <para>以可空路径形式获取选项：空值（附属文件不存在）转换为 <c>null</c>。</para></summary>
    internal static string? NullIfEmpty(IReadOnlyDictionary<string, string> options, string key)
        => options.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
}