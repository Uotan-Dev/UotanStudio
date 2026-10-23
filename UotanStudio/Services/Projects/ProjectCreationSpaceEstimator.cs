using UotanStudio.Compress.Zip;
using UotanStudio.Delivery;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.BlockImage.Sparse.IO;

namespace UotanStudio.Services.Projects;

/// <summary>Read-only peak-space estimates for the selected creation stages, without extracting images.
/// <para>对所选创建阶段做只读的峰值空间估算，不解压镜像。</para></summary>
internal static class ProjectCreationSpaceEstimator
{
    internal const long MetadataReserve = 16 * 1024 * 1024;

    /// <summary>Estimates retained copies, selected expanded images and workspace for further unpacking.
    /// <para>估算保留副本、所选展开镜像以及进一步解包所需的工作空间。</para></summary>
    /// <param name="copyPaths">Files selected for retained copying.<para>入选保留复制的文件。</para></param>
    /// <param name="extractedEntries">Already extracted container entries to account for.<para>需计入的已解包容器条目。</para></param>
    /// <param name="unpackCopiedImages">Whether copied images are also expanded in place.<para>是否同时原地展开被复制的镜像。</para></param>
    /// <param name="unpackFully">Whether unpacking depth is treated as recursive.<para>是否按递归解包计算。</para></param>
    /// <param name="payloadZipPath">Optional ZIP archive scanned for its payload member.<para>要扫描其负载条目的可选 ZIP 包。</para></param>
    /// <returns>The estimated target and temporary demand.<para>估算的目标与临时空间需求。</para></returns>
    internal static CreationSpaceEstimate Estimate(IReadOnlyList<string> copyPaths,
        IReadOnlyList<ContainerEntry> extractedEntries, bool unpackCopiedImages, bool unpackFully,
        string? payloadZipPath = null)
    {
        long target = MetadataReserve, temporary = 0;
        var approximate = false;
        foreach (var path in copyPaths.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            try
            {
                var size = new FileInfo(path).Length;
                target = Add(target, size);
                if (!unpackCopiedImages) continue;
                var expanded = size;
                if (size >= 28)
                {
                    var header = SparseReader.PeekHeader(path);
                    if (header.IsValid()) expanded = Math.Max(size, Multiply(header.TotalBlocks, header.BlockSize));
                }
                target = Add(target, Multiply(expanded, unpackFully ? 3 : 2));
                approximate = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                approximate = true;
            }
        }
        foreach (var entry in extractedEntries)
        {
            var size = Math.Max(0, entry.SizeBytes);
            if (size == 0) approximate = true;
            target = Add(target, Multiply(size, unpackFully ? 3 : 1));
        }
        approximate |= unpackFully;
        if (payloadZipPath is not null)
        {
            try
            {
                using var zip = ZipContainer.OpenRead(payloadZipPath);
                var member = zip.Entries.FirstOrDefault(e =>
                    Path.GetFileName(e.FullName).Equals("payload.bin", StringComparison.OrdinalIgnoreCase));
                if (member is not null) temporary = member.Length;
                else approximate = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                approximate = true;
            }
        }
        return new(target, temporary, approximate);
    }

    /// <summary>Combines target and temporary requirements when they share a volume.
    /// <para>在目标与临时需求位于同一卷时合并两者。</para></summary>
    /// <param name="targetDirectory">The destination directory receiving the project.<para>接收项目的目标目录。</para></param>
    /// <param name="estimate">The demand estimate to check.<para>要核对的需求估算。</para></param>
    /// <param name="availableBytes">Optional override returning free bytes for a path.<para>可选的按路径返回可用字数的覆盖实现。</para></param>
    /// <param name="volumePath">Optional override resolving the volume root for a path.<para>可选的按路径解析卷根的覆盖实现。</para></param>
    /// <returns>Destinations whose free space is below the combined demand.<para>可用空间低于合并需求的目录。</para></returns>
    internal static IReadOnlyList<CreationSpaceShortage> FindShortages(string targetDirectory,
        CreationSpaceEstimate estimate, Func<string, long>? availableBytes = null,
        Func<string, string?>? volumePath = null)
    {
        availableBytes ??= DiskSpaceEstimator.GetFreeBytes;
        volumePath ??= DiskSpaceEstimator.GetVolumePath;
        var demands = new Dictionary<string, (string Path, long Required)>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        AddDemand(targetDirectory, estimate.TargetBytes);
        if (estimate.TemporaryBytes > 0) AddDemand(Path.GetTempPath(), estimate.TemporaryBytes);
        List<CreationSpaceShortage> shortages = [];
        foreach (var (_, (path, required)) in demands)
        {
            var free = availableBytes(path);
            if (free >= 0 && free < required) shortages.Add(new(path, required, free));
        }
        return shortages;

        void AddDemand(string path, long required)
        {
            var volume = volumePath(path) ?? Path.GetFullPath(path);
            demands[volume] = demands.TryGetValue(volume, out var existing)
                ? (existing.Path, Add(existing.Required, required)) : (path, required);
        }
    }

    internal static long Add(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
    internal static long Multiply(long size, long count) => count > 0 && size > long.MaxValue / count ? long.MaxValue : size * count;
}

/// <summary>Estimated peak demand; recursive unpacking is a heuristic rather than a size guarantee.
/// <para>估算的峰值空间需求；递归解包只是启发式估计，不是容量保证。</para></summary>
/// <param name="TargetBytes">Bytes required at the target location.<para>目标位置所需字节数。</para></param>
/// <param name="TemporaryBytes">Bytes required in the temporary location.<para>临时位置所需字节数。</para></param>
/// <param name="IsApproximate">Whether the estimate relies on heuristics.<para>估算是否依赖启发式。</para></param>
internal sealed record CreationSpaceEstimate(long TargetBytes, long TemporaryBytes, bool IsApproximate);

/// <summary>A destination with less available space than the estimated demand.
/// <para>可用空间小于估算需求的目的地。</para></summary>
/// <param name="Directory">The destination path with insufficient space.<para>空间不足的目的地路径。</para></param>
/// <param name="RequiredBytes">Combined bytes demanded on the volume.<para>该卷上合并所需的字节数。</para></param>
/// <param name="AvailableBytes">Bytes currently available.<para>当前可用字节数。</para></param>
internal sealed record CreationSpaceShortage(string Directory, long RequiredBytes, long AvailableBytes);