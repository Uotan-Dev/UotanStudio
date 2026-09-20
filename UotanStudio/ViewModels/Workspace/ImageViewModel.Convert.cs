using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.Compression.Compressors;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Writer;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    /// <summary><para>稀疏转换方向标识（资源管理器「稀疏转换」）。</para>Sparse conversion direction key (explorer "sparse convert").</summary>
    internal const string ConvertSparse = "sparse";

    /// <summary><para>IMG→DAT 转换方向标识。</para>IMG-to-DAT conversion direction key.</summary>
    internal const string ConvertImgToDat = "dat";

    /// <summary><para>IMG→BR 转换方向标识。</para>IMG-to-BR conversion direction key.</summary>
    internal const string ConvertImgToBr = "br";

    /// <summary>
    /// <para>格式转换的预期结果描述，用于执行前的确认对话框。</para>
    /// The expected result of a format conversion, used by the pre-action confirmation dialog.
    /// </summary>
    internal sealed record FormatConversionPreview(string Operation, string OutputDirectory, IReadOnlyList<string> OutputPaths);

    /// <summary>
    /// <para>计算一次格式转换的预期结果，供右键菜单弹出确认对话框展示操作内容与预期输出文件。</para>
    /// Computes the expected result of a format conversion for the context-menu confirmation dialog
    /// (operation description and expected output files).
    /// </summary>
    /// <param name="sourcePath"><para>源镜像的绝对路径。</para>Absolute path of the source image.</param>
    /// <param name="direction"><para>转换方向：<see cref="ConvertSparse"/>、<see cref="ConvertImgToDat"/>、<see cref="ConvertImgToBr"/>。</para>
    /// Conversion direction: <see cref="ConvertSparse"/>, <see cref="ConvertImgToDat"/> or <see cref="ConvertImgToBr"/>.</param>
    /// <returns>预期输出描述；源不属于活动子项目等校验不通过时抛出异常。</returns>
    internal FormatConversionPreview GetFormatConversionPreview(string sourcePath, string direction)
    {
        var (full, project) = ValidateConversionSource(sourcePath);
        var outputDir = Directory.CreateDirectory(project.OutputDir).FullName;
        var baseName = Path.GetFileNameWithoutExtension(full);
        var format = ImageInputService.DetectFormat(full);

        string[] outputPaths;
        if (direction == ConvertSparse)
        {
            var toSparse = !string.Equals(format, "sparse", StringComparison.OrdinalIgnoreCase);
            outputPaths = [Path.Combine(outputDir, baseName + (toSparse ? ".sparse" : ".raw"))];
        }
        else if (direction == ConvertImgToDat)
        {
            outputPaths = [Path.Combine(outputDir, baseName + ".new.dat"), Path.Combine(outputDir, baseName + ".transfer.list")];
        }
        else if (direction == ConvertImgToBr)
        {
            outputPaths = [Path.Combine(outputDir, baseName + ".new.dat.br"), Path.Combine(outputDir, baseName + ".transfer.list")];
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown conversion direction.");
        }

        return new FormatConversionPreview(GetOperationLabel(direction, format, baseName), outputDir, outputPaths);
    }

    /// <summary>
    /// <para>执行一次镜像格式转换并把按字节进度上报到镜像管理面板的进度显示
    /// （百分比、可视化进度、速度、已处理大小与预计剩余时间）。</para>
    /// Runs one image format conversion and reports byte progress to the image manager panel's
    /// progress display (percentage, visual progress, speed, processed size and estimated time left).
    /// </summary>
    /// <param name="sourcePath"><para>源镜像的绝对路径。</para>Absolute path of the source image.</param>
    /// <param name="direction"><para>转换方向（同 <see cref="GetFormatConversionPreview"/>）。</para>Conversion direction (same as <see cref="GetFormatConversionPreview"/>).</param>
    /// <returns>本次转换产出到 <c>output/</c> 的绝对路径列表。</returns>
    /// <exception cref="IOException">源不属于活动子项目或转换失败时抛出。</exception>
    internal async Task<IReadOnlyList<string>> RunExplorerFormatConversionAsync(string sourcePath, string direction)
    {
        if (IsOperating)
        {
            throw new InvalidOperationException("Another image operation is in progress. 正在进行其他镜像操作。");
        }

        var (full, project) = ValidateConversionSource(sourcePath);
        var outputDir = Directory.CreateDirectory(project.OutputDir).FullName;
        var baseName = Path.GetFileNameWithoutExtension(full);
        var format = ImageInputService.DetectFormat(full);

        IsOperating = true;
        // 捕获当前代际：OnIsOperatingChanged(true) 自增后即为本次操作的活跃代际。
        // Capture the active generation after IsOperating flips (it increments in OnIsOperatingChanged).
        var generation = _progressGeneration;
        ProgressPercent = -1;
        _meter.Reset();
        ProgressStats = string.Empty;
        try
        {
            StatusText = string.Format(LanguageService.Instance.GetString("Image_ConvertStage"),
                GetOperationLabel(direction, format, baseName));
            IReadOnlyList<string> produced = direction switch
            {
                ConvertSparse => await ConvertSparseImageAsync(full, outputDir, baseName, format, generation),
                ConvertImgToDat => await ConvertToDatOrBrAsync(full, outputDir, baseName, format, br: false, generation),
                ConvertImgToBr => await ConvertToDatOrBrAsync(full, outputDir, baseName, format, br: true, generation),
                _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown conversion direction.")
            };
            StatusText = string.Format(LanguageService.Instance.GetString("Image_ConvertSummary"), string.Join("  ·  ", produced));
            LogService.Instance.Write(LogLevel.Info, "Images", StatusText);
            return produced;
        }
        catch
        {
            StatusText = LanguageService.Instance.GetString("Image_ConvertFailed");
            LogService.Instance.Write(LogLevel.Error, "Images", $"Format conversion ({direction}) failed: {sourcePath}");
            throw;
        }
        finally
        {
            IsOperating = false;
        }
    }

    /// <summary>Resolves and validates the source image of a conversion against the active sub-project.</summary>
    private static (string Full, SubProjectContext Project) ValidateConversionSource(string sourcePath)
    {
        var context = ProjectContextService.Instance;
        var project = context.ActiveSubProject ?? throw new IOException("Select a sub-project before converting. 请先选择要转换镜像所属的子项目。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootPath));
        var full = Path.GetFullPath(sourcePath);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Select the sub-project containing this image before converting. 请先选择此镜像所属的子项目。");
        if (!File.Exists(full))
            throw new FileNotFoundException("Image file not found.", full);
        return (full, project);
    }

    /// <summary>Builds the localized operation label shown in the confirmation dialog and progress text.</summary>
    private static string GetOperationLabel(string direction, string format, string baseName)
    {
        var language = LanguageService.Instance;
        if (direction == ConvertSparse)
        {
            var toSparse = !string.Equals(format, "sparse", StringComparison.OrdinalIgnoreCase);
            return language.GetString(toSparse ? "Image_ConvertRawToSparse" : "Image_ConvertSparseToRaw");
        }
        return string.Format(language.GetString(direction == ConvertImgToBr ? "Image_ConvertImgToBr" : "Image_ConvertImgToDat"), baseName);
    }

    /// <summary>Creates a phase progress that reports the UTC context and maps a phase-local byte count onto the overall total.</summary>
    private IProgress<long> CreateConversionByteProgress(long baseOffset, long globalTotal, int generation)
        => new Progress<long>(local =>
        {
            if (generation != _progressGeneration || !IsOperating) return;
            ReportByteStatistics(baseOffset + local, globalTotal);
        });

    private async Task<IReadOnlyList<string>> ConvertSparseImageAsync(string source, string outputDir, string baseName, string format, int generation)
    {
        var toSparse = !string.Equals(format, "sparse", StringComparison.OrdinalIgnoreCase);
        var output = Path.Combine(outputDir, baseName + (toSparse ? ".sparse" : ".raw"));
        if (toSparse)
        {
            var total = new FileInfo(source).Length;
            var progress = CreateConversionByteProgress(0, total, generation);
            await Task.Run(() =>
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                using var paced = new ProgressReadStream(input, total, progress);
                using var outputStream = File.Create(output);
                SparseImageConverter.ConvertRawToSparse(paced, outputStream, 4096, total);
            });
        }
        else
        {
            using var input = new SparseReadStream(SparseReader.FromImageFile(source));
            var total = input.Length;
            var progress = CreateConversionByteProgress(0, total, generation);
            using var outputStream = File.Create(output);
            await Task.Run(() => CopyWithProgress(input, outputStream, total, progress));
        }
        return [output];
    }

    private async Task<IReadOnlyList<string>> ConvertToDatOrBrAsync(string source, string outputDir, string baseName, string format, bool br, int generation)
    {
        var datPath = Path.Combine(outputDir, baseName + ".new.dat");
        var transferPath = Path.Combine(outputDir, baseName + ".transfer.list");

        // Sparse sources are first expanded to a raw staging file; the dat pass then reads the same raw bytes.
        // 稀疏源先展开为 raw 暂存文件，之后 DAT 阶段读取同一份 raw 字节。
        string rawInput = source;
        string? stagingRaw = null;
        var rawPhases = 1;
        long rawSize;
        if (string.Equals(format, "sparse", StringComparison.OrdinalIgnoreCase))
        {
            stagingRaw = Path.Combine(outputDir, ".convert-" + Guid.NewGuid().ToString("N") + ".raw");
            rawPhases = 2;
            using (var logical = new SparseReadStream(SparseReader.FromImageFile(source)))
            {
                rawSize = logical.Length;
            }
            var expandBase = 0L;
            var expandTotal = checked(rawPhases * rawSize);
            var expandProgress = CreateConversionByteProgress(expandBase, expandTotal, generation);
            using (var input = new SparseReadStream(SparseReader.FromImageFile(source)))
            using (var outputStream = File.Create(stagingRaw))
            {
                await Task.Run(() => CopyWithProgress(input, outputStream, rawSize, expandProgress));
            }
        }
        else
        {
            rawSize = new FileInfo(source).Length;
        }

        var succeeded = false;
        try
        {
            // DAT 阶段：baseOffset 叠加已完成的展开字节，total 为「展开+DAT」的汇总量。
            // DAT phase: baseOffset stacks the expansion bytes already done; total sums "expansion + DAT".
            var datBase = (rawPhases - 1) * rawSize;
            var datTotal = checked(rawPhases * rawSize);
            var datProgress = new Progress<(long Processed, long Total)>(p =>
            {
                if (generation != _progressGeneration || !IsOperating) return;
                ReportByteStatistics(datBase + p.Processed, datTotal);
            });
            await Task.Run(() => DatWriter.Write(rawInput, datPath, transferPath, new DatWriteOptions(), datProgress));

            if (!br)
            {
                succeeded = true;
                return [datPath, transferPath];
            }

            // BR 阶段：压缩 .new.dat 为 .new.dat.br，把导出文件体积并入总量以延续进度。
            // BR phase: compress .new.dat into .new.dat.br, folding the exported size into the total.
            var datSize = new FileInfo(datPath).Length;
            var brPath = datPath + ".br";
            var brBase = checked(rawPhases * rawSize);
            var brTotal = checked(brBase + datSize);
            var brProgress = CreateConversionByteProgress(brBase, brTotal, generation);
            await Task.Run(() =>
            {
                using var input = new FileStream(datPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                using var paced = new ProgressReadStream(input, datSize, brProgress);
                using var output = File.Create(brPath);
                BrotliCompressor.Compress(paced, output);
            });
            File.Delete(datPath);
            succeeded = true;
            return [brPath, transferPath];
        }
        finally
        {
            // 临时 raw 始终删除；失败的转换回滚不完整的 union 产物。
            // The staging raw is always removed; a failed conversion rolls back incomplete outputs.
            if (stagingRaw is not null) TryDelete(stagingRaw);
            if (!succeeded)
            {
                TryDelete(datPath);
                TryDelete(datPath + ".br");
                TryDelete(transferPath);
            }
        }
    }

    /// <summary>Streams bytes from source to destination while reporting cumulative processed bytes.</summary>
    private static void CopyWithProgress(Stream source, Stream destination, long total, IProgress<long> progress)
    {
        var buffer = new byte[1024 * 1024];
        long processed = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            processed += read;
            progress.Report(Math.Min(processed, total));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "Images", $"Conversion cleanup failed for {path}: {ex.Message}  转换清理失败：{ex.Message}");
        }
    }

    /// <summary>
    /// <para>包装流并统计已读字节，供无进度回调的读取方（稀疏写出、Brotli 压缩）按字节上报进度。</para>
    /// Wraps a stream and counts read bytes so readers without a progress hook (sparse writing,
    /// Brotli compression) can report byte progress; reports the local count (0..total).
    /// </summary>
    private sealed class ProgressReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _total;
        private readonly IProgress<long>? _progress;
        private long _read;

        internal ProgressReadStream(Stream inner, long total, IProgress<long>? progress)
        {
            _inner = inner;
            _total = Math.Max(0, total);
            _progress = progress;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            if (read > 0) Note(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            if (read > 0) Note(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() => _inner.Flush();
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        private void Note(int read)
        {
            _read = Math.Min(_read + read, _total);
            _progress?.Report(_read);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}