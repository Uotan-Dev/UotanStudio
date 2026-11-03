using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Security;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Pins the pure-logic contracts changed while closing the audit findings: verification match
/// semantics, erase budget derivation, status naming, and UFS geometry offsets.
/// </summary>
public sealed class MtkProtocolContractTests
{
    [Fact]
    public void VerificationResultRequiresEveryExpectedByte()
    {
        Assert.True(new MtkStorageVerificationResult(512, null, 512).Matches);
        Assert.False(new MtkStorageVerificationResult(0, null, 512).Matches);
        Assert.False(new MtkStorageVerificationResult(256, null, 512).Matches);
        Assert.False(new MtkStorageVerificationResult(512, 7, 512).Matches);
        Assert.True(new MtkStorageVerificationResult(512, null).Matches);
    }

    [Fact]
    public void EraseBudgetScalesWithSizeAndStaysBounded()
    {
        var small = MtkTimeoutPolicy.ComputeEraseTimeout(4 * 1024, 120_000);
        var halfGigabyte = MtkTimeoutPolicy.ComputeEraseTimeout(512L * 1024 * 1024, 120_000);
        var enormous = MtkTimeoutPolicy.ComputeEraseTimeout(1024L * 1024 * 1024 * 1024, 120_000);

        Assert.Equal(120_000, small);
        Assert.True(halfGigabyte > small);
        Assert.Equal(MtkTimeoutPolicy.MaximumEraseTimeoutMs, enormous);
    }

    [Fact]
    public void EraseBudgetHonoursALargerCallerFloor()
    {
        Assert.Equal(300_000, MtkTimeoutPolicy.ComputeEraseTimeout(4 * 1024, 300_000));
    }

    [Fact]
    public void CatalogueNamesOnlyCodesItCanPointAt()
    {
        Assert.Equal("SECCFG invalid", MtkSecStatusCode.TryDescribe(0xc0020030));
        Assert.Null(MtkSecStatusCode.TryDescribe(0xc0020055));
        Assert.Equal("0xC0020055", MtkSecStatusCode.Describe(0xc0020055));
    }

    [Fact]
    public void ProtocolExceptionExposesStatusAndName()
    {
        var error = new MtkProtocolException(MtkProtocolException.DescribeRejection("format", 0xc0020030), 0xc0020030);

        Assert.Equal(0xc0020030u, error.Status);
        Assert.Equal("SECCFG invalid", error.StatusDescription);
        Assert.Contains("SECCFG invalid", error.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(error);
    }

    [Fact]
    public void UfsGeometryUsesLoadUnitOrderAndSkipsTheVendorId()
    {
        var geometry = BuildUfsGeometry(lu0: 0x0010_0000, lu1: 0x0020_0000, lu2: 0x0030_0000,
            cid: [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10]);
        var packets = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(packets, 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(packets.AsSpan(4), 4096);

        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Ufs, geometry, packets);

        Assert.Equal(0x0010_0000, storage.Regions[0].CapacityBytes);
        Assert.Equal(0x0020_0000, storage.Regions[1].CapacityBytes);
        Assert.Equal(0x0030_0000, storage.Regions[2].CapacityBytes);
        Assert.Equal(0, storage.Regions[3].CapacityBytes);
        Assert.Equal("0102030405060708090A0B0C0D0E0F10", storage.CidHex);
    }

    private static byte[] BuildUfsGeometry(long lu0, long lu1, long lu2, byte[] cid)
    {
        var geometry = new byte[50];
        BinaryPrimitives.WriteUInt32LittleEndian(geometry, 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), 512);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8), (ulong)lu0);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(16), (ulong)lu1);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(24), (ulong)lu2);
        BinaryPrimitives.WriteUInt16LittleEndian(geometry.AsSpan(32), 0xBEEF);
        cid.CopyTo(geometry.AsSpan(34));
        return geometry;
    }
}