using System.Diagnostics;
using System.Reflection;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Transports.Hsuart;
using Xunit;

namespace UotanStudio.Tests.Comm;

/// <summary>Regression tests for incremental communication-protocol framing.</summary>
public class StreamingProtocolTests
{
    [Fact]
    public void AdbShellParser_PreservesSplitHeaderAndPayload()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var frame = new byte[] { (byte)ShellFrameKind.Stdout }
            .Concat(BitConverter.GetBytes(payload.Length)).Concat(payload).ToArray();
        var parser = new AdbShellClient.FrameParser();

        Assert.Empty(parser.Feed(frame[..2]));
        Assert.Empty(parser.Feed(frame[2..7]));
        var parsed = Assert.Single(parser.Feed(frame[7..]));

        Assert.Equal(ShellFrameKind.Stdout, parsed.Kind);
        Assert.Equal(payload, parsed.Payload);
        parser.Complete();
    }

    [Fact]
    public void AdbShellParser_RejectsTruncatedStream()
    {
        var parser = new AdbShellClient.FrameParser();
        parser.Feed([(byte)ShellFrameKind.Stdout, 4, 0, 0, 0, 1]);

        Assert.Throws<InvalidDataException>(() => parser.Complete());
    }

    [Fact]
    public void HsuartFramingParser_PreservesPartialFrameAndReturnsMultipleFrames()
    {
        var first = Encode([1, 2, 3]);
        var second = Encode([4, 5]);
        var parser = new HsuartFrameParser();

        Assert.Empty(parser.Feed(first.AsSpan(0, 2)));

        var combined = first[2..].Concat(second).ToArray();
        var frames = parser.Feed(combined);

        Assert.Equal(2, frames.Count);
        Assert.Equal(first, frames[0]);
        Assert.Equal(second, frames[1]);
    }

    [Fact]
    public void HsuartFramingParser_SeparatesRawAckWithoutDroppingFollowingFrame()
    {
        var encoded = Encode([9, 8, 7]);
        var parser = new HsuartFrameParser();

        var frames = parser.Feed(
            new[] { (byte)HsuartPacketId.AckPacket }.Concat(encoded).ToArray(),
            acceptRawAcknowledgements: true);

        Assert.Equal(2, frames.Count);
        Assert.Equal([(byte)HsuartPacketId.AckPacket], frames[0]);
        Assert.Equal(encoded, frames[1]);
    }

    [Fact]
    public void HsuartFramingParser_DoesNotMistakeCobsCode06ForAck()
    {
        var encoded = Encode([1, 2]);
        Assert.Equal((byte)HsuartPacketId.AckPacket, encoded[0]);
        var parser = new HsuartFrameParser();

        Assert.Empty(parser.Feed(encoded.AsSpan(0, 1), acceptRawAcknowledgements: true));
        var frame = Assert.Single(parser.Feed(encoded.AsSpan(1), acceptRawAcknowledgements: true));

        Assert.Equal(encoded, frame);
    }

    [Fact]
    public void HsuartFramingParser_SeparatesAckAndPreservesPartialFollowingFrame()
    {
        var encoded = Encode([9, 8, 7]);
        var parser = new HsuartFrameParser();
        var prefix = new[] { (byte)HsuartPacketId.AckPacket }.Concat(encoded[..3]).ToArray();

        var acknowledgement = Assert.Single(parser.Feed(prefix, acceptRawAcknowledgements: true));
        Assert.Equal([(byte)HsuartPacketId.AckPacket], acknowledgement);
        var frame = Assert.Single(parser.Feed(encoded.AsSpan(3), acceptRawAcknowledgements: true));
        Assert.Equal(encoded, frame);
    }

    [Fact]
    public void HsuartFramingParser_DoesNotMistakeCompleteCobsCode06FrameForAckPrefix()
    {
        var encoded = Encode([1, 2]);
        var parser = new HsuartFrameParser();

        var frame = Assert.Single(parser.Feed(encoded, acceptRawAcknowledgements: true));

        Assert.Equal(encoded, frame);
    }

    [Fact]
    public void HsuartFramingParser_ReturnsAckOnlyWhenProvenIsolated()
    {
        var parser = new HsuartFrameParser();

        Assert.Empty(parser.Feed([(byte)HsuartPacketId.AckPacket], acceptRawAcknowledgements: true));
        var acknowledgement = parser.TakeIsolatedAcknowledgement();
        Assert.NotNull(acknowledgement);
        Assert.Equal([(byte)HsuartPacketId.AckPacket], acknowledgement);
    }

    [Fact]
    public void HsuartFramingParser_PreservesFragmentAcrossDeadlineBoundary()
    {
        var encoded = Encode([7, 6, 5, 4]);
        var parser = new HsuartFrameParser();

        Assert.Empty(parser.Feed(encoded[..3]));
        Assert.True(parser.HasPending);

        // A frame-read deadline expiring does not reset parser state; the next read can finish it.
        var frame = Assert.Single(parser.Feed(encoded[3..]));
        Assert.Equal(encoded, frame);
        Assert.False(parser.HasPending);
    }

    [Fact]
    public void HsuartFramingParser_SeparatesBoundedAckPrefixInOnePass()
    {
        var encoded = Encode([3, 2, 1]);
        var prefix = Enumerable.Repeat((byte)HsuartPacketId.AckPacket, 8).ToArray();
        var parser = new HsuartFrameParser();

        var frames = parser.Feed(prefix.Concat(encoded).ToArray(), acceptRawAcknowledgements: true);

        Assert.Equal(prefix.Length + 1, frames.Count);
        Assert.All(frames.Take(prefix.Length), frame => Assert.Equal([(byte)HsuartPacketId.AckPacket], frame));
        Assert.Equal(encoded, frames[^1]);
    }

    [Theory]
    [InlineData((byte)HsuartPacketId.AckPacket)]
    [InlineData((byte)HsuartPacketId.NakPacket)]
    public void HsuartFramingParser_HoldsSingleAcknowledgementCandidateUntilDisambiguated(byte value)
    {
        var parser = new HsuartFrameParser();

        Assert.Empty(parser.Feed([value], acceptRawAcknowledgements: true));
        Assert.True(parser.HasIsolatedAcknowledgementCandidate);
        var acknowledgement = parser.TakeIsolatedAcknowledgement();
        Assert.NotNull(acknowledgement);
        Assert.Equal([value], acknowledgement);
    }

    [Fact]
    public void HsuartFramingParser_HandlesFragmentedFrameAcrossManyFeeds()
    {
        var payload = Enumerable.Range(0, 3000).Select(i => (byte)(i & 0xFF)).ToArray();
        var encoded = Encode(payload);
        var parser = new HsuartFrameParser();
        var frames = new List<byte[]>();

        foreach (var value in encoded)
        {
            frames.AddRange(parser.Feed([value]));
        }

        Assert.Equal(encoded, Assert.Single(frames));
        Assert.False(parser.HasPending);
    }

    [Fact]
    public void HsuartFramingParser_HandlesConcatenatedFramesInOneFeed()
    {
        var first = Encode([1, 2, 3]);
        var second = Encode([4, 5, 6, 7]);
        var third = Encode([8, 9]);
        var parser = new HsuartFrameParser();

        var frames = parser.Feed(first.Concat(second).Concat(third).ToArray());

        Assert.Equal(3, frames.Count);
        Assert.Equal(first, frames[0]);
        Assert.Equal(second, frames[1]);
        Assert.Equal(third, frames[2]);
        Assert.False(parser.HasPending);
    }

    [Fact]
    public void HsuartFramingParser_HandlesCompleteAndPartialFramesAcrossFeeds()
    {
        var first = Encode([1, 2, 3]);
        var second = Encode([4, 5]);
        var parser = new HsuartFrameParser();

        var frames = parser.Feed(first.Concat(second[..2]).ToArray());
        Assert.Equal(first, Assert.Single(frames));
        Assert.True(parser.HasPending);

        var frame = Assert.Single(parser.Feed(second[2..]));
        Assert.Equal(second, frame);
        Assert.False(parser.HasPending);
    }

    [Fact]
    public void HsuartFramingParser_ReusesBufferAcrossFragmentedFeeds()
    {
        var payloads = new[]
        {
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(),
            Enumerable.Range(0, 128).Select(i => (byte)(i * 2)).ToArray(),
            Enumerable.Range(0, 96).Select(i => (byte)(i ^ 0x5A)).ToArray()
        };
        var parser = new HsuartFrameParser();
        var parsed = new List<byte[]>();
        var rng = new Random(12345);
        foreach (var payload in payloads)
        {
            var encoded = Encode(payload);
            var offset = 0;
            while (offset < encoded.Length)
            {
                var take = rng.Next(1, 7);
                take = Math.Min(take, encoded.Length - offset);
                parsed.AddRange(parser.Feed(encoded.AsSpan(offset, take)));
                offset += take;
            }
        }

        Assert.Equal(3, parsed.Count);
        for (var i = 0; i < payloads.Length; i++)
        {
            Assert.Equal(Encode(payloads[i]), parsed[i]);
        }
        Assert.False(parser.HasPending);
    }

    [Fact]
    public void HsuartFramingParser_DoesNotLeakBytesAcrossReset()
    {
        var parser = new HsuartFrameParser();
        parser.Feed(Encode([1, 2, 3])[..2]);
        Assert.True(parser.HasPending);

        parser.Reset();
        Assert.False(parser.HasPending);

        var frame = Assert.Single(parser.Feed(Encode([9, 8, 7])));
        Assert.Equal(Encode([9, 8, 7]), frame);
        Assert.False(parser.HasPending);
    }

    [Fact]
    public async Task HsuartTransport_ConnectRechecksDisposedAfterAcquiringSessionGate()
    {
        var transport = new HsuartTransport("HSUART-CONNECT-GATE-GUARD-TEST");
        var gate = (SemaphoreSlim)GetPrivateField(transport, "_sessionGate");
        gate.Wait(TestContext.Current.CancellationToken);
        try
        {
            var connectTask = Task.Run(transport.Connect, TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            SetPrivateField(transport, "_disposeStarted", 1);
            gate.Release();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => connectTask);
        }
        finally
        {
            SetPrivateField(transport, "_disposeStarted", 0);
            if (gate.CurrentCount == 0)
            {
                gate.Release();
            }
            transport.Dispose();
        }
    }

    [Fact]
    public async Task HsuartTransport_ConnectAsyncRechecksDisposedAfterAcquiringSessionGate()
    {
        var transport = new HsuartTransport("HSUART-CONNECT-ASYNC-GATE-GUARD-TEST");
        var gate = (SemaphoreSlim)GetPrivateField(transport, "_sessionGate");
        gate.Wait(TestContext.Current.CancellationToken);
        try
        {
            var connectTask = transport.ConnectAsync(TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            SetPrivateField(transport, "_disposeStarted", 1);
            gate.Release();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => connectTask);
        }
        finally
        {
            SetPrivateField(transport, "_disposeStarted", 0);
            if (gate.CurrentCount == 0)
            {
                gate.Release();
            }
            transport.Dispose();
        }
    }

    [Fact]
    public async Task HsuartTransport_RejectsOperationsAsSoonAsConcurrentDisposeStarts()
    {
        var transport = new HsuartTransport("HSUART-DISPOSE-GUARD-TEST");
        var gate = (SemaphoreSlim)GetPrivateField(transport, "_sessionGate");
        gate.Wait(TestContext.Current.CancellationToken);
        try
        {
            var disposeTask = Task.Run(transport.Dispose, TestContext.Current.CancellationToken);
            Assert.True(SpinWait.SpinUntil(
                () => (int)GetPrivateField(transport, "_disposeStarted") == 1,
                TimeSpan.FromSeconds(5)));

            Assert.Throws<ObjectDisposedException>(transport.Reset);
            gate.Release();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            if (gate.CurrentCount == 0)
            {
                gate.Release();
            }
            transport.Dispose();
        }
    }

    [Theory]
    [InlineData("RemainingTimeout")]
    [InlineData("RemainingFrameTimeout")]
    public void HsuartTimeoutHelpers_PreserveInfiniteTimeout(string helperName)
    {
        var result = InvokeTimeoutHelper(helperName, Timeout.Infinite);

        Assert.Equal(Timeout.Infinite, result);
    }

    [Fact]
    public void HsuartTransport_ExposesSixParameterConstructorAndDefaultsRawAcknowledgementsOn()
    {
        var parameterTypes = new[]
        {
            typeof(string), typeof(int), typeof(System.IO.Ports.Parity), typeof(int),
            typeof(System.IO.Ports.StopBits), typeof(System.IO.Ports.Handshake)
        };
        Assert.NotNull(typeof(HsuartTransport).GetConstructor(parameterTypes));

        using var transport = new HsuartTransport("HSUART-DEFAULT-ACK-TEST");
        Assert.True((bool)GetPrivateField(transport, "_rawAcknowledgementMode"));
    }

    [Theory]
    [InlineData("RemainingTimeout")]
    [InlineData("RemainingFrameTimeout")]
    public void HsuartTimeoutHelpers_AllowZeroTimeoutPoll(string helperName)
    {
        Assert.Equal(0, InvokeTimeoutHelper(helperName, 0));
    }

    [Theory]
    [InlineData("RemainingTimeout")]
    [InlineData("RemainingFrameTimeout")]
    public void HsuartTimeoutHelpers_RejectOtherNegativeTimeouts(string helperName)
    {
        var exception = Assert.Throws<TargetInvocationException>(() => InvokeTimeoutHelper(helperName, -2));

        Assert.IsType<ArgumentOutOfRangeException>(exception.InnerException);
    }

    [Fact]
    public void AdbShellParser_HandlesOneByteFeedsAndReusesStorage()
    {
        var payload = Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray();
        var frame = new byte[] { (byte)ShellFrameKind.Stdout }
            .Concat(BitConverter.GetBytes(payload.Length)).Concat(payload).ToArray();
        var parser = new AdbShellClient.FrameParser();
        var parsed = new List<ShellFrame>();

        foreach (var value in frame)
        {
            parsed.AddRange(parser.Feed([value]));
        }

        Assert.Equal(payload, Assert.Single(parsed).Payload);
        parser.Complete();
    }

    private static object GetPrivateField(HsuartTransport transport, string fieldName) =>
        typeof(HsuartTransport).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;

    private static void SetPrivateField(HsuartTransport transport, string fieldName, object value) =>
        typeof(HsuartTransport).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(transport, value);

    private static int InvokeTimeoutHelper(string helperName, int timeoutMs) =>
        (int)typeof(HsuartTransport).GetMethod(helperName, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [timeoutMs, Stopwatch.StartNew()])!;

    private static byte[] Encode(byte[] payload)
    {
        var buffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var length = HsuartPacket.Encode(HsuartPacketId.ProtocolPacket, payload, buffer, out var error);
        Assert.Equal(HsuartPacketError.Success, error);
        return buffer[..length];
    }
}