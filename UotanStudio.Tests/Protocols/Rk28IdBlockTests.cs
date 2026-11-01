using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Hash.Crc16Xmodem;
using UotanStudio.Hash.RockchipCrc32;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Verifies the RK28 ID block against the byte layout of <c>RK28_IDB_SEC0</c> to <c>RK28_IDB_SEC3</c> and the steps of
/// <c>MakeIDBlockData</c>: header fields, the checksum placement, the always-on RC4 pass over sectors 0, 2 and 3, and
/// the conditional RC4 pass over loader data and code.
/// <para>按 <c>RK28_IDB_SEC0</c> 到 <c>RK28_IDB_SEC3</c> 的字节布局与 <c>MakeIDBlockData</c> 的步骤验证 RK28 ID block：
/// 头部字段、校验和位置、始终对扇区 0/2/3 施加的 RC4，以及对 loader 数据与代码的有条件 RC4。</para>
/// </summary>
public class Rk28IdBlockTests
{
    private const int SectorSize = 512;
    private const uint Sector0Tag = 0x0FF0AA55;

    [Fact]
    public void Build_LegacyLayout_MatchesTheReferenceHeaderFields()
    {
        var loaderData = Filled(2 * SectorSize, 0x11);
        var loaderCode = Filled(SectorSize, 0x22);

        var block = Rk28IdBlock.Build(loaderData, loaderCode, flashDataSectors: 2, flashBootSectors: 1,
            rc4Flag: false);

        Assert.Equal(7 * SectorSize, block.Length);
        var sector0 = Decrypted(block, 0);
        var sector1 = Unencrypted(block, 1);
        var sector2 = Decrypted(block, 2);
        var sector3 = Decrypted(block, 3);

        Assert.Equal(Sector0Tag, BinaryPrimitives.ReadUInt32LittleEndian(sector0));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(sector0.AsSpan(8)));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(sector0.AsSpan(12)));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(sector0.AsSpan(14)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(sector0.AsSpan(506)));
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(sector0.AsSpan(508)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(sector0.AsSpan(510)));

        Assert.Equal((ushort)0xC, BinaryPrimitives.ReadUInt16LittleEndian(sector1));
        Assert.Equal((ushort)0xFFFF, BinaryPrimitives.ReadUInt16LittleEndian(sector1.AsSpan(2)));
        Assert.Equal(0x38324B52u, BinaryPrimitives.ReadUInt32LittleEndian(sector1.AsSpan(10)));

        Assert.Equal("VC", Encoding.ASCII.GetString(sector2, 491, 2));
        Assert.Equal("CRC", Encoding.ASCII.GetString(sector2, 506, 3));
        Assert.All(sector3, value => Assert.Equal((byte)0, value));

        Assert.Equal(loaderData, block[2048..3072]);
        Assert.Equal(loaderCode, block[3072..3584]);
    }

    [Fact]
    public void Build_LegacyLayout_SectorCrcsCoverThePlaintextHeaderSectors()
    {
        var block = Rk28IdBlock.Build(Filled(2 * SectorSize, 0x11), Filled(SectorSize, 0x22),
            flashDataSectors: 2, flashBootSectors: 1, rc4Flag: false);
        var sector0 = Decrypted(block, 0);
        var sector1 = Unencrypted(block, 1);
        var sector2 = Decrypted(block, 2);
        var sector3 = Decrypted(block, 3);

        Assert.Equal(Crc16Xmodem.Compute(sector0), BinaryPrimitives.ReadUInt16LittleEndian(sector2.AsSpan(494)));
        Assert.Equal(Crc16Xmodem.Compute(sector1), BinaryPrimitives.ReadUInt16LittleEndian(sector2.AsSpan(496)));
        Assert.Equal(Crc16Xmodem.Compute(sector3), BinaryPrimitives.ReadUInt16LittleEndian(sector2.AsSpan(510)));
    }

    [Fact]
    public void Build_WithoutRc4Flag_StillEncryptsSectorsZeroTwoAndThree()
    {
        var loaderData = Filled(2 * SectorSize, 0x11);
        var loaderCode = Filled(SectorSize, 0x22);

        var block = Rk28IdBlock.Build(loaderData, loaderCode, flashDataSectors: 2, flashBootSectors: 1,
            rc4Flag: false);

        Assert.NotEqual(Filled(SectorSize, 0), block[..SectorSize]);
        Assert.NotEqual(Filled(SectorSize, 0), block[1024..1536]);
        Assert.Equal(Filled(SectorSize, 0), Decrypted(block, 3));
        Assert.Equal(loaderData, block[2048..3072]);
        Assert.Equal(loaderCode, block[3072..3584]);
    }

    [Fact]
    public void Build_WithRc4Flag_EncryptsDataAndCodeAndChecksumsTheStoredBytes()
    {
        var loaderData = Filled(2 * SectorSize, 0x11);
        var loaderCode = Filled(SectorSize, 0x22);
        var plainData = (byte[])loaderData.Clone();
        var plainCode = (byte[])loaderCode.Clone();

        var block = Rk28IdBlock.Build(loaderData, loaderCode, flashDataSectors: 2, flashBootSectors: 1,
            rc4Flag: true);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(Decrypted(block, 0).AsSpan(8)));
        Assert.NotEqual(plainData, block[2048..3072]);
        Assert.NotEqual(plainCode, block[3072..3584]);
        Assert.Equal(plainData, DecryptedRegion(block, 2048, 2 * SectorSize));
        Assert.Equal(plainCode, DecryptedRegion(block, 3072, SectorSize));

        var sector2 = Decrypted(block, 2);
        Assert.Equal(RockchipCrc32.Compute(block.AsSpan(2048, 3 * SectorSize)),
            BinaryPrimitives.ReadUInt32LittleEndian(sector2.AsSpan(498)));
    }

    [Fact]
    public void Build_DataLargerThanItsSectorRange_Throws()
    {
        var error = Assert.Throws<RkdlException>(() => Rk28IdBlock.Build(new byte[1024], new byte[SectorSize],
            flashDataSectors: 1, flashBootSectors: 1, rc4Flag: false));

        Assert.Contains("does not fit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_TotalSectorCountBeyondTheDeviceField_Throws()
    {
        var error = Assert.Throws<RkdlException>(() => Rk28IdBlock.Build([], [], flashDataSectors: 0xFFFF,
            flashBootSectors: 5, rc4Flag: false));

        Assert.Contains("out of range", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 4)]
    [InlineData(2047, 4)]
    [InlineData(2048, 4)]
    [InlineData(2049, 8)]
    [InlineData(8192, 16)]
    public void SectorsFor_RoundsUpToTheTwoKilobyteAlignment(int bytes, int expected)
        => Assert.Equal(expected, Rk28IdBlock.SectorsFor(bytes));

    [Fact]
    public void BuildNewIdBlock_HeadDataAndCode_FollowInThatOrderWithoutHeaders()
    {
        var head = Filled(4 * SectorSize, 0x33);
        var loaderData = Filled(2 * SectorSize, 0x11);
        var loaderCode = Filled(SectorSize, 0x22);

        var block = Rk28IdBlock.BuildNewIdBlock(head, loaderData, loaderCode, flashHeadSectors: 4,
            flashDataSectors: 2, flashBootSectors: 1, rc4Flag: false);

        Assert.Equal(7 * SectorSize, block.Length);
        Assert.Equal(head, block[..2048]);
        Assert.Equal(loaderData, block[2048..3072]);
        Assert.Equal(loaderCode, block[3072..3584]);
    }

    [Fact]
    public void BuildNewIdBlock_WithRc4Flag_EncryptsEveryPayload()
    {
        var head = Filled(4 * SectorSize, 0x33);
        var loaderData = Filled(2 * SectorSize, 0x11);
        var loaderCode = Filled(SectorSize, 0x22);
        var plainHead = (byte[])head.Clone();
        var plainData = (byte[])loaderData.Clone();
        var plainCode = (byte[])loaderCode.Clone();

        var block = Rk28IdBlock.BuildNewIdBlock(head, loaderData, loaderCode, flashHeadSectors: 4,
            flashDataSectors: 2, flashBootSectors: 1, rc4Flag: true);

        Assert.NotEqual(plainHead, block[..2048]);
        Assert.NotEqual(plainData, block[2048..3072]);
        Assert.NotEqual(plainCode, block[3072..3584]);
        Assert.Equal(plainHead, DecryptedRegion(block, 0, 4 * SectorSize));
        Assert.Equal(plainData, DecryptedRegion(block, 2048, 2 * SectorSize));
        Assert.Equal(plainCode, DecryptedRegion(block, 3072, SectorSize));
    }

    [Fact]
    public void BuildNewIdBlock_HeadLargerThanItsSectorRange_Throws()
    {
        var error = Assert.Throws<RkdlException>(() => Rk28IdBlock.BuildNewIdBlock(new byte[4096], [], [],
            flashHeadSectors: 4, flashDataSectors: 0, flashBootSectors: 0, rc4Flag: false));

        Assert.Contains("Loader head", error.Message, StringComparison.Ordinal);
    }

    private static byte[] Filled(int length, byte value)
    {
        var data = new byte[length];
        Array.Fill(data, value);
        return data;
    }

    private static byte[] Decrypted(byte[] block, int sector)
    {
        var copy = Unencrypted(block, sector);
        RkdlRc4.Encrypt(copy);
        return copy;
    }

    private static byte[] Unencrypted(byte[] block, int sector)
        => block.AsSpan(sector * SectorSize, SectorSize).ToArray();

    private static byte[] DecryptedRegion(byte[] block, int offset, int length)
    {
        var copy = block.AsSpan(offset, length).ToArray();
        RkdlRc4.EncryptSectors(copy);
        return copy;
    }
}