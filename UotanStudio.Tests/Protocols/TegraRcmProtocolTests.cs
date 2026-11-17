using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Tegra;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class TegraRcmProtocolTests
{
    [Theory]
    [InlineData(TegraChip.Tegra20, 0x20, 0x74, 0x01, 0x0001_0000u)]
    [InlineData(TegraChip.Tegra30, 0x30, 0x74, 0x01, 0x0001_0000u)]
    [InlineData(TegraChip.Tegra114, 0x35, 0x274, 0x35, 0x0035_0001u)]
    [InlineData(TegraChip.Tegra124, 0x40, 0x284, 0x40, 0x0040_0001u)]
    public void HeaderLayout_EachGeneration_UsesTheDocumentedHeaderLength(TegraChip chip, int deviceId,
        int headerLength, uint rcmVersionMajor, uint expectedPackedVersion)
    {
        var layout = TegraRcmHeaderLayout.For(chip);

        Assert.Equal(headerLength, layout.HeaderLength);
        Assert.Equal(expectedPackedVersion, layout.RcmVersion);
        Assert.Equal(rcmVersionMajor, TegraConstants.GetRcmVersionMajor(layout.RcmVersion));
        Assert.Equal(deviceId, TegraConstants.ProductIdMask & chip switch
        {
            TegraChip.Tegra20 => TegraConstants.Tegra20ProductId,
            TegraChip.Tegra30 => TegraConstants.Tegra30ProductId,
            TegraChip.Tegra114 => TegraConstants.Tegra114ProductId,
            TegraChip.Tegra124 => TegraConstants.Tegra124ProductId,
            _ => 0
        });
    }

    [Theory]
    [InlineData(TegraChip.Tegra114)]
    [InlineData(TegraChip.Tegra124)]
    public void CreateFrame_EmittedHeader_CarriesOpcodePayloadAndVersionAtTheirDocumentedOffsets(TegraChip chip)
    {
        var layout = TegraRcmHeaderLayout.For(chip);
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var frame = TegraRcmProtocol.CreateFrame(chip, TegraRcmOpcode.DownloadMiniloader, default, payload);

        Assert.Equal(TegraRcmProtocol.GetFrameLength(chip, payload.Length), frame.Length);
        Assert.Equal((uint)frame.Length, BinaryPrimitives.ReadUInt32LittleEndian(frame));
        Assert.Equal((uint)TegraRcmOpcode.DownloadMiniloader,
            BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(layout.OpcodeOffset)));
        Assert.Equal((uint)payload.Length,
            BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(layout.PayloadLengthOffset)));
        Assert.Equal(layout.RcmVersion,
            BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(layout.RcmVersionOffset)));
        Assert.Equal(payload, frame.AsSpan(layout.HeaderLength, payload.Length).ToArray());
    }

    [Fact]
    public void CreateFrame_ShortMessage_IsPaddedToTheMinimumMessageLength()
    {
        var frame = TegraRcmProtocol.CreateFrame(TegraChip.Tegra114, TegraRcmOpcode.QueryRcmVersion);

        Assert.True(frame.Length >= TegraConstants.RcmMinimumMessageLength,
            $"An RCM frame is at least {TegraConstants.RcmMinimumMessageLength} bytes, but {frame.Length} were built.");
        var headerLength = TegraRcmHeaderLayout.For(TegraChip.Tegra114).HeaderLength;
        var paddingLength = frame.Length - headerLength;
        Assert.Equal(0, paddingLength % 16);
    }

    [Fact]
    public void CreateFrame_LongerPayload_IsPaddedToAWholeSixteenByteBlock()
    {
        var payload = new byte[TegraConstants.RcmMinimumMessageLength + 7];

        var frame = TegraRcmProtocol.CreateFrame(TegraChip.Tegra114, TegraRcmOpcode.DownloadMiniloader,
            default, payload);

        var headerLength = TegraRcmHeaderLayout.For(TegraChip.Tegra114).HeaderLength;
        Assert.Equal(0, (frame.Length - headerLength) % 16);
        var paddingLength = frame.Length - headerLength - payload.Length;
        Assert.InRange(paddingLength, 1, 16);
        Assert.Equal(0, (payload.Length + paddingLength) % 16);
    }

    [Fact]
    public void TryGetFrameLength_HeaderPrefix_ReportsTheDeclaredLength()
    {
        var frame = TegraRcmProtocol.CreateFrame(TegraChip.Tegra114, TegraRcmOpcode.Sync);

        Assert.True(TegraRcmProtocol.TryGetFrameLength(frame.AsSpan(0, 4), out var frameLength));
        Assert.Equal(frame.Length, frameLength);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0xFFFF_FFFFu)]
    public void TryGetFrameLength_ImpossibleDeclaredLength_IsRejected(uint declared)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, declared);

        Assert.False(TegraRcmProtocol.TryGetFrameLength(prefix, out _));
    }

    [Fact]
    public void CreateFrame_UnknownChip_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TegraRcmProtocol.CreateFrame(TegraChip.Unknown, TegraRcmOpcode.Sync));
    }

    [Theory]
    [InlineData("35.1.2.0.0", TegraChip.Tegra114)]
    [InlineData("40.1.2.0.0", TegraChip.Tegra124)]
    [InlineData("30.1.0.0.0", TegraChip.Tegra30)]
    [InlineData("20.1.0.0.0", TegraChip.Tegra20)]
    [InlineData("0x35.1.2.0.0", TegraChip.Tegra114)]
    [InlineData("99.0.0.0.0", TegraChip.Unknown)]
    [InlineData("", TegraChip.Unknown)]
    public void FromTegraId_KernelCommandLineValue_MapsToTheGeneration(string tegraId, TegraChip expected)
    {
        Assert.Equal(expected, TegraChips.FromTegraId(tegraId));
    }

    [Theory]
    [InlineData(0x7135, TegraChip.Tegra114)]
    [InlineData(0x7140, TegraChip.Tegra124)]
    [InlineData(0x7330, TegraChip.Tegra30)]
    [InlineData(0x7820, TegraChip.Tegra20)]
    public void TryMapProductId_RcmPortProductId_MapsToTheGeneration(ushort productId, TegraChip expected)
    {
        Assert.True(TegraConstants.TryMapProductId(productId, out var chip));
        Assert.Equal(expected, chip);
    }

    [Theory]
    [InlineData(0xD00D)]
    [InlineData(0x7011)]
    [InlineData(0x0000)]
    public void TryMapProductId_NonRcmProductId_IsRejected(ushort productId)
    {
        Assert.False(TegraConstants.TryMapProductId(productId, out _));
    }

    [Fact]
    public void IsTegraRcmEndpoint_PiscesFastbootPair_IsNotAnRcmEndpoint()
    {
        Assert.False(TegraConstants.IsTegraRcmEndpoint(0x18D1, 0xD00D));
    }

    [Fact]
    public void IsTegraRcmEndpoint_Tegra4Port_IsAnRcmEndpoint()
    {
        Assert.True(TegraConstants.IsTegraRcmEndpoint(TegraConstants.NvidiaVendorId, 0x7135));
    }

    [Fact]
    public void IsTegraRcmEndpoint_NvidiaVendorWithNonRcmProduct_IsRejected()
    {
        Assert.False(TegraConstants.IsTegraRcmEndpoint(TegraConstants.NvidiaVendorId, 0xD00D));
    }
}