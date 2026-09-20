using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Parser;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Partition-size resolution tests for <see cref="PayloadParser.GetPartitionSize"/>. The manifest's
/// declared <c>new_partition_info.size</c> is authoritative; only when it is absent does the parser
/// fall back to the highest destination-extent end offset. Incremental payloads dominated by
/// <c>SOURCE_COPY</c> carry empty destination extents, so summing extent blocks (the pre-fix
/// behaviour) under-reported the size and showed near-zero partitions in the file tree.
/// <para>分区大小解析测试。manifest 声明的 <c>new_partition_info.size</c> 是权威值；缺失时才回退到
/// 最高目标 extent 结束偏移。以 <c>SOURCE_COPY</c> 为主的增量载荷目标 extent 为空，修复前按 extent 块数求和
/// 会低估大小、在文件树中显示接近 0 的分区。</para>
/// </summary>
public class PayloadPartitionSizeTests
{
    private const uint BlockSize = 4096;

    [Fact]
    public void GetPartitionSize_SourceCopyOnly_UsesDeclaredNewPartitionInfoSize()
    {
        // SOURCE_COPY 为主的增量分区：目标 extent 为空，仅有声明大小。
        // SOURCE_COPY-dominated incremental partition: no destination extents, declared size only.
        var partition = new PartitionUpdate
        {
            PartitionName = "system",
            NewPartitionInfo = new PartitionInfo { Size = 512UL * 1024 * 1024 },
        };
        partition.Operations.Add(new InstallOperation
        {
            Type = InstallOperation.Types.Type.SourceCopy,
            SrcLength = 512UL * 1024 * 1024,
            DstLength = 512UL * 1024 * 1024,
        });

        Assert.Equal(512L * 1024 * 1024, PayloadParser.GetPartitionSize(partition, BlockSize));
    }

    [Fact]
    public void GetPartitionSize_DeclaredSizeAndExtents_DeclaredSizeWins()
    {
        // 两者都存在时声明大小优先，即便 extent 结束偏移更大。
        // Declared size wins even when the extent end offset would be larger.
        var partition = new PartitionUpdate
        {
            PartitionName = "vendor",
            NewPartitionInfo = new PartitionInfo { Size = 100UL * BlockSize },
        };
        var op = new InstallOperation { Type = InstallOperation.Types.Type.Replace, DstLength = 4 * BlockSize };
        op.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = 4 });
        partition.Operations.Add(op);

        Assert.Equal(100L * BlockSize, PayloadParser.GetPartitionSize(partition, BlockSize));
    }

    [Fact]
    public void GetPartitionSize_MissingNewPartitionInfo_FallsBackToMaxDestinationExtentEnd()
    {
        // 无声明大小时取最高目标 extent 结束偏移（非块数求和）。
        // Without a declared size, use the highest destination-extent end (not a block sum).
        var partition = new PartitionUpdate { PartitionName = "product" };
        var first = new InstallOperation { Type = InstallOperation.Types.Type.Replace, DstLength = 2 * BlockSize };
        first.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = 2 });
        var second = new InstallOperation { Type = InstallOperation.Types.Type.Replace, DstLength = 3 * BlockSize };
        second.DstExtents.Add(new Extent { StartBlock = 10, NumBlocks = 3 });
        partition.Operations.Add(first);
        partition.Operations.Add(second);

        // (10 + 3) * 4096 = 53248 而不是 (2 + 3) * 4096 = 20480。
        Assert.Equal(13L * BlockSize, PayloadParser.GetPartitionSize(partition, BlockSize));
    }

    [Fact]
    public void GetPartitionSize_EmptyPartitionWithoutAnyInfo_ReturnsZero()
    {
        var partition = new PartitionUpdate { PartitionName = "empty" };

        Assert.Equal(0L, PayloadParser.GetPartitionSize(partition, BlockSize));
    }

    [Fact]
    public void GetPartitionSize_NullPartition_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PayloadParser.GetPartitionSize(null!, BlockSize));
    }
}