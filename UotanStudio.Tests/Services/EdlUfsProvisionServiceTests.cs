using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public class EdlUfsProvisionServiceTests
{
    private const string Ack = """<data><response value="ACK" rawmode="false"/></data>""";

    [Fact]
    public async Task ProvisionCoreAsync_UnconfirmedProfile_ThrowsBeforeAnyDeviceByte()
    {
        using var transport = new ScriptedCommTransport();
        using var firehose = CreateProtocol(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EdlUfsProvisionService.ProvisionCoreAsync(firehose, TwoLunProfile(commit: false), confirmed: false,
                null, TestContext.Current.CancellationToken));

        Assert.Contains("explicit confirmation", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Fact]
    public async Task ProvisionCoreAsync_CancelledToken_ThrowsBeforeAnyDeviceByte()
    {
        using var transport = new ScriptedCommTransport();
        using var firehose = CreateProtocol(transport);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EdlUfsProvisionService.ProvisionCoreAsync(firehose, TwoLunProfile(commit: true), confirmed: true,
                null, cancellation.Token));

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Fact]
    public async Task ProvisionCoreAsync_EmmcReportedStorage_RefusesBeforeAnyDeviceByte()
    {
        using var transport = new ScriptedCommTransport();
        using var firehose = CreateProtocol(transport, FirehoseStorageType.Emmc);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EdlUfsProvisionService.ProvisionCoreAsync(firehose, TwoLunProfile(commit: true), confirmed: true,
                null, TestContext.Current.CancellationToken));

        Assert.Contains("reported Emmc storage", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Fact]
    public async Task PreviewCoreAsync_EmmcReportedStorage_RefusesBeforeAnyDeviceByte()
    {
        using var transport = new ScriptedCommTransport();
        using var firehose = CreateProtocol(transport, FirehoseStorageType.Nand);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EdlUfsProvisionService.PreviewCoreAsync(firehose, TwoLunProfile(commit: true), null,
                TestContext.Current.CancellationToken));

        Assert.Contains("reported Nand storage", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task PreviewCoreAsync_UfsDevice_ReportsLocatedLunTableAndFramesNoUfsCommand()
    {
        using var transport = new ScriptedCommTransport();
        using var firehose = CreateProtocol(transport, slot: 3);
        var frames = new List<FlashProgress>();

        var result = await EdlUfsProvisionService.PreviewCoreAsync(firehose, TwoLunProfile(commit: true),
            new RecordingProgress(frames), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Single(transport.Writes);
        Assert.Contains("getstorageinfo", Encoding.UTF8.GetString(transport.Writes[0]), StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Writes, write => Encoding.UTF8.GetString(write).Contains("<ufs",
            StringComparison.Ordinal));
        Assert.Contains("LUN 0 = 4096 KB", result.Summary, StringComparison.Ordinal);
        Assert.Contains("LUN 1 = 2097152 KB", result.Summary, StringComparison.Ordinal);
        Assert.Contains("commit=true", result.Summary, StringComparison.Ordinal);
        Assert.Contains("slot 3", result.Summary, StringComparison.Ordinal);
        Assert.Contains("commands 4", result.Summary, StringComparison.Ordinal);
        Assert.Contains("without sending a provisioning command", result.Summary, StringComparison.Ordinal);
        Assert.Single(frames);
        Assert.Equal(100, frames[0].Percent);
        Assert.Contains("preview", frames[0].Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewCoreAsync_DeviceReportsSmallBlocks_RefusesDeclaredCapacitiesWithoutProvisioning()
    {
        using var transport = new ScriptedCommTransport();
        using var firehose = CreateProtocol(transport, blockSize: 512);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlUfsProvisionService.PreviewCoreAsync(firehose, TwoLunProfile(commit: false), null,
                TestContext.Current.CancellationToken));

        Assert.Contains("not a UFS block width", exception.Message, StringComparison.Ordinal);
        Assert.Single(transport.Writes);
        Assert.DoesNotContain(transport.Writes, write => Encoding.UTF8.GetString(write).Contains("<ufs",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewCoreAsync_SizeOffTheDeviceBlockBoundary_RefusesBeforeFramingAnyCommand()
    {
        using var transport = new ScriptedCommTransport();
        using var firehose = CreateProtocol(transport, slot: 0);
        var profile = new UfsProvisionProfile(new UfsCommon { BNumberLU = 2 },
            [new UfsBody { LUNum = 0, SizeInKb = 1 }], new UfsEpilogue());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlUfsProvisionService.PreviewCoreAsync(firehose, profile, null,
                TestContext.Current.CancellationToken));

        Assert.Contains("not a whole number of the 4096-byte blocks", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Writes, write => Encoding.UTF8.GetString(write).Contains("<ufs",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProvisionCoreAsync_CommittedProfile_FramesExactlyWhatTheProtocolBuildersProduce()
    {
        using var transport = new ScriptedCommTransport();
        using var firehose = CreateProtocol(transport, slot: 2);
        var common = new UfsCommon { BNumberLU = 2, BBootEnable = 1, BDescrAccessEn = 1 };
        var bodies = new List<UfsBody>
        {
            new() { LUNum = 0, BLUEnable = 1, BBootLUNID = 1, SizeInKb = 4096, BProvisioningType = 1, Desc = "a<&b" },
            new() { LUNum = 1, BLUEnable = 1, SizeInKb = 2097152, BProvisioningType = 1 }
        };
        var epilogue = new UfsEpilogue { LUNumToGrow = 1 };
        var profile = new UfsProvisionProfile(common, bodies, epilogue, commit: true);

        var result = await EdlUfsProvisionService.ProvisionCoreAsync(firehose, profile, confirmed: true, null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("4 UFS provisioning command(s) sent on slot 2", result.Summary, StringComparison.Ordinal);
        Assert.Contains("commit=persisted", result.Summary, StringComparison.Ordinal);
        Assert.Equal(5, transport.Writes.Count);
        var expected = new[]
        {
            FirehoseXmlBuilder.BuildUfsCommon(common, 2),
            FirehoseXmlBuilder.BuildUfsBody(bodies[0], 2),
            FirehoseXmlBuilder.BuildUfsBody(bodies[1], 2),
            FirehoseXmlBuilder.BuildUfsEpilogue(epilogue, true, 2)
        };
        var actual = transport.Writes.Skip(1).Select(Encoding.UTF8.GetString).ToArray();
        Assert.Equal(expected, actual);
        var last = XDocument.Parse(Encoding.UTF8.GetString(transport.Writes[^1])).Root!.Element("ufs")!;
        Assert.Equal("1", (string?)last.Attribute("commit"));
        Assert.Equal("1", (string?)last.Attribute("LUNtoGrow"));
    }

    [Fact]
    public async Task ProvisionCoreAsync_PendingProfile_FramesEpilogueWithCommitZero()
    {
        using var transport = new ScriptedCommTransport();
        using var firehose = CreateProtocol(transport);
        var profile = TwoLunProfile(commit: false);

        var result = await EdlUfsProvisionService.ProvisionCoreAsync(firehose, profile, confirmed: true, null,
            TestContext.Current.CancellationToken);

        Assert.Contains("commit=pending", result.Summary, StringComparison.Ordinal);
        var epilogue = XDocument.Parse(Encoding.UTF8.GetString(transport.Writes[^1])).Root!.Element("ufs")!;
        Assert.Equal("0", (string?)epilogue.Attribute("commit"));
        Assert.Equal(5, transport.Writes.Count);
    }

    [Fact]
    public async Task ProvisionCoreAsync_TargetNaksBody_StopsBeforeTheEpilogue()
    {
        using var transport = new ScriptedCommTransport();
        var ufsCommands = 0;
        transport.OnWrite = bytes =>
        {
            var request = Encoding.UTF8.GetString(bytes);
            if (request.Contains("getstorageinfo", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(GeometryLog(4096, 4_194_304))];
            }

            ufsCommands++;
            return [Encoding.UTF8.GetBytes(ufsCommands == 2
                ? """<data><response value="NAK"/></data>"""
                : Ack)];
        };
        using var firehose = new FirehoseProtocol(transport,
            new FirehoseConfiguration { StorageType = FirehoseStorageType.Ufs });

        await Assert.ThrowsAsync<EdlException>(() =>
            EdlUfsProvisionService.ProvisionCoreAsync(firehose, TwoLunProfile(commit: true), confirmed: true, null,
                TestContext.Current.CancellationToken));

        Assert.Equal(3, transport.Writes.Count);
        Assert.DoesNotContain("LUNtoGrow", Encoding.UTF8.GetString(transport.Writes[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisionCoreAsync_GeometryQueryFails_RefusesWithoutFramingAnyUfsCommand()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes("""<data><response value="NAK"/></data>""")];
        using var firehose = new FirehoseProtocol(transport,
            new FirehoseConfiguration { StorageType = FirehoseStorageType.Ufs });

        await Assert.ThrowsAsync<FirehoseException>(() =>
            EdlUfsProvisionService.PreviewCoreAsync(firehose, TwoLunProfile(commit: true), null,
                TestContext.Current.CancellationToken));

        Assert.Single(transport.Writes);
        Assert.DoesNotContain(transport.Writes, write => Encoding.UTF8.GetString(write).Contains("<ufs",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetCoreAsync_Unconfirmed_ThrowsBeforeAnyDeviceByte()
    {
        using var transport = new ScriptedCommTransport();
        using var firehose = CreateProtocol(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EdlUfsProvisionService.ResetCoreAsync(firehose, confirmed: false, null,
                TestContext.Current.CancellationToken));

        Assert.Contains("Discarding a pending UFS configuration requires explicit confirmation",
            exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ResetCoreAsync_Confirmed_FramesZeroCommitEpilogueOnly()
    {
        using var transport = new ScriptedCommTransport();
        using var firehose = CreateProtocol(transport, slot: 1);

        var result = await EdlUfsProvisionService.ResetCoreAsync(firehose, confirmed: true, null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("nothing was persisted", result.Summary, StringComparison.Ordinal);
        Assert.Single(transport.Writes);
        Assert.Equal(FirehoseXmlBuilder.BuildUfsReset(1), Encoding.UTF8.GetString(transport.Writes[0]));
    }

    [Fact]
    public async Task PreviewCoreAsync_NullArguments_RejectWithoutDeviceInteraction()
    {
        using var transport = new ScriptedCommTransport();
        using var firehose = CreateProtocol(transport);

        await Assert.ThrowsAsync<ArgumentNullException>(() => EdlUfsProvisionService.PreviewCoreAsync(null!,
            TwoLunProfile(commit: false), null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => EdlUfsProvisionService.PreviewCoreAsync(firehose,
            null!, null, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void DescribePlan_RendersEveryLogicalUnitWithItsDeclaredSize()
    {
        var profile = TwoLunProfile(commit: false);

        var plan = EdlUfsProvisionService.DescribePlan(profile, new FirehoseStorageGeometry(0, 4096, 4_194_304), 0);

        Assert.Contains("LUN 0 = 4096 KB", plan, StringComparison.Ordinal);
        Assert.Contains("LUN 1 = 2097152 KB", plan, StringComparison.Ordinal);
        Assert.Contains("bNumberLU 2", plan, StringComparison.Ordinal);
        Assert.Contains("commit=false", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("LUN 2", plan, StringComparison.Ordinal);
    }

    private static UfsProvisionProfile TwoLunProfile(bool commit) => new(
        new UfsCommon { BNumberLU = 2, BBootEnable = 1 },
        [
            new UfsBody { LUNum = 0, BLUEnable = 1, BBootLUNID = 1, SizeInKb = 4096 },
            new UfsBody { LUNum = 1, BLUEnable = 1, SizeInKb = 2097152 }
        ],
        new UfsEpilogue { LUNumToGrow = 0 },
        commit);

    private static FirehoseProtocol CreateProtocol(ScriptedCommTransport transport,
        FirehoseStorageType storageType = FirehoseStorageType.Ufs, long blockSize = 4096, long blockCount = 4_194_304,
        int slot = 0)
    {
        transport.OnWrite = bytes =>
        {
            var request = Encoding.UTF8.GetString(bytes);
            return request.Contains("getstorageinfo", StringComparison.Ordinal)
                ? [Encoding.UTF8.GetBytes(GeometryLog(blockSize, blockCount))]
                : [Encoding.UTF8.GetBytes(Ack)];
        };
        return new FirehoseProtocol(transport,
            new FirehoseConfiguration { StorageType = storageType, Slot = slot });
    }

    private static string GeometryLog(long blockSize, long blockCount) =>
        $$$"""<data><log value='{"storage_info":{"block_size":{{{blockSize}}},"total_blocks":{{{blockCount}}},"physical_partition_number":0}}'/><response value="ACK" rawmode="false"/></data>""";

    private sealed class RecordingProgress(List<FlashProgress> sink) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => sink.Add(value);
    }
}