using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkXmlSecurityResourceKind.FlashPolicy, false, "CMD:SECURITY-SET-FLASH-POLICY")]
    [InlineData(MtkXmlSecurityResourceKind.FlashPolicy, true, "CMD:SECURITY-SET-FLASH-POLICY")]
    [InlineData(MtkXmlSecurityResourceKind.AllInOneSignature, false, "CMD:SECURITY-SET-ALLINONE-SIGNATURE")]
    [InlineData(MtkXmlSecurityResourceKind.AllInOneSignature, true, "CMD:SECURITY-SET-ALLINONE-SIGNATURE")]
    public async Task UploadXmlSecurityResourceAsync_ExplicitResource_PreservesBytesWithoutClaimingAuthorization(
        MtkXmlSecurityResourceKind kind, bool stageTwo, string commandName)
    {
        using var transport = new ScriptedCommTransport();
        using var session = stageTwo
            ? await CreateXmlStageTwoHardwareSessionAsync(transport)
            : await CreateXmlHardwareSessionAsync(transport);
        var state = session.State;
        var bytes = Enumerable.Range(0, 1301).Select(index => (byte)index).ToArray();
        using var source = new FragmentedSource([.. new byte[7], .. bytes, .. new byte[9]]);
        source.Position = 7;
        EnqueueXmlSecurityResource(transport, bytes.Length, 600);
        var before = transport.Writes.Count;
        var progress = new ProgressRecorder();

        await session.UploadXmlSecurityResourceAsync(kind, source, bytes.Length,
            progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(state, session.State);
        Assert.Null(session.XmlStorage);
        Assert.Null(session.XFlashStorage);
        Assert.Equal(new long[] { 600, 1200, 1301 }, progress.Values);
        Assert.Equal(1308, source.Position);
        Assert.True(source.CanRead);
        var writes = transport.Writes.Skip(before).ToArray();
        Assert.All(writes, write => Assert.InRange(write.Length, 1, 512));
        var frames = DecodeXmlWriteFrames(writes);
        Assert.Equal(14, frames.Count);
        var command = XDocument.Parse(Encoding.UTF8.GetString(frames[3]).TrimEnd('\0')).Root!;
        Assert.Equal(commandName, command.Element("command")!.Value);
        Assert.Equal("MEM://0x0:0x515", command.Element("arg")!.Element("source_file")!.Value);
        Assert.Single(command.Element("arg")!.Elements());
        Assert.Equal(bytes, frames[7].Concat(frames[9]).Concat(frames[11]).ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes("OK@0x515\0\0"), frames[5]);
        Assert.All(frames.Skip(12), frame => Assert.Equal("OK\0\0"u8.ToArray(), frame));
        EnqueueXmlCompletion(transport);
        await session.SetXmlHostInfoAsync("after resource", TestContext.Current.CancellationToken);
        Assert.Equal(state, session.State);
    }

    [Theory]
    [InlineData("source_file", "C:/credential.bin")]
    [InlineData("source_file", "MEM://0x0:0x201")]
    [InlineData("checksum", "CHK_SUM")]
    [InlineData("info", "")]
    [InlineData("packet_length", "0x0")]
    [InlineData("packet_length", "0x100001")]
    [InlineData("packet_length", "512")]
    [InlineData("extra", "value")]
    public async Task UploadXmlSecurityResourceAsync_InvalidCallback_RejectsWithoutReadingSource(string field, string value)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        var request = XmlSecurityRequest(512, 512);
        request.Element("arg")!.SetElementValue(field, value);
        EnqueueXmlText(transport, request.ToString(SaveOptions.DisableFormatting));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadXmlSecurityResourceAsync(
            MtkXmlSecurityResourceKind.FlashPolicy, source, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Equal(0, source.Position);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task UploadXmlSecurityResourceAsync_RejectedPhase_FaultsWithoutFallbackOrReplay(int phase)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        var progress = new ProgressRecorder();
        if (phase == 0)
        {
            EnqueueXmlText(transport, "ERR!REJECTED");
        }
        else
        {
            EnqueueXmlCompletion(transport);
            EnqueueXmlText(transport, phase == 1 ? "ERR!REJECTED" : "OK");
            EnqueueXmlText(transport, XmlSecurityRequest(512, 512).ToString(SaveOptions.DisableFormatting));
            EnqueueXmlText(transport, phase == 2 ? "ERR!REJECTED" : "OK");
            EnqueueXmlText(transport, phase == 3 ? "ERR!REJECTED" : "OK");
            EnqueueXmlText(transport, phase == 4 ? "ERR!NOT_OK" : "OK");
            EnqueueXmlText(transport, XmlControl("END", phase == 5 ? "NOT_OK" : "OK"));
            EnqueueXmlText(transport, XmlControl(phase == 6 ? "END" : "START"));
        }

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadXmlSecurityResourceAsync(
            MtkXmlSecurityResourceKind.AllInOneSignature, source, 512, progress: progress,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(phase >= 4 ? 512 : 0, source.Position);
        Assert.Equal(phase >= 5 ? new long[] { 512 } : [], progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        source.Position = 0;
        var before = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UploadXmlSecurityResourceAsync(
            MtkXmlSecurityResourceKind.AllInOneSignature, source, 512, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before, transport.Writes.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1048577)]
    public async Task UploadXmlSecurityResourceAsync_InvalidLength_RejectsBeforeIo(int length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.UploadXmlSecurityResourceAsync(
            MtkXmlSecurityResourceKind.FlashPolicy, source, length, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlHardwareInitialized, session.State);
    }

    [Fact]
    public async Task UploadXmlSecurityResourceAsync_UnknownKind_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.UploadXmlSecurityResourceAsync(
            (MtkXmlSecurityResourceKind)99, source, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlHardwareInitialized, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadXmlSecurityResourceAsync_BeforeHardwareOrAfterDiscovery_RejectsBeforeIo(bool discovered)
    {
        using var transport = new ScriptedCommTransport();
        using var session = discovered
            ? await CreateXmlStorageSessionAsync(transport)
            : await CreateXmlRuntimeSessionAsync(transport);
        var state = session.State;
        var geometry = session.XmlStorage;
        using var source = new MemoryStream(new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UploadXmlSecurityResourceAsync(
            MtkXmlSecurityResourceKind.FlashPolicy, source, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(state, session.State);
        Assert.Same(geometry, session.XmlStorage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadXmlSecurityResourceAsync_ShortSource_DistinguishesPreflightAndMidExchange(bool nonSeekable)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = nonSeekable ? new NonSeekableStorageSource(new byte[500]) : new MemoryStream(new byte[500]);
        EnqueueXmlSecurityResource(transport, 512, 512);
        var before = transport.Writes.Count;

        if (nonSeekable)
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => session.UploadXmlSecurityResourceAsync(
                MtkXmlSecurityResourceKind.FlashPolicy, source, 512, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(MtkSessionState.Faulted, session.State);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => session.UploadXmlSecurityResourceAsync(
                MtkXmlSecurityResourceKind.FlashPolicy, source, 512, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(before, transport.Writes.Count);
            Assert.Equal(MtkSessionState.XmlHardwareInitialized, session.State);
        }
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadXmlSecurityResourceAsync_Cancellation_DistinguishesPreIoAndFullProgress(bool afterData)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        EnqueueXmlSecurityResource(transport, 512, 512);
        if (!afterData)
        {
            cancellation.Cancel();
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.UploadXmlSecurityResourceAsync(
            MtkXmlSecurityResourceKind.AllInOneSignature, source, 512,
            progress: new InlineStorageProgress(_ => cancellation.Cancel()), cancellationToken: cancellation.Token));

        Assert.Equal(afterData ? 16 : 0, transport.Writes.Count - before);
        Assert.Equal(afterData ? 512 : 0, source.Position);
        Assert.Equal(afterData ? MtkSessionState.Faulted : MtkSessionState.XmlHardwareInitialized, session.State);
    }

    [Fact]
    public async Task UploadXmlSecurityResourceAsync_QueuedDiscovery_WaitsForFinalHandoff()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        using var source = new PausedStorageSource();
        EnqueueXmlSecurityResource(transport, 512, 512);
        EnqueueXmlStorage(transport, XmlHardwareDocument(false));
        var upload = session.UploadXmlSecurityResourceAsync(MtkXmlSecurityResourceKind.FlashPolicy, source, 512,
            cancellationToken: TestContext.Current.CancellationToken);
        await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task<MtkXmlStorageInfo> discovery;
        try
        {
            var before = transport.Writes.Count;
            discovery = session.DiscoverXmlStorageAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(discovery.IsCompleted);
            Assert.Equal(before, transport.Writes.Count);
        }
        finally
        {
            source.Resume.TrySetResult();
            await upload;
        }
        var geometry = await discovery;
        Assert.Same(geometry, session.XmlStorage);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(86400001)]
    public async Task UploadXmlSecurityResourceAsync_InvalidDeadline_RejectsBeforeIo(int timeoutMs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.UploadXmlSecurityResourceAsync(
            MtkXmlSecurityResourceKind.FlashPolicy, source, 512, timeoutMs, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlHardwareInitialized, session.State);
    }

    private static XElement XmlSecurityRequest(int length, int packetLength)
    {
        var request = XmlUploadRequest(length, packetLength);
        request.Element("arg")!.SetElementValue("info", "security-resource");
        return request;
    }

    private static void EnqueueXmlSecurityResource(ScriptedCommTransport transport, int length, int packetLength)
    {
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlSecurityRequest(length, packetLength).ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, "OK");
        for (var offset = 0; offset < length; offset += packetLength)
        {
            EnqueueXmlText(transport, "OK");
            EnqueueXmlText(transport, "OK");
        }
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }
}