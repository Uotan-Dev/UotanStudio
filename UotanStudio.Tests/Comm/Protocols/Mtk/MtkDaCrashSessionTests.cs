using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task CrashDaAsync_NotConfirmed_SendsNothingAndKeepsBootloaderStage()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            session.CrashDaAsync(MtkDaCrashMode.Dasend1, false, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task CrashDaAsync_UndefinedMode_IsRejectedWithoutBytes()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.CrashDaAsync((MtkDaCrashMode)9, true, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task CrashDaAsync_Dasend1_SendsMismatchedBigEndianHeaderAndZeroPayload()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xD7]);
        transport.Enqueue([0x00, 0x00, 0x00, 0x00]);
        transport.Enqueue([0x00, 0x00, 0x01, 0x00]);
        transport.Enqueue([0x00, 0x00, 0x01, 0x00]);
        transport.Enqueue([0x00, 0x00]);
        transport.Enqueue([0x00, 0x00, 0x00, 0x00]);
        var before = transport.Writes.Count;

        var result = await session.CrashDaAsync(MtkDaCrashMode.Dasend1, true,
            TestContext.Current.CancellationToken);

        Assert.Equal([0xD7], transport.Writes[before]);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], transport.Writes[before + 1]);
        Assert.Equal([0x00, 0x00, 0x01, 0x00], transport.Writes[before + 2]);
        Assert.Equal([0x00, 0x00, 0x01, 0x00], transport.Writes[before + 3]);
        Assert.Equal(Enumerable.Repeat((byte)0, 0x100), transport.Writes[before + 4]);
        Assert.Equal(5, transport.Writes.Count - before);
        Assert.Equal(MtkDaCrashMode.Dasend1, result.Mode);
        Assert.True(result.TrailingExchangeCompleted);
        Assert.Equal((ushort)0, result.LastStatusCode);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Contains(session.Evidence, entry => entry.Phase == "da-crash");
    }

    [Fact]
    public async Task CrashDaAsync_Daread_SendsReadEchoTripleAndDrainsBoundedWindow()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xD1]);
        transport.Enqueue([0x00, 0x00, 0x00, 0x00]);
        transport.Enqueue([0x00, 0x00, 0x01, 0x00]);
        transport.Enqueue([0x00, 0x00]);
        var drain = new byte[0x400];
        for (var index = 0; index < drain.Length; index++)
        {
            drain[index] = (byte)index;
        }
        transport.Enqueue(drain);
        transport.Enqueue([0x00, 0x00]);
        var before = transport.Writes.Count;

        var result = await session.CrashDaAsync(MtkDaCrashMode.Daread, true,
            TestContext.Current.CancellationToken);

        Assert.Equal([0xD1], transport.Writes[before]);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], transport.Writes[before + 1]);
        Assert.Equal([0x00, 0x00, 0x01, 0x00], transport.Writes[before + 2]);
        Assert.Equal(3, transport.Writes.Count - before);
        Assert.Equal(MtkDaCrashMode.Daread, result.Mode);
        Assert.True(result.TrailingExchangeCompleted);
        Assert.Equal((ushort)0, result.LastStatusCode);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task CrashDaAsync_Daread_TargetGoesSilent_ReportsWithoutThrowing()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xD1]);
        transport.Enqueue([0x00, 0x00, 0x00, 0x00]);
        transport.Enqueue([0x00, 0x00, 0x01, 0x00]);
        transport.Enqueue([0x00, 0x00]);

        var result = await session.CrashDaAsync(MtkDaCrashMode.Daread, true,
            TestContext.Current.CancellationToken);

        Assert.False(result.TrailingExchangeCompleted);
        Assert.Null(result.LastStatusCode);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task CrashDaAsync_Dasend2_UploadsDocumentedArmSequenceThenJumpsToZero()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xD7]);
        transport.Enqueue([0x00, 0x00, 0x00, 0x00]);
        transport.Enqueue([0x00, 0x00, 0x01, 0x18]);
        transport.Enqueue([0x00, 0x00, 0x00, 0x00]);
        transport.Enqueue([0x00, 0x00]);
        transport.Enqueue([0xFA, 0x40, 0x00, 0x00]);
        transport.Enqueue([0xD5]);
        transport.Enqueue([0x00, 0x00, 0x00, 0x00]);
        transport.Enqueue([0x00, 0x00, 0x00, 0x00]);
        transport.Enqueue([0x00, 0x00]);
        var before = transport.Writes.Count;

        var result = await session.CrashDaAsync(MtkDaCrashMode.Dasend2, true,
            TestContext.Current.CancellationToken);

        Assert.Equal([0xD7], transport.Writes[before]);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], transport.Writes[before + 1]);
        Assert.Equal([0x00, 0x00, 0x01, 0x18], transport.Writes[before + 2]);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], transport.Writes[before + 3]);
        Assert.Equal(Convert.FromHexString("00019FE510FF2FE1")
            .Concat(Enumerable.Repeat((byte)0, 0x110)), transport.Writes[before + 4]);
        Assert.Equal([0xD5], transport.Writes[before + 5]);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], transport.Writes[before + 6]);
        Assert.Equal(7, transport.Writes.Count - before);
        Assert.Equal(MtkDaCrashMode.Dasend2, result.Mode);
        Assert.True(result.TrailingExchangeCompleted);
        Assert.Equal((ushort)0, result.LastStatusCode);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task CrashDaAsync_Dasend2_SilentAfterUpload_ReportsBeforeJump()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xD7]);
        transport.Enqueue([0x00, 0x00, 0x00, 0x00]);
        transport.Enqueue([0x00, 0x00, 0x01, 0x18]);
        transport.Enqueue([0x00, 0x00, 0x00, 0x00]);
        transport.Enqueue([0x00, 0x00]);
        var before = transport.Writes.Count;

        var result = await session.CrashDaAsync(MtkDaCrashMode.Dasend2, true,
            TestContext.Current.CancellationToken);

        Assert.Equal(5, transport.Writes.Count - before);
        Assert.False(result.TrailingExchangeCompleted);
        Assert.Contains("before the jump", result.Detail, StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task CrashDaAsync_EchoMismatch_FaultsAndThrows()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0x00]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.CrashDaAsync(MtkDaCrashMode.Dasend1, true, TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task CrashDaAsync_RejectedFirstStatus_FaultsAndSendsNoPayload()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xD1]);
        transport.Enqueue([0x00, 0x00, 0x00, 0x00]);
        transport.Enqueue([0x00, 0x00, 0x01, 0x00]);
        transport.Enqueue([0x1D, 0x0D]);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.CrashDaAsync(MtkDaCrashMode.Daread, true, TestContext.Current.CancellationToken));

        Assert.Equal(0x1D0Du, error.Status);
        Assert.Equal(3, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }
}