namespace UotanStudio.Services.Projects;

/// <summary>Shared validation for persisted solution references, without restricting editor document paths.</summary>
public static class ProjectValidation
{
    /// <summary>State-owning directories must be distinct from the solution root and outside reserved data folders.</summary>
    public static bool IsSubProjectDirectory(string solutionRoot, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(solutionRoot), Path.GetFullPath(directory));
        if (relative == ".") return false;
        var reserved = new[] { "images", "output", "delivery", ".uotan", ".deleted", ".git" };
        return !relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Any(part => reserved.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Mirrors <see cref="IsSubProjectDirectory"/> but additionally requires the directory to be a
    /// direct child of the solution root — sub-projects are independent folders and never nested inside
    /// another sub-project (or a deeper path). 除保留目录校验外，还要求目录是解决方案根的直接子级：
    /// 子项目独立平铺，禁止在另一子项目内再建子项目。
    /// </summary>
    /// <param name="solutionRoot">Solution root directory. 解决方案根目录。</param>
    /// <param name="directory">Candidate sub-project directory. 待判定子项目目录。</param>
    public static bool IsDirectSubProjectDirectory(string solutionRoot, string directory)
    {
        if (!IsSubProjectDirectory(solutionRoot, directory)) return false;
        var relative = Path.GetRelativePath(Path.GetFullPath(solutionRoot), Path.GetFullPath(directory));
        return !relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .SkipLast(1)
            .Any(part => part.Length > 0);
    }
    /// <summary>Rejects unsupported versions and ambiguous sub-project identities before context publication.</summary>
    public static void ValidateSolution(SolutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.FormatVersion != 2) throw new InvalidDataException("Unsupported solution format version.");
        if (snapshot.SubProjects is not { } projects) return;
        if (projects.Count > 10000) throw new InvalidDataException("Too many sub-projects.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            if (project is null) throw new InvalidDataException("Null sub-project reference.");
            ValidateName(project.Name);
            if (!names.Add(project.Name)) throw new InvalidDataException("Duplicate sub-project name.");
            if (string.IsNullOrWhiteSpace(project.RelativePath)) throw new InvalidDataException("Empty sub-project path.");
        }
    }

    /// <summary>Validates a display name; renaming a reference does not rename its files.</summary>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." || name.Any(char.IsControl)
            || name.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("Invalid sub-project name.", nameof(name));
    }
}