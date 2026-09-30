using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteXmlStorageAsync_FragmentedSource_PreservesBytesAndRawCompletion(bool ufs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, ufs);
        var bytes = Enumerable.Range(0, 1536).Select(index => (byte)index).ToArray();
        using var source = new FragmentedSource([.. new byte[7], .. bytes, .. new byte[9]]);
        source.Position = 7;
        EnqueueXmlWrite(transport, bytes.Length, 600);
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;
        var region = ufs ? "UFS-LUA2" : "EMMC-USER";

        await session.WriteXmlStorageAsync(region, 512, bytes.Length, source,
            progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new long[] { 600, 1200, 1536 }, progress.Values);
        Assert.Equal(1543, source.Position);
        Assert.True(source.CanRead);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        var writes = transport.Writes.Skip(before).ToArray();
        Assert.All(writes, write => Assert.InRange(write.Length, 1, 512));
        var frames = DecodeXmlWriteFrames(writes);
        Assert.Equal(17, frames.Count);
        var capability = XDocument.Parse(Encoding.UTF8.GetString(frames[0]).TrimEnd('\0')).Root!;
        Assert.Equal("CMD:DOWNLOAD-FILE^1@CMD:FILE-SYS-OPERATION^1@CMD:PROGRESS-REPORT^1@",
            capability.Element("arg")!.Element("host_capability")!.Value);
        var command = XDocument.Parse(Encoding.UTF8.GetString(frames[3]).TrimEnd('\0')).Root!;
        Assert.Equal("CMD:WRITE-FLASH", command.Element("command")!.Value);
        Assert.Equal(region, command.Element("arg")!.Element("partition")!.Value);
        Assert.Equal("0x200", command.Element("arg")!.Element("offset")!.Value);
        Assert.Equal("MEM://0x0:0x600", command.Element("arg")!.Element("source_file")!.Value);
        Assert.Equal(bytes, frames[9].Concat(frames[11]).Concat(frames[13]).ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes("OK@0x600\0"), frames[5]);
        Assert.Equal(Encoding.UTF8.GetBytes("OK@0x600\0"), frames[7]);
        Assert.All(frames.Skip(14), frame => Assert.Equal("OK\0"u8.ToArray(), frame));
        EnqueueXmlRead(transport, bytes);
        using var reference = new MemoryStream(bytes);
        var verification = await session.VerifyXmlStorageAsync(region, 512, bytes.Length, reference,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(verification.Matches);
    }

    [Fact]
    public async Task WriteXmlStorageAsync_LargeOffset_EmitsExact64BitAddress()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, capacity: 0x200000000);
        using var source = new MemoryStream(new byte[512]);
        EnqueueXmlWrite(transport, 512, 512);
        var before = transport.Writes.Count;

        await session.WriteXmlStorageAsync("EMMC-USER", 0x100000000, 512, source,
            cancellationToken: TestContext.Current.CancellationToken);

        var frames = DecodeXmlWriteFrames(transport.Writes.Skip(before));
        var command = XDocument.Parse(Encoding.UTF8.GetString(frames[3]).TrimEnd('\0')).Root!;
        Assert.Equal("0x100000000", command.Element("arg")!.Element("offset")!.Value);
        Assert.Equal(512, source.Position);
    }

    [Fact]
    public async Task WriteXmlStorageAsync_LargeLength_Advertises64BitSizeWithoutWholeSourceAllocation()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, capacity: 0x200000000);
        using var source = new NonSeekableStorageSource(new byte[512]);
        const long length = 0x100000200;
        EnqueueXmlWritePrefix(transport, length, 512);
        EnqueueXmlText(transport, "ERR!REJECTED");
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteXmlStorageAsync(
            "EMMC-USER", 0, length, source, cancellationToken: TestContext.Current.CancellationToken));

        var frames = DecodeXmlWriteFrames(transport.Writes.Skip(before));
        Assert.Single(frames, frame => frame.SequenceEqual(Encoding.UTF8.GetBytes("OK@0x100000200\0")));
        Assert.Equal(0, source.Position);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task WriteXmlStorageAsync_NegotiatedPacketCountAboveBound_RejectsBeforeReadingSource()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, capacity: 0x20000);
        using var source = new MemoryStream(new byte[0x20000]);
        EnqueueXmlWritePrefix(transport, 0x20000, 1);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteXmlStorageAsync(
            "EMMC-USER", 0, 0x20000, source, cancellationToken: TestContext.Current.CancellationToken));

        var frames = DecodeXmlWriteFrames(transport.Writes.Skip(before));
        Assert.Single(frames, frame => frame.SequenceEqual(Encoding.UTF8.GetBytes("OK@0x20000\0")));
        Assert.Equal(0, source.Position);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task WriteXmlStorageAsync_NegotiatedPacketCountAtBound_AcknowledgesDeclaredLengthBeforeFailingPhase()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, capacity: 0x10000);
        using var source = new MemoryStream(new byte[0x10000]);
        EnqueueXmlWritePrefix(transport, 0x10000, 1);
        EnqueueXmlText(transport, "ERR!REJECTED");
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteXmlStorageAsync(
            "EMMC-USER", 0, 0x10000, source, cancellationToken: TestContext.Current.CancellationToken));

        var frames = DecodeXmlWriteFrames(transport.Writes.Skip(before));
        Assert.Equal(2, frames.Count(frame => frame.SequenceEqual(Encoding.UTF8.GetBytes("OK@0x10000\0"))));
        Assert.Equal(0, source.Position);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task WriteXmlStorageAsync_MegabytePackets_UsesBoundedSourceReadsAndTransportWrites()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, capacity: 0x200000);
        var bytes = Enumerable.Range(0, 1024 * 1024 + 512).Select(index => (byte)index).ToArray();
        using var source = new XmlBoundedWriteSource(bytes);
        EnqueueXmlWrite(transport, bytes.Length, 1024 * 1024);
        var before = transport.Writes.Count;

        await session.WriteXmlStorageAsync("EMMC-USER", 0, bytes.Length, source,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(512, source.MaximumReadLength);
        Assert.Equal(bytes.Length, source.Position);
        var writes = transport.Writes.Skip(before).ToArray();
        Assert.All(writes, write => Assert.InRange(write.Length, 1, 512));
        var frames = DecodeXmlWriteFrames(writes);
        Assert.Equal(1024 * 1024, frames[9].Length);
        Assert.Equal(512, frames[11].Length);
        Assert.Equal(bytes, frames[9].Concat(frames[11]).ToArray());
    }

    [Fact]
    public async Task WriteXmlStorageAsync_FirstPacketRejected_DoesNotReadSecondPacket()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var source = new MemoryStream(new byte[1024]);
        EnqueueXmlWritePrefix(transport, 1024, 512);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, "ERR!REJECTED");
        var progress = new ProgressRecorder();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteXmlStorageAsync(
            "EMMC-USER", 0, 1024, source, progress: progress, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(512, source.Position);
        Assert.Empty(progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task WriteXmlStorageAsync_QueuedSourceShrinks_RevalidatesBeforeNextCommand()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var source = new PausedStorageSource();
        using var queuedSource = new MemoryStream(new byte[512]);
        EnqueueXmlWrite(transport, 512, 512);
        var before = transport.Writes.Count;
        var write = session.WriteXmlStorageAsync("EMMC-USER", 0, 512, source,
            cancellationToken: TestContext.Current.CancellationToken);
        await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task queued;
        try
        {
            queued = session.WriteXmlStorageAsync("EMMC-USER", 512, 512, queuedSource,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            queuedSource.SetLength(0);
        }
        finally
        {
            source.Resume.TrySetResult();
            await write;
        }

        await Assert.ThrowsAsync<ArgumentException>(() => queued);

        var frames = DecodeXmlWriteFrames(transport.Writes.Skip(before));
        Assert.Single(frames, frame => Encoding.UTF8.GetString(frame).Contains("CMD:WRITE-FLASH", StringComparison.Ordinal));
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        Assert.NotNull(session.XmlStorage);
    }

    [Theory]
    [InlineData("key", "DELETE")]
    [InlineData("key", "FILE-EXIST")]
    [InlineData("file_path", "C:/secret.bin")]
    [InlineData("file_path", "MEM://0x0:0x201")]
    [InlineData("extra", "value")]
    public async Task WriteXmlStorageAsync_UnexpectedFileOperation_RejectsBeforeReadingSource(string field, string value)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        var request = XmlFileSizeRequest(512);
        request.Element("arg")!.SetElementValue(field, value);
        EnqueueXmlText(transport, request.ToString(SaveOptions.DisableFormatting));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteXmlStorageAsync(
            "EMMC-USER", 0, 512, source, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Equal(0, source.Position);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Theory]
    [InlineData("source_file", "C:/source.bin")]
    [InlineData("source_file", "MEM://0x0:0x201")]
    [InlineData("checksum", "CHK_SUM")]
    [InlineData("info", "")]
    [InlineData("packet_length", "0x0")]
    [InlineData("packet_length", "0x100001")]
    [InlineData("packet_length", "512")]
    [InlineData("extra", "value")]
    public async Task WriteXmlStorageAsync_InvalidDownloadRequest_RejectsBeforeReadingSource(string field, string value)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlFileSizeRequest(512).ToString(SaveOptions.DisableFormatting));
        var request = XmlWriteRequest(512, 512);
        request.Element("arg")!.SetElementValue(field, value);
        EnqueueXmlText(transport, request.ToString(SaveOptions.DisableFormatting));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteXmlStorageAsync(
            "EMMC-USER", 0, 512, source, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(12, transport.Writes.Count - before);
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
    public async Task WriteXmlStorageAsync_RejectedPhase_FaultsWithoutReplay(int phase)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
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
            EnqueueXmlText(transport, XmlFileSizeRequest(512).ToString(SaveOptions.DisableFormatting));
            EnqueueXmlText(transport, XmlWriteRequest(512, 512).ToString(SaveOptions.DisableFormatting));
            EnqueueXmlText(transport, phase == 2 ? "ERR!REJECTED" : "OK");
            EnqueueXmlText(transport, phase == 3 ? "ERR!REJECTED" : "OK");
            EnqueueXmlText(transport, phase == 4 ? "ERR!NOT_OK" : "OK");
            EnqueueXmlText(transport, XmlControl("END", phase == 5 ? "NOT_OK" : "OK"));
            EnqueueXmlText(transport, XmlControl(phase == 6 ? "END" : "START"));
        }

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteXmlStorageAsync(
            "EMMC-USER", 0, 512, source, progress: progress, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(phase >= 4 ? 512 : 0, source.Position);
        Assert.Equal(phase >= 5 ? new long[] { 512 } : [], progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
        source.Position = 0;
        var before = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteXmlStorageAsync(
            "EMMC-USER", 0, 512, source, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before, transport.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteXmlStorageAsync_ShortSource_RejectsOrFaultsWithoutPadding(bool nonSeekable)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var source = nonSeekable ? new NonSeekableStorageSource(new byte[500]) : new MemoryStream(new byte[500]);
        EnqueueXmlWrite(transport, 512, 512);
        var before = transport.Writes.Count;

        if (nonSeekable)
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => session.WriteXmlStorageAsync(
                "EMMC-USER", 0, 512, source, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(19, transport.Writes.Count - before);
            Assert.Equal(MtkSessionState.Faulted, session.State);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => session.WriteXmlStorageAsync(
                "EMMC-USER", 0, 512, source, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(before, transport.Writes.Count);
            Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        }
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteXmlStorageAsync_Cancellation_DistinguishesPreIoAndAcknowledgedData(bool afterData)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        EnqueueXmlWrite(transport, 512, 512);
        if (!afterData)
        {
            cancellation.Cancel();
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WriteXmlStorageAsync(
            "EMMC-USER", 0, 512, source, progress: new InlineStorageProgress(_ => cancellation.Cancel()),
            cancellationToken: cancellation.Token));

        Assert.Equal(afterData ? 20 : 0, transport.Writes.Count - before);
        Assert.Equal(afterData ? 512 : 0, source.Position);
        Assert.Equal(afterData ? MtkSessionState.Faulted : MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task WriteXmlStorageAsync_QueuedReadCancellation_DoesNotInterruptActiveWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var source = new PausedStorageSource();
        using var destination = new MemoryStream();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        EnqueueXmlWrite(transport, 512, 512);
        var write = session.WriteXmlStorageAsync("EMMC-USER", 0, 512, source,
            cancellationToken: TestContext.Current.CancellationToken);
        await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;
        try
        {
            var read = session.ReadXmlStorageAsync("EMMC-USER", 0, 512, destination, cancellationToken: cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.Equal(before, transport.Writes.Count);
        }
        finally
        {
            source.Resume.TrySetResult();
            await write;
        }
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        Assert.Equal(0, destination.Length);
    }

    [Theory]
    [InlineData("EMMC-USER", -512, 512)]
    [InlineData("EMMC-USER", 1, 512)]
    [InlineData("EMMC-USER", 0, 0)]
    [InlineData("EMMC-USER", 0, 513)]
    [InlineData("EMMC-USER", 65536, 512)]
    [InlineData("EMMC-USER", long.MaxValue - 511, 512)]
    [InlineData("8", 0, 512)]
    public async Task XmlMutationsAsync_InvalidRange_RejectsBeforeIo(string region, long offset, long length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var source = new MemoryStream(new byte[1024]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.WriteXmlStorageAsync(
            region, offset, length, source, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.EraseXmlStorageAsync(
            region, offset, length, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XmlMutationsAsync_AuthenticatedRegion_RejectsBeforeIo(bool ufs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, ufs);
        using var source = new MemoryStream(new byte[512]);
        var region = ufs ? "UFS-LUA3" : "EMMC-RPMB";
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.WriteXmlStorageAsync(
            region, 0, 512, source, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.EraseXmlStorageAsync(
            region, 0, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    private static XElement XmlFileSizeRequest(long length) => new("host", new XElement("version", "1.0"),
        new XElement("command", "CMD:FILE-SYS-OPERATION"), new XElement("arg",
            new XElement("key", "FILE-SIZE"), new XElement("file_path", $"MEM://0x0:0x{length:x}")));

    private static XElement XmlWriteRequest(long length, int packetLength) => new("host",
        new XElement("version", "1.0"), new XElement("command", "CMD:DOWNLOAD-FILE"), new XElement("arg",
            new XElement("checksum", "CHK_NO"), new XElement("info", "write-flash"),
            new XElement("source_file", $"MEM://0x0:0x{length:x}"), new XElement("packet_length", $"0x{packetLength:x}")));

    private static void EnqueueXmlWritePrefix(ScriptedCommTransport transport, long length, int packetLength)
    {
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlFileSizeRequest(length).ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, XmlWriteRequest(length, packetLength).ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, "OK");
    }

    private static void EnqueueXmlWrite(ScriptedCommTransport transport, int length, int packetLength)
    {
        EnqueueXmlWritePrefix(transport, length, packetLength);
        for (var offset = 0; offset < length; offset += packetLength)
        {
            EnqueueXmlText(transport, "OK");
            EnqueueXmlText(transport, "OK");
        }
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }

    private static List<byte[]> DecodeXmlWriteFrames(IEnumerable<byte[]> writes)
    {
        var wire = writes.SelectMany(value => value).ToArray();
        var frames = new List<byte[]>();
        for (var offset = 0; offset < wire.Length;)
        {
            Assert.True(wire.Length - offset >= 12);
            Assert.Equal(0xFEEEEEEFu, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(offset)));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(offset + 4)));
            var length = BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(offset + 8));
            Assert.InRange(length, 1, wire.Length - offset - 12);
            frames.Add(wire.AsSpan(offset + 12, length).ToArray());
            offset += length + 12;
        }
        return frames;
    }

    private sealed class XmlBoundedWriteSource(byte[] bytes) : MemoryStream(bytes)
    {
        internal int MaximumReadLength { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumReadLength = Math.Max(MaximumReadLength, buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}