using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BlockImage.Sparse.Models;

namespace UotanStudio.Firmware.BlockImage.Sparse;

/// <summary>
/// Disposable logical view over an expanded sparse container: it owns the <see cref="SparseReadStream"/> and the
/// <see cref="SparseFile"/> behind it, so disposing the view releases both, and it never disposes an underlying
/// container stream supplied by the caller.
/// <para>已展开稀疏容器的可释放逻辑视图：持有 <see cref="SparseReadStream"/> 与其后的 <see cref="SparseFile"/>，
/// 因此释放视图会同时释放两者，且绝不释放由调用方提供的底层容器流。</para>
/// </summary>
sealed class SparseLogicalView(SparseFile model, SparseReadStream logicalStream) : Core.Bridges.ISparseLogicalView
{
    /// <summary>
    /// Gets the seekable logical byte stream presented to the consuming context.
    /// <para>获取向消费上下文呈现的可寻址逻辑字节流。</para>
    /// </summary>
    /// <value>The expanded logical stream, valid until this view is disposed.
    /// <para>展开后的逻辑流，在视图释放前有效。</para></value>
    public Stream Logical => logicalStream;

    /// <summary>
    /// Gets the block size declared by the container header, which the consumer records to reproduce the physical layout.
    /// <para>获取容器头声明的块大小；消费者记录它以复现物理布局。</para>
    /// </summary>
    /// <value>The container block size in bytes. <para>容器块大小（字节）。</para></value>
    public uint BlockSize => model.Header.BlockSize;

    /// <summary>
    /// Releases the logical stream, which in turn releases the sparse model it owns.
    /// <para>释放逻辑流，由其连带释放所持有的稀疏模型。</para>
    /// </summary>
    public void Dispose() => logicalStream.Dispose();
}