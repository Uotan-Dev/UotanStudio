using System.Globalization;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Security;

namespace UotanStudio.Services.Flash;

/// <summary>
/// The outcome of one secure-boot entry point, with the vendor status preserved when the target answered.
/// <para>一次安全启动入口调用的结果；目标应答时保留厂商状态码。</para>
/// </summary>
/// <param name="Success"><para>操作是否被目标确认成功。</para>Whether the target acknowledged the operation.
/// </param>
/// <param name="Summary"><para>与本地化无关的结果说明。</para>A localization-neutral result description.
/// </param>
/// <param name="VendorStatusCode"><para>目标上报的原始状态码；主机侧拒绝或无法取得状态码时为 null。</para>
/// The raw status the target reported, or null for a host-side refusal or an unreported failure.</param>
internal readonly record struct MtkSecOperationStatus(bool Success, string Summary, uint? VendorStatusCode)
{
    /// <summary>Builds an acknowledged outcome.
    /// <para>构造一个已获确认的结果。</para></summary>
    /// <param name="summary">The description to carry. <para>要携带的说明。</para></param>
    /// <returns>A successful status with no vendor code. <para>不带厂商状态码的成功结果。</para></returns>
    internal static MtkSecOperationStatus Succeeded(string summary) => new(true, summary, null);

    /// <summary>Builds a failed outcome from a target or transport failure.
    /// <para>由目标或传输失败构造一个失败结果。</para></summary>
    /// <remarks>
    /// <para>Failures never default to success: the vendor code is taken from the status token the session
    /// readers append to their message, and an absent code still reports a failure with the exception text.
    /// The session's protocol failure type is internal to the communication layer, so the code is recovered
    /// from the text the reader documented rather than from a typed property.</para>
    /// <para>失败绝不会默认成为成功：厂商状态码取自会话读取器附加在消息末尾的状态标记，取不到状态码时也会
    /// 连同异常文本一起上报失败。通信层的协议失败类型对宿主不可见，因此状态码依据读取器记载的文本还原，
    /// 而不是通过强类型属性取得。</para>
    /// </remarks>
    /// <param name="summary">The description of the attempted operation.
    /// <para>对所尝试操作的说明。</para></param>
    /// <param name="failure">The exception the session raised. <para>会话抛出的异常。</para></param>
    /// <returns>A failed status carrying the vendor code when it is available.
    /// <para>可用时携带厂商状态码的失败结果。</para></returns>
    internal static MtkSecOperationStatus Failed(string summary, Exception failure)
    {
        var code = TryReadVendorStatusCode(failure);
        return code is { } value
            ? new MtkSecOperationStatus(false,
                $"{summary} The download agent reported {MtkSecStatusCode.Describe(value)}: {failure.Message}", value)
            : new MtkSecOperationStatus(false, $"{summary} {failure.Message}", null);
    }

    /// <summary>Gets the catalogued name of the reported status, when the evidence has one.
    /// <para>获取所上报状态的已编目名称（证据存在时）。</para></summary>
    /// <value>
    /// The documented description of <see cref="VendorStatusCode"/>, or <see langword="null"/>.
    /// <para><see cref="VendorStatusCode"/> 的已记载说明，或 <see langword="null"/>。</para>
    /// </value>
    internal string? VendorStatusDescription =>
        VendorStatusCode is { } code ? MtkSecStatusCode.Describe(code) : null;

    /// <summary>Reports whether the target rejected the operation with a status the evidence names.
    /// <para>报告目标是否以证据已命名的状态码拒绝了操作。</para></summary>
    /// <value>
    /// <see langword="true"/> when a vendor code other than success was reported.
    /// <para>上报了非成功的厂商状态码时为 <see langword="true"/>。</para>
    /// </value>
    internal bool IsVendorRejection => VendorStatusCode is not null and not MtkSecStatusCode.Ok;

    /// <summary>Reads the trailing status token the session readers append to a protocol failure.
    /// <para>读取会话读取器附加在协议失败末尾的状态标记。</para></summary>
    /// <param name="failure">The exception the session raised. <para>会话抛出的异常。</para></param>
    /// <returns>The reported status, or <see langword="false"/> equivalent null when no token is present.
    /// <para>上报的状态；不存在该标记时为 null。</para></returns>
    private static uint? TryReadVendorStatusCode(Exception failure)
    {
        if (failure is not IOException)
        {
            return null;
        }

        var message = failure.Message.AsSpan();
        var marker = message.LastIndexOf("(0x", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var digits = message[(marker + 3)..];
        var length = 0;
        while (length < digits.Length && length < 8 && char.IsAsciiHexDigit(digits[length]))
        {
            length++;
        }

        return length > 0 && uint.TryParse(digits[..length], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }
}

/// <summary>The named eFuse fields the session APIs can locate without a positional cell index.
/// <para>会话 API 无需按位置单元索引即可定位的具名 eFuse 字段。</para></summary>
/// <remarks>
/// <para>The transcribed cell table stores cells by position only, so the boot public key window is the single
/// field with a documented name and offset; every other field has to be addressed by its cell index.</para>
/// <para>转录的单元表只按位置保存单元，因此引导公钥窗口是唯一具有记载名称与偏移的字段；其他字段都必须按单元
/// 索引寻址。</para>
/// </remarks>
internal enum MtkSecEfuseField
{
    /// <summary>The documented boot public key window.
    /// <para>已记载的引导公钥窗口。</para></summary>
    BootPublicKey
}

/// <summary>The result of a secure-boot configuration read.
/// <para>安全启动配置读取的结果。</para></summary>
/// <param name="Status"><para>操作状态。</para>The operation status.</param>
/// <param name="View"><para>解码后的配置视图；失败时为 null。</para>The decoded view, or null on failure.</param>
/// <param name="Family"><para>执行读取的下载代理家族。</para>The family that performed the read.</param>
internal sealed record MtkSecCfgReportOutcome(MtkSecOperationStatus Status, MtkSecCfgView? View,
    MtkDaProtocol Family);

/// <summary>The result of a secure-boot configuration write attempt.
/// <para>安全启动配置写入尝试的结果。</para></summary>
/// <param name="Status"><para>操作状态。</para>The operation status.</param>
/// <param name="Receipt"><para>写入回执；未被目标确认时为 null。</para>The write receipt, or null when the
/// target did not acknowledge the write.</param>
/// <param name="Family"><para>执行写入的下载代理家族。</para>The family that performed the write.</param>
internal sealed record MtkSecCfgApplyOutcome(MtkSecOperationStatus Status, MtkSecCfgWriteResult? Receipt,
    MtkDaProtocol Family);

/// <summary>The result of a named eFuse field read.
/// <para>具名 eFuse 字段读取的结果。</para></summary>
/// <param name="Status"><para>操作状态。</para>The operation status.</param>
/// <param name="Bytes"><para>字段字节；失败时为 null。</para>The field bytes, or null on failure.</param>
/// <param name="Field"><para>所读取的具名字段。</para>The named field that was read.</param>
/// <param name="Family"><para>执行读取的下载代理家族。</para>The family that performed the read.</param>
internal sealed record MtkSecEfuseFieldOutcome(MtkSecOperationStatus Status, byte[]? Bytes, MtkSecEfuseField Field,
    MtkDaProtocol Family);

/// <summary>The result of a bounded eFuse cell read.
/// <para>有界 eFuse 单元读取的结果。</para></summary>
/// <param name="Status"><para>操作状态。</para>The operation status.</param>
/// <param name="Reading"><para>逐单元读取结果；失败时为 null。</para>The per-cell readings, or null on
/// failure.</param>
/// <param name="Family"><para>执行读取的下载代理家族。</para>The family that performed the read.</param>
internal sealed record MtkSecEfuseRangeOutcome(MtkSecOperationStatus Status, MtkEfuseReading? Reading,
    MtkDaProtocol Family);

/// <summary>The result of preparing an OEM key flash request.
/// <para>准备 OEM 密钥刷写请求的结果。</para></summary>
/// <param name="Status"><para>操作状态。</para>The operation status.</param>
/// <param name="Plan"><para>准备好的计划；被拒绝时为 null。</para>The prepared plan, or null when the request
/// was refused.</param>
internal sealed record MtkOemKeyPlanOutcome(MtkSecOperationStatus Status, MtkOemKeyPlan? Plan);

/// <summary>The result of submitting an OEM key through the family's documented path.
/// <para>按家族已记载路径提交 OEM 密钥的结果。</para></summary>
/// <param name="Status">The operation status. <para>操作状态。</para></param>
/// <param name="Receipt">The agent receipt, or <see langword="null"/> when the target refused.
/// <para>代理回执；目标拒绝时为 <see langword="null"/>。</para></param>
/// <param name="Family">The family that carried the submission.
/// <para>承载本次提交的下载代理家族。</para></param>
internal sealed record MtkOemKeyReceiptOutcome(MtkSecOperationStatus Status, MtkOemKeyReceipt? Receipt,
    MtkDaProtocol Family);

/// <summary>What a later OEM key submission would send, without sending it.
/// <para>后续 OEM 密钥提交将要发送的内容，但本身不发送。</para></summary>
/// <remarks>
/// <para>The plan names the delivery, the register window and the key digest so a confirmation dialog can show
/// exactly what changes. Key material is never carried here; the digest is the only trace, matching the
/// session receipt style.</para>
/// <para>该计划写明投递方式、寄存器窗口与密钥摘要，便于确认对话框展示究竟会改动什么。此处不携带密钥材料，
/// 只留下摘要，与会话回执的风格一致。</para>
/// </remarks>
/// <param name="Family"><para>后续提交所使用的下载代理家族。</para>The family a submission would use.</param>
/// <param name="Kind"><para>密钥类别。</para>The key class.</param>
/// <param name="KeySha256Hex"><para>密钥字节的 SHA-256 摘要，而非密钥本身。</para>The SHA-256 digest of the
/// key bytes rather than the key itself.</param>
/// <param name="RegisterAddresses"><para>按写入顺序排列的 SEJ 寄存器地址；由控制码投递时为空。</para>
/// The SEJ register addresses in write order, empty when the class is delivered by control code.</param>
/// <param name="Delivery"><para>说明投递路径的文本。</para>Text naming the delivery path.</param>
/// <param name="SubmittedUnits"><para>后续提交会发送的寄存器写入或密钥帧数量。</para>How many register writes
/// or key frames a submission would send.</param>
internal sealed record MtkOemKeyPlan(MtkDaProtocol Family, MtkOemKeyKind Kind, string KeySha256Hex,
    IReadOnlyList<uint> RegisterAddresses, string Delivery, int SubmittedUnits);