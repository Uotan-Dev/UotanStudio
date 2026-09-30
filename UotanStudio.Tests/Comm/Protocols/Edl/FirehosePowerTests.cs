using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public class FirehosePowerTests
{
    private const string Ack = "<data><response value='ACK' rawmode='false'/></data>";
    private const string Nak = "<data><response value='NAK' rawmode='false'/></data>";

    [Theory]
    [InlineData(RebootOption.Reset)]
    [InlineData(RebootOption.PowerOff)]
    [InlineData(RebootOption.ResetToEdl)]
    public async Task PowerAsync_Acknowledged_RetiresSessionWithoutClaimingBoot(RebootOption option)
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(Ack)];
        using var protocol = CreateProtocol(transport);

        var response = await protocol.PowerAsync(option, TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(Ack, response.RawXml);
        Assert.Equal(FirehosePowerStatus.Acknowledged, protocol.LastPowerStatus);
        Assert.True(protocol.RequiresReconnect);
        await Assert.ThrowsAsync<EdlException>(() => protocol.NopAsync(TestContext.Current.CancellationToken));
        Assert.Equal(option == RebootOption.Reset ? 2 : 1, transport.Writes.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<data><log value='Will issue reset'/></data>")]
    [InlineData("<data><log value='bsp_target_reset'/></data>")]
    public async Task PowerAsync_NoAcknowledgement_ReportsUnknownAndNeverRetries(string reply)
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(reply)];
        using var protocol = CreateProtocol(transport);

        var exception = await Assert.ThrowsAsync<EdlException>(() => protocol.PowerOffAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.TransportTimeout, exception.ErrorCode);
        Assert.Equal(FirehosePowerStatus.Unknown, protocol.LastPowerStatus);
        Assert.True(protocol.RequiresReconnect);
        Assert.Single(transport.Writes);
    }

    [Theory]
    [InlineData("<data><response value='ACK'></data>")]
    [InlineData("<data><response value='ACK' rawmode='true'/></data>")]
    [InlineData("<data><response value='NAK' rawmode='true'/></data>")]
    [InlineData("<data><response value='UNKNOWN'/></data>")]
    [InlineData("<data><response/></data>")]
    public async Task PowerAsync_InvalidResponse_ThrowsInsteadOfAssumingReset(string reply)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(reply);
        using var protocol = CreateProtocol(transport);

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.PowerOffAsync(TestContext.Current.CancellationToken));

        Assert.Equal(FirehosePowerStatus.Unknown, protocol.LastPowerStatus);
        Assert.True(protocol.RequiresReconnect);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task PowerAsync_Rejected_PreservesNakAndUsableSession()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(transport.Writes.Count == 1 ? Nak : Ack)];
        using var protocol = CreateProtocol(transport);

        var response = await protocol.PowerOffAsync(TestContext.Current.CancellationToken);
        var next = await protocol.NopAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsNak);
        Assert.Equal(Nak, response.RawXml);
        Assert.Equal(FirehosePowerStatus.Rejected, protocol.LastPowerStatus);
        Assert.False(protocol.RequiresReconnect);
        Assert.True(next.IsAck);
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task PowerAsync_WriteFailure_ReportsUnknownWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => throw new IOException("Uncertain partial write.");
        using var protocol = CreateProtocol(transport);

        await Assert.ThrowsAsync<IOException>(() => protocol.PowerOffAsync(TestContext.Current.CancellationToken));

        Assert.Equal(FirehosePowerStatus.Unknown, protocol.LastPowerStatus);
        Assert.True(protocol.RequiresReconnect);
        Assert.Single(transport.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PowerAsync_TransportDisconnects_RequiresRealAckForAcknowledgedStatus(bool acknowledged)
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ =>
        {
            transport.Disconnect();
            return acknowledged ? [Encoding.UTF8.GetBytes(Ack)] : [];
        };
        using var protocol = CreateProtocol(transport);

        if (acknowledged)
        {
            var response = await protocol.PowerOffAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsAck);
            Assert.Equal(FirehosePowerStatus.AcknowledgedAndDisconnected, protocol.LastPowerStatus);
        }
        else
        {
            await Assert.ThrowsAsync<EdlException>(() => protocol.PowerOffAsync(TestContext.Current.CancellationToken));
            Assert.Equal(FirehosePowerStatus.Unknown, protocol.LastPowerStatus);
        }

        Assert.True(protocol.RequiresReconnect);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task PowerAsync_CancelledWhileObservingDisconnect_RetainsAcknowledgementEvidence()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ack);
        using var protocol = CreateProtocol(transport);
        protocol.Configuration.PowerResetVerifyTimeoutMs = 1000;

        var power = protocol.PowerOffAsync(cancellation.Token);
        Assert.Equal(FirehosePowerStatus.Acknowledged, protocol.LastPowerStatus);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => power);

        Assert.True(protocol.RequiresReconnect);
        Assert.Equal(FirehosePowerStatus.Acknowledged, protocol.LastPowerStatus);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task PowerAsync_CancelledBeforeResponse_ReportsUnknown()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ =>
        {
            cancellation.Cancel();
            return [];
        };
        using var protocol = CreateProtocol(transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => protocol.PowerOffAsync(cancellation.Token));

        Assert.True(protocol.RequiresReconnect);
        Assert.Equal(FirehosePowerStatus.Unknown, protocol.LastPowerStatus);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task PowerAsync_DisconnectObservedAfterAck_RecordsOnlyTransportEvidence()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ack);
        using var protocol = CreateProtocol(transport);
        protocol.Configuration.PowerResetVerifyTimeoutMs = 1000;

        var power = protocol.PowerOffAsync(TestContext.Current.CancellationToken);
        Assert.Equal(FirehosePowerStatus.Acknowledged, protocol.LastPowerStatus);
        Assert.False(power.IsCompleted);
        transport.Disconnect();
        var response = await power.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(FirehosePowerStatus.AcknowledgedAndDisconnected, protocol.LastPowerStatus);
        Assert.True(protocol.RequiresReconnect);
        Assert.Equal(1, transport.ReadCount);
    }

    [Theory]
    [InlineData("<data><log value='Will issue reset'/></data>")]
    [InlineData("<data><broken")]
    public async Task PowerAsync_TrailingLogsOrMalformedBytes_DoNotCountAsDisconnect(string trailing)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ack + trailing);
        using var protocol = CreateProtocol(transport);
        protocol.Configuration.PowerResetVerifyTimeoutMs = 1;

        var response = await protocol.PowerOffAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(FirehosePowerStatus.Acknowledged, protocol.LastPowerStatus);
        Assert.True(transport.IsConnected);
        Assert.Equal(1, transport.ReadCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<data><response value='ACK' rawmode='true'/></data>")]
    public async Task ResetAsync_UncertainBootDrivePreparation_DoesNotSendPower(string reply)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(reply);
        using var protocol = CreateProtocol(transport);
        protocol.Configuration.ResponseTimeoutSeconds = 1;

        await Assert.ThrowsAnyAsync<EdlException>(() => protocol.ResetAsync(TestContext.Current.CancellationToken));

        Assert.Equal(FirehosePowerStatus.NotRequested, protocol.LastPowerStatus);
        Assert.True(protocol.RequiresReconnect);
        Assert.Contains("<setbootablestoragedrive ", Encoding.UTF8.GetString(Assert.Single(transport.Writes)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetAsync_BootDrivePreparationNak_StillSendsRequestedPower()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(transport.Writes.Count == 1 ? Nak : Ack)];
        using var protocol = CreateProtocol(transport);

        var response = await protocol.ResetAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(FirehosePowerStatus.Acknowledged, protocol.LastPowerStatus);
        Assert.Contains("<power ", Encoding.UTF8.GetString(transport.Writes[1]), StringComparison.Ordinal);
        Assert.Equal(2, transport.Writes.Count);
    }

    [Theory]
    [InlineData(FirehoseStorageType.Ufs, "<setbootablestoragedrive value=\"1\" />")]
    [InlineData(FirehoseStorageType.Emmc, "<setbootablestoragedrive value=\"0\" />")]
    public async Task ResetAsync_StorageWithBootableUnit_SendsMappedPreparation(FirehoseStorageType storageType, string expected)
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(Ack)];
        using var protocol = CreateProtocol(transport, storageType);

        var response = await protocol.ResetAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(expected, ExtractElement(transport.Writes[0]));
        Assert.Equal("<power value=\"reset\" />", ExtractElement(transport.Writes[1]));
    }

    [Theory]
    [InlineData(FirehoseStorageType.Nand)]
    [InlineData(FirehoseStorageType.Spinor)]
    [InlineData(FirehoseStorageType.Unknown)]
    public async Task ResetAsync_StorageWithoutBootableUnit_SendsOnlyPower(FirehoseStorageType storageType)
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(Ack)];
        using var protocol = CreateProtocol(transport, storageType);

        var response = await protocol.ResetAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(FirehosePowerStatus.Acknowledged, protocol.LastPowerStatus);
        Assert.Equal("<power value=\"reset\" />", ExtractElement(Assert.Single(transport.Writes)));
    }

    [Fact]
    public async Task PowerAsync_InvalidOption_DoesNotSendAnyCommand()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            protocol.PowerAsync((RebootOption)int.MaxValue, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.False(protocol.RequiresReconnect);
        Assert.Equal(FirehosePowerStatus.NotRequested, protocol.LastPowerStatus);
    }

    private static string ExtractElement(byte[] write)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(write));
        return document.Root!.Elements().Single().ToString(SaveOptions.DisableFormatting);
    }

    private static FirehoseProtocol CreateProtocol(ScriptedCommTransport transport) => new(transport, new FirehoseConfiguration
    {
        SectorSize = 512,
        PowerResponseTimeoutMs = 20,
        PowerResetVerifyTimeoutMs = 0
    }, timeoutMs: 10);

    private static FirehoseProtocol CreateProtocol(ScriptedCommTransport transport, FirehoseStorageType storageType) => new(transport, new FirehoseConfiguration
    {
        SectorSize = 512,
        StorageType = storageType,
        PowerResponseTimeoutMs = 20,
        PowerResetVerifyTimeoutMs = 0
    }, timeoutMs: 10);
}