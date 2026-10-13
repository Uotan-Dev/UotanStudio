using System.Security.Cryptography;
using System.Text;
using UotanStudio.Compress.Compressors;
using UotanStudio.Firmware.OtaDelta.Diff;

namespace UotanStudio.Firmware.OtaDelta.Dat;

/// <summary>
/// Writes block-OTA datasets — a <c>.new.dat</c> (optionally <c>.new.dat.br</c>) data file plus a
/// <c>.transfer.list</c> — from raw partition images, one <see cref="BlockSize"/>-byte block at a time.
/// <para>从原始分区镜像写出块 OTA 数据集——<c>.new.dat</c>（或 <c>.new.dat.br</c>）数据文件与
/// <c>.transfer.list</c>——每次处理一个 <see cref="BlockSize"/> 字节的数据块。</para>
/// </summary>
/// <remarks>
/// File sizes are read from the input before any output is written, so a missing or unreadable input fails before
/// existing outputs are replaced. Progress reporting is best effort and runs on the calling thread.
/// <para>写出前会先读取输入文件大小，因此输入缺失或不可读时不会替换已有输出。进度上报为尽力而为，并在调用线程上执行。</para>
/// </remarks>
public static class DatWriter
{
    /// <summary>Block size in bytes used for range accounting and for the data file layout. <para>用于范围统计与数据文件布局的块大小（字节）。</para></summary>
    public const int BlockSize = 4096;

    private const int StreamBufferSize = 1024 * 1024;

    /// <remarks>
    /// A block-diff run longer than this many blocks (16 MiB) is split across several commands so one command's
    /// source and target regions stay bounded in memory.
    /// <para>超过该块数（16 MiB）的差分区段会拆成多条命令，使单条命令的源/目标区域在内存中有界。</para>
    /// </remarks>
    private const long MaximumBlocksPerPatchCommand = 4096;

    /// <summary>
    /// Writes a raw image as a DAT image pair.
    /// <para>将原始镜像写出为 DAT 镜像对。</para>
    /// </summary>
    /// <param name="inputImagePath">
    /// Raw (non-sparse) image to read; the file must exist.
    /// <para>待读取的原始（非 sparse）镜像；文件必须存在。</para>
    /// </param>
    /// <param name="outputDatPath">
    /// Data file created with every non-zero block, zero padding to the block boundary; replaced when it already
    /// exists. <para>写出全部非零块（末尾按块边界补零）的数据文件；已存在时被替换。</para>
    /// </param>
    /// <param name="outputTransferListPath">
    /// Version 4 transfer list describing the <c>new</c> and <c>zero</c> ranges; replaced when it already exists.
    /// <para>描述 <c>new</c> 与 <c>zero</c> 范围的版本 4 传输列表；已存在时被替换。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// A path is empty or contains only whitespace. <para>路径为空或仅含空白字符。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="inputImagePath"/> does not exist. <para><paramref name="inputImagePath"/> 不存在。</para>
    /// </exception>
    public static void Write(string inputImagePath, string outputDatPath, string outputTransferListPath)
        => Write(inputImagePath, outputDatPath, outputTransferListPath, new DatWriteOptions());

    /// <summary>
    /// Writes a raw image as a DAT image pair with the given options.
    /// <para>使用给定选项将原始镜像写出为 DAT 镜像对。</para>
    /// </summary>
    /// <param name="inputImagePath">
    /// Raw (non-sparse) image to read; the file must exist.
    /// <para>待读取的原始（非 sparse）镜像；文件必须存在。</para>
    /// </param>
    /// <param name="outputDatPath">
    /// Data file created with every non-zero block, zero padding to the block boundary; replaced when it already
    /// exists. <para>写出全部非零块（末尾按块边界补零）的数据文件；已存在时被替换。</para>
    /// </param>
    /// <param name="outputTransferListPath">
    /// Version 4 transfer list describing the <c>new</c> and <c>zero</c> ranges; replaced when it already exists.
    /// <para>描述 <c>new</c> 与 <c>zero</c> 范围的版本 4 传输列表；已存在时被替换。</para>
    /// </param>
    /// <param name="options">
    /// Compression and per-command block cap settings; ownership stays with the caller and the object is only read.
    /// <para>压缩与单条命令块数上限设置；所有权归调用方，本方法只读取。</para>
    /// </param>
    /// <param name="progress">
    /// Optional byte progress, fed with (processed bytes read from the raw image, total raw bytes); <c>null</c> to
    /// disable reporting. Reports are coalesced to one notification per 100 millisecond window and always end with a
    /// notification carrying the total. <para>可选的按字节进度回调（读入原始镜像的字节数、原始镜像总字节数）；<c>null</c> 表示不报告。
    /// 上报按 100 毫秒窗口合并，并必定以一条携带总量的通知结束。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels between blocks and before self-verification; a cancelled write may leave partial output files and
    /// never runs the verify pass. <para>在块间与自校验前响应取消；被取消的写出可能留下部分输出文件且不会执行校验趟。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// A path is empty or contains only whitespace. <para>路径为空或仅含空白字符。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> is <see langword="null"/>. <para><paramref name="options"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// <see cref="DatWriteOptions.SelfVerifyIncremental"/> is set and the freshly written dataset does not rebuild
    /// the input image. <para>设置了 <see cref="DatWriteOptions.SelfVerifyIncremental"/> 且新写出的数据集未能重建输入镜像。</para>
    /// </exception>
    public static void Write(string inputImagePath, string outputDatPath, string outputTransferListPath, DatWriteOptions options, IProgress<(long Processed, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDatPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTransferListPath);
        ArgumentNullException.ThrowIfNull(options);

        var totalBytes = new FileInfo(inputImagePath).Length;
        var totalBlocks = (totalBytes + BlockSize - 1) / BlockSize;

        var newRanges = new List<BlockRange>();
        var zeroRanges = new List<BlockRange>();
        var meter = new Core.ProgressThrottle(totalBytes, progress, cancellationToken);

        using (var input = new FileStream(inputImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.SequentialScan))
        using (var output = new FileStream(outputDatPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize))
        {
            var buffer = new byte[BlockSize];
            long block = 0;
            long? newStart = null;
            long? zeroStart = null;
            while (block < totalBlocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = ReadBlock(input, buffer);
                var isZero = IsZeroBlock(buffer, read);
                if (isZero)
                {
                    if (newStart is not null)
                    {
                        newRanges.Add(new BlockRange(newStart.Value, block));
                        newStart = null;
                    }

                    zeroStart ??= block;
                }
                else
                {
                    if (zeroStart is not null)
                    {
                        zeroRanges.Add(new BlockRange(zeroStart.Value, block));
                        zeroStart = null;
                    }

                    newStart ??= block;
                    if (read < BlockSize)
                    {
                        Array.Clear(buffer, read, BlockSize - read);
                    }

                    output.Write(buffer, 0, BlockSize);
                }

                block++;
                meter.Report((Math.Min(checked(block * BlockSize), totalBytes), totalBytes));
            }
            meter.Complete();

            if (newStart is not null)
            {
                newRanges.Add(new BlockRange(newStart.Value, block));
            }

            if (zeroStart is not null)
            {
                zeroRanges.Add(new BlockRange(zeroStart.Value, block));
            }
        }

        using (var writer = new StreamWriter(outputTransferListPath))
        {
            writer.WriteLine("4");
            writer.WriteLine(totalBlocks);
            writer.WriteLine("0");
            writer.WriteLine("0");
            WriteNewCommands(writer, newRanges, options.MaxCommandRangeBlocks);
            if (zeroRanges.Count > 0)
            {
                writer.WriteLine($"zero {FormatRanges(zeroRanges)}");
            }
        }

        if (options.CompressWithBrotli)
        {
            CompressDatWithBrotli(outputDatPath);
        }

        if (options.SelfVerifyIncremental)
        {
            SelfVerify(outputTransferListPath, options.CompressWithBrotli ? outputDatPath + ".br" : outputDatPath, null, null, inputImagePath, cancellationToken);
        }
    }

    /// <summary>
    /// Writes an incremental classic block OTA from a source and target image.
    /// <para>由源镜像与目标镜像写出增量经典块 OTA。</para>
    /// </summary>
    /// <remarks>
    /// A dataset without source-dependent commands (<c>imgdiff</c>, <c>diff</c>, <c>move</c> or <c>stash</c>)
    /// cannot be seeded from the base image by the reader, so the writer ships the target as a plain
    /// full image instead.
    /// <para>不含源依赖命令（<c>imgdiff</c>、<c>diff</c>、<c>move</c> 或 <c>stash</c>）的数据集无法由
    /// 读取器从基座镜像预置，因此此时把目标作为普通全量镜像发布。</para>
    /// </remarks>
    /// <param name="sourceImagePath">Base image the updater seeds the target from; the file must exist. <para>updater 用于预置目标的基座镜像；文件必须存在。</para></param>
    /// <param name="targetImagePath">Desired image compared block by block against the source; the file must exist. <para>与源逐块比较的目标镜像；文件必须存在。</para></param>
    /// <param name="outputDatPath">
    /// Data file receiving the non-zero blocks of <c>imgdiff</c> runs; on the full-image fallback it holds every
    /// non-zero target block. <para>接收 <c>imgdiff</c> 区段非零块的数据文件；走全量回退时包含目标的全部非零块。</para>
    /// </param>
    /// <param name="outputPatchDatPath">
    /// File receiving the concatenated bsdiff bytes referenced by the <c>imgdiff</c> commands; written as an empty
    /// file on the full-image fallback.
    /// <para>接收各 <c>imgdiff</c> 命令所引用的 bsdiff 字节拼接结果的文件；走全量回退时写为空文件。</para>
    /// </param>
    /// <param name="outputTransferListPath">
    /// Version 4 transfer list carrying the positional <c>imgdiff</c>, <c>zero</c> and <c>new</c> commands.
    /// <para>包含位置式 <c>imgdiff</c>、<c>zero</c> 与 <c>new</c> 命令的版本 4 传输列表。</para>
    /// </param>
    /// <param name="options">
    /// Compression and per-command block cap settings applied to the emitted data; ownership stays with the caller.
    /// <para>应用于所写出数据的压缩与单条命令块数上限设置；所有权归调用方。</para>
    /// </param>
    /// <param name="progress">Optional byte progress over the target image, coalesced to one notification per 100
    /// millisecond window and ended by a notification carrying the total. <para>可选的按目标镜像字节进度，按 100
    /// 毫秒窗口合并，并以一条携带总量的通知结束。</para></param>
    /// <param name="cancellationToken">
    /// Cancels between blocks, patch computations and the self-verification pass. <para>在块间、补丁计算与自校验趟之间响应取消。</para>
    /// </param>
    /// <exception cref="ArgumentException">One of the paths is empty or contains only whitespace. <para>某个路径为空或仅含空白字符。</para></exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> is <see langword="null"/>. <para><paramref name="options"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="sourceImagePath"/> or <paramref name="targetImagePath"/> does not exist.
    /// <para><paramref name="sourceImagePath"/> 或 <paramref name="targetImagePath"/> 不存在。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// <see cref="DatWriteOptions.SelfVerifyIncremental"/> is set and the generated triple does not rebuild the
    /// target image when replayed through <c>DatReader</c>. <para>设置了 <see cref="DatWriteOptions.SelfVerifyIncremental"/>
    /// 且生成的三元组经 <c>DatReader</c> 回放后未能重建目标镜像。</para>
    /// </exception>
    public static void WriteIncremental(string sourceImagePath, string targetImagePath, string outputDatPath, string outputPatchDatPath,
        string outputTransferListPath, DatWriteOptions options, IProgress<(long Processed, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDatPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPatchDatPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTransferListPath);
        ArgumentNullException.ThrowIfNull(options);

        var sourceLength = new FileInfo(sourceImagePath).Length;
        var totalBytes = new FileInfo(targetImagePath).Length;
        var totalBlocks = (totalBytes + BlockSize - 1) / BlockSize;

        var runs = new List<(long Start, long End, bool Imgdiff)>();
        var meter = new Core.ProgressThrottle(totalBytes, progress, cancellationToken);
        using (var source = new FileStream(sourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.RandomAccess))
        using (var target = new FileStream(targetImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.RandomAccess))
        {
            var targetBlock = new byte[BlockSize];
            var sourceBlock = new byte[BlockSize];
            var zeroBlock = new byte[BlockSize];
            long block = 0;
            while (block < totalBlocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadAt(target, block * BlockSize, targetBlock);
                if (block * BlockSize < sourceLength)
                {
                    ReadAt(source, block * BlockSize, sourceBlock);
                }
                else
                {
                    Array.Clear(sourceBlock);
                }

                bool imgdiff;
                bool emit;
                if (targetBlock.AsSpan().SequenceEqual(sourceBlock))
                {
                    emit = false; imgdiff = false;
                }
                else if (targetBlock.AsSpan().SequenceEqual(zeroBlock))
                {
                    emit = true; imgdiff = false;
                }
                else
                {
                    emit = true; imgdiff = true;
                }

                if (emit && runs.Count > 0 && runs[^1].End == block && runs[^1].Imgdiff == imgdiff)
                {
                    runs[^1] = (runs[^1].Start, block + 1, imgdiff);
                }
                else
                {
                    if (emit) runs.Add((block, block + 1, imgdiff));
                }

                block++;
                meter.Report((Math.Min(checked(block * BlockSize), totalBytes), totalBytes));
            }
            meter.Complete();
        }

        if (!runs.Any(run => run.Imgdiff))
        {
            File.WriteAllBytes(outputPatchDatPath, []);
            Write(targetImagePath, outputDatPath, outputTransferListPath, options, null, cancellationToken);
            return;
        }

        var commands = new List<TransferListCommand>();
        long patchOffset = 0;
        using (var source = new FileStream(sourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.RandomAccess))
        using (var target = new FileStream(targetImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.RandomAccess))
        using (var patch = new FileStream(outputPatchDatPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize))
        using (var newData = new FileStream(outputDatPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize))
        {
            foreach (var (start, end, isImgdiff) in runs)
            {
                if (!isImgdiff)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    commands.Add(new TransferListCommand(TransferListCommandKind.Zero, [new BlockRange(start, end)]));
                    continue;
                }

                for (var pieceStart = start; pieceStart < end; pieceStart += MaximumBlocksPerPatchCommand)
                {
                    var count = Math.Min(MaximumBlocksPerPatchCommand, end - pieceStart);
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceRegion = ReadRegion(source, pieceStart, count);
                    var targetRegion = ReadRegion(target, pieceStart, count);
                    var range = new BlockRange(pieceStart, pieceStart + count);
                    var (kind, patchBytes) = CreateRunPatch(sourceRegion, targetRegion, options.IncrementalCommandStyle, cancellationToken);
                    if (options.AllowRawNewCommands && targetRegion.LongLength <= patchBytes.LongLength)
                    {
                        commands.Add(new TransferListCommand(TransferListCommandKind.New, [range]));
                        newData.Write(targetRegion);
                        continue;
                    }

                    patch.Write(patchBytes);
                    patchOffset += patchBytes.Length;
                    commands.Add(new TransferListCommand(kind, [range], sourceRanges: [range])
                    {
                        SourceBlockCount = count,
                        SourceLocations = [new BlockRange(0, count)],
                        PatchOffset = patchOffset - patchBytes.Length,
                        PatchLength = patchBytes.Length,
                        SourceHash = Sha1(sourceRegion),
                        TargetHash = Sha1(targetRegion),
                    });
                }
            }
        }

        if (!commands.Any(command => command.Kind is TransferListCommandKind.Imgdiff or TransferListCommandKind.Diff
            or TransferListCommandKind.Move or TransferListCommandKind.Stash))
        {
            File.WriteAllBytes(outputPatchDatPath, []);
            Write(targetImagePath, outputDatPath, outputTransferListPath, options, null, cancellationToken);
            return;
        }

        var list = new TransferList(4, totalBlocks, 0, 0, commands);
        TransferListSerializer.Write(outputTransferListPath, list);
        if (options.SelfVerifyIncremental)
        {
            SelfVerify(outputTransferListPath, outputDatPath, outputPatchDatPath, sourceImagePath, targetImagePath, cancellationToken);
        }
    }

    /// <summary>
    /// Encodes one differing run with the requested command style and returns the command kind together with
    /// its patch bytes. <see cref="DatIncrementalCommandStyle.ShortestWins"/> computes both encodings and keeps
    /// the smaller one; the other styles compute a single encoding.
    /// <para>按请求的命令风格编码一个差异区段，返回命令类型及其补丁字节。<see cref="DatIncrementalCommandStyle.ShortestWins"/>
    /// 同时计算两种编码并保留较短者；其余风格只计算一种编码。</para>
    /// </summary>
    private static (TransferListCommandKind Kind, byte[] PatchBytes) CreateRunPatch(byte[] sourceRegion, byte[] targetRegion, DatIncrementalCommandStyle style, CancellationToken ct)
    {
        byte[]? imgdiffBytes = null;
        byte[]? bsdiffBytes = null;
        if (style is DatIncrementalCommandStyle.Imgdiff or DatIncrementalCommandStyle.ShortestWins)
        {
            using var container = new MemoryStream();
            ImageDeltaBuilder.Build(
                new MemoryStream(sourceRegion, writable: false),
                new MemoryStream(targetRegion, writable: false),
                container,
                ImageDeltaBuilder.DefaultBlockSize,
                new ImgDiffBuilderOptions { SelfVerify = false },
                ct);
            imgdiffBytes = container.ToArray();
        }

        if (style is DatIncrementalCommandStyle.Bsdiff or DatIncrementalCommandStyle.ShortestWins)
        {
            using var raw = new MemoryStream();
            BinaryDeltaPatch.Create(new MemoryStream(sourceRegion, writable: false), new MemoryStream(targetRegion, writable: false), raw, ct);
            bsdiffBytes = raw.ToArray();
        }

        if (imgdiffBytes is null)
        {
            return (TransferListCommandKind.Diff, bsdiffBytes!);
        }

        if (bsdiffBytes is null || imgdiffBytes.LongLength <= bsdiffBytes.LongLength)
        {
            return (TransferListCommandKind.Imgdiff, imgdiffBytes);
        }

        return (TransferListCommandKind.Diff, bsdiffBytes);
    }

    /// <summary>
    /// Replays the freshly written dataset through <c>DatReader</c> and fails unless the rebuilt image equals
    /// the reference image byte-for-byte (extra block padding must be zero). Generation therefore always implies
    /// an apply-side check when self-verification is enabled.
    /// <para>用 <c>DatReader</c> 回放刚写出的数据集，除非重建镜像与参照镜像逐字节相等（多出的块填充必须为零）否则失败。
    /// 因此启用自校验时，生成必然蕴含一次 apply 端检查。</para>
    /// </summary>
    private static void SelfVerify(string transferListPath, string newDataPath, string? patchDataPath, string? sourceImagePath, string referenceImagePath, CancellationToken ct)
    {
        var rebuiltPath = Path.Combine(Path.GetTempPath(), "uotan-dat-verify-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            DatReader.Unpack(transferListPath, newDataPath, patchDataPath, sourceImagePath, rebuiltPath, false, null, ct);
            var expected = new FileInfo(referenceImagePath).Length;
            using var expectedStream = File.OpenRead(referenceImagePath);
            using var actualStream = File.OpenRead(rebuiltPath);
            var buffer = new byte[64 * 1024];
            var expectedBuffer = new byte[buffer.Length];
            long offset = 0;
            while (offset < expected)
            {
                ct.ThrowIfCancellationRequested();
                var left = (int)Math.Min(buffer.Length, expected - offset);
                expectedStream.ReadExactly(expectedBuffer, 0, left);
                try
                {
                    actualStream.ReadExactly(buffer, 0, left);
                }
                catch (EndOfStreamException)
                {
                    throw new InvalidDataException("Incremental DAT self-verification failed: rebuilt image is shorter than the target.");
                }

                if (!expectedBuffer.AsSpan(0, left).SequenceEqual(buffer.AsSpan(0, left)))
                {
                    throw new InvalidDataException($"Incremental DAT self-verification failed at byte {offset}.");
                }

                offset += left;
            }

            while (actualStream.Position < actualStream.Length)
            {
                var read = actualStream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0)
                {
                    throw new InvalidDataException("Incremental DAT self-verification failed: non-zero data beyond the target length.");
                }
            }
        }
        finally
        {
            File.Delete(rebuiltPath);
        }
    }

    private static void ReadAt(FileStream stream, long offset, byte[] buffer)
    {
        stream.Position = offset;
        var read = ReadBlock(stream, buffer);
        if (read < buffer.Length) Array.Clear(buffer, read, buffer.Length - read);
    }

    private static byte[] ReadRegion(FileStream stream, long startBlock, long count)
    {
        var buffer = new byte[checked((int)(count * BlockSize))];
        stream.Position = startBlock * BlockSize;
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        return buffer;
    }

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

    /// <summary>
    /// Writes the new command lines, splitting the merged ranges into multiple commands when maxBlocksPerCommand is greater than zero.
    /// <para>写出 new 命令行；当 maxBlocksPerCommand 大于 0 时将合并后的范围拆分为 多条命令。</para>
    /// </summary>
    private static void WriteNewCommands(TextWriter writer, List<BlockRange> ranges, int maxBlocksPerCommand)
    {
        if (ranges.Count == 0)
        {
            return;
        }

        if (maxBlocksPerCommand <= 0)
        {
            writer.WriteLine($"new {FormatRanges(ranges)}");
            return;
        }

        var commandRanges = new List<BlockRange>();
        long commandBlocks = 0;
        foreach (var range in ranges)
        {
            var start = range.Start;
            while (start < range.End)
            {
                var capacity = maxBlocksPerCommand - commandBlocks;
                if (capacity <= 0)
                {
                    writer.WriteLine($"new {FormatRanges(commandRanges)}");
                    commandRanges.Clear();
                    commandBlocks = 0;
                    continue;
                }

                var take = (int)Math.Min(range.End - start, capacity);
                commandRanges.Add(new BlockRange(start, start + take));
                commandBlocks += take;
                start += take;
            }
        }

        if (commandRanges.Count > 0)
        {
            writer.WriteLine($"new {FormatRanges(commandRanges)}");
        }
    }

    /// <summary>
    /// Brotli-compresses the .new.dat file into .new.dat.br (streaming, with the compressor's default options) and deletes the uncompressed file.
    /// <para>将 .new.dat 文件流式 Brotli 压缩为 .new.dat.br（使用压缩器默认选项）并删除未压缩 的文件。</para>
    /// </summary>
    private static void CompressDatWithBrotli(string datPath)
    {
        var brPath = datPath + ".br";
        using (var input = new FileStream(datPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.SequentialScan))
        using (var output = new FileStream(brPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize))
        {
            BrotliCompressor.Compress(input, output);
        }

        File.Delete(datPath);
    }

    /// <summary>
    /// Formats block ranges as a comma-separated count,start,end,... range set.
    /// <para>将块范围格式化为逗号分隔的 count,start,end,... 范围集。</para>
    /// </summary>
    private static string FormatRanges(List<BlockRange> ranges)
    {
        var builder = new StringBuilder();
        builder.Append(checked(ranges.Count * 2));
        foreach (var range in ranges)
        {
            builder.Append(',');
            builder.Append(range.Start);
            builder.Append(',');
            builder.Append(range.End);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads up to BlockSize bytes into the buffer, returning the number actually read.
    /// <para>向缓冲区读取最多 BlockSize 字节，返回实际读取的字节数。</para>
    /// </summary>
    private static int ReadBlock(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < BlockSize)
        {
            var read = stream.Read(buffer, total, BlockSize - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>
    /// Determines whether the first length bytes of the buffer are all zero.
    /// <para>判断缓冲区前 length 个字节是否全为零。</para>
    /// </summary>
    private static bool IsZeroBlock(byte[] buffer, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] != 0)
            {
                return false;
            }
        }

        return true;
    }
}