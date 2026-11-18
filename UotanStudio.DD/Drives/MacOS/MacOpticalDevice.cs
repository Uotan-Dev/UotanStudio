using System.Text.RegularExpressions;

namespace UotanStudio.DD.Drives.MacOS;

/// <summary>
/// Shared parsing of <c>drutil list</c> records for the macOS optical scanner and writer.
/// <para>macOS 光驱扫描器与写入器共用的 <c>drutil list</c> 记录解析。</para>
/// </summary>
internal static class MacOpticalDevice
{
    internal const string DrutilPath = "/usr/bin/drutil";

    private static readonly Regex LinePattern = new(
        @"^\s*(?<name>disk\d+)\s+(?<vendor>.+?)\s+\((?<product>[^)]+)\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses a <c>drutil list</c> line into a disk name and description.
    /// <para>把 <c>drutil list</c> 的一行解析为磁盘名与描述。</para>
    /// </summary>
    /// <param name="line">One output line.<para>一行输出。</para></param>
    /// <param name="name">Disk name such as <c>disk2</c>.<para>磁盘名，例如 <c>disk2</c>。</para></param>
    /// <param name="description">Vendor and product description.<para>厂商与产品描述。</para></param>
    /// <returns><see langword="true"/> when the line matched.<para>行匹配时为 <see langword="true"/>。</para></returns>
    internal static bool TryParse(string line, out string name, out string description)
    {
        var match = LinePattern.Match(line.TrimEnd('\r'));
        if (!match.Success)
        {
            name = string.Empty;
            description = string.Empty;
            return false;
        }

        name = match.Groups["name"].Value;
        var vendor = match.Groups["vendor"].Value.Trim();
        var product = match.Groups["product"].Value.Trim();
        description = string.Join(' ', new[] { vendor, product }.Where(part => part.Length > 0));
        return true;
    }

    /// <summary>
    /// Resolves the zero-based <c>drutil</c> device index for a raw device path.
    /// <para>为裸设备路径解析从零起算的 <c>drutil</c> 设备序号。</para>
    /// </summary>
    /// <param name="devicePath">Raw device path such as <c>/dev/rdisk2</c>.
    /// <para>裸设备路径，例如 <c>/dev/rdisk2</c>。</para></param>
    /// <param name="cancellationToken">Cancels the lookup.<para>取消查询。</para></param>
    /// <returns>The device index, or <see langword="null"/> when the drive is no longer present.
    /// <para>设备序号；光驱已不存在时为 <see langword="null"/>。</para></returns>
    internal static async Task<int?> ResolveIndexAsync(
        string devicePath,
        CancellationToken cancellationToken)
    {
        string listing;
        try
        {
            listing = await MacCommand.RunAsync(DrutilPath, ["list"], cancellationToken).ConfigureAwait(false);
        }
        catch (FlashException)
        {
            return null;
        }

        var bare = Path.GetFileName(devicePath);
        if (bare.StartsWith("rdisk", StringComparison.OrdinalIgnoreCase))
        {
            bare = bare[1..];
        }

        var index = 0;
        foreach (var line in listing.Split('\n'))
        {
            if (!TryParse(line, out var name, out _))
            {
                continue;
            }

            if (string.Equals(name, bare, StringComparison.Ordinal))
            {
                return index;
            }

            index++;
        }

        return null;
    }
}