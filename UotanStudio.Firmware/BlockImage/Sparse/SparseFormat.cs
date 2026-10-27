using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.BlockImage.Sparse;

/// <summary>
/// Format-wide constants shared by the sparse image reader, writer and model types.
/// <para>供稀疏镜像读取器、写入器与模型类型共用的格式级常量。</para>
/// </summary>
public static class SparseFormat
{
    /// <summary>Little-endian magic value that identifies an Android sparse image file header. <para>标识 Android 稀疏镜像文件头部的小端魔数。</para></summary>
    public const uint SparseHeaderMagic = BlockImageMagic.SparseHeaderMagic;

    /// <summary>Size in bytes of the base sparse file header before any extended header padding. <para>扩展头部填充之前，稀疏文件基础头部的字节大小。</para></summary>
    public const ushort SparseHeaderSize = 28;

    /// <summary>Size in bytes of the base chunk header before any extended chunk-header padding. <para>扩展数据块头部填充之前，数据块基础头部的字节大小。</para></summary>
    public const ushort ChunkHeaderSize = 12;

    /// <summary>Reference ceiling of 64 MiB for a single chunk's payload size declared by the format constants. <para>格式常量声明的单个数据块载荷大小参考上限，为 64 MiB。</para></summary>
    public const uint MaxChunkDataSize = 64 * 1024 * 1024;
}