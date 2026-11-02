using System.Buffers.Binary;
using UotanStudio.Hash.RockchipCrc32;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// Cases for the vendor PARM container: the wrapped layout matches the vendor byte order, unwrapping restores the
/// payload, and hostile inputs are rejected instead of producing text.
/// <para>厂商 PARM 容器用例：封装布局与厂商字节序一致，解包还原负载，敌意输入被拒绝而不是产出文本。</para>
/// </summary>
public sealed class RockchipParmContainerTests
{
    [Fact]
    public void Wrap_Layout_MatchesVendorHeaderOrder()
    {
        var payload = "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot)"u8.ToArray();

        var wrapped = RockchipParmContainer.Wrap(payload);

        Assert.Equal((uint)0x4D524150, BinaryPrimitives.ReadUInt32LittleEndian(wrapped));
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(wrapped.AsSpan(4)));
        Assert.Equal(payload, wrapped.AsSpan(8, payload.Length).ToArray());
        Assert.Equal(RockchipCrc32.Compute(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(wrapped.AsSpan(8 + payload.Length)));
    }

    [Fact]
    public void WrapUnwrap_Roundtrip_RestoresPayload()
    {
        var payload = "FIRMWARE_VER: 8.1\nCMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot)\n"u8.ToArray();

        var restored = RockchipParmContainer.Unwrap(RockchipParmContainer.Wrap(payload));

        Assert.Equal(payload, restored);
    }

    [Fact]
    public void Wrap_OversizedPayload_IsRejectedBeforeAnyWrite()
    {
        var payload = new byte[(RockchipParmContainer.MaxParameterSectors + 1) * 512];

        Assert.Throws<ArgumentException>(() => RockchipParmContainer.Wrap(payload));
    }

    [Fact]
    public void Unwrap_CorruptedCrc_IsRejected()
    {
        var wrapped = RockchipParmContainer.Wrap("CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot)"u8.ToArray());
        wrapped[^1] ^= 0xFF;

        Assert.Throws<InvalidDataException>(() => RockchipParmContainer.Unwrap(wrapped));
    }

    [Fact]
    public void Unwrap_OversizedDeclaredLength_IsRejected()
    {
        var wrapped = RockchipParmContainer.Wrap("CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot)"u8.ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(4), 0xFFFFFFF0u);

        Assert.Throws<InvalidDataException>(() => RockchipParmContainer.Unwrap(wrapped));
    }

    [Fact]
    public void Unwrap_ForeignMagic_IsRejected()
    {
        var data = new byte[] { 0x45, 0x46, 0x49, 0x20, 0, 0, 0, 0, 1, 2, 3, 4 };

        Assert.Throws<InvalidDataException>(() => RockchipParmContainer.Unwrap(data));
    }

    [Fact]
    public void Constants_MatchTheVendorParameterWindow()
    {
        Assert.Equal(0x2000, RockchipParmContainer.FirmwareParameterStartLba);
        Assert.Equal(0x20, RockchipParmContainer.LegacyParameterStartLba);
        Assert.Equal(512, RockchipParmContainer.FirmwareParameterReadSectors);
        Assert.Equal(128, RockchipParmContainer.LegacyParameterReadSectors);
        Assert.Equal(1024, RockchipParmContainer.MaxParameterSectors);
    }
}