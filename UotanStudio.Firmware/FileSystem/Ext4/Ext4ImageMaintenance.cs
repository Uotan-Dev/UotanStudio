using System.Buffers.Binary;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.FileSystem.Native;

namespace UotanStudio.Firmware.FileSystem.Ext4;

/// <summary>Checks, optionally repairs, and minimizes an existing raw EXT4 image on a private copy with native filesystem tools.
/// <para>在私有副本上用原生文件系统工具对既有 raw EXT4 镜像进行检查、可选修复与容量最小化。</para></summary>
public static class Ext4ImageMaintenance
{
    /// <summary>
    /// Shrinks a raw or Android-sparse EXT4 image to its minimum size and publishes the result.
    /// <para>把 raw 或 Android 稀疏 EXT4 镜像缩小到最小容量并发布结果。</para>
    /// </summary>
    /// <param name="inputPath">Source image; sparse containers (magic 0xED26FF3A) are converted to raw before resizing. <para>源镜像；稀疏容器（魔数 0xED26FF3A）先转换为 raw 再调整。</para></param>
    /// <param name="outputPath">Destination image path; an existing file is replaced. It must differ from inputPath. <para>目标镜像路径；已存在的文件会被替换。必须与 inputPath 不同。</para></param>
    /// <param name="tools">Native tool provider that stages the image and runs the check and resize passes. <para>用于暂存镜像并执行检查与容量调整步骤的原生工具提供方。</para></param>
    /// <param name="repairBeforeResize">When <c>true</c>, runs a forced repair first and accepts exit codes 0 or 1. <para>为 <c>true</c> 时先执行强制修复，并接受退出码 0 或 1。</para></param>
    /// <param name="cancellationToken">Cancels the copy, the checks and the native tool runs; the destination is left untouched. <para>取消复制、检查与原生工具运行；目标文件保持不变。</para></param>
    /// <returns>A task that completes once the minimized image has been published to outputPath. <para>最小化后的镜像发布到 outputPath 后完成的任务。</para></returns>
    /// <exception cref="ArgumentException">The tools provider is <see langword="null"/>, or the input and output paths denote the same file. <para>tools 提供方为 <see langword="null"/>，或输入与输出为同一文件。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The image is too small, lacks the EXT4 magic at offset 1080, or a repair or resize run fails. <para>镜像过小、偏移 1080 处缺少 EXT4 魔数，或修复/容量调整步骤执行失败。</para></exception>
    public static async Task MinimizeAsync(string inputPath, string outputPath, NativeFileSystemTools tools,
        bool repairBeforeResize = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var source = Path.GetFullPath(inputPath);
        var destination = Path.GetFullPath(outputPath);
        if (string.Equals(source, destination, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The source and output paths must differ.", nameof(outputPath));
        }

        bool sparseInput;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
        {
            if (input.Length < 4)
            {
                throw new FileSystemCorruptedException("Image is too small.");
            }
            var magic = new byte[4];
            await input.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
            sparseInput = BinaryPrimitives.ReadUInt32LittleEndian(magic) == BlockImageMagic.SparseHeaderMagic;
        }

        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (sparseInput)
            {
                SparseContainerAccess.Resolve().ConvertSparseToRaw([source], temporary, cancellationToken);
            }
            else
            {
                await using var input = File.OpenRead(source);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                await input.CopyToAsync(output, 65536, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var staged = File.OpenRead(temporary))
            {
                if (staged.Length < 2048)
                {
                    throw new FileSystemCorruptedException("EXT4 image is too small.");
                }
                staged.Position = 1024 + 56;
                var magic = new byte[2];
                await staged.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
                if (BinaryPrimitives.ReadUInt16LittleEndian(magic) != 0xEF53)
                {
                    throw new FileSystemCorruptedException("Input does not contain a raw EXT4 filesystem.");
                }
            }

            var image = await tools.PathForToolAsync(temporary, cancellationToken).ConfigureAwait(false);
            if (repairBeforeResize)
            {
                var repaired = await tools.RunAsync("e2fsck", ["-fy", image], cancellationToken).ConfigureAwait(false);
                if (repaired.ExitCode is not (0 or 1))
                {
                    throw new FileSystemCorruptedException($"e2fsck repair failed ({repaired.ExitCode}): {repaired.Output}");
                }
            }

            await CheckAsync(tools, image, cancellationToken).ConfigureAwait(false);
            NativeFileSystemTools.RequireSuccess("resize2fs",
                await tools.RunAsync("resize2fs", ["-M", image], cancellationToken).ConfigureAwait(false));
            await CheckAsync(tools, image, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await using (var result = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                result.Flush(flushToDisk: true);
            }
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

    private static async Task CheckAsync(NativeFileSystemTools tools, string image, CancellationToken cancellationToken)
        => NativeFileSystemTools.RequireSuccess("e2fsck",
            await tools.RunAsync("e2fsck", ["-fn", image], cancellationToken).ConfigureAwait(false));
}