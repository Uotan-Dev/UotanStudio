using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Hdc;

public sealed class HdcTransferTests
{
    [Fact]
    public void TransferMetadata_UsesUpstreamStringOptionsAndReservedPrefix()
    {
        var expected = Convert.FromHexString("08031000180022002A022F61320038004000480052005A0062006A00");
        var config = new TransferConfig { FileSize = 3, Path = "/a", HoldTimestamp = false };
        Assert.Equal(expected, HdcTransferCodec.EncodeConfig(config));
        Assert.Equal("/a", HdcTransferCodec.DecodeConfig(expected).Path);
        var chunk = HdcTransferCodec.EncodeChunk(128, "abc"u8);
        Assert.Equal(Convert.FromHexString("088001100018032003"), chunk[..9]);
        Assert.Equal(67, chunk.Length);
        Assert.Equal("abc"u8.ToArray(), HdcTransferCodec.DecodeChunk(chunk).Data);
        Assert.Throws<InvalidDataException>(() => HdcTransferCodec.DecodeChunk(chunk[..^1]));
        Assert.Throws<InvalidDataException>(() => HdcTransferCodec.DecodeConfig([]));
    }

    [Fact]
    public void Lz4_DecodesIndependentLiteralBlock_AndBoundsExpandedSize()
    {
        var packet = new byte[68];
        Convert.FromHexString("0800100118042003").CopyTo(packet, 0);
        new byte[] { 0x30, 0x61, 0x62, 0x63 }.CopyTo(packet, 64);
        Assert.Equal("abc"u8.ToArray(), HdcTransferCodec.DecodeChunk(packet).Data);
        packet[7] = 4;
        Assert.Throws<InvalidDataException>(() => HdcTransferCodec.DecodeChunk(packet));
        var input = new byte[48000];
        var compressed = HdcTransferCodec.EncodeChunk(0, input, true);
        Assert.True(compressed.Length < input.Length);
        Assert.Equal(input, HdcTransferCodec.DecodeChunk(compressed).Data);
    }

    [Fact]
    public void Install_SendsDispatchFunction_AndWaitsForCorrectAppMode()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".hap");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            using var session = Session(message =>
            {
                if (message.Command == HdcCommand.AppCheck)
                {
                    var config = HdcTransferCodec.DecodeConfig(message.Payload!);
                    Assert.Equal("install", config.FunctionName);
                    Assert.EndsWith(".hap", config.OptionalName);
                    return [Reply(message, HdcCommand.AppBegin, [])];
                }
                if (message.Command == HdcCommand.AppData)
                {
                    Assert.Equal(new byte[] { 1, 2, 3 }, HdcTransferCodec.DecodeChunk(message.Payload!).Data);
                    return [Reply(message, HdcCommand.AppFinish, [1, 1, .. "installed"u8.ToArray()])];
                }
                return [];
            });
            Assert.Equal("installed", new HdcAppClient(session.Connection).Install(path, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CancelledTransfer_DoesNotOpenDeviceTask()
    {
        var opened = false;
        using var session = Session(message => { opened = true; return []; });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var transfer = new HdcSyncClient(session.Connection);
        Assert.ThrowsAny<OperationCanceledException>(() => transfer.SendStream(new MemoryStream(), "/x", 0, cancellationToken: cancelled.Token));
        Assert.False(opened);
    }

    [Theory]
    [InlineData("source/a.txt", true)]
    [InlineData("../escape.txt", false)]
    [InlineData("source/../../escape.txt", false)]
    [InlineData("/absolute", false)]
    [InlineData("source/a:stream", false)]
    public void DirectoryReceive_PublishesCompleteTree_RejectsTraversal(string name, bool valid)
    {
        var destination = Path.Combine(Path.GetTempPath(), "hdc-directory-test-" + Guid.NewGuid().ToString("N"));
        using var session = Session(message => message.Command switch
        {
            HdcCommand.FileInit => [Reply(message, HdcCommand.FileCheck, HdcTransferCodec.EncodeConfig(new() { FileSize = 3, OptionalName = name }))],
            HdcCommand.FileBegin => [Reply(message, HdcCommand.FileData, HdcTransferCodec.EncodeChunk(0, "abc"u8))],
            HdcCommand.FileFinish => [Reply(message, HdcCommand.FileFinish, [0])],
            _ => [],
        });
        using var transfer = new HdcSyncClient(session.Connection);
        try
        {
            if (valid)
            {
                transfer.RecvDirectory("/system/source", destination, 3, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal("abc", File.ReadAllText(Path.Combine(destination, "source", "a.txt")));
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => transfer.RecvDirectory("/system/source", destination, 3, cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(Directory.Exists(destination));
            }
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(destination)!, Path.GetFileName(destination) + ".uotan-*"));
        }
        finally { if (Directory.Exists(destination)) Directory.Delete(destination, true); }
    }

    [Fact]
    public void Receive_ChecksOffsetsAndCompletesPeerHandshake()
    {
        var finished = false;
        using var session = Session(message => message.Command switch
        {
            HdcCommand.FileInit => [Reply(message, HdcCommand.WakeupSlaveTask, []), Reply(message, HdcCommand.FileCheck, HdcTransferCodec.EncodeConfig(new() { FileSize = 3, HoldTimestamp = false }))],
            HdcCommand.FileBegin => [Reply(message, HdcCommand.FileData, HdcTransferCodec.EncodeChunk(0, "abc"u8))],
            HdcCommand.FileFinish => Finish(),
            _ => [],
        });
        using var output = new MemoryStream();
        using var transfer = new HdcSyncClient(session.Connection);
        transfer.RecvStream("/system/sample", output, TestContext.Current.CancellationToken);
        Assert.Equal("abc"u8.ToArray(), output.ToArray());
        Assert.True(finished);

        byte[][] Finish() { finished = true; return [HdcDaemonCodec.Encode(new(HdcCommand.FileFinish, 1, 0, [0]))]; }
    }

    [Fact]
    public void Receive_TruncatedFile_DoesNotReplaceExistingDestination()
    {
        using var session = Session(message => message.Command switch
        {
            HdcCommand.FileInit => [Reply(message, HdcCommand.FileCheck, HdcTransferCodec.EncodeConfig(new() { FileSize = 10 }))],
            HdcCommand.FileBegin => [Reply(message, HdcCommand.ChannelClose, [0])],
            _ => [],
        });
        var path = Path.Combine(Path.GetTempPath(), "hdc-receive-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "existing");
        try
        {
            using var transfer = new HdcSyncClient(session.Connection);
            Assert.Throws<EndOfStreamException>(() => transfer.Recv("/system/sample", path, TestContext.Current.CancellationToken));
            Assert.Equal("existing", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Send_StreamsMultipleChunks_AndWaitsForReceiverCompletion()
    {
        var input = new byte[100000]; Random.Shared.NextBytes(input);
        using var received = new MemoryStream();
        var acknowledged = false;
        using var session = Session(message =>
        {
            if (message.Command == HdcCommand.FileCheck)
                return [Reply(message, HdcCommand.FileBegin, [])];
            if (message.Command == HdcCommand.FileData)
            {
                var chunk = HdcTransferCodec.DecodeChunk(message.Payload!);
                Assert.Equal(received.Length, chunk.Offset);
                received.Write(chunk.Data);
                if (received.Length == input.Length) return [Reply(message, HdcCommand.FileFinish, [1])];
            }
            if (message.Command == HdcCommand.FileFinish) { Assert.Equal(new byte[] { 0 }, message.Payload); acknowledged = true; }
            return [];
        });
        using var transfer = new HdcSyncClient(session.Connection);
        transfer.SendStream(new MemoryStream(input), "/data/local/tmp/sample", input.Length, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(input, received.ToArray());
        Assert.True(acknowledged);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AppUninstall_UsesResultFlag_NotChannelClosure(int success)
    {
        using var session = Session(message => message.Command == HdcCommand.AppUninstall
            ? [Reply(message, HdcCommand.AppFinish, [2, (byte)success, .. "result"u8.ToArray()])] : []);
        var app = new HdcAppClient(session.Connection);
        if (success == 0) Assert.Throws<IOException>(() => app.Uninstall("org.example.app", TestContext.Current.CancellationToken));
        else Assert.Equal("result", app.Uninstall("org.example.app", TestContext.Current.CancellationToken));
    }

    private static byte[] Reply(HdcMessage message, HdcCommand command, byte[] payload)
        => HdcDaemonCodec.Encode(new(command, message.ChannelId, 0, payload));

    internal static HdcSession Session(Func<HdcMessage, byte[][]> handler)
    {
        var transport = new FakeCommTransport();
        var slaveTasks = new HashSet<uint>();
        transport.OnWrite = packet =>
        {
            var message = HdcDaemonCodec.Decode(packet);
            if (message.Command == HdcCommand.WakeupSlaveTask) slaveTasks.Add(message.ChannelId);
            if (message.Command is HdcCommand.AppCheck or HdcCommand.FileCheck or HdcCommand.ForwardCheck)
                Assert.Contains(message.ChannelId, slaveTasks);
            if (message.Command == HdcCommand.ChannelClose) slaveTasks.Remove(message.ChannelId);
            if (message.Command == HdcCommand.ChannelClose && message.ChannelId == 0) return [];
            return message.Command == HdcCommand.Handshake
                ? [HdcDaemonCodec.Encode(new(HdcCommand.Handshake, 0, 0, HdcDaemonCodec.Handshake(4, 0, "", [], "test"))),
                    HdcDaemonCodec.Encode(new(HdcCommand.ChannelClose, 0, 0, [1]))]
                : handler(message);
        };
        var session = new HdcSession(transport, HdcTestKeys.CreateAuthentication());
        session.Connect(); return session;
    }

    [Theory]
    [InlineData(0, "install bundle successfully. ", true)]
    [InlineData(1, "installed", true)]
    [InlineData(0, "error: install bundle successfully. expected but failed", false)]
    [InlineData(0, "error: no signature file.", false)]
    [InlineData(1, "error: failed to install bundle.", false)]
    [InlineData(2, "install bundle successfully.", false)]
    public void Install_HandlesUpstreamAndHarmonyResultConventions(byte status, string text, bool success)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".hap");
        File.WriteAllBytes(path, [1]);
        try
        {
            using var session = Session(message => message.Command switch
            {
                HdcCommand.AppCheck => [Reply(message, HdcCommand.AppBegin, [])],
                HdcCommand.AppData => [Reply(message, HdcCommand.AppFinish, [1, status, .. System.Text.Encoding.UTF8.GetBytes(text)])],
                _ => [],
            });
            var client = new HdcAppClient(session.Connection);
            if (success) Assert.Equal(text, client.Install(path, "-r", TestContext.Current.CancellationToken));
            else Assert.Throws<IOException>(() => client.Install(path, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FailedTasks_ReleaseChannelSlotsWithoutWaitingForPeerClose()
    {
        using var session = Session(message => message.Command == HdcCommand.FileInit
            ? [Reply(message, HdcCommand.Echo, [0, .. "denied"u8.ToArray()])] : []);
        using var transfer = new HdcSyncClient(session.Connection);
        for (var i = 0; i < 140; i++)
        {
            using var destination = new MemoryStream();
            var failure = Assert.Throws<IOException>(() => transfer.RecvStream("/missing", destination, TestContext.Current.CancellationToken));
            Assert.Contains("denied", failure.ToString());
        }
    }
}