using UotanStudio.Services.Firmware;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>把固件库 <see cref="UotanStudio.Firmware.Core.IFirmwareProgress"/> 的百分比回调
/// 适配为条目内 <see cref="IProgress{Double}"/>，供 <see cref="UnpackOptions.Progress"/> 使用。</para>
/// Adapts the firmware library's <see cref="UotanStudio.Firmware.Core.IFirmwareProgress"/>
/// percent callbacks to an in-item <see cref="IProgress{Double}"/> for <see cref="UnpackOptions.Progress"/>.
/// </summary>
internal sealed class FirmwareItemProgress(IProgress<double> itemProgress) : UotanStudio.Firmware.Core.IFirmwareProgress
{
    /// <inheritdoc />
    public void Report(double percent, string message) => itemProgress.Report(percent);
}