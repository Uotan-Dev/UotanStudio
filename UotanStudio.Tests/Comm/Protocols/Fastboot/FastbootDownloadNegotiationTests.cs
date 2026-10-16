using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Fastboot;

/// <summary>
/// Download negotiation tests: a max-download-size the device never confirmed must not be cached, and
/// an abandoned download must be drained so the bootloader's download state machine returns to idle
/// instead of leaving the next command reading a stale reply.
/// <para>下载协商测试：设备从未确认过的 max-download-size 不得被缓存；被弃单的下载必须被排空，使
/// bootloader 的下载状态机回到空闲，而不是让下一条命令读到过期应答。</para>
/// </summary>
public class FastbootDownloadNegotiationTests
{
    private static FastbootSession NewSession(ScriptedCommTransport transport)
        => new(transport, timeoutMs: 250);

    private static List<string> Commands(ScriptedCommTransport transport)
        => [.. transport.Writes.Select(Encoding.UTF8.GetString)];

    private static long CountCommand(ScriptedCommTransport transport, string command)
        => Commands(transport).Count(c => c == command);

    [Fact]
    public void GetMaxDownloadSize_DeviceRejectedFirstQuery_ReturnsFallbackThenHonorsConfirmedValue()
    {
        var transport = new ScriptedCommTransport();
        var answers = new Queue<string>(["FAILunknown variable", "OKAY0x2000000"]);
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(answers.Dequeue())];

        using var session = NewSession(transport);

        Assert.Equal(FastbootConstants.FallbackMaxDownloadSize, session.GetMaxDownloadSize());
        Assert.Equal(0x2000000L, session.GetMaxDownloadSize());
        Assert.Equal(2, CountCommand(transport, "getvar:max-download-size"));
    }

    [Fact]
    public void GetMaxDownloadSize_ConfirmedValue_IsNegotiatedOnce()
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes("OKAY0x1000000")];

        using var session = NewSession(transport);

        Assert.Equal(0x1000000L, session.GetMaxDownloadSize());
        Assert.Equal(0x1000000L, session.GetMaxDownloadSize());
        Assert.Equal(0x1000000L, session.GetMaxDownloadSize());
        Assert.Equal(1, CountCommand(transport, "getvar:max-download-size"));
    }

    [Fact]
    public void GetMaxDownloadSize_UnparsableAnswer_ReturnsFallbackWithoutCachingIt()
    {
        var transport = new ScriptedCommTransport();
        var answers = new Queue<string>(["OKAYnot-a-number", "OKAY0x4000000"]);
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(answers.Dequeue())];

        using var session = NewSession(transport);

        Assert.Equal(FastbootConstants.FallbackMaxDownloadSize, session.GetMaxDownloadSize());
        Assert.Equal(0x4000000L, session.GetMaxDownloadSize());
    }

    [Fact]
    public void GetMaxDownloadSize_DeviceReportsMoreThanWireCeiling_ClampsToUintMax()
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes("OKAY0x1FFFFFFFF")];

        using var session = NewSession(transport);

        Assert.Equal((long)uint.MaxValue, session.GetMaxDownloadSize());
    }

    [Fact]
    public void DownloadData_DeviceAcceptsFewerBytes_ZeroFillsAnnouncedSizeThenDrainsTerminal()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAY0x10000");
        transport.Enqueue("DATA00000800");
        transport.Enqueue("OKAY");
        transport.Enqueue("OKAYdragon");

        using var session = NewSession(transport);
        using var payload = new MemoryStream(new byte[4096]);
        var response = session.DownloadData(payload, 4096, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(FastbootResponseState.Data, response.State);
        Assert.Contains("download size mismatch", response.Text, StringComparison.Ordinal);
        Assert.Equal(new byte[2048], transport.Writes[2]);
        Assert.Equal(0, transport.ResetCount);
        Assert.Equal("dragon", session.GetVariable("product", useCache: false));
    }

    [Fact]
    public void DownloadData_SourceStreamEndsEarly_ZeroFillsUnsentRemainder()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAY0x10000");
        transport.Enqueue("DATA00001000");
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);
        using var payload = new MemoryStream(new byte[100]);

        Assert.Throws<EndOfStreamException>(() =>
            session.DownloadData(payload, 4096, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new byte[3996], transport.Writes[3]);
        Assert.Equal(0, transport.ResetCount);
    }

    [Fact]
    public void DownloadData_CancelledBeforeFirstChunk_ZeroFillsWholeAnnouncedSize()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAY0x10000");
        transport.Enqueue("DATA00001000");
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);
        using var payload = new MemoryStream(new byte[4096]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            session.DownloadData(payload, 4096, cancellationToken: cancelled.Token));

        Assert.Equal(new byte[4096], transport.Writes[2]);
        Assert.Equal(0, transport.ResetCount);
    }

    [Fact]
    public void DownloadData_DeviceNeverAcknowledgesAbandon_ResetsTransportAndRerenegotiates()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAY0x10000");
        transport.Enqueue("DATA00001000");

        using var session = NewSession(transport);
        using var payload = new MemoryStream(new byte[100]);

        Assert.Throws<EndOfStreamException>(() =>
            session.DownloadData(payload, 4096, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, transport.ResetCount);

        transport.Enqueue("OKAY0x10000");
        transport.Enqueue("OKAYdragon");
        Assert.Equal(0x10000L, session.GetMaxDownloadSize());
        Assert.Equal("dragon", session.GetVariable("product", useCache: false));
        Assert.Equal(2, CountCommand(transport, "getvar:max-download-size"));
    }

    [Fact]
    public void DownloadData_TransportCannotAcceptPadding_ResetsTransport()
    {
        var payloadWrites = 0;
        var transport = new ScriptedCommTransport
        {
            RejectWrite = (data, offset, length) =>
                data[offset] == 0 && Interlocked.Increment(ref payloadWrites) > 1
        };
        transport.Enqueue("OKAY0x10000");
        transport.Enqueue("DATA00001000");

        using var session = NewSession(transport);
        using var payload = new MemoryStream(new byte[100]);

        Assert.Throws<EndOfStreamException>(() =>
            session.DownloadData(payload, 4096, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, transport.ResetCount);
    }

    [Fact]
    public void DownloadData_PayloadExceedsNegotiatedLimit_RejectsBeforeAnyCommand()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAY0x1000");

        using var session = NewSession(transport);
        using var payload = new MemoryStream(new byte[8192]);

        Assert.Throws<FastbootException>(() =>
            session.DownloadData(payload, 8192, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(["getvar:max-download-size"], Commands(transport));
    }
}