using System.Security.Cryptography;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.OtaDelta.Payload;

/// <summary>
/// Rebuilds an existing A/B payload with replacement partition images, optionally signing the result with RSA
/// SHA-256 PKCS#1 v1.5.
/// <para>使用替换分区镜像重建既有 A/B 载荷，并可选地以 RSA SHA-256 PKCS#1 v1.5 签名。</para>
/// </summary>
/// <remarks>
/// Every retained partition is extracted to its full target image and re-emitted as REPLACE operations, so the
/// output is a self-contained full payload rather than a delta. Postinstall flags and dynamic group membership
/// are inherited from the source manifest. Source-dependent delta operations require base images that this
/// entry point does not supply and therefore fail with an extraction error.
/// <para>每个保留分区都会导出为完整目标镜像并重新以 REPLACE 操作输出，因此结果是自包含的完整载荷而非增量。
/// postinstall 标志与动态分组关系继承自源清单。依赖源镜像的差分操作需要本入口不提供的基座镜像，因此会以提取错误失败。</para>
/// </remarks>
public static class PayloadPatcher
{
    /// <summary>Replaces named partitions in a payload and atomically publishes the rebuilt payload.
    /// <para>替换载荷中的指定分区，并原子发布重建后的载荷。</para></summary>
    /// <param name="sourcePath">Existing payload file; opened read-only and never modified.
    /// <para>既有载荷文件；以只读方式打开，绝不修改。</para></param>
    /// <param name="outputPath">Destination path, published only after the complete rebuild succeeds; must differ from <paramref name="sourcePath"/>.
    /// <para>目标路径，仅在完整重建成功后发布；必须与 <paramref name="sourcePath"/> 不同。</para></param>
    /// <param name="replacementImages">Partition names already present in the manifest mapped to replacement image files; at most 4096 entries.
    /// <para>清单中已存在的分区名到替换镜像文件的映射；至多 4096 个条目。</para></param>
    /// <param name="signingKey">Optional RSA 2048/4096 key that signs the rebuilt payload; <see langword="null"/> leaves it unsigned. The caller keeps ownership.
    /// <para>可选 RSA 2048/4096 密钥，用于签名重建后的载荷；<see langword="null"/> 表示不签名。所有权归调用方。</para></param>
    /// <param name="cancellationToken">Cancels extraction, rebuild and publication. <para>取消提取、重建与发布。</para></param>
    /// <returns>The number of replacement partitions written.
    /// <para>写入的替换分区数量。</para></returns>
    /// <exception cref="ArgumentException">A path is empty, both paths are equal, or the replacement map is empty or oversized.
    /// <para>路径为空、两个路径相同，或替换映射为空或超限。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="replacementImages"/> is <see langword="null"/>.
    /// <para><paramref name="replacementImages"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException">The source payload or a replacement image is missing.
    /// <para>源载荷或替换镜像不存在。</para></exception>
    public static async Task<int> ReplaceAsync(
        string sourcePath,
        string outputPath,
        IReadOnlyDictionary<string, string> replacementImages,
        RSA? signingKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(replacementImages);
        if (replacementImages.Count is 0 or > 4096)
        {
            throw new ArgumentException("Replacement images must contain between 1 and 4096 entries.", nameof(replacementImages));
        }

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("Output path must differ from the source payload.", nameof(outputPath));
        }

        foreach (var path in replacementImages.Values)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Replacement partition image was not found.", path);
            }
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Payload file was not found.", sourcePath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = TempFileHelper.CreateAdjacentPath(destination);
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await PayloadPartitionPatcher.PatchAsync(
                    source, output,
                    new PayloadPartitionPatch
                    {
                        ReplaceImagePaths = replacementImages,
                        SigningKey = signingKey,
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AtomicPublish.Publish(temporary, destination);
            return replacementImages.Count;
        }
        finally
        {
            TempFileHelper.SafeDelete(temporary);
        }
    }
}