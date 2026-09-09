using UotanStudio.Media.Decoding;
using Xunit;

namespace UotanStudio.Tests.Media;

public class FallbackVideoDecoderTests
{
    [Fact]
    public void InitializationFailure_ReleasesGpuBeforeCpu_AndForwardsOperations()
    {
        var gpu = new StubDecoder { FailInitialization = true };
        var cpu = new StubDecoder();
        using var decoder = new FallbackVideoDecoder(() => gpu, () =>
        {
            Assert.True(gpu.Disposed);
            return cpu;
        });
        decoder.Initialize(VideoCodecId.H264, 64, 64, _ => { });
        Assert.Same(cpu, decoder.ActiveDecoder);
        Assert.NotNull(VideoDecoderFactory.GetFallbackReason(decoder));
        decoder.Decode([1], 1234);
        decoder.Drain();
        decoder.Reset();
        Assert.Equal(1234, cpu.Timestamp);
        Assert.Equal(1, cpu.Drains);
        Assert.Equal(1, cpu.Resets);
        decoder.Dispose();
        decoder.Dispose();
        Assert.True(cpu.Disposed);
        Assert.False(decoder.IsInitialized);
        Assert.Throws<ObjectDisposedException>(() => decoder.Reset());
    }

    [Fact]
    public void SuccessfulGpu_DoesNotCreateCpu_OrFallbackOnDecodeFailure()
    {
        var gpu = new StubDecoder { FailDecode = true };
        using var decoder = new FallbackVideoDecoder(() => gpu, () => throw new Exception("Unexpected CPU creation"));
        decoder.Initialize(VideoCodecId.H264, 64, 64, _ => { });
        Assert.Same(gpu, decoder.ActiveDecoder);
        Assert.Null(VideoDecoderFactory.GetFallbackReason(decoder));
        Assert.Throws<InvalidOperationException>(() => decoder.Decode([1]));
    }

    [Fact]
    public void FailedBackends_AreReleased_AndBothErrorsArePreserved()
    {
        var gpu = new StubDecoder { FailInitialization = true };
        var cpu = new StubDecoder { FailInitialization = true };
        using var decoder = new FallbackVideoDecoder(() => gpu, () => cpu);
        var error = Assert.Throws<InvalidOperationException>(() => decoder.Initialize(VideoCodecId.H264, 64, 64, _ => { }));
        Assert.IsType<AggregateException>(error.InnerException);
        Assert.True(gpu.Disposed);
        Assert.True(cpu.Disposed);
        Assert.False(decoder.IsInitialized);
    }

    [Fact]
    public void Reinitialize_RetriesGpu_AndClearsPreviousFallback()
    {
        var first = new StubDecoder { FailInitialization = true };
        var second = new StubDecoder();
        var cpu = new StubDecoder();
        int attempts = 0;
        using var decoder = new FallbackVideoDecoder(() => ++attempts == 1 ? first : second, () => cpu);
        decoder.Initialize(VideoCodecId.H264, 64, 64, _ => { });
        decoder.Initialize(VideoCodecId.H264, 176, 144, _ => { });
        Assert.True(cpu.Disposed);
        Assert.Same(second, decoder.ActiveDecoder);
        Assert.Null(decoder.FallbackReason);
    }

    [Fact]
    public void InvalidArguments_DoNotCreateBackends()
    {
        using var decoder = new FallbackVideoDecoder(() => throw new Exception("Unexpected creation"), () => throw new Exception("Unexpected creation"));
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Initialize(VideoCodecId.H264, 0, 64, _ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Initialize((VideoCodecId)999, 64, 64, _ => { }));
        Assert.Throws<ArgumentNullException>(() => decoder.Initialize(VideoCodecId.H264, 64, 64, null!));
    }

    private sealed class StubDecoder : IVideoDecoder
    {
        public bool FailInitialization { get; init; }
        public bool FailDecode { get; init; }
        public bool Disposed { get; private set; }
        public bool IsInitialized { get; private set; }
        public long Timestamp { get; private set; }
        public int Drains { get; private set; }
        public int Resets { get; private set; }
        public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback)
        {
            if (FailInitialization) throw new NotSupportedException("Backend unavailable");
            IsInitialized = true;
        }
        public void Decode(ReadOnlySpan<byte> accessUnit) => Decode(accessUnit, 0);
        public void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks)
        {
            if (FailDecode) throw new InvalidOperationException("Bad stream");
            Timestamp = timestampTicks;
        }
        public void Drain() => Drains++;
        public void Reset() => Resets++;
        public void Dispose() { Disposed = true; IsInitialized = false; }
    }
}