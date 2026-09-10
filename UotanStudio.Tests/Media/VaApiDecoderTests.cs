using System.Runtime.InteropServices;
using UotanStudio.Media.Acceleration.Linux;
using UotanStudio.Media.Decoding;
using Xunit;

namespace UotanStudio.Tests.Media;

public class VaApiDecoderTests
{
    [Fact]
    public void BridgeAbi_IsVersionIndependent()
    {
        Assert.Equal(32, Marshal.SizeOf<VaFrameInfo>());
        Assert.Equal(24, Marshal.OffsetOf<VaFrameInfo>(nameof(VaFrameInfo.Timestamp)).ToInt32());
    }

    [Theory]
    [InlineData(VideoCodecId.H264)]
    [InlineData(VideoCodecId.H265)]
    [InlineData(VideoCodecId.Av1)]
    public void Initialize_ForwardsActualCodec_AndReleasesPreviousSession(VideoCodecId codec)
    {
        var first = new FakeSession();
        var second = new FakeSession();
        int attempts = 0;
        using var decoder = new VaApiVideoDecoder(new(), (requested, node) =>
        {
            Assert.Equal(codec, requested);
            Assert.Null(node);
            if (++attempts == 1) return first;
            Assert.True(first.Disposed);
            return second;
        });
        decoder.Initialize(codec, 64, 64, _ => { });
        decoder.Initialize(codec, 64, 64, _ => { });
        decoder.Dispose();
        Assert.True(second.Disposed);
        Assert.False(decoder.IsInitialized);
    }

    [Fact]
    public void Retry_PreservesPacketAndPts_AndDeliversCallbackScopedFrames()
    {
        var session = new FakeSession();
        session.Statuses.Enqueue(2);
        session.Frames.Enqueue(Frame(10));
        DecodedVideoFrame? borrowed = null;
        using var decoder = new VaApiVideoDecoder(new(), (_, _) => session);
        decoder.Initialize(VideoCodecId.H264, 64, 64, frame => borrowed = frame);
        byte[] packet = [0, 0, 1, 0x65, 0x80];
        decoder.Decode(packet, 1234567890123);
        Assert.Equal(2, session.Packets.Count);
        Assert.Equal(packet, session.Packets[0].Bytes);
        Assert.Equal(packet, session.Packets[1].Bytes);
        Assert.Equal(1234567890123, session.Packets[1].Timestamp);
        Assert.NotNull(borrowed);
        Assert.Equal(10, borrowed.TimestampTicks);
        Assert.Throws<ObjectDisposedException>(() => borrowed.Buffer);
    }

    [Fact]
    public void ParameterOnlyPacket_IsCombinedWithNextPicture()
    {
        var session = new FakeSession();
        using var decoder = new VaApiVideoDecoder(new(), (_, _) => session);
        decoder.Initialize(VideoCodecId.H265, 64, 64, _ => { });
        byte[] config = [0, 0, 1, 0x40, 1, 0x80];
        byte[] picture = [0, 0, 1, 0x26, 1, 0x80];
        decoder.Decode(config, 0);
        Assert.Empty(session.Packets);
        decoder.Decode(picture, 99);
        Assert.Equal(config.Concat(picture), session.Packets.Single().Bytes);
        Assert.Equal(99, session.Packets.Single().Timestamp);
    }

    [Fact]
    public void Av1Configuration_IsCombinedWithoutSubmittingAFrameEarly()
    {
        var session = new FakeSession();
        using var decoder = new VaApiVideoDecoder(new(), (_, _) => session);
        decoder.Initialize(VideoCodecId.Av1, 64, 64, _ => { });
        decoder.Decode([0x0a, 1, 0x80], 0);
        Assert.Empty(session.Packets);
        decoder.Decode([0x32, 1, 0x80], 42);
        Assert.Equal(new byte[] { 0x0a, 1, 0x80, 0x32, 1, 0x80 }, session.Packets.Single().Bytes);
        Assert.Equal(42, session.Packets.Single().Timestamp);
    }

    [Fact]
    public void Poll_ForwardsMetadataAndOverrides_AndRejectsReentry()
    {
        var session = new FakeSession();
        session.Frames.Enqueue(Frame(321));
        using var decoder = new VaApiVideoDecoder(new() { ColorRange = VideoColorRange.Full }, (_, _) => session);
        int count = 0;
        decoder.Initialize(VideoCodecId.H264, 64, 64, frame =>
        {
            Assert.Equal(321, frame.TimestampTicks);
            Assert.Equal(VideoColorMatrix.Bt709, frame.ColorMatrix);
            Assert.Equal(VideoColorRange.Full, frame.ColorRange);
            Assert.Throws<InvalidOperationException>(() => decoder.Reset());
            Assert.Throws<InvalidOperationException>(() => decoder.Dispose());
            count++;
        });
        decoder.Poll();
        Assert.Equal(1, count);
    }

    [Fact]
    public void InvalidNativeLayout_FaultsUntilReset_WithoutCopyingPixels()
    {
        var session = new FakeSession();
        var info = Frame(0);
        info.Length--;
        session.Frames.Enqueue(info);
        using var decoder = new VaApiVideoDecoder(new(), (_, _) => session);
        decoder.Initialize(VideoCodecId.H264, 64, 64, _ => throw new Exception("Unexpected callback"));
        Assert.Throws<InvalidDataException>(() => decoder.Poll());
        Assert.Equal(0, session.Copies);
        Assert.Throws<InvalidOperationException>(() => decoder.Poll());
        decoder.Reset();
        decoder.Poll();
        Assert.Equal(1, session.Resets);
    }

    [Fact]
    public void Drain_DeliversRemainingFrame_ThenRequiresReset()
    {
        var session = new FakeSession();
        session.Frames.Enqueue(Frame(20));
        using var decoder = new VaApiVideoDecoder(new(), (_, _) => session);
        int count = 0;
        decoder.Initialize(VideoCodecId.H264, 64, 64, _ => count++);
        decoder.Drain();
        decoder.Drain();
        Assert.Equal(1, count);
        Assert.Single(session.Packets);
        Assert.Throws<InvalidOperationException>(() => decoder.Decode([0, 0, 1, 0x65], 0));
        decoder.Reset();
        decoder.Decode([0, 0, 1, 0x65], 10);
        Assert.Equal(2, session.Packets.Count);
    }

    [Fact]
    public void CodecOpenFailure_CanParticipateInInitializationFallback()
    {
        var hardware = new VaApiVideoDecoder(new(), (_, _) => throw new NotSupportedException("Driver has no VLD profile"));
        var fallbackSession = new FakeSession();
        using var selection = new FallbackVideoDecoder(() => hardware,
            () => new VaApiVideoDecoder(new(), (_, _) => fallbackSession));
        selection.Initialize(VideoCodecId.H265, 64, 64, _ => { });
        Assert.Contains("no VLD profile", selection.FallbackReason);
        Assert.Throws<ObjectDisposedException>(() => hardware.Poll());
    }

    [Fact]
    public void CallbackFailure_ReleasesFrame_AndRequiresReset()
    {
        var session = new FakeSession();
        session.Frames.Enqueue(Frame(0));
        DecodedVideoFrame? borrowed = null;
        using var decoder = new VaApiVideoDecoder(new(), (_, _) => session);
        decoder.Initialize(VideoCodecId.Av1, 64, 64, frame => { borrowed = frame; throw new ArithmeticException(); });
        Assert.Throws<ArithmeticException>(() => decoder.Poll());
        Assert.Throws<ObjectDisposedException>(() => borrowed!.Buffer);
        Assert.Throws<InvalidOperationException>(() => decoder.Poll());
        decoder.Reset();
        decoder.Poll();
    }

    private static VaFrameInfo Frame(long pts) => new()
    {
        Width = 4, Height = 4, Stride = 4, Length = 24, Matrix = 2, Range = 1, Timestamp = pts,
    };

    private sealed class FakeSession : IVaCodecSession
    {
        internal Queue<int> Statuses { get; } = new();
        internal Queue<VaFrameInfo> Frames { get; } = new();
        internal List<(byte[] Bytes, long Timestamp)> Packets { get; } = [];
        internal bool Disposed { get; private set; }
        internal int Copies { get; private set; }
        internal int Resets { get; private set; }
        private bool _ended;
        public int Send(ReadOnlySpan<byte> packet, long timestamp)
        {
            Packets.Add((packet.ToArray(), timestamp));
            _ended = packet.IsEmpty;
            return Statuses.TryDequeue(out int value) ? value : 0;
        }
        public int Receive(out VaFrameInfo info) => Frames.TryDequeue(out info) ? 1 : _ended ? 3 : 0;
        public void Copy(Span<byte> destination) { Copies++; destination.Fill(128); }
        public void Reset() { Resets++; _ended = false; Frames.Clear(); }
        public void Dispose() => Disposed = true;
    }
}
