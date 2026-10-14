using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Hash.Crc16CcittFalse;
using UotanStudio.Hash.RockchipCrc32;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Bootrom;

/// <summary>
/// Independent golden byte vectors for the rockusb CBW/CSW wire format. Expected bytes are
/// hardcoded literals, so an incorrect production constant cannot validate itself.
/// <para>rockusb CBW/CSW 线上格式的独立黄金字节向量。期望字节都是硬编码字面量，因此错误的生产常量无法自我验证。</para>
/// </summary>
public class RkdlFrameWireVectorTests
{
    /// <summary>
    /// CBW signature bytes exactly as they appear on the wire. A little-endian host storing
    /// <c>0x43425355</c> emits <c>55 53 42 43</c>, which is the ASCII text "USBC".
    /// <para>CBW 签名在线上的确切字节。小端主机存储 <c>0x43425355</c> 时发出 <c>55 53 42 43</c>，即 ASCII 文本 "USBC"。</para>
    /// </summary>
    private static readonly byte[] UpstreamCbwSignatureWireBytes = [0x55, 0x53, 0x42, 0x43];

    /// <summary>
    /// CSW signature bytes exactly as the device emits them: <c>55 53 42 53</c>, the little-endian
    /// form of <c>0x53425355</c> and the ASCII text "USBS".
    /// <para>设备发出的 CSW 签名确切字节：<c>55 53 42 53</c>，即 <c>0x53425355</c> 的小端形式，也是 ASCII 文本 "USBS"。</para>
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
    /// Golden vector: the CBW signature constant equals the little-endian wire bytes
    /// <c>55 53 42 43</c>, which spell "USBC".
    /// <para>黄金向量：CBW 签名常量等于小端线上字节 <c>55 53 42 43</c>，拼出 "USBC"。</para>
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
    /// Golden vector: the CSW signature constant equals the device-side little-endian bytes
    /// <c>55 53 42 53</c>, which spell "USBS".
    /// <para>黄金向量：CSW 签名常量等于设备侧小端字节 <c>55 53 42 53</c>，拼出 "USBS"。</para>
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
    /// A CSW built from the literal signature bytes <c>55 53 42 53</c> is accepted, matching the
    /// sequence a rockusb device returns.
    /// <para>由字面签名字节 <c>55 53 42 53</c> 构建的 CSW 会被接受，与 rockusb 设备返回的序列一致。</para>
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
    /// A CSW whose signature is the byte-reversed sequence <c>53 42 53 55</c>, spelling "SBSU", is rejected.
    /// <para>签名为字节逆序序列 <c>53 42 53 55</c>（拼出 "SBSU"）的 CSW 会被拒绝。</para>
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
        Assert.Equal(0x889A9615u, RockchipCrc32.Compute("123456789"u8.ToArray()));
        Assert.Equal(0xA862DB20u, RockchipCrc32.Compute("a"u8.ToArray()));
        Assert.Equal(0x3A06543Eu, RockchipCrc32.Compute("abc"u8.ToArray()));
        Assert.Equal(0x00000000u, RockchipCrc32.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0x00000000u, RockchipCrc32.Compute(new byte[1024]));
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
    public void Crc16CcittFalse_UpstreamGoldenVectors_MatchReferenceCheckValues()
    {
        byte[] payload =
            [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10];

        Assert.Equal((ushort)0x29B1, Crc16CcittFalse.Compute("123456789"u8));
        Assert.Equal((ushort)0xFFFF, Crc16CcittFalse.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal((ushort)0x0FEF, Crc16CcittFalse.Compute(payload));
    }

    [Fact]
    public void Crc32_IeeeGoldenVector_MatchesStandardCheckValue()
    {
        Assert.Equal(0xCBF43926u, RkdlCrc.Crc32("123456789"u8.ToArray()));
    }
}