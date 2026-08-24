using System.Threading.Tasks;
using UotanStudio.Core.Projects;
using UotanStudio.Firmware.Common;
using UotanStudio.Services;

namespace UotanStudio.Core.Runtime;

/// <summary>
/// <para>固件 IDE 运行时门面契约：统一暴露工程、设备、固件解析与日志能力，
/// 供各面板与节点编辑器共享，避免各自直连全局服务。</para>
/// Firmware IDE runtime facade contract: uniformly exposes project, device, firmware
/// parsing and logging capabilities to all panels and the node editor, avoiding direct
/// global-service access from each view model.
/// </summary>
public interface IStudioRuntime
{
    /// <summary>
    /// <para>获取当前打开的固件工程；未打开时为 null。</para>
    /// Gets the currently open firmware project; null when none is open.
    /// </summary>
    FirmwareProject? CurrentProject { get; }

    /// <summary>
    /// <para>获取固件操作服务。</para>
    /// Gets the firmware operation service.
    /// </summary>
    FirmwareService Firmware { get; }

    /// <summary>
    /// <para>获取设备监视服务。</para>
    /// Gets the device monitor service.
    /// </summary>
    DeviceMonitorService Devices { get; }

    /// <summary>
    /// <para>获取日志服务。</para>
    /// Gets the log service.
    /// </summary>
    LogService Logs { get; }

    /// <summary>
    /// <para>识别一个文件的固件格式处理器；无法识别时返回 null。</para>
    /// Detects the firmware format handler for a file; null when unrecognized.
    /// </summary>
    /// <param name="path"><para>文件路径。</para>File path.</param>
    /// <returns>对应的格式处理器或 null。</returns>
    IFormatHandler? DetectFormat(string path);

    /// <summary>
    /// <para>异步返回格式识别信息的文本摘要。</para>
    /// Returns a text summary of the detected format asynchronously.
    /// </summary>
    /// <param name="path"><para>文件路径。</para>File path.</param>
    /// <returns>格式摘要。</returns>
    Task<Services.FirmwareOpResult> DescribeFormatAsync(string path);
}