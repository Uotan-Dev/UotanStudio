using System.Threading;
using UotanStudio.Abstractions.Devices;
using UotanStudio.Abstractions.Logging;

namespace UotanStudio.Abstractions.Nodes;

/// <summary>
/// <para>节点执行上下文，提供日志、设备管理器等服务。</para>
/// Node execution context, providing logging, device manager, etc.
/// </summary>
public interface INodeExecutionContext
{
    /// <summary>
    /// <para>统一日志服务，所有节点执行日志通过此推送。</para>
    /// Unified log service; all node execution logs are pushed through this.
    /// </summary>
    IUnifiedLog Log { get; }

    /// <summary>
    /// <para>设备管理器，提供 ADB/Fastboot/EDL/MTK/Rockchip 等协议访问。</para>
    /// Device manager, providing access to ADB/Fastboot/EDL/MTK/Rockchip protocols.
    /// </summary>
    IDeviceManager Devices { get; }

    /// <summary>
    /// <para>取消令牌。</para>
    /// Cancellation token.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// <para>报告节点执行进度（0.0 ~ 1.0）。</para>
    /// Reports the node execution progress (0.0 ~ 1.0).
    /// </summary>
    void ReportProgress(double progress);
}
