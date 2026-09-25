using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReadXmlStorageAsync_FragmentedBinary_PreservesBytesAndConsumesFinalStart(bool ufs, bool xmlEnd)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, ufs);
        var bytes = Enumerable.Range(0, 1024).Select(index => (byte)index).ToArray();
        Encoding.UTF8.GetBytes(XmlControl("END", "FAILED")).CopyTo(bytes, 0);
        EnqueueXmlRead(transport, bytes, 333, xmlEnd);
        using var destination = new XmlReadDestination();
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;
        var region = ufs ? "UFS-LUA2" : "EMMC-USER";

        await session.ReadXmlStorageAsync(region, 512, bytes.Length, destination,
            progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(bytes, destination.ToArray());
        Assert.True(destination.CanWrite);
        Assert.Equal(new long[] { 333, 666, 999, 1024 }, progress.Values);
        Assert.All(destination.WriteSizes, size => Assert.InRange(size, 1, 333));
        Assert.Equal(32, transport.Writes.Count - before);
        var command = XDocument.Parse(Encoding.UTF8.GetString(transport.Writes[before + 7]).TrimEnd('\0')).Root!;
        Assert.Equal("CMD:READ-FLASH", command.Element("command")!.Value);
        Assert.Equal(region, command.Element("arg")!.Element("partition")!.Value);
        Assert.Equal("0x200", command.Element("arg")!.Element("offset")!.Value);
        Assert.Equal("0x400", command.Element("arg")!.Element("length")!.Value);
        Assert.Equal("ROM_0", command.Element("arg")!.Element("target_file")!.Value);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        EnqueueXmlRead(transport, new byte[512]);
        await session.ReadXmlStorageAsync(region, 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1536, destination.Length);
    }

    [Fact]
    public async Task ReadXmlStorageAsync_MegabyteFrames_RemainsBoundedWithoutTextDecoding()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, capacity: 0x200000);
        var bytes = Enumerable.Range(0, 1024 * 1024 + 512).Select(index => (byte)index).ToArray();
        EnqueueXmlRead(transport, bytes, 1024 * 1024);
        using var destination = new XmlReadDestination();

        await session.ReadXmlStorageAsync("EMMC-USER", 0, bytes.Length, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(new[] { 1024 * 1024, 512 }, destination.WriteSizes);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task ReadXmlStorageAsync_LargeOffset_EmitsExact64BitAddress()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, capacity: 0x200000000);
        EnqueueXmlRead(transport, new byte[512]);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await session.ReadXmlStorageAsync("EMMC-USER", 0x100000000, 512, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        var command = XDocument.Parse(Encoding.UTF8.GetString(transport.Writes[before + 7]).TrimEnd('\0')).Root!;
        Assert.Equal("0x100000000", command.Element("arg")!.Element("offset")!.Value);
        Assert.Equal(512, destination.Length);
    }

    [Fact]
    public async Task ReadXmlStorageAsync_LargeLength_NegotiatesWithoutWholeRangeAllocation()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, capacity: 0x200000000);
        EnqueueXmlReadPrefix(transport, 0x100000200, 512);
        EnqueueXFlashFrame(transport, []);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, 0x100000200, destination, cancellationToken: TestContext.Current.CancellationToken));

        var command = XDocument.Parse(Encoding.UTF8.GetString(transport.Writes[before + 7]).TrimEnd('\0')).Root!;
        Assert.Equal("0x100000200", command.Element("arg")!.Element("length")!.Value);
        Assert.Equal(14, transport.Writes.Count - before);
        Assert.Equal(0, destination.Length);
        Assert.Null(session.XmlStorage);
    }

    [Theory]
    [InlineData("target_file", "C:/secret.bin")]
    [InlineData("target_file", "rom_0")]
    [InlineData("checksum", "CHK_SUM")]
    [InlineData("info", "")]
    [InlineData("packet_length", "0x0")]
    [InlineData("packet_length", "0x100001")]
    [InlineData("packet_length", "512")]
    [InlineData("extra", "value")]
    public async Task ReadXmlStorageAsync_InvalidCallback_RejectsBeforeDestinationWrite(string field, string value)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        var request = XmlReadRequest(512);
        request.Element("arg")!.SetElementValue(field, value);
        EnqueueXmlText(transport, request.ToString(SaveOptions.DisableFormatting));
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Equal(0, destination.Length);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Theory]
    [InlineData("OK@0x0")]
    [InlineData("OK@0x1ff")]
    [InlineData("OK@0x201")]
    [InlineData("OK@512")]
    [InlineData("OK@0xffffffffffffffff")]
    [InlineData("OK@0x10000000000000000")]
    [InlineData("ok@0x200")]
    [InlineData("ERR!OK@0x200")]
    public async Task ReadXmlStorageAsync_UnexpectedLength_RejectsBeforeDataAcceptance(string length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlReadRequest(512).ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, length);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(10, transport.Writes.Count - before);
        Assert.Equal(0, destination.Length);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(0, 512, 512)]
    [InlineData(513, 512, 1024)]
    [InlineData(513, 1024, 512)]
    public async Task ReadXmlStorageAsync_FrameViolatesBounds_DoesNotWriteIt(int frameLength, int packetLength, int length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlReadPrefix(transport, length, packetLength);
        EnqueueXFlashFrame(transport, new byte[frameLength]);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, length, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, destination.Length);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadXmlStorageAsync_PeerRejectsPacket_FaultsAndDoesNotReplay(bool final)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var length = final ? 512 : 1024;
        EnqueueXmlReadPrefix(transport, length, 512);
        EnqueueXFlashFrame(transport, new byte[512]);
        EnqueueXmlText(transport, "ERR!NOT_OK");
        using var destination = new MemoryStream();
        var progress = new ProgressRecorder();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, length, destination, progress: progress, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(512, destination.Length);
        Assert.Empty(progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
        var before = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, length, destination, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before, transport.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadXmlStorageAsync_EndRejectedOrPremature_DoesNotTreatOkSubstringAsSuccess(bool premature)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var length = premature ? 1024 : 512;
        EnqueueXmlReadPrefix(transport, length, 512);
        EnqueueXFlashFrame(transport, new byte[512]);
        EnqueueXmlText(transport, XmlControl("END", premature ? "OK" : "NOT_OK"));
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, length, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Fact]
    public async Task ReadXmlStorageAsync_MissingFinalStart_FullProgressIsNotSuccess()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlRead(transport, new byte[512], finish: false);
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        using var destination = new MemoryStream();
        var progress = new ProgressRecorder();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, 512, destination, progress: progress, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new long[] { 512 }, progress.Values);
        Assert.Equal(512, destination.Length);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Fact]
    public async Task ReadXmlStorageAsync_DestinationFailure_DoesNotAcknowledgeUnstoredData()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlRead(transport, new byte[512]);
        using var destination = new XmlReadDestination(() => throw new IOException("Simulated destination failure."));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<IOException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(14, transport.Writes.Count - before);
        Assert.Equal(0, destination.Length);
        Assert.True(destination.CanWrite);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadXmlStorageAsync_Cancellation_DistinguishesPreIoAndFullDataWithoutCompletion(bool afterData)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlRead(transport, new byte[512]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (!afterData)
        {
            cancellation.Cancel();
        }
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, 512, destination, progress: new InlineStorageProgress(_ => cancellation.Cancel()),
            cancellationToken: cancellation.Token));

        Assert.Equal(afterData ? 18 : 0, transport.Writes.Count - before);
        Assert.Equal(afterData ? MtkSessionState.Faulted : MtkSessionState.XmlStorageGeometryKnown, session.State);
        Assert.Equal(afterData ? 512 : 0, destination.Length);
    }

    [Fact]
    public async Task ReadXmlStorageAsync_QueuedRead_DoesNotInterleavePackets()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlRead(transport, new byte[1024]);
        EnqueueXmlRead(transport, Enumerable.Repeat((byte)7, 512).ToArray());
        using var other = new MemoryStream();
        Task? queued = null;
        using var destination = new XmlReadDestination(() =>
        {
            queued ??= session.ReadXmlStorageAsync("EMMC-USER", 0, 512, other,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
        });

        await session.ReadXmlStorageAsync("EMMC-USER", 0, 1024, destination,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        await queued;

        Assert.Equal(new byte[1024], destination.ToArray());
        Assert.Equal(Enumerable.Repeat((byte)7, 512), other.ToArray());
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Theory]
    [InlineData("EMMC-USER", -512, 512)]
    [InlineData("EMMC-USER", 1, 512)]
    [InlineData("EMMC-USER", 0, 0)]
    [InlineData("EMMC-USER", 0, 513)]
    [InlineData("EMMC-USER", 65536, 512)]
    [InlineData("EMMC-USER", long.MaxValue - 511, 512)]
    [InlineData("8", 0, 512)]
    public async Task ReadXmlStorageAsync_InvalidRange_RejectsBeforeIo(string region, long offset, long length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadXmlStorageAsync(
            region, offset, length, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadXmlStorageAsync_AuthenticatedRegion_RejectsBeforeIo(bool ufs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, ufs);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadXmlStorageAsync(
            ufs ? "UFS-LUA3" : "EMMC-RPMB", 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(86400001)]
    public async Task ReadXmlStorageAsync_InvalidDeadline_RejectsBeforeIo(int timeoutMs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, 512, destination, timeoutMs, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task ReadXmlStorageAsync_NotDiscoveredOrUnwritable_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        using var destination = new MemoryStream();
        using var readOnly = new MemoryStream(new byte[512], writable: false);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, 512, readOnly, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStageTwoHardwareInitialized, session.State);
    }

    [Fact]
    public async Task ReadXmlStorageAsync_LogsLargerThanRemainingData_ExcludesLogsFromOutput()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlReadPrefix(transport, 512, 512);
        EnqueueXFlashFrame(transport, [0, 0, 0, 0, .. Enumerable.Repeat((byte)'L', 4096), 0], 2);
        var bytes = Enumerable.Repeat((byte)0xaa, 512).ToArray();
        EnqueueXFlashFrame(transport, bytes);
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
        using var destination = new MemoryStream();

        await session.ReadXmlStorageAsync("EMMC-USER", 0, 512, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task ReadXmlStorageAsync_ExcessiveLogs_FaultsWithoutData()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlReadPrefix(transport, 512, 512);
        for (var index = 0; index < 65; index++)
        {
            EnqueueXFlashFrame(transport, [0, 0, 0, 0, 65, 0], 2);
        }
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, destination.Length);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XmlChannel_OversizedFrame_RejectsHeaderBeforeReadingPayload(bool text)
    {
        using var transport = new ScriptedCommTransport();
        var channel = new MtkXmlChannel(transport, 5000);
        var length = text ? MtkXmlChannel.MaximumPayload + 1 : MtkXmlChannel.MaximumBinaryPayload + 1;
        EnqueueXFlashFrame(transport, new byte[length]);

        if (text)
        {
            await Assert.ThrowsAsync<MtkProtocolException>(() => channel.ReceiveTextAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<MtkProtocolException>(() => channel.ReceivePayloadAsync(
                MtkXmlChannel.MaximumBinaryPayload, TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, transport.ReadCount);
        Assert.True(channel.IsFaulted);
    }

    [Fact]
    public async Task ReadXmlStorageAsync_TruncatedFrame_FaultsWithoutWritingIncompleteFrame()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlReadPrefix(transport, 512, 512);
        transport.Enqueue([0xef, 0xee, 0xee, 0xfe, 1, 0, 0, 0, 0, 2, 0, 0]);
        transport.Enqueue(new byte[100]);
        using var destination = new MemoryStream();

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlStorageAsync(
            "EMMC-USER", 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Truncated", error.Message);
        Assert.Equal(0, destination.Length);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    private static XElement XmlReadRequest(int packetLength)
    {
        var request = XmlStorageRequest();
        request.Element("arg")!.SetElementValue("info", "read-flash");
        request.Element("arg")!.SetElementValue("target_file", "ROM_0");
        request.Element("arg")!.SetElementValue("packet_length", $"0x{packetLength:x}");
        return request;
    }

    private static void EnqueueXmlReadPrefix(ScriptedCommTransport transport, long length, int packetLength)
    {
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlReadRequest(packetLength).ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, $"OK@0x{length:x}");
        EnqueueXmlText(transport, "OK");
    }

    private static void EnqueueXmlRead(ScriptedCommTransport transport, byte[] bytes, int packetLength = 512,
        bool xmlEnd = true, bool finish = true)
    {
        EnqueueXmlReadPrefix(transport, bytes.Length, packetLength);
        for (var offset = 0; offset < bytes.Length; offset += packetLength)
        {
            var count = Math.Min(packetLength, bytes.Length - offset);
            EnqueueXFlashFrame(transport, bytes.AsSpan(offset, count).ToArray());
            EnqueueXmlText(transport, offset + count == bytes.Length && xmlEnd ? XmlControl("END", "OK") : "OK");
        }
        if (finish)
        {
            EnqueueXmlText(transport, XmlControl("START"));
        }
    }

    private static async Task<MtkSession> CreateXmlStorageSessionAsync(ScriptedCommTransport transport,
        bool ufs = false, long capacity = 65536)
    {
        var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        var document = XmlHardwareDocument(ufs);
        document.Element(ufs ? "ufs" : "emmc")!.SetElementValue(ufs ? "lua2_size" : "user_size", $"0x{capacity:x}");
        EnqueueXmlStorage(transport, document);
        await session.DiscoverXmlStorageAsync(cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }

    private sealed class XmlReadDestination(Action? beforeWrite = null) : MemoryStream
    {
        internal List<int> WriteSizes { get; } = [];
        public override bool CanSeek => false;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            beforeWrite?.Invoke();
            WriteSizes.Add(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}