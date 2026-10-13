using System.Globalization;

namespace UotanStudio.Services;

/// <summary>
/// Shared progress meter: computes sliding-window speed and estimated time remaining from
/// processed/total samples and formats a stats line.
/// <para>共享进度计量器：按已处理量与总量样本计算滑动窗口速度与预计剩余时间，并生成统计文本。</para>
/// </summary>
public sealed class ProgressMeter
{
    private const double WindowSeconds = 4.0;
    private const double MinimumSampleGapSeconds = 0.25;

    private readonly record struct Sample(long Processed, long Timestamp);

    private readonly TimeProvider _clock;
    /// <summary>Creates a meter using the system monotonic clock.<para>使用系统单调时钟创建计量器。</para></summary>
    public ProgressMeter() : this(TimeProvider.System) { }
    internal ProgressMeter(TimeProvider clock) => _clock = clock;

    private readonly Lock _gate = new();
    private readonly Queue<Sample> _samples = new();
    private bool _itemMode;

    /// <summary>The latest processed amount (bytes or items). <para>最近一次上报的已处理量（字节或条目数）。</para></summary>
    /// <value>The amount accepted by the newest sample. <para>最新样本接受的已处理量。</para></value>
    public long Processed { get; private set; }

    /// <summary>The latest total amount (bytes or items). <para>最近一次上报的总量（字节或条目数）。</para></summary>
    /// <value>The total amount accepted by the newest sample. <para>最新样本接受的总量。</para></value>
    public long Total { get; private set; }

    /// <summary>
    /// Reports byte-based progress; sampling restarts through <see cref="Reset"/> at a stage transition.
    /// <para>以字节样本上报进度；阶段切换时经 <see cref="Reset"/> 重新采样。</para>
    /// </summary>
    /// <param name="processed">Bytes processed so far. <para>截至当前的已处理字节数。</para></param>
    /// <param name="total">Total bytes for the stage. <para>该阶段的总字节数。</para></param>
    public void Report(long processed, long total)
    {
        lock (_gate)
        {
            if (_itemMode) _samples.Clear();
            _itemMode = false;
            Accept(processed, total);
        }
    }

    /// <summary>
    /// Reports item-count progress; the stats line is rendered as items.
    /// <para>以条目计数样本上报进度；统计文本按「项」呈现。</para>
    /// </summary>
    /// <param name="processed">Items processed so far. <para>截至当前的已处理条目数。</para></param>
    /// <param name="total">Total items for the stage. <para>该阶段的总条目数。</para></param>
    public void ReportItems(long processed, long total)
    {
        lock (_gate)
        {
            if (!_itemMode) _samples.Clear();
            _itemMode = true;
            Accept(processed, total);
        }
    }

    /// <summary>Clears samples and counters for stage transitions. <para>清空样本与计数，用于阶段切换。</para></summary>
    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _itemMode = false;
            Processed = 0;
            Total = 0;
        }
    }

    private void Accept(long processed, long total)
    {
        if (processed < Processed) _samples.Clear();
        Processed = Math.Max(0, processed);
        Total = Math.Max(0, total);
        var now = _clock.GetTimestamp();
        _samples.Enqueue(new Sample(Processed, now));
        while (_samples.Count > 2 && (_samples.Count > 64 || _clock.GetElapsedTime(_samples.Peek().Timestamp, now).TotalSeconds > WindowSeconds))
            _samples.Dequeue();
    }

    /// <summary>
    /// Byte speed within the sliding window; <c>null</c> when samples are insufficient or not positive.
    /// <para>滑动窗口内的字节速度；样本不足或速度非正时为 <c>null</c>。</para>
    /// </summary>
    /// <value>The current byte throughput, or <c>null</c> when not computable. <para>当前的字节吞吐，无法计算时为 <c>null</c>。</para></value>
    public double? BytesPerSecond
    {
        get
        {
            lock (_gate) return ComputeSpeed();
        }
    }

    /// <summary>Estimated remaining time at the current speed; <c>null</c> when not estimable. <para>按当前速度估算的剩余时间；无法估算时为 <c>null</c>。</para></summary>
    /// <value>The projected time left, or <c>null</c> when not estimable. <para>预计剩余时间，无法估算时为 <c>null</c>。</para></value>
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            lock (_gate)
            {
                var speed = ComputeSpeed();
                if (speed is not { } value || Total <= Processed) return null;
                var seconds = (Total - Processed) / value;
                return seconds is > 0 and < 86400 * 7 ? TimeSpan.FromSeconds(seconds) : null;
            }
        }
    }

    /// <summary>
    /// Builds the stats text from a localized format template.
    /// <para>用本地化格式模板生成统计文本。</para>
    /// </summary>
    /// <param name="template">Format string with {0}=processed, {1}=total, {2}=speed and {3}=eta.
    /// <para>格式串，占位符 {0}=已处理、{1}=总量、{2}=速度、{3}=剩余时间。</para></param>
    /// <returns>The formatted stats line. <para>格式化后的统计文本。</para></returns>
    public string FormatStats(string template)
    {
        lock (_gate)
        {
            var speed = ComputeSpeed();
            string processed, total, speedText;
            if (_itemMode)
            {
                processed = Processed.ToString(CultureInfo.CurrentCulture);
                total = Total.ToString(CultureInfo.CurrentCulture);
                speedText = speed is { } value ? string.Create(CultureInfo.CurrentCulture, $"{value:0.#}/s") : "—";
            }
            else
            {
                processed = FormatSize(Processed);
                total = FormatSize(Total);
                speedText = speed is { } value ? FormatSpeed(value) : "—";
            }
            return string.Format(CultureInfo.CurrentCulture, template, processed, total, speedText, FormatEta(EstimatedRemaining));
        }
    }

    private double? ComputeSpeed()
    {
        if (_samples.Count < 2) return null;
        var first = _samples.Peek();
        var last = _samples.Last();
        var seconds = _clock.GetElapsedTime(first.Timestamp, last.Timestamp).TotalSeconds;
        if (seconds < MinimumSampleGapSeconds) return null;
        var bytes = last.Processed - first.Processed;
        return bytes > 0 ? bytes / seconds : null;
    }

    /// <summary>Formats a speed as "x unit/s"; invalid values render as "—". <para>把速度格式化为「x 单位/s」；无效值显示「—」。</para></summary>
    /// <param name="bytesPerSecond">The byte throughput to render. <para>待呈现的字节吞吐。</para></param>
    /// <returns>The rendered speed text. <para>渲染后的速度文本。</para></returns>
    public static string FormatSpeed(double bytesPerSecond) =>
        double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? FormatSize(bytesPerSecond) + "/s" : "—";

    /// <summary>
    /// Formats eta as mm:ss (under an hour) or h:mm:ss; <c>null</c> renders as "—".
    /// <para>把剩余时间格式化为 mm:ss（不足一小时）或 h:mm:ss；<c>null</c> 显示「—」。</para>
    /// </summary>
    /// <param name="eta">The estimated remaining time to render. <para>待渲染的预计剩余时间。</para></param>
    /// <returns>The rendered time text. <para>渲染后的时间文本。</para></returns>
    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is not { } value || value <= TimeSpan.Zero) return "—";
        return value.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.Minutes:00}:{value.Seconds:00}");
    }

    /// <summary>
    /// Formats bytes as base-1024 B/KB/MB/GB/TB with one decimal; negative or non-finite values render as "—".
    /// <para>把字节数格式化为 1024 进制的 B/KB/MB/GB/TB，一位小数；负值或非有限值显示「—」。</para>
    /// </summary>
    /// <param name="bytes">The byte count to render. <para>待渲染的字节数。</para></param>
    /// <returns>The rendered size text. <para>渲染后的大小文本。</para></returns>
    public static string FormatSize(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        while (bytes >= 1024 && index < units.Length - 1)
        {
            bytes /= 1024;
            index++;
        }
        return index == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{(long)bytes} {units[index]}")
            : string.Create(CultureInfo.CurrentCulture, $"{bytes:0.#} {units[index]}");
    }
}