using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    private const int DaMemoryBulkCeiling = MtkDaMemoryAccess.MaximumBulkReadBytes;
    private const int DaMemoryWordCeiling = MtkDaMemoryAccess.MaximumWordLoopReadBytes;

    [Fact]
    public async Task PeekXFlashMemoryAsync_FullWindow_SendsQuadAddressAndLengthAndKeepsReplyBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var payload = DaMemoryPattern(0x40);
        var before = transport.Writes.Count;
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashFrame(transport, payload);
        EnqueueXFlashValue(transport, 0);

        var data = await session.PeekXFlashMemoryAsync(0x1234_5678, 0x40,
            TestContext.Current.CancellationToken);

        Assert.Equal(payload, data);
        Assert.Equal("00010009+000F0001+00000040", SecurityWordText(transport, before));
        var frames = DaMemoryFrames(transport, before);
        Assert.Equal(0x12345678UL, BinaryPrimitives.ReadUInt64LittleEndian(frames[2]));
        Assert.Equal(8, frames[2].Length);
        Assert.Equal(4, frames.Count);
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("oversized")]
    public async Task PeekXFlashMemoryAsync_ReplyLengthMismatch_FaultsWithoutRetry(string shape)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashFrame(transport, new byte[shape == "short" ? 0x3F : 0x41]);
        EnqueueXFlashValue(transport, 0);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.PeekXFlashMemoryAsync(0x2000, 0x40,
            TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        var refused = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PeekXFlashMemoryAsync(0x2000, 0x40,
            TestContext.Current.CancellationToken));
        Assert.Equal(refused, transport.Writes.Count);
    }

    [Fact]
    public async Task PeekXFlashMemoryAsync_LengthBeyondCeiling_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.PeekXFlashMemoryAsync(
            0x2000, DaMemoryBulkCeiling + 1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.PeekXFlashMemoryAsync(
            0x2000, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.PeekXFlashMemoryAsync(
            0xFFFF_FFF0, 32, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
    }

    [Fact]
    public async Task PeekXFlashRegisterAsync_UnalignedLength_ReadsOneExchangePerWordAndTrimsTail()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        for (var word = 0; word < 2; word++)
        {
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashFrame(transport, [(byte)(0x10 + word), 0x22, 0x33, 0x44]);
            EnqueueXFlashValue(transport, 0);
        }

        var data = await session.PeekXFlashRegisterAsync(0x1000_0000, 6, TestContext.Current.CancellationToken);

        Assert.Equal([0x10, 0x22, 0x33, 0x44, 0x11, 0x22], data);
        Assert.Equal("00010009+000F0002+10000000+00010009+000F0002+10000004", SecurityWordText(transport, before));
        Assert.Equal(12, transport.Writes.Count - before);
    }

    [Fact]
    public async Task PeekXFlashRegisterAsync_StageOneOnly_IsRefusedBecauseCustomCodesNeedDaTwo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PeekXFlashRegisterAsync(
            0x1000_0000, 4, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
    }

    [Fact]
    public async Task PokeXFlashMemoryAsync_WriteNotConfirmed_SendsNoBytesAndKeepsStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.PokeXFlashMemoryAsync(
            0x1000_0000, new byte[] { 1, 2, 3, 4, 5, 6 }, confirmWrite: false,
            TestContext.Current.CancellationToken));

        Assert.Contains("requires an explicit confirmation", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
    }

    [Fact]
    public async Task PokeXFlashMemoryAsync_Confirmed_SendsQuadAddressLengthPayloadAndOneStatus()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        var before = transport.Writes.Count;

        var written = await session.PokeXFlashMemoryAsync(0x4000_0000,
            new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }, confirmWrite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(6, written);
        Assert.Equal("00010009+000F0003+00000006", SecurityWordText(transport, before));
        var frames = DaMemoryFrames(transport, before);
        Assert.Equal(0x40000000UL, BinaryPrimitives.ReadUInt64LittleEndian(frames[2]));
        Assert.Equal([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF], frames[4]);
        Assert.Equal(5, frames.Count);
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
    }

    [Fact]
    public async Task PokeXFlashMemoryAsync_RejectedCompletion_FaultsTheFamily()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        var failure = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(failure, 0xC000_0001);
        EnqueueXFlashFrame(transport, failure);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.PokeXFlashMemoryAsync(0x4000_0000,
            new byte[] { 1, 2, 3, 4, 5, 6 }, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task DumpXFlashBromAsync_OneStep_IssuesSixteenRegisterReadsAndWritesLittleEndianBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        for (var word = 0; word < MtkDaMemoryAccess.DumpStepWords; word++)
        {
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashFrame(transport, [(byte)word, 0x00, 0x00, 0x00]);
            EnqueueXFlashValue(transport, 0);
        }

        using var destination = new MemoryStream();
        var progress = new List<long>();
        var before = transport.Writes.Count;

        var captured = await session.DumpXFlashBromAsync(destination,
            new MtkDaMemoryRange(0x0000_0000, MtkDaMemoryAccess.DumpStepBytes), new ListProgress(progress),
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkDaMemoryAccess.DumpStepBytes, captured);
        Assert.Equal(MtkDaMemoryAccess.DumpStepBytes, destination.Length);
        Assert.Equal(MtkDaMemoryAccess.DumpStepBytes, progress[^1]);
        Assert.Equal(Enumerable.Range(0, MtkDaMemoryAccess.DumpStepWords)
            .SelectMany(word => new byte[] { (byte)word, 0, 0, 0 }), destination.ToArray());
        Assert.Equal(6 * MtkDaMemoryAccess.DumpStepWords, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
    }

    [Fact]
    public async Task DumpXFlashBromAsync_CancelledMidStep_StopsAfterTheDeliveredBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        for (var word = 0; word < MtkDaMemoryAccess.DumpStepWords; word++)
        {
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashFrame(transport, [(byte)word, 0x00, 0x00, 0x00]);
            EnqueueXFlashValue(transport, 0);
        }

        using var cancellation = new CancellationTokenSource();
        using var destination = new CancellingDumpStream(8, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.DumpXFlashBromAsync(destination,
            new MtkDaMemoryRange(0x0000_0000, MtkDaMemoryAccess.DumpStepBytes * 2), cancellationToken:
            cancellation.Token));

        Assert.Equal(8, destination.Length);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(0, "empty")]
    [InlineData(0x41, "unaligned")]
    [InlineData(0x200040, "oversized")]
    public async Task DumpXFlashBromAsync_InvalidRange_RejectsBeforeIo(int length, string shape)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        using var destination = new MemoryStream();
        var range = new MtkDaMemoryRange(0x0000_0000, length);

        if (shape == "unaligned")
        {
            await Assert.ThrowsAsync<ArgumentException>(() => session.DumpXFlashBromAsync(destination, range,
                cancellationToken: TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.DumpXFlashBromAsync(destination,
                range, cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(0, destination.Length);
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
    }

    [Fact]
    public void BromDumpRange_DefaultsToTheDocumentedBootRomWindow()
    {
        Assert.Equal(0x0000_0000u, MtkDaMemoryRange.BromDump.StartAddress);
        Assert.Equal(0x200000, MtkDaMemoryRange.BromDump.LengthBytes);
        Assert.Equal(0x200000L, MtkDaMemoryRange.BromDump.EndAddress);
        Assert.Equal(0x40, MtkDaMemoryAccess.DumpStepBytes);
        Assert.Equal(16, MtkDaMemoryAccess.DumpStepWords);
        Assert.Equal("brom_8125.bin", MtkDaMemoryAccess.GetBromDumpFileName(0x8125));
        Assert.Equal("brom_335.bin", MtkDaMemoryAccess.GetBromDumpFileName(0x0335));
    }

    [Fact]
    public async Task PeekLegacyMemoryAsync_UnalignedLength_ReversesEachBigEndianWordAndTrimsTail()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        transport.Enqueue([0x11, 0x22, 0x33, 0x44, 0x5A]);
        transport.Enqueue([0x55, 0x66, 0x77, 0x88, 0x5A]);
        var before = transport.Writes.Count;

        var data = await session.PeekLegacyMemoryAsync(0x0010_0008, 6, TestContext.Current.CancellationToken);

        Assert.Equal([0x44, 0x33, 0x22, 0x11, 0x88, 0x77], data);
        Assert.Equal([0x7A, 0x00, 0x10, 0x00, 0x08], transport.Writes[before]);
        Assert.Equal([0x7A, 0x00, 0x10, 0x00, 0x0C], transport.Writes[before + 1]);
        Assert.Equal(2, transport.Writes.Count - before);
    }

    [Fact]
    public async Task PeekLegacyRegisterAsync_SameWindow_UsesTheIdenticalRegisterFramingAsMemoryPeek()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        transport.Enqueue([0x11, 0x22, 0x33, 0x44, 0x5A]);
        var before = transport.Writes.Count;

        var register = await session.PeekLegacyRegisterAsync(0x0010_0008, 4,
            TestContext.Current.CancellationToken);

        transport.Enqueue([0x11, 0x22, 0x33, 0x44, 0x5A]);
        var memory = await session.PeekLegacyMemoryAsync(0x0010_0008, 4, TestContext.Current.CancellationToken);

        Assert.Equal([0x44, 0x33, 0x22, 0x11], register);
        Assert.Equal(register, memory);
        Assert.Equal(2, transport.Writes.Count - before);
        Assert.All(transport.Writes.Skip(before), bytes => Assert.Equal(0x7A, (int)bytes[0]));
    }

    [Fact]
    public async Task PeekLegacyMemoryAsync_MissingAcknowledge_FaultsTheLegacyFamily()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        transport.Enqueue([0x11, 0x22, 0x33, 0x44, 0xA5]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.PeekLegacyMemoryAsync(0x0010_0008, 4,
            TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        var refused = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PeekLegacyMemoryAsync(0x0010_0008, 4,
            TestContext.Current.CancellationToken));
        Assert.Equal(refused, transport.Writes.Count);
    }

    [Fact]
    public async Task PokeLegacyMemoryAsync_WriteNotConfirmed_SendsNoBytesAndKeepsStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.PokeLegacyMemoryAsync(0x0010_0000,
            new byte[] { 1, 2, 3, 4, 5, 6 }, confirmWrite: false, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task PokeLegacyMemoryAsync_Confirmed_WritesReversedWordsAndPadsTheTailWord()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        transport.Enqueue([0x5A]);
        transport.Enqueue([0x5A]);
        var before = transport.Writes.Count;

        var written = await session.PokeLegacyMemoryAsync(0x0010_0000, new byte[] { 1, 2, 3, 4, 5, 6 },
            confirmWrite: true, TestContext.Current.CancellationToken);

        Assert.Equal(6, written);
        Assert.Equal([0x7B, 0x00, 0x10, 0x00, 0x00, 0x04, 0x03, 0x02, 0x01], transport.Writes[before]);
        Assert.Equal([0x7B, 0x00, 0x10, 0x00, 0x04, 0x00, 0x00, 0x06, 0x05], transport.Writes[before + 1]);
        Assert.Equal(2, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task DumpLegacyBromAsync_OneStep_ReadsSixteenRegistersAndEmitsLittleEndianWords()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        for (var word = 0; word < MtkDaMemoryAccess.DumpStepWords; word++)
        {
            transport.Enqueue([(byte)(0x40 + word), 0x00, 0x00, 0x00, 0x5A]);
        }

        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        var captured = await session.DumpLegacyBromAsync(destination,
            new MtkDaMemoryRange(0x0000_0000, MtkDaMemoryAccess.DumpStepBytes),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MtkDaMemoryAccess.DumpStepBytes, captured);
        Assert.Equal(MtkDaMemoryAccess.DumpStepBytes, destination.Length);
        Assert.Equal(Enumerable.Range(0, MtkDaMemoryAccess.DumpStepWords)
            .SelectMany(word => new byte[] { 0, 0, 0, (byte)(0x40 + word) }), destination.ToArray());
        Assert.Equal(MtkDaMemoryAccess.DumpStepWords, transport.Writes.Count - before);
        Assert.All(transport.Writes.Skip(before), bytes => Assert.Equal(0x7A, (int)bytes[0]));
    }

    [Fact]
    public async Task PeekXmlMemoryAsync_FullWindow_SendsCommandAddressAndLengthAndClosesWithEndAndStart()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var payload = DaMemoryPattern(0x40);
        EnqueueXmlText(transport, "OK");
        EnqueueXFlashFrame(transport, payload);
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
        var before = transport.Writes.Count;

        var data = await session.PeekXmlMemoryAsync(0x4000_0000, 0x40, TestContext.Current.CancellationToken);

        Assert.Equal(payload, data);
        var frames = DaMemoryFrames(transport, before);
        Assert.Contains("CMD:CUSTOMMEMR", System.Text.Encoding.UTF8.GetString(frames[0]), StringComparison.Ordinal);
        Assert.Equal(0x40000000UL, BinaryPrimitives.ReadUInt64LittleEndian(frames[1]));
        Assert.Equal(0x40u, BinaryPrimitives.ReadUInt32LittleEndian(frames[2]));
        Assert.Equal("OK\0"u8.ToArray(), frames[3]);
        Assert.Equal("OK\0"u8.ToArray(), frames[4]);
        Assert.Equal(5, frames.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task PeekXmlMemoryAsync_ShortReply_FaultsWithoutReinterpreting()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXFlashFrame(transport, new byte[0x3F]);
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.PeekXmlMemoryAsync(0x4000_0000, 0x40,
            TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        var refused = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PeekXmlMemoryAsync(0x4000_0000, 0x40,
            TestContext.Current.CancellationToken));
        Assert.Equal(refused, transport.Writes.Count);
    }

    [Fact]
    public async Task PeekXmlRegisterAsync_TwoWords_IssuesOneCommandExchangePerDoubleword()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        for (var word = 0; word < 2; word++)
        {
            EnqueueXmlText(transport, "OK");
            EnqueueXFlashFrame(transport, [(byte)(0x10 + word), 0x22, 0x33, 0x44]);
            EnqueueXmlText(transport, XmlControl("END", "OK"));
            EnqueueXmlText(transport, XmlControl("START"));
        }

        var before = transport.Writes.Count;

        var data = await session.PeekXmlRegisterAsync(0x0010_0000, 8, TestContext.Current.CancellationToken);

        Assert.Equal([0x10, 0x22, 0x33, 0x44, 0x11, 0x22, 0x33, 0x44], data);
        var frames = DaMemoryFrames(transport, before);
        Assert.Contains("CMD:CUSTOMREGR", System.Text.Encoding.UTF8.GetString(frames[0]), StringComparison.Ordinal);
        Assert.Equal(0x0010_0000u, BinaryPrimitives.ReadUInt32LittleEndian(frames[1]));
        Assert.Equal(0x0010_0004u, BinaryPrimitives.ReadUInt32LittleEndian(frames[5]));
        Assert.Equal(8, frames.Count);
    }

    [Fact]
    public async Task PokeXmlMemoryAsync_WriteNotConfirmed_SendsNoBytesAndKeepsStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.PokeXmlMemoryAsync(0x4000_0000,
            new byte[] { 1, 2, 3, 4 }, confirmWrite: false, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task PokeXmlMemoryAsync_Confirmed_ReusesTheReferenceReadTokenForWriteFrames()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
        var before = transport.Writes.Count;

        var written = await session.PokeXmlMemoryAsync(0x4000_0000, new byte[] { 1, 2, 3, 4 },
            confirmWrite: true, TestContext.Current.CancellationToken);

        Assert.Equal(4, written);
        var frames = DaMemoryFrames(transport, before);
        Assert.Contains("CMD:CUSTOMMEMR", System.Text.Encoding.UTF8.GetString(frames[0]), StringComparison.Ordinal);
        Assert.Equal(0x40000000UL, BinaryPrimitives.ReadUInt64LittleEndian(frames[1]));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(frames[2]));
        Assert.Equal([1, 2, 3, 4], frames[3]);
        Assert.Equal("OK\0"u8.ToArray(), frames[4]);
        Assert.Equal("OK\0"u8.ToArray(), frames[5]);
        Assert.Equal(6, frames.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task DumpXmlBromAsync_OneStep_IssuesSixteenRegisterExchangesAndKeepsReplyBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        for (var word = 0; word < MtkDaMemoryAccess.DumpStepWords; word++)
        {
            EnqueueXmlText(transport, "OK");
            EnqueueXFlashFrame(transport, [(byte)word, 0x00, 0x00, 0x00]);
            EnqueueXmlText(transport, XmlControl("END", "OK"));
            EnqueueXmlText(transport, XmlControl("START"));
        }

        using var destination = new MemoryStream();
        var progress = new List<long>();
        var before = transport.Writes.Count;

        var captured = await session.DumpXmlBromAsync(destination,
            new MtkDaMemoryRange(0x0000_0000, MtkDaMemoryAccess.DumpStepBytes), new ListProgress(progress),
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkDaMemoryAccess.DumpStepBytes, captured);
        Assert.Equal(Enumerable.Range(0, MtkDaMemoryAccess.DumpStepWords)
            .SelectMany(word => new byte[] { (byte)word, 0, 0, 0 }), destination.ToArray());
        Assert.Equal(MtkDaMemoryAccess.DumpStepBytes, progress[^1]);
        Assert.Equal(8 * MtkDaMemoryAccess.DumpStepWords, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task PeekXmlMemoryAsync_DetachedControlStage_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStartedSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PeekXmlMemoryAsync(0x4000_0000, 4,
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.AgentStarted, session.State);
    }

    private static byte[] DaMemoryPattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }

    private static List<byte[]> DaMemoryFrames(ScriptedCommTransport transport, int before)
    {
        var wire = transport.Writes.Skip(before).SelectMany(value => value).ToArray();
        var frames = new List<byte[]>();
        for (var offset = 0; offset + 12 <= wire.Length;)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(offset + 8));
            frames.Add(wire.AsSpan(offset + 12, length).ToArray());
            offset += length + 12;
        }

        return frames;
    }

    private sealed class CancellingDumpStream(int bytesBeforeCancel, CancellationTokenSource cancellation)
        : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (Length >= bytesBeforeCancel)
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class ListProgress(List<long> values) : IProgress<long>
    {
        public void Report(long value) => values.Add(value);
    }
}