using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Fact]
    public async Task UploadXmlStageTwoAsync_FragmentedSource_PreservesBytesAndRequiresHandoff()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        var bytes = Enumerable.Range(0, 1301).Select(i => (byte)i).ToArray();
        using var source = new FragmentedSource(bytes);
        var progress = new ProgressRecorder();
        EnqueueXmlUpload(transport, bytes.Length, 600);
        var before = transport.Writes.Count;

        await session.UploadXmlStageTwoAsync(source, 0x400000, bytes.Length,
            progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.XmlStageTwoStarted, session.State);
        Assert.True(source.CanRead);
        Assert.Equal(bytes.Length, source.Position);
        Assert.Equal(new long[] { 600, 1200, 1301 }, progress.Values);
        var writes = transport.Writes.Skip(before).ToArray();
        var command = XDocument.Parse(Encoding.UTF8.GetString(writes[7]).TrimEnd('\0')).Root!;
        Assert.Equal("CMD:BOOT-TO", command.Element("command")!.Value);
        Assert.Equal("0x400000", command.Element("arg")!.Element("at_address")!.Value);
        Assert.Equal("0x400000", command.Element("arg")!.Element("jmp_address")!.Value);
        Assert.Equal("MEM://0x0:0x515", command.Element("arg")!.Element("source_file")!.Value);
        // Decode envelopes independently, including fragmented stream writes.
        var wire = writes.SelectMany(value => value).ToArray();
        var payloads = new List<byte[]>();
        for (var offset = 0; offset < wire.Length;)
        {
            Assert.True(wire.Length - offset >= 12);
            var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(offset + 8, 4));
            Assert.InRange(count, 1, wire.Length - offset - 12);
            payloads.Add(wire.AsSpan(offset + 12, count).ToArray());
            offset += 12 + count;
        }
        Assert.Equal(bytes, payloads.Where(value => value.Length is 600 or 101).SelectMany(value => value).ToArray());
        Assert.Contains(payloads, value => value.SequenceEqual(Encoding.UTF8.GetBytes("OK@0x515\0\0")));
        Assert.Equal(3, payloads.Count(value => value.SequenceEqual(Encoding.UTF8.GetBytes("OK@0x0\0\0"))));
        Assert.Null(session.XFlashStorage);
    }

    [Theory]
    [InlineData("source_file", "/tmp/host-secret")]
    [InlineData("source_file", "MEM://0x0:0x9")]
    [InlineData("info", "other")]
    [InlineData("checksum", "CHK_SUM")]
    [InlineData("packet_length", "0x0")]
    [InlineData("packet_length", "0x100001")]
    [InlineData("packet_length", "12")]
    [InlineData("packet_length", "0xzz")]
    [InlineData("extra", "value")]
    public async Task UploadXmlStageTwoAsync_UntrustedRequest_RejectsBeforeReadingSource(string field, string value)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[8]);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        var request = XmlUploadRequest(8, 8);
        request.Element("arg")!.SetElementValue(field, value);
        EnqueueXmlText(transport, request.ToString(SaveOptions.DisableFormatting));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadXmlStageTwoAsync(
            source, 0x400000, 8, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, source.Position);
        Assert.Equal(8, transport.Writes.Count - before);
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
    public async Task UploadXmlStageTwoAsync_RejectedPhase_FaultsWithoutReplay(int phase)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[8]);
        if (phase == 0)
        {
            EnqueueXmlText(transport, "ERR!REJECTED");
        }
        else
        {
            EnqueueXmlCompletion(transport);
            EnqueueXmlText(transport, phase == 1 ? "ERR!REJECTED" : "OK");
            EnqueueXmlText(transport, XmlUploadRequest(8, 8).ToString(SaveOptions.DisableFormatting));
            EnqueueXmlText(transport, phase == 2 ? "ERR!REJECTED" : "OK");
            EnqueueXmlText(transport, phase == 3 ? "ERR!REJECTED" : "OK");
            EnqueueXmlText(transport, phase == 4 ? "ERR!REJECTED" : "OK");
            EnqueueXmlText(transport, XmlControl("END", phase == 5 ? "FAILED" : "OK"));
            EnqueueXmlText(transport, XmlControl(phase == 6 ? "UNKNOWN" : "START"));
        }

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadXmlStageTwoAsync(
            source, 0x400000, 8, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(phase >= 4 ? 8 : 0, source.Position);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        var before = transport.Writes.Count;
        source.Position = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UploadXmlStageTwoAsync(
            source, 0x400000, 1, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before, transport.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadXmlStageTwoAsync_Cancellation_DistinguishesPreIoAndUncertainUpload(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[8]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (inFlight)
        {
            transport.OnWrite = _ =>
            {
                cancellation.Cancel();
                return [];
            };
        }
        else
        {
            cancellation.Cancel();
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.UploadXmlStageTwoAsync(
            source, 0x400000, 8, cancellationToken: cancellation.Token));

        Assert.Equal(inFlight ? 1 : 0, transport.Writes.Count - before);
        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.XmlHardwareInitialized, session.State);
        Assert.Equal(0, source.Position);
    }

    [Theory]
    [InlineData(0, 0x400000UL)]
    [InlineData(67108865, 0x400000UL)]
    [InlineData(8, 0x400001UL)]
    [InlineData(8, 18446744073709551612UL)]
    public async Task UploadXmlStageTwoAsync_InvalidRange_RejectsBeforeIo(int length, ulong address)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[8]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.UploadXmlStageTwoAsync(
            source, address, length, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlHardwareInitialized, session.State);
    }

    [Fact]
    public async Task UploadXmlStageTwoAsync_ExcessivePackets_RejectsBeforeSourceRead()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[65537]);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlUploadRequest(65537, 1).ToString(SaveOptions.DisableFormatting));

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadXmlStageTwoAsync(
            source, 0x400000, 65537, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, source.Position);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task UploadXmlStageTwoAsync_QueuedOperation_DoesNotInterleave()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[8]);
        using var otherSource = new MemoryStream(new byte[8]);
        EnqueueXmlUpload(transport, 8, 4);
        Task? queued = null;
        transport.OnWrite = _ =>
        {
            queued ??= session.UploadXmlStageTwoAsync(otherSource, 0x400000, 8,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            return [];
        };

        await session.UploadXmlStageTwoAsync(source, 0x400000, 8,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(queued);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        Assert.Equal(0, otherSource.Position);
        Assert.Equal(MtkSessionState.XmlStageTwoStarted, session.State);
    }

    [Fact]
    public async Task UploadXmlStageTwoAsync_FullProgressWithoutStart_DoesNotClaimHandoff()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[8]);
        var progress = new ProgressRecorder();
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlUploadRequest(8, 8).ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("END", "OK"));

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.UploadXmlStageTwoAsync(
            source, 0x400000, 8, progress: progress, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new long[] { 8 }, progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    private static XElement XmlUploadRequest(int length, int packetLength) => new("host",
        new XElement("version", "1.0"), new XElement("command", "CMD:DOWNLOAD-FILE"),
        new XElement("arg", new XElement("checksum", "CHK_NO"), new XElement("info", "2nd-DA"),
            new XElement("source_file", $"MEM://0x0:0x{length:x}"), new XElement("packet_length", $"0x{packetLength:x}")));

    private static void EnqueueXmlUpload(ScriptedCommTransport transport, int length, int packetLength)
    {
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlUploadRequest(length, packetLength).ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, "OK");
        for (var offset = 0; offset < length; offset += packetLength)
        {
            EnqueueXmlText(transport, "OK");
            EnqueueXmlText(transport, "OK");
        }
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }

    private static async Task<MtkSession> CreateXmlHardwareSessionAsync(ScriptedCommTransport transport)
    {
        var session = await CreateXmlRuntimeSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlCompletion(transport);
        await session.InitializeXmlHardwareAsync(cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }
}