using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class FirehoseSkipResponseTests
{
    [Fact]
    public async Task EraseAsync_SkipResponseEnabled_CompletesWithoutReadingAResponse()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 512,
            SkipResponseOnErase = true
        });

        var response = await protocol.EraseAsync(0, 4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Single(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task EraseAsync_StrictDefault_StillWaitsForResponseAndFaultsWhenNoneArrives()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 512,
            ResponseTimeoutSeconds = 1
        });
        transport.OnWrite = _ => [];

        await Assert.ThrowsAnyAsync<Exception>(() => protocol.EraseAsync(0, 4,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(transport.ReadCount > 0);
    }

    [Fact]
    public async Task ProgramAsync_SkipReadWriteHeader_WritesPayloadAndConsumesCompletion()
    {
        var completionQueued = false;
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            if (bytes.Length > 0 && bytes[0] == (byte)'<')
            {
                return [];
            }

            IEnumerable<byte[]> responses = completionQueued
                ? []
                : [Encoding.UTF8.GetBytes("<data><response value='ACK'/></data>")];
            completionQueued = true;
            return responses;
        };
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            MaxPayloadSizeToTarget = 4096,
            MaxPayloadSizeToTargetSupported = 4096,
            SkipResponseOnReadWrite = true
        });
        using var data = RawStream(4096);

        var response = await protocol.ProgramAsync(data, 0, 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.True(completionQueued);
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task ProgramAsync_StrictDefault_HeaderStillWaitsAndFaultsWhenHeaderUnanswered()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [];
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            MaxPayloadSizeToTarget = 4096,
            MaxPayloadSizeToTargetSupported = 4096,
            ResponseTimeoutSeconds = 1
        });
        using var data = RawStream(4096);

        await Assert.ThrowsAnyAsync<Exception>(() => protocol.ProgramAsync(data, 0, 1,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(transport.Writes);
    }

    private static MemoryStream RawStream(int length)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, (byte)0xAB);
        return new MemoryStream(buffer, writable: false);
    }
}