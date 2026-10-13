using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.Compression.Files;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.OtaDelta.Dat;

namespace UotanStudio.Services.Firmware;

/// <summary>Builds image and Android DAT outputs on disk.<para>在磁盘上构建镜像与 Android DAT 产物。</para></summary>
internal static class ImageOutputService
{
    /// <summary>
    /// Lists the files a pack operation publishes under its output directory before any writing starts.
    /// <para>列出打包操作在开始写入前会在其输出目录下写出的目标文件。</para>
    /// </summary>
    /// <param name="paths">Operation paths naming the output root and entry name. <para>指明输出根目录与条目名的操作路径。</para></param>
    /// <param name="operation">Pack mode: img, dat or br. <para>打包方式：img、dat 或 br。</para></param>
    /// <returns>The absolute paths that would be published, empty for other modes. <para>将被写出的绝对路径，其他方式为空。</para></returns>
    internal static IReadOnlyList<string> GetPlannedOutputs(ImageOperationPaths paths, string operation) => operation switch
    {
        "img" => [Path.Combine(paths.OutputRoot, paths.Name + ".img")],
        "br" => [Path.Combine(paths.OutputRoot, paths.Name + ".new.dat.br"), Path.Combine(paths.OutputRoot, paths.Name + ".transfer.list")],
        "dat" => [Path.Combine(paths.OutputRoot, paths.Name + ".new.dat"), Path.Combine(paths.OutputRoot, paths.Name + ".transfer.list")],
        _ => []
    };

    /// <summary>
    /// Builds the img, DAT or Brotli output for one entry under its planned paths.
    /// <para>为单个条目在其预定路径下构建 img、dat 或 br 产物。</para>
    /// </summary>
    /// <param name="paths">Operation paths for the entry. <para>该条目的操作路径。</para></param>
    /// <param name="operation">Output kind: img, dat or br. <para>产物种类：img、dat 或 br。</para></param>
    /// <param name="brotliLevel">Brotli compression level applied to the br output. <para>施加于 br 产物的 Brotli 压缩级别。</para></param>
    /// <param name="progress">Optional byte-progress reporter, null when progress is not tracked. <para>可选的字节进度回调，不跟踪进度时为 null。</para></param>
    /// <returns>The build outcome; the img path carries the packing result. <para>构建结果；img 路径附带打包服务的结果。</para></returns>
    internal static FirmwareOpResult Build(ImageOperationPaths paths, string operation, int brotliLevel, IProgress<FileSystemByteProgress>? progress = null)
    {
        if (operation is not ("img" or "dat" or "br")) throw new ArgumentOutOfRangeException(nameof(operation));
        Directory.CreateDirectory(paths.OutputRoot);
        if (operation == "img")
        {
            if (!Directory.Exists(paths.UnpackedDirectory)) throw new DirectoryNotFoundException(paths.UnpackedDirectory);
            return PackingService.BuildImage(paths.UnpackedDirectory, Path.Combine(paths.OutputRoot, paths.Name + ".img"), progress: progress);
        }

        var staging = Path.Combine(paths.OutputRoot, ".build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var input = paths.Source;
            if (paths.IsDirectory)
            {
                input = Path.Combine(staging, "image.img");
                var built = PackingService.BuildImage(paths.UnpackedDirectory, input, progress: progress);
                if (!built.Success) return built;
            }
            if (new SparseFormatHandler().CanHandle(input))
            {
                var raw = Path.Combine(staging, "raw.img");
                using var sparseFile = SparseReader.FromImageFile(input, validateCrc: true);
                using var sparse = new SparseReadStream(sparseFile);
                using var output = File.Create(raw);
                sparse.CopyTo(output);
                input = raw;
            }
            var dat = Path.Combine(staging, paths.Name + ".new.dat");
            var transfer = Path.Combine(staging, paths.Name + ".transfer.list");
            var writing = FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.WritingDat);
            DatWriter.Write(input, dat, transfer, new DatWriteOptions(), writing);
            if (operation == "br")
            {
                var compressed = dat + ".br";
                CompressionFile.CompressFile(dat, compressed,
                    UotanStudio.Firmware.Compression.CompressionFormat.Brotli,
                    new() { Level = brotliLevel },
                    FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.Compressing));
                dat = compressed;
            }
            PublishOutputs(paths.OutputRoot, staging, dat, transfer);
            return new FirmwareOpResult { Success = true, Summary = $"Built {operation}: {paths.Name}" };
        }
        finally
        {
            if (Directory.Exists(staging) && !Directory.EnumerateFiles(staging, ".backup-*").Any())
                Directory.Delete(staging, recursive: true);
        }
    }

    private static void PublishOutputs(string outputRoot, string staging, params ReadOnlySpan<string> files)
    {
        List<string> published = [];
        List<(string Target, string Backup)> backups = [];
        foreach (var file in files)
            if (Directory.Exists(Path.Combine(outputRoot, Path.GetFileName(file))))
                throw new IOException($"Output path is a directory: {Path.GetFileName(file)}");
        try
        {
            foreach (var file in files)
            {
                var target = Path.Combine(outputRoot, Path.GetFileName(file));
                if (File.Exists(target))
                {
                    var backup = Path.Combine(staging, ".backup-" + Guid.NewGuid().ToString("N"));
                    File.Move(target, backup);
                    backups.Add((target, backup));
                }
                File.Move(file, target);
                published.Add(target);
            }
        }
        catch
        {
            foreach (var target in published) File.Delete(target);
            foreach (var (target, backup) in backups) File.Move(backup, target);
            throw;
        }
        foreach (var (_, backup) in backups) File.Delete(backup);
    }
}