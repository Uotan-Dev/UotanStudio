using System.Buffers;
using System.Runtime.ExceptionServices;

namespace UotanStudio.Firmware.FileSystem.Erofs.Parser;

/// <summary>
/// EROFS parser part that owns the bounded pcluster decode pipeline: a ring of reusable slots with one
/// long-lived decode worker per slot, and the shared lane budget that nests the pipeline under the parallel
/// extraction workers instead of on top of them.
/// <para>EROFS 解析器的组成部分，提供有界 pcluster 解码流水线：环形的可复用槽位、每个槽位一个长期工作者，
/// 以及把流水线嵌套在并行解包工作者之下而不是叠加在其上的共享通道预算。</para>
/// </summary>
public sealed partial class ErofsParser
{
    /// <summary>
    /// Smallest compressed inode worth running the pipeline for. At or below this size the fixed cost of a
    /// worker set exceeds the decode work it can overlap, so the sequential walk writes the pclusters alone.
    /// <para>值得为流水线启用压缩 inode 的最小大小。不大于该值时，工作者集合的固定开销超过其可重叠的解码工作量，
    /// 因此由顺序遍历独自写出 pcluster。</para>
    /// </summary>
    private const int PipelinedDecodeMinInodeBytes = 64 * 1024;

    /// <summary>
    /// Global decode lane budget: every inode writer holds one lane for itself and may claim spare lanes for its
    /// decode workers, so concurrent inodes share the worker budget instead of each starting a full worker set.
    /// <para>全局解码通道预算：每个 inode 写出者自持一个通道，并可为其解码工作者申领空闲通道，使并发 inode
    /// 共享工作者预算，而不是各自启动一整套工作者。</para>
    /// </summary>
    private static class DecodeLaneBudget
    {
        private static int _claimedLanes;

        /// <summary>Reserves the lane the calling inode writer occupies while it prefetches and writes.
        /// <para>为调用方 inode 写出者预留其在预取与写出期间占用的通道。</para>
        /// </summary>
        internal static void EnterFile() => Interlocked.Increment(ref _claimedLanes);

        /// <summary>Releases the lane reserved by <see cref="EnterFile"/>.
        /// <para>释放由 <see cref="EnterFile"/> 预留的通道。</para>
        /// </summary>
        internal static void ExitFile() => Interlocked.Decrement(ref _claimedLanes);

        /// <summary>
        /// Claims decode workers for one inode out of the lanes no other writer holds, capped by the inode's own
        /// frame count. The claim is best effort: a writer that finds no spare lane keeps decoding sequentially.
        /// <para>从其他写出者未占用的通道中为单个 inode 申领解码工作者，并按该 inode 的帧数封顶。申领是尽力而为的：
        /// 找不到空闲通道的写出者继续顺序解码。</para>
        /// </summary>
        /// <param name="requested">Workers the memory budget would allow. <para>内存预算允许的工作者数量。</para></param>
        /// <param name="frameCount">Frames the inode can decode in parallel. <para>该 inode 可并行解码的帧数。</para></param>
        /// <returns>The claimed worker count, zero when no lane is spare. <para>申领到的工作者数量；没有空闲通道时为零。</para></returns>
        internal static int ClaimWorkers(int requested, int frameCount)
        {
            var wanted = Math.Min(requested, Math.Max(0, frameCount - 1));
            while (wanted > 0)
            {
                var claimed = Volatile.Read(ref _claimedLanes);
                var available = Core.FirmwareParallelism.Workers - claimed;
                if (claimed == 0)
                {
                    available--;
                }

                var take = Math.Min(wanted, available);
                if (take <= 0)
                {
                    return 0;
                }

                if (Interlocked.CompareExchange(ref _claimedLanes, claimed + take, claimed) == claimed)
                {
                    return take;
                }
            }

            return 0;
        }

        /// <summary>Returns decode workers claimed by <see cref="ClaimWorkers"/>.
        /// <para>归还由 <see cref="ClaimWorkers"/> 申领的解码工作者。</para>
        /// </summary>
        /// <param name="workers">Claimed worker count; non-positive values are ignored.
        /// <para>已申领的工作者数量；非正值被忽略。</para></param>
        internal static void ReleaseWorkers(int workers)
        {
            if (workers > 0)
            {
                Interlocked.Add(ref _claimedLanes, -workers);
            }
        }
    }

    /// <summary>
    /// Bounded decode pipeline for one compressed inode. The calling thread walks the lcluster index and stages
    /// each encoded frame into the next ring slot, one long-lived worker per slot decodes the frame staged
    /// there while the following frames are read, and the calling thread writes every decoded frame in logical
    /// order.
    /// <para>单个压缩 inode 的有界解码流水线。调用线程遍历 lcluster 索引并把每个编码帧暂存到下一个环形槽位，
    /// 每个槽位各有一个长期工作者在后续帧被读取时解码已暂存的帧，调用线程随后按逻辑顺序写出每个已解码帧。</para>
    /// </summary>
    private sealed class CompressedDecodePipeline : IDisposable
    {
        private readonly ErofsParser _parser;
        private readonly ulong _nid;
        private readonly DecodeSlot[] _slots;
        private readonly Task[] _workers;
        private readonly SemaphoreSlim _filled = new(0);
        private int _produced;
        private int _nextFrame = -1;
        private int _stop;
        private bool _disposed;

        /// <summary>Creates the slots and starts one decode worker per slot.
        /// <para>创建槽位并为每个槽位启动一个解码工作者。</para>
        /// </summary>
        /// <param name="parser">Parser owning the decoder state the workers borrow.
        /// <para>工作者借用其解码器状态的解析器。</para></param>
        /// <param name="nid">Inode whose frames the workers decode. <para>工作者解码其帧的 inode。</para></param>
        /// <param name="workers">Number of slots and decode workers; at least <c>2</c>.
        /// <para>槽位与解码工作者的数量；至少为 <c>2</c>。</para></param>
        internal CompressedDecodePipeline(ErofsParser parser, ulong nid, int workers)
        {
            _parser = parser;
            _nid = nid;
            _slots = new DecodeSlot[workers];
            _workers = new Task[workers];
            for (var slot = 0; slot < workers; slot++)
            {
                _slots[slot] = new DecodeSlot();
            }

            try
            {
                for (var worker = 0; worker < workers; worker++)
                {
                    _workers[worker] = Task.Run(DecodeLoop);
                }
            }
            catch
            {
                Interlocked.Exchange(ref _stop, 1);
                _filled.Release(workers);
                throw;
            }
        }

        /// <summary>Slot carrying the frame at <paramref name="index"/>.
        /// <para>承载第 <paramref name="index"/> 帧的槽位。</para>
        /// </summary>
        /// <param name="index">Zero-based logical frame index. <para>从零开始的逻辑帧索引。</para></param>
        /// <returns>The slot the frame occupies. <para>该帧占用的槽位。</para></returns>
        internal DecodeSlot SlotAt(int index) => _slots[index % _slots.Length];

        /// <summary>Publishes the frame staged in the newest slot to a decode worker.
        /// <para>把最新槽位中暂存的帧发布给一个解码工作者。</para>
        /// </summary>
        /// <param name="count">Number of frames published so far, including this one.
        /// <para>迄今已发布的帧数，含本次。</para></param>
        internal void Publish(int count)
        {
            Volatile.Write(ref _produced, count);
            _filled.Release();
        }

        /// <summary>Wakes every worker and waits until all of them stopped.
        /// <para>唤醒全部工作者并等待它们全部停止。</para>
        /// </summary>
        internal void Stop()
        {
            if (Interlocked.Exchange(ref _stop, 1) != 0)
            {
                return;
            }

            _filled.Release(_workers.Length);
            for (var worker = 0; worker < _workers.Length; worker++)
            {
                try
                {
                    _workers[worker].Wait();
                }
                catch (AggregateException)
                {
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            _filled.Dispose();
            foreach (var slot in _slots)
            {
                slot.Dispose();
            }
        }

        private void DecodeLoop()
        {
            while (true)
            {
                _filled.Wait();
                if (Volatile.Read(ref _stop) != 0)
                {
                    return;
                }

                var index = Interlocked.Increment(ref _nextFrame);
                if (index >= Volatile.Read(ref _produced))
                {
                    return;
                }

                var slot = _slots[index % _slots.Length];
                try
                {
                    _parser.DecodeEncodedPcluster(_nid, slot.Frame);
                    slot.Failure = null;
                }
                catch (Exception ex)
                {
                    slot.Failure = ExceptionDispatchInfo.Capture(ex);
                }

                slot.Ready.Set();
            }
        }
    }

    /// <summary>
    /// One ring slot of the decode pipeline: the pooled staging buffers for the frame it currently carries, the
    /// failure recorded at that frame's logical position, and the signal that wakes the ordered writer once the
    /// decode finished.
    /// <para>解码流水线的一个环形槽位：当前帧的池化暂存缓冲、按该帧逻辑位置记录的失败，以及解码完成后唤醒顺序
    /// 写出器的信号。</para>
    /// </summary>
    private sealed class DecodeSlot : IDisposable
    {
        /// <summary>Pooled encoded-input buffer, grown in place while the frame is staged.
        /// <para>池化的编码输入缓冲，在帧暂存期间按需就地扩容。</para>
        /// </summary>
        internal byte[] Compressed = [];

        /// <summary>Pooled decoded-output buffer, grown in place while the frame is staged.
        /// <para>池化的解码输出缓冲，在帧暂存期间按需就地扩容。</para>
        /// </summary>
        internal byte[] Decoded = [];

        /// <summary>Frame staged in this slot, valid from its publication until the ordered write.
        /// <para>本槽位暂存的帧，自发布起至顺序写出为止有效。</para>
        /// </summary>
        internal EncodedPcluster Frame;

        /// <summary>Failure recorded for this slot's decode, or <see langword="null"/> when it decoded.
        /// <para>本槽位解码记录的失败；解码成功时为 <see langword="null"/>。</para>
        /// </summary>
        internal ExceptionDispatchInfo? Failure;

        /// <summary>Set once the staged frame is decoded; the ordered writer waits on it.
        /// <para>暂存帧解码完成后置位；顺序写出器等待该信号。</para>
        /// </summary>
        internal ManualResetEventSlim Ready { get; } = new(false);

        /// <summary>Clears the completion signal before the slot carries another frame.
        /// <para>在槽位承载另一帧前清除完成信号。</para>
        /// </summary>
        internal void Prepare() => Ready.Reset();

        /// <inheritdoc/>
        public void Dispose()
        {
            Ready.Dispose();
            if (Compressed.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(Compressed);
                Compressed = [];
            }

            if (Decoded.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(Decoded);
                Decoded = [];
            }
        }
    }
}