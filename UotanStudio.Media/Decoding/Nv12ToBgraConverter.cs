using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace UotanStudio.Media.Decoding;

/// <summary>
/// Converts NV12 frames to 32-bit BGRA pixel data for rendering.
/// <para>将 NV12 帧转换为 32 位 BGRA 像素数据以供渲染。</para>
/// </summary>
/// <remarks>
/// <para>
/// Coefficient selection follows the explicit matrix and range arguments; the defaults are
/// BT.601 limited-range. The converter never infers color metadata from a codec, and BT.2020
/// is not among the coefficient sets. Chroma is sampled bilinearly in both axes at half
/// resolution, matching the GPU shader path.
/// <para>
/// 系数选择遵循显式的矩阵与范围参数；默认 BT.601 有限范围。转换器不会从编码推断色彩元数据，
/// BT.2020 不在系数集之内。色度在半分辨率上按双轴双线性采样，与 GPU 着色器路径一致。
/// </para>
/// </para>
/// </remarks>
public static unsafe class Nv12ToBgraConverter
{
    /// <summary>Shuffle mask that gathers the even chroma bytes (Cb) into the low half of a register.
    /// <para>把偶数位置的色度字节（Cb）收集到寄存器低半部的洗牌掩码。</para></summary>
    private static readonly Vector128<byte> EvenBytesMask = Vector128.Create(
        (byte)0, 2, 4, 6, 8, 10, 12, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

    /// <summary>Shuffle mask that gathers the odd chroma bytes (Cr) into the low half of a register.
    /// <para>把奇数位置的色度字节（Cr）收集到寄存器低半部的洗牌掩码。</para></summary>
    private static readonly Vector128<byte> OddBytesMask = Vector128.Create(
        (byte)1, 3, 5, 7, 9, 11, 13, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

    /// <summary>Signed <c>3:1</c> weights that turn <c>pmaddubsw</c> into the horizontal chroma interpolation.
    /// <para>把 <c>pmaddubsw</c> 变成水平色度插值的带符号 <c>3:1</c> 权重。</para></summary>
    private static readonly Vector128<sbyte> HorizontalWeights = Vector128.Create(
        (sbyte)3, 1, 3, 1, 3, 1, 3, 1, 3, 1, 3, 1, 3, 1, 3, 1);

    /// <summary>Shuffle mask that duplicates each of four chroma cells into a lane pair.
    /// <para>把四个色度单元各自复制成一对通道的洗牌掩码。</para></summary>
    private static readonly Vector128<byte> ChromaPairMask = Vector128.Create(
        (byte)0, 0, 1, 1, 2, 2, 3, 3, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

    /// <summary>Computes the required BGRA buffer size for a frame.
    /// <para>计算一帧所需的 BGRA 缓冲区大小。</para></summary>
    /// <param name="width">Frame width in pixels.<para>帧宽度（像素）。</para></param>
    /// <param name="height">Frame height in pixels.<para>帧高度（像素）。</para></param>
    /// <returns>The required buffer size in bytes, that is width × height × 4.
    /// <para>所需缓冲区大小（字节），即宽度 × 高度 × 4。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.
    /// <para><paramref name="width"/> 或 <paramref name="height"/> 不是正数。</para></exception>
    /// <exception cref="OverflowException">The product of width, height and 4 exceeds <see cref="int"/>.
    /// <para>宽度、高度与 4 的乘积超出 <see cref="int"/> 范围。</para></exception>
    public static int GetBgraBufferSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive. 帧尺寸必须为正。");
        }

        return checked(width * height * 4);
    }

    /// <summary>Converts an NV12 frame to BGRA. Destination must be at least <see cref="GetBgraBufferSize"/> bytes.
    /// <para>将 NV12 帧转换为 BGRA。目标缓冲区不得小于 <see cref="GetBgraBufferSize"/> 字节。</para></summary>
    /// <param name="nv12">The NV12 source frame, covering both planes at the given stride.
    /// <para>NV12 源帧，须按给定行距完整覆盖两个平面。</para></param>
    /// <param name="width">Frame width in pixels.<para>帧宽度（像素）。</para></param>
    /// <param name="height">Frame height in pixels.<para>帧高度（像素）。</para></param>
    /// <param name="nv12Stride">Row pitch in bytes of the NV12 source; must be at least the width.
    /// <para>NV12 源数据的行距（字节），不得小于宽度。</para></param>
    /// <param name="destination">The BGRA destination buffer, written row by row at <paramref name="destinationStride"/>.
    /// <para>BGRA 目标缓冲区，按 <paramref name="destinationStride"/> 逐行写入。</para></param>
    /// <param name="destinationStride">Row pitch in bytes of the destination; pass <c>0</c> to default to width × 4 and must never be less than width × 4.
    /// <para>目标缓冲区的行距（字节）；传 <c>0</c> 表示默认宽度 × 4，且不得小于宽度 × 4。</para></param>
    /// <param name="matrix">YUV matrix selecting the BT.601 or BT.709 coefficient set.
    /// <para>YUV 色彩矩阵，用于选择 BT.601 或 BT.709 系数组。</para></param>
    /// <param name="range">YUV range selecting limited (offset 16, scale 298) or full (offset 0, scale 256) luma handling.
    /// <para>YUV 数值范围，选择有限（偏移 16、缩放 298）或全量（偏移 0、缩放 256）亮度处理。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">Dimensions are not positive, <paramref name="nv12Stride"/> is below the width, or <paramref name="destinationStride"/> is below width × 4.
    /// <para>尺寸非正数、<paramref name="nv12Stride"/> 小于宽度，或 <paramref name="destinationStride"/> 小于宽度 × 4。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="nv12"/> is shorter than the two stride-sized planes, or <paramref name="destination"/> cannot hold every row.
    /// <para><paramref name="nv12"/> 短于按行距计算的两个平面，或 <paramref name="destination"/> 无法容纳全部行。</para></exception>
    /// <exception cref="OverflowException">A stride or size computation exceeds <see cref="int"/>.
    /// <para>行距或尺寸计算超出 <see cref="int"/>。</para></exception>
    public static void Convert(ReadOnlySpan<byte> nv12, int width, int height, int nv12Stride, Span<byte> destination, int destinationStride = 0, VideoColorMatrix matrix = VideoColorMatrix.Bt601, VideoColorRange range = VideoColorRange.Limited)
    {
        if (destinationStride == 0)
        {
            destinationStride = checked(width * 4);
        }

        Validate(width, height, nv12Stride, destination.Length, destinationStride);
        var sourceLength = VideoFrameLimits.Nv12Length(width, height, nv12Stride);
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

    /// <summary>Span overload taking a <see cref="DecodedVideoFrame"/> directly, converted with the default BT.601 limited-range coefficients.
    /// <para>直接接收 <see cref="DecodedVideoFrame"/> 的 Span 重载，按默认的 BT.601 有限范围系数转换。</para></summary>
    /// <param name="frame">Decoded NV12 frame supplying pixels, dimensions and stride.
    /// <para>提供像素、尺寸与行距的已解码 NV12 帧。</para></param>
    /// <param name="destination">The BGRA destination buffer.<para>BGRA 目标缓冲区。</para></param>
    /// <param name="destinationStride">Row pitch in bytes of the destination; pass <c>0</c> to default to width × 4.
    /// <para>目标缓冲区的行距（字节）；传 <c>0</c> 表示默认宽度 × 4。</para></param>
    /// <exception cref="ArgumentException">The frame span or the destination is too small for the computed layout, or the frame does not carry <see cref="VideoPixelFormat.Nv12"/> planes.
    /// <para>帧跨度或目标缓冲区小于按布局计算所需的大小，或该帧并非 <see cref="VideoPixelFormat.Nv12"/> 平面布局。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The frame dimensions or strides violate the converter bounds.
    /// <para>帧尺寸或行距超出转换器的边界约束。</para></exception>
    public static void Convert(in DecodedVideoFrame frame, Span<byte> destination, int destinationStride = 0)
    {
        if (frame.PixelFormat != VideoPixelFormat.Nv12)
        {
            throw new ArgumentException($"The converter reads NV12 planes; the frame carries {frame.PixelFormat}.", nameof(frame));
        }

        Convert(frame.Span, frame.Width, frame.Height, frame.Stride, destination, destinationStride);
    }

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
        var coefficients = Nv12ConversionTable.For(matrix, range);
        int yOffset = coefficients.LumaOffset, yScale = coefficients.LumaScale;
        var red = coefficients.RedFromChromaRed;
        var greenU = -coefficients.GreenFromChromaBlue;
        var greenV = -coefficients.GreenFromChromaRed;
        var blue = coefficients.BlueFromChromaBlue;
        var yPlane = src;
        var uvPlane = src + (long)nv12Stride * height;
        var chromaWidth = (width + 1) >> 1;
        var chromaRows = (height + 1) >> 1;
        var sliceStride = chromaWidth + 16;
        var yOffset128 = Vector128.Create(yOffset);
        var yScale128 = Vector128.Create(yScale);
        var red128 = Vector128.Create(red);
        var greenU128 = Vector128.Create(greenU);
        var greenV128 = Vector128.Create(greenV);
        var blue128 = Vector128.Create(blue);
        var yOffset256 = Vector256.Create(yOffset);
        var yScale256 = Vector256.Create(yScale);
        var red256 = Vector256.Create(red);
        var greenU256 = Vector256.Create(greenU);
        var greenV256 = Vector256.Create(greenV);
        var blue256 = Vector256.Create(blue);
        var bias128 = Vector128.Create(128);
        var bias256 = Vector256.Create(128);
        var alpha256 = Vector256.Create(unchecked((int)0xff000000));
        var scratch = ArrayPool<byte>.Shared.Rent(checked(sliceStride * 6));
        try
        {
            fixed (byte* scratchPointer = scratch)
            {
                var upperCb = scratchPointer;
                var upperCr = scratchPointer + sliceStride;
                var lowerCb = scratchPointer + sliceStride * 2;
                var lowerCr = scratchPointer + sliceStride * 3;
                var blendedCb = scratchPointer + sliceStride * 4;
                var blendedCr = scratchPointer + sliceStride * 5;
                var cachedUpper = -1;
                var cachedLower = -1;

                for (var row = 0; row < height; row++)
                {
                    var yRow = yPlane + (long)row * nv12Stride;
                    var chromaRow = row >> 1;
                    if (cachedUpper != chromaRow)
                    {
                        if (cachedLower == chromaRow)
                        {
                            var swapCb = upperCb;
                            upperCb = lowerCb;
                            lowerCb = swapCb;
                            var swapCr = upperCr;
                            upperCr = lowerCr;
                            lowerCr = swapCr;
                            (cachedUpper, cachedLower) = (chromaRow, cachedUpper);
                        }
                        else
                        {
                            PrecomputeChromaRow(uvPlane + (long)chromaRow * nv12Stride, upperCb, upperCr, chromaWidth, nv12Stride);
                            cachedUpper = chromaRow;
                        }
                    }

                    var rowCb = upperCb;
                    var rowCr = upperCr;
                    if ((row & 1) != 0)
                    {
                        var nextChromaRow = chromaRow + 1 < chromaRows ? chromaRow + 1 : chromaRow;
                        if (cachedLower != nextChromaRow)
                        {
                            PrecomputeChromaRow(uvPlane + (long)nextChromaRow * nv12Stride, lowerCb, lowerCr, chromaWidth, nv12Stride);
                            cachedLower = nextChromaRow;
                        }
                        BlendChromaRows(upperCb, lowerCb, blendedCb, chromaWidth);
                        BlendChromaRows(upperCr, lowerCr, blendedCr, chromaWidth);
                        rowCb = blendedCb;
                        rowCr = blendedCr;
                    }

                    var dstRow = dst + (long)row * dstStride;
                    var col = 0;
                    if (Avx2.IsSupported)
                    {
                        for (; col <= width - 8; col += 8)
                        {
                            var y = Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<byte>>(yRow + col));
                            var cell = col >> 1;
                            var d = ExpandChroma(rowCb + cell) - bias256;
                            var e = ExpandChroma(rowCr + cell) - bias256;
                            var c = (y - yOffset256) * yScale256 + bias256;
                            var r = Vector256.ShiftRightArithmetic(c + e * red256, 8);
                            var g = Vector256.ShiftRightArithmetic(c - d * greenU256 - e * greenV256, 8);
                            var b = Vector256.ShiftRightArithmetic(c + d * blue256, 8);
                            r = Vector256.Min(Vector256.Max(r, Vector256<int>.Zero), Vector256.Create(255));
                            g = Vector256.Min(Vector256.Max(g, Vector256<int>.Zero), Vector256.Create(255));
                            b = Vector256.Min(Vector256.Max(b, Vector256<int>.Zero), Vector256.Create(255));
                            var pixels = b | Vector256.ShiftLeft(g, 8) | Vector256.ShiftLeft(r, 16) | alpha256;
                            pixels.Store((int*)(dstRow + col * 4));
                        }
                    }

                    if (Vector128.IsHardwareAccelerated)
                    {
                        for (; col <= width - 4; col += 4)
                        {
                            var y = Vector128.Create((int)yRow[col], yRow[col + 1], yRow[col + 2], yRow[col + 3]);
                            var cell = col >> 1;
                            var d0 = rowCb[cell] - 128;
                            var d1 = rowCb[cell + 1] - 128;
                            var e0 = rowCr[cell] - 128;
                            var e1 = rowCr[cell + 1] - 128;
                            var d = Vector128.Create(d0, d0, d1, d1);
                            var e = Vector128.Create(e0, e0, e1, e1);
                            var c = (y - yOffset128) * yScale128 + bias128;
                            var r = Vector128.ShiftRightArithmetic(c + e * red128, 8);
                            var g = Vector128.ShiftRightArithmetic(c - d * greenU128 - e * greenV128, 8);
                            var b = Vector128.ShiftRightArithmetic(c + d * blue128, 8);
                            r = Vector128.Min(Vector128.Max(r, Vector128<int>.Zero), Vector128.Create(255));
                            g = Vector128.Min(Vector128.Max(g, Vector128<int>.Zero), Vector128.Create(255));
                            b = Vector128.Min(Vector128.Max(b, Vector128<int>.Zero), Vector128.Create(255));
                            var pixels = b | Vector128.ShiftLeft(g, 8) | Vector128.ShiftLeft(r, 16) | Vector128.Create(unchecked((int)0xff000000));
                            pixels.Store((int*)(dstRow + col * 4));
                        }
                    }

                    for (; col < width; col += 2)
                    {
                        var cell = col >> 1;
                        var d = rowCb[cell] - 128;
                        var e = rowCr[cell] - 128;

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
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Precomputes the horizontal <c>3:1</c> chroma interpolation of one chroma row into per-cell Cb and Cr values.
    /// <para>把一条色度行的水平 <c>3:1</c> 插值预计算成每个色度单元的 Cb 与 Cr 值。</para>
    /// </summary>
    /// <param name="row">Interleaved Cb/Cr chroma row. <para>交错的 Cb/Cr 色度行。</para></param>
    /// <param name="destinationCb">Cb value per cell. <para>每个色度单元的 Cb 值。</para></param>
    /// <param name="destinationCr">Cr value per cell. <para>每个色度单元的 Cr 值。</para></param>
    /// <param name="chromaWidth">Number of chroma cells to produce. <para>要生成的色度单元数。</para></param>
    /// <param name="rowStride">Row pitch in bytes of the chroma plane, used to bound the vector reads.
    /// <para>色度平面的行距（字节），用于界定向量读取范围。</para></param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void PrecomputeChromaRow(byte* row, byte* destinationCb, byte* destinationCr, int chromaWidth, int rowStride)
    {
        var cell = 0;
        if (Ssse3.IsSupported)
        {
            var vectorCells = chromaWidth - 9;
            var pitchCells = (rowStride - 18) >> 1;
            if (pitchCells < vectorCells)
            {
                vectorCells = pitchCells;
            }

            for (; cell <= vectorCells; cell += 8)
            {
                var current = Unsafe.ReadUnaligned<Vector128<byte>>(row + cell * 2);
                var next = Unsafe.ReadUnaligned<Vector128<byte>>(row + cell * 2 + 2);
                StoreHorizontal(destinationCb + cell, Ssse3.Shuffle(current, EvenBytesMask), Ssse3.Shuffle(next, EvenBytesMask));
                StoreHorizontal(destinationCr + cell, Ssse3.Shuffle(current, OddBytesMask), Ssse3.Shuffle(next, OddBytesMask));
            }
        }

        var last = chromaWidth - 1;
        for (; cell < chromaWidth; cell++)
        {
            var next = cell < last ? cell + 1 : cell;
            destinationCb[cell] = (byte)((row[cell * 2] * 3 + row[next * 2] + 2) >> 2);
            destinationCr[cell] = (byte)((row[cell * 2 + 1] * 3 + row[next * 2 + 1] + 2) >> 2);
        }
    }

    /// <summary>
    /// Weights eight adjacent cell pairs with <c>pmaddubsw</c>, then rounds and packs them into eight bytes.
    /// <para>用 <c>pmaddubsw</c> 对八对相邻色度单元加权，再取整并打包成八个字节。</para>
    /// </summary>
    /// <param name="destination">Cell destination; the first eight bytes are valid and the upper half is scratch.
    /// <para>单元目标地址；前八个字节有效，上半部为临时数据。</para></param>
    /// <param name="current">Eight cells starting at the current cell. <para>从当前单元开始的八个色度字节。</para></param>
    /// <param name="next">Eight cells starting at the next cell. <para>从后一单元开始的八个色度字节。</para></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreHorizontal(byte* destination, Vector128<byte> current, Vector128<byte> next)
    {
        var pairs = Sse2.UnpackLow(current, next);
        var scaled = Ssse3.MultiplyAddAdjacent(pairs, HorizontalWeights);
        var rounded = Sse2.ShiftRightArithmetic(Sse2.Add(scaled, Vector128.Create((short)2)), 2);
        Sse2.PackUnsignedSaturate(rounded, rounded).Store(destination);
    }

    /// <summary>
    /// Blends two horizontally precomputed chroma rows with the odd-row <c>1:1</c> vertical mix.
    /// <para>按奇数行的 <c>1:1</c> 垂直混合对两条已预计算的水平色度行取平均。</para>
    /// </summary>
    /// <param name="upper">Horizontally precomputed row of the current chroma row. <para>当前色度行的水平预计算结果。</para></param>
    /// <param name="lower">Horizontally precomputed row of the next chroma row. <para>下一条色度行的水平预计算结果。</para></param>
    /// <param name="destination">Blended value per cell. <para>每个色度单元的混合结果。</para></param>
    /// <param name="chromaWidth">Number of chroma cells in both rows. <para>两条行的色度单元数。</para></param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BlendChromaRows(byte* upper, byte* lower, byte* destination, int chromaWidth)
    {
        var cell = 0;
        if (Sse2.IsSupported)
        {
            for (; cell + 16 <= chromaWidth; cell += 16)
            {
                Sse2.Average(Unsafe.ReadUnaligned<Vector128<byte>>(upper + cell), Unsafe.ReadUnaligned<Vector128<byte>>(lower + cell))
                    .Store(destination + cell);
            }
        }

        for (; cell < chromaWidth; cell++)
        {
            destination[cell] = (byte)((upper[cell] + lower[cell] + 1) >> 1);
        }
    }

    /// <summary>
    /// Loads four precomputed chroma cells and duplicates each value into a pair of 32-bit lanes.
    /// <para>读取四个预计算的色度单元，并把每个值复制成一对 32 位通道。</para>
    /// </summary>
    /// <param name="source">Precomputed cells; four bytes are read. <para>预计算单元；读取四个字节。</para></param>
    /// <returns>Eight lanes holding each cell value twice, in pixel order.
    /// <para>八个通道，按像素顺序把每个单元值重复两次。</para></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> ExpandChroma(byte* source)
    {
        var duplicated = Ssse3.Shuffle(Vector128.Create(Unsafe.ReadUnaligned<uint>(source)).AsByte(), ChromaPairMask);
        return Avx2.ConvertToVector256Int32(duplicated);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clamp(int value)
        => (byte)(value < 0 ? 0 : (value > 255 ? 255 : value));
}