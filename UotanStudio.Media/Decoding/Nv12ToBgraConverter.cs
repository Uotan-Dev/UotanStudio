using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace UotanStudio.Media.Decoding;

/// <summary>
/// Converts NV12 frames to 32-bit BGRA pixel data for rendering.
/// <para>将 NV12 帧转换为 32 位 BGRA 像素数据以供渲染。</para>
/// </summary>
/// <remarks>
/// <para>
/// Coefficient selection follows the explicit matrix and range arguments; the defaults are
/// BT.601 limited-range. The converter never infers color metadata from a codec, and BT.2020
/// is not among the coefficient sets. Chroma uses nearest-neighbour sampling at half resolution.
/// </para>
/// </remarks>
public static unsafe class Nv12ToBgraConverter
{
    /// <summary>
    /// Computes the required BGRA buffer size for a frame.
    /// <para>计算一帧所需的 BGRA 缓冲区大小。</para>
    /// </summary>
    /// <param name="width">
    /// Frame width in pixels.
    /// <para>帧宽度（像素）。</para>
    /// </param>
    /// <param name="height">
    /// Frame height in pixels.
    /// <para>帧高度（像素）。</para>
    /// </param>
    /// <returns>
    /// The required buffer size in bytes, that is width × height × 4.
    /// <para>所需缓冲区大小（字节），即宽度 × 高度 × 4。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="width"/> or <paramref name="height"/> is not positive.
    /// <para><paramref name="width"/> 或 <paramref name="height"/> 不是正数。</para>
    /// </exception>
    /// <exception cref="OverflowException">
    /// The product of width, height and 4 exceeds <see cref="int"/>.
    /// <para>宽度、高度与 4 的乘积超出 <see cref="int"/> 范围。</para>
    /// </exception>
    public static int GetBgraBufferSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive. 帧尺寸必须为正。");
        }

        return checked(width * height * 4);
    }

    /// <summary>
    /// Converts an NV12 frame to BGRA. Destination must be at least
    /// <see cref="GetBgraBufferSize"/> bytes.
    /// <para>将 NV12 帧转换为 BGRA。目标缓冲区不得小于 <see cref="GetBgraBufferSize"/> 字节。</para>
    /// </summary>
    /// <param name="nv12">
    /// The NV12 source frame, covering both planes at the given stride.
    /// <para>NV12 源帧，须按给定行距完整覆盖两个平面。</para>
    /// </param>
    /// <param name="width">
    /// Frame width in pixels.
    /// <para>帧宽度（像素）。</para>
    /// </param>
    /// <param name="height">
    /// Frame height in pixels.
    /// <para>帧高度（像素）。</para>
    /// </param>
    /// <param name="nv12Stride">
    /// Row pitch in bytes of the NV12 source; must be at least the width.
    /// <para>NV12 源数据的行距（字节），不得小于宽度。</para>
    /// </param>
    /// <param name="destination">
    /// The BGRA destination buffer, written row by row at <paramref name="destinationStride"/>.
    /// <para>BGRA 目标缓冲区，按 <paramref name="destinationStride"/> 逐行写入。</para>
    /// </param>
    /// <param name="destinationStride">
    /// Row pitch in bytes of the destination; pass <c>0</c> to default to width × 4 and must never be less than width × 4.
    /// <para>目标缓冲区的行距（字节）；传 <c>0</c> 表示默认宽度 × 4，且不得小于宽度 × 4。</para>
    /// </param>
    /// <param name="matrix">
    /// YUV matrix selecting the BT.601 or BT.709 coefficient set.
    /// <para>YUV 色彩矩阵，用于选择 BT.601 或 BT.709 系数组。</para>
    /// </param>
    /// <param name="range">
    /// YUV range selecting limited (offset 16, scale 298) or full (offset 0, scale 256) luma handling.
    /// <para>YUV 数值范围，选择有限（偏移 16、缩放 298）或全量（偏移 0、缩放 256）亮度处理。</para>
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Dimensions are not positive, <paramref name="nv12Stride"/> is below the width, or <paramref name="destinationStride"/> is below width × 4.
    /// <para>尺寸非正数、<paramref name="nv12Stride"/> 小于宽度，或 <paramref name="destinationStride"/> 小于宽度 × 4。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="nv12"/> is shorter than the two stride-sized planes, or <paramref name="destination"/> cannot hold every row.
    /// <para><paramref name="nv12"/> 短于按行距计算的两个平面，或 <paramref name="destination"/> 无法容纳全部行。</para>
    /// </exception>
    /// <exception cref="OverflowException">
    /// A stride or size computation exceeds <see cref="int"/>.
    /// <para>行距或尺寸计算超出 <see cref="int"/>。</para>
    /// </exception>
    public static void Convert(ReadOnlySpan<byte> nv12, int width, int height, int nv12Stride, Span<byte> destination, int destinationStride = 0, VideoColorMatrix matrix = VideoColorMatrix.Bt601, VideoColorRange range = VideoColorRange.Limited)
    {
        if (destinationStride == 0)
        {
            destinationStride = checked(width * 4);
        }

        Validate(width, height, nv12Stride, destination.Length, destinationStride);
        int sourceLength = VideoFrameLimits.Nv12Length(width, height, nv12Stride);
        if (nv12.Length < sourceLength)
        {
            throw new ArgumentException("The source does not contain complete NV12 planes.", nameof(nv12));
        }

        fixed (byte* src = nv12)
        fixed (byte* dst = destination)
        {
            ConvertCore(src, dst, width, height, nv12Stride, destinationStride, matrix, range);
        }
    }

    /// <summary>
    /// Span overload taking a <see cref="DecodedVideoFrame"/> directly, converted with the default
    /// BT.601 limited-range coefficients.
    /// <para>直接接收 <see cref="DecodedVideoFrame"/> 的 Span 重载，按默认的 BT.601 有限范围系数转换。</para>
    /// </summary>
    /// <param name="frame">
    /// Decoded NV12 frame supplying pixels, dimensions and stride.
    /// <para>提供像素、尺寸与行距的已解码 NV12 帧。</para>
    /// </param>
    /// <param name="destination">
    /// The BGRA destination buffer.
    /// <para>BGRA 目标缓冲区。</para>
    /// </param>
    /// <param name="destinationStride">
    /// Row pitch in bytes of the destination; pass <c>0</c> to default to width × 4.
    /// <para>目标缓冲区的行距（字节）；传 <c>0</c> 表示默认宽度 × 4。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// The frame span or the destination is too small for the computed layout.
    /// <para>帧跨度或目标缓冲区小于按布局计算所需的大小。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The frame dimensions or strides violate the converter bounds.
    /// <para>帧尺寸或行距超出转换器的边界约束。</para>
    /// </exception>
    public static void Convert(in DecodedVideoFrame frame, Span<byte> destination, int destinationStride = 0)
        => Convert(frame.Span, frame.Width, frame.Height, frame.Stride, destination, destinationStride);

    private static void Validate(int width, int height, int nv12Stride, int destinationLength, int destinationStride)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive. 帧尺寸必须为正。");
        }

        if (nv12Stride < width)
        {
            throw new ArgumentOutOfRangeException(nameof(nv12Stride), "NV12 stride must be at least the width. NV12 行距不得小于宽度。");
        }

        if (destinationStride < checked(width * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(destinationStride), "Destination stride must be at least width × 4. 目标行距不得小于宽度 × 4。");
        }

        var required = checked((height - 1) * destinationStride) + checked(width * 4);
        if (destinationLength < required)
        {
            throw new ArgumentException(
                $"Destination buffer too small: requires {required} bytes. 目标缓冲区过小：需要 {required} 字节。",
                nameof(destinationLength));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertCore(byte* src, byte* dst, int width, int height, int nv12Stride, int dstStride, VideoColorMatrix matrix, VideoColorRange range)
    {
        bool full = range == VideoColorRange.Full;
        bool hd = matrix == VideoColorMatrix.Bt709;
        int yOffset = full ? 0 : 16, yScale = full ? 256 : 298;
        int red = hd ? (full ? 403 : 459) : (full ? 359 : 409);
        int greenU = hd ? (full ? 48 : 55) : (full ? 88 : 100);
        int greenV = hd ? (full ? 120 : 136) : (full ? 183 : 208);
        int blue = hd ? (full ? 475 : 541) : (full ? 454 : 516);
        var yPlane = src;
        var uvPlane = src + (long)nv12Stride * height;
        var uvStride = nv12Stride;

        for (var row = 0; row < height; row++)
        {
            var yRow = yPlane + (long)row * nv12Stride;
            var uvRow = uvPlane + (long)(row >> 1) * uvStride;
            var dstRow = dst + (long)row * dstStride;

            int col = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                for (; col <= width - 4; col += 4)
                {
                    var y = Vector128.Create((int)yRow[col], yRow[col + 1], yRow[col + 2], yRow[col + 3]);
                    var d = Vector128.Create((int)uvRow[col], uvRow[col], uvRow[col + 2], uvRow[col + 2]) - Vector128.Create(128);
                    var e = Vector128.Create((int)uvRow[col + 1], uvRow[col + 1], uvRow[col + 3], uvRow[col + 3]) - Vector128.Create(128);
                    var c = (y - Vector128.Create(yOffset)) * Vector128.Create(yScale) + Vector128.Create(128);
                    var r = Vector128.ShiftRightArithmetic(c + e * Vector128.Create(red), 8);
                    var g = Vector128.ShiftRightArithmetic(c - d * Vector128.Create(greenU) - e * Vector128.Create(greenV), 8);
                    var b = Vector128.ShiftRightArithmetic(c + d * Vector128.Create(blue), 8);
                    r = Vector128.Min(Vector128.Max(r, Vector128<int>.Zero), Vector128.Create(255));
                    g = Vector128.Min(Vector128.Max(g, Vector128<int>.Zero), Vector128.Create(255));
                    b = Vector128.Min(Vector128.Max(b, Vector128<int>.Zero), Vector128.Create(255));
                    var pixels = b | Vector128.ShiftLeft(g, 8) | Vector128.ShiftLeft(r, 16) | Vector128.Create(unchecked((int)0xff000000));
                    pixels.Store((int*)(dstRow + col * 4));
                }
            }

            for (; col < width; col += 2)
            {
                var uvIndex = col & ~1;
                var u = uvRow[uvIndex];
                var v = uvRow[uvIndex + 1];
                var d = u - 128;
                var e = v - 128;

                for (var pixel = col; pixel < Math.Min(col + 2, width); pixel++)
                {
                    var c = yRow[pixel] - yOffset;

                    var r = (yScale * c + red * e + 128) >> 8;
                    var g = (yScale * c - greenU * d - greenV * e + 128) >> 8;
                    var b = (yScale * c + blue * d + 128) >> 8;

                    dstRow[pixel * 4 + 0] = Clamp(b);
                    dstRow[pixel * 4 + 1] = Clamp(g);
                    dstRow[pixel * 4 + 2] = Clamp(r);
                    dstRow[pixel * 4 + 3] = 255;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clamp(int value)
        => (byte)(value < 0 ? 0 : (value > 255 ? 255 : value));
}