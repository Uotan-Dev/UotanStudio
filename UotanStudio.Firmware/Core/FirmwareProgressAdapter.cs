namespace UotanStudio.Firmware.Core;

/// <summary>
/// Bridges the fraction-of-one progress reported by format handlers into an <see cref="IFirmwareProgress"/> sink with
/// a fixed step label; the adapter scales the fraction by 100.
/// <para>把格式处理器上报的 0-1 分数进度桥接为带固定步骤标签的 <see cref="IFirmwareProgress"/>；适配器将分数放大 100 倍。</para>
/// </summary>
/// <param name="sink">Progress sink receiving the scaled reports. <para>接收放大后进度报告的接收端。</para></param>
/// <param name="label">Step label attached to every report. <para>附加到每次报告的步骤标签。</para></param>
internal sealed class FirmwareProgressAdapter(IFirmwareProgress sink, string label) : IProgress<double>
{
    private readonly IFirmwareProgress _sink = sink;
    private readonly string _label = label;

    /// <inheritdoc/>
    public void Report(double value) => _sink.Report(value * 100, _label);
}

/// <summary>
/// Minimal synchronous <see cref="IProgress{T}"/> that forwards each value to a callback, unlike
/// <see cref="Progress{T}"/> which posts to the capture context.
/// <para>最小的同步 <see cref="IProgress{T}"/>，把每个值直接转发给回调；不同于 <see cref="Progress{T}"/> 会投递到捕获的上下文。</para>
/// </summary>
/// <typeparam name="T">Type of the progress values. <para>进度值的类型。</para></typeparam>
/// <param name="report">Callback invoked synchronously for every value. <para>对每个值同步调用的回调。</para></param>
internal sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly Action<T> _report = report;

    /// <inheritdoc/>
    public void Report(T value) => _report(value);
}