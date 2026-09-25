namespace UotanStudio.Firmware.SharedKernel;

/// <summary>
/// Sink for progress notifications raised by long-running firmware operations such as unpack and pack.
/// <para>解包、打包等长时间运行的固件操作所发出进度通知的接收端。</para>
/// </summary>
public interface IFirmwareProgress
{
    /// <summary>
    /// Reports progress.
    /// <para>报告进度。</para>
    /// </summary>
    /// <param name="percent">
    /// Progress percentage as supplied by the operation; the interface does not clamp it.
    /// <para>操作提供的进度百分比；本接口不做裁剪。</para>
    /// </param>
    /// <param name="message">
    /// Short description of the current step.
    /// <para>对当前步骤的简短说明。</para>
    /// </param>
    void Report(double percent, string message);
}