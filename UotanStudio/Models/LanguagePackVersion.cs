using System;

namespace UotanStudio.Models;

/// <summary>
/// <para>表示语言包的语义化版本号，支持 Major.Minor.Revision 格式的比较。</para>
/// Represents a semantic version number for a language pack, supporting Major.Minor.Revision format comparison.
/// </summary>
public sealed class LanguagePackVersion : IComparable<LanguagePackVersion>, IEquatable<LanguagePackVersion>
{
    /// <summary>
    /// <para>获取主版本号。</para>
    /// Gets the major version number.
    /// </summary>
    public int Major { get; }

    /// <summary>
    /// <para>获取次版本号。</para>
    /// Gets the minor version number.
    /// </summary>
    public int Minor { get; }

    /// <summary>
    /// <para>获取修订号。</para>
    /// Gets the revision number.
    /// </summary>
    public int Revision { get; }

    /// <summary>
    /// <para>初始化 <see cref="LanguagePackVersion"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="LanguagePackVersion"/> class.
    /// </summary>
    /// <param name="major">
    /// <para>主版本号。</para>
    /// The major version number.
    /// </param>
    /// <param name="minor">
    /// <para>次版本号。</para>
    /// The minor version number.
    /// </param>
    /// <param name="revision">
    /// <para>修订号。</para>
    /// The revision number.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <para>当版本号任一部分为负数时抛出。</para>
    /// Thrown when any part of the version number is negative.
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
    /// <para>获取默认版本号（1.0.0）。</para>
    /// Gets the default version (1.0.0).
    /// </summary>
    public static LanguagePackVersion Default { get; } = new(1, 0, 0);

    /// <summary>
    /// <para>将版本字符串（如 "1.2.3"）解析为 <see cref="LanguagePackVersion"/> 实例。</para>
    /// Parses a version string (e.g., "1.2.3") into a <see cref="LanguagePackVersion"/> instance.
    /// </summary>
    /// <param name="versionString">
    /// <para>要解析的版本字符串。</para>
    /// The version string to parse.
    /// </param>
    /// <returns>
    /// <para>解析后的 <see cref="LanguagePackVersion"/> 实例。</para>
    /// The parsed <see cref="LanguagePackVersion"/> instance.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <para>当版本字符串格式无效时抛出。</para>
    /// Thrown when the version string format is invalid.
    /// </exception>
    public static LanguagePackVersion Parse(string versionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionString);

        var parts = versionString.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var revision))
        {
            throw new ArgumentException($"Invalid version format: '{versionString}'. Expected 'Major.Minor.Revision'.", nameof(versionString));
        }

        return new LanguagePackVersion(major, minor, revision);
    }

    /// <summary>
    /// <para>尝试将版本字符串解析为 <see cref="LanguagePackVersion"/> 实例。</para>
    /// Attempts to parse a version string into a <see cref="LanguagePackVersion"/> instance.
    /// </summary>
    /// <param name="versionString">
    /// <para>要解析的版本字符串。</para>
    /// The version string to parse.
    /// </param>
    /// <param name="result">
    /// <para>解析成功时包含结果；否则为 null。</para>
    /// Contains the result on success; otherwise, null.
    /// </param>
    /// <returns>
    /// <para>解析成功返回 true；否则返回 false。</para>
    /// True if parsing succeeded; otherwise, false.
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
    /// <para>将当前版本与另一个版本进行比较。</para>
    /// Compares the current version with another version.
    /// </summary>
    /// <param name="other">
    /// <para>要比较的另一个版本。</para>
    /// The other version to compare.
    /// </param>
    /// <returns>
    /// <para>小于零表示当前版本较低；零表示相等；大于零表示当前版本较高。</para>
    /// Less than zero if the current version is lower; zero if equal; greater than zero if higher.
    /// </returns>
    public int CompareTo(LanguagePackVersion? other)
    {
        if (other is null) return 1;

        int result = Major.CompareTo(other.Major);
        if (result != 0) return result;

        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;

        return Revision.CompareTo(other.Revision);
    }

    /// <summary>
    /// <para>判断当前版本是否与另一个版本相等。</para>
    /// Determines whether the current version is equal to another version.
    /// </summary>
    public bool Equals(LanguagePackVersion? other)
    {
        if (other is null) return false;
        return Major == other.Major && Minor == other.Minor && Revision == other.Revision;
    }

    /// <summary>
    /// <para>判断当前版本是否与指定对象相等。</para>
    /// Determines whether the current version is equal to the specified object.
    /// </summary>
    public override bool Equals(object? obj) => Equals(obj as LanguagePackVersion);

    /// <summary>
    /// <para>获取哈希代码。</para>
    /// Gets the hash code.
    /// </summary>
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Revision);

    /// <summary>
    /// <para>返回版本号的字符串表示（如 "1.2.3"）。</para>
    /// Returns the string representation of the version (e.g., "1.2.3").
    /// </summary>
    public override string ToString() => $"{Major}.{Minor}.{Revision}";

    /// <summary>
    /// <para>比较运算符。</para>
    /// Comparison operators.
    /// </summary>
    public static bool operator <(LanguagePackVersion left, LanguagePackVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(LanguagePackVersion left, LanguagePackVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(LanguagePackVersion left, LanguagePackVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(LanguagePackVersion left, LanguagePackVersion right) => left.CompareTo(right) >= 0;
    public static bool operator ==(LanguagePackVersion? left, LanguagePackVersion? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }
    public static bool operator !=(LanguagePackVersion? left, LanguagePackVersion? right) => !(left == right);
}
