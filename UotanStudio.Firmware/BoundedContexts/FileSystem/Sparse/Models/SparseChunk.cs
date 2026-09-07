namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;

public class SparseChunk(ChunkHeader header) : IDisposable
{

    public uint StartBlock { get; set; } = 0;

    public ChunkHeader Header { get; init; } = header;

    public ISparseDataProvider? DataProvider { get; set; }

    public uint FillValue { get; set; }

    /// <summary>
    /// Releases all resources used by the SparseChunk, including its data provider.
    /// <para>释放 SparseChunk 使用的所有资源，包括其数据提供程序。</para>
    /// </summary>
    public void Dispose() => DataProvider?.Dispose();
}
