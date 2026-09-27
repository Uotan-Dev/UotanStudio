using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(0x69)]
    [InlineData(0xA5)]
    public async Task EraseLegacyNandStorageAsync_ExplicitStatisticsMarker_ReturnsBadBlockCountsWithoutClaimingTheyWereErased(byte marker)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A5A0000000064"));
        transport.Enqueue([marker]);
        transport.Enqueue(LegacyEraseStatistics(131072, 262144, 2, 1, 0));
        transport.Enqueue([0xA5, 0x5A]);
        var options = LegacyNandEraseOptions() with { Wire = LegacySdkEraseOptions() with { StatisticsMarker = marker } };

        var result = await session.EraseLegacyNandStorageAsync(131072, 262144, options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(131072, result.Offset);
        Assert.Equal(262144, result.Length);
        Assert.Equal(2U, result.TotalBlocks);
        Assert.Equal(1U, result.BadBlocks);
        Assert.Equal(0U, result.ErrorBlocks);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task EraseLegacyNandStorageAsync_LogicalStatistics_KeepsReportedPhysicalRangeSeparate()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A5A000000006469"));
        transport.Enqueue(LegacyEraseStatistics(262144, 393216, 3, 1, 0));
        transport.Enqueue([0xA5, 0x5A]);
        var options = LegacyNandEraseOptions() with
        {
            AddressType = MtkLegacyNandAddressType.Logical,
            LogicalCapacityBytes = 1024 * 1024,
            Wire = LegacySdkEraseOptions() with { StatisticsMarker = 0x69 }
        };

        var result = await session.EraseLegacyNandStorageAsync(0, 262144, options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(262144, result.Offset);
        Assert.Equal(393216, result.Length);
        Assert.Equal(1U, result.BadBlocks);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("offset-overflow")]
    [InlineData("length-overflow")]
    [InlineData("past-capacity")]
    [InlineData("zero-length")]
    [InlineData("unaligned-offset")]
    [InlineData("unaligned-length")]
    [InlineData("different-range")]
    [InlineData("total-overflow")]
    [InlineData("total-past-reported-span")]
    [InlineData("bad-overflow")]
    [InlineData("error-overflow")]
    [InlineData("reported-error")]
    public async Task EraseLegacyNorStorageAsync_InvalidStatistics_FaultsWithoutPublishingCompletion(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        var bytes = LegacyEraseStatistics(0, 1024, 1, 0, 0);
        switch (failure)
        {
            case "truncated": bytes = bytes[..^1]; break;
            case "offset-overflow": BinaryPrimitives.WriteUInt64BigEndian(bytes, ulong.MaxValue); break;
            case "length-overflow": BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), ulong.MaxValue); break;
            case "past-capacity": BinaryPrimitives.WriteUInt64BigEndian(bytes, 8192); break;
            case "zero-length": bytes.AsSpan(8, 8).Clear(); break;
            case "unaligned-offset": bytes[7] = 1; break;
            case "unaligned-length": bytes[15] = 1; break;
            case "different-range": BinaryPrimitives.WriteUInt64BigEndian(bytes, 1024); break;
            case "total-overflow": BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 9); break;
            case "total-past-reported-span": BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 2); break;
            case "bad-overflow": BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 2); break;
            case "error-overflow": BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 2); break;
            case "reported-error": BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 1); break;
        }
        transport.Enqueue(Convert.FromHexString("5A5A000000006469"));
        transport.Enqueue(bytes);
        var progress = new LegacyEraseProgressRecorder();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseLegacyNorStorageAsync(0, 1024, 1024,
            LegacySdkEraseOptions() with { StatisticsMarker = 0x69 }, progress: progress,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Fact]
    public async Task EraseLegacyNorStorageAsync_UnexpectedStatisticsMarker_DoesNotAcceptAnotherLayout()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A5A000000006469"));
        transport.Enqueue(LegacyEraseStatistics(0, 1024, 1, 0, 0));
        transport.Enqueue([0x5A]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseLegacyNorStorageAsync(0, 1024, 1024,
            LegacySdkEraseOptions() with { StatisticsMarker = 0x68 }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(27)]
    [InlineData(29)]
    public void LegacyEraseStatistics_InvalidLength_Rejects(int size)
    {
        Assert.Throws<MtkProtocolException>(() => MtkLegacyEraseStatistics.Parse(new byte[size], 8192, 1024, 0, 1024, false));
    }

    private static byte[] LegacyEraseStatistics(ulong offset, ulong length, uint total, uint bad, uint errors)
    {
        var bytes = new byte[28];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, offset);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), total);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), bad);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), errors);
        return bytes;
    }
}