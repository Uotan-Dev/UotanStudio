using System.Globalization;

namespace UotanStudio.Services;

/// <summary>
/// <para>共享进度计量器：按 (已处理, 总量) 样本计算滑动窗口速度与预计剩余时间，
/// 并格式化为「已处理 / 总量 · 速度 · 剩余时间」统计文本。字节模式由
/// <see cref="Report"/> 喂入，条目计数模式由 <see cref="ReportItems"/> 喂入；
/// 格式化模板由调用方经本地化提供，本类型不依赖 <see cref="LanguageService"/>。</para>
/// Shared progress meter: computes sliding-window speed and estimated time remaining from
/// (processed, total) samples and formats a "processed / total · speed · eta" stats line. The byte
/// mode is fed via <see cref="Report"/> and the item-count mode via <see cref="ReportItems"/>; the
/// caller supplies the localized format template — this type does not depend on <see cref="LanguageService"/>.
/// </summary>
public sealed class ProgressMeter
{
    private const double WindowSeconds = 4.0;
    private const double MinimumSampleGapSeconds = 0.25;

    private readonly record struct Sample(long Processed, long Timestamp);

    private readonly TimeProvider _clock;
    /// <summary>Creates a meter using the system monotonic clock.</summary>
    public ProgressMeter() : this(TimeProvider.System) { }
    internal ProgressMeter(TimeProvider clock) => _clock = clock;

    private readonly object _gate = new();
    private readonly Queue<Sample> _samples = new();
    private bool _itemMode;

    /// <summary><para>最近一次上报的已处理量（字节或条目数）。</para>The latest processed amount (bytes or items).</summary>
    public long Processed { get; private set; }

    /// <summary><para>最近一次上报的总量（字节或条目数）。</para>The latest total amount (bytes or items).</summary>
    public long Total { get; private set; }

    /// <summary>
    /// <para>以字节样本上报进度（阶段开始或切换时先 <see cref="Reset"/>）。</para>
    /// Reports byte-based progress (call <see cref="Reset"/> when a stage starts or switches).</summary>
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
    /// <para>以条目计数样本上报进度；统计文本按「项」呈现。</para>
    /// Reports item-count progress; the stats line is rendered as items.</summary>
    public void ReportItems(long processed, long total)
    {
        lock (_gate)
        {
            if (!_itemMode) _samples.Clear();
            _itemMode = true;
            Accept(processed, total);
        }
    }

    /// <summary><para>清空样本与计数，用于阶段切换。</para>Clears samples and counters for stage transitions.</summary>
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
        // 新阶段（processed 回退）视为重新开始，丢弃历史样本避免速度被拉低。
        // A falling processed count means a new stage; drop history so the speed is not diluted.
        if (processed < Processed) _samples.Clear();
        Processed = Math.Max(0, processed);
        Total = Math.Max(0, total);
        var now = _clock.GetTimestamp();
        _samples.Enqueue(new Sample(Processed, now));
        while (_samples.Count > 2 && (_samples.Count > 64 || _clock.GetElapsedTime(_samples.Peek().Timestamp, now).TotalSeconds > WindowSeconds))
            _samples.Dequeue();
    }

    /// <summary>
    /// <para>滑动窗口内的字节速度；样本不足或速度非正时为 <c>null</c>。</para>
    /// Byte speed within the sliding window; <c>null</c> when samples are insufficient or not positive.</summary>
    public double? BytesPerSecond
    {
        get
        {
            lock (_gate) return ComputeSpeed();
        }
    }

    /// <summary><para>按当前速度估算的剩余时间；无法估算时为 <c>null</c>。</para>Estimated remaining time at the current speed; <c>null</c> when not estimable.</summary>
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
    /// <para>用调用方提供的本地化模板生成统计文本；模板占位符：
    /// {0}=已处理、{1}=总量、{2}=速度、{3}=剩余时间。</para>
    /// Builds the stats text with a caller-supplied localized template; placeholders:
    /// {0}=processed, {1}=total, {2}=speed, {3}=eta.</summary>
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
        // 速度取窗口首尾样本差；首尾间隔过短时不估算，避免瞬时抖动。
        // Speed is the delta across window edges; skip estimation when the span is too short.
        if (_samples.Count < 2) return null;
        var first = _samples.Peek();
        var last = _samples.Last();
        var seconds = _clock.GetElapsedTime(first.Timestamp, last.Timestamp).TotalSeconds;
        if (seconds < MinimumSampleGapSeconds) return null;
        var bytes = last.Processed - first.Processed;
        return bytes > 0 ? bytes / seconds : null;
    }

    /// <summary><para>把速度格式化为「x 单位/s」；无效值显示「—」。</para>Formats a speed as "x unit/s"; invalid values render as "—".</summary>
    public static string FormatSpeed(double bytesPerSecond) =>
        double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? FormatSize(bytesPerSecond) + "/s" : "—";

    /// <summary><para>把剩余时间格式化为 mm:ss（不足一小时）或 h:mm:ss；<c>null</c> 显示「—」。</para>
    /// Formats eta as mm:ss (under an hour) or h:mm:ss; <c>null</c> renders as "—".</summary>
    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is not { } value || value <= TimeSpan.Zero) return "—";
        return value.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.Minutes:00}:{value.Seconds:00}");
    }

    /// <summary><para>把字节数格式化为 1024 进制的 B/KB/MB/GB/TB，一位小数；负值或非有限值显示「—」。</para>
    /// Formats bytes as base-1024 B/KB/MB/GB/TB with one decimal; negative or non-finite values render as "—".</summary>
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