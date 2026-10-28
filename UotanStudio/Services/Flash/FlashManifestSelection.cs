using System.Text.RegularExpressions;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Selects the primary numbered rawprogram manifests of a flash folder so the provisioning manifests shipped next
/// to them are never executed as ordinary bundle members.
/// <para>选择刷机目录中的主编号 rawprogram 清单，使随附的配置类清单不会被当成普通包成员执行。</para>
/// </summary>
internal static partial class FlashManifestSelection
{
    /// <summary>
    /// Returns the primary <c>rawprogram&lt;number&gt;.xml</c> manifests in ordinal name order.
    /// <para>按名称顺序返回主 <c>rawprogram&lt;number&gt;.xml</c> 清单。</para>
    /// </summary>
    /// <param name="directory">Existing directory that holds the manifests.
    /// <para>存放清单且已存在的目录。</para></param>
    /// <returns>The primary manifests, or an empty array when the directory has none.
    /// <para>主清单数组；目录内没有时为空数组。</para></returns>
    public static string[] Enumerate(string directory)
    {
        var manifests = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "rawprogram*.xml", SearchOption.TopDirectoryOnly))
        {
            if (IsPrimary(Path.GetFileName(path)))
            {
                manifests.Add(path);
            }
        }

        manifests.Sort(StringComparer.OrdinalIgnoreCase);
        return [.. manifests];
    }

    /// <summary>
    /// Whether a file name is a primary numbered manifest instead of a blank-GPT, wipe-partitions or PHY wipe one.
    /// <para>判断文件名是主编号清单，而不是 blank-GPT、wipe-partitions 或 PHY 擦除清单。</para>
    /// </summary>
    /// <param name="fileName">Manifest file name without a directory.
    /// <para>不含目录的清单文件名。</para></param>
    /// <returns><see langword="true"/> for a primary manifest. <para>是主清单时为 <see langword="true"/>。</para></returns>
    public static bool IsPrimary(string fileName) => PrimaryManifest().IsMatch(fileName);

    [GeneratedRegex(@"^rawprogram[0-9]*\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrimaryManifest();
}