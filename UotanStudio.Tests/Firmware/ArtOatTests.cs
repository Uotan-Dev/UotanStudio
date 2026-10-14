using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Art;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ArtOatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-oat-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ArtOatTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, 131)]
    [InlineData(true, 131)]
    [InlineData(true, 124, true)]
    [InlineData(true, 131, true)]
    public void Inspect_VersionedOatAndVdex_BindsLocationsAndClasses(bool elf, int version = 124, bool elf32 = false)
    {
        var (oat, vdex) = Fixture(version);
        if (elf32)
        {
            Put32(oat, 12, 4);
        }
        var result = OatFile.Inspect(Write("test.odex", elf ? Elf(oat, elf32) : oat), Write("test.vdex", vdex), Token);
        Assert.Equal(version, result.Version);
        Assert.Equal(elf32 ? 4u : 5u, result.InstructionSet);
        Assert.Equal("quicken", result.Metadata["compiler-filter"]);
        var dex = Assert.Single(result.DexFiles);
        Assert.Equal("/system/app/Example.apk", dex.Location);
        Assert.Equal(28, dex.Dex.Offset);
        Assert.Single(dex.ClassOffsets);
        if (version == 131)
        {
            Assert.Equal(10, dex.LayoutHints.Count);
            Assert.Equal(112u, dex.LayoutHints[0].Offset);
            var mapping = Assert.Single(dex.MethodMappings);
            Assert.Equal(1, mapping.MethodIndex);
            Assert.Equal(0x8000, mapping.IndexMask);
            Assert.Equal(8u, mapping.BssOffset);
        }
        else
        {
            Assert.Empty(dex.LayoutHints);
            Assert.Empty(dex.MethodMappings);
        }
    }

    [Theory]
    [InlineData("records")]
    [InlineData("layout")]
    [InlineData("mapping")]
    [InlineData("underflow")]
    [InlineData("architecture")]
    public void Inspect_Invalid131Extensions_Rejects(string mutation)
    {
        var (oat, vdex) = Fixture(131);
        switch (mutation)
        {
            case "records": Put32(oat, 24, 4); break;
            case "layout": Put32(oat, oat.Length - 12 - 80, uint.MaxValue); break;
            case "mapping": Put32(oat, oat.Length - 12, uint.MaxValue); break;
            case "underflow": Put32(oat, oat.Length - 4, 0); break;
            case "architecture": Put32(oat, 12, 4); break;
        }
        Assert.Throws<InvalidDataException>(() =>
            OatFile.Inspect(Write("bad.odex", Elf(oat)), Write("bad.vdex", vdex), Token));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("checksum")]
    [InlineData("offset")]
    [InlineData("count")]
    [InlineData("metadata")]
    [InlineData("class")]
    [InlineData("lookup")]
    [InlineData("truncated")]
    [InlineData("companion")]
    public void Inspect_MalformedOrMismatchedPair_Rejects(string mutation)
    {
        var (oat, vdex) = Fixture();
        const int record = 72 + 24;
        var fields = record + 4 + Encoding.UTF8.GetByteCount("/system/app/Example.apk");
        switch (mutation)
        {
            case "version": oat[4] = (byte)'9'; break;
            case "checksum": oat[fields] ^= 1; break;
            case "offset": Put32(oat, fields + 4, 0); break;
            case "count": Put32(oat, 20, 2); break;
            case "metadata": oat[95] = 1; break;
            case "class": Put32(oat, fields + 8, uint.MaxValue); break;
            case "lookup": Put32(oat, fields + 12, uint.MaxValue); break;
            case "truncated": oat = oat[..70]; break;
            case "companion": vdex = ArtVdexFixture.Fixture(1, 10).Vdex; break;
        }
        var oatPath = Write("bad.odex", oat);
        var vdexPath = Write("bad.vdex", vdex);
        if (mutation == "version")
        {
            Assert.Throws<NotSupportedException>(() => OatFile.Inspect(oatPath, vdexPath, Token));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => OatFile.Inspect(oatPath, vdexPath, Token));
        }
    }

    [Fact]
    public void Inspect_Cancelled_Throws()
    {
        var (oat, vdex) = Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            OatFile.Inspect(Write("test.odex", oat), Write("test.vdex", vdex), cancellation.Token));
    }

    [Fact]
    public void WriteDexHashes_AssembledDex_StoredChecksumMatchesOneShotAdler32()
    {
        var (_, dex) = ArtVdexFixture.Fixture(1);
        var bytes = (byte[])dex.Clone();
        using var stream = new MemoryStream(bytes);
        var data = new ArtData(stream, 0, bytes.Length, Token);
        data.WriteDexHashes();
        Assert.Equal(UotanStudio.Hash.Adler32.Adler32.Compute(bytes.AsSpan(12)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        data.VerifyDexHashes();
    }

    private static (byte[] Oat, byte[] Vdex) Fixture(int version = 124)
    {
        var vdex = ArtVdexFixture.Fixture(1, version == 124 ? 6 : 10).Vdex;
        var metadata = "compiler-filter\0quicken\0"u8.ToArray();
        Assert.Equal(24, metadata.Length);
        var location = "/system/app/Example.apk"u8.ToArray();
        var headerSize = version == 124 ? 72 : 76;
        var record = headerSize + metadata.Length + (version == 131 ? 8 : 0);
        var recordsEnd = record + 4 + location.Length + (version == 124 ? 16 : 24);
        var classTable = (recordsEnd + 3) & ~3;
        var bytes = new byte[classTable + 8 + (version == 131 ? 92 : 0)];
        (version == 124 ? "oat\n124\0"u8 : "oat\n131\0"u8).CopyTo(bytes);
        Put32(bytes, 12, 5);
        Put32(bytes, 20, 1);
        Put32(bytes, headerSize - 4, (uint)metadata.Length);
        metadata.CopyTo(bytes, headerSize);
        Put32(bytes, record, (uint)location.Length);
        location.CopyTo(bytes, record + 4);
        var fields = record + 4 + location.Length;
        Put32(bytes, fields, BinaryPrimitives.ReadUInt32LittleEndian(vdex.AsSpan(24)));
        Put32(bytes, fields + 4, 28);
        Put32(bytes, fields + 8, (uint)classTable);
        Put32(bytes, classTable, (uint)(classTable + 4));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(classTable + 6), 2);
        if (version == 131)
        {
            Put32(bytes, 24, (uint)record);
            var layout = classTable + 8;
            var mapping = layout + 80;
            Put32(bytes, fields + 16, (uint)layout);
            Put32(bytes, fields + 20, (uint)mapping);
            Put32(bytes, layout, 112);
            Put32(bytes, layout + 4, 32);
            Put32(bytes, mapping, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(mapping + 4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(mapping + 6), 0x8000);
            Put32(bytes, mapping + 8, 8);
        }
        return (bytes, vdex);
    }

    private static byte[] Elf(byte[] oat, bool elf32 = false)
    {
        var names = "\0.shstrtab\0.rodata\0"u8.ToArray();
        if (elf32)
        {
            const int nameOffset = 52 + 3 * 40;
            var contentOffset = (nameOffset + names.Length + 3) & ~3;
            var image = new byte[contentOffset + oat.Length];
            "\u007fELF"u8.CopyTo(image);
            image[4] = 1;
            image[5] = 1;
            image[6] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18), 3);
            Put32(image, 20, 1);
            Put32(image, 32, 52);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(40), 52);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(46), 40);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(48), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(50), 1);
            Put32(image, 92, 1);
            Put32(image, 96, 3);
            Put32(image, 108, nameOffset);
            Put32(image, 112, (uint)names.Length);
            Put32(image, 132, 11);
            Put32(image, 136, 1);
            Put32(image, 148, (uint)contentOffset);
            Put32(image, 152, (uint)oat.Length);
            names.CopyTo(image, nameOffset);
            oat.CopyTo(image, contentOffset);
            return image;
        }
        const int namesStart = 64 + 3 * 64;
        var oatStart = (namesStart + names.Length + 7) & ~7;
        var bytes = new byte[oatStart + oat.Length];
        "\u007fELF"u8.CopyTo(bytes);
        bytes[4] = 2;
        bytes[5] = 1;
        bytes[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 62);
        Put32(bytes, 20, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(58), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(60), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(62), 1);
        Put32(bytes, 128, 1);
        Put32(bytes, 132, 3);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(152), namesStart);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(160), (ulong)names.Length);
        Put32(bytes, 192, 11);
        Put32(bytes, 196, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(216), (ulong)oatStart);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(224), (ulong)oat.Length);
        names.CopyTo(bytes, namesStart);
        oat.CopyTo(bytes, oatStart);
        return bytes;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    public void Dispose() => Directory.Delete(_root, true);
}