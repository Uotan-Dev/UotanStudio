namespace UotanStudio.Models;

/// <summary>
/// Represents a version number made of major, minor, and revision parts.
/// <para>表示由主版本号、次版本号与修订号组成的版本号。</para>
/// </summary>
public sealed class LanguagePackVersion : IComparable<LanguagePackVersion>, IEquatable<LanguagePackVersion>
{
    /// <summary>
    /// Gets the major version number.
    /// <para>获取主版本号。</para>
    /// </summary>
    /// <value>The major version number.<para>主版本号。</para></value>
    public int Major { get; }

    /// <summary>
    /// Gets the minor version number.
    /// <para>获取次版本号。</para>
    /// </summary>
    /// <value>The minor version number.<para>次版本号。</para></value>
    public int Minor { get; }

    /// <summary>
    /// Gets the revision number.
    /// <para>获取修订号。</para>
    /// </summary>
    /// <value>The revision number.<para>修订号。</para></value>
    public int Revision { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguagePackVersion"/> class.
    /// <para>初始化 <see cref="LanguagePackVersion"/> 类的新实例。</para>
    /// </summary>
    /// <param name="major">
    /// The major version number.
    /// <para>主版本号。</para>
    /// </param>
    /// <param name="minor">
    /// The minor version number.
    /// <para>次版本号。</para>
    /// </param>
    /// <param name="revision">
    /// The revision number.
    /// <para>修订号。</para>
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when any part of the version number is negative.
    /// <para>当版本号任一部分为负数时抛出。</para>
    /// </exception>
    public LanguagePackVersion(int major, int minor, int revision)
    {
        if (major < 0) throw new ArgumentOutOfRangeException(nameof(major), "Major version must be non-negative.");
        if (minor < 0) throw new ArgumentOutOfRangeException(nameof(minor), "Minor version must be non-negative.");
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision), "Revision must be non-negative.");
        Major = major;
        Minor = minor;
        Revision = revision;
    }

    /// <summary>
    /// Gets the default version.
    /// <para>获取默认版本号。</para>
    /// </summary>
    /// <value>The version with major part 1 and zero other parts.<para>主版本号为 1、其余部分为 0 的版本。</para></value>
    public static LanguagePackVersion Default { get; } = new(1, 0, 0);

    /// <summary>
    /// Parses a version string into a <see cref="LanguagePackVersion"/> instance.
    /// <para>将版本字符串解析为 <see cref="LanguagePackVersion"/> 实例。</para>
    /// </summary>
    /// <param name="versionString">
    /// The version string to parse.
    /// <para>要解析的版本字符串。</para>
    /// </param>
    /// <returns>
    /// The parsed <see cref="LanguagePackVersion"/> instance.
    /// <para>解析后的 <see cref="LanguagePackVersion"/> 实例。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the version string format is invalid.
    /// <para>当版本字符串格式无效时抛出。</para>
    /// </exception>
    public static LanguagePackVersion Parse(string versionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionString);

        var parts = versionString.Split('.');
        return parts.Length != 3 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var revision)
            ? throw new ArgumentException($"Invalid version format: '{versionString}'. Expected 'Major.Minor.Revision'.", nameof(versionString))
            : new LanguagePackVersion(major, minor, revision);
    }

    /// <summary>
    /// Attempts to parse a version string into a <see cref="LanguagePackVersion"/> instance.
    /// <para>尝试将版本字符串解析为 <see cref="LanguagePackVersion"/> 实例。</para>
    /// </summary>
    /// <param name="versionString">
    /// The version string to parse.
    /// <para>要解析的版本字符串。</para>
    /// </param>
    /// <param name="result">
    /// Contains the result on success; otherwise, null.
    /// <para>解析成功时包含结果；否则为 null。</para>
    /// </param>
    /// <returns>
    /// True if parsing succeeded; otherwise, false.
    /// <para>解析成功返回 true；否则返回 false。</para>
    /// </returns>
    public static bool TryParse(string? versionString, out LanguagePackVersion? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(versionString)) return false;

        var parts = versionString.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var revision))
        {
            return false;
        }

        if (major < 0 || minor < 0 || revision < 0) return false;

        result = new LanguagePackVersion(major, minor, revision);
        return true;
    }

    /// <summary>
    /// Compares the current version with another version.
    /// <para>将当前版本与另一个版本进行比较。</para>
    /// </summary>
    /// <param name="other">
    /// The other version to compare.
    /// <para>要比较的另一个版本。</para>
    /// </param>
    /// <returns>
    /// Less than zero if the current version is lower; zero if equal; greater than zero if higher.
    /// <para>小于零表示当前版本较低；零表示相等；大于零表示当前版本较高。</para>
    /// </returns>
    public int CompareTo(LanguagePackVersion? other)
    {
        if (other is null) return 1;

        int result = Major.CompareTo(other.Major);
        if (result != 0) return result;

        result = Minor.CompareTo(other.Minor);
        return result != 0 ? result : Revision.CompareTo(other.Revision);
    }

    /// <summary>
    /// Determines whether the current version is equal to another version.
    /// <para>判断当前版本是否与另一个版本相等。</para>
    /// </summary>
    /// <param name="other">The version to compare. <para>要比较的版本。</para></param>
    /// <returns>True if equal; otherwise, false.<para>如果相等则为 true；否则为 false。</para></returns>
    public bool Equals(LanguagePackVersion? other)
    {
        return other is null ? false : Major == other.Major && Minor == other.Minor && Revision == other.Revision;
    }

    /// <summary>
    /// Determines whether the current version is equal to the specified object.
    /// <para>判断当前版本是否与指定对象相等。</para>
    /// </summary>
    /// <param name="obj">The object to compare. <para>要比较的对象。</para></param>
    /// <returns>True if equal; otherwise, false.<para>如果相等则为 true；否则为 false。</para></returns>
    public override bool Equals(object? obj) => Equals(obj as LanguagePackVersion);

    /// <summary>
    /// Gets the hash code.
    /// <para>获取哈希代码。</para>
    /// </summary>
    /// <returns>The hash code for this instance.<para>当前实例的哈希代码。</para></returns>
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Revision);

    /// <summary>
    /// Returns the string representation of the version.
    /// <para>返回版本号的字符串表示。</para>
    /// </summary>
    /// <returns>The major, minor, and revision parts joined by dots.<para>以点号连接主版本号、次版本号与修订号。</para></returns>
    public override string ToString() => $"{Major}.{Minor}.{Revision}";

    /// <summary>
    /// Comparison operators.
    /// <para>比较运算符。</para>
    /// </summary>
    public static bool operator <(LanguagePackVersion left, LanguagePackVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(LanguagePackVersion left, LanguagePackVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(LanguagePackVersion left, LanguagePackVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(LanguagePackVersion left, LanguagePackVersion right) => left.CompareTo(right) >= 0;
    public static bool operator ==(LanguagePackVersion? left, LanguagePackVersion? right)
    {
        return left is null ? right is null : left.Equals(right);
    }
    public static bool operator !=(LanguagePackVersion? left, LanguagePackVersion? right) => !(left == right);
}