using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class FirehosePowerTests
{
    private static byte[] Response(string value) => Encoding.UTF8.GetBytes(
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<data>\n<response value=\"{value}\" />\n</data>\n");

    private static FirehoseConfiguration Configuration(int verifyMs = 50) => new()
    {
        PowerResponseTimeoutMs = 1000,
        PowerResetVerifyTimeoutMs = verifyMs
    };

    [Theory]
    [InlineData(RebootOption.Reset, "reset")]
    [InlineData(RebootOption.PowerOff, "off")]
    [InlineData(RebootOption.ResetToEdl, "reset_to_edl")]
    public void BuildPower_AllOptions_EmitsSelfClosingSchemaCommand(RebootOption option, string value)
    {
        var xml = FirehoseXmlBuilder.BuildPower(option);

        Assert.StartsWith(EdlConstants.XmlHeader, xml);
        Assert.Contains($"<power value=\"{value}\" DelayInSeconds=\"1\" />", xml);
        Assert.DoesNotContain("</power>", xml);
        Assert.DoesNotContain("delay=\"", xml);
        Assert.EndsWith(EdlConstants.DataClose, xml);
    }

    [Fact]
    public async Task ResetAsync_AcknowledgedThenTransportLoss_ReportsObservedDisconnect()
    {
        var transport = new PowerScriptedTransport(Response("ACK"), ReadOutcome.Disconnect);
        var firehose = new FirehoseProtocol(transport, Configuration());
        try
        {
            var response = await firehose.ResetAsync(TestContext.Current.CancellationToken);

            Assert.True(response.IsAck);
            Assert.Equal(FirehosePowerStatus.AcknowledgedAndDisconnected, firehose.LastPowerStatus);
            Assert.True(transport.ReadsAfterResponse >= 1);
            Assert.Contains("<power", Encoding.UTF8.GetString(Assert.Single(transport.Writes)));
        }
        finally
        {
            firehose.Dispose();
        }
    }

    [Fact]
    public async Task ResetAsync_AcknowledgedThenConnectedFlagDrops_ReportsObservedDisconnect()
    {
        var transport = new PowerScriptedTransport(Response("ACK"), ReadOutcome.MarkDisconnected);
        var firehose = new FirehoseProtocol(transport, Configuration());
        try
        {
            var response = await firehose.ResetAsync(TestContext.Current.CancellationToken);

            Assert.True(response.IsAck);
            Assert.Equal(FirehosePowerStatus.AcknowledgedAndDisconnected, firehose.LastPowerStatus);
        }
        finally
        {
            firehose.Dispose();
        }
    }

    [Fact]
    public async Task ResetAsync_LinkStaysUp_ReportsOnlyAcknowledged()
    {
        var transport = new PowerScriptedTransport(Response("ACK"), ReadOutcome.Timeout);
        var firehose = new FirehoseProtocol(transport, Configuration());
        try
        {
            var response = await firehose.ResetAsync(TestContext.Current.CancellationToken);

            Assert.True(response.IsAck);
            Assert.Equal(FirehosePowerStatus.Acknowledged, firehose.LastPowerStatus);
            Assert.True(transport.ReadsAfterResponse >= 1);
        }
        finally
        {
            firehose.Dispose();
        }
    }

    [Fact]
    public async Task ResetAsync_ZeroObservationBudget_StillIssuesOneRead()
    {
        var transport = new PowerScriptedTransport(Response("ACK"), ReadOutcome.Timeout);
        var firehose = new FirehoseProtocol(transport, Configuration(0));
        try
        {
            var response = await firehose.ResetAsync(TestContext.Current.CancellationToken);

            Assert.True(response.IsAck);
            Assert.Equal(FirehosePowerStatus.Acknowledged, firehose.LastPowerStatus);
            Assert.True(transport.ReadsAfterResponse >= 1);
        }
        finally
        {
            firehose.Dispose();
        }
    }

    [Fact]
    public async Task ResetAsync_ResponseTimeout_DoesNotFabricateAck()
    {
        var transport = new PowerScriptedTransport(null, ReadOutcome.Timeout);
        var firehose = new FirehoseProtocol(transport, Configuration());
        try
        {
            var failure = await Assert.ThrowsAsync<EdlException>(
                () => firehose.ResetAsync(TestContext.Current.CancellationToken));

            Assert.Equal(EdlErrorCode.ReadPortTimeout, failure.ErrorCode);
            Assert.Equal(FirehosePowerStatus.Unknown, firehose.LastPowerStatus);
            Assert.Single(transport.Writes);
        }
        finally
        {
            firehose.Dispose();
        }
    }

    [Fact]
    public async Task PowerAsync_Nak_ReportsRejectedWithoutObservation()
    {
        var transport = new PowerScriptedTransport(Response("NAK"), ReadOutcome.Timeout);
        var firehose = new FirehoseProtocol(transport, Configuration());
        try
        {
            var response = await firehose.PowerAsync(RebootOption.Reset, TestContext.Current.CancellationToken);

            Assert.True(response.IsNak);
            Assert.Equal(FirehosePowerStatus.Rejected, firehose.LastPowerStatus);
            Assert.Equal(0, transport.ReadsAfterResponse);
        }
        finally
        {
            firehose.Dispose();
        }
    }

    private enum ReadOutcome
    {
        Timeout,
        Disconnect,
        MarkDisconnected
    }

    private sealed class PowerScriptedTransport : ICommTransport
    {
        private readonly Queue<byte[]> _responses = new();
        private readonly ReadOutcome _outcome;

        public PowerScriptedTransport(byte[]? response, ReadOutcome outcome)
        {
            if (response is not null)
            {
                _responses.Enqueue(response);
            }

            _outcome = outcome;
        }

        public List<byte[]> Writes { get; } = [];

        public bool IsConnected { get; private set; } = true;

        public int ReadsAfterResponse { get; private set; }

        public void Connect()
        {
        }

        public void Disconnect() => IsConnected = false;

        public void Reset()
        {
        }

        public int Read(byte[] buffer, int offset, int length, int timeoutMs) => throw new NotSupportedException();

        public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count > 0)
            {
                var chunk = _responses.Dequeue();
                chunk.CopyTo(buffer, offset);
                return Task.FromResult(chunk.Length);
            }

            ReadsAfterResponse++;
            return _outcome switch
            {
                ReadOutcome.Disconnect => throw new IOException("USB async poll reported POLLERR/POLLHUP."),
                ReadOutcome.MarkDisconnected => MarkDisconnectedAsync(),
                _ => throw new TimeoutException()
            };
        }

        public long Write(byte[] data, int offset, int length, int timeoutMs) => throw new NotSupportedException();

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(data.AsSpan(offset, length).ToArray());
            return Task.FromResult((long)length);
        }

        public Stream GetStream() => throw new NotSupportedException();

        public void Dispose()
        {
        }

        private Task<int> MarkDisconnectedAsync()
        {
            IsConnected = false;
            return Task.FromResult(0);
        }
    }
}