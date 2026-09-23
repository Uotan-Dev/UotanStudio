using System.Globalization;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>An explicit caller selection; the scatter file name is never opened automatically.</summary>
/// <param name="PartitionIndex">Exact scatter SYS index within the caller-selected storage layout.</param>
/// <param name="Kind">Explicit raw, sparse or erase operation, not inferred from scatter image type.</param>
/// <param name="Offset">Byte offset relative to the GPT partition.</param>
/// <param name="Length">Raw/erase byte count or sparse target capacity.</param>
/// <param name="SourcePath">Caller-selected image path, not resolved from the scatter file name.</param>
/// <param name="SourceOffset">Raw source offset; zero for sparse/erase.</param>
/// <param name="Verify">Whether written extents require readback.</param>
public sealed record MtkScatterFlashStep(string PartitionIndex, MtkFlashStepKind Kind, long Offset, long Length,
    string? SourcePath = null, long SourceOffset = 0, bool Verify = false);

public sealed partial class MtkFlashPlan
{
    /// <summary>
    /// Reconciles selected ordinary Scatter entries with GPT before opening any sources.
    /// Platform/loader authorization and explicit source selection remain caller responsibilities.
    /// </summary>
    /// <remarks>
    /// Only user-region UPDATE entries with matching linear/physical/GPT addresses and exact
    /// partition capacity are accepted. Bootloader, reserved, protected and unknown policy
    /// operations require dedicated support; no manifest flag disables range validation.
    /// Scatter block-size hints and file names never override device geometry or source paths.
    /// </remarks>
    /// <param name="snapshot">Validated GPT observation from the intended XFlash session.</param>
    /// <param name="layout">Explicitly selected storage layout; no automatic EMMC/UFS fallback.</param>
    /// <param name="steps">Explicitly selected and ordered operations, not all download-enabled entries.</param>
    /// <param name="cancellationToken">Cancellation for reconciliation and source preflight.</param>
    /// <returns>A retained one-shot plan with normal GPT revalidation at execution.</returns>
    public static MtkFlashPlan Prepare(MtkGptSnapshot snapshot, MtkScatterStorage layout,
        IReadOnlyList<MtkScatterFlashStep> steps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count is 0 or > 256 || layout.Partitions.Count is 0 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), "Scatter selections or partition count exceed plan limits.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var selected = steps.ToArray();
        var (storageName, hardwareName, regionName, regionId) = snapshot.Storage.Kind switch
        {
            MtkXFlashStorageKind.Emmc => ("EMMC", "HW_STORAGE_EMMC", "EMMC_USER", 8u),
            MtkXFlashStorageKind.Ufs => ("UFS", "HW_STORAGE_UFS", "UFS_LU2", 3u),
            _ => throw new NotSupportedException("Unsupported scatter storage family.")
        };
        if (layout.Name != storageName || snapshot.PartitionId != regionId)
        {
            throw new InvalidDataException("The scatter layout and GPT observation must identify the same user region.");
        }
        var entries = new Dictionary<string, MtkScatterPartition>(StringComparer.Ordinal);
        foreach (var entry in layout.Partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null || string.IsNullOrWhiteSpace(entry.Index) || !entries.TryAdd(entry.Index, entry))
            {
                throw new InvalidDataException("The scatter layout has a missing or duplicate partition index.");
            }
        }
        var named = new MtkPartitionFlashStep[selected.Length];
        for (var index = 0; index < selected.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = selected[index] ?? throw new ArgumentException("A scatter selection is null.", nameof(steps));
            ArgumentException.ThrowIfNullOrWhiteSpace(step.PartitionIndex);
            if (!entries.TryGetValue(step.PartitionIndex, out var entry))
            {
                throw new KeyNotFoundException("The selected scatter partition index was not found.");
            }
            if (entry.Storage != hardwareName || entry.Region != regionName)
            {
                throw new NotSupportedException("The selected scatter entry is not in the observed user region.");
            }
            ValidateScatterPolicy(entry);
            var partition = snapshot.Table.FindPartition(entry.Name);
            if (entry.PhysicalStartAddress != (ulong)partition.ByteOffset
                || entry.LinearStartAddress != entry.PhysicalStartAddress || entry.Size != (ulong)partition.ByteLength)
            {
                throw new InvalidDataException("Scatter addresses or capacity disagree with the observed GPT partition.");
            }
            named[index] = new MtkPartitionFlashStep(step.Kind, entry.Name, step.Offset, step.Length,
                step.SourcePath, step.SourceOffset, step.Verify);
        }
        return Prepare(snapshot, named, cancellationToken);
    }

    private static void ValidateScatterPolicy(MtkScatterPartition entry)
    {
        if (!entry.IsDownload || entry.IsReserved || !entry.BoundaryCheck || entry.OperationType != "UPDATE"
            || entry.Type is not ("NORMAL_ROM" or "EXT4_IMG"))
        {
            throw new NotSupportedException("The selected scatter entry requires unsupported policy or image handling.");
        }
        foreach (var (key, value) in entry.Fields)
        {
            switch (key)
            {
                case "partition_index":
                case "partition_name":
                case "file_name":
                case "is_download":
                case "type":
                case "linear_start_addr":
                case "physical_start_addr":
                case "partition_size":
                case "region":
                case "storage":
                case "boundary_check":
                case "is_reserved":
                case "operation_type":
                    break;
                case "is_upgradable":
                case "empty_boot_needed":
                    if (!bool.TryParse(value, out var flag) || flag != (key == "is_upgradable"))
                    {
                        throw new NotSupportedException("The selected scatter entry requires a different upgrade or boot policy.");
                    }
                    break;
                case "reserve":
                    var text = value.AsSpan();
                    var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                    if (!ulong.TryParse(hex ? text[2..] : text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                            CultureInfo.InvariantCulture, out var reserved) || reserved != 0)
                    {
                        throw new NotSupportedException("Nonzero or invalid scatter reserve flags are unsupported.");
                    }
                    break;
                default:
                    throw new NotSupportedException($"The selected scatter entry has an unsupported policy field: {key}.");
            }
        }
    }
}