using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task SetXFlashMetaBootModeAsync_UsbMode_SendsFlagTripleAndConsumesAcknowledgement()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueXFlashStatusFrames(transport, 3);
        var before = transport.Writes.Count;

        await session.SetXFlashMetaBootModeAsync(MtkXFlashMetaBootMode.Usb, TestContext.Current.CancellationToken);

        Assert.Equal(Convert.FromHexString("09000100"), transport.Writes[before + 1]);
        Assert.Equal(Convert.FromHexString("06000200"), transport.Writes[before + 3]);
        Assert.Equal(Convert.FromHexString("EFEEEEFE0100000003000000"), transport.Writes[before + 4]);
        Assert.Equal(new byte[] { 1, 2, 0 }, transport.Writes[before + 5]);
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
    }

    [Fact]
    public async Task SetXFlashEmmcHwResetPinAsync_Disabled_SendsZeroWord()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueXFlashStatusFrames(transport, 3);
        var before = transport.Writes.Count;

        await session.SetXFlashEmmcHwResetPinAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(Convert.FromHexString("07000200"), transport.Writes[before + 3]);
        Assert.Equal(new byte[4], transport.Writes[before + 5]);
    }


    [Fact]
    public async Task SetXFlashMetaBootModeAsync_BeforeStageOne_RefusesWithoutDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetXFlashMetaBootModeAsync(
            MtkXFlashMetaBootMode.Uart, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task StartXFlashDownloadInfoAsync_ParameterlessMarker_UsesQueryExchangeAndReturnsReply()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueControlReply(transport, [0xAA]);
        var before = transport.Writes.Count;

        var reply = await session.StartXFlashDownloadInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0xAA }, reply);
        Assert.Equal(Convert.FromHexString("01000800"), transport.Writes[before + 3]);
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
    }

    [Fact]
    public async Task EndXFlashDownloadInfoAsync_ParameterlessMarker_ReturnsReply()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueControlReply(transport, [0x5A]);

        Assert.Equal(new byte[] { 0x5A },
            await session.EndXFlashDownloadInfoAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisableXFlashEmmcHwResetPinAsync_ParameterlessMarker_SendsDocumentedCode()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueControlReply(transport, []);
        var before = transport.Writes.Count;

        await session.DisableXFlashEmmcHwResetPinAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Convert.FromHexString("04000800"), transport.Writes[before + 3]);
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
    }

    private static void EnqueueXFlashStatusFrames(ScriptedCommTransport transport, int count)
    {
        for (var index = 0; index < count; index++)
        {
            EnqueueXFlashFrame(transport, new byte[4]);
        }
    }
}