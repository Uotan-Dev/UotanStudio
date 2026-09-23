using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Fact]
    public void Prepare_SparseExceedsTarget_RejectsDeclaredLengthBeforeReadingChunks()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("oversized.img", PlanSparse()[..28]);
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            StorageGeometry(MtkXFlashStorageKind.Emmc), PacketLengths());

        var error = Assert.Throws<InvalidDataException>(() => MtkFlashPlan.Prepare(storage,
            [new(MtkFlashStepKind.Sparse, 8, 0, 512, path)], TestContext.Current.CancellationToken));

        Assert.Contains("logical length", error.Message, StringComparison.Ordinal);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(28, exclusive.Length);
    }

    [Fact]
    public void Prepare_RawAndErase_RetainsExactRangesWithoutDeviceIo()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("raw.img", [.. new byte[512], .. Enumerable.Repeat((byte)0x42, 512)]);
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            StorageGeometry(MtkXFlashStorageKind.Emmc), PacketLengths());
        using var plan = MtkFlashPlan.Prepare(storage,
        [
            new(MtkFlashStepKind.Erase, 8, 0, 512),
            new(MtkFlashStepKind.Raw, 8, 512, 512, path, 512, true)
        ], TestContext.Current.CancellationToken);

        Assert.Same(storage, plan.Storage);
        Assert.Equal(1024, plan.TotalBytes);
        Assert.True(plan.Operations[0].IsErase);
        Assert.True(plan.Operations[1].Verify);
        var operation = plan.Prepared[1];
        Assert.NotNull(operation.Source);
        operation.Source.Position = operation.SourceOffset;
        Assert.Equal(0x42, operation.Source.ReadByte());
    }

    [Fact]
    public async Task Prepare_SparseMixedChunks_PreservesHolesAndProvidesFillBytes()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("sparse.img", PlanSparse());
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            StorageGeometry(MtkXFlashStorageKind.Emmc), PacketLengths());
        using var plan = MtkFlashPlan.Prepare(storage,
            [new(MtkFlashStepKind.Sparse, 8, 512, 1536, path, Verify: true)], TestContext.Current.CancellationToken);

        Assert.Equal(1024, plan.TotalBytes);
        Assert.Equal(new long[] { 512, 1536 }, plan.Operations.Select(operation => operation.Offset));
        Assert.All(plan.Operations, operation => Assert.True(operation.Verify));
        var raw = plan.Prepared[0];
        raw.Source!.Position = raw.SourceOffset;
        var data = new byte[512];
        await raw.Source.ReadExactlyAsync(data, TestContext.Current.CancellationToken);
        Assert.All(data, value => Assert.Equal(0x4A, value));
        var fill = plan.Prepared[1];
        fill.Source!.Position = fill.SourceOffset;
        await fill.Source.ReadExactlyAsync(data, TestContext.Current.CancellationToken);
        for (var index = 0; index < data.Length; index++)
        {
            Assert.Equal((byte)(4 - index % 4), data[index]);
        }
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("short-raw")]
    [InlineData("target-overrun")]
    [InlineData("rpmb")]
    [InlineData("sparse-offset")]
    [InlineData("sparse-capacity")]
    [InlineData("sparse-trailing")]
    [InlineData("erase-source")]
    public void Prepare_InvalidLaterStep_ReleasesAllEarlierSources(string failure)
    {
        using var dir = new TempDirectory();
        var raw = dir.WriteBytes("raw.img", new byte[512]);
        var sparseBytes = PlanSparse();
        if (failure == "sparse-trailing")
        {
            sparseBytes = [.. sparseBytes, 1];
        }
        var sparse = dir.WriteBytes("sparse.img", sparseBytes);
        var invalid = failure switch
        {
            "missing-file" => new MtkFlashStep(MtkFlashStepKind.Raw, 8, 512, 512, Path.Combine(dir.Path, "missing.img")),
            "short-raw" => new MtkFlashStep(MtkFlashStepKind.Raw, 8, 512, 1024, raw),
            "target-overrun" => new MtkFlashStep(MtkFlashStepKind.Raw, 8, 4096, 512, raw),
            "rpmb" => new MtkFlashStep(MtkFlashStepKind.Raw, 3, 0, 512, raw),
            "sparse-offset" => new MtkFlashStep(MtkFlashStepKind.Sparse, 8, 0, 1536, sparse, 1),
            "sparse-capacity" => new MtkFlashStep(MtkFlashStepKind.Sparse, 8, 0, 512, sparse),
            "sparse-trailing" => new MtkFlashStep(MtkFlashStepKind.Sparse, 8, 0, 1536, sparse),
            _ => new MtkFlashStep(MtkFlashStepKind.Erase, 8, 0, 512, raw)
        };
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            StorageGeometry(MtkXFlashStorageKind.Emmc), PacketLengths());

        Assert.ThrowsAny<Exception>(() => MtkFlashPlan.Prepare(storage,
            [new(MtkFlashStepKind.Raw, 8, 0, 512, raw), invalid], TestContext.Current.CancellationToken));

        using var rawExclusive = new FileStream(raw, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var sparseExclusive = new FileStream(sparse, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Prepare_PreCancelled_DoesNotOpenSources()
    {
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            StorageGeometry(MtkXFlashStorageKind.Emmc), PacketLengths());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => MtkFlashPlan.Prepare(storage,
            [new(MtkFlashStepKind.Raw, 8, 0, 512, "nonexistent.img")], cancellation.Token));
    }

    [Fact]
    public void Dispose_PreparedPlan_ClosesSourcesAndRejectsExecutionAccess()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("raw.img", new byte[512]);
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            StorageGeometry(MtkXFlashStorageKind.Emmc), PacketLengths());
        var plan = MtkFlashPlan.Prepare(storage, [new(MtkFlashStepKind.Raw, 8, 0, 512, path)],
            TestContext.Current.CancellationToken);

        plan.Dispose();
        plan.Dispose();

        Assert.Throws<ObjectDisposedException>(() => plan.Prepared);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static byte[] PlanSparse()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0xED26FF3Au);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)28);
        writer.Write((ushort)12);
        writer.Write(512u);
        writer.Write(3u);
        writer.Write(3u);
        writer.Write(0u);
        writer.Write((ushort)0xCAC1);
        writer.Write((ushort)0);
        writer.Write(1u);
        writer.Write(524u);
        writer.Write(Enumerable.Repeat((byte)0x4A, 512).ToArray());
        writer.Write((ushort)0xCAC3);
        writer.Write((ushort)0);
        writer.Write(1u);
        writer.Write(12u);
        writer.Write((ushort)0xCAC2);
        writer.Write((ushort)0);
        writer.Write(1u);
        writer.Write(16u);
        writer.Write(0x01020304u);
        writer.Flush();
        return stream.ToArray();
    }
}