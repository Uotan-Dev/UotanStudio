namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Represents a sparse image file: header + ordered chunk list.
/// <para>表示一个稀疏镜像文件:头部 + 有序数据块列表。</para>
/// </summary>
public sealed class SparseFile : IDisposable
{
    private readonly List<SparseChunk> _chunks = [];

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
    public SparseFile(uint blockSize, long totalSize)
    {
        var totalBlocks = (uint)((totalSize + blockSize - 1) / blockSize);
        Header = SparseHeader.CreateDefault(blockSize, totalBlocks);
    }

    /// <summary>
    /// Gets or sets the sparse header.
    /// <para>获取或设置稀疏文件头部。</para>
    /// </summary>
    public SparseHeader Header { get; set; }

    /// <summary>
    /// Gets the ordered chunk list.
    /// <para>获取有序数据块列表。</para>
    /// </summary>
    public IReadOnlyList<SparseChunk> Chunks => _chunks;

    /// <summary>
    /// Gets the total number of logical blocks covered so far.
    /// <para>获取当前累计覆盖的逻辑块数。</para>
    /// </summary>
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
    public void AddFillChunk(uint fillValue, long sizeInBytes, uint? blockIndex = null)
    {
        var sizeInBlocks = (uint)((sizeInBytes + Header.BlockSize - 1) / Header.BlockSize);
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
    public void AddDontCareChunk(long sizeInBytes, uint? blockIndex = null)
    {
        var sizeInBlocks = (uint)((sizeInBytes + Header.BlockSize - 1) / Header.BlockSize);
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
    public void Resize(long newSize)
    {
        var newTotalBlocks = (uint)((newSize + Header.BlockSize - 1) / Header.BlockSize);
        Header = Header with { TotalBlocks = newTotalBlocks };
    }

    /// <summary>
    /// Writes the sparse image (or raw data) to the stream.
    /// <para>将稀疏镜像(或原始数据)写入流。</para>
    /// </summary>
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
    public void WriteRawToStream(Stream stream)
    {
        var blockSize = Header.BlockSize;
        var zeros = new byte[blockSize];
        foreach (var chunk in _chunks)
        {
            switch ((ChunkType)chunk.Header.ChunkType)
            {
                case ChunkType.Raw when chunk.DataProvider != null:
                    chunk.DataProvider.WriteTo(stream);
                    break;
                case ChunkType.Fill:
                    {
                        var fill = BitConverter.GetBytes(chunk.FillValue);
                        var total = (long)chunk.Header.ChunkSize * blockSize;
                        while (total > 0)
                        {
                            var n = (int)Math.Min(blockSize, total);
                            for (var i = 0; i < n; i += 4)
                            {
                                var copy = Math.Min(4, n - i);
                                stream.Write(fill, 0, copy);
                            }
                            total -= n;
                        }
                        break;
                    }

                case ChunkType.Raw:
                    break;
                case ChunkType.DontCare:
                    break;
                case ChunkType.Crc32:
                    break;
                default:
                    {
                        // DontCare: zero-fill so the output image has full length.
                        var total = (long)chunk.Header.ChunkSize * blockSize;
                        while (total > 0)
                        {
                            var n = (int)Math.Min(blockSize, total);
                            stream.Write(zeros, 0, n);
                            total -= n;
                        }
                        break;
                    }
            }
        }
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
        foreach (var chunk in _chunks)
        {
            chunk.Dispose();
        }
        _chunks.Clear();
    }
}