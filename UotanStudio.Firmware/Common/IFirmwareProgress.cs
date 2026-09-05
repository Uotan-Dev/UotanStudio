namespace UotanStudio.Firmware.Common;

/// <summary>
/// Progress reporting abstraction for long-running firmware operations.
/// <para>长时间固件操作的进度报告抽象。</para>
/// </summary>
public interface IFirmwareProgress
{
    /// <summary>
    /// Reports progress.
    /// <para>报告进度。</para>
    /// </summary>
    /// <param name="percent">Percent completed (0-100).</param>
    /// <param name="message">Current stage message.</param>
    void Report(double percent, string message);
}