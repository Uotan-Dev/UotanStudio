using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Security;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task GetXFlashRamInfoAsync_DoubleWordReply_DecodesSixLittleEndianFields()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueControlReply(transport, Convert.FromHexString("010000000020000000C00000020000000000100000008000"));
        var before = transport.Writes.Count;

        var info = await session.GetXFlashRamInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1u, info.Sram.Type);
        Assert.Equal(0x2000UL, info.Sram.BaseAddress);
        Assert.Equal(0xC000UL, info.Sram.Size);
        Assert.Equal(2u, info.Dram.Type);
        Assert.Equal(0x100000UL, info.Dram.BaseAddress);
        Assert.Equal(0x800000UL, info.Dram.Size);
        Assert.Equal("010000000020000000C00000020000000000100000008000", info.ReplyHex);
        Assert.Equal(Convert.FromHexString("09000100"), transport.Writes[before + 1]);
        Assert.Equal(Convert.FromHexString("0C000400"), transport.Writes[before + 3]);
    }

    [Fact]
    public async Task GetXFlashRamInfoAsync_QuadWordReply_DecodesSixFields()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        var reply = new byte[48];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(reply, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(8), 0x1_0000_0000UL);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(16), 0x2000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(24), 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(32), 0x40000000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(reply.AsSpan(40), 0x4000000);
        EnqueueControlReply(transport, reply);

        var info = await session.GetXFlashRamInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1UL, info.Sram.Type);
        Assert.Equal(0x1_0000_0000UL, info.Sram.BaseAddress);
        Assert.Equal(0x2000UL, info.Sram.Size);
        Assert.Equal(8UL, info.Dram.Type);
        Assert.Equal(0x40000000UL, info.Dram.BaseAddress);
        Assert.Equal(0x4000000UL, info.Dram.Size);
    }

    [Fact]
    public async Task GetXFlashRamInfoAsync_OversizedDeclaredPayload_FaultsBeforeBuffering()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[64]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.GetXFlashRamInfoAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task GetXFlashRamInfoAsync_UnknownWidth_FaultsXFlash()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueControlReply(transport, new byte[12]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.GetXFlashRamInfoAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task GetXFlashRandomIdAsync_OpaqueReply_ReturnedUnchanged()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        var reply = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        EnqueueControlReply(transport, reply);

        var randomId = await session.GetXFlashRandomIdAsync(TestContext.Current.CancellationToken);

        Assert.Equal(reply, randomId);
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
    }

    [Fact]
    public async Task GetXFlashRandomIdAsync_ReplyAboveBound_FaultsBeforeBuffering()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[4]);
        EnqueueXFlashFrame(transport, new byte[0x101]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.GetXFlashRandomIdAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task GetXFlashStorageLifeCycleCheckAsync_FourByteReply_DecodesLittleEndian()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueControlReply(transport, Convert.FromHexString("08000000"));

        var life = await session.GetXFlashStorageLifeCycleCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(8u, life);
    }

    [Theory]
    [InlineData("0800000000")]
    [InlineData("0800")]
    public async Task GetXFlashStorageLifeCycleCheckAsync_WrongWidth_FaultsXFlash(string replyHex)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueControlReply(transport, Convert.FromHexString(replyHex));

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.GetXFlashStorageLifeCycleCheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task GetXFlashExpireDateAsync_OpaqueReply_ReturnedUnchanged()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        var reply = "2030-12-31"u8.ToArray();
        EnqueueControlReply(transport, reply);

        var expireDate = await session.GetXFlashExpireDateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(reply, expireDate);
    }

    [Theory]
    [InlineData("64000000", MtkPartitionTableCategory.Gpt)]
    [InlineData("65000000", MtkPartitionTableCategory.Pmt)]
    public async Task GetXFlashPartitionTableCategoryAsync_DocumentedValues_Decode(string replyHex,
        MtkPartitionTableCategory expected)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueControlReply(transport, Convert.FromHexString(replyHex));

        var category = await session.GetXFlashPartitionTableCategoryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, category);
    }

    [Fact]
    public async Task GetXFlashPartitionTableCategoryAsync_UnknownValue_FaultsXFlash()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueControlReply(transport, Convert.FromHexString("66000000"));

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.GetXFlashPartitionTableCategoryAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }
}