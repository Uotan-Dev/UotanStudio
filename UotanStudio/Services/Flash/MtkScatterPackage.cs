using System.Globalization;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.VendorFirmware.MediaTek;

namespace UotanStudio.Services.Flash;

/// <summary>Resolved one-storage scatter package: the selected layout, its display label and the rows
/// whose declared images exist inside the scatter directory and satisfy the scatter policy.
/// <para>已解析的单存储 scatter 包：所选布局、其显示标签，以及声明镜像存在于 scatter 目录内且满足 scatter 策略的条目。</para></summary>
/// <param name="Storage">The selected storage layout.<para>所选的存储布局。</para></param>
/// <param name="StorageLabel">The layout name, for example <c>EMMC</c> or <c>UFS</c>.
/// <para>布局名称，例如 <c>EMMC</c> 或 <c>UFS</c>。</para></param>
/// <param name="Rows">Resolvable downloadable rows in scatter order, bounded by the plan-step limit.
/// <para>按 scatter 顺序的可解析下载条目，受计划步数上限约束。</para></param>
public sealed record MtkScatterPackageResolution(MtkScatterStorage Storage, string StorageLabel,
    IReadOnlyList<MtkScatterPackageRow> Rows);

/// <summary>A scatter partition whose declared image was found on disk inside the scatter directory.
/// <para>位于 scatter 目录内且声明镜像已在磁盘找到的 scatter 分区。</para></summary>
/// <param name="Partition">The manifest entry; its <c>Index</c> is the exact scatter SYS token.
/// <para>清单条目；其 <c>Index</c> 是精确的 scatter SYS 标记。</para></param>
/// <param name="ImagePath">Normalized absolute path of the existing image file.
/// <para>已存在镜像文件的规范化绝对路径。</para></param>
/// <param name="Length">On-disk byte length at resolution time.
/// <para>解析时的磁盘字节长度。</para></param>
public sealed record MtkScatterPackageRow(MtkScatterPartition Partition, string ImagePath, long Length);

/// <summary>A DA directory entry with a complete bounded stage-one/stage-two region pair, keyed for explicit index selection by its hardware identity fields.
/// <para>带有完整有界 stage-one/stage-two 区域对的 DA 目录条目，以硬件身份字段作为显式索引选择的键。</para></summary>
/// <param name="Index">Zero-based directory index to submit as the explicit entry selection.
/// <para>作为显式条目选择提交的从零开始目录索引。</para></param>
/// <param name="HardwareCode">Target hardware code; zero means the entry lacks identity.
/// <para>目标硬件码；为零表示该条目缺少身份信息。</para></param>
/// <param name="HardwareSubCode">Hardware subcode.<para>硬件子码。</para></param>
/// <param name="HardwareVersion">Declared hardware revision.<para>声明的硬件修订版本。</para></param>
/// <param name="SoftwareVersion">Declared software revision, or <see langword="null"/> in legacy layouts.
/// <para>声明的软件修订版本，在 Legacy 布局中为 <see langword="null"/>。</para></param>
/// <param name="StageOneRegion">Region index that passed DA1 geometry validation.
/// <para>通过 DA1 几何校验的区域索引。</para></param>
/// <param name="StageTwoRegion">Region index that passed DA2 geometry validation.
/// <para>通过 DA2 几何校验的区域索引。</para></param>
public sealed record MtkLoaderEntryInfo(int Index, ushort HardwareCode, ushort HardwareSubCode,
    ushort HardwareVersion, ushort? SoftwareVersion, int StageOneRegion, int StageTwoRegion);

/// <summary>Reader and resolver for MediaTek scatter packages: it opens bounded scatter text, lists
/// downloadable rows present on disk, and builds scatter flash steps without contacting a device.
/// <para>读取并解析 MediaTek scatter 包：打开有界 scatter 文本，列出磁盘上存在的可下载条目，并在不接触设备的情况下构造 scatter 刷写步骤。</para></summary>
public static class MtkScatterPackage
{
    /// <summary>Hard ceiling on the scatter text file size in bytes.
    /// <para>scatter 文本文件大小的字节数硬上限。</para></summary>
    public const long MaxScatterBytes = 8 * 1024 * 1024;

    /// <summary>Maximum resolvable rows, matching the plan-step limit enforced during preparation.
    /// <para>可解析条目数的最大值，与准备阶段强制的计划步数上限一致。</para></summary>
    public const int MaxPlanSteps = 256;

    private const int MaxLayoutPartitions = 4096;
    private const int MaxStorages = 8;

    /// <summary>Reads a scatter file from disk after a bounded size check.
    /// <para>在大小上限检查后从磁盘读取 scatter 文件。</para></summary>
    /// <param name="scatterPath">Explicit path to the scatter YAML file.
    /// <para>scatter YAML 文件的显式路径。</para></param>
    /// <returns>The parsed document with all storage layouts in source order.
    /// <para>按源顺序包含全部存储布局的已解析文档。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="scatterPath"/> is empty or whitespace.
    /// <para><paramref name="scatterPath"/> 为空或仅空白。</para></exception>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="scatterPath"/>.
    /// <para><paramref name="scatterPath"/> 处不存在文件。</para></exception>
    /// <exception cref="InvalidDataException">The file exceeds <see cref="MaxScatterBytes"/> or the bounded
    /// reader rejects the scatter structure. <para>文件超过 <see cref="MaxScatterBytes"/>，或有界读取器拒绝 scatter 结构。</para></exception>
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

    /// <summary>Resolves which images of a parsed scatter document are present next to the scatter file.
    /// <para>解析已读入的 scatter 文档中，哪些镜像存在于 scatter 文件旁。</para></summary>
    /// <param name="scatterPath">The path the document was read from; its directory anchors file names.
    /// <para>文档的读取路径；其目录用于锚定文件名。</para></param>
    /// <param name="document">An already parsed scatter document.
    /// <para>已解析的 scatter 文档。</para></param>
    /// <returns>The selected layout with its resolvable rows in manifest order.
    /// <para>按清单顺序连同可解析条目的所选布局。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="scatterPath"/> is empty or whitespace.
    /// <para><paramref name="scatterPath"/> 为空或仅空白。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.
    /// <para><paramref name="document"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The document exceeds count limits, holds null entries, or resolves past <see cref="MaxPlanSteps"/> rows.
    /// <para>文档超出数量上限、含空条目或解析出超过 <see cref="MaxPlanSteps"/> 条。</para></exception>
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

    /// <summary>Builds the explicit scatter flash step for one resolved row.
    /// <para>为一条已解析条目构造显式 scatter 刷写步骤。</para></summary>
    /// <param name="resolution">A resolution already returned by <see cref="Resolve"/>.
    /// <para>由 <see cref="Resolve"/> 返回的解析结果。</para></param>
    /// <param name="rowIndex">Zero-based index into <see cref="MtkScatterPackageResolution.Rows"/>.
    /// <para>指向 <see cref="MtkScatterPackageResolution.Rows"/> 的从零开始索引。</para></param>
    /// <param name="verify">Whether written extents require readback; passed through unchanged.
    /// <para>写入范围是否需要回读；原样透传。</para></param>
    /// <returns>The scatter flash step for this row.<para>该行的 scatter 刷写步骤。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> or its rows are null.
    /// <para><paramref name="resolution"/> 或其行为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowIndex"/> is outside the rows or the row count exceeds the plan-step limit.
    /// <para><paramref name="rowIndex"/> 越界，或行数超出计划步数上限。</para></exception>
    /// <exception cref="InvalidDataException">A row or its partition entry is null, or a valid sparse header declares a target capacity that overflows or is empty.
    /// <para>某行或其分区条目为 null，或有效稀疏头声明的目标容量溢出或为空。</para></exception>
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

    /// <summary>Maps an observed storage family to the XFlash physical user-region identifier.
    /// <para>把观察到的存储家族映射为 XFlash 物理用户区域标识符。</para></summary>
    /// <param name="kind">The storage family reported by the device.
    /// <para>设备上报的存储家族。</para></param>
    /// <returns>The physical partition identifier to pass to preparation.
    /// <para>传给准备阶段的物理分区标识符。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not eMMC or UFS.
    /// <para><paramref name="kind"/> 不是 eMMC 或 UFS。</para></exception>
    public static uint GetPhysicalPartition(MtkXFlashStorageKind kind) => kind switch
    {
        MtkXFlashStorageKind.Emmc => 8,
        MtkXFlashStorageKind.Ufs => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind),
            "Only eMMC and UFS scatter storage families have a physical user region.")
    };

    /// <summary>Lists the DA directory entries of a loader file that carry a complete usable DA1/DA2 region pair, in original directory-index order.
    /// <para>列出加载器文件中带有完整可用 DA1/DA2 区域对的 DA 目录条目，按原目录索引顺序排列。</para></summary>
    /// <param name="loaderPath">Explicit path to an all-in-one DA loader container.
    /// <para>一体化 DA 加载器容器的显式路径。</para></param>
    /// <param name="cancellationToken">Cancellation for hashing and parsing.
    /// <para>用于哈希与解析的取消令牌。</para></param>
    /// <returns>One row per usable entry; empty when the container offers none.
    /// <para>每个可用条目一行；容器无可提供时为空。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="loaderPath"/> is empty or whitespace.
    /// <para><paramref name="loaderPath"/> 为空或仅空白。</para></exception>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="loaderPath"/>.
    /// <para><paramref name="loaderPath"/> 处不存在文件。</para></exception>
    /// <exception cref="InvalidDataException">The container fails structural validation.
    /// <para>容器未通过结构校验。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.
    /// <para>已请求取消。</para></exception>
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
    /// other layouts (for example NAND scatters) yield no rows because preparation rejects them.
    /// <para>仅保留该布局名在准备阶段会复核的用户区域条目；其他布局（例如 NAND scatter）不产生任何行，因为准备阶段会拒绝它们。</para></summary>
    /// <param name="storage">The storage layout under evaluation.<para>正在评估的存储布局。</para></param>
    /// <param name="partition">The partition entry to test.<para>要测试的分区条目。</para></param>
    /// <returns>Whether the entry lies in the reconcilable user region.<para>该条目是否位于可复核的用户区域。</para></returns>
    private static bool MatchesUserRegion(MtkScatterStorage storage, MtkScatterPartition partition) =>
        storage.Name switch
        {
            "EMMC" => partition.Storage == "HW_STORAGE_EMMC" && partition.Region == "EMMC_USER",
            "UFS" => partition.Storage == "HW_STORAGE_UFS" && partition.Region == "UFS_LU2",
            _ => false
        };

    /// <summary>Keeps an entry eligible only when every literal scatter policy field would be accepted.
    /// <para>仅当每个字面 scatter 策略字段都会被接受时，才让条目保持可选。</para></summary>
    /// <param name="partition">The partition entry to filter.<para>要过滤的分区条目。</para></param>
    /// <returns>Whether the entry passes the scatter policy filter.<para>该条目是否通过 scatter 策略过滤。</para></returns>
    private static bool MatchesScatterPolicy(MtkScatterPartition partition)
        => partition.IsDownload && !partition.IsReserved && partition.BoundaryCheck
            && partition.OperationType == "UPDATE"
            && partition.Type is ("NORMAL_ROM" or "EXT4_IMG")
            && !string.Equals(partition.FileName, "NONE", StringComparison.OrdinalIgnoreCase)
            && MatchesPolicyFields(partition);

    /// <summary>Erasing is not a download, so the download-only gates do not apply while the region,
    /// reservation, boundary and policy-field guards stay strict.
    /// <para>擦除不是下载，因此仅适用于下载的门槛不生效，而区域、保留位、边界与策略字段守卫仍保持严格。</para></summary>
    /// <param name="partition">The partition entry to filter.<para>要过滤的分区条目。</para></param>
    /// <returns>Whether the entry may be erased.<para>该条目是否可被擦除。</para></returns>
    private static bool MatchesErasePolicy(MtkScatterPartition partition)
        => !partition.IsReserved && partition.BoundaryCheck
            && partition.Size is > 0 and <= long.MaxValue
            && MatchesPolicyFields(partition);

    /// <summary>Sweeps every literal policy field so an entry with unknown or contradicting policy is
    /// rejected instead of silently acted upon.
    /// <para>遍历每个字面策略字段，使策略未知或矛盾的条目被拒绝而非被静默处理。</para></summary>
    /// <param name="partition">The partition entry whose fields are swept.<para>要遍历字段的分区条目。</para></param>
    /// <returns>Whether every policy field is recognized and consistent.<para>每个策略字段是否都被识别且一致。</para></returns>
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
    /// <param name="storage">The parsed storage layout.<para>已解析的存储布局。</para></param>
    /// <returns>Erased-eligible entries in manifest order.<para>按清单顺序排列的可擦除条目。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.
    /// <para><paramref name="storage"/> 为 null。</para></exception>
    /// <exception cref="InvalidDataException">The layout is missing, out of bounds, or contains a null
    /// entry. <para>布局缺失、条目数量越界或包含空条目。</para></exception>
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
    /// <param name="partition">The erase-eligible scatter entry, usually from <see cref="ListEraseTargets"/>.
    /// <para>可擦除的 scatter 条目，通常来自 <see cref="ListEraseTargets"/>。</para></param>
    /// <returns>A GPT-named erase step.<para>GPT 命名的擦除步骤。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="partition"/> is null.
    /// <para><paramref name="partition"/> 为 null。</para></exception>
    /// <exception cref="InvalidDataException">The entry has no partition name or declares an unusable
    /// capacity. <para>条目缺少分区名或声明容量不可用。</para></exception>
    public static MtkPartitionFlashStep CreateEraseStep(MtkScatterPartition partition)
    {
        var (name, size) = EraseRange(partition);
        return new MtkPartitionFlashStep(MtkFlashStepKind.Erase, name, 0, size, null, 0, Verify: false);
    }

    /// <summary>Builds the raw write operation for one resolved row addressed by GPT partition name, for the Legacy download agent.
    /// <para>为一个按 GPT 分区名寻址的已解析条目构造 Legacy 下载代理使用的原始写入操作。</para></summary>
    /// <param name="resolution">A resolution already returned by <see cref="Resolve"/>.
    /// <para>由 <see cref="Resolve"/> 返回的解析结果。</para></param>
    /// <param name="rowIndex">Zero-based index into <see cref="MtkScatterPackageResolution.Rows"/>.
    /// <para>指向 <see cref="MtkScatterPackageResolution.Rows"/> 的从零开始索引。</para></param>
    /// <param name="verify">Whether written extents require readback; passed through unchanged.
    /// <para>写入范围是否需要回读；原样透传。</para></param>
    /// <returns>The Legacy raw write step for this row.<para>该行的 Legacy 原始写入步骤。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> or its rows are null.
    /// <para><paramref name="resolution"/> 或其行为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowIndex"/> is outside the rows.
    /// <para><paramref name="rowIndex"/> 越界。</para></exception>
    /// <exception cref="InvalidDataException">A row or its partition entry is null, the entry has no partition name, or the image exceeds the range limits.
    /// <para>某行或其分区条目为 null、条目缺少分区名，或镜像超出范围上限。</para></exception>
    /// <exception cref="NotSupportedException">The row's image is a sparse container.
    /// <para>该行的镜像是稀疏容器。</para></exception>
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
    /// <returns>A GPT-named erase step.<para>GPT 命名的擦除步骤。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="partition"/> is null.
    /// <para><paramref name="partition"/> 为 null。</para></exception>
    /// <exception cref="InvalidDataException">The entry has no partition name or declares an unusable
    /// capacity. <para>条目缺少分区名或声明容量不可用。</para></exception>
    public static MtkLegacyPartitionFlashStep CreateLegacyEraseStep(MtkScatterPartition partition)
    {
        var (name, size) = EraseRange(partition);
        return new MtkLegacyPartitionFlashStep(name, MtkFlashStepKind.Erase, 0, AlignToBlock(size));
    }

    /// <summary>Maps an observed storage family to the Legacy agent's physical region identifier.
    /// <para>把观察到的存储家族映射为 Legacy 代理的物理区域标识符。</para></summary>
    /// <param name="kind">The storage family reported by the device.<para>设备上报的存储家族。</para></param>
    /// <returns>The region identifier to pass to Legacy preparation.<para>传给 Legacy 准备阶段的区域标识符。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not eMMC.
    /// <para><paramref name="kind"/> 不是 eMMC。</para></exception>
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
    /// Extracts the partition name and declared capacity an erase needs so the name and capacity guards
    /// stay identical across erase builders.
    /// <para>取出擦除所需的分区名与声明容量，使名称与容量守卫在各擦除构造器之间保持一致。</para>
    /// </summary>
    /// <param name="partition">The erase-eligible scatter entry.<para>可擦除的 scatter 条目。</para></param>
    /// <returns>The partition name and the capacity validated against the signed range.
    /// <para>分区名与按有符号范围校验过的声明容量。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="partition"/> is null.
    /// <para><paramref name="partition"/> 为 null。</para></exception>
    /// <exception cref="InvalidDataException">The entry has no partition name or declares an unusable
    /// capacity. <para>条目缺少分区名或声明容量不可用。</para></exception>
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
    /// Extracts the row a raw-only family may write: it requires a GPT partition name and rejects a sparse
    /// container, because streaming a sparse header as raw bytes would corrupt the partition.
    /// <para>取出只支持原始字节的家族可写入的条目：它要求 GPT 分区名并拒绝稀疏容器，因为把稀疏头当原始字节写入会损坏分区。</para>
    /// </summary>
    /// <param name="resolution">A resolution already returned by <see cref="Resolve"/>.
    /// <para>由 <see cref="Resolve"/> 返回的解析结果。</para></param>
    /// <param name="rowIndex">Zero-based row index.<para>行序号。</para></param>
    /// <param name="agent">The family name used in the messages.<para>用于文案的家族名。</para></param>
    /// <returns>The resolvable row to write and its GPT partition name.
    /// <para>可写入的已解析行与其 GPT 分区名。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> or its rows are null.
    /// <para><paramref name="resolution"/> 或其行为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowIndex"/> is outside the rows.
    /// <para><paramref name="rowIndex"/> 越界。</para></exception>
    /// <exception cref="InvalidDataException">The row or its partition entry is null, or the entry has no
    /// partition name. <para>行或其分区条目为 null，或条目缺少分区名。</para></exception>
    /// <exception cref="NotSupportedException">The row's image is a sparse container.
    /// <para>该行的镜像是稀疏容器。</para></exception>
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

    /// <summary>Builds the raw write operation for one resolved row addressed by GPT partition name, for the XML download agent, rounding the extent up to the block granularity the scatter layout declares.
    /// <para>为一个按 GPT 分区名寻址的已解析条目构造 XML 下载代理使用的原始写入操作，范围向上取整到 scatter 布局声明的块粒度。</para></summary>
    /// <param name="resolution">A resolution already returned by <see cref="Resolve"/>.
    /// <para>由 <see cref="Resolve"/> 返回的解析结果。</para></param>
    /// <param name="rowIndex">Zero-based index into <see cref="MtkScatterPackageResolution.Rows"/>.
    /// <para>指向 <see cref="MtkScatterPackageResolution.Rows"/> 的从零开始索引。</para></param>
    /// <param name="verify">Whether written extents require readback; passed through unchanged.
    /// <para>写入范围是否需要回读；原样透传。</para></param>
    /// <returns>The XML raw write step for this row.<para>该行的 XML 原始写入步骤。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> or its rows are null.
    /// <para><paramref name="resolution"/> 或其行为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowIndex"/> is outside the rows or the block granularity is unsupported.
    /// <para><paramref name="rowIndex"/> 越界，或块粒度不受支持。</para></exception>
    /// <exception cref="InvalidDataException">A row or its partition entry is null, or the entry has no partition name.
    /// <para>某行或其分区条目为 null，或条目缺少分区名。</para></exception>
    /// <exception cref="NotSupportedException">The row's image is a sparse container.
    /// <para>该行的镜像是稀疏容器。</para></exception>
    public static MtkXmlPartitionFlashStep CreateXmlStep(MtkScatterPackageResolution resolution, int rowIndex,
        bool verify)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var block = XmlBlockSize(resolution);
        var (row, name) = RawWriteRow(resolution, rowIndex, "XML");
        return new MtkXmlPartitionFlashStep(name, MtkFlashStepKind.Raw, 0,
            AlignToBlock(row.Length, block), row.ImagePath, 0, verify, PadSourceTail: true);
    }

    /// <summary>Builds the XML erase operation for one scatter entry; the range is the entry's declared capacity rounded up to the block granularity the scatter layout declares.
    /// <para>为一个 scatter 条目构造 XML 擦除操作；范围取该条目声明容量并向上取整到 scatter 布局声明的块粒度。</para></summary>
    /// <param name="resolution">A resolution already returned by <see cref="Resolve"/>.
    /// <para>由 <see cref="Resolve"/> 返回的解析结果。</para></param>
    /// <param name="partition">The erase-eligible scatter entry, usually from <see cref="ListEraseTargets"/>.
    /// <para>可擦除的 scatter 条目，通常来自 <see cref="ListEraseTargets"/>。</para></param>
    /// <returns>A GPT-named erase step.<para>GPT 命名的擦除步骤。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> or <paramref name="partition"/> is null.
    /// <para><paramref name="resolution"/> 或 <paramref name="partition"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The layout declares a block granularity outside the supported range.
    /// <para>布局声明的块粒度越界。</para></exception>
    /// <exception cref="InvalidDataException">The entry has no partition name or declares an unusable capacity.
    /// <para>条目缺少分区名或声明容量不可用。</para></exception>
    public static MtkXmlPartitionFlashStep CreateXmlEraseStep(MtkScatterPackageResolution resolution,
        MtkScatterPartition partition)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var block = XmlBlockSize(resolution);
        var (name, size) = EraseRange(partition);
        return new MtkXmlPartitionFlashStep(name, MtkFlashStepKind.Erase, 0, AlignToBlock(size, block));
    }

    /// <summary>Maps an observed storage family to the XML agent's named user-data region.
    /// <para>把观察到的存储家族映射为 XML 代理具名的用户数据区域。</para></summary>
    /// <param name="kind">The storage family reported by the device.<para>设备上报的存储家族。</para></param>
    /// <returns>The region token to pass to XML preparation.<para>传给 XML 准备阶段的区域标记。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not eMMC or UFS.
    /// <para><paramref name="kind"/> 不是 eMMC 或 UFS。</para></exception>
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