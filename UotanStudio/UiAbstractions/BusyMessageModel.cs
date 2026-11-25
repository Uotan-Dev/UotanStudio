using System.Collections.ObjectModel;
using System.Globalization;
using UotanStudio.Services;

namespace UotanStudio.UiAbstractions;

/// <summary>
/// Payload rendered under a busy spinner, split into two independent wrapping layers: primary
/// groups (batch position and stage text) and metric groups (measured speed and estimated time
/// left), so rapid numeric updates in the metric line never push the primary line into wrapping.
/// <para>忙碌转圈下方呈现的负载，拆分为两层独立的换行分组：主状态分组（批次序号与阶段文本）与
/// 遥测分组（测得速度与预计剩余时间），避免遥测行数字波动把主状态行顶成反复换行。</para>
/// </summary>
public sealed class BusyMessageModel
{
    /// <summary>Gets the primary groups (batch position and stage text) in reading order.
    /// <para>获取按阅读顺序排列的主状态分组（批次序号与阶段文本）。</para></summary>
    /// <value>The live primary group collection; updated in place.
    /// <para>实时的主状态分组集合；原地更新。</para></value>
    public ObservableCollection<string> PrimaryGroups { get; } = [];

    /// <summary>Gets the metric groups (measured speed and estimated time left) in reading order.
    /// <para>获取按阅读顺序排列的遥测分组（测得速度与预计剩余时间）。</para></summary>
    /// <value>The live metric group collection; updated in place.
    /// <para>实时的遥测分组集合；原地更新。</para></value>
    public ObservableCollection<string> MetricGroups { get; } = [];

    /// <summary>
    /// Raised once after a batch update actually changes <see cref="PrimaryGroups"/> or
    /// <see cref="MetricGroups"/>, so forwarders can relay the whole payload without reacting to
    /// every per-item collection change.
    /// <para>批量更新实际改变了 <see cref="PrimaryGroups"/> 或 <see cref="MetricGroups"/> 后触发一次，
    /// 使转发方只需转发整份负载，而不必响应逐项集合变更。</para>
    /// </summary>
    public event EventHandler? Updated;

    /// <summary>
    /// Composes the groups from a status line and a progress meter: batch position and stage text
    /// into <see cref="PrimaryGroups"/>, measured speed and estimated time left into
    /// <see cref="MetricGroups"/>.
    /// <para>由状态文本与进度计量器合成分组：批次序号与阶段文本放入 <see cref="PrimaryGroups"/>，
    /// 测得速度与预计剩余时间放入 <see cref="MetricGroups"/>。</para>
    /// </summary>
    /// <param name="status">The stage text, or null to omit that group.
    /// <para>阶段文本；为 null 时省略该分组。</para></param>
    /// <param name="meter">The meter providing speed and remaining time, or null to omit both.
    /// <para>提供速度与剩余时间的计量器；为 null 时省略这两个分组。</para></param>
    /// <param name="index">One-based batch position; ignored unless <paramref name="total"/> is positive.
    /// <para>批次序号（从 1 开始）；仅当 <paramref name="total"/> 为正时使用。</para></param>
    /// <param name="total">Batch size; 0 hides the position group. <para>批次总数；为 0 时隐藏序号分组。</para></param>
    /// <param name="emptyPlaceholder">Text used when no primary group was produced, or null to stay empty.
    /// <para>没有任何主状态分组时的占位文本；为 null 时保持为空。</para></param>
    public void Update(string? status, ProgressMeter? meter, int index = 0, int total = 0,
        string? emptyPlaceholder = null)
    {
        // 条目计数模式的速度以「项/秒」为单位，与吞吐无关，因此只显示字节速度。
        var speedText = meter is { IsItemMode: false, CurrentSpeed: { } speed }
            ? ProgressMeter.FormatSpeed(speed)
            : null;
        var etaText = meter?.EstimatedRemaining is { } eta
            ? string.Format(CultureInfo.CurrentCulture,
                LanguageService.Instance.GetString("Progress_BusyEta"), ProgressMeter.FormatEta(eta))
            : null;
        Update(status, speedText, etaText, index, total, emptyPlaceholder);
    }

    /// <summary>
    /// Composes the groups from already-formatted status, speed and remaining-time text, so a
    /// caller holding the meter elsewhere can reuse it without recomputing the strings.
    /// <para>由已格式化的状态、速度与剩余时间文本合成分组，使持有计量器的调用方无需重复格式化。</para>
    /// </summary>
    /// <param name="status">The stage text, or null to omit that group. <para>阶段文本；为 null 时省略该分组。</para></param>
    /// <param name="speedText">The formatted throughput, or null to omit that group. <para>已格式化的吞吐；为 null 时省略该分组。</para></param>
    /// <param name="etaText">The formatted remaining time, or null to omit that group. <para>已格式化的剩余时间；为 null 时省略该分组。</para></param>
    /// <param name="index">One-based batch position; ignored unless <paramref name="total"/> is positive.
    /// <para>批次序号（从 1 开始）；仅当 <paramref name="total"/> 为正时使用。</para></param>
    /// <param name="total">Batch size; 0 hides the position group. <para>批次总数；为 0 时隐藏序号分组。</para></param>
    /// <param name="emptyPlaceholder">Text used when no primary group was produced, or null to stay empty.
    /// <para>没有任何主状态分组时的占位文本；为 null 时保持为空。</para></param>
    public void Update(string? status, string? speedText, string? etaText, int index = 0, int total = 0,
        string? emptyPlaceholder = null)
    {
        List<string> primary = new(2);
        if (total > 0)
        {
            primary.Add(string.Create(CultureInfo.CurrentCulture,
                $"[{Math.Clamp(index, 0, total)}/{total}]"));
        }
        if (!string.IsNullOrWhiteSpace(status)) primary.Add(status.Trim());
        if (primary.Count == 0 && !string.IsNullOrWhiteSpace(emptyPlaceholder))
            primary.Add(emptyPlaceholder);

        List<string> metrics = new(2);
        if (!string.IsNullOrWhiteSpace(speedText)) metrics.Add(speedText);
        if (!string.IsNullOrWhiteSpace(etaText)) metrics.Add(etaText);

        SetGroups(primary, metrics);
    }

    /// <summary>Copies both primary and metric groups from another <see cref="BusyMessageModel"/>.
    /// <para>从另一个 <see cref="BusyMessageModel"/> 复制主状态分组与遥测分组。</para></summary>
    /// <param name="source">The source payload to mirror. <para>要镜像的源负载。</para></param>
    public void CopyFrom(BusyMessageModel source) => SetGroups(source.PrimaryGroups, source.MetricGroups);

    /// <summary>Replaces the primary and metric groups, updating in place so bound controls are reused.
    /// <para>替换主状态分组与遥测分组；原地更新以便复用已绑定的控件。</para></summary>
    /// <param name="primaryGroups">The primary groups in reading order. <para>按阅读顺序排列的主状态分组。</para></param>
    /// <param name="metricGroups">The metric groups in reading order. <para>按阅读顺序排列的遥测分组。</para></param>
    public void SetGroups(IEnumerable<string> primaryGroups, IEnumerable<string> metricGroups)
    {
        var changed = SyncCollection(PrimaryGroups, primaryGroups);
        changed |= SyncCollection(MetricGroups, metricGroups);
        if (changed) Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes every group from both layers, leaving the busy area empty.
    /// <para>清空两层的全部分组，使忙碌区为空。</para></summary>
    public void Clear() => SetGroups([], []);

    private static bool SyncCollection(ObservableCollection<string> destination, IEnumerable<string> source)
    {
        List<string> target = [.. source];
        var changed = false;
        var shared = Math.Min(destination.Count, target.Count);
        for (var i = 0; i < shared; i++)
        {
            if (!string.Equals(destination[i], target[i], StringComparison.Ordinal))
            {
                destination[i] = target[i];
                changed = true;
            }
        }
        while (destination.Count > target.Count)
        {
            destination.RemoveAt(destination.Count - 1);
            changed = true;
        }
        for (var i = shared; i < target.Count; i++)
        {
            destination.Add(target[i]);
            changed = true;
        }
        return changed;
    }
}