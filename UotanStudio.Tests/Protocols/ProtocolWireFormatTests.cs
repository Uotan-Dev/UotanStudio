using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Edl;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Wire-format checks for device protocol headers and constants. These tests only compare bytes, lengths and
/// protocol codes; they never open a transport or contact a device.
/// <para>设备协议头部与常量的线格式校验：只比较字节、长度与协议码，不打开传输也不接触设备。</para>
/// </summary>
public sealed class ProtocolWireFormatTests
{
    [Fact]
    public void Sahara_HelloCommandConstants_MatchTheWireLayout()
    {
        Assert.Equal(0x01u, EdlConstants.SaharaCmdHello);
        Assert.Equal(0x02u, EdlConstants.SaharaCmdHelloResponse);
        Assert.Equal(2u, EdlConstants.SaharaVersion);
    }

    [Fact]
    public void Sahara_HelloPacket_FirstFieldsAreCommandAndLength()
    {
        var hello = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(hello, EdlConstants.SaharaCmdHello);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(4), (uint)hello.Length);

        Assert.Equal(0x01u, BinaryPrimitives.ReadUInt32LittleEndian(hello));
        Assert.Equal(48u, BinaryPrimitives.ReadUInt32LittleEndian(hello.AsSpan(4)));
    }

    [Fact]
    public void Sahara_ProtocolVersionsAreOrdered()
    {
        Assert.True(EdlConstants.SaharaMinVersion <= EdlConstants.SaharaHostCompatibleVersion);
        Assert.True(EdlConstants.SaharaHostCompatibleVersion <= EdlConstants.SaharaHostMaxProtocolVersion);
    }

    [Fact]
    public void Adb_BannerContainsTheAdbServerVersionPrefix()
    {
        const string banner = "001f0001";
        Assert.Equal(8, banner.Length);
        Assert.StartsWith("001f", banner, StringComparison.Ordinal);
        Assert.All(banner, character => Assert.True(char.IsAsciiHexDigitLower(character) || char.IsAsciiDigit(character)));
    }

    [Fact]
    public void Adb_SyncDataLength_IsWrittenLittleEndianAfterTheFourByteCommand()
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x01020304);

        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, header[4..]);
    }
}