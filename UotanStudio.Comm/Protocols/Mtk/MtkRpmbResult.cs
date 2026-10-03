namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// The per-frame result the agent reports for one RPMB access.
/// <para>代理针对单次 RPMB 访问上报的逐帧结果。</para>
/// </summary>
/// <remarks>
/// Only the codes the reference client names are represented. The table it prints for them lives at
/// <c>mtkclient/Library/DA/xflash/extension/xflash.py:29-38</c> and
/// <c>mtkclient/Library/DA/xml/extension/v6.py:16-25</c>, which list exactly these eight entries; any other
/// reported number stays uninterpreted and is preserved as a raw code in <see cref="MtkRpmbFrameOutcome"/>.
/// <para>仅表示参考客户端明确命名的状态码。其打印的对照表位于
/// <c>mtkclient/Library/DA/xflash/extension/xflash.py:29-38</c> 与
/// <c>mtkclient/Library/DA/xml/extension/v6.py:16-25</c>，其中恰好只有这八项；其他数值一律不做解释，
/// 只以 <see cref="MtkRpmbFrameOutcome"/> 的原始码保留。
/// </para>
/// </remarks>
public enum MtkRpmbFrameStatus
{
    /// <summary>
    /// The frame completed; the first table entry is an empty label.
    /// <para>该帧已完成；对照表首项的标签为空字符串。</para>
    /// </summary>
    Okay = 0,
    /// <summary>
    /// General failure. <para>一般性失败。</para>
    /// </summary>
    GeneralFailure = 1,
    /// <summary>
    /// Authentication failure. <para>认证失败。</para>
    /// </summary>
    AuthenticationFailure = 2,
    /// <summary>
    /// Counter failure. <para>计数器失败。</para>
    /// </summary>
    CounterFailure = 3,
    /// <summary>
    /// Address failure. <para>地址失败。</para>
    /// </summary>
    AddressFailure = 4,
    /// <summary>
    /// Write failure. <para>写入失败。</para>
    /// </summary>
    WriteFailure = 5,
    /// <summary>
    /// Read failure. <para>读取失败。</para>
    /// </summary>
    ReadFailure = 6,
    /// <summary>
    /// The authentication key is not yet programmed, the only provisioning state the evidence names.
    /// <para>认证密钥尚未写入，这是证据中唯一明确命名的配置状态。</para>
    /// </summary>
    AuthenticationKeyNotProgrammed = 7
}

/// <summary>
/// One device-reported RPMB frame result, keeping the original wire number.
/// <para>设备上报的单帧 RPMB 结果，保留原始线协议数值。</para>
/// </summary>
/// <param name="ReportedCode">
/// The little-endian sixteen-bit number taken from the reply frame, unchanged.
/// <para>从应答帧按小端读取的 16 位数值，未经修改。</para>
/// </param>
/// <param name="Status">
/// The named meaning when <paramref name="ReportedCode"/> is zero through seven, otherwise
/// <see langword="null"/>.
/// <para>当 <paramref name="ReportedCode"/> 为 0 至 7 时的已命名含义，否则为 <see langword="null"/>。</para>
/// </param>
public readonly record struct MtkRpmbFrameOutcome(ushort ReportedCode, MtkRpmbFrameStatus? Status)
{
    /// <summary>
    /// Gets whether the frame completed successfully.
    /// <para>获取该帧是否成功完成。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> only for the reported code zero.
    /// <para>仅在上报数值为零时返回 <see langword="true"/>。</para>
    /// </value>
    public bool IsOkay => Status == MtkRpmbFrameStatus.Okay;

    /// <summary>
    /// Gets whether the reported code has a meaning the evidence supplies.
    /// <para>获取该上报数值是否具有证据提供的含义。</para>
    /// </summary>
    /// <value>
    /// <see langword="false"/> for codes above seven, which the reference table does not name.
    /// <para>大于 7 的数值返回 <see langword="false"/>，参考对照表未命名这些值。</para>
    /// </value>
    public bool IsKnown => Status is not null;
}

/// <summary>
/// The outcome of one addressed RPMB frame, with its raw bytes when the device delivered them.
/// <para>单个已寻址 RPMB 帧的结果，并在设备交付时附带原始字节。</para>
/// </summary>
public sealed record MtkRpmbFrameResult
{
    /// <summary>
    /// Gets the frame index this result belongs to.
    /// <para>获取该结果所属的帧序号。</para>
    /// </summary>
    /// <value>
    /// The absolute frame index, starting at the requested range's first frame.
    /// <para>绝对帧序号，从请求范围的首帧开始。</para>
    /// </value>
    public required long Frame { get; init; }
    /// <summary>
    /// Gets the reported frame result.
    /// <para>获取上报的帧结果。</para>
    /// </summary>
    /// <value>The raw sixteen-bit code plus its named meaning, when known.
    /// <para>原始 16 位数值及其已命名含义（若有）。</para></value>
    public required MtkRpmbFrameOutcome Outcome { get; init; }
    /// <summary>
    /// Gets the frame bytes the device returned.
    /// <para>获取设备返回的帧字节。</para>
    /// </summary>
    /// <value>
    /// Exactly one frame of payload after a successful read, and an empty array for every other result,
    /// including writes and refused frames.
    /// <para>成功读取时恰好为一帧载荷；其他所有结果（包括写入与被拒绝的帧）均为空数组。</para>
    /// </value>
    public required byte[] RawFrame { get; init; }
}

/// <summary>
/// The outcome of a bounded RPMB frame read.
/// <para>有界 RPMB 帧读取的结果。</para>
/// </summary>
public sealed record MtkRpmbReadResult
{
    /// <summary>
    /// Gets the interface that served the read.
    /// <para>获取提供本次读取的接口。</para>
    /// </summary>
    /// <value>The eMMC or UFS RPMB target derived from the discovered geometry.
    /// <para>由已发现几何信息推导的 eMMC 或 UFS RPMB 目标。</para></value>
    public required MtkRpmbTarget Target { get; init; }
    /// <summary>
    /// Gets the range that was requested.
    /// <para>获取所请求的范围。</para>
    /// </summary>
    /// <value>The validated frame range, preserved even for an incomplete read.
    /// <para>已校验的帧范围；即使读取未完成也会保留。</para></value>
    public required MtkRpmbFrameRange Requested { get; init; }
    /// <summary>
    /// Gets one result per frame the session actually addressed.
    /// <para>获取会话实际寻址的每一帧的结果。</para>
    /// </summary>
    /// <value>
    /// Ordered frame results. The read stops after the first frame whose reported code is not zero and the
    /// session never replays that frame.
    /// <para>按序排列的帧结果；上报数值非零的首帧之后即停止，且会话绝不重放该帧。</para>
    /// </value>
    public required IReadOnlyList<MtkRpmbFrameResult> Frames { get; init; }
    /// <summary>
    /// Gets the concatenated frame bytes.
    /// <para>获取拼接后的帧字节。</para>
    /// </summary>
    /// <value>
    /// The raw bytes of every successful frame, in address order, with nothing removed or reinterpreted.
    /// <para>成功帧的原始字节按地址顺序拼接，不做任何删减或重新解释。</para>
    /// </value>
    public required byte[] RawBytes { get; init; }
    /// <summary>
    /// Gets the RPMB capacity reported by the device geometry.
    /// <para>获取设备几何信息上报的 RPMB 容量。</para>
    /// </summary>
    /// <value>
    /// The reported byte capacity, or <see langword="null"/> when the selected interface's geometry has no
    /// RPMB size field, which is the UFS case for the XFlash family.
    /// <para>上报的字节容量；所选接口的几何信息没有 RPMB 大小字段时为 <see langword="null"/>，
    /// 即 XFlash 族的 UFS 情形。</para>
    /// </value>
    public long? ReportedRpmbCapacityBytes { get; init; }
    /// <summary>
    /// Gets whether every requested frame returned a successful code.
    /// <para>获取所有请求帧是否都返回了成功码。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> only when one result exists per requested frame and every result is successful.
    /// <para>仅当每个请求帧都有结果且全部成功时返回 <see langword="true"/>。</para>
    /// </value>
    public bool IsComplete => Frames.Count == Requested.FrameCount && Frames.All(static frame => frame.Outcome.IsOkay);
    /// <summary>
    /// Gets the number of delivered bytes.
    /// <para>获取已交付的字节数。</para>
    /// </summary>
    /// <value>The length of <see cref="RawBytes"/>. <para><see cref="RawBytes"/> 的长度。</para></value>
    public int DeliveredByteCount => RawBytes.Length;
}

/// <summary>
/// The outcome of a gated RPMB frame write.
/// <para>受开关控制的 RPMB 帧写入结果。</para>
/// </summary>
public sealed record MtkRpmbWriteResult
{
    /// <summary>
    /// Gets the interface that served the write.
    /// <para>获取提供本次写入的接口。</para>
    /// </summary>
    /// <value>The eMMC or UFS RPMB target derived from the discovered geometry.
    /// <para>由已发现几何信息推导的 eMMC 或 UFS RPMB 目标。</para></value>
    public required MtkRpmbTarget Target { get; init; }
    /// <summary>
    /// Gets the range that was submitted.
    /// <para>获取提交的范围。</para>
    /// </summary>
    /// <value>The validated frame range. <para>已校验的帧范围。</para></value>
    public required MtkRpmbFrameRange Requested { get; init; }
    /// <summary>
    /// Gets one result per frame the session addressed.
    /// <para>获取会话已寻址每一帧的结果。</para>
    /// </summary>
    /// <value>
    /// Ordered frame results whose <see cref="MtkRpmbFrameResult.RawFrame"/> is always empty, because the
    /// write exchange returns status only and this session never echoes source bytes back.
    /// <para>按序排列的帧结果，其 <see cref="MtkRpmbFrameResult.RawFrame"/> 恒为空数组，
    /// 因为写入交换只返回状态，且本会话绝不会回显源字节。</para>
    /// </value>
    public required IReadOnlyList<MtkRpmbFrameResult> Frames { get; init; }
    /// <summary>
    /// Gets whether every submitted frame was accepted.
    /// <para>获取所有提交帧是否均被接受。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> only when one result exists per submitted frame and every result is successful.
    /// A device refusal is reported here and is not retried.
    /// <para>仅当每个提交帧都有结果且全部成功时为 <see langword="true"/>；设备拒绝会在此报告，且不会重试。</para>
    /// </value>
    public bool IsComplete => Frames.Count == Requested.FrameCount && Frames.All(static frame => frame.Outcome.IsOkay);
}

/// <summary>
/// The RPMB status query result, preserving the device reply exactly as received.
/// <para>RPMB 状态查询结果，按接收原样保留设备应答。</para>
/// </summary>
/// <remarks>
/// The reference client requests this control code, checks that the exchange completed and returns the
/// reply without decoding it, so this record exposes no interpretation either:
/// <c>mtkclient/Library/DA/xflash/xflash_lib.py:590-598</c>.
/// <para>参考客户端请求此控制码、仅确认交换完成后就返回应答，从不解码，因此本记录同样不提供解释：
/// <c>mtkclient/Library/DA/xflash/xflash_lib.py:590-598</c>。</para>
/// </remarks>
public sealed record MtkRpmbStatusResult
{
    /// <summary>
    /// Gets the undecoded reply payload.
    /// <para>获取未经解码的应答载荷。</para>
    /// </summary>
    /// <value>The exact bytes between the status frames of the status query; may be empty.
    /// <para>状态查询两个状态帧之间的精确字节；可能为空。</para></value>
    public required byte[] RawReply { get; init; }
    /// <summary>
    /// Gets the RPMB capacity from the discovered geometry, when the family reports one.
    /// <para>获取已发现几何信息中的 RPMB 容量（若该族有上报）。</para>
    /// </summary>
    /// <value>
    /// The reported byte size of the authenticated RPMB region, or <see langword="null"/> when the selected
    /// interface's geometry carries no RPMB size field.
    /// <para>已认证 RPMB 区域的上报字节大小；所选接口的几何信息没有 RPMB 大小字段时为
    /// <see langword="null"/>。</para>
    /// </value>
    /// <remarks>
    /// For eMMC this is the third capacity field of the storage reply,
    /// <c>mtkclient/Library/DA/xflash/xflash_lib.py:518-522</c>.
    /// <para>对 eMMC 而言这是存储应答的第三个容量字段，
    /// <c>mtkclient/Library/DA/xflash/xflash_lib.py:518-522</c>。</para>
    /// </remarks>
    public long? ReportedRpmbCapacityBytes { get; init; }
    /// <summary>
    /// Gets the reply length in bytes.
    /// <para>获取应答字节长度。</para>
    /// </summary>
    /// <value>The size of <see cref="RawReply"/>. <para><see cref="RawReply"/> 的大小。</para></value>
    public int ReplyLength => RawReply.Length;
    /// <summary>
    /// Gets the reply as uppercase hexadecimal text.
    /// <para>获取大写的十六进制应答文本。</para>
    /// </summary>
    /// <value>Two hexadecimal characters per reply byte, for evidence records only.
    /// <para>每个应答字节两个十六进制字符，仅用于证据记录。</para></value>
    public string RawReplyHex => Convert.ToHexString(RawReply);
    /// <summary>
    /// Gets whether the geometry reserves a nonzero RPMB region.
    /// <para>获取几何信息是否保留了非零的 RPMB 区域。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> only when the device reported a positive RPMB capacity; this says nothing about
    /// whether an authentication key has been programmed.
    /// <para>仅当设备上报正的 RPMB 容量时为 <see langword="true"/>；这不代表认证密钥是否已写入。</para>
    /// </value>
    public bool ReportsRpmbRegion => ReportedRpmbCapacityBytes > 0;
}

/// <summary>
/// The result of a byte-addressed RPMB read through the Legacy DA framing.
/// <para>通过 Legacy DA 框架进行的字节寻址 RPMB 读取结果。</para>
/// </summary>
public sealed record MtkRpmbBlockReadResult
{
    /// <summary>
    /// Gets the byte offset of the first delivered block.
    /// <para>获取首个交付块的字节偏移。</para>
    /// </summary>
    /// <value>The validated start offset inside the RPMB partition.
    /// <para>RPMB 分区内已校验的起始偏移。</para></value>
    public required long ByteOffset { get; init; }
    /// <summary>
    /// Gets the number of bytes the agent delivered.
    /// <para>获取代理交付的字节数。</para>
    /// </summary>
    /// <value>The validated requested length; every byte passed the packet checksum check.
    /// <para>已校验的请求长度；每个字节都通过了数据包校验和检查。</para></value>
    public required int ByteLength { get; init; }
    /// <summary>
    /// Gets the RPMB capacity reported by the Legacy eMMC inventory.
    /// <para>获取 Legacy eMMC 清单上报的 RPMB 容量。</para>
    /// </summary>
    /// <value>The authenticated region's reported size in bytes.
    /// <para>已认证区域上报的字节大小。</para></value>
    public required long ReportedRpmbCapacityBytes { get; init; }
}