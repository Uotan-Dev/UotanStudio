using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Lenovo.Qsb;
using UotanStudio.Firmware.SharedKernel;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class QsbTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-qsb-" + Guid.NewGuid().ToString("N"));

    public QsbTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static string Rawprogram(params (string Filename, long Start, long Sectors, long FileOffset, long SectorSize)[] programs)
    {
        var builder = new StringBuilder("<?xml version=\"1.0\"?>\n<data>\n");
        foreach (var program in programs)
        {
            builder.Append("<program SECTOR_SIZE_IN_BYTES=\"")
                .Append(program.SectorSize)
                .Append("\" file_sector_offset=\"").Append(program.FileOffset)
                .Append("\" filename=\"").Append(program.Filename)
                .Append("\" label=\"").Append(program.Filename)
                .Append("\" num_partition_sectors=\"").Append(program.Sectors)
                .Append("\" physical_partition_number=\"0\" start_sector=\"").Append(program.Start)
                .Append("\" />\n");
        }

        builder.Append("</data>\n");
        return builder.ToString();
    }

    private static byte[] Ext4Chunk(int size, uint totalBlocks, uint logBlockSize)
    {
        var chunk = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(1028), totalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(1048), logBlockSize);
        chunk[1080] = 0x53;
        chunk[1081] = 0xEF;
        return chunk;
    }

    [Fact]
    public void Parse_DeclaredPrograms_ReturnsSectorWindowsInDocumentOrder()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Rawprogram(("system_0.img", 2, 4, 0, 512))));

        var programs = QsbRawprogram.Parse(stream, TestContext.Current.CancellationToken);

        var program = Assert.Single(programs);
        Assert.Equal("system_0.img", program.Filename);
        Assert.Equal(2, program.StartSector);
        Assert.Equal(4, program.NumPartitionSectors);
        Assert.Equal(512, program.SectorSizeInBytes);
    }

    [Fact]
    public void GroupChunks_NamesWithNumericSegment_SortsByIndex()
    {
        var programs = new[]
        {
            new QsbProgram("l", "system_2.img", 0, 1, 0, 512),
            new QsbProgram("l", "system_10.img", 0, 1, 0, 512),
            new QsbProgram("l", "system_1.img", 0, 1, 0, 512),
            new QsbProgram("l", "boot.img", 0, 1, 0, 512)
        };

        var groups = QsbRawprogram.GroupChunks(programs);

        var group = Assert.Single(groups);
        Assert.Equal("system", group.Prefix);
        Assert.Equal(["system_1.img", "system_2.img", "system_10.img"], group.Members.Select(m => m.Filename));
    }

    [Fact]
    public async Task MergeGroupAsync_GapAndOffsets_PlacesPayloadsAtExactSectors()
    {
        var package = Path.Combine(_root, "package");
        Directory.CreateDirectory(package);
        var output = Path.Combine(_root, "merged");
        var first = Ext4Chunk(2048, totalBlocks: 24, logBlockSize: 0);
        for (var index = 0; index < first.Length; index++)
        {
            if (index is < 1024 or >= 1082)
            {
                first[index] = 0xA1;
            }
        }

        var second = new byte[1024];
        second.AsSpan().Fill(0xB2);
        await File.WriteAllBytesAsync(Path.Combine(package, "system_0.img"), first, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(package, "system_1.img"), second, TestContext.Current.CancellationToken);
        var programs = QsbRawprogram.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Rawprogram(
            ("system_0.img", 2, 4, 0, 512),
            ("system_1.img", 20, 2, 1, 512)))), TestContext.Current.CancellationToken);
        var group = Assert.Single(QsbRawprogram.GroupChunks(programs));

        var destination = await QsbRawprogram.MergeGroupAsync(package, group.Prefix, group.Members, output,
            1L << 20, TestContext.Current.CancellationToken);

        var merged = await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken);
        Assert.Equal(25088, merged.Length);
        Assert.Equal(first, merged.AsSpan(0, 2048).ToArray());
        Assert.True(merged.AsSpan(2048, 7680).ToArray().All(b => b == 0));
        Assert.Equal(second, merged.AsSpan(9728, 1024).ToArray());
        Assert.Equal(14336, merged.AsSpan(10752).ToArray().Count(b => b == 0));
    }

    [Fact]
    public async Task MergeGroupAsync_MissingChunk_ThrowsInvalidData()
    {
        var package = Path.Combine(_root, "missing");
        Directory.CreateDirectory(package);
        var programs = new[] { new QsbProgram("l", "system_0.img", 0, 1, 0, 512) };

        await Assert.ThrowsAsync<InvalidDataException>(() => QsbRawprogram.MergeGroupAsync(
            package, "system", programs, Path.Combine(package, "out"), 1 << 20,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MergeGroupAsync_OutputBeyondCeiling_ThrowsInvalidData()
    {
        var package = Path.Combine(_root, "ceiling");
        Directory.CreateDirectory(package);
        await File.WriteAllBytesAsync(Path.Combine(package, "system_0.img"), new byte[600], TestContext.Current.CancellationToken);
        var programs = new[] { new QsbProgram("l", "system_0.img", 0, 1, 100, 512) };

        await Assert.ThrowsAsync<InvalidDataException>(() => QsbRawprogram.MergeGroupAsync(
            package, "system", programs, Path.Combine(package, "out"), 512,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("start_sector=\"x\" filename=\"system_0.img\"", true)]
    [InlineData("filename=\"system_0.img\" start_sector=\"0\" num_partition_sectors=\"1\" file_sector_offset=\"0\"", false)]
    public void Parse_MissingOrNonNumericSectorAttribute_Rejects(string attributeClause, bool expectFailure)
    {
        var xml = $"<?xml version=\"1.0\"?>\n<data>\n<program {attributeClause} />\n</data>\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        if (expectFailure)
        {
            Assert.Throws<InvalidDataException>(() => QsbRawprogram.Parse(stream, TestContext.Current.CancellationToken));
        }
        else
        {
            var programs = QsbRawprogram.Parse(stream, TestContext.Current.CancellationToken);
            var program = Assert.Single(programs);
            Assert.Equal(0, program.FileSectorOffset);
        }
    }

    [Fact]
    public async Task CanHandle_RawprogramVersusForeignXml_DecidesByProgramChunks()
    {
        var raw = Path.Combine(_root, "rawprogram.xml");
        await File.WriteAllTextAsync(raw, Rawprogram(("system_0.img", 0, 4, 0, 512)), TestContext.Current.CancellationToken);
        var foreign = Path.Combine(_root, "other.xml");
        await File.WriteAllTextAsync(foreign, "<config><item/></config>", TestContext.Current.CancellationToken);

        var handler = new QsbFormatHandler();
        Assert.True(handler.CanHandle(raw));
        Assert.False(handler.CanHandle(foreign));
    }

    [Fact]
    public async Task UnpackAsync_TwoChunkGroup_MergesIntoPrefixedImage()
    {
        var package = Path.Combine(_root, "pkg");
        Directory.CreateDirectory(package);
        var first = Ext4Chunk(2048, totalBlocks: 3, logBlockSize: 0);
        await File.WriteAllBytesAsync(Path.Combine(package, "factory_0.img"), first, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(package, "factory_1.img"), new byte[1024], TestContext.Current.CancellationToken);
        var manifest = Path.Combine(package, "rawprogram0.xml");
        await File.WriteAllTextAsync(manifest, Rawprogram(
            ("factory_0.img", 0, 4, 0, 512),
            ("factory_1.img", 4, 2, 0, 512)), TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "pkg-out");

        var result = await new QsbFormatHandler().UnpackAsync(manifest, output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        var merged = Path.Combine(output, "factory.img");
        Assert.True(File.Exists(merged));
        Assert.Equal(3072, new FileInfo(merged).Length);
        var mergedBytes = await File.ReadAllBytesAsync(merged, TestContext.Current.CancellationToken);
        Assert.Equal(first, mergedBytes[..2048]);
        Assert.True(mergedBytes[2048..].All(b => b == 0));
    }

    [Fact]
    public async Task ReadMetadataAsync_CeilingExceeded_FailsWithMessage()
    {
        var package = Path.Combine(_root, "fail");
        Directory.CreateDirectory(package);
        var manifest = Path.Combine(package, "rawprogram.xml");
        await File.WriteAllTextAsync(manifest, Rawprogram(("system_0.img", 0, 1, 0, 512)), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(package, "unrelated.txt"), "x", TestContext.Current.CancellationToken);

        var result = await new QsbFormatHandler().ReadMetadataAsync(
            Path.Combine(package, "unrelated.txt"), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }
}