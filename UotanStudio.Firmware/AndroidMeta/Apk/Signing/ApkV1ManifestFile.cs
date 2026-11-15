using System.Text;

namespace UotanStudio.Firmware.AndroidMeta.Apk.Signing;

/// <summary>
/// One section of a JAR manifest (<c>META-INF/MANIFEST.MF</c> or a <c>.SF</c> signature file).
/// <para>JAR 清单（<c>META-INF/MANIFEST.MF</c> 或 <c>.SF</c> 签名文件）的一个段。</para></summary>
/// <param name="Name">The section's <c>Name</c> attribute; <see langword="null"/> for the main section.
/// <para>段的 <c>Name</c> 属性；主段为 <see langword="null"/>。</para></param>
/// <param name="Attributes">Attribute name/value pairs of the section.
/// <para>段的属性名值对。</para></param>
/// <param name="RawBytes">The section's raw bytes from the first attribute through the blank-line separator, exactly what the JAR digest covers.
/// <para>段的原始字节，从首属性到空行分隔符，与 JAR 摘要覆盖的范围完全一致。</para></param>
public sealed record ApkV1ManifestSection(string? Name, IReadOnlyDictionary<string, string> Attributes, byte[] RawBytes);

/// <summary>
/// Parses JAR manifest bytes into sections with their raw digest-covered spans.
/// <para>把 JAR 清单字节解析为段，并保留参与摘要计算的原始字节范围。</para></summary>
/// <remarks>
/// Handles CRLF and LF terminators plus single-space continuation lines. A section spans from its
/// first attribute line through the blank line that ends it, which is the span the
/// <c>SHA-256-Digest</c> attribute of a <c>.SF</c> entry covers.
/// <para>支持 CRLF 与 LF 行尾以及单空格续行。段从其首属性行延伸到结束该段的空行，这正是
/// <c>.SF</c> 条目的 <c>SHA-256-Digest</c> 属性覆盖的范围。</para>
/// </remarks>
public static class ApkV1ManifestFile
{
    /// <summary>Parses manifest bytes into ordered sections.
    /// <para>把清单字节解析为有序段。</para></summary>
    /// <param name="bytes">UTF-8 manifest bytes. <para>UTF-8 清单字节。</para></param>
    /// <returns>Sections in file order; the main section first when present.
    /// <para>按文件顺序的段；存在主段时位于最前。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is <see langword="null"/>.
    /// <para><paramref name="bytes"/> 为 <see langword="null"/>。</para></exception>
    public static IReadOnlyList<ApkV1ManifestSection> Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var text = Encoding.UTF8.GetString(bytes);
        var sections = new List<ApkV1ManifestSection>();
        var attributes = new List<(string Name, string Value)>();
        var sectionStart = 0;
        var sectionEnd = 0;

        void CloseSection(int endOffset)
        {
            if (attributes.Count == 0)
            {
                sectionStart = endOffset;
                return;
            }
            var name = attributes.FirstOrDefault(a => string.Equals(a.Name, "Name", StringComparison.OrdinalIgnoreCase)).Value;
            sections.Add(new ApkV1ManifestSection(
                string.IsNullOrEmpty(name) ? null : name,
                attributes.ToDictionary(a => a.Name, a => a.Value, StringComparer.OrdinalIgnoreCase),
                bytes[sectionStart..Math.Min(endOffset, bytes.Length)]));
            attributes.Clear();
            sectionStart = endOffset;
        }

        var position = 0;
        while (position < text.Length)
        {
            var lineEnd = text.IndexOfAny(['\r', '\n'], position);
            var line = lineEnd < 0 ? text[position..] : text[position..lineEnd];
            var next = lineEnd < 0 ? text.Length : lineEnd;
            if (next < text.Length && text[next] == '\r' && next + 1 < text.Length && text[next + 1] == '\n')
            {
                next++;
            }
            next++;

            if (line.StartsWith(' '))
            {
                if (attributes.Count > 0)
                {
                    var (last, value) = attributes[^1];
                    attributes[^1] = (last, value + line[1..]);
                }
                sectionEnd = next;
                position = next;
                continue;
            }
            if (line.Length == 0)
            {
                sectionEnd = next;
                CloseSection(next);
                position = next;
                continue;
            }
            var separator = line.IndexOf(':');
            if (separator > 0 && attributes.Count == 0)
            {
                sectionStart = position;
            }
            if (separator > 0)
            {
                attributes.Add((line[..separator].Trim(), line[(separator + 1)..].Trim()));
            }
            sectionEnd = next;
            position = next;
        }
        CloseSection(Math.Min(sectionEnd, bytes.Length));
        return sections;
    }
}