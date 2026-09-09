namespace UotanStudio.Media.Decoding;

/// <summary>Creates AOT-safe platform decoders and probes native codec availability.</summary>
public static class VideoDecoderFactory
{
    /// <summary>
    /// Checks whether a backend accepts a small stream configuration (Windows) or advertises
    /// hardware codec support (macOS). Actual stream profile, dimensions and decoding can still fail.
    /// Linux VA/EGL primitives do not yet provide an Annex-B parser/DPB and are not advertised here.
    /// </summary>
    public static bool IsSupported(VideoCodecId codec)
    {
        if (codec is not (VideoCodecId.H264 or VideoCodecId.H265)) return false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var decoder = new MediaFoundation.MediaFoundationVideoDecoder();
                decoder.Initialize(codec, 64, 64, static _ => { });
                return true;
            }
            if (OperatingSystem.IsMacOS())
                return VideoToolbox.VtNative.VTIsHardwareDecodeSupported(VideoToolbox.VtNative.Codec(codec)) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
        return false;
    }

    /// <summary>Creates an uninitialized platform decoder. Windows currently uses synchronous CPU-readable MFT output.</summary>
    public static IVideoDecoder Create()
    {
        if (OperatingSystem.IsWindows()) return new MediaFoundation.MediaFoundationVideoDecoder();
        if (OperatingSystem.IsMacOS()) return new VideoToolbox.VideoToolboxVideoDecoder();
        throw new NotSupportedException("An Annex-B video decoder is not available on this platform. Linux VA-API/EGL primitives require a codec parser and reference-picture manager.");
    }
}
