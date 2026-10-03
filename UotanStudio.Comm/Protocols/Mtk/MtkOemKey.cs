namespace UotanStudio.Comm.Protocols.Mtk.Security;

/// <summary>The key classes the evidence shows a host can hand to a download agent.
/// <para>证据显示可由主机交给下载代理的密钥类别。</para></summary>
/// <remarks>
/// Only two classes have a documented delivery path: the eight-doubleword software one-time-program
/// key written through the SEJ register window (Library/Hardware/hwcrypto_sej.py:66-73 and :613-622)
/// and the replay-protected-block key submitted with the set-rpmb-key control code
/// (Library/DA/xflash/extension/xflash.py:442-447 with Library/DA/xflash/xflash_param.py:26).
/// Other keys the reference client mentions, such as the mirror or second-generation
/// replay-protected keys and the secure-init or MKJ registers
/// (Library/DA/xflash/extension/xflash.py:718-743, Library/Hardware/hwcrypto_sej.py:74-77 and :623-626),
/// are only printed or saved on the host, or are explicitly commented out at their call site, so no
/// class is offered for them.
/// <para>只有两类密钥有记载的投递路径：通过 SEJ 寄存器窗口写入的 8 双字软件一次性编程密钥
/// （Library/Hardware/hwcrypto_sej.py:66-73 与 :613-622），以及用设置重放保护密钥控制码提交的
/// 重放保护块密钥（Library/DA/xflash/extension/xflash.py:442-447，控制码见
/// Library/DA/xflash/xflash_param.py:26）。参考客户端提到的其他密钥，例如镜像或第二代重放保护密钥、
/// 安全初始化与 MKJ 寄存器（Library/DA/xflash/extension/xflash.py:718-743，
/// Library/Hardware/hwcrypto_sej.py:74-77 与 :623-626），只在主机侧打印或保存，或在调用处被显式注释掉，
/// 因此不为它们提供任何类别。</para>
/// </remarks>
public enum MtkOemKeyKind
{
    /// <summary>The eight-doubleword software one-time-program key loaded into the SEJ register window.
    /// <para>加载到 SEJ 寄存器窗口的 8 双字软件一次性编程密钥。</para></summary>
    SoftwareOneTimeProgramKey,
    /// <summary>The replay-protected-block key submitted to the download agent.
    /// <para>提交给下载代理的重放保护块密钥。</para></summary>
    ReplayProtectedAreaKey
}

/// <summary>A bounded key-programming request for one documented key class.
/// <para>针对一类已记载密钥的有界密钥编程请求。</para></summary>
/// <remarks>
/// The payload length is fixed at 32 bytes because that is the only size the evidence carries: the
/// software one-time-program key is 32 bytes read straight from the preloader image
/// (Library/DA/xflash/extension/xflash.py:466-474) and the replay-protected key produced by the
/// documented generator is 32 bytes too (Library/Hardware/hwcrypto_sej.py:713). A key is never
/// padded, truncated or derived here.
/// <para>载荷长度固定为 32 字节，因为这只是证据携带的大小：软件一次性编程密钥为 32 字节，直接从
/// preloader 镜像读出（Library/DA/xflash/extension/xflash.py:466-474），已记载生成器产出的重放保护密钥
/// 也是 32 字节（Library/Hardware/hwcrypto_sej.py:713）。此处绝不补齐、截断或推导密钥。</para>
/// </remarks>
/// <param name="Kind">The key class being programmed. 要编程的密钥类别。</param>
/// <param name="KeyHex">The exact 32 key bytes as hexadecimal text.
/// <para>32 个密钥字节的十六进制文本。</para></param>
/// <param name="SejBaseAddress">The SEJ peripheral base for a software one-time-program key;
/// <see langword="null"/> for the replay-protected class, which needs no host-side address.
/// <para>软件一次性编程密钥的 SEJ 外设基址；重放保护类别不需要主机侧地址时为 <see langword="null"/>。</para></param>
public sealed record MtkOemKeyRequest(MtkOemKeyKind Kind, string KeyHex, uint? SejBaseAddress)
{
    /// <summary>The only key length the documented classes carry, in bytes.
    /// <para>已记载类别携带的唯一密钥长度（字节）。</para></summary>
    public const int KeyLength = 32;

    /// <summary>Decodes and validates the request payload.
    /// <para>解码并校验请求载荷。</para></summary>
    /// <returns>Exactly <see cref="KeyLength"/> key bytes.
    /// <para>恰好 <see cref="KeyLength"/> 个密钥字节。</para></returns>
    /// <exception cref="ArgumentException">The <see cref="KeyHex"/> payload is missing, not
    /// hexadecimal, or not exactly <see cref="KeyLength"/> bytes; a class-incompatible or absent
    /// <see cref="SejBaseAddress"/> is rejected the same way instead of defaulting it.
    /// <para><see cref="KeyHex"/> 载荷缺失、非十六进制或长度不等于 <see cref="KeyLength"/> 字节；
    /// 与类别不匹配或缺失的 <see cref="SejBaseAddress"/> 同样被拒绝，而不会被默认填充。</para></exception>
    public byte[] DecodeKey()
    {
        if (string.IsNullOrWhiteSpace(KeyHex))
        {
            throw new ArgumentException("A key payload is required.", nameof(KeyHex));
        }

        byte[] key;
        try
        {
            key = Convert.FromHexString(KeyHex);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The key payload must be hexadecimal text.", nameof(KeyHex), exception);
        }

        if (key.Length != KeyLength)
        {
            throw new ArgumentException(
                $"The documented key classes carry exactly {KeyLength} bytes; the request has {key.Length}.",
                nameof(KeyHex));
        }

        switch (Kind)
        {
            case MtkOemKeyKind.SoftwareOneTimeProgramKey:
                {
                    var baseAddress = SejBaseAddress ?? throw new ArgumentException(
                        "The software one-time-program key class requires the SEJ peripheral base address.",
                        nameof(SejBaseAddress));
                    if ((long)baseAddress + MtkOemKeyRegisterWindow.Offset + KeyLength > uint.MaxValue)
                    {
                        throw new ArgumentException(
                            "The SEJ register window would overflow the 32-bit address space.", nameof(SejBaseAddress));
                    }

                    return key;
                }
            case MtkOemKeyKind.ReplayProtectedAreaKey when SejBaseAddress is null:
                return key;
            case MtkOemKeyKind.ReplayProtectedAreaKey:
                throw new ArgumentException(
                    "The replay-protected key class is delivered by control code and takes no SEJ base address.",
                    nameof(SejBaseAddress));
            default:
                throw new ArgumentException($"The key class {Kind} is not documented by the evidence.", nameof(Kind));
        }
    }
}

/// <summary>The SEJ register window used for the documented software one-time-program key.
/// <para>用于已记载软件一次性编程密钥的 SEJ 寄存器窗口。</para></summary>
/// <remarks>
/// The offsets are relative to the SEJ peripheral base and follow
/// Library/Hardware/hwcrypto_sej.py:66-73; the eight-slot write is Library/Hardware/hwcrypto_sej.py:613-622.
/// <para>这些偏移相对于 SEJ 外设基址，遵循 Library/Hardware/hwcrypto_sej.py:66-73；
/// 写入 8 个槽位的过程见 Library/Hardware/hwcrypto_sej.py:613-622。</para>
/// </remarks>
public static class MtkOemKeyRegisterWindow
{
    /// <summary>The offset of the first software one-time-program register from the SEJ base
    /// (Library/Hardware/hwcrypto_sej.py:66).
    /// <para>首个软件一次性编程寄存器相对 SEJ 基址的偏移（Library/Hardware/hwcrypto_sej.py:66）。</para></summary>
    public const uint Offset = 0x60;

    /// <summary>The number of documented register slots, each one doubleword.
    /// <para>已记载寄存器槽数量，每槽一个双字。</para></summary>
    public const int SlotCount = 8;

    /// <summary>The offsets of the eight documented software one-time-program registers from the SEJ
    /// base, in write order (Library/Hardware/hwcrypto_sej.py:66-73).
    /// <para>八个已记载软件一次性编程寄存器相对 SEJ 基址的偏移，按写入顺序排列
    /// （Library/Hardware/hwcrypto_sej.py:66-73）。</para></summary>
    /// <value>
    /// Eight read-only offsets from 0x60 through 0x7C.
    /// <para>从 0x60 到 0x7C 的八个只读偏移。</para>
    /// </value>
    public static IReadOnlyList<uint> Offsets { get; } =
        [Offset, 0x64, 0x68, 0x6C, 0x70, 0x74, 0x78, 0x7C];

    /// <summary>Explains why the adjacent secure-init and MKJ registers are not offered.
    /// <para>说明为何不提供相邻的安全初始化与 MKJ 寄存器。</para></summary>
    /// <returns>A localization-neutral refusal reason naming the source lines.
    /// <para>注明源码行的、与本地化无关的拒绝原因。</para></returns>
    public static string ExplainReservedRegisters() =>
        "The HACC_SECINIT0-2 and HACC_MKJ offsets exist in the reference register map " +
        "(Library/Hardware/hwcrypto_sej.py:74-77) but every write of them is commented out at the " +
        "only call site (Library/Hardware/hwcrypto_sej.py:623-626), so no key class is offered for them.";
}

/// <summary>Locates the software one-time-program key inside a preloader image.
/// <para>在 preloader 镜像中定位软件一次性编程密钥。</para></summary>
/// <remarks>
/// The reference client searches the preloader bytes for the five-byte marker
/// <c>4D 4D 4D 01 30</c> and reads 32 bytes from the marker position plus 0xC, falling back to an
/// all-zero key when the marker is absent (Library/DA/xflash/extension/xflash.py:466-474, repeated at
/// Library/DA/legacy/extension/legacy.py:142-150 and Library/DA/xml/extension/v6.py:414-422). This
/// reader keeps the marker and the window but reports "not found" instead of substituting zeros,
/// because a zero key would silently change the security configuration being computed.
/// <para>参考客户端在 preloader 字节中查找五字节标记 <c>4D 4D 4D 01 30</c>，并从标记位置加 0xC 处读取
/// 32 字节；找不到标记时回退为全零密钥（Library/DA/xflash/extension/xflash.py:466-474，
/// Library/DA/legacy/extension/legacy.py:142-150 与 Library/DA/xml/extension/v6.py:414-422 同样如此）。
/// 本读取器保留标记与窗口位置，但把“未找到”上报而不替换成零密钥，因为零密钥会悄悄改变所计算的安全配置。</para>
/// </remarks>
public static class MtkOemKeyPreloaderCarrier
{
    private static ReadOnlySpan<byte> Marker => [0x4D, 0x4D, 0x4D, 0x01, 0x30];
    private const int WindowOffset = 0xC;

    /// <summary>Reads the key window from the first marker occurrence.
    /// <para>从首个标记出现处读取密钥窗口。</para></summary>
    /// <param name="preloader">The exact preloader bytes. preloader 的原始字节。</param>
    /// <param name="key">Receives the 32 key bytes when the marker and window are present.
    /// <para>标记与窗口都存在时接收 32 个密钥字节。</para></param>
    /// <param name="markerOffset">Receives the zero-based marker position.
    /// <para>接收从零开始的标记位置。</para></param>
    /// <returns><see langword="true"/> when a complete key window was read.
    /// <para>读到完整密钥窗口时为 <see langword="true"/>。</para></returns>
    public static bool TryExtract(ReadOnlySpan<byte> preloader, out byte[]? key, out int markerOffset)
    {
        key = null;
        markerOffset = preloader.IndexOf(Marker);
        if (markerOffset < 0)
        {
            return false;
        }

        var start = checked(markerOffset + WindowOffset);
        if (checked(start + MtkOemKeyRequest.KeyLength) > preloader.Length)
        {
            return false;
        }

        key = preloader.Slice(start, MtkOemKeyRequest.KeyLength).ToArray();
        return true;
    }
}

/// <summary>The receipt for one submitted key class.
/// <para>一次密钥类别提交的回执。</para></summary>
/// <remarks>
/// The receipt names the class, digests rather than repeats the key material, and counts what was
/// sent. Only the replay-protected class has a documented echo, and the reference client treats a
/// mismatching echo as a failed program rather than a success
/// (Library/DA/xflash/extension/xflash.py:442-447); the register-window class has no echo step at
/// all, so no verification is claimed for it.
/// <para>回执写明类别、以摘要而非原文表示密钥材料，并统计发送内容。只有重放保护类别有记载的回显，且参考
/// 客户端把回显不一致当作编程失败而非成功
/// （Library/DA/xflash/extension/xflash.py:442-447）；寄存器窗口类别完全没有回显步骤，因此不为它声称任何
/// 校验。</para>
/// </remarks>
/// <param name="Kind">The key class that was submitted. 被提交的密钥类别。</param>
/// <param name="KeySha256Hex">The SHA-256 digest of the key bytes that were submitted.
/// <para>被提交密钥字节的 SHA-256 摘要。</para></param>
/// <param name="SubmittedUnits">How many register writes or key frames were submitted.
/// <para>提交的寄存器写入或密钥帧数量。</para></param>
/// <param name="EchoHex">The exact echoed key bytes for the replay-protected class, otherwise
/// <see langword="null"/>. <para>重放保护类别回显的精确密钥字节，其他情况为 <see langword="null"/>。</para></param>
public sealed record MtkOemKeyReceipt(
    MtkOemKeyKind Kind,
    string KeySha256Hex,
    int SubmittedUnits,
    string? EchoHex);