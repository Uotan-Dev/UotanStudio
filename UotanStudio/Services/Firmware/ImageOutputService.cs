using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat;

namespace UotanStudio.Services.Firmware;

/// <summary>Builds image and Android DAT outputs on disk. 在磁盘上构建镜像与 Android DAT 产物。</summary>
internal static class ImageOutputService
{
    internal static FirmwareOpResult Build(ImageOperationPaths paths, string operation, int brotliLevel)
    {
        if (operation is not ("img" or "dat" or "br")) throw new ArgumentOutOfRangeException(nameof(operation));
        Directory.CreateDirectory(paths.OutputRoot);
        if (operation == "img")
        {
            if (!Directory.Exists(paths.UnpackedDirectory)) throw new DirectoryNotFoundException(paths.UnpackedDirectory);
            var built = PackingService.BuildImage(paths.UnpackedDirectory, Path.Combine(paths.OutputRoot, paths.Name + ".img"));
            return new FirmwareOpResult { Success = built.Success, Summary = built.Summary };
        }

        // Intermediate images stay on disk; no whole-image byte arrays.
        // 中间镜像使用磁盘暂存，不将完整镜像读入内存。
        var staging = Path.Combine(paths.OutputRoot, ".build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var input = paths.Source;
            if (paths.IsDirectory)
            {
                input = Path.Combine(staging, "image.img");
                var built = PackingService.BuildImage(paths.UnpackedDirectory, input);
                if (!built.Success) return FirmwareOpResult.Fail(built.Summary);
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
            DatWriter.Write(input, dat, transfer);
            if (operation == "br")
            {
                var compressed = dat + ".br";
                UotanStudio.Firmware.CrossCutting.Compress.CompressionService.CompressFile(dat, compressed,
                    UotanStudio.Firmware.CrossCutting.Compress.CompressionFormat.Brotli,
                    new() { Level = brotliLevel });
                dat = compressed;
            }
            PublishOutputs(paths.OutputRoot, staging, dat, transfer);
            return new FirmwareOpResult { Success = true, Summary = $"Built {operation}: {paths.Name}" };
        }
        finally
        {
            // Preserve recovery files if publishing or rollback could not complete.
            // 发布或回滚未完成时保留恢复文件。
            if (Directory.Exists(staging) && !Directory.EnumerateFiles(staging, ".backup-*").Any())
                Directory.Delete(staging, recursive: true);
        }
    }

    private static void PublishOutputs(string outputRoot, string staging, params string[] files)
    {
        var published = new List<string>();
        var backups = new List<(string Target, string Backup)>();
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