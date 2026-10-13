using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    private const byte PreloaderRead16 = 0xD0;
    private const byte PreloaderRead32 = 0xD1;
    private const byte PreloaderWrite32 = 0xD4;

    [Fact]
    public async Task ReadPreloader32Async_KnownWindow_SendsBigEndianEchoOrderAndKeepsWireOrder()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        byte[] payload = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];
        EnqueuePreloaderRead(transport, PreloaderRead32, 0x4000_1000, 2, payload);
        var before = transport.Writes.Count;

        var values = await session.ReadPreloader32Async(0x4000_1000, 2,
            TestContext.Current.CancellationToken);

        Assert.Equal([0x1122_3344u, 0x5566_7788u], values);
        Assert.Equal(Convert.ToHexString(
        [
            PreloaderRead32, 0x40, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x02
        ]), Convert.ToHexString(transport.Writes.Skip(before).SelectMany(bytes => bytes).ToArray()));
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task ReadPreloader16Async_KnownWindow_SendsCommandD0AndDecodesBigEndianWords()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        EnqueuePreloaderRead(transport, PreloaderRead16, 0x8000_0008, 2, [0xCA, 0x01, 0x12, 0x34]);
        var before = transport.Writes.Count;

        var values = await session.ReadPreloader16Async(0x8000_0008, 2,
            TestContext.Current.CancellationToken);

        Assert.Equal([(ushort)0xCA01, (ushort)0x1234], values);
        Assert.Equal(PreloaderRead16, transport.Writes[before][0]);
        Assert.Equal(3, transport.Writes.Count - before);
    }

    [Fact]
    public async Task ReadPreloader32Async_RejectedStatus_FaultsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue([PreloaderRead32]);
        transport.Enqueue(BigEndianPreloader(0x4000_1000));
        transport.Enqueue(BigEndianPreloader(1));
        transport.Enqueue([0x01, 0x00]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadPreloader32Async(0x4000_1000, 1,
            TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        var refused = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadPreloader32Async(0x4000_1000, 1,
            TestContext.Current.CancellationToken));
        Assert.Equal(refused, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadPreloader32Async_EchoMismatch_FaultsTheSession()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue([0x00]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadPreloader32Async(0x4000_1000, 1,
            TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }


    [Fact]
    public async Task RunPreloaderExtendedCommandAsync_NotConfirmed_SendsZeroBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.RunPreloaderExtendedCommandAsync(
            confirm: false, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task RunPreloaderExtendedCommandAsync_Confirmed_EchoesOpcodeAndOperandThenDrainsReply()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue([0xC8]);
        transport.Enqueue([0xB1]);
        transport.Enqueue([0x00]);
        transport.Enqueue([0x00, 0x00]);

        await session.RunPreloaderExtendedCommandAsync(confirm: true, cancellationToken:
            TestContext.Current.CancellationToken);

        Assert.Equal([0xC8, 0xB1], transport.Writes.TakeLast(2).SelectMany(bytes => bytes).ToArray());
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task JumpPreloaderToBootLoaderAsync_Confirmed_RequiresTwoStatusWords()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue([0xD6]);
        transport.Enqueue([0x00, 0x00]);
        transport.Enqueue([0x00, 0x00]);

        await session.JumpPreloaderToBootLoaderAsync(confirm: true, TestContext.Current.CancellationToken);

        Assert.Equal([0xD6], transport.Writes[^1]);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task JumpPreloaderToPartitionAsync_NameLongerThanField_TruncatesToExactlySixtyFourBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue([0x71]);
        transport.Enqueue([0x00, 0x00]);
        var before = transport.Writes.Count;

        await session.JumpPreloaderToPartitionAsync(new string('a', 100), confirm: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(0x71, transport.Writes[before][0]);
        var field = transport.Writes[before + 1];
        Assert.Equal(64, field.Length);
        Assert.All(field, value => Assert.Equal((byte)'a', value));
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task JumpPreloaderToPartitionAsync_ShortName_PadsFieldWithNulls()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue([0x71]);
        transport.Enqueue([0x00, 0x00]);

        await session.JumpPreloaderToPartitionAsync("boot", confirm: true,
            TestContext.Current.CancellationToken);

        var field = transport.Writes[^1];
        Assert.Equal(64, field.Length);
        Assert.Equal("boot"u8.ToArray(), field[..4]);
        Assert.All(field[4..], value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task ReturnPreloaderToBootRomAsync_NotConfirmed_SendsZeroBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.ReturnPreloaderToBootRomAsync(
            enable: true, timeoutMs: 0, confirm: false, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task ReturnPreloaderToBootRomAsync_Confirmed_ComposesUsbdlMagicAndLockSequence()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        EnqueuePreloaderWrite32(transport, 0x1001_A100, [0xAD98]);
        EnqueuePreloaderWrite32(transport, 0x1001_A108, [1]);
        EnqueuePreloaderWrite32(transport, 0x1001_A100, [0]);
        EnqueuePreloaderWrite32(transport, 0x1001_A0E0, [0x444C_FFFD]);
        var before = transport.Writes.Count;

        await session.ReturnPreloaderToBootRomAsync(enable: true, timeoutMs: 0, confirm: true,
            TestContext.Current.CancellationToken);

        var wire = transport.Writes.Skip(before).SelectMany(bytes => bytes).ToArray();
        Assert.Contains("1001A10000000001" + "0000AD98", Convert.ToHexString(wire), StringComparison.Ordinal);
        Assert.Contains("1001A10800000001" + "00000001", Convert.ToHexString(wire), StringComparison.Ordinal);
        Assert.Contains("1001A10000000001" + "00000000", Convert.ToHexString(wire), StringComparison.Ordinal);
        Assert.Contains("1001A0E000000001" + "444CFFFD", Convert.ToHexString(wire), StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task ReturnPreloaderToBootRomAsync_UnknownHardwareCode_RefusesWithoutIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport, 0xF00D);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => session.ReturnPreloaderToBootRomAsync(
            enable: true, timeoutMs: 0, confirm: true, TestContext.Current.CancellationToken));

        Assert.Contains("0xF00D", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
    }

    [Fact]
    public async Task DisablePreloaderWatchdogAsync_UnknownHardwareCode_RefusesWithoutIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport, 0xF00D);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => session.DisablePreloaderWatchdogAsync(
            confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Contains("0xF00D", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task DisablePreloaderWatchdogAsync_ConfirmedKnownChip_WritesRecordedDefaultWord()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        EnqueuePreloaderWrite32(transport, 0x1000_7000, [0x2200_0064]);
        var before = transport.Writes.Count;

        await session.DisablePreloaderWatchdogAsync(confirmWrite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(Convert.ToHexString(
        [
            PreloaderWrite32, 0x10, 0x00, 0x70, 0x00, 0x00, 0x00, 0x00, 0x01, 0x22, 0x00, 0x00, 0x64
        ]), Convert.ToHexString(transport.Writes.Skip(before).SelectMany(bytes => bytes).ToArray()));
    }

    [Fact]
    public async Task PeekPreloaderMemoryAsync_UnalignedLength_ReversesWordsAndTrimsTail()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        EnqueuePreloaderRead(transport, PreloaderRead32, 0x0000_1000, 2,
            [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88]);
        var before = transport.Writes.Count;

        var data = await session.PeekPreloaderMemoryAsync(0x0000_1000, 5,
            TestContext.Current.CancellationToken);

        Assert.Equal([0x44, 0x33, 0x22, 0x11, 0x88], data);
        Assert.Equal(3, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task PeekPreloaderMemoryAsync_AgentUploadAlreadyRequested_RefusesBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("D7002000000000000800000000000000000000"));
        using var image = new MemoryStream(new byte[8]);
        await session.UploadDownloadAgentAsync(image, 0x200000, 8,
            cancellationToken: TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PeekPreloaderMemoryAsync(
            0x0000_1000, 4, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadPreloader32Async_PreCancelledToken_StopsBeforeFirstByte()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadPreloader32Async(
            0x4000_1000, 1, cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    private static async Task<MtkSession> CreatePreloaderSessionAsync(ScriptedCommTransport transport,
        ushort hardwareCode = 0x0766)
    {
        if (hardwareCode == 0x0766)
        {
            EnqueueIdentity(transport);
        }
        else
        {
            byte[] wire =
            [
                0x5F, 0xF5, 0xAF, 0xFA,
                0xFD, (byte)(hardwareCode >> 8), (byte)hardwareCode, 0, 0,
                0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
                0xD8, 0, 0, 0, 0x17, 0, 0
            ];
            transport.Enqueue(wire);
        }

        var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        return session;
    }

    private static byte[] BigEndianPreloader(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static void EnqueuePreloaderRead(ScriptedCommTransport transport, byte command, uint address,
        int words, byte[] payload)
    {
        transport.Enqueue([command]);
        transport.Enqueue(BigEndianPreloader(address));
        transport.Enqueue(BigEndianPreloader((uint)words));
        transport.Enqueue([0x00, 0x00]);
        transport.Enqueue(payload);
        transport.Enqueue([0x00, 0x00]);
    }

    private static void EnqueuePreloaderWrite32(ScriptedCommTransport transport, uint address, uint[] values)
    {
        transport.Enqueue([PreloaderWrite32]);
        transport.Enqueue(BigEndianPreloader(address));
        transport.Enqueue(BigEndianPreloader((uint)values.Length));
        transport.Enqueue([0x00, 0x00]);
        foreach (var value in values)
        {
            transport.Enqueue(BigEndianPreloader(value));
        }

        transport.Enqueue([0x00, 0x00]);
    }
}