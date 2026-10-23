namespace UotanStudio.Fonts;

/// <summary>
/// Identifies the two MiSans faces shipped as compressed manifest resources.
/// <para>标识以压缩清单资源形式随包的两个 MiSans 字形。</para>
/// </summary>
internal static class MiSansFontData
{
    /// <summary>
    /// The logical manifest resource name of the regular face.
    /// <para>常规字重的逻辑清单资源名。</para>
    /// </summary>
    internal const string RegularResourceName = "UotanStudio.Fonts.MiSans-Regular.ttf";

    /// <summary>
    /// The logical manifest resource name of the bold face.
    /// <para>粗体的逻辑清单资源名。</para>
    /// </summary>
    internal const string BoldResourceName = "UotanStudio.Fonts.MiSans-Bold.ttf";

    /// <summary>
    /// The uncompressed byte length of the regular face.
    /// <para>常规字重的未压缩字节长度。</para>
    /// </summary>
    internal const int RegularUncompressedLength = 8_122_324;

    /// <summary>
    /// The uncompressed byte length of the bold face.
    /// <para>粗体的未压缩字节长度。</para>
    /// </summary>
    internal const int BoldUncompressedLength = 8_009_316;
}