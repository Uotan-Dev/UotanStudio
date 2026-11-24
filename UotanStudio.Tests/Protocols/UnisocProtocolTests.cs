using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Unisoc;
using UotanStudio.Hash.Crc16Xmodem;
using UotanStudio.Hash.Sums;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Wire-level tests of the UNISOC BSL sessions: frame encode/parse round trips, the stage detection lines, the
/// two-stage loader uploads and the storage primitives, all driven by a queued fake transport that speaks the
/// device side of the protocol.
/// <para>紫光展锐 BSL 会话的线上测试：帧编解码往返、阶段识别各线路、两级加载器上传与存储原语，全部由以设备侧
/// 视角应答的队列式假传输驱动。</para>
/// </summary>
public class UnisocProtocolTests
{
    [Fact]
    public void EncodeThenParse_RoundTripsCommandAndPayload()
    {
        var payload = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        var frame = UnisocFrames.EncodeCommand(UnisocCommand.StartData, payload, UnisocChecksumKind.Sum,
            transcode: true);
        var parser = new UnisocFrameParser();

        parser.Append(frame);
        Assert.Equal(UnisocParseStatus.Frame, parser.TryRead(UnisocChecksumKind.Sum, true, out var parsed));
        Assert.Equal((ushort)UnisocCommand.StartData, parsed.Type);
        Assert.Equal(payload, parsed.Payload);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void EncodeThenParse_OddLengthPayload_RoundTripsThroughTranscode(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < length; index++)
        {
            payload[index] = (byte)(0x30 + index);
        }

        var frame = UnisocFrames.EncodeCommand(UnisocCommand.StartData, payload, UnisocChecksumKind.Sum,
            transcode: true);
        var parser = new UnisocFrameParser();

        parser.Append(frame);
        Assert.Equal(UnisocParseStatus.Frame, parser.TryRead(UnisocChecksumKind.Sum, true, out var parsed));
        Assert.Equal(payload, parsed.Payload);
    }

    [Fact]
    public void Parser_AppendAtExactFrameLimit_DoesNotThrow()
    {
        var parser = new UnisocFrameParser();
        var block = new byte[4096];
        for (var sent = 0; sent < UnisocConstants.MaximumEncodedFrame; sent += block.Length)
        {
            parser.Append(block.AsSpan(0, Math.Min(block.Length, UnisocConstants.MaximumEncodedFrame - sent)));
        }
    }

    [Fact]
    public void Parser_AppendBeyondFrameLimit_Throws()
    {
        var parser = new UnisocFrameParser();
        var block = new byte[4096];
        for (var sent = 0; sent < UnisocConstants.MaximumEncodedFrame; sent += block.Length)
        {
            parser.Append(block.AsSpan(0, Math.Min(block.Length, UnisocConstants.MaximumEncodedFrame - sent)));
        }

        Assert.Throws<UnisocProtocolException>(() => parser.Append([0x7E]));
    }

    [Fact]
    public void Parser_ByteByByteDelivery_FindsTheSameFrame()
    {
        var frame = UnisocFrames.EncodeCommand(UnisocCommand.Connect, ReadOnlySpan<byte>.Empty,
            UnisocChecksumKind.Sum, transcode: true);
        var parser = new UnisocFrameParser();
        UnisocParseStatus status = UnisocParseStatus.Incomplete;
        UnisocFrame parsed = default;
        foreach (var value in frame)
        {
            if (status == UnisocParseStatus.Frame)
            {
                break;
            }

            parser.Append([value]);
            status = parser.TryRead(UnisocChecksumKind.Sum, true, out parsed);
        }

        Assert.Equal(UnisocParseStatus.Frame, status);
        Assert.Equal((ushort)UnisocCommand.Connect, parsed.Type);
    }

    [Fact]
    public void Parser_WrongAlgorithm_MalformedLeavesBufferForTheRightOne()
    {
        var frame = UnisocFrames.EncodeCommand(UnisocCommand.Connect, ReadOnlySpan<byte>.Empty,
            UnisocChecksumKind.Sum, transcode: true);
        var parser = new UnisocFrameParser();

        parser.Append(frame);
        Assert.Equal(UnisocParseStatus.Malformed, parser.TryRead(UnisocChecksumKind.Crc16, true, out _));
        Assert.Equal(UnisocParseStatus.Frame, parser.TryRead(UnisocChecksumKind.Sum, true, out var parsed));
        Assert.Equal((ushort)UnisocCommand.Connect, parsed.Type);
    }

    [Fact]
    public void Parser_GarbageBeforeDelimiter_SkipsToTheFrame()
    {
        var frame = UnisocFrames.EncodeCommand(UnisocCommand.ReadChipUid, ReadOnlySpan<byte>.Empty,
            UnisocChecksumKind.Sum, transcode: true);
        var parser = new UnisocFrameParser();

        parser.Append([0x00, 0x55, 0xAA]);
        Assert.Equal(UnisocParseStatus.Incomplete, parser.TryRead(UnisocChecksumKind.Sum, true, out _));
        parser.Append(frame);
        Assert.Equal(UnisocParseStatus.Frame, parser.TryRead(UnisocChecksumKind.Sum, true, out var parsed));
        Assert.Equal((ushort)UnisocCommand.ReadChipUid, parsed.Type);
    }

    [Fact]
    public async Task Open_BootRomAnswersSprd3_IdentifiesOpenBootRom()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0x81, "SPRD3\0"u8.ToArray(), UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));

        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);
        Assert.Equal(UnisocBootStage.BootRom, session.Stage);
        Assert.False(session.Identity.SecureBoot);
        Assert.Equal("SPRD3", session.Identity.Version is { } version
            ? System.Text.Encoding.ASCII.GetString(version).TrimEnd('\0')
            : string.Empty);
    }

    [Fact]
    public async Task Open_CapturedBootRomWireFrame_IdentifiesOpenBootRom()
    {
        // Exact reply captured from the iPlay40 (T1020S) BootROM: a CRC16-framed SPRD3 version reply.
        var transport = new FakeTransport();
        transport.EnqueueRaw(Convert.FromHexString("7E00810006535052443300570A7E"));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));

        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);

        Assert.Equal(UnisocBootStage.BootRom, session.Stage);
        Assert.False(session.Identity.SecureBoot);
        Assert.Equal("SPRD3", System.Text.Encoding.ASCII.GetString(session.Identity.Version!).TrimEnd('\0'));
    }

    [Fact]
    public async Task Open_BootRomAnswersOtherVersion_MarksSecureBoot()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0x81, "SPRD3W25X64"u8.ToArray(), UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));

        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);
        Assert.Equal(UnisocBootStage.BootRom, session.Stage);
        Assert.True(session.Identity.SecureBoot);
    }

    [Fact]
    public async Task Open_EmptyCrc16VerifyError_TakesTheBootRomCorrectionLine()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0x8B, [], UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));

        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);
        Assert.Equal(UnisocBootStage.BootRom, session.Stage);
        Assert.True(session.Identity.SecureBoot);
    }

    [Fact]
    public async Task Open_Fdl1AnswersUnderSumChecksum_IdentifiesFdl1()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0x81, "FDL1 SPRD"u8.ToArray(), UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));

        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);
        Assert.Equal(UnisocBootStage.Fdl1, session.Stage);
    }

    [Fact]
    public async Task Open_EmptyUnsupportedUnderSum_IdentifiesRunningFdl2()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0xFE, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));

        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);
        Assert.Equal(UnisocBootStage.Fdl2, session.Stage);
    }

    [Fact]
    public async Task Open_SilentLink_FailsInsteadOfGuessing()
    {
        var transport = new FakeTransport();

        await Assert.ThrowsAsync<UnisocProtocolException>(() => UnisocDeviceSession.OpenAsync(transport, null,
            TestContext.Current.CancellationToken));
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task UploadFdl1_ChunksWithCrc16AndRehandshakesUnderSum()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0x81, "SPRD3\0"u8.ToArray(), UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));
        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);

        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x81, "FDL1 loader"u8.ToArray(), UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));

        var loader = new byte[] { 0xA5 };
        await session.UploadFdl1Async(new MemoryStream(loader), 0x40004000, TestContext.Current.CancellationToken);

        Assert.Equal(UnisocBootStage.Fdl1, session.Stage);
        var starts = transport.WrittenFrames(UnisocCommand.StartData);
        Assert.Single(starts);
        Assert.Equal(0x40004000u, BinaryPrimitives.ReadUInt32BigEndian(starts[0]));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(starts[0].AsSpan(4)));
        var mids = transport.WrittenFrames(UnisocCommand.MidstData);
        Assert.Single(mids);
        Assert.Equal(loader, mids[0]);
        Assert.Contains(transport.WrittenTypes(), type => type == (ushort)UnisocCommand.ExecData);
    }

    [Fact]
    public async Task UploadFdl1_StreamPositionedPastStart_DeclaresOnlyRemainingBytes()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0x81, "SPRD3\0"u8.ToArray(), UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));
        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);

        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x81, "FDL1 loader"u8.ToArray(), UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));

        var loader = new byte[] { 0x11, 0xA5 };
        var stream = new MemoryStream(loader);
        stream.Position = 1;
        await session.UploadFdl1Async(stream, 0x40004000, TestContext.Current.CancellationToken);

        var starts = transport.WrittenFrames(UnisocCommand.StartData);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(starts[0].AsSpan(4)));
        var mids = transport.WrittenFrames(UnisocCommand.MidstData);
        Assert.Equal(new byte[] { 0xA5 }, mids[0]);
    }

    [Fact]
    public async Task UploadFdl1_AddressPlusSizeBeyondRam_IsRejectedBeforeAnyFrame()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0x81, "SPRD3\0"u8.ToArray(), UnisocChecksumKind.Crc16));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Crc16));
        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);

        var loader = new byte[512];
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.UploadFdl1Async(new MemoryStream(loader), 0xFFFFFF00, TestContext.Current.CancellationToken));
        Assert.Empty(transport.WrittenFrames(UnisocCommand.StartData));
        Assert.Equal(UnisocBootStage.BootRom, session.Stage);
    }

    [Fact]
    public async Task UploadFdl2_ToleratesIncompatiblePartitionExecReply()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0x81, "FDL1 loader"u8.ToArray(), UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);

        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x96, [], UnisocChecksumKind.Sum));

        var loader = new byte[600];
        await session.UploadFdl2Async(new MemoryStream(loader), 0x9EFFFE00, TestContext.Current.CancellationToken);

        Assert.Equal(UnisocBootStage.Fdl2, session.Stage);
        var mids = transport.WrittenFrames(UnisocCommand.MidstData);
        Assert.Equal(2, mids.Count);
        Assert.Equal(528, mids[0].Length);
        Assert.Equal(72, mids[1].Length);
    }

    [Fact]
    public async Task DisableTranscode_AckThenFramesGoOutUnescaped()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0xFE, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);

        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        await session.DisableTranscodeAsync(TestContext.Current.CancellationToken);

        var last = transport.LastRawWrite;
        Assert.DoesNotContain((byte)0x7D, last);
        Assert.Contains((byte)0x7E, last);
    }

    [Fact]
    public async Task ReadChipUid_ReturnsPayloadAndRefusesOutOfBoundsReplies()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0xFE, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);

        var uid = new byte[] { 0xAB, 0x01, 0x02, 0x03 };
        transport.Enqueue(broker => broker.Reply(0xAB, uid, UnisocChecksumKind.Sum));
        Assert.Equal(uid, await session.ReadChipUidAsync(TestContext.Current.CancellationToken));

        transport.Enqueue(broker => broker.Reply(0xAB, new byte[300], UnisocChecksumKind.Sum));
        await Assert.ThrowsAsync<UnisocProtocolException>(() =>
            session.ReadChipUidAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NormalResetAndPowerOff_ExpectAcknowledgement()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0xFE, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);

        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        await session.NormalResetAsync(TestContext.Current.CancellationToken);
        Assert.Contains(transport.WrittenTypes(), type => type == (ushort)UnisocCommand.NormalReset);

        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        await session.PowerOffAsync(TestContext.Current.CancellationToken);
        Assert.Contains(transport.WrittenTypes(), type => type == (ushort)UnisocCommand.PowerOff);
    }

    [Fact]
    public async Task Storage_ReadPartitionTable_ParsesRecordsAndRefusesBadOnes()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0xFE, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);
        var storage = session.OpenStorage();

        var records = new byte[2 * 0x4C];
        WriteName(records.AsSpan(0), "boot");
        BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(0x48), 65536);
        WriteName(records.AsSpan(0x4C), "cache");
        BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(0x4C + 0x48), 0xFFFFFFFF);
        transport.Enqueue(broker => broker.Reply(0xBA, records, UnisocChecksumKind.Sum));
        var table = await storage.ReadPartitionTableAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, table.Count);
        Assert.Equal("boot", table[0].Name);
        Assert.Equal(65536u, table[0].SizeValue);
        Assert.False(table[0].IsRemaining);
        Assert.Equal("cache", table[1].Name);
        Assert.True(table[1].IsRemaining);

        transport.Enqueue(broker => broker.Reply(0xBA, new byte[10], UnisocChecksumKind.Sum));
        await Assert.ThrowsAsync<UnisocProtocolException>(() =>
            storage.ReadPartitionTableAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Storage_ReadWindow_PullsChunksToEndAck()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0xFE, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);
        var storage = session.OpenStorage();

        var payload = new byte[9000];
        Array.Fill(payload, (byte)0x5A);
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x93, payload.AsSpan(0, 4096).ToArray(), UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x93, payload.AsSpan(4096, 4096).ToArray(), UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x93, payload.AsSpan(8192, 808).ToArray(), UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));

        await using var output = new MemoryStream();
        var read = await storage.ReadPartitionAsync("boot", 0, payload.Length, output,
            TestContext.Current.CancellationToken);

        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, output.ToArray());
        var starts = transport.WrittenFrames(UnisocCommand.ReadStart);
        Assert.Single(starts);
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(starts[0].AsSpan(72)));
        Assert.Contains(transport.WrittenTypes(), type => type == (ushort)UnisocCommand.ReadEnd);
    }

    [Fact]
    public async Task Storage_WritePartition_RequiresConfirmationAndRefusesNvNames()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0xFE, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);
        var storage = session.OpenStorage();

        await using var image = new MemoryStream(new byte[512]);
        await Assert.ThrowsAsync<ArgumentException>(() => storage.WritePartitionAsync("boot", image, 512,
            "wrong", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.WritePartitionAsync("runtimenv", image, 512,
            UnisocFlashOptions.ConfirmationPhrase, TestContext.Current.CancellationToken));

        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        await storage.WritePartitionAsync("boot", image, 512, UnisocFlashOptions.ConfirmationPhrase,
            TestContext.Current.CancellationToken);

        var starts = transport.WrittenFrames(UnisocCommand.StartData);
        Assert.Single(starts);
        Assert.Equal(512u, BinaryPrimitives.ReadUInt32LittleEndian(starts[0].AsSpan(72)));
        var mids = transport.WrittenFrames(UnisocCommand.MidstData);
        Assert.Single(mids);
        Assert.Equal(512, mids[0].Length);
    }

    [Fact]
    public async Task Storage_ReadGpt_ReturnsNullWithoutSignature()
    {
        var transport = new FakeTransport();
        transport.Enqueue(broker => broker.Reply(0xFE, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        using var session = await UnisocDeviceSession.OpenAsync(transport, null, TestContext.Current.CancellationToken);
        var storage = session.OpenStorage();

        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x93, new byte[4096], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x93, new byte[4096], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x93, new byte[4096], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x93, new byte[4096], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x93, new byte[4096], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x93, new byte[4096], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x93, new byte[4096], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x93, new byte[4096], UnisocChecksumKind.Sum));
        transport.Enqueue(broker => broker.Reply(0x80, [], UnisocChecksumKind.Sum));

        Assert.Null(await storage.ReadGptAsync(UnisocStorageSession.DefaultGptWindowBytes,
            TestContext.Current.CancellationToken));
    }

    private static void WriteName(Span<byte> destination, string name)
    {
        destination[..UnisocConstants.PartitionNameSize].Clear();
        for (var i = 0; i < name.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(i * 2, 2), name[i]);
        }
    }

    /// <summary>
    /// The device side of the wire: queued replies written with the device's own checksum convention, plus a log of
    /// every frame the host sent. A read with nothing queued returns zero bytes, which the sessions treat as a
    /// timeout, so the silent-link and budget lines are exercised for real.
    /// <para>线上的设备侧：以设备自身的校验约定排队应答，并记录主机发出的每一条帧。没有排队应答的读取返回零字节，
    /// 会话据此判超时，因此静默链路与预算线路都得到真实演练。</para>
    /// </summary>
    private sealed class FakeTransport : IUnisocTransport
    {
        private readonly List<Action<DeviceBroker>> _replies = [];
        private readonly List<byte[]> _writes = [];
        private readonly Queue<byte[]> _rawReplies = new();
        private byte[]? _pending;

        public bool IsDisposed { get; private set; }

        public void Enqueue(Action<DeviceBroker> reply) => _replies.Add(reply);

        public void EnqueueRaw(byte[] bytes)
        {
            _rawReplies.Enqueue(bytes);
        }

        public IReadOnlyList<byte[]> WrittenFrames(UnisocCommand command) =>
            _writes.Where(frame => frame.Length >= 4 &&
                    BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(1)) == (ushort)command &&
                    frame[0] == UnisocConstants.HeaderByte)
                .Select(DecodePayload)
                .ToList();

        public IReadOnlyList<ushort> WrittenTypes() => _writes
            .Where(frame => frame.Length >= 3 && frame[0] == UnisocConstants.HeaderByte)
            .Select(frame => BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(1)))
            .ToList();

        public byte[] LastRawWrite => _writes[^1];

        private static byte[] DecodePayload(byte[] frame)
        {
            var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3));
            var payload = new byte[payloadLength];
            frame.AsSpan(5, payloadLength).CopyTo(payload);
            return payload;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken,
            int? timeoutMilliseconds = null)
        {
            _writes.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken,
            int? timeoutMilliseconds = null)
        {
            byte[] source;
            if (_pending is { } pending)
            {
                source = pending;
                _pending = null;
            }
            else if (_rawReplies.Count > 0)
            {
                source = _rawReplies.Dequeue();
            }
            else if (_replies.Count > 0)
            {
                var broker = new DeviceBroker();
                _replies[0](broker);
                _replies.RemoveAt(0);
                source = broker.Build();
            }
            else
            {
                return Task.FromResult(0);
            }

            var count = Math.Min(source.Length, buffer.Length);
            source.AsSpan(0, count).CopyTo(buffer.Span);
            if (source.Length > count)
            {
                _pending = source[count..];
            }

            return Task.FromResult(count);
        }

        public void Dispose() => IsDisposed = true;
    }

    /// <summary>
    /// Builds one device-originated frame with the device's own wire convention: the additive checksum is written
    /// big-endian without any byte swap, and the payload is escape-transcoded.
    /// <para>构造一条设备发出的帧，遵循设备自身的线上约定：加性校验和按大端原样写出、不做字节交换，负载做转义变换。</para>
    /// </summary>
    private sealed class DeviceBroker
    {
        private ushort _type;
        private byte[] _payload = [];
        private UnisocChecksumKind _kind = UnisocChecksumKind.Sum;

        public DeviceBroker Reply(ushort type, byte[] payload, UnisocChecksumKind kind)
        {
            _type = type;
            _payload = payload;
            _kind = kind;
            return this;
        }

        public byte[] Build()
        {
            var raw = new byte[4 + _payload.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(raw, _type);
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(2), (ushort)_payload.Length);
            _payload.CopyTo(raw, 4);
            var checksum = _kind == UnisocChecksumKind.Crc16
                ? Crc16Xmodem.Compute(raw.AsSpan(0, 4 + _payload.Length))
                : SprdChecksum16.Compute(raw.AsSpan(0, 4 + _payload.Length));
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(4 + _payload.Length), checksum);

            var frame = new List<byte> { UnisocConstants.HeaderByte };
            foreach (var value in raw)
            {
                if (value is UnisocConstants.HeaderByte or UnisocConstants.EscapeByte)
                {
                    frame.Add(UnisocConstants.EscapeByte);
                    frame.Add((byte)(value ^ UnisocConstants.EscapeMask));
                }
                else
                {
                    frame.Add(value);
                }
            }

            frame.Add(UnisocConstants.HeaderByte);
            return [.. frame];
        }
    }
}