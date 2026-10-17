using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Arsc;
using UotanStudio.Firmware.AndroidMeta.Resources;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Covers compact entry storage, lazy sparse materialization and configuration deduplication.
/// <para>覆盖紧凑条目存储、稀疏视图惰性物化与配置去重。</para>
/// </summary>
public class ArscModelStorageTests
{
    [Fact]
    public void Parse_SparseChunk_KeepsCompactStorageUntilEntriesAccessed()
    {
        var data = SparseTable(specCount: 6, rows: [(2, true), (5, true)]);

        var type = Assert.Single(Assert.Single(ArscParser.Parse(data).Packages).Types);

        Assert.Null(type.MaterializedEntries);
        Assert.Equal(6, type.EntryCount);
        Assert.Equal(2, type.PresentEntryCount);
        Assert.NotNull(type.FindEntry(2));
        Assert.Null(type.FindEntry(3));

        var entries = type.Entries;
        Assert.Same(entries, type.MaterializedEntries);
        Assert.Equal(6, entries.Count);
        Assert.All([0, 1, 3, 4], index => Assert.Null(entries[index]));
        Assert.Equal(42u, entries[2]!.Value!.Data);
        Assert.Equal(43u, entries[5]!.Value!.Data);
        Assert.Equal(6, type.EntryCount);
        Assert.Equal(2, type.PresentEntryCount);
    }

    [Fact]
    public void Write_ParsedSparseChunk_EmitsHighestIndexBound()
    {
        var parsed = ArscParser.Parse(SparseTable(specCount: 6, rows: [(2, true), (5, true)]));

        var rebuilt = ArscParser.Parse(ArscWriter.Write(parsed));
        var type = Assert.Single(Assert.Single(rebuilt.Packages).Types);

        Assert.Equal(6, type.EntryCount);
        Assert.Equal(6, type.Entries.Count);
        Assert.Equal(42u, type.Entries[2]!.Value!.Data);
        Assert.Equal(43u, type.Entries[5]!.Value!.Data);
        Assert.All([0, 1, 3, 4], index => Assert.Null(type.Entries[index]));
    }

    [Fact]
    public void Parse_SparseIndexBeyondTypeSpecification_IsRejected()
    {
        var data = SparseTable(specCount: 4, rows: [(ushort.MaxValue, true)]);

        Assert.Throws<ArscException>(() => ArscParser.Parse(data));
    }

    [Fact]
    public void Parse_IdenticalConfigBytes_SharesDescriptionAndBytes()
    {
        var data = TwoTypeTable();

        var package = Assert.Single(ArscParser.Parse(data).Packages);
        Assert.Equal(2, package.Types.Count);
        Assert.Same(package.Types[0].ConfigBytes, package.Types[1].ConfigBytes);
        Assert.Same(package.Types[0].ConfigDescription, package.Types[1].ConfigDescription);
        Assert.Equal("zh-rCN-d240", package.Types[0].ConfigDescription);
        Assert.Equal(Convert.ToHexString(package.Types[0].ConfigBytes), package.Types[0].ConfigHex);
    }

    [Fact]
    public void Intern_ParsedPoolWithDuplicates_ReturnsFirstOccurrence()
    {
        var pool = new AXmlStringPool();
        var bytes = Pool(["x", "x", "y"]);
        pool.Parse(bytes, 0, bytes.Length);

        Assert.Equal(3, pool.Count);
        Assert.Equal(0, pool.Intern("x"));
        Assert.Equal(2, pool.Intern("y"));
        Assert.Equal(3, pool.Intern("z"));
    }

    [Fact]
    public void Parse_Utf16Pool_DecodesCodeUnits()
    {
        var pool = new AXmlStringPool();
        var bytes = Utf16Pool(["manifest", "版本-\U0001F600"]);

        pool.Parse(bytes, 0, bytes.Length);

        Assert.False(pool.IsUtf8);
        Assert.Equal("manifest", pool.GetString(0));
        Assert.Equal("版本-\U0001F600", pool.GetString(1));
    }

    private static byte[] SparseTable(int specCount, (ushort Index, bool Present)[] rows)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        void Header(ushort type, ushort header, uint size)
        {
            writer.Write(type);
            writer.Write(header);
            writer.Write(size);
        }

        var packageStart = 12 + Pool(["value"]).Length;
        Header(2, 12, 0);
        writer.Write(1u);
        writer.Write(Pool(["value"]));
        Header(0x200, 288, 0);
        writer.Write(0x7fu);
        var name = new byte[256];
        Encoding.Unicode.GetBytes("com.example.storage").CopyTo(name, 0);
        writer.Write(name);
        var typePool = Pool(["string"]);
        var keyPool = Pool(["key0"]);
        writer.Write(288u);
        writer.Write(1u);
        writer.Write((uint)(288 + typePool.Length));
        writer.Write(1u);
        writer.Write(0u);
        writer.Write(typePool);
        writer.Write(keyPool);
        Header(0x202, 16, (uint)(16 + specCount * 4));
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write((ushort)0);
        writer.Write((uint)specCount);
        for (var i = 0; i < specCount; i++)
        {
            writer.Write(0u);
        }
        var headerSize = 20 + 28;
        var entriesStart = headerSize + rows.Length * 4;
        Header(0x201, (ushort)headerSize, (uint)(entriesStart + rows.Length * 16));
        writer.Write((byte)1);
        writer.Write((byte)1);
        writer.Write((ushort)0);
        writer.Write((uint)rows.Length);
        writer.Write((uint)entriesStart);
        writer.Write(28u);
        writer.Write(new byte[24]);
        for (var i = 0; i < rows.Length; i++)
        {
            writer.Write(rows[i].Index);
            writer.Write((ushort)(i * 16 / 4));
        }
        for (var i = 0; i < rows.Length; i++)
        {
            writer.Write((ushort)8);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write((ushort)8);
            writer.Write((byte)0);
            writer.Write((byte)0x10);
            writer.Write((uint)(42 + i));
        }
        var end = stream.Position;
        stream.Position = packageStart + 4;
        writer.Write((uint)(end - packageStart));
        stream.Position = end;
        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length);
        return result;
    }

    private static byte[] TwoTypeTable()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        void Header(ushort type, ushort header, uint size)
        {
            writer.Write(type);
            writer.Write(header);
            writer.Write(size);
        }

        Header(2, 12, 0);
        writer.Write(1u);
        writer.Write(Pool(["value"]));
        var packageStart = stream.Position;
        Header(0x200, 288, 0);
        writer.Write(0x7fu);
        var name = new byte[256];
        Encoding.Unicode.GetBytes("com.example.config").CopyTo(name, 0);
        writer.Write(name);
        var typePool = Pool(["string"]);
        var keyPool = Pool(["key0"]);
        writer.Write(288u);
        writer.Write(1u);
        writer.Write((uint)(288 + typePool.Length));
        writer.Write(1u);
        writer.Write(0u);
        writer.Write(typePool);
        writer.Write(keyPool);
        Header(0x202, 16, 32);
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write((ushort)0);
        writer.Write(4u);
        for (var i = 0; i < 4; i++)
        {
            writer.Write(0u);
        }
        WriteType(writer, Header);
        WriteType(writer, Header);
        var end = stream.Position;
        stream.Position = packageStart + 4;
        writer.Write((uint)(end - packageStart));
        stream.Position = end;
        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length);
        return result;
    }

    private static void WriteType(BinaryWriter writer, Action<ushort, ushort, uint> header)
    {
        var config = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(config, 28);
        BinaryPrimitives.WriteUInt16LittleEndian(config.AsSpan(14), 240);
        config[8] = (byte)'z';
        config[9] = (byte)'h';
        config[10] = (byte)'C';
        config[11] = (byte)'N';
        var headerSize = 20 + config.Length;
        var entriesStart = headerSize + 4;
        header(0x201, (ushort)headerSize, (uint)(entriesStart + 16));
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write((ushort)0);
        writer.Write(1u);
        writer.Write((uint)entriesStart);
        writer.Write((uint)config.Length);
        writer.Write(config.AsSpan(4));
        writer.Write(0u);
        writer.Write((ushort)8);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write((ushort)8);
        writer.Write((byte)0);
        writer.Write((byte)0x10);
        writer.Write(7u);
    }

    private static byte[] Pool(string[] strings)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((ushort)1);
        writer.Write((ushort)28);
        writer.Write(0u);
        writer.Write((uint)strings.Length);
        writer.Write(0u);
        writer.Write(0x100u);
        writer.Write((uint)(28 + strings.Length * 4));
        writer.Write(0u);
        var relative = 0;
        var payloads = new List<byte[]>();
        foreach (var text in strings)
        {
            var utf8 = Encoding.UTF8.GetBytes(text);
            var payload = new byte[(3 + utf8.Length + 3) & ~3];
            payload[0] = (byte)text.Length;
            payload[1] = (byte)utf8.Length;
            utf8.CopyTo(payload, 2);
            payloads.Add(payload);
            writer.Write((uint)relative);
            relative += payload.Length;
        }
        foreach (var payload in payloads)
        {
            writer.Write(payload);
        }
        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length);
        return result;
    }

    private static byte[] Utf16Pool(string[] strings)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((ushort)1);
        writer.Write((ushort)28);
        writer.Write(0u);
        writer.Write((uint)strings.Length);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((uint)(28 + strings.Length * 4));
        writer.Write(0u);
        var relative = 0;
        var payloads = new List<byte[]>();
        foreach (var text in strings)
        {
            var payload = new byte[((2 + text.Length * 2 + 2) + 3) & ~3];
            BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)text.Length);
            Encoding.Unicode.GetBytes(text).CopyTo(payload, 2);
            payloads.Add(payload);
            writer.Write((uint)relative);
            relative += payload.Length;
        }
        foreach (var payload in payloads)
        {
            writer.Write(payload);
        }
        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length);
        return result;
    }
}