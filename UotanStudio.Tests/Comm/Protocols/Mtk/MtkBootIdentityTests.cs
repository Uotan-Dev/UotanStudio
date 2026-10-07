using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    private static readonly byte[] DocumentedMeid =
    [
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10
    ];

    private static readonly byte[] DocumentedSocid =
    [
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF,
        0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7,
        0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF
    ];

    [Fact]
    public async Task InitializeAsync_BootRomIdentityAnswers_PopulatesVersionsMeidAndSocid()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue([
            0x5F, 0xF5, 0xAF, 0xFA,
            0xFD, 0x07, 0x66, 0, 0,
            0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
            0xD8, 0, 0, 0, 0x17, 0, 0
        ]);
        transport.Enqueue([0x02]);
        transport.Enqueue([0x1D]);
        transport.Enqueue([0xFE]);
        transport.Enqueue([0xE1]);
        transport.Enqueue([0, 0, 0, 16]);
        transport.Enqueue(DocumentedMeid);
        transport.Enqueue([0, 0]);
        transport.Enqueue([0xFE]);
        transport.Enqueue([0xE7]);
        transport.Enqueue([0, 0, 0, 32]);
        transport.Enqueue(DocumentedSocid);
        transport.Enqueue([0, 0]);
        using var session = new MtkSession(transport);

        var info = await session.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal((byte)2, info.BlVersion);
        Assert.Equal((byte)0x1D, info.BromVersion);
        Assert.Equal(DocumentedMeid, info.Meid);
        Assert.Equal(DocumentedSocid, info.Socid);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
        Assert.Equal(new byte[] { 0xA0, 0x0A, 0x50, 0x05, 0xFD, 0xFC, 0xD8, 0xFE, 0xFF, 0xFE, 0xE1, 0xFE, 0xE7 },
            transport.Writes.SelectMany(bytes => bytes));
    }

    [Fact]
    public async Task InitializeAsync_SilentIdentityQueries_DegradesToNullAndStaysBootloader()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue([
            0x5F, 0xF5, 0xAF, 0xFA,
            0xFD, 0x07, 0x66, 0, 0,
            0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
            0xD8, 0, 0, 0, 0x17, 0, 0
        ]);
        using var session = new MtkSession(transport);

        var info = await session.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
        Assert.Null(info.BlVersion);
        Assert.Null(info.BromVersion);
        Assert.Null(info.Meid);
        Assert.Null(info.Socid);
        Assert.Equal(11, transport.Writes.Count);
        Assert.Contains(session.Evidence, entry => entry.Phase == "boot-identity"
            && entry.Outcome != CommandOutcomeKind.Completed);
    }

    [Fact]
    public async Task InitializeAsync_MeidStatusRejected_KeepsSocidAnswerAndStaysBootloader()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue([
            0x5F, 0xF5, 0xAF, 0xFA,
            0xFD, 0x07, 0x66, 0, 0,
            0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
            0xD8, 0, 0, 0, 0x17, 0, 0
        ]);
        transport.Enqueue([0x02]);
        transport.Enqueue([0x1D]);
        transport.Enqueue([0xFE]);
        transport.Enqueue([0xE1]);
        transport.Enqueue([0, 0, 0, 4]);
        transport.Enqueue([1, 2, 3, 4]);
        transport.Enqueue([0x0D, 0x1D]);
        transport.Enqueue([0xFE]);
        transport.Enqueue([0xE7]);
        transport.Enqueue([0, 0, 0, 4]);
        transport.Enqueue([0xA0, 0, 0, 0]);
        transport.Enqueue([0, 0]);
        using var session = new MtkSession(transport);

        var info = await session.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
        Assert.Equal((byte)2, info.BlVersion);
        Assert.Null(info.Meid);
        Assert.Equal(new byte[] { 0xA0, 0, 0, 0 }, info.Socid);
    }

    [Fact]
    public async Task GetBootLoaderVersionsAsync_KnownAnswers_ReturnBytesAndStoreIdentity()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSilentIdentitySessionAsync(transport);
        transport.Enqueue([0x02]);
        transport.Enqueue([0x1D]);

        Assert.Equal(2, await session.GetBootLoaderVersionAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0x1D, await session.GetBromVersionAsync(TestContext.Current.CancellationToken));

        Assert.Equal((byte)2, session.DeviceInfo!.BlVersion);
        Assert.Equal((byte)0x1D, session.DeviceInfo.BromVersion);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task GetMeidAsync_ProbeBelowUnsupportedThreshold_ReturnsNullWithoutIdentifierCommand()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSilentIdentitySessionAsync(transport);
        transport.Enqueue([0x01]);
        var before = transport.Writes.Count;

        var meid = await session.GetMeidAsync(TestContext.Current.CancellationToken);

        Assert.Null(meid);
        Assert.Equal(new byte[] { 0xFE }, transport.Writes[before]);
        Assert.Equal(before + 1, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task GetMeidAsync_RejectedStatus_FaultsSession()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSilentIdentitySessionAsync(transport);
        transport.Enqueue([0xFE]);
        transport.Enqueue([0xE1]);
        transport.Enqueue([0, 0, 0, 4]);
        transport.Enqueue([1, 2, 3, 4]);
        transport.Enqueue([0x0D, 0x1D]);

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.GetMeidAsync(TestContext.Current.CancellationToken));

        Assert.Equal((ushort)0x1D0D, error.Status);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.DeviceInfo!.Meid);
    }

    [Fact]
    public async Task GetMeidAsync_OversizedDeclaredLength_FaultsBeforeBuffering()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSilentIdentitySessionAsync(transport);
        transport.Enqueue([0xFE]);
        transport.Enqueue([0xE1]);
        transport.Enqueue([0, 0, 1, 1]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.GetMeidAsync(TestContext.Current.CancellationToken));

        Assert.Equal(before + 2, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task GetSocidAsync_AcceptedAnswer_ReturnsBytesAndStoresIdentity()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateSilentIdentitySessionAsync(transport);
        transport.Enqueue([0xFE]);
        transport.Enqueue([0xE7]);
        transport.Enqueue([0, 0, 0, 32]);
        transport.Enqueue(DocumentedSocid);
        transport.Enqueue([0, 0]);

        var socid = await session.GetSocidAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DocumentedSocid, socid);
        Assert.Equal(DocumentedSocid, session.DeviceInfo!.Socid);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    private static async Task<MtkSession> CreateSilentIdentitySessionAsync(ScriptedCommTransport transport)
    {
        transport.Enqueue([
            0x5F, 0xF5, 0xAF, 0xFA,
            0xFD, 0x07, 0x66, 0, 0,
            0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
            0xD8, 0, 0, 0, 0x17, 0, 0
        ]);
        var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        return session;
    }
}