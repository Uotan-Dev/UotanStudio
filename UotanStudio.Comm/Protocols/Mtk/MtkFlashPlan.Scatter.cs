using System.Globalization;
using UotanStudio.Firmware.VendorFirmware.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// An explicit caller selection; the scatter file name is never opened automatically.
/// <para>调用方的显式选择；scatter 中的 file_name 永不会被自动打开。</para>
/// </summary>
/// <param name="PartitionIndex">
/// Exact scatter SYS index within the caller-selected storage layout.
/// <para>在调用方选定的存储布局中精确匹配的 scatter SYS 索引。</para>
/// </param>
/// <param name="Kind">
/// Explicit raw, sparse or erase operation, not inferred from scatter image type.
/// <para>显式指定的原始、稀疏或擦除操作，不依据 scatter 镜像类型推断。</para>
/// </param>
/// <param name="Offset">Byte offset relative to the GPT partition. <para>相对于 GPT 分区的字节偏移。</para></param>
/// <param name="Length">
/// Raw/erase byte count or sparse target capacity.
/// <para>原始/擦除字节数，或稀疏目标容量。</para>
/// </param>
/// <param name="SourcePath">
/// Caller-selected image path, not resolved from the scatter file name.
/// <para>由调用方选定的镜像路径，不会从 scatter 的 file_name 解析。</para>
/// </param>
/// <param name="SourceOffset">Raw source offset; zero for sparse/erase. <para>原始源偏移；稀疏/擦除时为零。</para></param>
/// <param name="Verify">Whether written extents require readback. <para>写出的范围是否需要回读。</para></param>
public sealed record MtkScatterFlashStep(string PartitionIndex, MtkFlashStepKind Kind, long Offset, long Length,
    string? SourcePath = null, long SourceOffset = 0, bool Verify = false);

public sealed partial class MtkFlashPlan
{
    /// <summary>
    /// Reconciles selected ordinary Scatter entries with GPT before opening any sources.
    /// Platform/loader authorization and explicit source selection remain caller responsibilities.
    /// <para>
    /// 在打开任何源之前，将选定的普通 Scatter 条目与 GPT 进行核对。平台/引导加载程序的授权以及
    /// 显式的源选择仍由调用方负责。
    /// </para>
    /// </summary>
    /// <remarks>
    /// Only user-region UPDATE entries with matching linear/physical/GPT addresses and exact
    /// partition capacity are accepted. Bootloader, reserved, protected and unknown policy
    /// operations require dedicated support; no manifest flag disables range validation.
    /// Scatter block-size hints and file names never override device geometry or source paths.
    /// </remarks>
    /// <param name="snapshot">
    /// Validated GPT observation from the intended XFlash session.
    /// <para>来自目标 XFlash 会话、已校验的 GPT 观测结果。</para>
    /// </param>
    /// <param name="layout">
    /// Explicitly selected storage layout; no automatic EMMC/UFS fallback.
    /// <para>显式选定的存储布局；不会在 EMMC/UFS 之间自动回退。</para>
    /// </param>
    /// <param name="steps">
    /// Explicitly selected and ordered operations, not all download-enabled entries.
    /// <para>显式选定并排序的操作，而非全部允许下载的条目。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation for reconciliation and source preflight.
    /// <para>用于核对与源预检的取消令牌。</para>
    /// </param>
    /// <returns>
    /// A retained one-shot plan with normal GPT revalidation at execution.
    /// <para>一个已保留的单次计划，执行时仍会进行常规的 GPT 复核。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A selected step is null or its <c>PartitionIndex</c> is blank.
    /// <para>某个选定步骤为 null，或其 <c>PartitionIndex</c> 为空白。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The step count or layout partition count exceeds plan limits, or a delegated physical-range
    /// check rejects an offset or length.
    /// <para>步骤数量或布局分区数量超出计划上限，或被委托执行的物理范围校验拒绝了某个偏移或长度。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="snapshot"/>, <paramref name="layout"/>, or <paramref name="steps"/> is null.
    /// <para><paramref name="snapshot"/>、<paramref name="layout"/> 或 <paramref name="steps"/> 为 null。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The layout and GPT observation identify different user regions, a partition index is missing
    /// or duplicated, or scatter addresses disagree with the observed GPT.
    /// <para>布局与 GPT 观测结果指向不同的用户区域、分区索引缺失或重复，或 scatter 地址与观测到的 GPT 不一致。</para>
    /// </exception>
    /// <exception cref="KeyNotFoundException">
    /// The selected scatter partition index was not found in the layout.
    /// <para>在布局中找不到选定的 scatter 分区索引。</para>
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The storage family, region, policy fields, or image type of a selected entry is outside the
    /// supported user-region UPDATE set.
    /// <para>选定条目的存储族、区域、策略字段或镜像类型不在受支持的用户区域 UPDATE 集合之内。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was requested during reconciliation.
    /// <para>在核对过程中请求了取消。</para>
    /// </exception>
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