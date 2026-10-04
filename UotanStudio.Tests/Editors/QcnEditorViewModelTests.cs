using System.Buffers.Binary;
using UotanStudio.Firmware.Radio.Qcn;
using UotanStudio.Tests.Firmware;
using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.Editors;

[Collection("Workspace state")]
public sealed class QcnEditorViewModelTests
{
    private static byte[] Record(ushort id, ushort flags, byte[] data)
    {
        var record = new byte[136];
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0, 2), 136);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4, 2), id);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6, 2), flags);
        data.CopyTo(record, 8);
        return record;
    }

    private static byte[] ImeiBytes(string imei)
    {
        Span<byte> encoded = stackalloc byte[9];
        Assert.True(QcnImeiCodec.TryEncode(imei, encoded));
        return [.. encoded.ToArray()];
    }

    private static byte[] CreateQcnFile()
    {
        var builder = new CompoundFileBuilder();
        builder.AddStream("File_Version", [0x02, 0x00, 0x00, 0x00, 0x00, 0x00]);
        builder.AddStream("NV_NUMBERED_ITEMS/NV_ITEM_ARRAY",
            new[]
            {
                Record(85, 0, "000000"u8.ToArray()),
                Record(550, 0, ImeiBytes("490154203237518")),
                Record(447, 0, [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
                Record(1234, 0, [0x01, 0x02, 0x03])
            }.SelectMany(record => record).ToArray());
        return builder.Build();
    }

    [Fact]
    public async Task LoadAsync_SynthesizedQcn_PopulatesItemsAndDecodesKnownFormats()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-qcn-vm-{Guid.NewGuid():N}.qcn");
        await File.WriteAllBytesAsync(path, CreateQcnFile(), TestContext.Current.CancellationToken);
        var document = new QcnEditorViewModel();
        try
        {
            await document.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.True(document.Items.Count >= 4);
            Assert.NotEmpty(document.SourceDescription);
            Assert.Empty(document.CompileError);
            Assert.False(document.IsDirty);

            var imei = document.Items.Single(item => item.Id == 550);
            Assert.Equal("UE IMEI", imei.KnownName);
            Assert.Equal(QcnStructuredKind.Imei, imei.Kind);
            Assert.Equal("490154203237518", imei.StructuredValue);
            Assert.Equal("490154203237518", imei.DecodedPreview);

            var spc = document.Items.Single(item => item.Id == 85);
            Assert.Equal("000000", spc.StructuredValue);

            var mac = document.Items.Single(item => item.Id == 447);
            Assert.Equal("AA:BB:CC:DD:EE:FF", mac.StructuredValue);

            var unknown = document.Items.Single(item => item.Id == 1234);
            Assert.Equal(QcnStructuredKind.None, unknown.Kind);
            Assert.Equal("010203", unknown.DecodedPreview);
        }
        finally
        {
            document.Cleanup();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_NonQcnContainer_SurfacesLocalizedError()
    {
        var builder = new CompoundFileBuilder();
        builder.AddStream("WordDocument", new byte[512]);
        var path = Path.Combine(Path.GetTempPath(), $"uotan-qcn-vm-{Guid.NewGuid():N}.qcn");
        await File.WriteAllBytesAsync(path, builder.Build(), TestContext.Current.CancellationToken);
        var document = new QcnEditorViewModel();
        try
        {
            await document.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.NotEmpty(document.CompileError);
            Assert.Empty(document.Items);
            Assert.False(document.IsDirty);
            Assert.False(document.IsBusy);
        }
        finally
        {
            document.Cleanup();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StructuredEdit_MarksDirty_AndSavePatchesOriginalBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-qcn-vm-{Guid.NewGuid():N}.qcn");
        var original = CreateQcnFile();
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
        var document = new QcnEditorViewModel();
        try
        {
            await document.LoadAsync(path, TestContext.Current.CancellationToken);

            var imei = document.Items.Single(item => item.Id == 550);
            imei.StructuredValue = "354806092731206";
            Assert.True(document.IsDirty);
            Assert.Equal("354806092731206", imei.DecodedPreview);
            Assert.Equal([0x08, 0x3A, 0x45, 0x08, 0x06, 0x29, 0x37, 0x21, 0x60], imei.Data[..9]);

            await document.SaveAsync();
            Assert.False(document.IsDirty);
            Assert.Empty(document.CompileError);

            var after = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(original.Length, after.Length);

            var reparsed = await QcnReader.ParseAsync(path, TestContext.Current.CancellationToken);
            Assert.True(QcnImeiCodec.TryDecode(reparsed.Items.Single(item => item.Id == 550).Data, out var decoded, out _));
            Assert.Equal("354806092731206", decoded);
            var spc = reparsed.Items.Single(item => item.Id == 85);
            Assert.Equal("000000", System.Text.Encoding.ASCII.GetString(spc.Data, 0, 6));
        }
        finally
        {
            document.Cleanup();
            File.Delete(path);
        }
    }
}