using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task InitializeAsync_LegacyWithoutAttestedPatchSite_FailsBeforeAnyExchange()
    {
        using var transport = new ScriptedCommTransport();
        using var dir = new TempDirectory();
        var options = LegacyBootstrapOptions(dir) with
        {
            Legacy = LegacyProfile() with { PatchStageTwoSecurityChecks = true },
        };
        EnqueueLegacyBootstrap(transport, false);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => MtkBootstrap.InitializeAsync(transport,
            MtkDaProtocol.Legacy, options, TestContext.Current.CancellationToken));

        Assert.Contains("no attested Legacy download-agent patch site", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
    }
}