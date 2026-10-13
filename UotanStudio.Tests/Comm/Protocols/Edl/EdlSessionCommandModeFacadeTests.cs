using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public class EdlSessionCommandModeFacadeTests
{
    [Fact]
    public void EnsureCommandModeSahara_FreshLink_ReusesOneSessionOwnedInstanceWithoutDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new EdlSession(transport);

        var first = session.EnsureCommandModeSahara();
        var second = session.EnsureCommandModeSahara();

        Assert.Same(first, second);
        Assert.Same(first, session.Sahara);
        Assert.Empty(transport.Writes);
        Assert.False(first.IsCommandModeReady);
    }

    [Fact]
    public void ResetCommandModeNegotiation_DroppedLink_ForcesAFreshHandshakeRecord()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new EdlSession(transport);
        var sahara = session.EnsureCommandModeSahara();

        sahara.ResetCommandModeNegotiation();

        Assert.False(sahara.IsCommandModeReady);
        Assert.Same(sahara, session.EnsureCommandModeSahara());
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void EnsureCommandModeSahara_DisposedSession_RefusesBeforeTouchingTheTransport()
    {
        var transport = new ScriptedCommTransport();
        var session = new EdlSession(transport);
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.EnsureCommandModeSahara());
        Assert.Empty(transport.Writes);
    }
}