namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

using ZLinq;

public sealed partial class FlashBundleOrchestrator
{
    private static void ValidateOptions(FlashBundleOptions options)
    {
        if (string.IsNullOrEmpty(options.RawProgramPath))
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "RawProgramPath must be specified. 必须指定 RawProgramPath。");
        }

        if (!File.Exists(options.RawProgramPath))
        {
            throw new EdlException(EdlErrorCode.FileIO, $"Rawprogram file not found: {options.RawProgramPath} rawprogram 文件不存在。");
        }

        if (!string.IsNullOrEmpty(options.PatchPath) && !File.Exists(options.PatchPath))
        {
            throw new EdlException(EdlErrorCode.FileIO, $"Patch file not found: {options.PatchPath} patch 文件不存在。");
        }

        if (string.IsNullOrEmpty(options.ImageDirectory))
        {
            throw new EdlException(EdlErrorCode.InvalidParameter, "ImageDirectory must be specified. 必须指定 ImageDirectory。");
        }
    }
    private static void ResolveSourceFiles(IEnumerable<RawProgramEntry> entries, string imageDirectory)
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.SourceFile) && !string.IsNullOrEmpty(entry.Filename))
            {
                entry.SourceFile = FlashBundlePath.ResolveImage(imageDirectory, entry.Filename);
            }
        }
    }
    private static void ApplySourceOverrides(IEnumerable<RawProgramEntry> entries,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is not { Count: > 0 })
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.SourceFile)
                && overrides.TryGetValue(entry.SourceFile, out var replacement))
            {
                entry.SourceFile = replacement;
            }
        }
    }

    private static List<RawProgramEntry> FilterProgramEntries(
        List<RawProgramEntry> entries, FlashBundleOptions options)
    {
        HashSet<string>? labelSet = options.Labels is { Count: > 0 } labels
            ? new HashSet<string>(labels, StringComparer.OrdinalIgnoreCase) : null;
        HashSet<string>? notLabelSet = options.NotLabels is { Count: > 0 } notLabels
            ? new HashSet<string>(notLabels, StringComparer.OrdinalIgnoreCase) : null;
        if (labelSet is null && notLabelSet is null)
        {
            return entries;
        }

        return entries.AsValueEnumerable()
            .Where(e => (labelSet is null || (!string.IsNullOrEmpty(e.Label) && labelSet.Contains(e.Label)))
                && (notLabelSet is null || string.IsNullOrEmpty(e.Label) || !notLabelSet.Contains(e.Label)))
            .ToList();
    }

    private static List<PatchEntry> FilterPatchEntries(
        List<PatchEntry> entries, FlashBundleOptions options)
    {
        HashSet<string>? labelSet = options.Labels is { Count: > 0 } labels
            ? new HashSet<string>(labels, StringComparer.OrdinalIgnoreCase) : null;
        HashSet<string>? notLabelSet = options.NotLabels is { Count: > 0 } notLabels
            ? new HashSet<string>(notLabels, StringComparer.OrdinalIgnoreCase) : null;
        if (labelSet is null && notLabelSet is null)
        {
            return entries;
        }

        return entries.AsValueEnumerable()
            .Where(e => (labelSet is null || (e.Label != null && labelSet.Contains(e.Label)))
                && (notLabelSet is null || e.Label == null || !notLabelSet.Contains(e.Label)))
            .ToList();
    }

    private static List<WipeManifestEntry> FilterWipeEntries(
        List<WipeManifestEntry> entries, FlashBundleOptions options)
    {
        HashSet<string>? labelSet = options.Labels is { Count: > 0 } labels
            ? new HashSet<string>(labels, StringComparer.OrdinalIgnoreCase) : null;
        HashSet<string>? notLabelSet = options.NotLabels is { Count: > 0 } notLabels
            ? new HashSet<string>(notLabels, StringComparer.OrdinalIgnoreCase) : null;
        if (labelSet is null && notLabelSet is null)
        {
            return entries;
        }

        return entries.AsValueEnumerable()
            .Where(e => (labelSet is null || labelSet.Contains(e.Label))
                && (notLabelSet is null || !notLabelSet.Contains(e.Label)))
            .ToList();
    }

    private static string? ResolveUfsProvisionPath(FlashBundleOptions options)
    {
        if (!string.IsNullOrEmpty(options.UfsProvisionPath))
        {
            var path = options.UfsProvisionPath;
            return !File.Exists(path) ? throw new EdlException(EdlErrorCode.FileIO, $"UFS provision file not found: {path} UFS 配置文件不存在。") : path;
        }

        if (string.IsNullOrEmpty(options.ImageDirectory) || !Directory.Exists(options.ImageDirectory))
        {
            return null;
        }
        var candidates = Directory.EnumerateFiles(options.ImageDirectory, "*.xml", SearchOption.TopDirectoryOnly)
            .AsValueEnumerable()
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return name.Contains("ufs", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("provision", StringComparison.OrdinalIgnoreCase);
            })
            .Take(2)
            .ToArray();
        if (candidates.Length > 1)
        {
            throw new InvalidDataException("Multiple UFS provisioning documents were found; select UfsProvisionPath explicitly.");
        }
        return candidates.Length == 0 ? null : candidates[0];
    }

    private UfsProvisionPlan PrepareUfs(UfsProvisionFile document, FlashBundleOptions options)
    {
        var entries = document.Entries;
        var hasNonBody = false;
        for (var index = 1; index < entries.Count - 1; index++)
        {
            if (entries[index].Value is not UfsProvisionBody)
            {
                hasNonBody = true;
                break;
            }
        }

        if (entries.Count is < 3 or > 258 || entries[0].Value is not UfsProvisionCommon common
            || entries[^1].Value is not UfsProvisionEpilogue epilogue
            || hasNonBody)
        {
            throw new InvalidDataException("UFS provisioning requires one common entry, one or more bodies, then one epilogue.");
        }
        if (epilogue.Commit is not (0 or 1))
        {
            throw new InvalidDataException("UFS commit must be zero or one.");
        }
        var slot = options.Slot ?? RequireFirehose().Configuration.Slot;
        foreach (var entry in entries)
        {
            if (entry.Slot < 0 || (entry.Slot != 0 && entry.Slot != slot))
            {
                throw new InvalidDataException("A UFS entry slot disagrees with the selected bundle slot.");
            }
        }
        return UfsProvisionPlan.Prepare(MapUfsCommon(common), [.. document.Bodies.Select(MapUfsBody)],
            MapUfsEpilogue(epilogue), options.UfsCommit ?? epilogue.Commit == 1, slot);
    }

    private static UfsCommon MapUfsCommon(UfsProvisionCommon source) => new()
    {
        BNumberLU = source.BNumberLU,
        BBootEnable = source.BBootEnable,
        BDescrAccessEn = source.BDescrAccessEn,
        BInitPowerMode = source.BInitPowerMode,
        BHighPriorityLUN = source.BHighPriorityLUN,
        BSecureRemovalType = source.BSecureRemovalType,
        BInitActiveICCLevel = source.BInitActiveICCLevel,
        WPeriodicRTCUpdate = source.WPeriodicRTCUpdate,
        BConfigDescrLock = source.BConfigDescrLock
    };

    private static UfsBody MapUfsBody(UfsProvisionBody source) => new()
    {
        LUNum = source.LUNum,
        BLUEnable = source.BLUEnable,
        BBootLUNID = source.BBootLUNID,
        SizeInKb = source.SizeInKb,
        BDataReliability = source.BDataReliability,
        BLUWriteProtect = source.BLUWriteProtect,
        BMemoryType = source.BMemoryType,
        BLogicalBlockSize = source.BLogicalBlockSize,
        BProvisioningType = source.BProvisioningType,
        WContextCapacities = source.WContextCapacities,
        Desc = source.Desc
    };

    private static UfsEpilogue MapUfsEpilogue(UfsProvisionEpilogue source) => new()
    {
        LUNumToGrow = source.LUNumToGrow
    };

}