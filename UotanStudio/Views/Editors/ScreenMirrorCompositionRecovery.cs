namespace UotanStudio.Views.Editors;

/// <summary>
/// Tracks the composition surface lifecycle: generation tokens that invalidate in-flight probes and
/// presentations, and whether a failed surface may be built again.
/// <para>跟踪合成表面生命周期：使在途探测与呈现失效的代际令牌，以及失败的表面是否允许重新构建。</para>
/// </summary>
/// <remarks>
/// A dock float, a tab switch or a rebuilt view detaches the visual while the session keeps its surface, so
/// every probe and every queued presentation must prove that it still owns the current generation before it
/// touches the surface. A presentation failure caused by a transient host change clears on the next attach;
/// only a compositor capability failure is terminal for the session.
/// <para>停靠浮动、标签切换或视图重建会分离可视对象，而会话保留其表面，因此每次探测与每次排队呈现都必须在操作
/// 表面前证明自己仍持有当前代际。宿主瞬态变化引起的呈现失败会在下次挂载时清除；只有合成器能力失败才对
/// 本会话终止。</para>
/// </remarks>
internal sealed class ScreenMirrorCompositionRecovery
{
    private readonly Lock _gate = new();
    private int _generation;
    private bool _failed;
    private bool _terminal;

    /// <summary>
    /// Gets the current generation; a probe or presentation must match it before touching the surface.
    /// <para>获取当前代际；探测或呈现只有匹配后才能操作表面。</para>
    /// </summary>
    /// <value>
    /// The generation installed by the newest attach or detach.
    /// <para>由最近一次挂载或分离写入的代际。</para>
    /// </value>
    internal int Generation
    {
        get
        {
            lock (_gate) return _generation;
        }
    }

    /// <summary>
    /// Gets whether the current surface cannot present; consumers fall back to bitmap output.
    /// <para>获取当前表面是否已无法呈现；消费方会回退到位图输出。</para>
    /// </summary>
    /// <value>
    /// True from a recorded failure until the next attach succeeds.
    /// <para>从记录失败开始为真，直到下一次挂载成功。</para>
    /// </value>
    internal bool Failed
    {
        get
        {
            lock (_gate) return _failed;
        }
    }

    /// <summary>
    /// Gets whether the compositor can never provide a usable surface in this session.
    /// <para>获取合成器在本会话中是否永远无法提供可用表面。</para>
    /// </summary>
    /// <value>
    /// True after a terminal failure; attach attempts are then refused.
    /// <para>终止性失败后为真；此后挂载尝试会被拒绝。</para>
    /// </value>
    internal bool IsTerminal
    {
        get
        {
            lock (_gate) return _terminal;
        }
    }

    /// <summary>
    /// Starts a probe and returns its generation, or 0 when probing is not allowed.
    /// <para>开始一次探测并返回其代际；不允许探测时返回 0。</para>
    /// </summary>
    /// <returns>
    /// The generation the probe must match, or 0 for a terminal compositor.
    /// <para>探测必须匹配的代际；合成器终止性失败时为 0。</para>
    /// </returns>
    internal int BeginAttach()
    {
        lock (_gate)
        {
            if (_terminal) return 0;
            _failed = false;
            return ++_generation;
        }
    }

    /// <summary>
    /// Returns whether a probe or presentation still owns the current generation.
    /// <para>返回探测或呈现是否仍持有当前代际。</para>
    /// </summary>
    /// <param name="generation">Generation captured before the asynchronous step.
    /// <para>异步步骤之前捕获的代际。</para></param>
    /// <returns>
    /// True when no detach or newer attach happened since <paramref name="generation"/> was returned.
    /// <para>自 <paramref name="generation"/> 返回以来没有分离或更新的挂载时为真。</para>
    /// </returns>
    internal bool IsCurrent(int generation)
    {
        lock (_gate) return generation != 0 && generation == _generation;
    }

    /// <summary>
    /// Invalidates the installed surface and returns the new generation.
    /// <para>使已安装表面失效，并返回新的代际。</para>
    /// </summary>
    /// <returns>
    /// The generation after the detach.
    /// <para>分离后的代际。</para>
    /// </returns>
    internal int BeginDetach()
    {
        lock (_gate)
        {
            return ++_generation;
        }
    }

    /// <summary>
    /// Records a successful attach and clears the failure state.
    /// <para>记录一次成功挂载并清除失败状态。</para>
    /// </summary>
    internal void RecordAttached()
    {
        lock (_gate) _failed = false;
    }

    /// <summary>
    /// Records a failed attach or presentation and reports whether it is the first failure since the last
    /// successful attach.
    /// <para>记录一次失败的挂载或呈现，并报告它是否是自上次成功挂载以来的首次失败。</para>
    /// </summary>
    /// <param name="terminal">Whether the compositor cannot provide a surface this session.
    /// <para>合成器本会话是否已不可能提供表面。</para></param>
    /// <returns>
    /// True for the first failure of the current episode; callers log and notify only once.
    /// <para>当前失败过程的首次失败为真；调用方只记录与通知一次。</para>
    /// </returns>
    internal bool RecordFailure(bool terminal)
    {
        lock (_gate)
        {
            var first = !_failed;
            _failed = true;
            if (terminal) _terminal = true;
            return first;
        }
    }
}