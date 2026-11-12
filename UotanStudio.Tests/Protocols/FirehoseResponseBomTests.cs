using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Replays Firehose byte streams and records written commands.
/// <para>回放 Firehose 字节流并记录写入的命令。</para>
/// </summary>
public sealed class ScriptedFirehoseTransport : ICommTransport
{
    private readonly Queue<byte[]> _reads = new();
    private byte[]? _carry;
    public List<byte[]> Writes { get; } = [];

    public bool IsConnected => true;

    public void Connect()
    {
    }

    public void Disconnect()
    {
    }

    public void Reset()
    {
    }

    public void EnqueueRead(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            _reads.Enqueue(chunk);
        }
    }

    public int Read(byte[] buffer, int offset, int length, int timeoutMs) => throw new NotSupportedException();

    public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (_carry is { Length: > 0 } carry)
        {
            var take = Math.Min(length, carry.Length);
            carry.AsSpan(0, take).CopyTo(buffer.AsSpan(offset));
            _carry = take < carry.Length ? carry[take..] : null;
            return Task.FromResult(take);
        }

        if (_reads.Count == 0)
        {
            throw new TimeoutException();
        }

        var chunk = _reads.Dequeue();
        var copy = Math.Min(length, chunk.Length);
        chunk.AsSpan(0, copy).CopyTo(buffer.AsSpan(offset));
        _carry = copy < chunk.Length ? chunk[copy..] : null;
        return Task.FromResult(copy);
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
}

public class FirehoseResponseBomTests
{
    private static byte[] Ack(string status = "ACK")
        => Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<data>\n<response value=\"{status}\" />\n</data>\n");

    [Fact]
    public void MeasureDocumentPrefix_Utf8BomAfterPadding_StripsBomAndTrailingPadding()
    {
        ReadOnlySpan<byte> prefix = [0x00, 0x20, 0xEF, 0xBB, 0xBF, (byte)'\n', (byte)'<'];

        Assert.Equal(6, EdlConstants.MeasureDocumentPrefix(prefix));
    }

    [Fact]
    public void MeasureDocumentPrefix_BomThenDocument_StripsOnlyBom()
    {
        ReadOnlySpan<byte> prefix = [0xEF, 0xBB, 0xBF, (byte)'<'];

        Assert.Equal(3, EdlConstants.MeasureDocumentPrefix(prefix));
    }

    [Fact]
    public void MeasureDocumentPrefix_NoPaddingOrBom_ReturnsZero()
    {
        ReadOnlySpan<byte> prefix = [(byte)'<', (byte)'?'];

        Assert.Equal(0, EdlConstants.MeasureDocumentPrefix(prefix));
    }

    [Fact]
    public void MeasureDocumentPrefix_PartialBom_DoesNotConsumeBytes()
    {
        ReadOnlySpan<byte> prefix = [0xEF, 0xBB];

        Assert.Equal(0, EdlConstants.MeasureDocumentPrefix(prefix));
    }

    /// <summary>
    /// The configuration the scripted transports use: the fake transport reports read timeouts and never drops
    /// the connection, so the real 30-second disconnection window would only ever be waited out in full.
    /// <para>脚本传输用法的配置：假传输只报读取超时且不会断开连接，因此真实的 30 秒断开观察窗口在这里只会
    /// 被整段白等。</para>
    /// </summary>
    /// <returns>A configuration with a shortened disconnection-observation budget. <para>断开观察预算被缩短的配置。</para></returns>
    private static FirehoseConfiguration ScriptedPowerConfiguration() => new() { PowerResetVerifyTimeoutMs = 50 };

    [Fact]
    public async Task PowerOff_BomPrefixedAckResponse_IsAcknowledged()
    {
        var transport = new ScriptedFirehoseTransport();
        transport.EnqueueRead([0xEF, 0xBB, 0xBF], Ack());

        var firehose = new FirehoseProtocol(transport, ScriptedPowerConfiguration());
        try
        {
            var response = await firehose.PowerOffAsync(TestContext.Current.CancellationToken);

            Assert.True(response.IsAck);
            Assert.False(response.IsNak);
        }
        finally
        {
            firehose.Dispose();
        }
    }

    [Fact]
    public async Task PowerOff_NulPaddedAckResponse_IsAcknowledged()
    {
        var transport = new ScriptedFirehoseTransport();
        transport.EnqueueRead([0x00, 0x00, 0x00], Ack());

        var firehose = new FirehoseProtocol(transport, ScriptedPowerConfiguration());
        try
        {
            var response = await firehose.PowerOffAsync(TestContext.Current.CancellationToken);

            Assert.True(response.IsAck);
        }
        finally
        {
            firehose.Dispose();
        }
    }

    [Fact]
    public async Task PowerOff_LogThenBomPrefixedResponse_ParsesBothBlocks()
    {
        var transport = new ScriptedFirehoseTransport();
        var log = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" ?>\n<data>\n<log value=\"hello\" />\n</data>\n");
        transport.EnqueueRead(log, [0xEF, 0xBB, 0xBF], Ack());

        var firehose = new FirehoseProtocol(transport, ScriptedPowerConfiguration());
        var logs = new List<string>();
        firehose.LogMessage += (_, message) => logs.Add(message);
        try
        {
            var response = await firehose.PowerOffAsync(TestContext.Current.CancellationToken);

            Assert.True(response.IsAck);
            Assert.Contains(logs, entry => entry.Contains("hello", StringComparison.Ordinal));
        }
        finally
        {
            firehose.Dispose();
        }
    }

    [Theory]
    [InlineData("NVMe", FirehoseStorageType.Nvme, "NVMe")]
    [InlineData("nvme", FirehoseStorageType.Nvme, "NVMe")]
    [InlineData("ufs", FirehoseStorageType.Ufs, "ufs")]
    [InlineData("spinor", FirehoseStorageType.Spinor, "spinor")]
    public async Task Configure_NakNamingStorageType_SwitchesConfiguredStorageType(
        string rejectedStorageText, FirehoseStorageType expectedType, string expectedToken)
    {
        var transport = new ScriptedFirehoseTransport();
        var nak = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" ?>\n<data>\n<response value=\"NAK\" MemoryName=\""
            + rejectedStorageText + "\" />\n</data>\n");
        transport.EnqueueRead(nak, Ack("ACK"));
        transport.EnqueueRead(Ack("ACK"), Ack("ACK"));

        var firehose = new FirehoseProtocol(transport,
            new FirehoseConfiguration { StorageType = FirehoseStorageType.Emmc, ConfigureRetryIntervalMs = 0 });
        try
        {
            var response = await firehose.ConfigureAsync(2, TestContext.Current.CancellationToken);

            Assert.True(response.IsAck);
            Assert.Equal(expectedType, firehose.Configuration.StorageType);
            Assert.Equal(expectedToken, firehose.Configuration.StorageTypeString);
        }
        finally
        {
            firehose.Dispose();
        }
    }

    [Fact]
    public void StorageTypeString_Nvme_SerializesAsNVMe()
    {
        var configuration = new FirehoseConfiguration { StorageType = FirehoseStorageType.Nvme };

        var xml = FirehoseXmlBuilder.BuildConfigure(configuration);

        Assert.Contains("MemoryName=\"NVMe\"", xml);
    }
}