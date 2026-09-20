using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using UotanStudio.Models;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class MbrDetectionTests
{
    [Fact]
    public async Task LoadAsync_FatBootRecord_RejectsWithoutLpExceptions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotanstudio-fat-editor-{Guid.NewGuid():N}.img");
        var scope = new AsyncLocal<bool> { Value = true };
        var lpExceptions = 0;
        var editor = new PartitionEditorViewModel();
        EventHandler<FirstChanceExceptionEventArgs> listener = (_, args) =>
        {
            if (scope.Value && args.Exception.Message == "Invalid LP geometry magic.")
            {
                Interlocked.Increment(ref lpExceptions);
            }
        };
        try
        {
            File.WriteAllBytes(path, CreateFatBootRecord(false, 4096));
            AppDomain.CurrentDomain.FirstChanceException += listener;

            await editor.LoadAsync(path);

            Assert.Equal(0, lpExceptions);
            Assert.Equal(0, editor.PartitionCount);
            Assert.NotEmpty(editor.CompileError);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= listener;
            scope.Value = false;
            editor.Cleanup();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false, 512)]
    [InlineData(false, 4096)]
    [InlineData(true, 512)]
    public void ResolveMode_FatBootRecord_DoesNotOpenPartitionEditor(bool fat32, int sectorBytes)
    {
        var header = CreateFatBootRecord(fat32, sectorBytes);
        // A plausible entry in the boot-code area must not override the valid FAT BPB.
        WritePartition(header, 0x83, 1, 64);
        var path = Path.Combine(Path.GetTempPath(), $"uotanstudio-fat-{Guid.NewGuid():N}.img");
        try
        {
            File.WriteAllBytes(path, header);
            Assert.True(MbrTable.HasSignature(header));
            Assert.False(MbrTable.HasPartitionTable(header));
            Assert.False(FileOpenMode.IsMbrImage(path));
            Assert.NotEqual(EditorOpenMode.Partition, FileOpenMode.ResolveMode(path));
            Assert.NotEqual(EditorOpenMode.Partition, FileOpenMode.ResolveMode(path, header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0x83, 2048u, 4096u)]
    [InlineData(0x0C, 2048u, 4096u)]
    [InlineData(0x0F, 2048u, 4096u)]
    [InlineData(0xEE, 1u, uint.MaxValue)]
    public void ResolveMode_ValidStandaloneMbr_OpensPartitionEditor(byte type, uint start, uint size)
    {
        var header = new byte[512];
        WritePartition(header, type, start, size);
        Assert.True(MbrTable.HasPartitionTable(header));
        Assert.Equal(EditorOpenMode.Partition, FileOpenMode.ResolveMode("disk.img", header));
    }

    [Theory]
    [InlineData(0x01, 0x83, 1u, 8u)]
    [InlineData(0x00, 0x00, 1u, 8u)]
    [InlineData(0x00, 0x83, 0u, 8u)]
    [InlineData(0x00, 0x83, 1u, 0u)]
    [InlineData(0x00, 0x83, uint.MaxValue, 2u)]
    public void HasPartitionTable_InvalidEntry_ReturnsFalse(byte status, byte type, uint start, uint size)
    {
        var header = new byte[512];
        WritePartition(header, type, start, size);
        header[446] = status;
        Assert.False(MbrTable.HasPartitionTable(header));
    }

    [Fact]
    public void HasPartitionTable_SignatureOnly_ReturnsFalse()
    {
        var header = new byte[512];
        header[510] = 0x55;
        header[511] = 0xAA;
        Assert.False(MbrTable.HasPartitionTable(header));
        Assert.False(MbrTable.HasPartitionTable(header.AsSpan(0, 511)));
    }

    [Fact]
    public void ResolveMode_GptOrLpHeader_OpensPartitionEditor()
    {
        var gpt = new byte[520];
        WritePartition(gpt, 0xEE, 1, uint.MaxValue);
        "EFI PART"u8.CopyTo(gpt.AsSpan(512));
        Assert.Equal(EditorOpenMode.Partition, FileOpenMode.ResolveMode("disk.img", gpt));

        var lp = new byte[4100];
        BinaryPrimitives.WriteUInt32LittleEndian(lp.AsSpan(4096), 0x616C4467);
        Assert.Equal(EditorOpenMode.Partition, FileOpenMode.ResolveMode("super.img", lp));
    }

    private static byte[] CreateFatBootRecord(bool fat32, int sectorBytes)
    {
        var header = new byte[512];
        header[0] = 0xEB;
        header[1] = 0x3C;
        header[2] = 0x90;
        "MSWIN4.1"u8.CopyTo(header.AsSpan(3));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(11), (ushort)sectorBytes);
        header[13] = 1;
        header[14] = 1;
        header[16] = 2;
        header[21] = 0xF8;
        if (fat32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), 131072);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), 1024);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 2);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(17), 512);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(19), 2048);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 8);
        }
        header[510] = 0x55;
        header[511] = 0xAA;
        return header;
    }

    private static void WritePartition(byte[] header, byte type, uint start, uint size)
    {
        header[510] = 0x55;
        header[511] = 0xAA;
        header[446] = 0x80;
        header[450] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(454), start);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(458), size);
    }
}