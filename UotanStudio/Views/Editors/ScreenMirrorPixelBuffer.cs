using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;

namespace UotanStudio.Views.Editors;

/// <summary>Owns one reusable, tightly packed BGRA copy between decoder and UI updates.</summary>
/// <remarks>The caller must serialize updates and reads until the bitmap copy completes.</remarks>
internal sealed class ScreenMirrorPixelBuffer
{
    internal byte[] Pixels { get; private set; } = [];
    internal int Width { get; private set; }
    internal int Height { get; private set; }

    internal void Update(DecodedVideoFrame frame, VideoColorMatrix matrix, VideoColorRange range)
    {
        int length = Nv12ToBgraConverter.GetBgraBufferSize(frame.Width, frame.Height);
        if (Pixels.Length != length) Pixels = GC.AllocateUninitializedArray<byte>(length);
        if (frame is GpuDecodedVideoFrame)
        {
            int rowBytes = checked(frame.Width * 4);
            for (int row = 0; row < frame.Height; row++)
                frame.Span.Slice(row * frame.Stride, rowBytes).CopyTo(Pixels.AsSpan(row * rowBytes, rowBytes));
        }
        else
        {
            Nv12ToBgraConverter.Convert(frame.Span, frame.Width, frame.Height, frame.Stride, Pixels,
                matrix: matrix == VideoColorMatrix.Auto ? frame.ColorMatrix : matrix,
                range: range == VideoColorRange.Auto ? frame.ColorRange : range);
        }
        Width = frame.Width;
        Height = frame.Height;
    }
}
