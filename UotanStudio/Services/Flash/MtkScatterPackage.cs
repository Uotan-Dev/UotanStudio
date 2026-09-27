using System.Globalization;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

namespace UotanStudio.Services.Flash;

/// <summary>Resolved one-storage scatter package: the selected layout, its display label and the rows
/// whose declared images exist inside the scatter directory and satisfy the host scatter policy.</summary>
/// <param name="Storage">The selected storage layout, ready for <c>MtkFlashService.PrepareScatterAsync</c>.</param>
/// <param name="StorageLabel">The layout name shown to the user (for example <c>EMMC</c> or <c>UFS</c>)
/// so a multi-storage scatter makes the automatic choice visible instead of implicit.</param>
/// <param name="Rows">Resolvable downloadable rows in scatter order, at most the host plan-step limit.</param>
public sealed record MtkScatterPackageResolution(MtkScatterStorage Storage, string StorageLabel,
    IReadOnlyList<MtkScatterPackageRow> Rows);

/// <summary>A scatter partition whose declared image was found on disk inside the scatter directory.</summary>
/// <param name="Partition">The manifest entry; its <c>Index</c> is the exact scatter SYS token.</param>
/// <param name="ImagePath">Normalized absolute path of the existing image file.</param>
/// <param name="Length">On-disk byte length at resolution time; sparse semantics are applied per step.</param>
public sealed record MtkScatterPackageRow(MtkScatterPartition Partition, string ImagePath, long Length);

/// <summary>A DA directory entry with a complete bounded stage-one/stage-two region pair, ready for
/// explicit index selection in the UI. MtkLoaderProfile deliberately carries no entry names, so the
/// hardware identity fields are the stable display key.</summary>
/// <param name="Index">Zero-based directory index to submit as the explicit entry selection.</param>
/// <param name="HardwareCode">Target hardware code; zero means the entry lacks identity.</param>
/// <param name="HardwareSubCode">Hardware subcode.</param>
/// <param name="HardwareVersion">Declared hardware revision.</param>
/// <param name="SoftwareVersion">Declared software revision, or <see langword="null"/> in legacy layouts.</param>
/// <param name="StageOneRegion">Region index that passed DA1 geometry validation.</param>
/// <param name="StageTwoRegion">Region index that passed DA2 geometry validation.</param>
public sealed record MtkLoaderEntryInfo(int Index, ushort HardwareCode, ushort HardwareSubCode,
    ushort HardwareVersion, ushort? SoftwareVersion, int StageOneRegion, int StageTwoRegion);

/// <summary>Host-side reader and resolver for MediaTek scatter packages: it opens bounded scatter text,
/// lists the downloadable rows that are actually present on disk, and builds explicit scatter flash
/// steps without contacting a device.</summary>
/// <remarks>
/// Scatter files, their names and their addresses are untrusted input. Every quantity is gated before
/// it is used, declared file names are never globbed, and a resolved image path must stay inside the
/// scatter directory. Row eligibility mirrors <c>MtkFlashPlan.ValidateScatterPolicy</c> entry-for-entry
/// (download enabled, not reserved, boundary check set, <c>UPDATE</c> operation, an accepted image
/// type, and only policy fields the plan understands), so the UI never offers a row that the plan
/// would reject for policy. Resolution performs no device I/O and authorizes nothing; it only reports
/// what the package and manifest agree on.
/// </remarks>
public static class MtkScatterPackage
{
    /// <summary>Hard ceiling on the scatter text file size in bytes. The Firmware reader re-bounds the
    /// decoded character count, but this gate rejects oversized files before any bytes are opened.</summary>
    public const long MaxScatterBytes = 8 * 1024 * 1024;

    /// <summary>Maximum resolvable rows, matching the plan-step limit enforced by
    /// <c>MtkFlashPlan.Prepare</c> and <c>MtkFlashService.PrepareScatterAsync</c>.</summary>
    public const int MaxPlanSteps = 256;

    private const int MaxLayoutPartitions = 4096;
    private const int MaxStorages = 8;

    /// <summary>Reads a scatter file from disk after a bounded size check.</summary>
    /// <param name="scatterPath">Explicit path to the scatter YAML file.</param>
    /// <returns>The parsed document with all storage layouts in source order.</returns>
    /// <exception cref="ArgumentException"><paramref name="scatterPath"/> is empty or whitespace.</exception>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="scatterPath"/>.</exception>
    /// <exception cref="InvalidDataException">The file exceeds <see cref="MaxScatterBytes"/> or the
    /// bounded reader rejects the scatter structure.</exception>
    public static MtkScatterDocument Read(string scatterPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scatterPath);
        var length = new FileInfo(scatterPath).Length;
        if (length is <= 0 or > MaxScatterBytes)
        {
            throw new InvalidDataException($"A scatter file must be between 1 and {MaxScatterBytes} bytes; found {length}.");
        }
        using var stream = File.OpenRead(scatterPath);
        return MtkScatterReader.Read(stream);
    }

    /// <summary>Resolves which images of a parsed scatter document are present next to the scatter file.</summary>
    /// <remarks>
    /// With a single storage layout that one is used. With several layouts, the first one that yields at
    /// least one policy-eligible row with an existing image is selected and its name is returned as
    /// <see cref="MtkScatterPackageResolution.StorageLabel"/> for visible display; nothing is guessed
    /// beyond that deterministic order, and a caller that knows the device storage family can bypass
    /// the choice by passing the desired <see cref="MtkScatterStorage"/> to the plan directly.
    /// A declared file name is accepted only when its normalized path stays inside the scatter
    /// directory and the file exists exactly; escape attempts and missing files drop the row silently
    /// because the manifest, not the disk, remains the authority on what the package contains.
    /// </remarks>
    /// <param name="scatterPath">The path the document was read from; its directory anchors file names.</param>
    /// <param name="document">A previously parsed scatter document.</param>
    /// <returns>The selected layout with its resolvable rows in manifest order.</returns>
    /// <exception cref="ArgumentException"><paramref name="scatterPath"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The document or a layout exceeds the host count limits,
    /// contains null entries, or the selected layout resolves to more than <see cref="MaxPlanSteps"/> rows.</exception>
    public static MtkScatterPackageResolution Resolve(string scatterPath, MtkScatterDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(scatterPath);
        var storages = document.Storages;
        if (storages is null || storages.Count is 0 or > MaxStorages)
        {
            throw new InvalidDataException($"A scatter document must contain between 1 and {MaxStorages} storage layouts.");
        }
        string directory;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(scatterPath)) ?? string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"The scatter path cannot be normalized: '{scatterPath}'.", exception);
        }
        if (directory.Length == 0)
        {
            throw new InvalidDataException("The scatter path has no parent directory to anchor image names.");
        }
        var root = directory + Path.DirectorySeparatorChar;
        MtkScatterStorage? selected = null;
        IReadOnlyList<MtkScatterPackageRow> selectedRows = [];
        foreach (var storage in storages)
        {
            if (storage is null)
            {
                throw new InvalidDataException("The scatter document contains a null storage layout.");
            }
            selected ??= storage;
            var rows = BuildRows(directory, root, storage);
            if (rows.Count > 0)
            {
                selected = storage;
                selectedRows = rows;
                break;
            }
        }
        if (selectedRows.Count > MaxPlanSteps)
        {
            throw new InvalidDataException(
                $"Too many downloadable scatter partitions in layout '{selected!.Name}'; the host prepares at most {MaxPlanSteps} plan steps.");
        }
        return new MtkScatterPackageResolution(selected!, selected!.Name, selectedRows);
    }

    /// <summary>Builds the explicit scatter flash step for one resolved row.</summary>
    /// <remarks>
    /// <c>PartitionIndex</c> carries the exact SYS token from the manifest because the plan looks steps
    /// up by ordinal string equality against <c>MtkScatterPartition.Index</c>. The operation kind is
    /// never inferred from the scatter <c>type</c> field; it is decided by inspecting the image itself,
    /// mirroring the host sparse probe in <c>FlashService.GetImageLogicalSize</c>: a valid sparse header
    /// selects <see cref="MtkFlashStepKind.Sparse"/> with the expanded target capacity as length (the
    /// meaning the plan assigns to sparse steps), while an unreadable or non-sparse header stays raw with
    /// the on-disk length. <see cref="MtkFlashStepKind.Erase"/> is never produced here because the
    /// scatter policy admits only <c>UPDATE</c> operations and an erase is a caller decision without a
    /// manifest counterpart. The write starts at offset zero of the GPT partition and the source is read
    /// from its first byte; both are reconciled against live GPT during preparation.
    /// </remarks>
    /// <param name="resolution">A resolution previously returned by <see cref="Resolve"/>.</param>
    /// <param name="rowIndex">Zero-based index into <see cref="MtkScatterPackageResolution.Rows"/>.</param>
    /// <param name="verify">Whether written extents require readback; passed through unchanged.</param>
    /// <returns>The step ready for <c>MtkFlashService.PrepareScatterAsync</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> or its rows are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowIndex"/> is outside the rows,
    /// or the row count is beyond the host plan-step limit.</exception>
    /// <exception cref="InvalidDataException">A row or its partition entry is null, or a valid sparse
    /// header declares a target capacity that overflows or is empty.</exception>
    public static MtkScatterFlashStep CreateStep(MtkScatterPackageResolution resolution, int rowIndex, bool verify)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var rows = resolution.Rows ?? throw new ArgumentNullException(nameof(resolution),
            "The scatter resolution carries no rows.");
        if (rows.Count is 0 or > MaxPlanSteps)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution),
                $"A scatter resolution must contain between 1 and {MaxPlanSteps} rows.");
        }
        if (rowIndex < 0 || rowIndex >= rows.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        }
        var row = rows[rowIndex] ?? throw new InvalidDataException("The scatter resolution contains a null row.");
        var partition = row.Partition ?? throw new InvalidDataException("A scatter row references a null partition entry.");
        if (string.IsNullOrWhiteSpace(partition.Index))
        {
            throw new InvalidDataException("A scatter row references a partition without an index token.");
        }
        var (kind, length) = ResolveKindAndLength(row);
        return new MtkScatterFlashStep(partition.Index, kind, 0, length, row.ImagePath, 0, verify);
    }

    /// <summary>Maps an observed storage family to the XFlash physical user-region identifier.</summary>
    /// <remarks>
    /// The values mirror the plan reconciliation table in <c>MtkFlashPlan.Prepare</c>: GPT observation on
    /// eMMC targets region 8 (<c>EMMC_USER</c>) and on UFS targets region 3 (<c>UFS_LU2</c>). NOR and NAND
    /// have no scatter user-region concept and are rejected, matching the plan's supported storage family.
    /// </remarks>
    /// <param name="kind">The storage family reported by the device.</param>
    /// <returns>The physical partition identifier to pass to preparation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not eMMC or UFS.</exception>
    public static uint GetPhysicalPartition(MtkXFlashStorageKind kind) => kind switch
    {
        MtkXFlashStorageKind.Emmc => 8,
        MtkXFlashStorageKind.Ufs => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind),
            "Only eMMC and UFS scatter storage families have a physical user region.")
    };

    /// <summary>Lists the DA directory entries of a loader file that carry a complete usable DA1/DA2
    /// region pair, in original directory-index order.</summary>
    /// <remarks>
    /// This is a pure file-side probe: the container is parsed and hashed, no device is contacted and
    /// nothing is uploaded. Entries without a bounded region pair are omitted because the profile
    /// treats them as structurally defective; the returned <see cref="MtkLoaderEntryInfo.Index"/> is the
    /// directory index for an explicit UI selection, and hardware identity is the display key because
    /// loader entries intentionally have no names.
    /// </remarks>
    /// <param name="loaderPath">Explicit path to an all-in-one DA loader container.</param>
    /// <param name="cancellationToken">Cancellation for hashing and parsing.</param>
    /// <returns>One row per usable entry; empty when the container offers none.</returns>
    /// <exception cref="ArgumentException"><paramref name="loaderPath"/> is empty or whitespace.</exception>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="loaderPath"/>.</exception>
    /// <exception cref="InvalidDataException">The container fails structural validation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static async Task<IReadOnlyList<MtkLoaderEntryInfo>> ListUsableLoaderEntriesAsync(string loaderPath,
        CancellationToken cancellationToken = default)
    {
        var profile = await MtkLoaderProfile.FromFileAsync(loaderPath, cancellationToken).ConfigureAwait(false);
        var entries = profile.Entries ?? throw new InvalidDataException("The loader profile exposes no entry directory.");
        var usable = new List<MtkLoaderEntryInfo>(entries.Count);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null)
            {
                throw new InvalidDataException("The loader profile contains a null entry descriptor.");
            }
            if (entry.UsableRegionPair is not { } pair)
            {
                continue;
            }
            usable.Add(new(entry.EntryIndex, entry.HardwareCode, entry.HardwareSubCode,
                entry.HardwareVersion, entry.SoftwareVersion, pair.StageOne, pair.StageTwo));
        }
        return usable;
    }

    private static IReadOnlyList<MtkScatterPackageRow> BuildRows(string directory, string root, MtkScatterStorage storage)
    {
        if (storage.Partitions is null || storage.Partitions.Count is 0 or > MaxLayoutPartitions)
        {
            throw new InvalidDataException(
                $"Scatter layout '{storage.Name}' must contain between 1 and {MaxLayoutPartitions} partitions.");
        }
        var rows = new List<MtkScatterPackageRow>();
        foreach (var partition in storage.Partitions)
        {
            if (partition is null)
            {
                throw new InvalidDataException($"Scatter layout '{storage.Name}' contains a null partition entry.");
            }
            if (!MatchesUserRegion(storage, partition) || !MatchesScatterPolicy(partition))
            {
                continue;
            }
            if (TryResolveImage(directory, root, partition.FileName, out var imagePath))
            {
                rows.Add(new(partition, imagePath, new FileInfo(imagePath).Length));
            }
        }
        return rows;
    }

    /// <summary>Keeps only entries in the user region the plan would reconcile for this layout name;
    /// other layouts (for example NAND scatters) yield no rows because preparation rejects them.</summary>
    private static bool MatchesUserRegion(MtkScatterStorage storage, MtkScatterPartition partition) =>
        storage.Name switch
        {
            "EMMC" => partition.Storage == "HW_STORAGE_EMMC" && partition.Region == "EMMC_USER",
            "UFS" => partition.Storage == "HW_STORAGE_UFS" && partition.Region == "UFS_LU2",
            _ => false
        };

    /// <summary>Mirrors <c>MtkFlashPlan.ValidateScatterPolicy</c> as a filter: an entry stays eligible
    /// only when the plan would accept it, including the sweep over every literal policy field.</summary>
    private static bool MatchesScatterPolicy(MtkScatterPartition partition)
    {
        if (!partition.IsDownload || partition.IsReserved || !partition.BoundaryCheck
            || partition.OperationType != "UPDATE"
            || partition.Type is not ("NORMAL_ROM" or "EXT4_IMG")
            || string.Equals(partition.FileName, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (partition.Fields is null)
        {
            return false;
        }
        foreach (var (key, value) in partition.Fields)
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
                    if (!bool.TryParse(value, out var upgradable) || !upgradable)
                    {
                        return false;
                    }
                    break;
                case "empty_boot_needed":
                    if (!bool.TryParse(value, out var bootNeeded) || bootNeeded)
                    {
                        return false;
                    }
                    break;
                case "reserve":
                    var text = value.AsSpan();
                    var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                    if (!ulong.TryParse(hex ? text[2..] : text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                            CultureInfo.InvariantCulture, out var reserved) || reserved != 0)
                    {
                        return false;
                    }
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static bool TryResolveImage(string directory, string root, string fileName, out string imagePath)
    {
        imagePath = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(directory, fileName));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or PathTooLongException or InvalidDataException)
        {
            return false;
        }
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
        {
            return false;
        }
        imagePath = candidate;
        return true;
    }

    private static (MtkFlashStepKind Kind, long Length) ResolveKindAndLength(MtkScatterPackageRow row)
    {
        long capacity;
        try
        {
            var header = SparseReader.PeekHeader(row.ImagePath);
            if (!header.IsValid())
            {
                return (MtkFlashStepKind.Raw, row.Length);
            }
            capacity = checked((long)header.TotalBlocks * header.BlockSize);
        }
        catch (IOException)
        {
            return (MtkFlashStepKind.Raw, row.Length);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                $"The sparse image for scatter partition '{row.Partition.Name}' declares a target capacity beyond the supported range.",
                exception);
        }
        if (capacity <= 0)
        {
            throw new InvalidDataException(
                $"The sparse image for scatter partition '{row.Partition.Name}' declares no logical blocks.");
        }
        return (MtkFlashStepKind.Sparse, capacity);
    }
}