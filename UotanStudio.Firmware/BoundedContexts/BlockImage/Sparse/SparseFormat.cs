namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;

public static class SparseFormat
{
    public const uint SparseHeaderMagic = 0xed26ff3a;

    public const ushort SparseHeaderSize = 28;

    public const ushort ChunkHeaderSize = 12;

    public const uint MaxChunkDataSize = 64 * 1024 * 1024;
}