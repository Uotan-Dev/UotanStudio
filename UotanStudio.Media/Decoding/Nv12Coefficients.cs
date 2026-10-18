namespace UotanStudio.Media.Decoding;

/// <summary>
/// Fixed-point NV12 to BGRA conversion constants for one matrix and range pair, shared by the CPU
/// converter and the GPU shader so both cannot drift.
/// <para>某一矩阵与范围组合下的 NV12 转 BGRA 定点常量，供 CPU 转换器与 GPU 着色器共用，避免两者漂移。</para>
/// </summary>
/// <remarks>
/// <para>
/// Chroma terms are signed and scaled by 256, derived from the ITU recommendation luma coefficients as
/// <c>2 * (1 - Kr)</c> for red, <c>2 * (1 - Kb)</c> for blue, and
/// <c>-2 * Kb * (1 - Kb) / (1 - Kt)</c> and <c>-2 * Kr * (1 - Kr) / (1 - Kt)</c> for the two green terms.
/// Limited range applies the <c>255 / 219</c> luma expansion and the <c>255 / 224</c> chroma expansion, so
/// the stored luma scale of 298 and the chroma terms are rounded independently. BT.601 uses Kr = 0.299 and
/// Kb = 0.114, BT.709 uses 0.2126 and 0.0722, and BT.2020 uses 0.2627 and 0.0593.
/// </para>
/// <para>
/// 色度项为带符号值并已乘 256：红色取 <c>2 * (1 - Kr)</c>，蓝色取 <c>2 * (1 - Kb)</c>，两个绿色项分别取
/// <c>-2 * Kb * (1 - Kb) / (1 - Kt)</c> 与 <c>-2 * Kr * (1 - Kr) / (1 - Kt)</c>。有限范围对亮度施加
/// <c>255 / 219</c> 扩展、对色度施加 <c>255 / 224</c> 扩展，因此存储的亮度比例 298 与色度项是各自独立取整的。
/// BT.601 使用 Kr = 0.299、Kb = 0.114，BT.709 使用 0.2126 与 0.0722，BT.2020 使用 0.2627 与 0.0593。
/// </para>
/// </remarks>
/// <param name="LumaOffset">
/// Value subtracted from luma before scaling: <c>0</c> for full range, <c>16</c> for limited range.
/// <para>缩放前从亮度中减去的值：全范围为 <c>0</c>，有限范围为 <c>16</c>。</para>
/// </param>
/// <param name="LumaScale">
/// Luma multiplier scaled by 256: <c>256</c> for full range, <c>298</c> for limited range.
/// <para>已乘 256 的亮度乘数：全范围为 <c>256</c>，有限范围为 <c>298</c>。</para>
/// </param>
/// <param name="RedFromChromaRed">
/// Signed contribution of <c>(Cr - 128)</c> to red, times 256.
/// <para><c>(Cr - 128)</c> 对红色的带符号贡献，已乘 256。</para>
/// </param>
/// <param name="GreenFromChromaBlue">
/// Signed contribution of <c>(Cb - 128)</c> to green, times 256.
/// <para><c>(Cb - 128)</c> 对绿色的带符号贡献，已乘 256。</para>
/// </param>
/// <param name="GreenFromChromaRed">
/// Signed contribution of <c>(Cr - 128)</c> to green, times 256.
/// <para><c>(Cr - 128)</c> 对绿色的带符号贡献，已乘 256。</para>
/// </param>
/// <param name="BlueFromChromaBlue">
/// Signed contribution of <c>(Cb - 128)</c> to blue, times 256.
/// <para><c>(Cb - 128)</c> 对蓝色的带符号贡献，已乘 256。</para>
/// </param>
public readonly record struct Nv12Coefficients(int LumaOffset, int LumaScale, int RedFromChromaRed, int GreenFromChromaBlue, int GreenFromChromaRed, int BlueFromChromaBlue);

/// <summary>
/// Resolves the shared NV12 conversion constants.
/// <para>解析共用的 NV12 转换常量。</para>
/// </summary>
public static class Nv12ConversionTable
{
    /// <summary>Returns the conversion constants for a matrix and range pair.
    /// <para>返回指定矩阵与范围组合的转换常量。</para></summary>
    /// <param name="matrix">The chroma matrix to apply; <see cref="VideoColorMatrix.Auto"/> resolves to BT.601, matching the default of the CPU converter.
    /// <para>要应用的色度矩阵；<see cref="VideoColorMatrix.Auto"/> 解析为 BT.601，与 CPU 转换器默认值一致。</para></param>
    /// <param name="range">The nominal range to apply; <see cref="VideoColorRange.Auto"/> resolves to limited range.
    /// <para>要应用的标称范围；<see cref="VideoColorRange.Auto"/> 解析为有限范围。</para></param>
    /// <returns>The fixed-point constants for the pair.<para>该组合对应的定点常量。</para></returns>
    public static Nv12Coefficients For(VideoColorMatrix matrix, VideoColorRange range)
    {
        var full = range == VideoColorRange.Full;
        return matrix switch
        {
            VideoColorMatrix.Bt709 when full => new Nv12Coefficients(0, 256, 403, -48, -120, 475),
            VideoColorMatrix.Bt709 => new Nv12Coefficients(16, 298, 459, -55, -136, 541),
            VideoColorMatrix.Bt2020 when full => new Nv12Coefficients(0, 256, 378, -42, -146, 482),
            VideoColorMatrix.Bt2020 => new Nv12Coefficients(16, 298, 430, -48, -167, 548),
            VideoColorMatrix.Bt601 when full => new Nv12Coefficients(0, 256, 359, -88, -183, 454),
            _ => new Nv12Coefficients(16, 298, 409, -100, -208, 516),
        };
    }
}