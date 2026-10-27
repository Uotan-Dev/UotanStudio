namespace UotanStudio.Firmware.FileSystem.Metadata;

/// <summary>
/// Reads and writes the <c>fs_config</c> permission token in the one dialect the format accepts: octal
/// digits, optionally prefixed with <c>0</c>, always written back as four zero-padded digits.
/// <para>以该格式唯一接受的写法读写 <c>fs_config</c> 权限记号：八进制数字，可带前导 <c>0</c>，
/// 输出时一律为零填充的四位。</para>
/// </summary>
internal static class FsConfigModes
{
    private const int PermissionMask = 0xFFF;

    /// <summary>
    /// Parses a permission token made only of digits <c>0</c>-<c>7</c>, with or without a leading <c>0</c>,
    /// keeping the twelve low bits of the octal value. Tokens containing <c>8</c>, <c>9</c>, a sign,
    /// whitespace or any other character are rejected; a token of only <c>0</c>-<c>7</c> is therefore always
    /// read as octal, never as the decimal spelling the repo used to emit.
    /// <para>解析仅由数字 <c>0</c>-<c>7</c> 组成的权限记号（可带前导 <c>0</c>），保留其八进制值的低十二位。
    /// 含 <c>8</c>、<c>9</c>、正负号、空白或其它字符的记号会被拒绝；因此仅由 <c>0</c>-<c>7</c> 组成的记号
    /// 永远按八进制解释，而不是按本仓库曾输出的十进制写法解释。</para>
    /// </summary>
    /// <param name="token">One whitespace-delimited mode field, never <see langword="null"/>.
    /// <para>一个以空白分隔的模式字段，永不为 <see langword="null"/>。</para></param>
    /// <param name="mode">The twelve low permission bits when <see langword="true"/>; otherwise
    /// <c>0</c>. <para>返回 <see langword="true"/> 时为低十二位权限位；否则为 <c>0</c>。</para></param>
    /// <returns><see langword="true"/> when <paramref name="token"/> is a non-empty octal digit run.
    /// <para><paramref name="token"/> 为非空的八进制数字串时返回 <see langword="true"/>。</para></returns>
    internal static bool TryParse(string token, out ushort mode)
    {
        mode = 0;
        if (token.Length == 0)
        {
            return false;
        }

        var bits = 0;
        foreach (var character in token)
        {
            if (character is < '0' or > '7')
            {
                return false;
            }

            bits = ((bits << 3) | (character - '0')) & PermissionMask;
        }

        mode = (ushort)bits;
        return true;
    }

    /// <summary>
    /// Renders a mode as the four zero-padded octal digits the format expects, discarding the file-type bits
    /// above the twelve permission bits; <c>0</c> therefore renders as <c>0000</c> rather than a substituted
    /// default.
    /// <para>将模式渲染为该格式期望的四位零填充八进制数字，丢弃低十二位权限位之上的文件类型位；
    /// 因此 <c>0</c> 输出为 <c>0000</c>，而不是被替换成默认权限。</para>
    /// </summary>
    /// <param name="mode">Raw inode mode, whose type bits are ignored.
    /// <para>原始 inode 模式，其类型位会被忽略。</para></param>
    /// <returns>A four-digit octal token such as <c>0644</c> or <c>4755</c>.
    /// <para>四位八进制记号，例如 <c>0644</c> 或 <c>4755</c>。</para></returns>
    internal static string Format(ushort mode)
        => Convert.ToString(mode & PermissionMask, 8).PadLeft(4, '0');
}