using System.Buffers.Binary;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.DeviceTree.Dt;
using UotanStudio.Services;
using UotanStudio.Services.Ui.Firmware;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Nodes;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DtboRepackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-dtbo-repack-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private string Input => Path.Combine(_root, "dtbo.img");
    private string Output => Path.Combine(_root, "rebuilt.img");
    private string Unpacked => Path.Combine(_root, "unpacked");

    public DtboRepackTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Dtb(string marker)
    {
        var tree = new DeviceTree();
        tree.Root.AddPropertyString("compatible", marker);
        tree.Root.AddProperty("data", Enumerable.Repeat((byte)0xA5, 4096).ToArray());
        return DtbFormat.Write(tree);
    }

    private void CreateImage(uint version = 1, bool trailer = false)
    {
        var image = DtboFormat.Pack([Dtb("original-0"), Dtb("original-1"), Dtb("original-2")],
            Enumerable.Range(0, 3).Select(i => new DtboEntry
            {
                Id = (uint)(0x100 + i),
                Rev = (uint)(5 + i),
                Custom0 = (uint)(0xA0 + i),
                Custom1 = 0x12345678,
                Custom2 = 0x89ABCDEF,
                Custom3 = 0xFEDCBA98
            }).ToArray(), pageSize: 4096, version: version);
        Array.Resize(ref image, image.Length + 32768);
        if (trailer) "AVBf"u8.CopyTo(image.AsSpan(image.Length - 64));
        File.WriteAllBytes(Input, image);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public async Task Handler_UnchangedDirectoryRoundTrip_PreservesEntireImage(uint version)
    {
        CreateImage(version, trailer: true);
        var handler = new DtFormatHandler();
        var unpack = await handler.UnpackAsync(Input, Unpacked, cancellationToken: Cancellation);
        Assert.True(unpack.Success, unpack.Summary);
        Assert.True(File.Exists(Path.Combine(Unpacked, RepackMetadata.FileName)));
        Assert.False(File.Exists(Path.Combine(Unpacked, "dtbo.json")));
        var result = await handler.PackAsync(Unpacked, Output, Cancellation);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(File.ReadAllBytes(Input), File.ReadAllBytes(Output));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void ModifiedOverlay_PreservesMetadataCompressionAndCapacity(uint version)
    {
        CreateImage(version);
        DtboFormat.Unpack(Input, Unpacked, cancellationToken: Cancellation);
        var changed = Dtb("modified");
        File.WriteAllBytes(Path.Combine(Unpacked, "overlay_01.dtb"), changed);
        DtboFormat.PackDirectory(Unpacked, Output, Cancellation);
        var (before, originalEntries, _) = DtboFormat.Read(File.ReadAllBytes(Input));
        var (after, entries, blobs) = DtboFormat.Read(File.ReadAllBytes(Output));
        Assert.Equal(before.PageSize, after.PageSize);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(new FileInfo(Input).Length, new FileInfo(Output).Length);
        Assert.Equal(changed, blobs[1]);
        Assert.Equal(Dtb("original-0"), blobs[0]);
        Assert.Equal(Dtb("original-2"), blobs[2]);
        for (var i = 0; i < entries.Count; i++)
            Assert.Equal(originalEntries[i].ToBytes()[8..], entries[i].ToBytes()[8..]);
        if (version == 1)
        {
            Assert.True(entries[1].DtSize < blobs[1].Length);
            Assert.True(entries[2].DtSize < blobs[2].Length);
        }
    }

    [Fact]
    public void Manifest_ChangesOrderAndMetadata()
    {
        CreateImage();
        DtboFormat.Unpack(Input, Unpacked, cancellationToken: Cancellation);
        EditManifest(json =>
        {
            var entries = json["entries"]!.AsArray();
            var first = entries[0]!.DeepClone();
            entries[0] = entries[2]!.DeepClone();
            entries[2] = first;
            entries[0]!["metadata"]!["id"] = 777;
            entries[0]!["metadata"]!["custom0"] = 0xB1;
        });
        DtboFormat.PackDirectory(Unpacked, Output, Cancellation);
        var (_, entries, dtbs) = DtboFormat.Read(File.ReadAllBytes(Output));
        Assert.Equal(Dtb("original-2"), dtbs[0]);
        Assert.Equal(Dtb("original-0"), dtbs[2]);
        Assert.Equal(777u, entries[0].Id);
        Assert.Equal(0xB1u, entries[0].Custom0);
    }

    [Fact]
    public void ModifiedAuthenticatedImage_FailsWithoutReplacingOutput()
    {
        CreateImage(trailer: true);
        DtboFormat.Unpack(Input, Unpacked, cancellationToken: Cancellation);
        File.WriteAllBytes(Path.Combine(Unpacked, "overlay_00.dtb"), Dtb("modified"));
        File.WriteAllText(Output, "keep");
        var error = Assert.Throws<InvalidDataException>(() => DtboFormat.PackDirectory(Unpacked, Output, Cancellation));
        Assert.Contains("AVB", error.Message);
        Assert.Equal("keep", File.ReadAllText(Output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void ExtendedHeader_UnchangedAndEdited_PreservesExtension()
    {
        var image = DtboFormat.Pack(Dtb("original"));
        var expanded = new byte[image.Length + 48 + 8192];
        image.AsSpan(0, 32).CopyTo(expanded);
        expanded.AsSpan(32, 48).Fill(0x5A);
        image.AsSpan(32).CopyTo(expanded.AsSpan(80));
        BinaryPrimitives.WriteUInt32BigEndian(expanded.AsSpan(4), (uint)image.Length + 48);
        BinaryPrimitives.WriteUInt32BigEndian(expanded.AsSpan(8), 48);
        BinaryPrimitives.WriteUInt32BigEndian(expanded.AsSpan(20), 80);
        BinaryPrimitives.WriteUInt32BigEndian(expanded.AsSpan(84), 112);
        File.WriteAllBytes(Input, expanded);
        DtboFormat.Unpack(Input, Unpacked, cancellationToken: Cancellation);
        DtboFormat.PackDirectory(Unpacked, Output, Cancellation);
        Assert.Equal(expanded, File.ReadAllBytes(Output));
        File.WriteAllBytes(Path.Combine(Unpacked, "overlay_00.dtb"), Dtb("modified"));
        DtboFormat.PackDirectory(Unpacked, Output, Cancellation);
        Assert.Equal(expanded[32..80], File.ReadAllBytes(Output)[32..80]);
        Assert.Equal(Dtb("modified"), DtboFormat.Read(File.ReadAllBytes(Output)).Dtbs[0]);
    }

    [Theory]
    [InlineData("../outside.dtb")]
    [InlineData("..\\outside.dtb")]
    [InlineData("C:\\outside.dtb")]
    public void Manifest_PathTraversalIsRejected(string path)
    {
        CreateImage();
        DtboFormat.Unpack(Input, Unpacked, cancellationToken: Cancellation);
        EditManifest(json => json["entries"]![0]!["file"] = path);
        Assert.Throws<InvalidDataException>(() => DtboFormat.PackDirectory(Unpacked, Output, Cancellation));
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public void OutputBudgetFailure_DoesNotPublishPartialExtraction()
    {
        CreateImage();
        Assert.Throws<InvalidDataException>(() => DtboFormat.Unpack(Input, Unpacked,
            new UnpackOptions { MaximumOutputBytes = 64 }, Cancellation));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Unpacked));
    }

    [Fact]
    public void NoOverwrite_DoesNotModifyExistingExtraction()
    {
        CreateImage();
        DtboFormat.Unpack(Input, Unpacked, cancellationToken: Cancellation);
        var overlay = Path.Combine(Unpacked, "overlay_00.dtb");
        File.WriteAllText(overlay, "keep");
        Assert.Throws<IOException>(() => DtboFormat.Unpack(Input, Unpacked,
            new UnpackOptions { Overwrite = false }, Cancellation));
        Assert.Equal("keep", File.ReadAllText(overlay));
    }

    [Fact]
    public void CancellationAfterFirstOverlay_DoesNotPublishStagedFiles()
    {
        CreateImage();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        Assert.ThrowsAny<OperationCanceledException>(() => DtboFormat.Unpack(Input, Unpacked,
            new UnpackOptions { Progress = new CancellingProgress(cancellation) }, cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Unpacked));
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IFirmwareProgress
    {
        public void Report(double percent, string message) => cancellation.Cancel();
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void TruncatedCompressionTrailer_FailsBeforePublishing(uint compression)
    {
        var image = DtboFormat.Pack([Dtb("original")], [new DtboEntry { Custom0 = compression }], version: 1);
        Array.Resize(ref image, image.Length - 2);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4), (uint)image.Length);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(32), (uint)image.Length - 64);
        File.WriteAllBytes(Input, image);
        Assert.ThrowsAny<InvalidDataException>(() => DtboFormat.Unpack(Input, Unpacked, cancellationToken: Cancellation));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Unpacked));
    }

    [Theory]
    [InlineData(4, 32u)]
    [InlineData(16, uint.MaxValue)]
    [InlineData(20, uint.MaxValue)]
    [InlineData(28, 2u)]
    [InlineData(36, 0u)]
    public void InvalidTableBoundsOrVersion_IsRejected(int offset, uint value)
    {
        var image = DtboFormat.Pack(Dtb("original"));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset), value);
        Assert.Throws<InvalidDataException>(() => DtboFormat.Read(image));
    }

    [Fact]
    public void MemoryRead_EnforcesCombinedOutputBudget()
    {
        var blob = Dtb("original");
        var image = DtboFormat.Pack([blob, blob]);
        Assert.Throws<InvalidDataException>(() => DtboFormat.Read(image, blob.Length));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void TruncatedHeader_ThrowsFormatError(int length)
        => Assert.Throws<InvalidDataException>(() => DtboFormat.Read(new byte[length]));

    [Fact]
    public async Task CancelledHandlerPack_PreservesExistingOutput()
    {
        CreateImage();
        File.WriteAllText(Output, "keep");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DtFormatHandler().PackAsync(Input, Output, cancellation.Token));
        Assert.Equal("keep", File.ReadAllText(Output));
    }

    [Fact]
    public async Task Handler_DirectAndInPlaceRepack_PreservesImage()
    {
        CreateImage(trailer: true);
        var expected = File.ReadAllBytes(Input);
        var result = await new DtFormatHandler().PackAsync(Input, Output, Cancellation);
        Assert.True(result.Success, result.Summary);
        result = await new DtFormatHandler().PackAsync(Input, Input, Cancellation);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(expected, File.ReadAllBytes(Output));
        Assert.Equal(expected, File.ReadAllBytes(Input));
    }

    [Fact]
    public async Task ImagePanel_DirectoryAutoDetection_ProducesDtbo()
    {
        CreateImage();
        Assert.True((await new DtFormatHandler().UnpackAsync(Input, Unpacked, cancellationToken: Cancellation)).Success);
        File.WriteAllBytes(Path.Combine(Unpacked, "overlay_00.dtb"), Dtb("panel-modified"));
        Assert.True(PackingService.BuildImage(Unpacked, Output).Success);
        Assert.Equal(Dtb("panel-modified"), DtboFormat.Read(File.ReadAllBytes(Output)).Dtbs[0]);
    }

    [Fact]
    public void NewManifest_WithoutOriginal_BuildsImage()
    {
        Directory.CreateDirectory(Unpacked);
        File.WriteAllBytes(Path.Combine(Unpacked, "board.dtb"), Dtb("new-board"));
        File.WriteAllText(Path.Combine(Unpacked, RepackMetadata.FileName), """
            {"schema":"UotanStudio.Firmware.Repack","version":1,"formatName":"Device Tree (DTB/DTBO)",
             "canPackDirectory":true,"configuration":{
             "formatVersion":1,"entries":[{"file":"board.dtb","metadata":{"id":99,"rev":8,"custom0":123}}]}}
            """);
        DtboFormat.PackDirectory(Unpacked, Output, Cancellation);
        var (header, entries, blobs) = DtboFormat.Read(File.ReadAllBytes(Output));
        Assert.Equal(0u, header.Version);
        Assert.Equal(99u, entries[0].Id);
        Assert.Equal(123u, entries[0].Custom0);
        Assert.Equal(Dtb("new-board"), blobs[0]);
        Assert.Equal((long)header.TotalSize, new FileInfo(Output).Length);
    }

    [Fact]
    public void TooSmallCapacity_FailsAtomically_ExplicitResizeSucceeds()
    {
        CreateImage();
        DtboFormat.Unpack(Input, Unpacked, cancellationToken: Cancellation);
        EditManifest(json => json["imageSize"] = 1);
        File.WriteAllText(Output, "keep");
        Assert.Throws<InvalidDataException>(() => DtboFormat.PackDirectory(Unpacked, Output, Cancellation));
        Assert.Equal("keep", File.ReadAllText(Output));
        EditManifest(json => json["imageSize"] = 0);
        DtboFormat.PackDirectory(Unpacked, Output, Cancellation);
        Assert.Equal(Dtb("original-0"), DtboFormat.Read(File.ReadAllBytes(Output)).Dtbs[0]);
    }

    [Fact]
    public async Task WorkflowNodes_UnpackThenPack_UsesDtboBackend()
    {
        CreateImage();
        var unpack = FirmwareNodeViewModel.CreateUnpackNode();
        unpack.Input.Single(p => p.Title == "Firmware Path").Data = Input;
        var context = new TestExecutionContext(Cancellation);
        await unpack.ExecuteAsync(context);
        var directory = Assert.IsType<string>(unpack.Output.Single(p => p.Title == "Output Dir").Data);
        var pack = FirmwareNodeViewModel.CreatePackNode();
        pack.Input.Single(p => p.Title == "Input Dir").Data = directory;
        pack.Input.Single(p => p.Title == "Config").Data = Output;
        await pack.ExecuteAsync(context);
        Assert.Equal(Output, pack.Output.Single(p => p.Title == "Firmware").Data);
        Assert.Equal(File.ReadAllBytes(Input), File.ReadAllBytes(Output));
    }

    private sealed class TestExecutionContext(CancellationToken cancellationToken) : INodeExecutionContext, IUnifiedLog
    {
        public IUnifiedLog Log => this;
        public CancellationToken CancellationToken => cancellationToken;
        public void ReportProgress(double progress) { }
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }

    private void EditManifest(Action<JsonObject> edit)
    {
        var path = Path.Combine(Unpacked, RepackMetadata.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(json["configuration"]!.AsObject());
        File.WriteAllText(path, json.ToJsonString());
    }
}