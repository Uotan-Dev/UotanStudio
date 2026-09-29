using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;

namespace UotanStudio.Firmware.VendorFirmware.Oppo;

/// <summary>
/// Guards extracted entry paths so they cannot escape the requested output directory.
/// <para>守护提取条目的输出路径，防止其逃逸出指定的输出目录。</para>
/// </summary>
internal static class OppOutputPathGuard
{
    /// <summary>
    /// Validates that <paramref name="filePath"/> resolves inside <paramref name="outputDir"/> and rejects traversal outside it.
    /// <para>校验 <paramref name="filePath"/> 解析后位于 <paramref name="outputDir"/> 之内，并拒绝逃逸出该目录的路径。</para>
    /// </summary>
    /// <param name="outputDir">
    /// Output directory the entry must stay inside.
    /// <para>条目必须停留在其中的输出目录。</para>
    /// </param>
    /// <param name="filePath">
    /// Resolved output path of the entry.
    /// <para>条目解析后的输出路径。</para>
    /// </param>
    /// <exception cref="InvalidOppFormatException">
    /// The path attempts to write outside the output directory.
    /// <para>路径试图写到输出目录之外。</para>
    /// </exception>
    internal static void Validate(string outputDir, string filePath)
    {
        var fullOutputDir = Path.GetFullPath(outputDir);
        var fullFilePath = Path.GetFullPath(filePath);
        if (!fullFilePath.StartsWith(fullOutputDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !fullFilePath.Equals(fullOutputDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOppFormatException($"Entry path '{filePath}' attempts to write outside the output directory.");
        }
    }
}