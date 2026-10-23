using System.Buffers;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;

namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

/// <summary>
/// Collects the frames a planning worker accepts into one local buffer and hands them to the shared staging
/// area a whole block at a time, so the staging lock and the copy or write behind it happen once per block
/// instead of once per frame. Frames are laid out back to back in the order they were appended, and every
/// plan receives its absolute offset when its block is committed, so the staging layout stays a detail
/// no index or payload byte depends on.
/// <para>把规划工作者接受的帧先收进本地缓冲，再以整块为单位交给共享暂存区，使暂存锁与随后的复制或写入每块
/// 各发生一次而不是每帧一次。帧按追加顺序首尾相接，每个规划项在其所属块提交时取得绝对偏移，因此暂存布局始终是
/// 不进入任何索引或负载字节的实现细节。</para>
/// </summary>
internal sealed class ErofsStagingBatch : IDisposable
{
    private const int ChunkBytes = 1 << 20;

    private readonly Core.CompressionPayloadStaging _staging;
    private readonly List<(ErofsPclusterPlan Plan, int Offset)> _pending = [];
    private byte[]? _buffer;
    private int _length;

    /// <summary>
    /// Creates a batch over the build's shared staging area.
    /// <para>在为本次构建共享的暂存区之上创建批量写入器。</para>
    /// </summary>
    /// <param name="staging">Staging area receiving the buffered frames.
    /// <para>接收已缓冲帧的暂存区。</para></param>
    internal ErofsStagingBatch(Core.CompressionPayloadStaging staging) => _staging = staging;

    /// <summary>
    /// Buffers one accepted frame and records the plan that will read it back.
    /// <para>缓冲一个被接受的帧，并记录将把它读回的规划项。</para>
    /// </summary>
    /// <param name="plan">Plan whose staged offset is filled in once the frame is committed; a plan already
    /// carrying a staged offset is never passed here. <para>帧提交后填入暂存偏移的规划项；此处不会传入已带
    /// 暂存偏移的规划项。</para></param>
    /// <param name="frame">Accepted encoded frame, empty for a plan that stages nothing.
    /// <para>被接受的编码帧；不暂存任何内容的规划项为空。</para></param>
    internal void Append(ErofsPclusterPlan plan, ReadOnlySpan<byte> frame)
    {
        if (frame.Length > ChunkBytes)
        {
            Flush();
            plan.StagedOffset = _staging.Append(frame);
            return;
        }

        _buffer ??= ArrayPool<byte>.Shared.Rent(ChunkBytes);
        if (_length + frame.Length > ChunkBytes)
        {
            Flush();
        }

        frame.CopyTo(_buffer.AsSpan(_length));
        _pending.Add((plan, _length));
        _length += frame.Length;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Flush();
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }

    /// <summary>
    /// Commits the buffered frames in one staging append and resolves every pending plan's offset.
    /// <para>以一次暂存追加提交已缓冲的帧，并解析每个待定规划项的偏移。</para>
    /// </summary>
    private void Flush()
    {
        if (_pending.Count == 0 || _buffer is null)
        {
            return;
        }

        var origin = _staging.Append(_buffer.AsSpan(0, _length));
        foreach (var (plan, offset) in _pending)
        {
            plan.StagedOffset = origin + offset;
        }

        _pending.Clear();
        _length = 0;
    }
}