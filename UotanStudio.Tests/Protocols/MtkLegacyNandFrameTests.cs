using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Pins the Legacy NAND read frame and the inventory width detection against the vendor command table:
/// the read command carries a host byte, a read flag, an address-type byte and 32-bit address, length and
/// zero fields, and the inventory reports its own capacity width rather than taking it from the caller.
/// </summary>
public sealed class MtkLegacyNandFrameTests
{
    private static MtkLegacyNandGeometry CreateGeometry(bool size64Bit = true)
    {
        var inventory = new byte[17];
        if (size64Bit)
        {
            BinaryPrimitives.WriteUInt64BigEndian(inventory.AsSpan(7), 0x1000_0000UL);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(inventory.AsSpan(7), 0x1000_0000U);
        }

        var ids = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(inventory.AsSpan(15), 1);
        var details = new byte[9];
        BinaryPrimitives.WriteUInt16BigEndian(details, 2048);
        BinaryPrimitives.WriteUInt16BigEndian(details.AsSpan(2), 64);
        BinaryPrimitives.WriteUInt16BigEndian(details.AsSpan(4), 64);
        return MtkLegacyNandGeometry.Parse(inventory, ids, details, size64Bit);
    }

    private static MtkLegacyNandReadOptions CreateOptions(MtkLegacyNandReadMode mode) => new()
    {
        Profile = MtkLegacyNandReadProfile.Sdk64Raw,
        AddressType = MtkLegacyNandAddressType.Logical,
        Mode = mode,
        LengthUnit = MtkLegacyNandLengthUnit.MainAreaBytes,
        LogicalCapacityBytes = 0x1000_0000
    };

    [Fact]
    public void CreateHeader_WireLayout_MatchesVendorCommandTable()
    {
        var geometry = CreateGeometry();
        var options = CreateOptions(MtkLegacyNandReadMode.PageAndSpare);

        const uint offset = 0x0012_3000;
        var header = options.CreateHeader(geometry, offset, 0x2000, 0x2000 + 256);

        Assert.Equal(15, header.Length);
        Assert.Equal(0xDF, header[0]);
        Assert.Equal(MtkLegacyNandReadOptions.LegacyHostOsByte, header[1]);
        Assert.Equal((byte)MtkLegacyNandReadMode.PageAndSpare, header[2]);
        Assert.Equal((byte)MtkLegacyNandAddressType.Logical, header[3]);
        Assert.Equal(offset, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)));
        Assert.Equal(0x2000u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(11)));
    }

    [Fact]
    public void CreateHeader_RangeBeyondThirtyTwoBits_Throws()
    {
        var geometry = CreateGeometry();
        var options = CreateOptions(MtkLegacyNandReadMode.PageOnly) with
        {
            LogicalCapacityBytes = 0x1_0000_0000
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.CreateHeader(geometry, 0xFFFF_F000, 0x2000, 0x2000));
    }

    [Fact]
    public void Parse_ThirtyTwoBitInventoryWithZeroCount_ReadsThirtyTwoBitCapacity()
    {
        const uint capacity = 0x0800_0000;
        var inventory = new byte[17];
        BinaryPrimitives.WriteUInt32BigEndian(inventory.AsSpan(7), capacity);
        BinaryPrimitives.WriteUInt16BigEndian(inventory.AsSpan(11), 2);

        var details = new byte[9];
        BinaryPrimitives.WriteUInt16BigEndian(details, 2048);
        BinaryPrimitives.WriteUInt16BigEndian(details.AsSpan(2), 64);
        BinaryPrimitives.WriteUInt16BigEndian(details.AsSpan(4), 64);
        var ids = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(ids, 0x2C);
        BinaryPrimitives.WriteUInt16BigEndian(ids.AsSpan(2), 0x64);

        var geometry = MtkLegacyNandGeometry.Parse(inventory, ids, details, size64Bit: false);

        Assert.Equal(capacity, geometry.CapacityBytes);
        Assert.Equal(2048, geometry.PageSize);
        Assert.Equal(64, geometry.SpareSize);
    }

    [Fact]
    public void ValidateLegacyEmi_VersionsThatCarryALengthWord_AreAccepted()
    {
        foreach (var version in new byte[] { 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15 })
        {
            var emi = new UotanStudio.Firmware.VendorFirmware.MediaTek.MtkEmiParameters(
                0, 0, 0, UotanStudio.Firmware.VendorFirmware.MediaTek.MtkEmiPayloadLayout.BootloaderInfo,
                [0x00, version]);

            MtkSession.ValidateLegacyEmi(emi);
        }
    }
}