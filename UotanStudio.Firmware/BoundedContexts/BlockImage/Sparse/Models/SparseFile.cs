using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.DataProviders;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;

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
    public SparseFile(uint blockSize, long totalSize)
    {
        ArgumentOutOfRangeException.ThrowIfZero(blockSize);
        ArgumentOutOfRangeException.ThrowIfNegative(totalSize);
        var totalBlocks = DivideRoundUpToUInt32(totalSize, blockSize);
        Header = SparseHeader.CreateDefault(blockSize, totalBlocks);
    }

    public SparseHeader Header { get; set; }

    public IReadOnlyList<SparseChunk> Chunks => _chunks;

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