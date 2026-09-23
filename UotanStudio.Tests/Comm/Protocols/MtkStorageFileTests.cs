using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadXFlashStorageToFileAsync_CompleteRead_PublishesExactBytes(bool existing)
    {
        using var dir = new TempDirectory();
        var destination = Path.Combine(dir.Path, "dump.bin");
        if (existing)
        {
            dir.WriteBytes("dump.bin", [1, 2, 3]);
        }
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        var bytes = Enumerable.Range(0, 1024).Select(index => (byte)index).ToArray();
        EnqueueStorageRead(transport, bytes);

        await session.ReadXFlashStorageToFileAsync(8, 0, bytes.Length, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashStorageToFileAsync_FinalRejection_PreservesExistingFileAndCleansStaging()
    {
        using var dir = new TempDirectory();
        var destination = dir.WriteBytes("dump.bin", [1, 2, 3]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueStorageRead(transport, new byte[512], 0xC0070004);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashStorageToFileAsync(
            8, 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadXFlashStorageToFileAsync_PublicationFailure_DoesNotFaultCompletedProtocol()
    {
        using var dir = new TempDirectory();
        var destination = Path.Combine(dir.Path, "occupied");
        Directory.CreateDirectory(destination);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueStorageRead(transport, new byte[512]);

        await Assert.ThrowsAnyAsync<Exception>(() => session.ReadXFlashStorageToFileAsync(
            8, 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.GetFiles(dir.Path));
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashStorageToFileAsync_CancellationAfterData_PreservesExistingFile()
    {
        using var dir = new TempDirectory();
        var destination = dir.WriteBytes("dump.bin", [1, 2, 3]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueStorageRead(transport, new byte[512]);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadXFlashStorageToFileAsync(
            8, 0, 512, destination, new InlineStorageProgress(_ => cancellation.Cancel()), cancellation.Token));

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(1023)]
    public async Task VerifyXFlashStorageAsync_ContentDifference_CompletesAndReportsRelativeOffset(int mismatch)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        var expectedBytes = Enumerable.Range(0, 1024).Select(index => (byte)index).ToArray();
        var actual = expectedBytes.ToArray();
        if (mismatch >= 0)
        {
            actual[mismatch] ^= 1;
        }
        EnqueueStorageRead(transport, actual);
        using var expected = new FragmentedSource([.. new byte[7], .. expectedBytes, .. new byte[9]]);
        expected.Position = 7;

        var result = await session.VerifyXFlashStorageAsync(8, 512, 1024, expected,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1024, result.ComparedBytes);
        Assert.Equal(mismatch < 0, result.Matches);
        Assert.Equal(mismatch < 0 ? (long?)null : mismatch, result.FirstMismatchOffset);
        Assert.Equal(1031, expected.Position);
        Assert.True(expected.CanRead);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        // A second exchange proves that mismatch handling consumed the first completion.
        EnqueueStorageRead(transport, new byte[512]);
        using var output = new MemoryStream();
        await session.ReadXFlashStorageAsync(8, 0, 512, output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(512, output.Length);
    }

    [Fact]
    public async Task VerifyXFlashStorageAsync_FinalRejection_DoesNotReturnMatchEvidence()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueStorageRead(transport, new byte[512], 0xC0070004);
        using var expected = new MemoryStream(new byte[512]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.VerifyXFlashStorageAsync(
            8, 0, 512, expected, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyXFlashStorageAsync_ShortReference_DistinguishesPreflightAndMidTransfer(bool nonSeekable)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        EnqueueStorageRead(transport, new byte[512]);
        using var expected = nonSeekable ? new NonSeekableStorageSource(new byte[500]) : new MemoryStream(new byte[500]);
        var before = transport.Writes.Count;

        if (nonSeekable)
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => session.VerifyXFlashStorageAsync(
                8, 0, 512, expected, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(MtkSessionState.Faulted, session.State);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => session.VerifyXFlashStorageAsync(
                8, 0, 512, expected, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(before, transport.Writes.Count);
            Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        }
    }

    private static void EnqueueStorageRead(ScriptedCommTransport transport, byte[] bytes, uint completion = 0)
    {
        EnqueueTransferStart(transport, read: true);
        for (var offset = 0; offset < bytes.Length; offset += 512)
        {
            EnqueueXFlashFrame(transport, bytes.AsSpan(offset, Math.Min(512, bytes.Length - offset)).ToArray());
        }
        EnqueueXFlashValue(transport, completion);
    }

    private sealed class InlineStorageProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}