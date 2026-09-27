using System.Xml;
using UotanStudio.Comm.Protocols.Mtk.XmlProfile;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Defines explicit XML startup policy without guessed runtime or resource selections.
/// <para>定义显式 XML 启动策略，不猜测运行时或资源选择。</para></summary>
public sealed record MtkXmlBootstrapOptions
{
    /// <summary>Gets the explicitly selected DA runtime policy.
    /// <para>获取显式选定的 DA 运行时策略。</para></summary>
    /// <value>The runtime configuration.
    /// <para>运行时配置。</para></value>
    public required MtkXmlRuntimeOptions Runtime { get; init; }

    /// <summary>Gets the exact SLA property envelope from the selected loader profile.
    /// <para>获取所选加载器配置中的精确 SLA 属性信封名称。</para></summary>
    /// <value>A nonempty unqualified XML name, up to 128 characters.
    /// <para>非空的不带命名空间的 XML 名称，最多 128 个字符。</para></value>
    public required string SlaEnvelopeName { get; init; }

    /// <summary>Gets the explicit host metadata sent before DA2 upload.
    /// <para>获取在 DA2 上传之前发送的显式主机元数据。</para></summary>
    /// <value>At most 256 XML-compatible characters; empty is allowed.
    /// <para>最多 256 个 XML 兼容字符，允许为空。</para></value>
    public required string HostInfo { get; init; }

    /// <summary>Gets the caller-owned policy for device-requested files, including DRAM resources.
    /// <para>获取调用方持有的设备请求文件策略，包括 DRAM 资源。</para></summary>
    /// <value>The restricted file store, or null to reject device file requests.
    /// <para>受限文件存储；为 null 时拒绝设备文件请求。</para></value>
    public IMtkXmlHostFileStore? HostFileStore { get; init; }

    /// <summary>Gets the maximum length of each device-requested file transfer.
    /// <para>获取每次设备请求文件传输的最大长度。</para></summary>
    /// <value>A positive byte limit, defaulting to 64 MiB.
    /// <para>正数字节上限，默认为 64 MiB。</para></value>
    public long MaximumHostFileTransferBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Gets the cooperative deadline for each XML setup, upload or discovery operation.
    /// <para>获取每次 XML 设置、上传或发现操作的协作式截止时间。</para></summary>
    /// <value>Milliseconds from 1 through 86400000. SLA uses the common SLA deadline.
    /// <para>1 至 86400000 毫秒；SLA 使用共享 SLA 截止时间。</para></value>
    public int OperationTimeoutMs { get; init; } = 120000;

    /// <summary>Gets the grading policy for unknown fields in device-authored XML DA messages.
    /// <para>获取设备侧 XML DA 消息中未知字段的分级策略。</para></summary>
    /// <value>Defaults to <see cref="MtkXmlFieldPolicy.Strict"/>, preserving the historical
    /// reject-everything parsing; the tolerant policy only downgrades bounded ignorable fields to
    /// warning evidence.
    /// <para>默认 <see cref="MtkXmlFieldPolicy.Strict"/>，保持历史的全量拒绝解析；容忍策略仅将有界的可忽略
    /// 字段降级为 warning 证据。</para></value>
    public MtkXmlFieldPolicy XmlFieldPolicy { get; init; } = MtkXmlFieldPolicy.Strict;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Runtime);
        _ = Runtime.BuildCommand();
        ArgumentException.ThrowIfNullOrWhiteSpace(SlaEnvelopeName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SlaEnvelopeName.Length, 128);
        XmlConvert.VerifyNCName(SlaEnvelopeName);
        ArgumentNullException.ThrowIfNull(HostInfo);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HostInfo.Length, 256);
        XmlConvert.VerifyXmlChars(HostInfo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumHostFileTransferBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(OperationTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(OperationTimeoutMs, 86400000);
    }
}