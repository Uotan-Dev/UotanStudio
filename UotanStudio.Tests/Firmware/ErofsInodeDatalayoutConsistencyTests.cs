using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsInodeDatalayoutConsistencyTests
{
    [Fact]
    public void PlannerAddressingViews_KeepStateAndDatalayoutConsistent()
    {
        var node = new ErofsInode();
        Assert.True(node.HasConsistentDatalayout);
        Assert.Equal(ErofsDataLayout.Flat, node.DataLayout);

        node.Pclusters = [new ErofsPclusterPlan()];
        Assert.True(node.HasConsistentDatalayout);
        Assert.Equal(ErofsDataLayout.Compressed, node.DataLayout);

        node.CompactedIndexes = true;
        Assert.True(node.HasConsistentDatalayout);
        Assert.Equal(ErofsDataLayout.CompactIndexes, node.DataLayout);

        node.CompactedIndexes = false;
        Assert.True(node.HasConsistentDatalayout);
        Assert.Equal(ErofsDataLayout.Compressed, node.DataLayout);

        var chunked = new ErofsInode();
        chunked.ChunkIndex = [0x10UL];
        Assert.True(chunked.HasConsistentDatalayout);
        Assert.Equal(ErofsDataLayout.ChunkBased, chunked.DataLayout);
    }

    [Fact]
    public void WriteTo_SerializesThePlanningDatalayoutOverRawFormatBits()
    {
        var node = new ErofsInode
        {
            IFormat = 0,
            Mode = ErofsInode.ModeRegularFile | 0x1A4,
            Nlink = 1,
        };
        node.Pclusters = [new ErofsPclusterPlan()];
        node.CompactedIndexes = true;

        var buffer = new byte[ErofsInode.ExtendedSize];
        node.WriteTo(buffer);

        Assert.True(node.HasConsistentDatalayout);
        Assert.Equal(
            ErofsInode.DataLayoutCompactIndexes,
            (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(buffer) & ErofsInode.DataLayoutMask));
    }
}