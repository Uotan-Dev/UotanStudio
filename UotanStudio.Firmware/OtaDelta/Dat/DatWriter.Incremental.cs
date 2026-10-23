using UotanStudio.Firmware.OtaDelta.Diff;

namespace UotanStudio.Firmware.OtaDelta.Dat;

public static partial class DatWriter
{
    /// <summary>Writes an incremental classic block OTA from a source and target image.
    /// <para>由源镜像与目标镜像写出增量经典块 OTA。</para></summary>
    /// <remarks>A dataset without source-dependent commands (<c>imgdiff</c>, <c>diff</c>, <c>move</c> or <c>stash</c>) cannot be seeded from the base image by the reader, so the writer ships the target as a plain full image instead.
    /// <para>不含源依赖命令（<c>imgdiff</c>、<c>diff</c>、<c>move</c> 或 <c>stash</c>）的数据集无法由 读取器从基座镜像预置，因此此时把目标作为普通全量镜像发布。</para><para>Both images are read once: the encode pipeline overlaps piece encoding with classification, so the patches are never derived from a second read of the same bytes. At most two pending pieces plus the piece being filled are buffered per side, each capped at the 16 MiB a single command may cover.</para><para>两侧镜像只读一遍：编码流水线让片段编码与分类重叠，补丁不会由同一批字节的第二次读取 推导。每侧最多缓冲两个待编码片段加上正在填充的片段，每段上限为单条命令可覆盖的 16 MiB。</para></remarks>
    /// <param name="sourceImagePath">Base image the updater seeds the target from; the file must exist.
    /// <para>updater 用于预置目标的基座镜像；文件必须存在。</para></param>
    /// <param name="targetImagePath">Desired image compared block by block against the source; the file must exist.
    /// <para>与源逐块比较的目标镜像；文件必须存在。</para></param>
    /// <param name="outputDatPath">Data file receiving the non-zero blocks of <c>imgdiff</c> runs; on the full-image fallback it holds every non-zero target block.
    /// <para>接收 <c>imgdiff</c> 区段非零块的数据文件；走全量回退时包含目标的全部非零块。</para></param>
    /// <param name="outputPatchDatPath">File receiving the concatenated bsdiff bytes referenced by the <c>imgdiff</c> commands; written as an empty file on the full-image fallback.
    /// <para>接收各 <c>imgdiff</c> 命令所引用的 bsdiff 字节拼接结果的文件；走全量回退时写为空文件。</para></param>
    /// <param name="outputTransferListPath">Version 4 transfer list carrying the positional <c>imgdiff</c>, <c>zero</c> and <c>new</c> commands.
    /// <para>包含位置式 <c>imgdiff</c>、<c>zero</c> 与 <c>new</c> 命令的版本 4 传输列表。</para></param>
    /// <param name="options">Compression and per-command block cap settings applied to the emitted data; ownership stays with the caller.
    /// <para>应用于所写出数据的压缩与单条命令块数上限设置；所有权归调用方。</para></param>
    /// <param name="progress">Optional byte progress over the target image, coalesced to one notification per 100 millisecond window and ended by a notification carrying the total.
    /// <para>可选的按目标镜像字节进度，按 100 毫秒窗口合并，并以一条携带总量的通知结束。</para></param>
    /// <param name="cancellationToken">Cancels between blocks and before the self-verification pass.
    /// <para>在块间与自校验趟之间响应取消。</para></param>
    /// <exception cref="ArgumentException">One of the paths is empty or contains only whitespace.
    /// <para>某个路径为空或仅含空白字符。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="sourceImagePath"/> or <paramref name="targetImagePath"/> does not exist.
    /// <para><paramref name="sourceImagePath"/> 或 <paramref name="targetImagePath"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException"><see cref="DatWriteOptions.SelfVerifyIncremental"/> is set and the generated triple does not rebuild the target image when replayed through <c>DatReader</c>.
    /// <para>设置了 <see cref="DatWriteOptions.SelfVerifyIncremental"/> 且生成的三元组经 <c>DatReader</c> 回放后未能重建目标镜像。</para></exception>
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

        var meter = new Core.ProgressThrottle(totalBytes, progress, cancellationToken);
        List<TransferListCommand> commands;
        bool sawImgdiffRun;
        using (var emitter = new IncrementalRunWriter(outputDatPath, outputPatchDatPath, options, totalBlocks, cancellationToken))
        {
            using (var source = new FileStream(sourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.RandomAccess))
            using (var target = new FileStream(targetImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.RandomAccess))
            {
                var targetBlock = new byte[BlockSize];
                var sourceBlock = new byte[BlockSize];
                var zeroBlock = new byte[BlockSize];
                long block = 0;
                long runStart = 0;
                var runActive = false;
                var runImgdiff = false;
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

                    if (runActive && (!emit || imgdiff != runImgdiff))
                    {
                        if (runImgdiff)
                        {
                            emitter.EndImgdiffRun();
                        }
                        else
                        {
                            emitter.AddZeroRun(runStart, block);
                        }

                        runActive = false;
                    }

                    if (emit)
                    {
                        if (!runActive)
                        {
                            runStart = block;
                            runImgdiff = imgdiff;
                            runActive = true;
                        }

                        if (imgdiff)
                        {
                            emitter.AppendImgdiffBlock(block, sourceBlock, targetBlock);
                        }
                    }

                    block++;
                    meter.Report((Math.Min(checked(block * BlockSize), totalBytes), totalBytes));
                }
                meter.Complete();

                if (runActive)
                {
                    if (runImgdiff)
                    {
                        emitter.EndImgdiffRun();
                    }
                    else
                    {
                        emitter.AddZeroRun(runStart, block);
                    }
                }

                sawImgdiffRun = emitter.HasImgdiffRun;
                emitter.OpenOutputs();
            }

            commands = emitter.Commands;
        }

        if (!sawImgdiffRun)
        {
            File.WriteAllBytes(outputPatchDatPath, []);
            Write(targetImagePath, outputDatPath, outputTransferListPath, options, null, cancellationToken);
            return;
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
    /// Encodes one difference piece with the requested command style and returns the command kind together with
    /// its patch bytes. <see cref="DatIncrementalCommandStyle.ShortestWins"/> needs both encodings only when the
    /// two sides differ in length, because that is the only case where the IMGDIFF2 container can carry a RAW tail;
    /// for equal lengths the container is the bare BSDIFF1 stream behind a fixed 40-byte header and therefore
    /// always loses, so that style computes the bare stream alone.
    /// <para>按请求的命令风格编码一个差异片段，返回命令类型及其补丁字节。只有在两侧长度不等——即 IMGDIFF2 容器可能
    /// 携带 RAW 尾部——时，<see cref="DatIncrementalCommandStyle.ShortestWins"/> 才需要两种编码；长度相等时容器
    /// 就是固定 40 字节头部加裸 BSDIFF1 流，必然更长，因此该风格只计算裸流。</para>
    /// </summary>
    /// <param name="sourceRegion">Base bytes of the piece; read from index <c>0</c>. <para>该片段的基座字节；从下标 <c>0</c> 读取。</para></param>
    /// <param name="sourceLength">Number of valid bytes in <paramref name="sourceRegion"/>. <para><paramref name="sourceRegion"/> 中的有效字节数。</para></param>
    /// <param name="targetRegion">Desired bytes of the piece; read from index <c>0</c>. <para>该片段的目标字节；从下标 <c>0</c> 读取。</para></param>
    /// <param name="targetLength">Number of valid bytes in <paramref name="targetRegion"/>. <para><paramref name="targetRegion"/> 中的有效字节数。</para></param>
    /// <param name="style">Requested command style. <para>请求的命令风格。</para></param>
    /// <param name="ct">Cancels during the encoding. <para>在编码期间响应取消。</para></param>
    /// <returns>The command kind and the patch bytes it references. <para>命令类型及其引用的补丁字节。</para></returns>
    private static (TransferListCommandKind Kind, byte[] PatchBytes) CreateRunPatch(
        byte[] sourceRegion, int sourceLength, byte[] targetRegion, int targetLength, DatIncrementalCommandStyle style, CancellationToken ct)
    {
        byte[]? imgdiffBytes = null;
        if (style is DatIncrementalCommandStyle.Imgdiff || (style is DatIncrementalCommandStyle.ShortestWins && sourceLength != targetLength))
        {
            using var container = new MemoryStream();
            using (var source = new MemoryStream(sourceRegion, 0, sourceLength, writable: false))
            using (var target = new MemoryStream(targetRegion, 0, targetLength, writable: false))
            {
                ImageDeltaBuilder.Build(
                    source,
                    target,
                    container,
                    ImageDeltaBuilder.DefaultBlockSize,
                    new ImgDiffBuilderOptions { SelfVerify = false },
                    ct);
            }

            imgdiffBytes = container.ToArray();
        }

        if (style is DatIncrementalCommandStyle.Imgdiff)
        {
            return (TransferListCommandKind.Imgdiff, imgdiffBytes!);
        }

        byte[] bsdiffBytes;
        using (var raw = new MemoryStream())
        {
            using (var source = new MemoryStream(sourceRegion, 0, sourceLength, writable: false))
            using (var target = new MemoryStream(targetRegion, 0, targetLength, writable: false))
            {
                BinaryDeltaPatch.Create(source, target, raw, ct);
            }

            bsdiffBytes = raw.ToArray();
        }

        if (imgdiffBytes is null)
        {
            return (TransferListCommandKind.Diff, bsdiffBytes);
        }

        return imgdiffBytes.LongLength <= bsdiffBytes.LongLength
            ? (TransferListCommandKind.Imgdiff, imgdiffBytes)
            : (TransferListCommandKind.Diff, bsdiffBytes);
    }

    /// <summary>
    /// Emits incremental commands while the classification pass runs: accumulates one piece of an <c>imgdiff</c>
    /// run, hands it to a bounded encode pipeline as soon as the piece is full or the run ends, and consumes the
    /// encoded pieces in production order so commands and patch offsets stay identical to a serial pass. Output
    /// files are created lazily, and <see cref="OpenOutputs"/> publishes them empty for datasets whose commands
    /// never write to them.
    /// <para>在分类趟运行时发射增量命令：累积 <c>imgdiff</c> 区段的一个片段，在片段填满或区段结束时交给有界编码
    /// 流水线，并按产生顺序消费已编码片段，使命令与补丁偏移与串行趟完全一致。输出文件按需创建，命令从不写入的
    /// 文件由 <see cref="OpenOutputs"/> 以空文件发布。</para>
    /// </summary>
    private sealed class IncrementalRunWriter : IDisposable
    {
        private readonly string _newDataPath;
        private readonly string _patchPath;
        private readonly DatWriteOptions _options;
        private readonly CancellationToken _ct;
        private readonly int _maximumPieceBlocks;
        private const int PipelineDepth = 2;
        private readonly List<TransferListCommand> _commands = [];
        private readonly Queue<PendingPiece> _pending = new();
        private byte[]? _sourcePiece;
        private byte[]? _targetPiece;
        private byte[]? _spareSourcePiece;
        private byte[]? _spareTargetPiece;
        private FileStream? _newData;
        private FileStream? _patch;
        private long _pieceStart;
        private long _patchOffset;
        private int _pieceBlocks;

        /// <summary>Initializes a writer that emits into the given data and patch files on demand.
        /// <para>初始化按需写入给定数据文件与补丁文件的写手。</para></summary>
        /// <param name="newDataPath">Data file the <c>new</c> commands stream into. <para><c>new</c> 命令写入的数据文件。</para></param>
        /// <param name="patchPath">Patch file the encoded pieces concatenate into. <para>编码后的片段拼接写入的补丁文件。</para></param>
        /// <param name="options">Command-generation settings; only read. <para>命令生成设置；只读取。</para></param>
        /// <param name="totalBlocks">
        /// Blocks the target image spans; bounds the piece buffers for images shorter than one command may cover.
        /// <para>目标镜像覆盖的块数；对短于单条命令上限的镜像用于收窄片段缓冲区。</para>
        /// </param>
        /// <param name="ct">Cancels during piece encoding. <para>在片段编码期间响应取消。</para></param>
        public IncrementalRunWriter(string newDataPath, string patchPath, DatWriteOptions options, long totalBlocks, CancellationToken ct)
        {
            _newDataPath = newDataPath;
            _patchPath = patchPath;
            _options = options;
            _maximumPieceBlocks = (int)Math.Min(MaximumBlocksPerPatchCommand, totalBlocks);
            _ct = ct;
        }

        /// <summary>Gets the commands emitted so far, in transfer-list order. <para>按传输列表顺序获取已发射的命令。</para></summary>
        /// <value>Zero, new and positional patch commands in the order the classification pass met them.
        /// <para>分类趟遇到的 <c>zero</c>、<c>new</c> 与位置式补丁命令。</para></value>
        public List<TransferListCommand> Commands => _commands;

        /// <summary>Gets whether at least one <c>imgdiff</c> run was emitted. <para>是否至少发射过一个 <c>imgdiff</c> 区段。</para></summary>
        /// <value>Set when an <c>imgdiff</c> run ends; a dataset without such a run falls back to a full image.
        /// <para>在 <c>imgdiff</c> 区段结束时置位；没有该区段的数据集回退为全量镜像。</para></value>
        public bool HasImgdiffRun { get; private set; }

        /// <summary>
        /// Appends one block to the current piece, zero padding included, and hands the piece to the encode pipeline
        /// once it reaches <see cref="MaximumBlocksPerPatchCommand"/> blocks.
        /// <para>把一块（含补零）追加到当前片段，片段达到 <see cref="MaximumBlocksPerPatchCommand"/> 块时交给编码流水线。</para>
        /// </summary>
        /// <param name="block">Block index the piece starts at. <para>该片段起始的块号。</para></param>
        /// <param name="sourceBlock">Classified source block, zero padded to <see cref="BlockSize"/> bytes.
        /// <para>分类用的源块，已补零到 <see cref="BlockSize"/> 字节。</para></param>
        /// <param name="targetBlock">Classified target block, zero padded to <see cref="BlockSize"/> bytes.
        /// <para>分类用的目标块，已补零到 <see cref="BlockSize"/> 字节。</para></param>
        public void AppendImgdiffBlock(long block, byte[] sourceBlock, byte[] targetBlock)
        {
            if (_pieceBlocks == 0)
            {
                _pieceStart = block;
                _sourcePiece = _spareSourcePiece ?? new byte[checked(_maximumPieceBlocks * BlockSize)];
                _targetPiece = _spareTargetPiece ?? new byte[checked(_maximumPieceBlocks * BlockSize)];
                _spareSourcePiece = null;
                _spareTargetPiece = null;
            }

            var offset = _pieceBlocks * BlockSize;
            sourceBlock.AsSpan().CopyTo(_sourcePiece.AsSpan(offset, BlockSize));
            targetBlock.AsSpan().CopyTo(_targetPiece.AsSpan(offset, BlockSize));
            _pieceBlocks++;
            if (_pieceBlocks == _maximumPieceBlocks)
            {
                FlushPiece();
            }
        }

        /// <summary>Marks the current <c>imgdiff</c> run as seen, flushes its partially filled piece and drains pending encodes.
        /// <para>标记已出现 <c>imgdiff</c> 区段，冲刷其未填满的片段并排空在途编码。</para></summary>
        public void EndImgdiffRun()
        {
            HasImgdiffRun = true;
            FlushPiece();
            DrainPending();
        }

        /// <summary>Appends one <c>zero</c> command for a finished run. <para>为一个结束的区段追加一条 <c>zero</c> 命令。</para></summary>
        /// <param name="start">First block of the run. <para>区段首块。</para></param>
        /// <param name="end">Block after the last block of the run. <para>区段末块之后的一块。</para></param>
        public void AddZeroRun(long start, long end)
        {
            DrainPending();
            _commands.Add(new TransferListCommand(TransferListCommandKind.Zero, [new BlockRange(start, end)]));
        }

        /// <summary>
        /// Opens both output files even when no command writes to them, so a successful pass always publishes the
        /// empty <c>.new.dat</c> and <c>.patch.dat</c> members the callers expect.
        /// <para>即便没有命令写入也会打开两个输出文件，使成功的一趟总是发布调用方预期的空 <c>.new.dat</c> 与
        /// <c>.patch.dat</c>。</para>
        /// </summary>
        public void OpenOutputs()
        {
            DrainPending();
            _ = NewData;
            _ = Patch;
        }

        /// <summary>Observes any abandoned encode and closes the output files opened for emitted commands.
        /// <para>观察任何被放弃的在途编码，并关闭为已发射命令打开的输出文件。</para></summary>
        public void Dispose()
        {
            ObservePending();
            _patch?.Dispose();
            _newData?.Dispose();
        }

        private FileStream NewData
            => _newData ??= new FileStream(_newDataPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize);

        private FileStream Patch
            => _patch ??= new FileStream(_patchPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize);

        private void FlushPiece()
        {
            if (_pieceBlocks == 0)
            {
                return;
            }

            if (_pending.Count >= PipelineDepth)
            {
                ConsumeOldest();
            }

            var source = _sourcePiece!;
            var target = _targetPiece!;
            _sourcePiece = null;
            _targetPiece = null;
            var start = _pieceStart;
            var blocks = _pieceBlocks;
            var length = checked(blocks * BlockSize);
            _pieceBlocks = 0;
            var style = _options.IncrementalCommandStyle;
            var encoding = Task.Run(() => CreateRunPatch(source, length, target, length, style, _ct), _ct);
            _pending.Enqueue(new PendingPiece(start, blocks, length, source, target, encoding));
        }

        private void DrainPending()
        {
            while (_pending.Count > 0)
            {
                ConsumeOldest();
            }
        }

        private void ConsumeOldest()
        {
            var piece = _pending.Dequeue();
            var (kind, patchBytes) = piece.Encoding.GetAwaiter().GetResult();
            var range = new BlockRange(piece.Start, piece.Start + piece.Blocks);
            if (_options.AllowRawNewCommands && piece.Length <= patchBytes.Length)
            {
                _commands.Add(new TransferListCommand(TransferListCommandKind.New, [range]));
                NewData.Write(piece.Target, 0, piece.Length);
            }
            else
            {
                Patch.Write(patchBytes);
                _patchOffset += patchBytes.Length;
                _commands.Add(new TransferListCommand(kind, [range], sourceRanges: [range])
                {
                    SourceBlockCount = piece.Blocks,
                    SourceLocations = [new BlockRange(0, piece.Blocks)],
                    PatchOffset = _patchOffset - patchBytes.Length,
                    PatchLength = patchBytes.Length,
                    SourceHash = Sha1(piece.Source, piece.Length),
                    TargetHash = Sha1(piece.Target, piece.Length),
                });
            }

            _spareSourcePiece ??= piece.Source;
            _spareTargetPiece ??= piece.Target;
        }

        private void ObservePending()
        {
            while (_pending.Count > 0)
            {
                var piece = _pending.Dequeue();
                try
                {
                    piece.Encoding.GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                }
            }
        }

        private sealed record PendingPiece(
            long Start,
            int Blocks,
            int Length,
            byte[] Source,
            byte[] Target,
            Task<(TransferListCommandKind Kind, byte[] PatchBytes)> Encoding);
    }
}