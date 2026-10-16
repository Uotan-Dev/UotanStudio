namespace UotanStudio.Media.Decoding;

/// <summary>Shared allocation and layout limits for native video boundaries.</summary>
internal static class VideoFrameLimits
{
    internal const int MaximumFrameBytes = 256 * 1024 * 1024;
    internal const int MaximumAccessUnitBytes = 32 * 1024 * 1024;

    internal static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384
            || (long)width * height * 4 > MaximumFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(width), "Unsupported or excessive frame dimensions.");
    }

    internal static int Nv12Length(int width, int height, int stride)
    {
        ValidateDimensions(width, height);
        if (stride < checked((width + 1) & ~1)) throw new ArgumentOutOfRangeException(nameof(stride));
        var length = (long)stride * (height + (height + 1) / 2);
        if (length > MaximumFrameBytes) throw new ArgumentOutOfRangeException(nameof(stride));
        return checked((int)length);
    }

    internal static void ValidateAccessUnit(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length > MaximumAccessUnitBytes)
            throw new ArgumentOutOfRangeException(nameof(data), "Access units must contain 1 byte to 32 MiB.");
    }
}