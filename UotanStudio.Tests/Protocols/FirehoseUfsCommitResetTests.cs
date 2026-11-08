using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Replays the UFS provisioning epilogue outcomes: a committing programmer that resets the device before
/// acknowledging the last epilogue command completes as applied, while a validation pass and an earlier
/// command still fail on the same silence.
/// <para>回放 UFS provisioning 结语结果：提交型 programmer 在确认最后一条结语命令之前复位设备时按已生效
/// 完成，而校验流程与更早的命令在同样的静默下仍然失败。</para>
/// </summary>
public sealed class FirehoseUfsCommitResetTests
{
    private static byte[] Response(string value) => Encoding.UTF8.GetBytes(
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<data>\n<response value=\"{value}\" />\n</data>\n");

    private static UfsProvisionPlan CommittingPlan() => UfsProvisionPlan.Prepare(
        new UfsCommon { BNumberLU = 1 },
        [new UfsBody { LUNum = 0, BLUEnable = 1, SizeInKb = 4096, BLogicalBlockSize = 12 }],
        new UfsEpilogue(),
        commit: true,
        slot: null);

    private static UfsProvisionPlan ValidatingPlan() => UfsProvisionPlan.Prepare(
        new UfsCommon { BNumberLU = 1 },
        [new UfsBody { LUNum = 0, BLUEnable = 1, SizeInKb = 4096, BLogicalBlockSize = 12 }],
        new UfsEpilogue(),
        commit: false,
        slot: null);

    [Fact]
    public async Task Provision_CommittingEpilogueResetsDeviceWithoutAck_CompletesWithSyntheticAck()
    {
        var transport = new ScriptedFirehoseTransport();
        transport.EnqueueRead(Response("ACK"), Response("ACK"));
        using var firehose = new FirehoseProtocol(transport);
        var response = await firehose.ProvisionUfsOwnedAsync(CommittingPlan(),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.False(firehose.RequiresReconnect);
    }

    [Fact]
    public async Task Provision_CommittingEpilogueReceivesAck_ReportsTheDeviceAcknowledgement()
    {
        var transport = new ScriptedFirehoseTransport();
        transport.EnqueueRead(Response("ACK"), Response("ACK"), Response("ACK"));
        using var firehose = new FirehoseProtocol(transport);
        var response = await firehose.ProvisionUfsOwnedAsync(CommittingPlan(),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal("ACK", response.RawValue);
    }

    [Fact]
    public async Task Provision_ValidatingEpilogueTimesOut_FailsTheProvisioning()
    {
        var transport = new ScriptedFirehoseTransport();
        transport.EnqueueRead(Response("ACK"), Response("ACK"));
        using var firehose = new FirehoseProtocol(transport);
        await Assert.ThrowsAsync<EdlException>(() => firehose.ProvisionUfsOwnedAsync(ValidatingPlan(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Provision_CommittingBodyCommandTimesOut_FailsTheProvisioning()
    {
        var transport = new ScriptedFirehoseTransport();
        transport.EnqueueRead(Response("ACK"));
        using var firehose = new FirehoseProtocol(transport);
        await Assert.ThrowsAsync<EdlException>(() => firehose.ProvisionUfsOwnedAsync(CommittingPlan(),
            TestContext.Current.CancellationToken));
    }
}