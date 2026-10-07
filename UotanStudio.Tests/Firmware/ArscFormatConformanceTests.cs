using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Arsc;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks Android resource layouts using independent binary fixtures. <para>使用独立二进制夹具检查 Android 资源布局。</para></summary>
public class ArscFormatConformanceTests
{
    [Theory]
    [InlineData(0, false, 28)]
    [InlineData(1, false, 64)]
    [InlineData(1, true, 72)]
    [InlineData(2, false, 28)]
    [InlineData(2, true, 72)]
    public void StandardEntryEncodings_PreserveEntryIdsAndValues(int encoding, bool compact, int configSize)
    {
        var original = StandardTable(encoding, compact, configSize);
        var parsed = ArscParser.Parse(original);
        var package = Assert.Single(parsed.Packages);
        Assert.Equal("com.example.资源", package.Name);
        Assert.Equal(2u, package.TypeIdOffset);
        var type = Assert.Single(package.Types);
        Assert.Equal(configSize * 2, type.ConfigHex.Length);
        Assert.Equal(4, type.Entries.Count);
        Assert.All(type.Entries.Take(3), entry => Assert.Null(entry));
        Assert.Equal(42u, type.Entries[3]!.Value!.Data);
        Assert.Equal(ResValueType.IntDec, type.Entries[3]!.Value!.Type);
        Assert.Equal(2, type.Entries[3]!.Flags);

        var rebuilt = ArscWriter.Write(parsed);
        var packageOffset = 12 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(16)));
        Assert.Equal("com.example.资源", Encoding.Unicode.GetString(rebuilt, packageOffset + 12, 256).TrimEnd('\0'));
        var reread = ArscParser.Parse(rebuilt).Packages[0].Types[0];
        Assert.Equal(type.ConfigHex, reread.ConfigHex);
        Assert.Equal(42u, reread.Entries[3]!.Value!.Data);
    }

    [Fact]
    public void TruncatedConfig_IsRejectedWithinItsChunk()
    {
        var data = StandardTable(0, false, 28);
        var typeOffset = FindType(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(typeOffset + 20), 1000);
        Assert.Throws<ArscException>(() => ArscParser.Parse(data));
    }

    [Theory]
    [InlineData(0x00000c01u, ResValueType.Dimension, "12dip")]
    [InlineData(0xfffff401u, ResValueType.Dimension, "-12dip")]
    [InlineData(0x40000030u, ResValueType.Fraction, "50%")]
    [InlineData(0x40000031u, ResValueType.Fraction, "50%p")]
    public void ComplexValues_UseAndroidMantissaAndRadix(uint data, ResValueType type, string expected)
        => Assert.Equal(expected, new ResValue { Type = type, Data = data }.FormatValue(null, 0x7f));

    private static int FindType(byte[] data)
    {
        var package = 12 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)));
        var cursor = package + 288;
        while (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor)) != 0x201)
            cursor += checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor + 4)));
        return cursor;
    }

    private static byte[] StandardTable(int encoding, bool compact, int configSize)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        void Header(ushort type, ushort header, uint size) { writer.Write(type); writer.Write(header); writer.Write(size); }
        void Patch(long pos, uint value) { var end = stream.Position; stream.Position = pos; writer.Write(value); stream.Position = end; }
        Header(2, 12, 0); writer.Write(1u);
        writer.Write(Pool());
        var package = stream.Position;
        Header(0x200, 288, 0); writer.Write(0x7fu);
        var name = new byte[256]; Encoding.Unicode.GetBytes("com.example.资源").CopyTo(name, 0); writer.Write(name);
        var typePool = Pool("integer"); var keyPool = Pool("answer");
        writer.Write(288u); writer.Write(1u); writer.Write((uint)(288 + typePool.Length)); writer.Write(1u); writer.Write(2u);
        writer.Write(typePool); writer.Write(keyPool);
        Header(0x202, 16, 32); writer.Write((byte)1); writer.Write((byte)0); writer.Write((ushort)0); writer.Write(4u);
        for (var i = 0; i < 4; i++) writer.Write(0u);
        var typeStart = stream.Position;
        var count = encoding == 1 ? 1 : 4;
        var headerSize = 20 + configSize;
        var offsetsSize = count * (encoding == 2 ? 2 : 4);
        Header(0x201, (ushort)headerSize, (uint)(headerSize + offsetsSize + (compact ? 8 : 16)));
        writer.Write((byte)1); writer.Write((byte)encoding); writer.Write((ushort)0); writer.Write((uint)count);
        writer.Write((uint)(headerSize + offsetsSize)); writer.Write((uint)configSize); writer.Write(new byte[configSize - 4]);
        if (encoding == 1) { writer.Write((ushort)3); writer.Write((ushort)0); }
        else if (encoding == 2) { for (var i = 0; i < 3; i++) writer.Write(ushort.MaxValue); writer.Write((ushort)0); }
        else { for (var i = 0; i < 3; i++) writer.Write(uint.MaxValue); writer.Write(0u); }
        if (compact) { writer.Write((ushort)0); writer.Write((ushort)0x100a); writer.Write(42u); }
        else { writer.Write((ushort)8); writer.Write((ushort)2); writer.Write(0u); writer.Write((ushort)8); writer.Write((byte)0); writer.Write((byte)0x10); writer.Write(42u); }
        Patch(package + 4, (uint)(stream.Position - package)); Patch(4, (uint)stream.Position);
        return stream.ToArray();
    }

    private static byte[] Pool(params ReadOnlySpan<string> strings)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((ushort)1); writer.Write((ushort)28); writer.Write(0u); writer.Write((uint)strings.Length);
        writer.Write(0u); writer.Write(0x100u); writer.Write((uint)(28 + strings.Length * 4)); writer.Write(0u);
        var relative = 0;
        foreach (var text in strings) { writer.Write((uint)relative); relative += 3 + Encoding.UTF8.GetByteCount(text); }
        foreach (var text in strings) { writer.Write((byte)text.Length); var bytes = Encoding.UTF8.GetBytes(text); writer.Write((byte)bytes.Length); writer.Write(bytes); writer.Write((byte)0); }
        while (stream.Position % 4 != 0) writer.Write((byte)0);
        var result = stream.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length); return result;
    }
}