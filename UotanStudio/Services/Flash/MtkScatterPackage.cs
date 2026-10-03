using System.Globalization;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.VendorFirmware.MediaTek;

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
        var row = RowAt(resolution, rowIndex);
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
        => partition.IsDownload && !partition.IsReserved && partition.BoundaryCheck
            && partition.OperationType == "UPDATE"
            && partition.Type is ("NORMAL_ROM" or "EXT4_IMG")
            && !string.Equals(partition.FileName, "NONE", StringComparison.OrdinalIgnoreCase)
            && MatchesPolicyFields(partition);

    /// <summary>Erasing is not a download, so the download-only gates — <c>is_download</c>,
    /// <c>operation_type</c>, the image <c>type</c> and the declared file name — do not apply while
    /// the region, reservation, boundary and recognized-policy-field guards stay exactly as strict.</summary>
    private static bool MatchesErasePolicy(MtkScatterPartition partition)
        => !partition.IsReserved && partition.BoundaryCheck
            && partition.Size is > 0 and <= long.MaxValue
            && MatchesPolicyFields(partition);

    /// <summary>Sweeps every literal policy field so an entry with unknown or contradicting policy is
    /// rejected instead of silently acted upon.</summary>
    private static bool MatchesPolicyFields(MtkScatterPartition partition)
    {
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

    /// <summary>
    /// Lists the entries of one storage layout that may be erased, whether or not their declared
    /// image exists on disk.
    /// <para>列出一个存储布局中可被擦除的条目，无论其声明的镜像是否存在于磁盘。</para>
    /// </summary>
    /// <remarks>
    /// Erasing needs no source file, so an entry whose <c>file_name</c> is <c>NONE</c> or whose image
    /// is absent stays eligible, while the user-region, reservation, boundary and recognized-policy
    /// field guards remain identical to the flash filter. Every range is still reconciled against the
    /// live GPT during preparation, and nothing here contacts a device.
    /// <para>擦除不需要源文件，因此 <c>file_name</c> 为 <c>NONE</c> 或镜像缺失的条目依然可选；
    /// 用户区域、保留位、边界检查与已识别策略字段的守卫与刷写过滤完全一致。所有范围仍在准备阶段
    /// 与实机 GPT 核对，本方法不接触设备。</para>
    /// </remarks>
    /// <param name="storage"><para>已解析的存储布局。</para>The parsed storage layout.</param>
    /// <returns><para>按清单顺序排列的可擦除条目。</para>Erased-eligible entries in manifest order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> 为 null。
    /// <para><paramref name="storage"/> is null.</para></exception>
    /// <exception cref="InvalidDataException">布局缺失、条目数量越界或包含空条目。
    /// <para>The layout is missing, out of bounds, or contains a null entry.</para></exception>
    public static IReadOnlyList<MtkScatterPartition> ListEraseTargets(MtkScatterStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (storage.Partitions is null || storage.Partitions.Count is 0 or > MaxLayoutPartitions)
        {
            throw new InvalidDataException(
                $"Scatter layout '{storage.Name}' must contain between 1 and {MaxLayoutPartitions} partitions.");
        }

        var targets = new List<MtkScatterPartition>(storage.Partitions.Count);
        foreach (var partition in storage.Partitions)
        {
            if (partition is null)
            {
                throw new InvalidDataException($"Scatter layout '{storage.Name}' contains a null partition entry.");
            }

            if (MatchesUserRegion(storage, partition) && MatchesErasePolicy(partition))
            {
                targets.Add(partition);
            }
        }

        return targets;
    }

    /// <summary>
    /// Builds the erase operation for one scatter entry; the range is the entry's declared capacity
    /// and the live GPT re-checks it during preparation.
    /// <para>为一个 scatter 条目构造擦除操作；范围取该条目声明的容量，准备阶段由实机 GPT 复核。</para>
    /// </summary>
    /// <param name="partition"><para>可擦除的 scatter 条目，通常来自 <see cref="ListEraseTargets"/>。</para>
    /// The erase-eligible scatter entry, usually from <see cref="ListEraseTargets"/>.</param>
    /// <returns><para>可直接交给 <c>MtkFlashService</c> 的 GPT 命名擦除步骤。</para>
    /// A GPT-named erase step ready for <c>MtkFlashService</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="partition"/> 为 null。
    /// <para><paramref name="partition"/> is null.</para></exception>
    /// <exception cref="InvalidDataException">条目缺少分区名或声明容量不可用。
    /// <para>The entry has no partition name or declares an unusable capacity.</para></exception>
    public static MtkPartitionFlashStep CreateEraseStep(MtkScatterPartition partition)
    {
        var (name, size) = EraseRange(partition);
        return new MtkPartitionFlashStep(MtkFlashStepKind.Erase, name, 0, size, null, 0, Verify: false);
    }

    /// <summary>
    /// Builds the raw write operation for one resolved row addressed by GPT partition name, for the
    /// Legacy download agent.
    /// <para>为一个按 GPT 分区名寻址的已解析条目构造 Legacy 下载代理使用的原始写入操作。</para>
    /// </summary>
    /// <remarks>
    /// The Legacy agent has no sparse transport, so a row whose image carries a valid sparse container is
    /// rejected here instead of being written as raw bytes. The extent starts at offset zero of the named
    /// partition and covers the image rounded up to the agent's 512-byte block, with the tail explicitly
    /// zero-filled; that mirrors the reference tools, which round the transfer length up to a whole block.
    /// The live GPT re-checks the name and the partition bounds during preparation.
    /// <para>Legacy 代理没有稀疏传输，因此镜像带有有效稀疏容器的条目在此被拒绝，而不是作为原始字节写入。
    /// 范围从该命名分区的零偏移开始，覆盖向上取整到代理 512 字节块的镜像长度，尾部显式补零；这与参考工具
    /// “传输长度向上取整到整块”的行为一致。准备阶段由实机 GPT 复核分区名与分区边界。</para>
    /// </remarks>
    /// <param name="resolution">A resolution previously returned by <see cref="Resolve"/>.</param>
    /// <param name="rowIndex">Zero-based index into <see cref="MtkScatterPackageResolution.Rows"/>.</param>
    /// <param name="verify">Whether written extents require readback; passed through unchanged.</param>
    /// <returns>The step ready for <c>MtkFlashService.PrepareLegacyAsync</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> or its rows are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowIndex"/> is outside the rows.</exception>
    /// <exception cref="InvalidDataException">A row or its partition entry is null, the entry has no
    /// partition name, or the image is longer than the host range limits.</exception>
    /// <exception cref="NotSupportedException">The row's image is a sparse container.</exception>
    public static MtkLegacyPartitionFlashStep CreateLegacyStep(MtkScatterPackageResolution resolution,
        int rowIndex, bool verify)
    {
        var (row, name) = RawWriteRow(resolution, rowIndex, "Legacy");
        return new MtkLegacyPartitionFlashStep(name, MtkFlashStepKind.Raw, 0,
            AlignToBlock(row.Length), row.ImagePath, 0, verify, PadSourceTail: true);
    }

    /// <summary>
    /// Builds the Legacy erase operation for one scatter entry; the range is the entry's declared
    /// capacity rounded up to the agent's block size.
    /// <para>为一个 scatter 条目构造 Legacy 擦除操作；范围取该条目声明容量并向上取整到代理块大小。</para>
    /// </summary>
    /// <param name="partition">The erase-eligible scatter entry, usually from <see cref="ListEraseTargets"/>.
    /// <para>可擦除的 scatter 条目，通常来自 <see cref="ListEraseTargets"/>。</para></param>
    /// <returns><para>可直接交给 <c>MtkFlashService.PrepareLegacyAsync</c> 的 GPT 命名擦除步骤。</para>
    /// A GPT-named erase step ready for <c>MtkFlashService.PrepareLegacyAsync</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="partition"/> 为 null。
    /// <para><paramref name="partition"/> is null.</para></exception>
    /// <exception cref="InvalidDataException">条目缺少分区名或声明容量不可用。
    /// <para>The entry has no partition name or declares an unusable capacity.</para></exception>
    public static MtkLegacyPartitionFlashStep CreateLegacyEraseStep(MtkScatterPartition partition)
    {
        var (name, size) = EraseRange(partition);
        return new MtkLegacyPartitionFlashStep(name, MtkFlashStepKind.Erase, 0, AlignToBlock(size));
    }

    /// <summary>Maps an observed storage family to the Legacy agent's physical region identifier.</summary>
    /// <remarks>
    /// Legacy eMMC exposes the user area as region 8, the same region the XFlash plan reconciles; NOR and
    /// NAND have no scatter user-region concept and the Legacy flash path rejects them because the plan
    /// carries eMMC page geometry only.
    /// <para>Legacy eMMC 把用户区呈现为区域 8，与 XFlash 计划复核的区域相同；NOR 与 NAND 没有 scatter
    /// 用户区概念，且其清单不携带 eMMC 页几何信息，因此 Legacy 刷写路径拒绝它们。</para>
    /// </remarks>
    /// <param name="kind">The storage family reported by the device. <para>设备上报的存储家族。</param>
    /// <returns>The region identifier to pass to Legacy preparation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not eMMC.</exception>
    public static byte GetLegacyPhysicalPartition(MtkXFlashStorageKind kind) => kind switch
    {
        MtkXFlashStorageKind.Emmc => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(kind),
            "Only eMMC has a Legacy agent user region; the Legacy flash path does not serve UFS.")
    };

    private static MtkScatterPackageRow RowAt(MtkScatterPackageResolution resolution, int rowIndex)
    {
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
        return rows[rowIndex] ?? throw new InvalidDataException("The scatter resolution contains a null row.");
    }

    /// <summary>
    /// Extracts the partition name and declared capacity an erase needs: the Legacy, XML and XFlash erase
    /// builders differ only in block granularity, so the name and capacity guards have to be identical or
    /// the same entry would be treated differently per family.
    /// <para>取出擦除所需的分区名与声明容量：Legacy、XML 与 XFlash 三种擦除步骤只在块粒度上不同，
    /// 名称与容量的守卫必须逐字一致，否则同一条目会在不同家族下被差别对待。</para>
    /// </summary>
    /// <param name="partition"><para>可擦除的 scatter 条目。</para>The erase-eligible scatter entry.</param>
    /// <returns><para>分区名与按有符号范围校验过的声明容量。</para>The partition name and the capacity
    /// validated against the signed range.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="partition"/> 为 null。
    /// <para><paramref name="partition"/> is null.</para></exception>
    /// <exception cref="InvalidDataException">条目缺少分区名或声明容量不可用。
    /// <para>The entry has no partition name or declares an unusable capacity.</para></exception>
    private static (string Name, long Size) EraseRange(MtkScatterPartition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (string.IsNullOrWhiteSpace(partition.Name))
        {
            throw new InvalidDataException("A scatter entry has no partition name to erase.");
        }
        if (partition.Size is 0 or > long.MaxValue)
        {
            throw new InvalidDataException(
                $"Scatter partition '{partition.Name}' declares a capacity that cannot be erased.");
        }
        return (partition.Name, (long)partition.Size);
    }

    /// <summary>
    /// <para>取出只支持原始字节的家族（Legacy 与 XML）所需的写入条目：两者都必须有 GPT 分区名，也都必须
    /// 拒绝稀疏容器，因为把稀疏头当原始字节写入会写坏分区。日志文案里的家族名是唯一的差别。</para>
    /// Extracts the row a raw-only family (Legacy and XML) may write: both require a GPT partition name and
    /// both must reject a sparse container, because streaming a sparse header as raw bytes would corrupt the
    /// partition. The family name inside the messages is the only difference.
    /// <para>Extracts the row a raw-only family (Legacy and XML) may write: both require a GPT partition
    /// name and both must reject a sparse container, because streaming a sparse header as raw bytes would
    /// corrupt the partition. The family name inside the messages is the only difference.</para>
    /// </summary>
    /// <param name="resolution"><para>此前由 <see cref="Resolve"/> 返回的解析结果。</para>A resolution
    /// previously returned by <see cref="Resolve"/>.</param>
    /// <param name="rowIndex"><para>行序号。</para>Zero-based row index.</param>
    /// <param name="agent"><para>用于文案的家族名。</para>The family name used in the messages.</param>
    /// <returns><para>可写入的已解析行与其 GPT 分区名。</para>The resolvable row to write and its GPT
    /// partition name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> 或其行为 null。
    /// <para><paramref name="resolution"/> or its rows are null.</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowIndex"/> 越界。
    /// <para><paramref name="rowIndex"/> is outside the rows.</para></exception>
    /// <exception cref="InvalidDataException">行或其分区条目为 null，或条目缺少分区名。
    /// <para>The row or its partition entry is null, or the entry has no partition name.</para></exception>
    /// <exception cref="NotSupportedException">该行的镜像是稀疏容器。
    /// <para>The row's image is a sparse container.</para></exception>
    private static (MtkScatterPackageRow Row, string Name) RawWriteRow(MtkScatterPackageResolution resolution,
        int rowIndex, string agent)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var row = RowAt(resolution, rowIndex);
        var partition = row.Partition ?? throw new InvalidDataException("A scatter row references a null partition entry.");
        if (string.IsNullOrWhiteSpace(partition.Name))
        {
            throw new InvalidDataException($"A scatter row has no partition name for the {agent} agent to address.");
        }
        if (ResolveKindAndLength(row).Kind == MtkFlashStepKind.Sparse)
        {
            throw new NotSupportedException(
                $"The {agent} download agent cannot write the sparse image of scatter partition '{partition.Name}'.");
        }
        return (row, partition.Name);
    }

    /// <summary>
    /// Builds the raw write operation for one resolved row addressed by GPT partition name, for the XML
    /// download agent, rounding the extent up to the block granularity the scatter layout declares.
    /// <para>为一个按 GPT 分区名寻址的已解析条目构造 XML 下载代理使用的原始写入操作，范围向上取整到
    /// scatter 布局声明的块粒度。</para>
    /// </summary>
    /// <remarks>
    /// <para>Like the Legacy builder, the XML agent has no sparse transport, so a row whose image carries a
    /// valid sparse container is rejected instead of being written as raw bytes. The extent starts at offset
    /// zero of the named partition, covers the image, and is rounded up to the scatter layout's
    /// <c>block_size</c> because the agent requires block-aligned ranges; the tail beyond the image is
    /// explicitly zero-filled. The live GPT re-checks the name and the partition bounds during preparation, and
    /// the agent's own reported block size still has to divide the result.</para>
    /// <para>与 Legacy 构造器一样，XML 代理没有稀疏传输，因此镜像带有有效稀疏容器的条目会被拒绝，而不是作为
    /// 原始字节写入。范围从该命名分区的零偏移开始，覆盖镜像长度，并向上取整到 scatter 布局的 <c>block_size</c>，
    /// 因为代理要求范围按块对齐；超出镜像的尾部显式补零。准备阶段由实机 GPT 复核分区名与分区边界，代理自报的
    /// 块大小仍必须整除该结果。</para>
    /// </remarks>
    /// <param name="resolution">A resolution previously returned by <see cref="Resolve"/>.</param>
    /// <param name="rowIndex">Zero-based index into <see cref="MtkScatterPackageResolution.Rows"/>.</param>
    /// <param name="verify">Whether written extents require readback; passed through unchanged.</param>
    /// <returns>The step ready for <c>MtkFlashService.PrepareXmlAsync</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> or its rows are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowIndex"/> is outside the rows, or the
    /// layout declares a block granularity outside 512 through 16 MiB.</exception>
    /// <exception cref="InvalidDataException">A row or its partition entry is null, or the entry has no
    /// partition name.</exception>
    /// <exception cref="NotSupportedException">The row's image is a sparse container.</exception>
    public static MtkXmlPartitionFlashStep CreateXmlStep(MtkScatterPackageResolution resolution, int rowIndex,
        bool verify)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var block = XmlBlockSize(resolution);
        var (row, name) = RawWriteRow(resolution, rowIndex, "XML");
        return new MtkXmlPartitionFlashStep(name, MtkFlashStepKind.Raw, 0,
            AlignToBlock(row.Length, block), row.ImagePath, 0, verify, PadSourceTail: true);
    }

    /// <summary>
    /// Builds the XML erase operation for one scatter entry; the range is the entry's declared capacity
    /// rounded up to the block granularity the scatter layout declares.
    /// <para>为一个 scatter 条目构造 XML 擦除操作；范围取该条目声明容量并向上取整到 scatter 布局声明的块粒度。</para>
    /// </summary>
    /// <param name="resolution">A resolution previously returned by <see cref="Resolve"/>.</param>
    /// <param name="partition">The erase-eligible scatter entry, usually from <see cref="ListEraseTargets"/>.
    /// <para>可擦除的 scatter 条目，通常来自 <see cref="ListEraseTargets"/>。</para></param>
    /// <returns><para>可直接交给 <c>MtkFlashService.PrepareXmlAsync</c> 的 GPT 命名擦除步骤。</para>
    /// A GPT-named erase step ready for <c>MtkFlashService.PrepareXmlAsync</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> 或 <paramref name="partition"/> 为 null。
    /// <para><paramref name="resolution"/> or <paramref name="partition"/> is null.</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">布局声明的块粒度越界。
    /// <para>The layout declares a block granularity outside the supported range.</para></exception>
    /// <exception cref="InvalidDataException">条目缺少分区名或声明容量不可用。
    /// <para>The entry has no partition name or declares an unusable capacity.</para></exception>
    public static MtkXmlPartitionFlashStep CreateXmlEraseStep(MtkScatterPackageResolution resolution,
        MtkScatterPartition partition)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var block = XmlBlockSize(resolution);
        var (name, size) = EraseRange(partition);
        return new MtkXmlPartitionFlashStep(name, MtkFlashStepKind.Erase, 0, AlignToBlock(size, block));
    }

    /// <summary>Maps an observed storage family to the XML agent's named user-data region.</summary>
    /// <remarks>
    /// The tokens are the canonical ones the reference tool sends for the "user" partition of each family:
    /// <c>EMMC-USER</c> for eMMC and <c>UFS-LUA2</c> for UFS. NOR and NAND have no scatter user-region concept
    /// and are rejected, matching the XML agent's block-storage geometry.
    /// <para>这些标记是参考工具为各家族 “user” 分区发送的规范值：eMMC 为 <c>EMMC-USER</c>，UFS 为
    /// <c>UFS-LUA2</c>。NOR 与 NAND 没有 scatter 用户区概念，且 XML 代理只描述块存储几何信息，因此被拒绝。</para>
    /// </remarks>
    /// <param name="kind">The storage family reported by the device. <para>设备上报的存储家族。</param>
    /// <returns>The region token to pass to XML preparation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not eMMC or UFS.</exception>
    public static string GetXmlRegionName(MtkXFlashStorageKind kind) => kind switch
    {
        MtkXFlashStorageKind.Emmc => MtkXmlStorageInfo.GetUserDataRegionName(MtkXmlStorageKind.Emmc),
        MtkXFlashStorageKind.Ufs => MtkXmlStorageInfo.GetUserDataRegionName(MtkXmlStorageKind.Ufs),
        _ => throw new ArgumentOutOfRangeException(nameof(kind),
            "Only eMMC and UFS have an XML agent user-data region.")
    };

    private static int XmlBlockSize(MtkScatterPackageResolution resolution)
    {
        var block = resolution.Storage?.BlockSize
            ?? throw new InvalidOperationException("The scatter resolution carries no storage layout.");
        if (block is < 512 or > 16 * 1024 * 1024 || block % 512 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution),
                $"The scatter layout declares block size 0x{block:x}; extents must round to a multiple of 512 between 512 and 16 MiB.");
        }
        return (int)block;
    }

    private static long AlignToBlock(long length) => AlignToBlock(length, 512);

    private static long AlignToBlock(long length, int block)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, long.MaxValue - block);
        return (length + block - 1) / block * block;
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