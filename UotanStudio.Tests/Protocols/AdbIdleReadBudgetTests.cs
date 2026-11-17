using System.Diagnostics;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class AdbIdleReadBudgetTests
{
    [Fact(Timeout = 30000)]
    public void Connect_TransportAnswersEveryReadEmpty_GivesUpInsteadOfSpinning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.False(cancellationToken.IsCancellationRequested);
        var transport = new ScriptedCommTransport();
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        var stopwatch = Stopwatch.StartNew();

        Assert.ThrowsAny<Exception>(session.Connect);

        stopwatch.Stop();
        Assert.True(transport.ReadCount > 0, "the idle wait must actually read from the transport");
        Assert.True(transport.ReadCount <= MaximumObservedReads,
            $"an idle link answered with empty reads spun {transport.ReadCount} times before giving up");
        Assert.True(stopwatch.ElapsedMilliseconds >= MinimumBackoffMs,
            $"the empty-read retries returned after {stopwatch.ElapsedMilliseconds} ms without any pause");
    }

    private const int MaximumObservedReads = 512;

    private const int MinimumBackoffMs = 200;
}