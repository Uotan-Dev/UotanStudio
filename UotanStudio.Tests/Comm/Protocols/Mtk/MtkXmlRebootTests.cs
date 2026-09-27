using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkXmlBootMode.Normal, null)]
    [InlineData(MtkXmlBootMode.Fastboot, "FASTBOOT")]
    [InlineData(MtkXmlBootMode.Meta, "META")]
    [InlineData(MtkXmlBootMode.AndroidTest, "ANDROID-TEST-MODE")]
    public async Task RebootXmlAsync_CompleteResponses_UsesSelectedModeAndTerminates(MtkXmlBootMode mode, string? wireMode)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRebootSessionAsync(transport);
        if (wireMode is not null)
        {
            EnqueueXmlCompletion(transport);
        }
        EnqueueXmlCompletion(transport);
        var before = transport.Writes.Count;

        await session.RebootXmlAsync(new() { Mode = mode }, cancellationToken: TestContext.Current.CancellationToken);

        var commands = DecodeXmlWriteFrames(transport.Writes.Skip(before).ToArray()).Select(Encoding.UTF8.GetString)
            .Where(text => text.Contains("<command>", StringComparison.Ordinal)).Select(text => XElement.Parse(text.TrimEnd('\0'))).ToArray();
        Assert.Equal(wireMode is null ? 1 : 2, commands.Length);
        if (wireMode is not null)
        {
            Assert.Equal("CMD:SET-BOOT-MODE", commands[0].Element("command")!.Value);
            var args = commands[0].Element("arg")!;
            Assert.Equal(wireMode, args.Element("mode")!.Value);
            Assert.Equal("USB", args.Element("connect_type")!.Value);
            Assert.Equal("OFF", args.Element("mobile_log")!.Value);
            Assert.Equal("OFF", args.Element("adb")!.Value);
        }
        Assert.Equal("CMD:REBOOT", commands.Last().Element("command")!.Value);
        Assert.Equal("IMMEDIATE", commands.Last().Element("arg")!.Element("action")!.Value);
        Assert.Equal(MtkSessionState.XmlRebootAcknowledged, session.State);
        Assert.False(session.IsConnected);
        Assert.False(transport.IsConnected);
        Assert.Null(session.XmlStorage);
        var after = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RebootXmlAsync(
            new() { Mode = mode }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(after, transport.Writes.Count);
    }

    [Fact]
    public async Task RebootXmlAsync_DisconnectFails_PreservesTerminalState()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRebootSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        transport.OnDisconnect = () => throw new IOException("Cannot close.");

        await Assert.ThrowsAsync<IOException>(() => session.RebootXmlAsync(
            new() { Mode = MtkXmlBootMode.Normal }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.XmlRebootAcknowledged, session.State);
        Assert.False(session.IsConnected);
        Assert.True(transport.IsConnected);
        Assert.Null(session.XmlStorage);
    }

    [Theory]
    [InlineData("mode-reject")]
    [InlineData("reboot-reject")]
    [InlineData("missing-end")]
    [InlineData("missing-start")]
    public async Task RebootXmlAsync_IncompleteResponse_FaultsWithoutRetry(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRebootSessionAsync(transport);
        var before = XmlBootstrapCommands(transport).Length;
        if (failure != "mode-reject")
        {
            EnqueueXmlCompletion(transport);
        }
        EnqueueXmlText(transport, failure.EndsWith("reject", StringComparison.Ordinal) ? "ERR!REJECTED" : "OK");
        if (failure == "missing-start")
        {
            EnqueueXmlText(transport, XmlControl("END", "OK"));
        }

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.RebootXmlAsync(
            new() { Mode = MtkXmlBootMode.Fastboot }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.False(session.IsConnected);
        Assert.Null(session.XmlStorage);
        Assert.Equal(failure == "mode-reject" ? 1 : 2, XmlBootstrapCommands(transport).Length - before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RebootXmlAsync_Cancellation_DistinguishesPreflightAndUncertainOutcome(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRebootSessionAsync(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RebootXmlAsync(
            new() { Mode = MtkXmlBootMode.Normal }, cancellationToken: cancellation.Token));

        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.XmlStorageGeometryKnown, session.State);
        Assert.Equal(inFlight ? 1 : 0, transport.Writes.Count - before);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("flags")]
    [InlineData("state")]
    [InlineData("timeout")]
    public async Task RebootXmlAsync_InvalidPrecondition_RejectsWithoutIo(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = failure == "state" ? new MtkSession(transport) : await CreateXmlRebootSessionAsync(transport);
        var options = new MtkXmlRebootOptions
        {
            Mode = failure == "mode" ? (MtkXmlBootMode)999 : MtkXmlBootMode.Normal,
            EnableAdb = failure == "flags"
        };
        var before = transport.Writes.Count;
        var state = session.State;

        await Assert.ThrowsAnyAsync<Exception>(() => session.RebootXmlAsync(options, failure == "timeout" ? 0 : 1000,
            TestContext.Current.CancellationToken));

        Assert.Equal(state, session.State);
        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task RebootXmlAsync_QueuedStorageQuery_CannotReuseTerminatedChannel()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRebootSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        Task? queued = null;
        transport.OnWrite = _ =>
        {
            queued ??= session.DiscoverXmlStorageAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            return [];
        };

        await session.RebootXmlAsync(new() { Mode = MtkXmlBootMode.Normal },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(queued);
        var after = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        Assert.Equal(after, transport.Writes.Count);
    }

    private static async Task<MtkSession> CreateXmlRebootSessionAsync(ScriptedCommTransport transport)
    {
        var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlHardwareDocument(false));
        await session.DiscoverXmlStorageAsync(cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }
}