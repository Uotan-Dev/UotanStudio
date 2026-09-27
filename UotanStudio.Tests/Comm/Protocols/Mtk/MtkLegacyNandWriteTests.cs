using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkLegacyNandWriteMode.PageAndSpare, 2112)]
    [InlineData(MtkLegacyNandWriteMode.PageOnly, 2048)]
    [InlineData(MtkLegacyNandWriteMode.SpareOnly, 64)]
    [InlineData(MtkLegacyNandWriteMode.PageAndSpareWithEcc, 2112)]
    public async Task WriteLegacyNandStorageAsync_CompleteBlock_SendsExactPageRecordsAndChecksums(
        MtkLegacyNandWriteMode mode, int recordSize)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        var data = Enumerable.Range(0, recordSize * 64 + 3).Select(index => (byte)(index % 251)).ToArray();
        using var source = new FragmentedSource(data);
        source.Position = 2;
        var options = LegacyNandWriteOptions() with { Mode = mode };
        transport.Enqueue([0x5A, .. Enumerable.Repeat((byte)0x69, 64), 0x5A, 0x5A]);
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        await session.WriteLegacyNandStorageAsync(131072, 131072, source, options,
            progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        var header = Convert.FromHexString("B3010000000001000000010000084000000000");
        header[2] = (byte)mode;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(11), (uint)recordSize);
        Assert.Equal(header, transport.Writes[before]);
        Assert.Equal(193, transport.Writes.Count - before);
        for (var page = 0; page < 64; page++)
        {
            var packet = data.AsSpan(2 + page * recordSize, recordSize).ToArray();
            Assert.Equal(new byte[] { 0x5A }, transport.Writes[before + 1 + page * 3]);
            Assert.Equal(packet, transport.Writes[before + 2 + page * 3]);
            Assert.Equal((ushort)(packet.Sum(value => (int)value) & 0xFFFF),
                BinaryPrimitives.ReadUInt16BigEndian(transport.Writes[before + 3 + page * 3]));
        }
        Assert.Equal(Enumerable.Range(1, 64).Select(page => (long)page * recordSize), progress.Values);
        Assert.Equal(2 + recordSize * 64, source.Position);
        Assert.True(source.CanRead);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);

        await session.ShutdownLegacyAsync(MtkLegacyShutdownMode.PowerOff, TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
    }

    [Theory]
    [InlineData(MtkLegacyNandAddressType.Physical)]
    [InlineData(MtkLegacyNandAddressType.Logical)]
    public async Task WriteLegacyNandStorageAsync_AboveFourGiB_EncodesBlockIndexWithoutTruncatingByteAddress(
        MtkLegacyNandAddressType addressType)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport, capacity: 8UL * 1024 * 1024 * 1024);
        using var source = new MemoryStream(new byte[131072]);
        var options = LegacyNandWriteOptions() with
        {
            Mode = MtkLegacyNandWriteMode.PageOnly,
            AddressType = addressType,
            LogicalCapacityBytes = addressType == MtkLegacyNandAddressType.Logical ? 6L * 1024 * 1024 * 1024 : null
        };
        transport.Enqueue([0x5A, .. Enumerable.Repeat((byte)0x69, 64)]);
        var before = transport.Writes.Count;

        await session.WriteLegacyNandStorageAsync(0x1_0000_0000, 131072, source, options,
            cancellationToken: TestContext.Current.CancellationToken);

        var header = Convert.FromHexString("B3010100008000000000010000080000000000");
        header[1] = (byte)addressType;
        Assert.Equal(header, transport.Writes[before]);
    }

    [Theory]
    [InlineData(false, 0xA5, 0x12345678U)]
    [InlineData(true, 0xA5, 0x12345678U)]
    [InlineData(true, 0xA5, 0xBFAU)]
    [InlineData(true, 0xA5, 0xBF6U)]
    [InlineData(true, 0xA5, 0xBF7U)]
    [InlineData(true, 0xA5, 0xBF9U)]
    [InlineData(false, 0x69, 0x69U)]
    [InlineData(true, 0x5A, 0x5AU)]
    [InlineData(true, 0x96, 0x96U)]
    [InlineData(true, 0, 0U)]
    public async Task WriteLegacyNandStorageAsync_RejectedPacket_PreservesStatusWithoutRetry(
        bool packet, byte response, uint status)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var source = new MemoryStream(new byte[135168]);
        if (packet)
        {
            transport.Enqueue([0x5A]);
        }
        transport.Enqueue([response]);
        if (response == 0xA5)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, status);
            foreach (var value in bytes)
            {
                transport.Enqueue([value]);
            }
        }
        var before = transport.Writes.Count;
        var progress = new ProgressRecorder();

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteLegacyNandStorageAsync(
            0, 131072, source, LegacyNandWriteOptions(), progress: progress,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(status, error.Status);
        Assert.Equal(packet ? 4 : 1, transport.Writes.Count - before);
        Assert.Equal(packet ? 2112 : 0, source.Position);
        Assert.Empty(progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("header-status")]
    [InlineData("packet")]
    [InlineData("packet-status")]
    [InlineData("last-packet")]
    public async Task WriteLegacyNandStorageAsync_TruncatedResponse_DoesNotReportUnacknowledgedBytes(string stage)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var source = new MemoryStream(new byte[135168]);
        if (stage is not ("header" or "header-status"))
        {
            transport.Enqueue([0x5A]);
        }
        if (stage == "last-packet")
        {
            transport.Enqueue(Enumerable.Repeat((byte)0x69, 63).ToArray());
        }
        if (stage.EndsWith("-status", StringComparison.Ordinal))
        {
            transport.Enqueue([0xA5, 0, 1, 2]);
        }
        var before = transport.Writes.Count;
        var progress = new ProgressRecorder();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteLegacyNandStorageAsync(
            0, 131072, source, LegacyNandWriteOptions(), progress: progress,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(stage switch { "header" or "header-status" => 1, "last-packet" => 193, _ => 4 },
            transport.Writes.Count - before);
        Assert.Equal(stage == "last-packet" ? 63 : 0, progress.Values.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    private static MtkLegacyNandWriteOptions LegacyNandWriteOptions() => new()
    {
        Profile = MtkLegacyNandWriteProfile.Sdk32BlocksRaw,
        AddressType = MtkLegacyNandAddressType.Physical,
        Mode = MtkLegacyNandWriteMode.PageAndSpare
    };
}