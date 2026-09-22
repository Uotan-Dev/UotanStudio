namespace UotanStudio.Services.Projects;

/// <summary>An installed package prerequisite with optional numeric major.minor.patch bounds.</summary>
public sealed class VisualPluginDependency
{
    /// <summary>Required composite package id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Inclusive minimum version; null means no lower bound.</summary>
    public string? MinimumVersion { get; set; }

    /// <summary>Exclusive maximum version; null means no upper bound.</summary>
    public string? MaximumVersionExclusive { get; set; }

    /// <summary>Checks an installed version. Unbounded requirements also support legacy free-form versions.</summary>
    public bool MatchesVersion(string version)
    {
        if (MinimumVersion is null && MaximumVersionExclusive is null)
        {
            return true;
        }
        if (!TryParseVersion(version, out var actual))
        {
            return false;
        }
        if (MinimumVersion is not null
            && (!TryParseVersion(MinimumVersion, out var minimum) || actual < minimum))
        {
            return false;
        }
        return MaximumVersionExclusive is null
            || TryParseVersion(MaximumVersionExclusive, out var maximum) && actual < maximum;
    }

    /// <summary>Accepts canonical three-component nonnegative Int32 versions, without prefixes or suffixes.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text is null || text.Length > 32 || !Version.TryParse(text, out var parsed)
            || parsed.Build < 0 || parsed.Revision >= 0 || parsed.ToString(3) != text)
        {
            return false;
        }
        version = parsed;
        return true;
    }
}