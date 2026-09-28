using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks modern DEX reference tables against independent layouts. <para>依据独立布局检查现代 DEX 引用表。</para></summary>
public class DexModernFormatTests
{
    [Fact]
    public void Dex038_IndependentMethodHandleAndCallSiteSurviveRebuild()
    {
        var parsed = DexFile.Load(StandardDex038());
        Assert.Equal((ushort)4, Assert.Single(parsed.MethodHandles).Kind);
        var callSite = Assert.Single(parsed.CallSites);
        Assert.Equal(3, callSite.Count);
        Assert.Equal(EncodedValueType.MethodHandle, callSite[0].Type);
        Assert.Equal(3u, Convert.ToUInt32(callSite[1].Value));

        var rebuilt = parsed.ToArray();
        var reloaded = DexFile.Load(rebuilt);
        Assert.Single(reloaded.MethodHandles);
        Assert.Equal(3u, Convert.ToUInt32(reloaded.CallSites[0][1].Value));
        var dataOffset = U32(rebuilt, 108);
        Assert.Equal((uint)rebuilt.Length - dataOffset, U32(rebuilt, 104));
        Assert.True(U32(rebuilt, 60) < dataOffset);
        Assert.True(U32(rebuilt, 68) < dataOffset);
        Assert.True(U32(rebuilt, 76) < dataOffset);
        Assert.True(U32(rebuilt, 92) < dataOffset);
        Assert.Equal(SHA1.HashData(rebuilt.AsSpan(32)), rebuilt.AsSpan(12, 20).ToArray());
        Assert.Contains(ReadMap(rebuilt), item => item.Type == 7 && item.Count == 1);
        Assert.Contains(ReadMap(rebuilt), item => item.Type == 8 && item.Count == 1);
    }

    [Theory]
    [InlineData("036")]
    [InlineData("041")]
    [InlineData("999")]
    public void UnsupportedVersions_AreExplicitlyRejected(string version)
    {
        var data = StandardDex038();
        Encoding.ASCII.GetBytes(version).CopyTo(data, 4);
        Assert.Throws<DexException>(() => DexFile.Load(data));
        Assert.Throws<DexException>(() => new DexFile(version).ToArray());
    }

    [Fact]
    public void DeclaredFileSizeMismatch_IsRejected()
    {
        var data = StandardDex038();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), (uint)data.Length - 1);
        Assert.Throws<DexException>(() => DexFile.Load(data));
    }

    private static IEnumerable<(ushort Type, uint Count, uint Offset)> ReadMap(byte[] data)
    {
        var map = checked((int)U32(data, 52));
        var count = U32(data, map);
        for (var i = 0; i < count; i++)
        {
            var pos = map + 4 + i * 12;
            yield return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos)), U32(data, pos + 4), U32(data, pos + 8));
        }
    }

    private static byte[] StandardDex038()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(new byte[168]);
        var strings = new[] { "LFixture;", "V", "bootstrap", "call" };
        var offsets = new uint[strings.Length];
        for (var i = 0; i < strings.Length; i++)
        {
            offsets[i] = (uint)stream.Position; writer.Write((byte)strings[i].Length);
            writer.Write(Encoding.UTF8.GetBytes(strings[i])); writer.Write((byte)0);
        }
        var callSite = (uint)stream.Position;
        writer.Write(new byte[] { 3, 0x16, 0, 0x17, 3, 0x15, 0 });
        while (stream.Position % 4 != 0) writer.Write((byte)0);
        var map = (uint)stream.Position;
        var entries = new (ushort Type, uint Count, uint Offset)[]
        { (0, 1, 0), (1, 4, 112), (2, 2, 128), (3, 1, 136), (5, 1, 148),
            (7, 1, 156), (8, 1, 160), (0x2002, 4, 168), (0x2005, 1, callSite), (0x1000, 1, map) };
        writer.Write((uint)entries.Length);
        foreach (var entry in entries) { writer.Write(entry.Type); writer.Write((ushort)0); writer.Write(entry.Count); writer.Write(entry.Offset); }
        var data = stream.ToArray();
        void Put(int pos, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos), value);
        Encoding.ASCII.GetBytes("dex\n038\0").CopyTo(data, 0);
        Put(32, (uint)data.Length); Put(36, 112); Put(40, 0x12345678); Put(52, map);
        Put(56, 4); Put(60, 112); Put(64, 2); Put(68, 128); Put(72, 1); Put(76, 136);
        Put(88, 1); Put(92, 148); Put(104, (uint)data.Length - 168); Put(108, 168);
        for (var i = 0; i < offsets.Length; i++) Put(112 + i * 4, offsets[i]);
        Put(128, 0); Put(132, 1); Put(136, 1); Put(140, 1); Put(152, 2); Put(156, callSite);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(160), 4);
        SHA1.HashData(data.AsSpan(32)).CopyTo(data, 12);
        uint a = 1, b = 0;
        foreach (var value in data.AsSpan(12)) { a = (a + value) % 65521; b = (b + a) % 65521; }
        Put(8, b << 16 | a);
        return data;
    }

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
}