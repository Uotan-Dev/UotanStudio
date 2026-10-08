using System.Buffers.Binary;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.FileSystem.Native;

namespace UotanStudio.Firmware.FileSystem.F2fs;

/// <summary>Builds an Android-profile F2FS image with native quota and read-only metadata structures.
/// <para>使用原生配额与只读元数据结构构建 Android 配置的 F2FS 镜像。</para></summary>
internal static class AndroidF2fsImageBuilder
{
    /// <summary>
    /// Builds a fixed-capacity Android-profile F2FS image by running <c>mkfs.f2fs</c>, <c>sload.f2fs</c> and
    /// a dry-run <c>fsck.f2fs</c> through <paramref name="tools"/>, then verifies the superblock feature bits
    /// (extra_attr, project_quota, readonly, plus compression when enabled) before atomically moving the
    /// validated temporary file onto <paramref name="outputPath"/>.
    /// <para>通过 <paramref name="tools"/> 依次运行 <c>mkfs.f2fs</c>、<c>sload.f2fs</c> 与演练模式的
    /// <c>fsck.f2fs</c> 构建固定容量的 Android 配置 F2FS 镜像，随后校验超级块特性位（extra_attr、
    /// project_quota、只读，以及启用压缩时的 compression），最后把通过校验的临时文件原子移动到
    /// <paramref name="outputPath"/>。</para>
    /// </summary>
    /// <param name="sourceDirectory">Directory tree loaded into the image. <para>要装入镜像的目录树。</para></param>
    /// <param name="outputPath">Destination image path; an existing file is replaced only after all checks pass.
    /// <para>目标镜像路径；仅在全部检查通过后才会覆盖已有文件。</para></param>
    /// <param name="imageSizeBytes">Fixed image capacity; must be at least 64 MiB and a multiple of 2 MiB.
    /// <para>固定镜像容量；至少 64 MiB 且须为 2 MiB 的整数倍。</para></param>
    /// <param name="tools">Native tool runner that also maps host paths to tool-visible paths.
    /// <para>原生工具运行器，同时负责把宿主路径映射为工具可见路径。</para></param>
    /// <param name="mountPoint">Absolute Android mount point recorded by <c>sload.f2fs</c>; must start with
    /// <c>/</c> and contain no <c>..</c> segment. Defaults to <c>/system</c>.
    /// <para><c>sload.f2fs</c> 记录的绝对 Android 挂载点；须以 <c>/</c> 开头且不含 <c>..</c> 段，
    /// 默认 <c>/system</c>。</para></param>
    /// <param name="fsConfigPath">Optional Android-style <c>fs_config</c> file passed via <c>-C</c>; the file must
    /// exist. For a preserved extraction directory the payload in <c>metadata.json</c> is materialised into a
    /// temporary octal sidecar instead and this path is not consulted.
    /// <para>可选的 Android 风格 <c>fs_config</c> 文件，经 <c>-C</c> 传入；文件必须存在。对受保留的解包目录，
    /// <c>metadata.json</c> 内的负载会被具象化为临时八进制附属文件，此路径不再被参考。</para></param>
    /// <param name="fileContextsPath">Optional Android-style <c>file_contexts</c> file passed via <c>-s</c>; the
    /// file must exist. For a preserved extraction directory the payload provides a temporary sidecar instead.
    /// <para>可选的 Android 风格 <c>file_contexts</c> 文件，经 <c>-s</c> 传入；文件必须存在。对受保留的解包目录，
    /// 改由负载提供临时附属文件。</para></param>
    /// <param name="enableCompression">Adds the <c>compression</c> feature and the <c>-c</c> flag; only LZ4 is supported.
    /// <para>启用则追加 <c>compression</c> 特性与 <c>-c</c> 标志；仅支持 LZ4。</para></param>
    /// <param name="compressionAlgorithm">Declared compression codec; any value other than LZ4 is rejected when compression is enabled.
    /// <para>声明的压缩编解码器；启用压缩时非 LZ4 的值会被拒绝。</para></param>
    /// <param name="cancellationToken">Cancels path mapping, each native tool run and the final move.
    /// <para>取消路径映射、每次原生工具运行以及最终移动。</para></param>
    /// <returns>A task that completes once the verified image has been moved to <paramref name="outputPath"/>.
    /// <para>任务在校验通过的镜像移动到 <paramref name="outputPath"/> 后完成。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="tools"/> is <see langword="null"/>.
    /// <para><paramref name="tools"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="sourceDirectory"/> does not exist.
    /// <para><paramref name="sourceDirectory"/> 不存在。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="fsConfigPath"/> or <paramref name="fileContextsPath"/> names a missing file.
    /// <para><paramref name="fsConfigPath"/> 或 <paramref name="fileContextsPath"/> 指向的文件缺失。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="imageSizeBytes"/> is below 64 MiB or not 2 MiB aligned.
    /// <para><paramref name="imageSizeBytes"/> 小于 64 MiB 或未按 2 MiB 对齐。</para></exception>
    /// <exception cref="NotSupportedException">Compression is enabled with a codec other than LZ4.
    /// <para>启用了压缩但编解码器不是 LZ4。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="mountPoint"/> is not a safe absolute path, or
    /// <paramref name="outputPath"/> lies inside <paramref name="sourceDirectory"/>.
    /// <para><paramref name="mountPoint"/> 不是安全的绝对路径，或 <paramref name="outputPath"/> 位于
    /// <paramref name="sourceDirectory"/> 之内。</para></exception>
    /// <exception cref="FileSystemCorruptedException">A native tool failed or the produced image lacks the required Android feature bits.
    /// <para>原生工具失败，或产出的镜像缺少必需的 Android 特性位。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static async Task BuildAsync(string sourceDirectory, string outputPath, long imageSizeBytes,
        NativeFileSystemTools tools, string mountPoint = "/system", string? fsConfigPath = null,
        string? fileContextsPath = null, bool enableCompression = false,
        CompressionAlgorithm compressionAlgorithm = CompressionAlgorithm.Lz4,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var source = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(outputPath);
        using var sidecars = NativeMetadataSidecars.TryCreate(source, cancellationToken);
        var effectiveFsConfigPath = sidecars?.FsConfigPath ?? fsConfigPath;
        var effectiveFileContextsPath = sidecars?.FileContextsPath ?? fileContextsPath;
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }
        if (effectiveFsConfigPath is not null && !File.Exists(effectiveFsConfigPath))
        {
            throw new FileNotFoundException("Android fs_config file was not found.", effectiveFsConfigPath);
        }
        if (effectiveFileContextsPath is not null && !File.Exists(effectiveFileContextsPath))
        {
            throw new FileNotFoundException("Android file_contexts file was not found.", effectiveFileContextsPath);
        }
        if (imageSizeBytes < 64L * 1024 * 1024 || imageSizeBytes % (2 * 1024 * 1024) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageSizeBytes),
                "Android F2FS capacity must be at least 64 MiB and aligned to 2 MiB.");
        }
        if (enableCompression && compressionAlgorithm != CompressionAlgorithm.Lz4)
        {
            throw new NotSupportedException("The native Android F2FS profile currently supports only LZ4 compression.");
        }
        if (!mountPoint.StartsWith('/') || mountPoint.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Mount point must be an absolute Android path.", nameof(mountPoint));
        }
        if (destination.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The output must not be inside the source directory.", nameof(outputPath));
        }

        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var image = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                image.SetLength(imageSizeBytes);
            }
            var imagePath = await tools.PathForToolAsync(temporary, cancellationToken).ConfigureAwait(false);
            var sourcePath = await tools.PathForToolAsync(source, cancellationToken).ConfigureAwait(false);
            var mkfs = await tools.RunAsync("mkfs.f2fs",
                ["-f", "-t", "0", "-g", "android", "-O",
                    enableCompression ? "extra_attr,project_quota,ro,compression" : "extra_attr,project_quota,ro", imagePath],
                cancellationToken).ConfigureAwait(false);
            NativeFileSystemTools.RequireSuccess("mkfs.f2fs", mkfs);

            List<string> args = ["-f", sourcePath, "-t", mountPoint];
            if (effectiveFsConfigPath is not null)
            {
                args.Add("-C");
                args.Add(await tools.PathForToolAsync(effectiveFsConfigPath, cancellationToken).ConfigureAwait(false));
            }
            if (effectiveFileContextsPath is not null)
            {
                args.Add("-s");
                args.Add(await tools.PathForToolAsync(effectiveFileContextsPath, cancellationToken).ConfigureAwait(false));
            }
            if (enableCompression)
            {
                args.Add("-c");
            }
            args.Add(imagePath);
            NativeFileSystemTools.RequireSuccess("sload.f2fs",
                await tools.RunAsync("sload.f2fs", args, cancellationToken).ConfigureAwait(false));
            NativeFileSystemTools.RequireSuccess("fsck.f2fs",
                await tools.RunAsync("fsck.f2fs", ["--dry-run", imagePath], cancellationToken).ConfigureAwait(false));

            await using (var image = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var header = new byte[1024 + 2184];
                await image.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                var features = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024 + 2180));
                var required = 0x0008u | 0x0010u | 0x4000u | (enableCompression ? 0x2000u : 0);
                if ((features & required) != required)
                {
                    throw new FileSystemCorruptedException($"Native F2FS image lacks required Android features (0x{features:X8}).");
                }
                image.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            AtomicPublish.Publish(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}