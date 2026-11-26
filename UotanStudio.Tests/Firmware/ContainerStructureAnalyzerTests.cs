using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.VendorFirmware.Pac;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Covers the new-project container analysis beyond super/payload/zip: vendor packages resolved through their
/// format handler, compressed archive variants, the per-entry content probe, and selected-entry extraction.
/// <para>覆盖 super/payload/zip 之外的新建项目容器分析：经格式处理器解析的厂商包、压缩归档变体、
/// 逐条目内容探测与按勾选提取。</para>
/// </summary>
[Collection("Workspace state")]
public sealed class ContainerStructureAnalyzerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public ContainerStructureAnalyzerTests() => FirmwareFormatRegistry.RegisterAll();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Analyze_PacPackage_ListsEntriesAsVendorContainer()
    {
        var source = Path.Combine(_temp.Path, "pac-src");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "boot.img"), BuildExt4Header(0x1000));
        File.WriteAllBytes(Path.Combine(source, "system.img"), BuildExt4Header(0x4000));
        var pacPath = Path.Combine(_temp.Path, "firmware.pac");
        PacBuilder.Build(source, pacPath, cancellationToken: TestContext.Current.CancellationToken);

        var structure = ContainerStructureAnalyzer.Analyze(pacPath);

        Assert.Equal(ContainerKind.Vendor, structure.Kind);
        Assert.Equal("pac", structure.VendorLabel);
        Assert.Equal<string>(["boot.img", "system.img"], [.. structure.Entries.Select(entry => entry.Name)]);
        Assert.All(structure.Entries, entry => Assert.False(entry.FromPayload));
    }

    [Fact]
    public void Analyze_TarGzArchive_ListsInnerEntries()
    {
        var tarPath = Path.Combine(_temp.Path, "images.tar");
        WriteTar(tarPath, [("system.img", BuildExt4Header(2048)), ("boot.img", BuildExt4Header(0x800))]);

        var structure = ContainerStructureAnalyzer.Analyze(tarPath);

        Assert.Equal(ContainerKind.Vendor, structure.Kind);
        Assert.Equal("tar", structure.VendorLabel);
        Assert.Equal<string>(["system.img", "boot.img"], [.. structure.Entries.Select(entry => entry.Name)]);
        Assert.Equal(2048L, structure.Entries[0].SizeBytes);
        Assert.Equal("ext4", structure.Entries[0].InnerFormat);
    }

    [Theory]
    [InlineData(".tar.gz", "tar")]
    [InlineData(".tgz", "tgz")]
    public void Analyze_CompressedTarVariants_ListEntriesAndProbeInnerFormat(string suffix, string expectedLabel)
    {
        var tarPath = Path.Combine(_temp.Path, "images.tar");
        WriteTar(tarPath, [("system.img", BuildExt4Header(0x1200))]);
        var archivePath = Path.Combine(_temp.Path, "images" + suffix);
        WriteGzip(tarPath, archivePath);

        var structure = ContainerStructureAnalyzer.Analyze(archivePath);

        Assert.Equal(ContainerKind.Vendor, structure.Kind);
        Assert.Equal(expectedLabel, structure.VendorLabel);
        var entry = Assert.Single(structure.Entries);
        Assert.Equal("system.img", entry.Name);
        Assert.Equal(0x1200L, entry.SizeBytes);
        Assert.Equal("ext4", entry.InnerFormat);
    }

    [Fact]
    public void Analyze_ZipWithExt4Member_ReportsProbedInnerFormat()
    {
        var zipPath = Path.Combine(_temp.Path, "images.zip");
        WriteZip(zipPath, [("system.img", BuildExt4Header(4096))]);

        var structure = ContainerStructureAnalyzer.Analyze(zipPath);

        Assert.Equal(ContainerKind.Zip, structure.Kind);
        var entry = Assert.Single(structure.Entries);
        Assert.Equal("system.img", entry.Name);
        Assert.Equal("ext4", entry.InnerFormat);
    }

    [Fact]
    public async Task ExtractAsync_VendorContainerSelectedEntry_WritesOnlyThatEntry()
    {
        var source = Path.Combine(_temp.Path, "pac-src");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "boot.img"), BuildExt4Header(0x800));
        File.WriteAllBytes(Path.Combine(source, "system.img"), BuildExt4Header(0x2000));
        var pacPath = Path.Combine(_temp.Path, "firmware.pac");
        PacBuilder.Build(source, pacPath, cancellationToken: TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(_temp.Path, "out");

        var result = await ContainerEntryExtractor.ExtractAsync(
            pacPath, ["boot.img"], outputDirectory: outputDirectory,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal<string>(["boot.img"], [.. result.Entries]);
        var written = Assert.Single(result.OutputPaths);
        Assert.Equal(0x800L, new FileInfo(written).Length);
        Assert.False(File.Exists(Path.Combine(outputDirectory, "system.img")));
    }

    [Fact]
    public void Identify_UnknownBytes_FallsBackToExtensionLabel()
    {
        var label = ContainerEntryFormatProbe.Identify(new byte[] { 0x00, 0x01, 0x02, 0x03 }, "mystery.raw");

        Assert.Equal("raw", label);
    }

    [Fact]
    public void Identify_BootImageHeader_ReportsBootLabel()
    {
        var header = Encoding.ASCII.GetBytes("ANDROID!").Concat(new byte[512]).ToArray();

        Assert.Equal("boot", ContainerEntryFormatProbe.Identify(header, "boot.img"));
    }

    private static byte[] BuildExt4Header(int length)
    {
        Assert.True(length > 0x439);
        var data = new byte[length];
        RandomNumberGenerator.Fill(data.AsSpan(0x440));
        data[0x438] = 0x53;
        data[0x439] = 0xEF;
        return data;
    }

    private static void WriteTar(string path, IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        using var stream = File.Create(path);
        using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var (name, data) in entries)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(data)
            };
            writer.WriteEntry(entry);
        }
    }

    private static void WriteGzip(string sourcePath, string destinationPath)
    {
        using var source = File.OpenRead(sourcePath);
        using var destination = File.Create(destinationPath);
        using var gzip = new System.IO.Compression.GZipStream(destination, System.IO.Compression.CompressionLevel.Fastest);
        source.CopyTo(gzip);
    }

    private static void WriteZip(string path, IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        using var stream = File.Create(path);
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            entryStream.Write(data);
        }
    }
}