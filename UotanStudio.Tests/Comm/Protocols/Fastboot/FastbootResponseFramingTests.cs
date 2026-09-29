using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Fastboot;

/// <summary>
/// Response framing tests: a reply built from several INFO/TEXT frames must be accumulated in full
/// before the terminal frame completes it, and a transport that can no longer deliver bytes must fail
/// the command instead of being polled until the deadline expires.
/// <para>响应成帧测试：由多个 INFO/TEXT 帧构成的应答必须在终止帧完成它之前被完整累积；无法再交付字节的
/// 传输必须使命令失败，而不是被轮询到截止时间耗尽。</para>
/// </summary>
public class FastbootResponseFramingTests
{
    private static FastbootSession NewSession(ICommTransport transport)
        => new(transport, timeoutMs: 250);

    private static List<string> Commands(ScriptedCommTransport transport)
        => [.. transport.Writes.Select(Encoding.UTF8.GetString)];

    [Fact]
    public void ExecuteCommand_MultipleInfoFramesBeforeOkay_AccumulatesEveryLine()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("INFOone");
        transport.Enqueue("INFOtwo");
        transport.Enqueue("INFOthree");
        transport.Enqueue("OKAYdone");

        using var session = NewSession(transport);
        var response = session.ExecuteCommand("getvar:all");

        Assert.Equal(FastbootResponseState.Success, response.State);
        Assert.Equal(["one", "two", "three"], response.InfoLines);
        Assert.Equal("done", response.Info);
    }

    [Fact]
    public void ExecuteCommand_CoalescedInfoFrames_AccumulatesEveryLine()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("INFOone\0INFOtwo\0OKAYdone\0");

        using var session = NewSession(transport);
        var response = session.ExecuteCommand("getvar:all");

        Assert.Equal(FastbootResponseState.Success, response.State);
        Assert.Equal(["one", "two"], response.InfoLines);
        Assert.Equal("done", response.Info);
    }

    [Fact]
    public void ExecuteCommand_TextFrames_ConcatenateIntoResponseText()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("TEXThello ");
        transport.Enqueue("TEXTworld");
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);
        var response = session.ExecuteCommand("oem device-info");

        Assert.Equal(FastbootResponseState.Success, response.State);
        Assert.Equal("hello world", response.Text);
    }

    [Fact]
    public void GetAllVariables_MultiLineReply_PopulatesEveryReportedVariable()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("INFOproduct: dragon");
        transport.Enqueue("INFOversion-bootloader: 1.2.3");
        transport.Enqueue("INFOmax-download-size: 0x2000000");
        transport.Enqueue("OKAY");

        using var session = NewSession(transport);
        var variables = session.GetAllVariables();

        Assert.Equal(3, variables.Count);
        Assert.Equal("dragon", variables["product"]);
        Assert.Equal("1.2.3", variables["version-bootloader"]);
        Assert.Equal("0x2000000", variables["max-download-size"]);
        Assert.Equal(0x2000000L, session.GetMaxDownloadSize());
        Assert.Equal(1, Commands(transport).Count(c => c == "getvar:all"));
    }

    [Fact]
    public void ReceiveResponse_TransportDisconnected_FailsInsteadOfWaitingOutTheDeadline()
    {
        var transport = new ScriptedCommTransport();
        using var session = NewSession(transport);
        transport.Disconnect();

        var started = Environment.TickCount64;
        var response = session.ReceiveResponse();
        var elapsed = Environment.TickCount64 - started;

        Assert.Equal(FastbootResponseState.Fail, response.State);
        Assert.Contains("disconnected", response.Text, StringComparison.Ordinal);
        Assert.True(elapsed < 200, $"a disconnected transport must fail immediately (took {elapsed} ms)");
    }

    [Fact]
    public void ReceiveResponse_SilentConnectedTransport_ReportsTimeoutWithinTheDeadline()
    {
        var transport = new SilentConnectedTransport();
        using var session = NewSession(transport);

        var started = Environment.TickCount64;
        var response = session.ReceiveResponse();
        var elapsed = Environment.TickCount64 - started;

        Assert.Equal(FastbootResponseState.Timeout, response.State);
        Assert.InRange(elapsed, 200, 5000);
        Assert.True(transport.ReadCount > 1, "the loop must keep polling while the transport stays connected");
    }

    [Fact]
    public void ReceiveResponse_TransportReturningZeroWithEmptyReads_StaysWithinTheDeadline()
    {
        var transport = new ScriptedCommTransport();
        using var session = NewSession(transport);

        var started = Environment.TickCount64;
        var response = session.ReceiveResponse();
        var elapsed = Environment.TickCount64 - started;

        Assert.Equal(FastbootResponseState.Timeout, response.State);
        Assert.InRange(elapsed, 200, 5000);
    }

    private sealed class SilentConnectedTransport : ICommTransport
    {
        public int ReadCount { get; private set; }

        public bool IsConnected => true;

        public void Connect() { }

        public void Disconnect() { }

        public void Reset() { }

        public int Read(byte[] buffer, int offset, int length, int timeoutMs)
        {
            ReadCount++;
            return 0;
        }

        public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
            => Task.FromResult(Read(buffer, offset, length, timeoutMs));

        public long Write(byte[] data, int offset, int length, int timeoutMs) => length;

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
            => Task.FromResult(Write(data, offset, length, timeoutMs));

        public Stream GetStream() => throw new NotSupportedException();

        public void Dispose() { }
    }
}