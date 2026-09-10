namespace UotanStudio.Media.Decoding;

/// <summary>Chooses the preferred backend at initialization, releasing it before trying the fallback.</summary>
internal sealed class FallbackVideoDecoder(Func<IVideoDecoder> preferred, Func<IVideoDecoder> fallback) : IVideoDecoder
{
    private IVideoDecoder? _decoder;
    private bool _disposed;

    internal IVideoDecoder? ActiveDecoder => _decoder;
    internal string? FallbackReason { get; private set; }
    public bool IsInitialized => _decoder?.IsInitialized == true;

    public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrameLimits.ValidateDimensions(width, height);
        ArgumentNullException.ThrowIfNull(frameCallback);
        if (codec is not (VideoCodecId.H264 or VideoCodecId.H265 or VideoCodecId.Av1)) throw new ArgumentOutOfRangeException(nameof(codec));
        _decoder?.Dispose();
        _decoder = null;
        FallbackReason = null;
        try
        {
            InitializeBackend(preferred, codec, width, height, frameCallback);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException or InvalidOperationException)
        {
            FallbackReason = error.Message;
            try { InitializeBackend(fallback, codec, width, height, frameCallback); }
            catch (Exception fallbackError)
            {
                throw new InvalidOperationException($"Preferred backend initialization failed: {error.Message} Fallback initialization failed: {fallbackError.Message}",
                    new AggregateException(error, fallbackError));
            }
        }
    }

    private void InitializeBackend(Func<IVideoDecoder> factory, VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> callback)
    {
        var candidate = factory();
        try { candidate.Initialize(codec, width, height, callback); }
        catch { candidate.Dispose(); throw; }
        _decoder = candidate;
    }

    private IVideoDecoder Decoder
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _decoder ?? throw new InvalidOperationException("Decoder is not initialized.");
        }
    }

    public void Decode(ReadOnlySpan<byte> accessUnit) => Decoder.Decode(accessUnit);
    public void Decode(ReadOnlySpan<byte> accessUnit, long timestampTicks) => Decoder.Decode(accessUnit, timestampTicks);
    public void Drain() => Decoder.Drain();
    public void Poll() => Decoder.Poll();
    public void Reset() => Decoder.Reset();
    public void Dispose()
    {
        if (_disposed) return;
        _decoder?.Dispose();
        _decoder = null;
        _disposed = true;
    }
}
