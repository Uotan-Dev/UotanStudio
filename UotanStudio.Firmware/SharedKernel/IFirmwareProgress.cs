namespace UotanStudio.Firmware.SharedKernel;

public interface IFirmwareProgress
{
    /// <summary>
    /// Reports progress.
    /// <para>报告进度。</para>
    /// </summary>
    void Report(double percent, string message);
}