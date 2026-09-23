using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadXFlashStageTwoAsync_ValidatedRamPath_StreamsExactImageAndRequiresExecution(bool preloader)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        if (preloader)
        {
            EnqueueXFlashFrame(transport, new byte[4]);
            EnqueueXFlashFrame(transport, new byte[4]);
            EnqueueXFlashFrame(transport, "preloader"u8.ToArray());
            EnqueueXFlashFrame(transport, new byte[4]);
            await session.GetXFlashConnectionAgentAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            EnqueueXFlashFrame(transport, new byte[4]);
            EnqueueXFlashFrame(transport, new byte[4]);
            using var emi = new MemoryStream([1, 2, 3]);
            await session.InitializeXFlashExternalRamAsync(emi, 3, TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 1, 2, 3 }, transport.Writes[^1]);
        }
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, Convert.FromHexString("53594E43"));
        var before = transport.Writes.Count;
        var bytes = Enumerable.Range(0, 1501).Select(i => (byte)i).ToArray();
        using var image = new FragmentedSource(bytes);
        var progress = new ProgressRecorder();

        await session.UploadXFlashStageTwoAsync(image, 0x40000000, bytes.Length, progress, TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
        Assert.Equal(Convert.FromHexString("08000100"), transport.Writes[before + 1]);
        var range = transport.Writes[before + 3];
        Assert.Equal(0x40000000ul, BinaryPrimitives.ReadUInt64LittleEndian(range));
        Assert.Equal((ulong)bytes.Length, BinaryPrimitives.ReadUInt64LittleEndian(range.AsSpan(8)));
        Assert.Equal(bytes, transport.Writes.Skip(before + 5).SelectMany(part => part));
        Assert.All(transport.Writes.Skip(before + 5), part => Assert.InRange(part.Length, 1, 512));
        Assert.Equal(new long[] { 512, 1024, 1501 }, progress.Values);
    }

    [Fact]
    public async Task UploadXFlashStageTwoAsync_NoRamEvidence_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        using var image = new MemoryStream(new byte[8]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UploadXFlashStageTwoAsync(
            image, 0x40000000, 8, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UploadXFlashStageTwoAsync_StageRejection_FaultsAndPreservesError(int rejectedStage)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        using var emi = new MemoryStream([1]);
        await session.InitializeXFlashExternalRamAsync(emi, 1, TestContext.Current.CancellationToken);
        for (var i = 0; i <= rejectedStage; i++)
        {
            EnqueueXFlashFrame(transport, i == rejectedStage ? [4, 0, 7, 0xC0] : new byte[4]);
        }
        using var image = new MemoryStream(new byte[8]);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadXFlashStageTwoAsync(
            image, 0x40000000, 8, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0xC0070004u, error.Status);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Equal(rejectedStage == 0 ? 2 : 6, transport.Writes.Count - before);
        Assert.Equal(rejectedStage == 0 ? 0 : 8, image.Position);
    }

    [Fact]
    public async Task InitializeXFlashExternalRamAsync_CompletionRejected_DoesNotAllowDa2()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, [1, 0, 0, 0xC0]);
        using var emi = new MemoryStream([1, 2]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.InitializeXFlashExternalRamAsync(emi, 2, TestContext.Current.CancellationToken));
        var before = transport.Writes.Count;
        using var image = new MemoryStream(new byte[8]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UploadXFlashStageTwoAsync(
            image, 0x40000000, 8, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    private static async Task<MtkSession> CreateStageOneSessionAsync(ScriptedCommTransport transport)
    {
        var session = await CreateStartedSessionAsync(transport);
        EnqueueXFlashSetup(transport);
        await session.InitializeXFlashAsync(cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }
}