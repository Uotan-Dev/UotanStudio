using System.Text;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;

namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>
/// Read-only inspection of a HAP container: whether a signing block is present and which application bundle name
/// the module metadata declares. <para>HAP 容器的只读检视：是否存在签名块，以及模块元数据声明的应用包名。</para>
/// </summary>
/// <remarks>
/// The bundle name comes from a bounded scan of the <c>module.json</c> entry; JSON5 comments and trailing commas are
/// tolerated by the scan, and a missing entry yields an empty name instead of an error so callers can ask the user.
/// <para>包名来自对 <c>module.json</c> 条目的有界扫描；扫描容忍 JSON5 注释与尾逗号，条目缺失时返回空名而非
/// 报错，以便调用方转而询问用户。</para>
/// </remarks>
public static class HapInspector
{
    private const int MaximumContainerBytes = 4 << 30;
    private const int MaximumModuleBytes = 4 << 20;

    /// <summary>
    /// Reports whether the container already carries a HAP signing block.
    /// <para>报告容器是否已携带 HAP 签名块。</para></summary>
    /// <param name="path">Container path. <para>容器路径。</para></param>
    /// <returns><see langword="true"/> when a signing block is present.
    /// <para>存在签名块时为 <see langword="true"/>。</para></returns>
    /// <exception cref="InvalidDataException">The file is not a supported ZIP32 container.
    /// <para>文件不是受支持的 ZIP32 容器。</para></exception>
    public static bool IsSigned(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumContainerBytes)
        {
            throw new InvalidDataException($"The container exceeds {MaximumContainerBytes} bytes. 容器超过 {MaximumContainerBytes} 字节。");
        }

        var sections = ApkZipSections.Locate(stream);
        return HapSigningBlock.Find(stream, sections.CentralDirectoryOffset) is not null;
    }

    /// <summary>
    /// Extracts the bundle name declared by the module metadata, tolerating JSON5 shapes.
    /// <para>提取模块元数据声明的包名，容忍 JSON5 形态。</para></summary>
    /// <param name="path">Container path. <para>容器路径。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The bundle name, or an empty string when undeclared.
    /// <para>包名；未声明时为空串。</para></returns>
    /// <exception cref="InvalidDataException">The file is not a supported ZIP32 container.
    /// <para>文件不是受支持的 ZIP32 容器。</para></exception>
    public static async Task<string> GetBundleNameAsync(string path, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumContainerBytes)
        {
            throw new InvalidDataException($"The container exceeds {MaximumContainerBytes} bytes. 容器超过 {MaximumContainerBytes} 字节。");
        }

        var sections = ApkZipSections.Locate(stream);
        var entries = HapZipArchive.Read(stream, sections.CentralDirectoryOffset, sections.CentralDirectorySize);
        var module = entries.FirstOrDefault(entry =>
            string.Equals(Encoding.UTF8.GetString(entry.Name), "module.json", StringComparison.OrdinalIgnoreCase));
        if (module is null || module.Data.Length is 0 or > MaximumModuleBytes)
        {
            return string.Empty;
        }

        return await Task.Run(() => ExtractBundleName(module.Data), cancellationToken).ConfigureAwait(false);
    }

    internal static string ExtractBundleName(ReadOnlySpan<byte> json)
    {
        // A tolerant scan: find "bundleName" and take the following quoted string, skipping comments and
        // whitespace. JSON5 module files are common in the wild.
        var text = Encoding.UTF8.GetString(json.ToArray());
        var keyIndex = text.IndexOf("\"bundleName\"", StringComparison.Ordinal);
        while (keyIndex >= 0)
        {
            var cursor = keyIndex + "\"bundleName\"".Length;
            cursor = SkipInsignificant(text, cursor);
            if (cursor < text.Length && text[cursor] == ':')
            {
                cursor = SkipInsignificant(text, cursor + 1);
                if (cursor < text.Length && text[cursor] == '"')
                {
                    var end = text.IndexOf('"', cursor + 1);
                    if (end > cursor)
                    {
                        return text[(cursor + 1)..end];
                    }
                }
            }

            keyIndex = text.IndexOf("\"bundleName\"", cursor, StringComparison.Ordinal);
        }

        return string.Empty;
    }

    private static int SkipInsignificant(string text, int index)
    {
        while (index < text.Length)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character == '/' && index + 1 < text.Length)
            {
                var next = text[index + 1];
                if (next == '/')
                {
                    var newline = text.IndexOf('\n', index);
                    index = newline < 0 ? text.Length : newline + 1;
                    continue;
                }

                if (next == '*')
                {
                    var close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    index = close < 0 ? text.Length : close + 2;
                    continue;
                }
            }

            break;
        }

        return index;
    }
}