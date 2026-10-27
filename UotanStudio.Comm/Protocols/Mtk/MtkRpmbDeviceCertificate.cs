namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// The readable RPMB area delivered as one opaque blob.
/// <para>以单个不透明数据块交付的可读 RPMB 区域。</para>
/// </summary>
/// <remarks>
/// This client names its RPMB dump <c>rpmb.bin</c> and builds it by concatenating fixed-width
/// frames: and
/// . Neither source defines a certificate, a
/// PKCS structure or any field layout inside those bytes, so this record asserts no internal meaning:
/// it preserves the delivered bytes, the frame count and the capacity the device reported. Callers that
/// expect a device certificate must parse the frames themselves and state their own interpretation.
/// <para>本客户端将其 RPMB 转储命名为 <c>rpmb.bin</c>，由定宽帧拼接而成：
/// 与
/// 。两处源码都没有定义证书、PKCS 结构或任何
/// 内部字段布局，因此本记录不断言任何内部含义：它只保留交付的字节、帧数量与设备上报的容量。
/// 期望设备证书的调用方必须自行解析帧并声明自己的解释。</para>
/// </remarks>
public sealed record MtkRpmbDeviceBlob
{
    /// <summary>
    /// Gets the interface that delivered the frames.
    /// <para>获取交付这些帧的接口。</para>
    /// </summary>
    /// <value>The RPMB target derived from the discovered geometry.
    /// <para>由已发现几何信息推导的 RPMB 目标。</para></value>
    public required MtkRpmbTarget Target { get; init; }
    /// <summary>
    /// Gets the delivered RPMB bytes.
    /// <para>获取交付的 RPMB 字节。</para>
    /// </summary>
    /// <value>
    /// Every successfully read frame concatenated in address order, with no trimming, re-parsing or
    /// authentication applied.
    /// <para>成功读取的每个帧按地址顺序拼接，未经裁剪、重新解析或认证处理。</para>
    /// </value>
    public required byte[] RawBytes { get; init; }
    /// <summary>
    /// Gets the number of frames the read addressed.
    /// <para>获取本次读取寻址的帧数量。</para>
    /// </summary>
    /// <value>One per frame result, including a frame the device refused.
    /// <para>每个帧结果计一个，包含被设备拒绝的帧。</para></value>
    public required int FrameCount { get; init; }
    /// <summary>
    /// Gets the RPMB capacity the device geometry reported.
    /// <para>获取设备几何信息上报的 RPMB 容量。</para>
    /// </summary>
    /// <value>The reported byte size that bounded the dump.
    /// <para>用于限制本次转储的上报字节大小。</para></value>
    public required long ReportedRpmbCapacityBytes { get; init; }
    /// <summary>
    /// Gets whether the whole reported area was delivered.
    /// <para>获取是否交付了完整的上报区域。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> only when <see cref="RawBytes"/> covers <see cref="ReportedRpmbCapacityBytes"/>;
    /// a device refusal mid-area leaves the blob short and this value <see langword="false"/>.
    /// <para>仅当 <see cref="RawBytes"/> 覆盖 <see cref="ReportedRpmbCapacityBytes"/> 时为
    /// <see langword="true"/>；交换途中被设备拒绝会使数据块短缺且此值为 <see langword="false"/>。</para>
    /// </value>
    public bool IsComplete => RawBytes.LongLength == ReportedRpmbCapacityBytes;
}