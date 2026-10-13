using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    private static async Task<MtkSession> CreateSlaSessionAsync(ScriptedCommTransport transport)
    {
        EnqueueIdentity(transport);
        var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue(Convert.FromHexString("D70020000000000008000000001D0D"));
        return session;
    }

    private static void EnqueueSlaExchange(ScriptedCommTransport transport, string failure = "")
    {
        transport.Enqueue(failure == "initial" ? [0xE3, 1, 0] : [0xE3, 0, 0]);
        transport.Enqueue(Convert.FromHexString("0000000401020304"));
        transport.Enqueue(failure == "length" ? [0, 0, 0, 7] : [0, 0, 0, 8]);
        transport.Enqueue(failure == "response-status" ? [1, 0] : [0, 0]);
        transport.Enqueue(failure == "final" ? [0, 0, 0, 0xFF]
            : failure == "truncated" ? [0] : new byte[4]);
    }
}