using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Edl;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class FirehoseConfigureRequestTests
{
    [Fact]
    public void BuildConfigure_OmitsResponseOnlyPayloadAttributes()
    {
        var xml = FirehoseXmlBuilder.BuildConfigure(new FirehoseConfiguration());

        Assert.Contains("MaxPayloadSizeToTargetInBytes=", xml);
        Assert.DoesNotContain("MaxPayloadSizeToTargetInBytesSupported=", xml);
        Assert.DoesNotContain("MaxPayloadSizeFromTargetInBytes=", xml);
    }

    [Fact]
    public void BuildConfigure_DeclaresZlpAwareHost()
    {
        var xml = FirehoseXmlBuilder.BuildConfigure(new FirehoseConfiguration());

        Assert.Contains("ZlpAwareHost=\"1\"", xml);
    }

    [Theory]
    [InlineData(FirehoseStorageType.Emmc, "eMMC")]
    [InlineData(FirehoseStorageType.Ufs, "ufs")]
    public void BuildConfigure_WritesConfiguredMemoryName(FirehoseStorageType storageType, string expected)
    {
        var configuration = new FirehoseConfiguration { StorageType = storageType };

        var xml = FirehoseXmlBuilder.BuildConfigure(configuration);

        Assert.Contains($"MemoryName=\"{expected}\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void FirehoseConfiguration_WithoutChoice_DefaultsToUfs()
    {
        var configuration = new FirehoseConfiguration();

        Assert.Equal(FirehoseStorageType.Ufs, configuration.StorageType);
        Assert.Contains("MemoryName=\"ufs\"", FirehoseXmlBuilder.BuildConfigure(configuration),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0u, 10u)]
    [InlineData(AdbConstants.ProtocolVersionMin, 10u)]
    public void PayloadChecksum_BelowSkipChecksumVersion_WritesAdditiveSum(uint version, uint expected)
        => Assert.Equal(expected, AdbMessaging.PayloadChecksum([1, 2, 3, 4], version));

    [Fact]
    public void PayloadChecksum_AtOrAboveSkipChecksumVersion_WritesZero()
    {
        Assert.Equal(0u, AdbMessaging.PayloadChecksum([1, 2, 3, 4], AdbConstants.ProtocolVersionSkipChecksum));
        Assert.Equal(0u, AdbMessaging.PayloadChecksum([1, 2, 3, 4], AdbConstants.ProtocolVersion));
        Assert.Equal(0u, AdbMessaging.PayloadChecksum([1, 2, 3, 4], AdbConstants.ProtocolVersion + 1));
    }

    [Fact]
    public void SerializeHeader_NegotiatedVersion_WritesZeroChecksumField()
    {
        var header = AdbMessaging.SerializeHeader(
            new AdbMessage(AdbCommand.Cnxn, AdbConstants.ProtocolVersion, AdbConstants.MaxPayload,
                [1, 2, 3, 4]), AdbConstants.ProtocolVersion);

        Assert.Equal(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)));
    }

    [Fact]
    public void SerializeHeader_LegacyVersion_WritesAdditiveChecksumField()
    {
        var header = AdbMessaging.SerializeHeader(
            new AdbMessage(AdbCommand.Cnxn, AdbConstants.ProtocolVersion, AdbConstants.MaxPayload,
                [1, 2, 3, 4]), AdbConstants.ProtocolVersionMin);

        Assert.Equal(10u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)));
    }
}