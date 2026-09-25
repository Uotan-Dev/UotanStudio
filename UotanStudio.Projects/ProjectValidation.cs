namespace UotanStudio.Services.Projects;

/// <summary>
/// Shared validation for persisted solution references, without restricting editor document paths.
/// <para>针对已持久化的解决方案引用的共享校验，不约束编辑器文档路径。</para>
/// </summary>
public static class ProjectValidation
{
    /// <summary>
    /// Decides whether a directory may own project state: it must resolve outside the solution root and
    /// no path segment below the root may be a reserved data folder (<c>images</c>, <c>output</c>,
    /// <c>delivery</c>, <c>.uotan</c>, <c>.deleted</c>, <c>.git</c>, case-insensitive).
    /// <para>判定目录能否持有工程状态：解析后必须位于解决方案根之外，且根以下的任一路径段都不得是保留
    /// 数据目录（<c>images</c>、<c>output</c>、<c>delivery</c>、<c>.uotan</c>、<c>.deleted</c>、
    /// <c>.git</c>，大小写不敏感）。</para>
    /// </summary>
    /// <param name="solutionRoot">Solution root directory used as the comparison base.
    /// <para>作为比较基准的解决方案根目录。</para></param>
    /// <param name="directory">Candidate state-owning directory. <para>待判定的状态目录。</para></param>
    /// <returns><see langword="false"/> when the directory is the solution root itself or touches a
    /// reserved folder; otherwise <see langword="true"/>. <para>目录即解决方案根本身、或触及保留目录时
    /// 返回 <see langword="false"/>；否则返回 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentException">A path is empty or contains invalid characters.
    /// <para>某路径为空或包含非法字符。</para></exception>
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
    /// another sub-project (or a deeper path).
    /// <para>与 <see cref="IsSubProjectDirectory"/> 相同，但额外要求目录必须是解决方案根的直接子级：
    /// 子项目独立平铺，禁止在另一子项目内再建子项目。</para>
    /// </summary>
    /// <param name="solutionRoot">Solution root directory. <para>解决方案根目录。</para></param>
    /// <param name="directory">Candidate sub-project directory. <para>待判定子项目目录。</para></param>
    /// <returns><see langword="true"/> only when the directory is a direct child of the root and passes
    /// the reserved-folder check; <see langword="false"/> for the root itself, nested or reserved paths.
    /// <para>仅当目录是解决方案根的直接子级且通过保留目录检查时返回 <see langword="true"/>；根目录本身、
    /// 嵌套或保留路径返回 <see langword="false"/>。</para></returns>
    /// <exception cref="ArgumentException">A path is empty or contains invalid characters.
    /// <para>某路径为空或包含非法字符。</para></exception>
    public static bool IsDirectSubProjectDirectory(string solutionRoot, string directory)
    {
        if (!IsSubProjectDirectory(solutionRoot, directory)) return false;
        var relative = Path.GetRelativePath(Path.GetFullPath(solutionRoot), Path.GetFullPath(directory));
        return !relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .SkipLast(1)
            .Any(part => part.Length > 0);
    }
    /// <summary>
    /// Rejects unsupported versions and ambiguous sub-project identities before context publication.
    /// A snapshot without sub-project references only needs the version check.
    /// <para>在发布上下文之前拒绝不支持的版本与有歧义的子项目标识。不含子项目引用的快照只需通过
    /// 版本检查。</para>
    /// </summary>
    /// <param name="snapshot">Solution snapshot to validate. <para>待校验的解决方案快照。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.
    /// <para><paramref name="snapshot"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The format version is not 2, there are more than 10000
    /// sub-projects, a reference is <see langword="null"/>, two names collide case-insensitively, or a
    /// relative path is blank. <para>格式版本不是 2、子项目超过 10000 个、引用为
    /// <see langword="null"/>、按大小写不敏感存在重名，或相对路径为空。</para></exception>
    /// <exception cref="ArgumentException">A sub-project name fails <see cref="ValidateName"/>.
    /// <para>某个子项目名称未通过 <see cref="ValidateName"/>。</para></exception>
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

    /// <summary>
    /// Validates a display name; renaming a reference does not rename its files.
    /// <para>校验显示名称；重命名引用不会重命名其文件。</para>
    /// </summary>
    /// <param name="name">Candidate display name. <para>待判定的显示名称。</para></param>
    /// <exception cref="ArgumentException">The name is blank, longer than 255 characters, is
    /// <c>.</c> or <c>..</c>, contains a control character, or contains a path separator.
    /// <para>名称为空白、超过 255 个字符、为 <c>.</c> 或 <c>..</c>、包含控制字符或路径分隔符。</para></exception>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." || name.Any(char.IsControl)
            || name.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("Invalid sub-project name.", nameof(name));
    }
}