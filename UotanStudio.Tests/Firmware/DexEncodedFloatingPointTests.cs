using System.Buffers.Binary;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexEncodedFloatingPointTests
{
    [Theory]
    [InlineData(0x00000000u, "1000")]
    [InlineData(0x80000000u, "1080")]
    [InlineData(0x3f800000u, "30803f")]
    [InlineData(0x3fa00000u, "30a03f")]
    [InlineData(0x00000001u, "7001000000")]
    [InlineData(0x7fc01234u, "703412c07f")]
    [InlineData(0x7f800000u, "30807f")]
    public void Write_FloatBitPattern_UsesRightZeroExtendedEncoding(uint bits, string expected)
    {
        var dex = SmaliAssembler.Assemble(".class public LExample;\n.field public static value:F = 0.0f");
        Assert.Single(dex.Classes).StaticValues![0] = EncodedValue.Float(BitConverter.UInt32BitsToSingle(bits));
        var bytes = dex.ToArray();
        var offset = StaticValueOffset(bytes);
        var encoded = Convert.FromHexString(expected);
        Assert.Equal(encoded, bytes.AsSpan(offset, encoded.Length).ToArray());
        var loaded = DexFile.Load(bytes);
        var value = Assert.IsType<float>(Assert.Single(Assert.Single(loaded.Classes).StaticValues!).Value);
        Assert.Equal(bits, BitConverter.SingleToUInt32Bits(value));
    }

    [Theory]
    [InlineData(0x0000000000000000ul, "1100")]
    [InlineData(0x8000000000000000ul, "1180")]
    [InlineData(0x3ff0000000000000ul, "31f03f")]
    [InlineData(0xc004000000000000ul, "3104c0")]
    [InlineData(0x0000000000000001ul, "f10100000000000000")]
    [InlineData(0x7ff8000000001234ul, "f1341200000000f87f")]
    public void Write_DoubleBitPattern_UsesRightZeroExtendedEncoding(ulong bits, string expected)
    {
        var dex = SmaliAssembler.Assemble(".class public LExample;\n.field public static value:D = 0.0");
        Assert.Single(dex.Classes).StaticValues![0] = EncodedValue.Double(BitConverter.UInt64BitsToDouble(bits));
        var bytes = dex.ToArray();
        var offset = StaticValueOffset(bytes);
        var encoded = Convert.FromHexString(expected);
        Assert.Equal(encoded, bytes.AsSpan(offset, encoded.Length).ToArray());
        var loaded = DexFile.Load(bytes);
        var value = Assert.IsType<double>(Assert.Single(Assert.Single(loaded.Classes).StaticValues!).Value);
        Assert.Equal(bits, BitConverter.DoubleToUInt64Bits(value));
    }

    [Fact]
    public void Read_FloatWithFiveByteValueArg_RejectsMalformedEncoding()
    {
        var bytes = SmaliAssembler.Assemble(".class public LExample;\n.field public static value:F = 0.0f").ToArray();
        bytes[StaticValueOffset(bytes)] = 0x90;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), UotanStudio.Hash.Adler32.Adler32.Compute(bytes.AsSpan(12)));
        Assert.Throws<InvalidDataException>(() => DexFile.Load(bytes));
    }

    private static int StaticValueOffset(byte[] bytes)
    {
        var classOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(100));
        var arrayOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(classOffset + 28));
        Assert.Equal(1, bytes[arrayOffset]);
        return arrayOffset + 1;
    }
}