using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Bootrom;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Bootrom;

/// <summary>
/// Independent golden byte vectors for the rockusb CBW/CSW wire format. Every expected byte in
/// this class is a hardcoded literal copied off the upstream packed structures instead of the
/// <see cref="RkdlFrame"/> constants, so a wrong production constant cannot validate itself.
/// Sources: rockchip-linux/rkdeveloptool <c>RKComm.h</c> (<c>#pragma pack(1)</c> <c>CBW</c> /
/// <c>CBWCB</c> / <c>CSW</c>, <c>CBW_SIGN 0x43425355 /* "USBC" */</c>,
/// <c>CSW_SIGN 0x53425355 /* "USBS" */</c>, <c>UFI_CHECK_SIGN</c>); u-boot
/// <c>arch/arm/include/asm/arch-rockchip/f_rockusb.h</c>
/// (<c>__le32 signature; /* Contains 'USBC' */</c>,
/// <c>USB_BULK_CB_SIG 0x43425355 /* Spells out USBC */</c>,
/// <c>USB_BULK_CS_SIG 0x53425355 /* Spells out USBS */</c>, <c>USB_BULK_CB_WRAP_LEN 31</c>,
/// <c>USB_BULK_CS_WRAP_LEN 13</c>) and <c>f_rockusb.c</c>
/// (<c>csw-&gt;signature = cpu_to_le32(USB_BULK_CS_SIG)</c>,
/// <c>get_unaligned_be32(&amp;cbw-&gt;CDB[2])</c>, <c>get_unaligned_be16(&amp;cbw-&gt;CDB[7])</c>);
/// linux-rockchip/rkflashtool <c>rkflashtool.c</c> (<c>memcpy(cmd, "USBC", 4)</c>,
/// <c>SETBE32(cmd+17, offset)</c>, <c>SETBE16(cmd+22, nsectors)</c>).
/// <para>rockusb CBW/CSW 线上格式的独立黄金字节向量。本类中每个期望字节都是直接从上游紧凑结构抄下的
/// 硬编码字面量，而不是 <see cref="RkdlFrame"/> 常量，因此错误的生产常量无法自我验证。来源：
/// rockchip-linux/rkdeveloptool 的 <c>RKComm.h</c>（<c>#pragma pack(1)</c> 的 <c>CBW</c> /
/// <c>CBWCB</c> / <c>CSW</c>，<c>CBW_SIGN 0x43425355 /* "USBC" */</c>、
/// <c>CSW_SIGN 0x53425355 /* "USBS" */</c>、<c>UFI_CHECK_SIGN</c>）；u-boot 的
/// <c>arch/arm/include/asm/arch-rockchip/f_rockusb.h</c>
/// （<c>__le32 signature; /* Contains 'USBC' */</c>、
/// <c>USB_BULK_CB_SIG 0x43425355 /* Spells out USBC */</c>、
/// <c>USB_BULK_CS_SIG 0x53425355 /* Spells out USBS */</c>、<c>USB_BULK_CB_WRAP_LEN 31</c>、
/// <c>USB_BULK_CS_WRAP_LEN 13</c>）与 <c>f_rockusb.c</c>
/// （<c>csw-&gt;signature = cpu_to_le32(USB_BULK_CS_SIG)</c>、
/// <c>get_unaligned_be32(&amp;cbw-&gt;CDB[2])</c>、<c>get_unaligned_be16(&amp;cbw-&gt;CDB[7])</c>）；
/// linux-rockchip/rkflashtool 的 <c>rkflashtool.c</c>（<c>memcpy(cmd, "USBC", 4)</c>、
/// <c>SETBE32(cmd+17, offset)</c>、<c>SETBE16(cmd+22, nsectors)</c>）。</para>
/// </summary>
public class RkdlFrameWireVectorTests
{
    /// <summary>
    /// CBW signature bytes exactly as they appear on the wire. A little-endian host storing the
    /// DWORD <c>0x43425355</c> emits <c>55 53 42 43</c>, which is ASCII "USBC"; rkflashtool writes
    /// the same four bytes with <c>memcpy(cmd, "USBC", 4)</c>.
    /// <para>CBW 签名在线上的确切字节。小端主机存储 DWORD <c>0x43425355</c> 时发出
    /// <c>55 53 42 43</c>，即 ASCII "USBC"；rkflashtool 用 <c>memcpy(cmd, "USBC", 4)</c>
    /// 写出同样的四个字节。</para>
    /// </summary>
    private static readonly byte[] UpstreamCbwSignatureWireBytes = [0x55, 0x53, 0x42, 0x43];

    /// <summary>
    /// CSW signature bytes exactly as the device emits them: u-boot rockusb assigns
    /// <c>csw-&gt;signature = cpu_to_le32(0x53425355)</c>, giving <c>55 53 42 53</c> = ASCII "USBS".
    /// <para>设备发出的 CSW 签名的确切字节：u-boot rockusb 赋值
    /// <c>csw-&gt;signature = cpu_to_le32(0x53425355)</c>，得到 <c>55 53 42 53</c> = ASCII "USBS"。</para>
    /// </summary>
    private static readonly byte[] UpstreamCswSignatureWireBytes = [0x55, 0x53, 0x42, 0x53];

    [Fact]
    public void UpstreamSignatureLiterals_SpellUsbcAndUsbs()
    {
        Assert.Equal("USBC", Encoding.ASCII.GetString(UpstreamCbwSignatureWireBytes));
        Assert.Equal("USBS", Encoding.ASCII.GetString(UpstreamCswSignatureWireBytes));
        Assert.Equal(0x43425355u, BinaryPrimitives.ReadUInt32LittleEndian(UpstreamCbwSignatureWireBytes));
        Assert.Equal(0x53425355u, BinaryPrimitives.ReadUInt32LittleEndian(UpstreamCswSignatureWireBytes));
    }

    /// <summary>
    /// Golden vector: the production CBW signature must equal the upstream wire bytes. The
    /// rkdeveloptool <c>CBW_SIGN 0x43425355 /* "USBC" */</c> DWORD is stored little-endian, so a
    /// little-endian host transmits <c>55 53 42 43</c>, and the USB Mass Storage Bulk-Only
    /// Transport specification requires dCBWSignature = 0x43425355 with the least significant
    /// byte first. The expected bytes below are literals taken off the upstream structures, not
    /// the production constant, so the assertion cannot be satisfied by a wrong constant.
    /// <para>黄金向量：生产的 CBW 签名必须等于上游线上字节。rkdeveloptool 的
    /// <c>CBW_SIGN 0x43425355 /* "USBC" */</c> DWORD 按小端存储，因此小端主机传输
    /// <c>55 53 42 43</c>；USB 大容量存储 Bulk-Only 传输规范要求 dCBWSignature = 0x43425355
    /// 且最低有效字节先到。下面的期望字节是直接从上游结构抄下的字面量，而不是生产常量，
    /// 因此错误的常量无法满足该断言。</para>
    /// </summary>
    [Fact]
    public void CbwSignature_MatchesUpstreamUsbcWireBytes()
    {
        var production = RkdlFrame.CbwSignature;

        Assert.Equal(UpstreamCbwSignatureWireBytes, production);
        Assert.Equal("USBC", Encoding.ASCII.GetString(production));
        Assert.Equal(0x43425355u, BinaryPrimitives.ReadUInt32LittleEndian(production));
    }

    /// <summary>
    /// Golden vector: the production CSW signature must equal the upstream wire bytes.
    /// <c>RkdlCsw.TryParse</c> compares a device response against these bytes, and a real rockusb
    /// device emits <c>csw-&gt;signature = cpu_to_le32(0x53425355)</c> = <c>55 53 42 53</c> =
    /// ASCII "USBS" (rkdeveloptool <c>CSW_SIGN 0x53425355 /* "USBS" */</c>,
    /// <c>UFI_CHECK_SIGN</c>). The expected bytes are upstream literals, so a reversed production
    /// constant fails this test instead of matching itself.
    /// <para>黄金向量：生产的 CSW 签名必须等于上游线上字节。<c>RkdlCsw.TryParse</c> 用这些字节比较
    /// 设备响应，而真实 rockusb 设备发出
    /// <c>csw-&gt;signature = cpu_to_le32(0x53425355)</c> = <c>55 53 42 53</c> = ASCII "USBS"
    /// （rkdeveloptool 的 <c>CSW_SIGN 0x53425355 /* "USBS" */</c> 与
    /// <c>UFI_CHECK_SIGN</c>）。期望字节是上游字面量，因此逆序的生产常量会让本测试失败，
    /// 而不是与自己自洽。</para>
    /// </summary>
    [Fact]
    public void CswSignature_MatchesUpstreamUsbsWireBytes()
    {
        var production = RkdlFrame.CswSignature;

        Assert.Equal(UpstreamCswSignatureWireBytes, production);
        Assert.Equal("USBS", Encoding.ASCII.GetString(production));
        Assert.Equal(0x53425355u, BinaryPrimitives.ReadUInt32LittleEndian(production));
    }

    [Fact]
    public void CbwSerialization_HeadFourBytes_MatchUpstreamUsbcGoldenVector()
    {
        var cbw = new RkdlCbw((RkdlCommand)0x15, flags: 0x00, transferLength: 512, cbwcbLength: 0x0A);

        var bytes = cbw.ToBytes();

        Assert.Equal(UpstreamCbwSignatureWireBytes, bytes[..4]);
        Assert.Equal("USBC", Encoding.ASCII.GetString(bytes[..4]));
    }

    [Fact]
    public void CbwSerialization_WriteLbaGoldenVector_MatchesUpstreamFieldOffsets()
    {
        var cbw = new RkdlCbw((RkdlCommand)0x15, flags: 0x00, transferLength: 1024, cbwcbLength: 0x0A,
            subCode: 0x00, address: 0x00001234, sectorLength: 2);

        var bytes = cbw.ToBytes();

        byte[] expectedAfterTag =
        [
            0x00, 0x04, 0x00, 0x00,
            0x00,
            0x00,
            0x0A,
            0x15,
            0x00,
            0x00, 0x00, 0x12, 0x34,
            0x00,
            0x00, 0x02,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];
        Assert.Equal(31, bytes.Length);
        Assert.Equal(cbw.Tag, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(expectedAfterTag, bytes[8..]);
    }

    [Fact]
    public void CbwSerialization_ReadFlashInfoGoldenVector_UsesDirectionInAndShortCbwcb()
    {
        var cbw = new RkdlCbw((RkdlCommand)0x1A, flags: 0x80, transferLength: 11, cbwcbLength: 0x06);

        var bytes = cbw.ToBytes();

        byte[] expectedAfterTransferLength =
        [
            0x80,
            0x00,
            0x06,
            0x1A,
            0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00,
            0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(expectedAfterTransferLength, bytes[12..]);
    }

    /// <summary>
    /// A CSW built from the upstream literal <c>55 53 42 53</c> must be accepted by the production
    /// parser: this is the byte sequence a real rockusb device returns, so accepting it is the
    /// contract every CBW/CSW command depends on.
    /// <para>用上游字面量 <c>55 53 42 53</c> 构建的 CSW 必须被生产解析器接受：这是真实 rockusb 设备
    /// 返回的字节序列，接受它是每条 CBW/CSW 命令所依赖的契约。</para>
    /// </summary>
    [Fact]
    public void CswParse_UpstreamUsbsGoldenVector_IsAcceptedByProductionParser()
    {
        var upstreamCsw = new byte[13];
        UpstreamCswSignatureWireBytes.CopyTo(upstreamCsw, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(upstreamCsw.AsSpan(4), 0xDEADBEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(upstreamCsw.AsSpan(8), 0);
        upstreamCsw[12] = 0;

        Assert.True(RkdlCsw.TryParse(upstreamCsw, 0xDEADBEEF, out var csw));
        Assert.Equal(0xDEADBEEFu, csw!.Tag);
        Assert.True(csw.IsSuccess);
    }

    /// <summary>
    /// A CSW whose signature is the upstream wire bytes reversed (<c>53 42 53 55</c>, spelling
    /// "SBSU") must be rejected, so a parser that compares against a byte-reversed constant cannot
    /// pass while the upstream vector does.
    /// <para>签名是上游线上字节逆序（<c>53 42 53 55</c>，拼出 "SBSU"）的 CSW 必须被拒绝，因此一个
    /// 与字节逆序常量比较的解析器不可能在通过上游向量的同时还通过本用例。</para>
    /// </summary>
    [Fact]
    public void CswParse_ByteReversedSignature_IsRejected()
    {
        var reversedCsw = new byte[13];
        UpstreamCswSignatureWireBytes.Reverse().ToArray().CopyTo(reversedCsw, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(reversedCsw.AsSpan(4), 0x01020304);
        reversedCsw[12] = 0;

        Assert.False(RkdlCsw.TryParse(reversedCsw, 0x01020304, out _));
    }

    [Fact]
    public void CswParse_ProductionSignatureGoldenVector_ParsesTagResidueAndStatus()
    {
        var productionCsw = new byte[13];
        RkdlFrame.CswSignature.CopyTo(productionCsw, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(productionCsw.AsSpan(4), 0x01020304);
        BinaryPrimitives.WriteUInt32LittleEndian(productionCsw.AsSpan(8), 0x00020001);
        productionCsw[12] = 1;

        Assert.Equal(UpstreamCswSignatureWireBytes, productionCsw[..4]);
        Assert.True(RkdlCsw.TryParse(productionCsw, 0x01020304, out var csw));
        Assert.Equal(0x01020304u, csw!.Tag);
        Assert.Equal(0x00020001u, csw.DataResidue);
        Assert.Equal((byte)1, csw.Status);
        Assert.False(csw.IsSuccess);
    }

    [Fact]
    public void CswParse_MismatchedTagGoldenVector_IsRejected()
    {
        var productionCsw = new byte[13];
        RkdlFrame.CswSignature.CopyTo(productionCsw, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(productionCsw.AsSpan(4), 0x01020304);
        productionCsw[12] = 0;

        Assert.False(RkdlCsw.TryParse(productionCsw, 0x01020305, out _));
    }

    [Fact]
    public void RockchipCrc32_UpstreamGoldenVectors_MatchReferenceTableImplementation()
    {
        Assert.Equal(0x889A9615u, RkBootImage.RockchipCrc32("123456789"u8.ToArray()));
        Assert.Equal(0xA862DB20u, RkBootImage.RockchipCrc32("a"u8.ToArray()));
        Assert.Equal(0x3A06543Eu, RkBootImage.RockchipCrc32("abc"u8.ToArray()));
        Assert.Equal(0x00000000u, RkBootImage.RockchipCrc32(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0x00000000u, RkBootImage.RockchipCrc32(new byte[1024]));
    }

    [Fact]
    public void Crc16Ccitt_GoldenVectors_MatchCcittFalseCheckValue()
    {
        byte[] payload =
            [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10];

        Assert.Equal((ushort)0x29B1, RkdlCrc.Crc16Ccitt("123456789"u8.ToArray()));
        Assert.Equal((ushort)0xFFFF, RkdlCrc.Crc16Ccitt(ReadOnlySpan<byte>.Empty));
        Assert.Equal((ushort)0x0FEF, RkdlCrc.Crc16Ccitt(payload));
    }

    [Fact]
    public void Crc32_IeeeGoldenVector_MatchesStandardCheckValue()
    {
        Assert.Equal(0xCBF43926u, RkdlCrc.Crc32("123456789"u8.ToArray()));
    }
}