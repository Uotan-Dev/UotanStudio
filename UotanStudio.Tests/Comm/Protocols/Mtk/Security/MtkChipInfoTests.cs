using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Security;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk.Security;

public sealed class MtkChipInfoTests
{
    private const string ChipIdReplyHex = "89670100020003000400";

    [Fact]
    public void Decode_TenByteReply_UnpacksFiveLittleEndianWordsInDocumentedOrder()
    {
        var identity = MtkXFlashChipId.Decode(Convert.FromHexString(ChipIdReplyHex));

        Assert.Equal(0x6789, identity.HardwareCode);
        Assert.Equal(0x0001, identity.HardwareSubCode);
        Assert.Equal(0x0002, identity.HardwareVersion);
        Assert.Equal(0x0003, identity.SoftwareVersion);
        Assert.Equal(0x0004, identity.ChipEvolution);
        Assert.Equal(ChipIdReplyHex, identity.ReplyHex);
    }

    [Fact]
    public void Decode_AllOnesWords_DecodesWithoutMaskingOrShifting()
    {
        var reply = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFF");

        var identity = MtkXFlashChipId.Decode(reply);

        Assert.Equal(ushort.MaxValue, identity.HardwareCode);
        Assert.Equal(ushort.MaxValue, identity.HardwareSubCode);
        Assert.Equal(ushort.MaxValue, identity.HardwareVersion);
        Assert.Equal(ushort.MaxValue, identity.SoftwareVersion);
        Assert.Equal(ushort.MaxValue, identity.ChipEvolution);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(20)]
    public void Decode_LengthOutsideTheDocumentedLayout_FaultsWithoutPadding(int length)
    {
        var reply = new byte[length];

        var exception = Assert.Throws<MtkProtocolException>(() => MtkXFlashChipId.Decode(reply));
        Assert.Contains("10", exception.Message, StringComparison.Ordinal);
        Assert.Contains(length.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MtkXFlashRawControlCode.ReplayProtectedAreaStatus, 0x040010u)]
    [InlineData(MtkXFlashRawControlCode.HardwareIdentifier, 0x040014u)]
    public void RawControlCode_CarriesTheDeclaredXFlashValue(MtkXFlashRawControlCode code, uint expected)
    {
        Assert.Equal(expected, (uint)code);
    }

    [Theory]
    [InlineData(MtkXFlashUndocumentedSecurityControlCode.ReadOneTimeProgramZone, 0x01000Cu)]
    [InlineData(MtkXFlashUndocumentedSecurityControlCode.WriteOneTimeProgramZone, 0x01000Du)]
    [InlineData(MtkXFlashUndocumentedSecurityControlCode.WriteElectronicFuse, 0x01000Eu)]
    [InlineData(MtkXFlashUndocumentedSecurityControlCode.ReadElectronicFuse, 0x01000Fu)]
    [InlineData(MtkXFlashUndocumentedSecurityControlCode.ActivateOneTimeProgramZoneLock, 0x080003u)]
    public void UndocumentedSecurityControlCode_DeclaresOnlyCataloguedValues(MtkXFlashUndocumentedSecurityControlCode code,
        uint expected)
    {
        Assert.Equal(expected, (uint)code);
    }

    [Fact]
    public void UndocumentedSecurityControl_Explain_RefusesToFrameWithoutInventingBytes()
    {
        var reason = MtkXFlashUndocumentedSecurityControl.Explain(
            MtkXFlashUndocumentedSecurityControlCode.WriteElectronicFuse);

        Assert.Contains("0x0001000E", reason, StringComparison.Ordinal);
        Assert.Contains("no documented request or reply layout", reason, StringComparison.Ordinal);
        Assert.Contains("request was refused", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ChipIdReplyHex_IsUppercaseAndPreservesEveryReplyByte()
    {
        var reply = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(reply, 0x0335);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(2), 0x0000);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(4), 0xABCD);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(6), 0x0010);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(8), 0x00FF);

        var identity = MtkXFlashChipId.Decode(reply);

        Assert.Equal("3503 0000 CDAB 1000 FF00".Replace(" ", string.Empty), identity.ReplyHex);
        Assert.Equal(0x0335, identity.HardwareCode);
        Assert.Equal(0xABCD, identity.HardwareVersion);
        Assert.Equal(0x0010, identity.SoftwareVersion);
        Assert.Equal(0x00FF, identity.ChipEvolution);
    }
}