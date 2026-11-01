using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Bootrom;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class RkdlCbwWireFormatTests
{
    [Fact]
    public void ToBytes_CarriesFlashCsInTheCbwLunByte()
    {
        var cbw = new RkdlCbw(RkdlCommand.EraseNormal, RkdlFrame.DirectionOut, 0, RkdlFrame.CbwcbLengthLong,
            subCode: 2, address: 0x01020304, sectorLength: 0x0506, flashCs: 7);

        var bytes = cbw.ToBytes();

        Assert.Equal(7, bytes[13]);
        Assert.Equal(0, bytes[21]);
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(17, 4)));
        Assert.Equal(0x0506, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(22, 2)));
    }

    [Fact]
    public void ToBytes_ReadLbaWithLbaMethod_PutsTheRwMethodInTheReservedCommandByte()
    {
        var cbw = new RkdlCbw(RkdlCommand.ReadLba, RkdlFrame.DirectionIn, 512, RkdlFrame.CbwcbLengthLong,
            subCode: RkdlRwMethod.Lba, address: 64, sectorLength: 1);

        var bytes = cbw.ToBytes();

        Assert.Equal((byte)RkdlCommand.ReadLba, bytes[15]);
        Assert.Equal(RkdlRwMethod.Lba, bytes[16]);
        Assert.Equal(0, bytes[21]);
    }

    [Fact]
    public void ToBytes_DefaultRwMethod_IsTheImageMethod()
    {
        var cbw = new RkdlCbw(RkdlCommand.WriteLba, RkdlFrame.DirectionOut, 512, RkdlFrame.CbwcbLengthLong,
            address: 64, sectorLength: 1);

        Assert.Equal(RkdlRwMethod.Image, cbw.SubCode);
        Assert.Equal(0, cbw.ToBytes()[16]);
    }
}