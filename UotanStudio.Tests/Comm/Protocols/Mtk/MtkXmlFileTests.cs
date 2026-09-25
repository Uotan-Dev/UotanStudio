using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadXmlStorageToFileAsync_CompleteRead_PublishesExactBytes(bool existing)
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "xml-dump.bin");
        if (existing)
        {
            dir.WriteBytes("xml-dump.bin", [1, 2, 3]);
        }
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var bytes = Enumerable.Range(0, 1024).Select(index => (byte)index).ToArray();
        EnqueueXmlRead(transport, bytes);

        await session.ReadXmlStorageToFileAsync("EMMC-USER", 0, bytes.Length, path,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadXmlStorageToFileAsync_HandshakeFailure_PreservesDestinationAndCleansStaging(bool existing)
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "xml-dump.bin");
        if (existing)
        {
            dir.WriteBytes("xml-dump.bin", [1, 2, 3]);
        }
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlRead(transport, new byte[512], finish: false);
        EnqueueXmlText(transport, XmlControl("END", "OK"));

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlStorageToFileAsync(
            "EMMC-USER", 0, 512, path, cancellationToken: TestContext.Current.CancellationToken));

        if (existing)
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(dir.Path));
        }
        else
        {
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(dir.Path));
        }
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadXmlStorageToFileAsync_CancellationAtFullProgress_PreservesExistingFile()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("xml-dump.bin", [1, 2, 3]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlRead(transport, new byte[512]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadXmlStorageToFileAsync(
            "EMMC-USER", 0, 512, path, progress: new InlineStorageProgress(_ => cancellation.Cancel()),
            cancellationToken: cancellation.Token));

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadXmlStorageToFileAsync_PublicationFailure_KeepsCompletedSessionUsable()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "occupied");
        Directory.CreateDirectory(path);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlRead(transport, new byte[512]);

        var error = await Record.ExceptionAsync(() => session.ReadXmlStorageToFileAsync(
            "EMMC-USER", 0, 512, path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(dir.Path));
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        Assert.NotNull(session.XmlStorage);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(1023)]
    public async Task VerifyXmlStorageAsync_Mismatch_CompletesReadAndReportsFirstRelativeOffset(int mismatch)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var bytes = Enumerable.Range(0, 1024).Select(index => (byte)index).ToArray();
        var actual = bytes.ToArray();
        if (mismatch >= 0)
        {
            actual[mismatch] ^= 1;
        }
        EnqueueXmlRead(transport, actual, 333);
        using var expected = new FragmentedSource([.. new byte[7], .. bytes, .. new byte[9]]);
        expected.Position = 7;

        var result = await session.VerifyXmlStorageAsync("EMMC-USER", 512, 1024, expected,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1024, result.ComparedBytes);
        Assert.Equal(mismatch < 0, result.Matches);
        Assert.Equal(mismatch < 0 ? (long?)null : mismatch, result.FirstMismatchOffset);
        Assert.Equal(1031, expected.Position);
        Assert.True(expected.CanRead);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        EnqueueXmlRead(transport, new byte[512]);
        using var destination = new MemoryStream();
        await session.ReadXmlStorageAsync("EMMC-USER", 0, 512, destination,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(512, destination.Length);
    }

    [Fact]
    public async Task VerifyXmlStorageAsync_FinalHandshakeFailure_DoesNotReturnMatchEvidence()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlRead(transport, new byte[512], finish: false);
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        using var expected = new MemoryStream(new byte[512]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.VerifyXmlStorageAsync(
            "EMMC-USER", 0, 512, expected, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(512, expected.Position);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyXmlStorageAsync_ShortReference_DistinguishesPreIoAndMidTransfer(bool nonSeekable)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlRead(transport, new byte[512]);
        using var expected = nonSeekable ? new NonSeekableStorageSource(new byte[500]) : new MemoryStream(new byte[500]);
        var before = transport.Writes.Count;

        if (nonSeekable)
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => session.VerifyXmlStorageAsync(
                "EMMC-USER", 0, 512, expected, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(MtkSessionState.Faulted, session.State);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => session.VerifyXmlStorageAsync(
                "EMMC-USER", 0, 512, expected, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(before, transport.Writes.Count);
            Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        }
    }
}