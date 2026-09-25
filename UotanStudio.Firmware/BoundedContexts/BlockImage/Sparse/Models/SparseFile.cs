using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.DataProviders;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;

/// <summary>
/// Mutable in-memory model of a sparse image: a <see cref="SparseHeader"/> plus chunk records kept ordered by start block. The file owns its chunk data providers and releases them on <see cref="Dispose"/>.
/// <para>稀疏镜像的内存可变模型：<see cref="SparseHeader"/> 加按起始块有序的块记录。文件持有各块的数据提供器，并在 <see cref="Dispose"/> 时释放。</para>
/// </summary>
public sealed class SparseFile : IDisposable
{
    private readonly List<SparseChunk> _chunks = [];
    private readonly List<IDisposable> _ownedResources = [];
    private bool _disposed;

    internal void Own(IDisposable resource) => _ownedResources.Add(resource);

    /// <summary>
    /// Initializes an empty sparse file with default settings.
    /// <para>使用默认设置初始化空稀疏文件。</para>
    /// </summary>
    public SparseFile()
    {
        Header = SparseHeader.CreateDefault(4096, 0);
    }

    /// <summary>
    /// Initializes a sparse file with the given block size and total logical size.
    /// <para>使用给定块大小与总逻辑大小初始化稀疏文件。</para>
    /// </summary>
    /// <param name="blockSize">Logical block size; must be non-zero. A multiple of 4 is required for the resulting header to pass <see cref="SparseHeader.IsValid"/>.
    /// <para>逻辑块大小；必须非 0。需为 4 的倍数，所得头部才能通过 <see cref="SparseHeader.IsValid"/>。</para></param>
    /// <param name="totalSize">Total logical size in bytes; rounded up to a whole number of blocks.
    /// <para>总逻辑大小（字节）；向上取整到整块。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is zero or <paramref name="totalSize"/> is negative.
    /// <para><paramref name="blockSize"/> 为 0 或 <paramref name="totalSize"/> 为负。</para></exception>
    /// <exception cref="OverflowException">The rounded-up block count exceeds <see cref="uint.MaxValue"/>.
    /// <para>向上取整后的块数超过 <see cref="uint.MaxValue"/>。</para></exception>
    public SparseFile(uint blockSize, long totalSize)
    {
        ArgumentOutOfRangeException.ThrowIfZero(blockSize);
        ArgumentOutOfRangeException.ThrowIfNegative(totalSize);
        var totalBlocks = DivideRoundUpToUInt32(totalSize, blockSize);
        Header = SparseHeader.CreateDefault(blockSize, totalBlocks);
    }

    /// <summary>The file header describing geometry and declared totals.
    /// <para>描述几何信息与声明总量的文件头部。</para></summary>
    /// <value>Freely replaceable; writers use <see cref="SparseHeader.BlockSize"/>, <see cref="SparseHeader.ChunkHeaderSize"/> and <see cref="SparseHeader.TotalBlocks"/> from the current value, and <see cref="Resize"/> only updates <see cref="SparseHeader.TotalBlocks"/>.
    /// <para>可自由替换；写入器使用当前值中的 <see cref="SparseHeader.BlockSize"/>、<see cref="SparseHeader.ChunkHeaderSize"/> 与 <see cref="SparseHeader.TotalBlocks"/>，<see cref="Resize"/> 仅更新 <see cref="SparseHeader.TotalBlocks"/>。</para></value>
    public SparseHeader Header { get; set; }

    /// <summary>The chunk records currently in the image, ordered by ascending start block.
    /// <para>当前镜像中的数据块记录，按起始块升序排列。</para></summary>
    /// <value>A live read-only view of the internal list; additions via the <c>Add*</c> methods are inserted in order, and it is empty for a newly created file. Readers cast this back to <c>List&lt;SparseChunk&gt;</c> to append parsed chunks.
    /// <para>内部列表的实时只读视图；通过 <c>Add*</c> 方法新增的项会按序插入，新建文件时为空。读取器会将其转回 <c>List&lt;SparseChunk&gt;</c> 以追加解析出的块。</para></value>
    public IReadOnlyList<SparseChunk> Chunks => _chunks;

    /// <summary>The first block index not yet covered by the last registered chunk.
    /// <para>最后一个已注册块未覆盖到的第一个块索引。</para></summary>
    /// <value>Start block plus chunk size of the final chunk, or <c>0</c> when <see cref="Chunks"/> is empty; used as the implicit append position by the <c>Add*</c> methods.
    /// <para>末个块的起始块加块大小；<see cref="Chunks"/> 为空时为 <c>0</c>。供 <c>Add*</c> 方法作为隐式追加位置使用。</para></value>
    public uint CurrentBlock
    {
        get
        {
            if (_chunks.Count == 0)
            {
                return 0;
            }
            var last = _chunks[^1];
            return last.StartBlock + last.Header.ChunkSize;
        }
    }

    /// <summary>
    /// Adds a raw data chunk.
    /// <para>添加一个原始数据块。</para>
    /// </summary>
    /// <param name="data">Literal bytes for the chunk; the in-memory payload is not copied and not size-bounded here.
    /// <para>块的原始字节；内存载荷不会被复制，此处也不限制大小。</para></param>
    /// <param name="blockIndex">Start block of the chunk, or <see langword="null"/> to append at <see cref="CurrentBlock"/>. An explicit index may overlap or precede existing chunks; writers insert don't-care gaps as needed.
    /// <para>块的起始块；为 <see langword="null"/> 时追加在 <see cref="CurrentBlock"/>。显式索引可能与已有块重叠或靠前；写入器会按需插入 don't-care 空隙。</para></param>
    public void AddRawChunk(byte[] data, uint? blockIndex = null)
    {
        var sizeInBlocks = (uint)((data.Length + Header.BlockSize - 1) / Header.BlockSize);
        var startBlock = blockIndex ?? CurrentBlock;
        var header = new ChunkHeader
        {
            ChunkType = (ushort)ChunkType.Raw,
            ChunkSize = sizeInBlocks,
            TotalSize = (uint)(SparseFormat.ChunkHeaderSize + data.Length)
        };
        AddChunkSorted(new SparseChunk(header) { StartBlock = startBlock, DataProvider = new MemoryDataProvider(data) });
    }

    /// <summary>
    /// Adds a raw chunk backed by a data provider.
    /// <para>添加由数据提供器支撑的原始数据块。</para>
    /// </summary>
    /// <param name="provider">Payload source; the chunk takes ownership and disposes it with the file. Its <c>Length</c> is recorded into the header's total size without an upper-bound check.
    /// <para>载荷来源；块取得所有权，并随文件一同释放。其 <c>Length</c> 会被记入头部总大小，且不做上限检查。</para></param>
    /// <param name="sizeInBlocks">Number of logical blocks the chunk covers; not cross-checked against <paramref name="provider"/> length.
    /// <para>块覆盖的逻辑块数；不会与 <paramref name="provider"/> 的长度交叉校验。</para></param>
    /// <param name="blockIndex">Start block of the chunk, or <see langword="null"/> to append at <see cref="CurrentBlock"/>.
    /// <para>块的起始块；为 <see langword="null"/> 时追加在 <see cref="CurrentBlock"/>。</para></param>
    public void AddRawChunk(ISparseDataProvider provider, uint sizeInBlocks, uint? blockIndex = null)
    {
        var startBlock = blockIndex ?? CurrentBlock;
        var header = new ChunkHeader
        {
            ChunkType = (ushort)ChunkType.Raw,
            ChunkSize = sizeInBlocks,
            TotalSize = (uint)(SparseFormat.ChunkHeaderSize + provider.Length)
        };
        AddChunkSorted(new SparseChunk(header) { StartBlock = startBlock, DataProvider = provider });
    }

    /// <summary>
    /// Adds a fill chunk (4-byte pattern repeated across blocks).
    /// <para>添加一个填充数据块(4 字节模式跨块重复)。</para>
    /// </summary>
    /// <param name="fillValue">Pattern written little-endian and repeated across the chunk's expanded bytes.
    /// <para>以小端写出并在块展开字节中重复的模式。</para></param>
    /// <param name="sizeInBytes">Logical byte size; rounded up to whole blocks.
    /// <para>逻辑字节大小；向上取整到整块。</para></param>
    /// <param name="blockIndex">Start block of the chunk, or <see langword="null"/> to append at <see cref="CurrentBlock"/>.
    /// <para>块的起始块；为 <see langword="null"/> 时追加在 <see cref="CurrentBlock"/>。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeInBytes"/> is negative.
    /// <para><paramref name="sizeInBytes"/> 为负。</para></exception>
    /// <exception cref="OverflowException">The rounded-up block count exceeds <see cref="uint.MaxValue"/>.
    /// <para>向上取整后的块数超过 <see cref="uint.MaxValue"/>。</para></exception>
    public void AddFillChunk(uint fillValue, long sizeInBytes, uint? blockIndex = null)
    {
        var sizeInBlocks = DivideRoundUpToUInt32(sizeInBytes, Header.BlockSize);
        var startBlock = blockIndex ?? CurrentBlock;
        var header = new ChunkHeader
        {
            ChunkType = (ushort)ChunkType.Fill,
            ChunkSize = sizeInBlocks,
            TotalSize = SparseFormat.ChunkHeaderSize + 4
        };
        AddChunkSorted(new SparseChunk(header) { StartBlock = startBlock, FillValue = fillValue });
    }

    /// <summary>
    /// Adds a don't-care chunk (skipped blocks).
    /// <para>添加一个不关心数据块(跳过的块)。</para>
    /// </summary>
    /// <param name="sizeInBytes">Logical byte size; rounded up to whole blocks. Sparse output stores no payload, raw output writes zeros.
    /// <para>逻辑字节大小；向上取整到整块。稀疏输出不存储载荷，原始输出写入零。</para></param>
    /// <param name="blockIndex">Start block of the chunk, or <see langword="null"/> to append at <see cref="CurrentBlock"/>.
    /// <para>块的起始块；为 <see langword="null"/> 时追加在 <see cref="CurrentBlock"/>。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeInBytes"/> is negative.
    /// <para><paramref name="sizeInBytes"/> 为负。</para></exception>
    /// <exception cref="OverflowException">The rounded-up block count exceeds <see cref="uint.MaxValue"/>.
    /// <para>向上取整后的块数超过 <see cref="uint.MaxValue"/>。</para></exception>
    public void AddDontCareChunk(long sizeInBytes, uint? blockIndex = null)
    {
        var sizeInBlocks = DivideRoundUpToUInt32(sizeInBytes, Header.BlockSize);
        var startBlock = blockIndex ?? CurrentBlock;
        var header = new ChunkHeader
        {
            ChunkType = (ushort)ChunkType.DontCare,
            ChunkSize = sizeInBlocks,
            TotalSize = SparseFormat.ChunkHeaderSize
        };
        AddChunkSorted(new SparseChunk(header) { StartBlock = startBlock });
    }

    /// <summary>
    /// Resizes the logical image size.
    /// <para>调整逻辑镜像大小。</para>
    /// </summary>
    /// <param name="newSize">New logical size in bytes; rounded up to whole blocks and stored as the header's total block count. Existing chunks are left untouched, so they may extend past the new size.
    /// <para>新的逻辑大小（字节）；向上取整到整块并存为头部的总块数。已有块不受影响，可能超出新大小。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="newSize"/> is negative.
    /// <para><paramref name="newSize"/> 为负。</para></exception>
    /// <exception cref="OverflowException">The rounded-up block count exceeds <see cref="uint.MaxValue"/>.
    /// <para>向上取整后的块数超过 <see cref="uint.MaxValue"/>。</para></exception>
    public void Resize(long newSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newSize);
        var newTotalBlocks = DivideRoundUpToUInt32(newSize, Header.BlockSize);
        Header = Header with { TotalBlocks = newTotalBlocks };
    }

    /// <summary>
    /// Writes the sparse image (or raw data) to the stream.
    /// <para>将稀疏镜像(或原始数据)写入流。</para>
    /// </summary>
    /// <param name="stream">Caller-owned writable destination; it is not flushed or closed here.
    /// <para>调用方持有的可写目标；此处不刷新也不关闭。</para></param>
    /// <param name="sparse">When <see langword="true"/>, emits the sparse container via <see cref="SparseWriter.WriteToStream"/>; otherwise expands every chunk as if flashed.
    /// <para>为 <see langword="true"/> 时经 <see cref="SparseWriter.WriteToStream"/> 输出稀疏容器；否则按刷写效果展开每个块。</para></param>
    /// <param name="includeCrc">Appends a CRC32 chunk when <paramref name="sparse"/> is <see langword="true"/>; ignored for raw output.
    /// <para>当 <paramref name="sparse"/> 为 <see langword="true"/> 时追加 CRC32 块；原始输出时忽略。</para></param>
    public void WriteToStream(Stream stream, bool sparse = true, bool includeCrc = false)
    {
        if (sparse)
        {
            SparseWriter.WriteToStream(this, stream, includeCrc);
        }
        else
        {
            WriteRawToStream(stream);
        }
    }

    /// <summary>
    /// Writes the raw (unsparsified) image to the stream.
    /// <para>将原始(非稀疏)镜像写入流。</para>
    /// </summary>
    /// <param name="stream">Caller-owned writable destination; it is not flushed or closed here. Every chunk is expanded in list order and written back-to-back — RAW payloads copied, FILL patterns repeated, don't-care and unknown types zeroed — then the output is padded with zeros up to total blocks times block size. Gaps between chunk start blocks receive no bytes at their position, so content after a gap is shifted earlier in the output.
    /// <para>调用方持有的可写目标；此处不刷新也不关闭。按列表顺序逐块展开并连续写入——复制 RAW 载荷、重复 FILL 模式、将 don't-care 与未知类型置零——随后用零补齐到总块数乘以块大小。块起始索引之间的空隙在对应位置不写入任何字节，因此空隙之后的内容在输出中前移。</para></param>
    public void WriteRawToStream(Stream stream)
    {
        var blockSize = Header.BlockSize;
        var zeros = new byte[64 * 1024];
        byte[]? fillBuffer = null;
        var written = 0L;
        var logicalLength = (long)Header.TotalBlocks * blockSize;

        foreach (var chunk in _chunks)
        {
            switch ((ChunkType)chunk.Header.ChunkType)
            {
                case ChunkType.Raw when chunk.DataProvider != null:
                    chunk.DataProvider.WriteTo(stream);
                    written += (long)chunk.Header.ChunkSize * blockSize;
                    break;
                case ChunkType.Fill:
                    {
                        var total = (long)chunk.Header.ChunkSize * blockSize;
                        fillBuffer ??= new byte[64 * 1024];
                        SparseFill.Fill(fillBuffer, chunk.FillValue);
                        while (total > 0)
                        {
                            var n = (int)Math.Min(fillBuffer.Length, total);
                            stream.Write(fillBuffer, 0, n);
                            total -= n;
                        }
                        written += (long)chunk.Header.ChunkSize * blockSize;
                        break;
                    }

                case ChunkType.Raw:
                    break;
                case ChunkType.DontCare:
                    {


                        var total = (long)chunk.Header.ChunkSize * blockSize;
                        while (total > 0)
                        {
                            var n = (int)Math.Min(zeros.Length, total);
                            stream.Write(zeros, 0, n);
                            total -= n;
                        }
                        written += (long)chunk.Header.ChunkSize * blockSize;
                        break;
                    }
                case ChunkType.Crc32:
                    break;
                default:
                    {


                        var total = (long)chunk.Header.ChunkSize * blockSize;
                        while (total > 0)
                        {
                            var n = (int)Math.Min(zeros.Length, total);
                            stream.Write(zeros, 0, n);
                            total -= n;
                        }
                        written += (long)chunk.Header.ChunkSize * blockSize;
                        break;
                    }
            }
        }


        while (written < logicalLength)
        {
            var n = (int)Math.Min(zeros.Length, logicalLength - written);
            stream.Write(zeros, 0, n);
            written += n;
        }
    }

    private static uint DivideRoundUpToUInt32(long value, uint divisor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfZero(divisor);
        var quotient = value / divisor;
        if (value % divisor != 0) quotient++;
        return checked((uint)quotient);
    }

    private void AddChunkSorted(SparseChunk chunk)
    {
        var index = _chunks.Count;
        while (index > 0 && _chunks[index - 1].StartBlock > chunk.StartBlock)
        {
            index--;
        }
        _chunks.Insert(index, chunk);
    }

    /// <summary>
    /// Disposes all chunk data providers.
    /// <para>释放所有数据块数据提供器。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        List<Exception>? errors = null;
        foreach (var chunk in _chunks)
        {
            try
            {
                chunk.Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }
        _chunks.Clear();
        foreach (var resource in _ownedResources)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }
        _ownedResources.Clear();
        if (errors is { Count: > 0 })
        {
            throw errors.Count == 1 ? errors[0] : new AggregateException(errors);
        }
    }
}