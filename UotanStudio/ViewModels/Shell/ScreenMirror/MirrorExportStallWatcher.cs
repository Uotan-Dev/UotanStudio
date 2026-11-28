namespace UotanStudio.ViewModels.Shell.ScreenMirror;

/// <summary>
/// Detects a stuck GPU-export presentation: video packets keep arriving while the composition presenter's
/// completed-frame counter stays frozen, which strands every export slot behind its keyed mutex and leaves the
/// picture black with the coverage flag still asserting that composition owns the video area. One-shot per
/// instance: after a trigger the session must switch its decode output away from GPU export.
/// <para>检测卡死的 GPU 导出呈现：视频包持续到达而合成呈现器的完成帧计数保持冻结，此时每个导出槽位都被键互锁
/// 扣住，画面保持黑屏，而覆盖标志仍声称合成器持有视频区域。每个实例只触发一次：触发后会话必须把解码输出
/// 切离 GPU 导出。</para>
/// </summary>
internal sealed class MirrorExportStallWatcher
{
    /// <summary>Frozen-composition window a stall must persist before it triggers.
    /// <para>停滞必须持续满该窗口才会触发。</para></summary>
    internal const long StallMilliseconds = 3000;

    private long _baselineTicks;
    private long _baselineComposited;
    private bool _triggered;

    /// <summary>
    /// Feeds one arrived video packet. Returns <see langword="true"/> exactly once when packets have arrived for
    /// <see cref="StallMilliseconds"/> while composition coverage stayed asserted and the composited-frame counter
    /// never advanced. Dropping coverage or observing progress re-arms the window instead of triggering.
    /// <para>喂数据包。当数据包在合成覆盖保持有效且完成帧计数从未前进的状态下持续到达满
    /// <see cref="StallMilliseconds"/> 时，恰好返回一次 <see langword="true"/>。覆盖被撤销或观察到进展都会
    /// 重置观察窗口而不是触发。</para>
    /// </summary>
    /// <param name="nowTicks">Monotonic environment tick count of this packet.
    /// <para>本数据包的单调环境计时。</para></param>
    /// <param name="compositedFrames">The presenter's completed composition-frame counter.
    /// <para>呈现器的已完成合成帧计数。</para></param>
    /// <param name="compositionCovering">Whether composition presentation currently claims the video area.
    /// <para>合成呈现当前是否声称持有视频区域。</para></param>
    internal bool OnPacketArrived(long nowTicks, long compositedFrames, bool compositionCovering)
    {
        if (_triggered || !compositionCovering)
        {
            _baselineTicks = 0;
            return false;
        }

        if (_baselineTicks == 0 || compositedFrames != _baselineComposited)
        {
            _baselineTicks = nowTicks;
            _baselineComposited = compositedFrames;
            return false;
        }

        if (nowTicks - _baselineTicks < StallMilliseconds)
        {
            return false;
        }

        _triggered = true;
        return true;
    }
}