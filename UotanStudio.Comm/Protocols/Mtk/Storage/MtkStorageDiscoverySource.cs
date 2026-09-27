namespace UotanStudio.Comm.Protocols.Mtk.Storage;

/// <summary>
/// Identifies which MTK agent protocol produced a storage capability report.
/// <para>标识是哪个 MTK 代理协议产生了该存储能力报告。</para>
/// The three discovery paths share one snapshot type so hosts can consume a single contract while
/// the source retains which wire profile produced the observation.
/// <para>三条发现路径共用同一快照类型，使宿主可以消费单一契约，同时由来源字段保留观测来自哪一线协议配置。</para>
/// </summary>
public enum MtkStorageDiscoverySource
{
    /// <summary>The XFlash DA device-control storage query. <para>XFlash DA 设备控制存储查询。</para></summary>
    XFlash,
    /// <summary>The XML DA <c>CMD:GET-HW-INFO</c> report. <para>XML DA 的 <c>CMD:GET-HW-INFO</c> 报告。</para></summary>
    Xml,
    /// <summary>The Legacy (non-IoT) DA initialization inventory. <para>Legacy（非 IoT）DA 初始化清单。</para></summary>
    Legacy
}