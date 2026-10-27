namespace UotanStudio.Firmware.BlockImage.Sparse;

/// <summary>
/// Selects how <see cref="Models.SparseFile.WriteRawToStream(System.IO.Stream, SparseRawZeroMode)"/> materializes
/// the don't-care, zero-pattern and tail regions of an expanded image.
/// <para>选择 <see cref="Models.SparseFile.WriteRawToStream(System.IO.Stream, SparseRawZeroMode)"/> 如何物化展开镜像的
/// don't-care、零填充模式与尾部区域。</para>
/// </summary>
public enum SparseRawZeroMode
{
    /// <summary>
    /// Writes every zero byte through the stream. Works on any writable stream,
    /// including non-seekable progress wrappers.
    /// <para>把每个零字节都经流写出。适用于任何可写流，包括不可寻址的进度包装流。</para>
    /// </summary>
    Write = 0,

    /// <summary>
    /// Skips zero runs with seeks instead of writing them, leaving the holes for the file system to zero-fill. The mode
    /// is applied only when the destination is an empty seekable <see cref="System.IO.FileStream"/> at position zero and
    /// the chunk list covers the image contiguously from block zero; any other destination or layout falls back to
    /// <see cref="Write"/> byte for byte.
    /// <para>用寻址跳过零段而不是写出它们，由文件系统把空洞补零。仅当目标是位于位置零的空
    /// <see cref="System.IO.FileStream"/> 且块列表从块零开始连续覆盖镜像时才生效；其他目标或布局都会逐字节回退到
    /// <see cref="Write"/>。</para>
    /// </summary>
    Seek = 1
}