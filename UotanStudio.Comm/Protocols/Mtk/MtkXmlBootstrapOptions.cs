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

    /// <summary>Gets the property-envelope outer element name asserted during the SLA query.
    /// <para>获取 SLA 查询期间断言的属性信封外层元素名。</para></summary>
    /// <value>
    /// A nonempty unqualified XML name of at most 128 characters, or <see langword="null"/> to accept the
    /// envelope the agent reports. The reference implementation reads the reply without naming its
    /// envelope, so a loader package that publishes no envelope profile can still start; when a value is
    /// supplied it is an additional assertion and a mismatch is a protocol failure. The reported name
    /// stays visible on the returned property either way.
    /// <para>非空、不带命名空间前缀且最长 128 字符的 XML 名称；为 <see langword="null"/> 时接受代理上报的信封。
    /// 参考实现读取回复时不指定信封名称，因此未公开信封配置的加载器包同样可以启动；提供数值时它作为额外断言，
    /// 不匹配即为协议失败。无论哪种情况，上报的名称仍保留在返回的属性中。</para>
    /// </value>
    public string? SlaEnvelopeName { get; init; }

    /// <summary>Gets the explicit host metadata sent before DA2 upload.
    /// <para>获取在 DA2 上传之前发送的显式主机元数据。</para></summary>
    /// <value>At most 256 XML-compatible characters; the reference tool sends an empty string, which is
    /// the default here too. <para>最多 256 个 XML 兼容字符；参考工具发送空字符串，此处默认值同样为空。</para></value>
    public string HostInfo { get; init; } = string.Empty;

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
        if (SlaEnvelopeName is { } envelope)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(envelope);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(envelope.Length, 128);
            XmlConvert.VerifyNCName(envelope);
        }
        ArgumentNullException.ThrowIfNull(HostInfo);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HostInfo.Length, 256);
        XmlConvert.VerifyXmlChars(HostInfo);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumHostFileTransferBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(OperationTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(OperationTimeoutMs, 86400000);
    }
}