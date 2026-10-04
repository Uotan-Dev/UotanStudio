using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task SetLegacyTransferSpeedAsync_DefaultBaud_NegotiatesLoopbackAndReportsUsbSpeed()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue([0x5A]);
        transport.Enqueue([0xC0]);
        transport.Enqueue([0x5A]);
        foreach (var value in Enumerable.Range(0, 256).Select(value => (byte)value))
        {
            transport.Enqueue([value]);
        }
        transport.Enqueue([0x5A, 0x01]);
        var before = transport.Writes.Count;

        var result = await session.SetLegacyTransferSpeedAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(921600, result.Baudrate);
        Assert.Equal(1, result.UsbSpeed);
        Assert.Equal([0xD2], transport.Writes[before]);
        Assert.Equal([0x00, 0x0E, 0x10, 0x00], transport.Writes[before + 1]);
        Assert.Equal([0xC0], transport.Writes[before + 2]);
        Assert.Equal([0x5A], transport.Writes[before + 3]);
        for (var value = 0; value < 256; value++)
        {
            Assert.Equal([(byte)value], transport.Writes[before + 4 + value]);
        }
        Assert.Equal([0x72], transport.Writes[before + 260]);
        Assert.Equal(261, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
        Assert.Contains(session.Evidence, entry => entry.Phase == "legacy-speed");
    }

    [Fact]
    public async Task SetLegacyTransferSpeedAsync_CustomBaud_SendsBigEndianWord()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue([0x5A]);
        transport.Enqueue([0x00]);
        transport.Enqueue([0xC0]);
        transport.Enqueue([0x5A]);
        foreach (var value in Enumerable.Range(0, 256).Select(value => (byte)value))
        {
            transport.Enqueue([value]);
        }
        transport.Enqueue([0x5A, 0x02]);
        var before = transport.Writes.Count;

        var result = await session.SetLegacyTransferSpeedAsync(115200,
            TestContext.Current.CancellationToken);

        Assert.Equal(115200, result.Baudrate);
        Assert.Equal(2, result.UsbSpeed);
        Assert.Equal([0xD2], transport.Writes[before]);
        Assert.Equal([0x00, 0x01, 0xC2, 0x00], transport.Writes[before + 1]);
        Assert.Equal([0xC0], transport.Writes[before + 2]);
        Assert.Equal([0xC0], transport.Writes[before + 3]);
        Assert.Equal([0x5A], transport.Writes[before + 4]);
        Assert.Equal(262, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task SetLegacyTransferSpeedAsync_InvalidBaud_SendsNothing()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetLegacyTransferSpeedAsync(0,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task SetLegacyTransferSpeedAsync_LoopbackMismatch_FaultsLegacy()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue([0x5A]);
        transport.Enqueue([0xC0]);
        transport.Enqueue([0x5A]);
        transport.Enqueue([0x00]);
        transport.Enqueue([0xFF]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.SetLegacyTransferSpeedAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(transport.Writes.Count - before < 261);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Fact]
    public async Task SetLegacyTransferSpeedAsync_AgentReportsUnknownSpeed_FaultsLegacy()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue([0x5A]);
        transport.Enqueue([0xC0]);
        transport.Enqueue([0x5A]);
        foreach (var value in Enumerable.Range(0, 256).Select(value => (byte)value))
        {
            transport.Enqueue([value]);
        }
        transport.Enqueue([0x5A, 0x07]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.SetLegacyTransferSpeedAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task SetXFlashUsbSpeedAsync_SwitchAccepted_ReportsAppliedSpeedToken()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, "high-speed"u8.ToArray());
        EnqueueXFlashFrame(transport, new byte[4]);
        var before = transport.Writes.Count;

        var speed = await session.SetXFlashUsbSpeedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MtkXFlashUsbSpeedKind.HighSpeed, speed.Kind);
        Assert.Equal("high-speed", speed.Text);
        Assert.Equal(Convert.FromHexString("0B000100"), transport.Writes[before + 1]);
        Assert.Equal(Convert.FromHexString("01208D0E"), transport.Writes[before + 3]);
        Assert.Equal(Convert.FromHexString("09000100"), transport.Writes[before + 5]);
        Assert.Equal(Convert.FromHexString("0B000400"), transport.Writes[before + 7]);
        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
        Assert.Contains(session.Evidence, entry => entry.Phase == "xflash-usb-speed");
    }

    [Theory]
    [InlineData("full-speed", MtkXFlashUsbSpeedKind.FullSpeed)]
    [InlineData("super-speed", MtkXFlashUsbSpeedKind.SuperSpeed)]
    public async Task SetXFlashUsbSpeedAsync_OtherDocumentedTokens_Decode(string token,
        MtkXFlashUsbSpeedKind expected)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, System.Text.Encoding.UTF8.GetBytes(token));
        EnqueueXFlashFrame(transport, new byte[4]);

        var speed = await session.SetXFlashUsbSpeedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, speed.Kind);
        Assert.Equal(token, speed.Text);
    }

    [Fact]
    public async Task SetXFlashUsbSpeedAsync_UnknownToken_FaultsXFlash()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, "warp-speed"u8.ToArray());
        EnqueueXFlashFrame(transport, new byte[4]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.SetXFlashUsbSpeedAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task SetXFlashUsbSpeedAsync_BeforeStageTwo_RefusedWithoutBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SetXFlashUsbSpeedAsync(TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
    }
}