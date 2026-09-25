using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Fact]
    public async Task AttachXmlAgentAsync_StartMessage_AcknowledgesWithoutClaimingStorageReadiness()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStartedSessionAsync(transport);
        EnqueueXmlText(transport, XmlControl("START"));
        var before = transport.Writes.Count;

        await session.AttachXmlAgentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.XmlControlReady, session.State);
        Assert.Null(session.XFlashStorage);
        Assert.Equal(2, transport.Writes.Count - before);
        Assert.Equal("OK\0"u8.ToArray(), transport.Writes[^1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EraseXFlashStorageAsync(
            8, 0, 512, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, transport.Writes.Count - before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetXmlHostInfoAsync_CompleteExchange_ConsumesLogsAndProgressBeforeReturning(bool progress)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlControlSessionAsync(transport);
        EnqueueXFlashFrame(transport, [1, 0, 0, 0, .. "log\0"u8.ToArray()], type: 2);
        EnqueueXmlText(transport, "OK");
        if (progress)
        {
            EnqueueXmlText(transport, XmlControl("PROGRESS-REPORT"));
            EnqueueXmlText(transport, "50%");
            EnqueueXmlText(transport, "OK!EOT");
        }
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
        var before = transport.Writes.Count;

        await session.SetXmlHostInfoAsync("operator<&>", TestContext.Current.CancellationToken);

        Assert.Equal(progress ? 12 : 6, transport.Writes.Count - before);
        var request = XDocument.Parse(Encoding.UTF8.GetString(transport.Writes[before + 1]).TrimEnd('\0'));
        Assert.Equal("da", request.Root!.Name.LocalName);
        Assert.Equal("CMD:SET-HOST-INFO", request.Root.Element("command")!.Value);
        Assert.Equal("operator<&>", request.Root.Element("arg")!.Element("info")!.Value);
        Assert.All(transport.Writes.Skip(before + 2).Where((_, index) => index % 2 == 1),
            bytes => Assert.Equal("OK\0"u8.ToArray(), bytes));
        Assert.Equal(MtkSessionState.XmlControlReady, session.State);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("end-error")]
    [InlineData("wrong-end")]
    [InlineData("missing-start")]
    [InlineData("start-error")]
    [InlineData("file-request")]
    [InlineData("binary-request")]
    public async Task SetXmlHostInfoAsync_IncompleteOrUnsupportedExchange_FaultsWithoutFallback(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlControlSessionAsync(transport);
        EnqueueXmlText(transport, failure == "rejected" ? "ERR!UNSUPPORTED" : "OK");
        EnqueueXmlText(transport, failure switch
        {
            "end-error" => XmlControl("END", "ERROR"),
            "wrong-end" => XmlControl("START"),
            "file-request" => XmlControl("DOWNLOAD-FILE"),
            "binary-request" => "OK@0x100",
            _ => XmlControl("END", "OK")
        });
        if (failure != "missing-start")
        {
            EnqueueXmlText(transport, XmlControl("START", failure == "start-error" ? "ERROR" : null));
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<Exception>(() => session.SetXmlHostInfoAsync("host", TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Equal(failure is "missing-start" or "start-error" ? 4 : 2, transport.Writes.Count - before);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetXmlHostInfoAsync("host", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetXmlHostInfoAsync_ProgressWithoutTerminator_StopsAtPacketBound()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlControlSessionAsync(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlControl("PROGRESS-REPORT"));
        for (var i = 0; i < 256; i++)
        {
            EnqueueXmlText(transport, "still working");
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.SetXmlHostInfoAsync(
            "host", TestContext.Current.CancellationToken));

        Assert.Equal(2 + 2 + 256 * 2, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task SetXmlHostInfoAsync_QueuedStorageCall_CannotInterleaveControlExchange()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlControlSessionAsync(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
        Task? queued = null;
        transport.OnWrite = _ =>
        {
            queued ??= session.EraseXFlashStorageAsync(8, 0, 512, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            return [];
        };
        var before = transport.Writes.Count;

        await session.SetXmlHostInfoAsync("host", TestContext.Current.CancellationToken);

        Assert.NotNull(queued);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        Assert.Equal(6, transport.Writes.Count - before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachXmlAgentAsync_Cancellation_DistinguishesPreIoAndPartialAck(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStartedSessionAsync(transport);
        EnqueueXmlText(transport, XmlControl("START"));
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AttachXmlAgentAsync(cancellation.Token));

        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.AgentStarted, session.State);
        Assert.Equal(inFlight ? 1 : 0, transport.Writes.Count - before);
    }

    [Theory]
    [InlineData("<!DOCTYPE host [<!ENTITY x SYSTEM 'file:///not-opened'>]><host><version>1.0</version><command>&x;</command></host>")]
    [InlineData("<host><version>1.0</version><version>1.0</version><command>CMD:START</command></host>")]
    [InlineData("<host><version>9.0</version><command>CMD:START</command></host>")]
    [InlineData("<host xmlns='other'><version>1.0</version><command>CMD:START</command></host>")]
    [InlineData("<host><version>1.0</version><command attr='x'>CMD:START</command></host>")]
    [InlineData("<host><version>1.0</version><command>CMD:START</command><arg><a>x</a><a>y</a></arg></host>")]
    [InlineData("<host><version>1.0</version><command>CMD:START</command><arg><a><b><c/></b></a></arg></host>")]
    [InlineData("<host><version>1.0</version><command>CMD:START</command><unknown/></host>")]
    public void XmlMessage_UnsupportedStructure_RejectsWithoutResolvingResources(string text)
    {
        Assert.ThrowsAny<Exception>(() => MtkXmlMessage.Parse(text));
    }

    [Theory]
    [InlineData("unknown-type")]
    [InlineData("short-log")]
    [InlineData("utf8")]
    [InlineData("embedded-nul")]
    [InlineData("log-flood")]
    [InlineData("oversized")]
    public async Task XmlChannel_InvalidFrame_FaultsAndCannotBeReused(string failure)
    {
        using var transport = new ScriptedCommTransport();
        var channel = new MtkXmlChannel(transport, 5000);
        switch (failure)
        {
            case "unknown-type": EnqueueXFlashFrame(transport, "OK"u8.ToArray(), 3); break;
            case "short-log": EnqueueXFlashFrame(transport, new byte[3], 2); break;
            case "utf8": EnqueueXFlashFrame(transport, [0xFF]); break;
            case "embedded-nul": EnqueueXFlashFrame(transport, "O\0K"u8.ToArray()); break;
            case "log-flood":
                for (var i = 0; i < 65; i++)
                {
                    EnqueueXFlashFrame(transport, new byte[4], 2);
                }
                break;
            case "oversized":
                transport.Enqueue(Convert.FromHexString("EFEEEEFE0100000001000400"));
                break;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => channel.ReceiveTextAsync(TestContext.Current.CancellationToken));

        Assert.True(channel.IsFaulted);
        var reads = transport.ReadCount;
        await Assert.ThrowsAsync<MtkProtocolException>(() => channel.ReceiveTextAsync(TestContext.Current.CancellationToken));
        Assert.Equal(reads, transport.ReadCount);
        Assert.Empty(transport.Writes);
    }

    private static string XmlControl(string command, string? result = null) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + new XElement("host", new XElement("version", "1.0"),
            new XElement("command", "CMD:" + command), result is null ? null : new XElement("result", result))
        .ToString(SaveOptions.DisableFormatting);

    private static void EnqueueXmlText(ScriptedCommTransport transport, string text) =>
        EnqueueXFlashFrame(transport, [.. Encoding.UTF8.GetBytes(text), 0]);

    private static async Task<MtkSession> CreateXmlControlSessionAsync(ScriptedCommTransport transport)
    {
        var session = await CreateStartedSessionAsync(transport);
        EnqueueXmlText(transport, XmlControl("START"));
        await session.AttachXmlAgentAsync(TestContext.Current.CancellationToken);
        return session;
    }
}