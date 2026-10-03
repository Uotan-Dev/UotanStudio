using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk.Security;

/// <summary>The chip identity reported by the download agent device-control query.
/// <para>下载代理设备控制查询上报的芯片身份。</para></summary>
/// <remarks>
/// The reference client sends the get-chip-id control code and decodes the reply as five
/// little-endian 16-bit words in this order (Library/DA/xflash/xflash_lib.py:448-471 with
/// Library/DA/xflash/xflash_param.py:53). The raw reply bytes are preserved so a later revision of
/// the agent can be compared byte for byte instead of re-derived from the decoded fields.
/// <para>参考客户端发送取得芯片标识的控制码，并把回复按五个小端 16 位字依此顺序解码
/// （Library/DA/xflash/xflash_lib.py:448-471，控制码见 Library/DA/xflash/xflash_param.py:53）。
/// 保留原始回复字节，便于在代理版本变更时逐字节比较，而不是仅从解码字段反推。</para>
/// </remarks>
/// <param name="HardwareCode">The raw hardware code word.
/// <para>原始硬件代码字。</para></param>
/// <param name="HardwareSubCode">The raw hardware subcode word.
/// <para>原始硬件子代码字。</para></param>
/// <param name="HardwareVersion">The raw hardware revision word.
/// <para>原始硬件修订字。</para></param>
/// <param name="SoftwareVersion">The raw software revision word.
/// <para>原始软件修订字。</para></param>
/// <param name="ChipEvolution">The raw chip evolution word.
/// <para>原始芯片演进字。</para></param>
/// <param name="ReplyHex">The exact reply bytes as uppercase hex.
/// <para>回复字节的大写十六进制表示。</para></param>
public sealed record MtkXFlashChipId(
    ushort HardwareCode,
    ushort HardwareSubCode,
    ushort HardwareVersion,
    ushort SoftwareVersion,
    ushort ChipEvolution,
    string ReplyHex)
{
    /// <summary>Decodes a chip-identity reply.
    /// <para>解码芯片标识回复。</para></summary>
    /// <param name="reply">The bytes returned by the device-control query.
    /// <para>设备控制查询返回的字节。</para></param>
    /// <returns>The decoded identity with its raw reply preserved.
    /// <para>解码后的身份信息，并保留原始回复。</para></returns>
    /// <exception cref="MtkProtocolException"><paramref name="reply"/> is not exactly the ten bytes
    /// the documented layout requires. <para><paramref name="reply"/> 不是已记载布局要求的恰好十个字节。</para></exception>
    public static MtkXFlashChipId Decode(ReadOnlySpan<byte> reply)
    {
        if (reply.Length != 5 * 2)
        {
            throw new MtkProtocolException(
                $"The chip-identity reply must be exactly {5 * 2} bytes; the agent returned {reply.Length}.");
        }

        return new MtkXFlashChipId(
            BinaryPrimitives.ReadUInt16LittleEndian(reply),
            BinaryPrimitives.ReadUInt16LittleEndian(reply[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(reply[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(reply[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(reply[8..]),
            Convert.ToHexString(reply));
    }
}

/// <summary>A device-control query whose reply the reference client consumes as opaque bytes.
/// <para>参考客户端把回复当作不透明字节消费的器件控制查询。</para></summary>
/// <remarks>
/// Both codes are exercised by the reference client and neither reply is decoded there
/// (Library/DA/xflash/xflash_lib.py:590-596 and :687-695), so the bytes are returned unchanged and
/// no claim is made about their meaning.
/// <para>参考客户端会实际使用这两个控制码，且都不解码其回复
/// （Library/DA/xflash/xflash_lib.py:590-596 与 :687-695），因此原样返回字节并不对其含义作出结论。</para>
/// </remarks>
public enum MtkXFlashRawControlCode
{
    /// <summary>The replay-protected-memory-block state query (Library/DA/xflash/xflash_param.py:56).
    /// <para>重放保护块状态查询（Library/DA/xflash/xflash_param.py:56）。</para></summary>
    ReplayProtectedAreaStatus = 0x040010,
    /// <summary>The hardware identifier query (Library/DA/xflash/xflash_param.py:60).
    /// <para>硬件标识符查询（Library/DA/xflash/xflash_param.py:60）。</para></summary>
    HardwareIdentifier = 0x040014
}

/// <summary>A security control code the reference client declares but never exercises.
/// <para>参考客户端声明但从未实际使用的安全控制码。</para></summary>
/// <remarks>
/// The values are catalogued at Library/DA/xflash/xflash_param.py:17-20 and :65. No call site
/// exists anywhere in that checkout, so the request-parameter layout, the reply layout and the
/// side effects are all unknown; the client therefore refuses to build a frame for them and
/// reports why rather than guessing a payload.
/// <para>这些取值记载于 Library/DA/xflash/xflash_param.py:17-20 与 :65。该代码库中不存在任何调用点，
/// 因此请求参数布局、回复布局与副作用都未知；客户端拒绝为它们构造帧，并说明原因而不是猜测载荷。</para>
/// </remarks>
public enum MtkXFlashUndocumentedSecurityControlCode
{
    /// <summary>Declared at Library/DA/xflash/xflash_param.py:17.
    /// <para>声明于 Library/DA/xflash/xflash_param.py:17。</para></summary>
    ReadOneTimeProgramZone = 0x01000C,
    /// <summary>Declared at Library/DA/xflash/xflash_param.py:18.
    /// <para>声明于 Library/DA/xflash/xflash_param.py:18。</para></summary>
    WriteOneTimeProgramZone = 0x01000D,
    /// <summary>Declared at Library/DA/xflash/xflash_param.py:19.
    /// <para>声明于 Library/DA/xflash/xflash_param.py:19。</para></summary>
    WriteElectronicFuse = 0x01000E,
    /// <summary>Declared at Library/DA/xflash/xflash_param.py:20.
    /// <para>声明于 Library/DA/xflash/xflash_param.py:20。</para></summary>
    ReadElectronicFuse = 0x01000F,
    /// <summary>Declared at Library/DA/xflash/xflash_param.py:65.
    /// <para>声明于 Library/DA/xflash/xflash_param.py:65。</para></summary>
    ActivateOneTimeProgramZoneLock = 0x080003
}

/// <summary>Reasons for refusing an undocumented security control code.
/// <para>拒绝未记载安全控制码的原因。</para></summary>
public static class MtkXFlashUndocumentedSecurityControl
{
    /// <summary>Describes why no frame is built for the selected control code.
    /// <para>说明为何不为所选控制码构造帧。</para></summary>
    /// <param name="code">The refused control code. 被拒绝的控制码。</param>
    /// <returns>A localization-neutral explanation naming the declaring source line.
    /// <para>与本地化无关的说明，指出其声明所在的源码行。</para></returns>
    public static string Explain(MtkXFlashUndocumentedSecurityControlCode code) =>
        $"The control code {code} (0x{(uint)code:X8}) is only declared by the reference client and has no " +
        "documented request or reply layout; sending it would require inventing bytes, so the request was refused.";
}