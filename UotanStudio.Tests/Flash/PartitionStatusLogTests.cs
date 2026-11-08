using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// The flash page logs one line per distinct status text and per partition: the phase lines (session opened, sparse
/// conversion, chunk re-cut) and every numbered chunk each appear once, in run order, while a repeated text is dropped.
/// <para>刷写页对每个分区、每种不同状态文本各记一行：各阶段（打开会话、转换为稀疏、重新切割）与每个编号分块都按运行
/// 顺序各出现一次，重复文本则被丢弃。</para>
/// </summary>
public sealed class PartitionStatusLogTests
{
    [Fact]
    public void TryAccept_SamePartitionRepeatingOneChunkStatus_KeepsOnlyTheFirstLine()
    {
        var log = new PartitionStatusLog();
        var chunk = "system_a: uploading chunk 1/5";

        Assert.True(log.TryAccept("system_a", chunk));
        Assert.False(log.TryAccept("system_a", chunk));
        Assert.False(log.TryAccept("system_a", chunk));
    }

    [Fact]
    public void TryAccept_EveryDistinctChunkStatus_IsAcceptedInOrder()
    {
        var log = new PartitionStatusLog();
        var statuses = Enumerable.Range(1, 5).Select(index => $"system_a: uploading chunk {index}/5").ToArray();

        Assert.All(statuses, status => Assert.True(log.TryAccept("system_a", status)));
    }

    [Fact]
    public void TryAccept_DistinctPhasesAroundTheChunks_EachAppearOnce()
    {
        var log = new PartitionStatusLog();
        string[] phases =
        [
            "system_a: opening the fastboot session…",
            "system_a: re-cutting the image into download-sized chunks…",
            "system_a: uploading chunk 1/2…",
            "system_a: uploading chunk 2/2…",
        ];

        foreach (var phase in phases)
        {
            Assert.True(log.TryAccept("system_a", phase));
            Assert.False(log.TryAccept("system_a", phase));
        }
    }

    [Fact]
    public void TryAccept_PartitionsAreTrackedSeparately()
    {
        var log = new PartitionStatusLog();

        Assert.True(log.TryAccept("system_a", "uploading chunk 1/5"));
        Assert.True(log.TryAccept("system_b", "uploading chunk 1/5"));
        Assert.False(log.TryAccept("system_a", "uploading chunk 1/5"));
    }

    [Fact]
    public void TryAccept_ANullPartitionUsesTheSharedKey()
    {
        var log = new PartitionStatusLog();

        Assert.True(log.TryAccept(null, "querying the device…"));
        Assert.False(log.TryAccept(null, "querying the device…"));
    }
}