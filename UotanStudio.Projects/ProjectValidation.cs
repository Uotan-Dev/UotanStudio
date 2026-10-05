using System.Collections.Frozen;

namespace UotanStudio.Services.Projects;

/// <summary>
/// Shared validation for persisted solution references, without restricting editor document paths.
/// <para>针对已持久化的解决方案引用的共享校验，不约束编辑器文档路径。</para>
/// </summary>
public static class ProjectValidation
{
    /// <summary>Reserved data directory names; the frozen set compares case-insensitively so neither validation
    /// path allocates a name array per call.
    /// <para>保留数据目录名；冻结集合并按大小写不敏感比较，使两处校验路径都不再逐次分配名称数组。</para></summary>
    private static readonly FrozenSet<string> ReservedDirectories =
        new[] { "images", "output", "delivery", "nodes", ".uotan", ".deleted", ".git" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Decides whether a single directory name is one of the reserved data folders
    /// (<c>images</c>, <c>output</c>, <c>delivery</c>, <c>nodes</c>, <c>.uotan</c>, <c>.deleted</c>, <c>.git</c>).
    /// <para>判定单个目录名是否为保留数据目录（<c>images</c>、<c>output</c>、<c>delivery</c>、<c>nodes</c>、
    /// <c>.uotan</c>、<c>.deleted</c>、<c>.git</c>）。</para></summary>
    /// <param name="name">Single path segment to test. <para>待判定的单个路径段。</para></param>
    /// <returns><see langword="true"/> for a reserved name, compared case-insensitively.
    /// <para>是保留名称时为 <see langword="true"/>，按大小写不敏感比较。</para></returns>
    internal static bool IsReservedDirectoryName(ReadOnlySpan<char> name) =>
        ReservedDirectories.GetAlternateLookup<ReadOnlySpan<char>>().Contains(name);

    /// <summary>
    /// Decides whether a directory may own project state: it must resolve outside the solution root and
    /// no path segment below the root may be a reserved data folder (<c>images</c>, <c>output</c>,
    /// <c>delivery</c>, <c>nodes</c>, <c>.uotan</c>, <c>.deleted</c>, <c>.git</c>, case-insensitive).
    /// <para>判定目录能否持有工程状态：解析后必须位于解决方案根之外，且根以下的任一路径段都不得是保留
    /// 数据目录（<c>images</c>、<c>output</c>、<c>delivery</c>、<c>nodes</c>、<c>.uotan</c>、
    /// <c>.deleted</c>、<c>.git</c>，大小写不敏感）。</para>
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
        var segments = relative.AsSpan();
        foreach (var segment in segments.SplitAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
        {
            if (IsReservedDirectoryName(segments[segment])) return false;
        }

        return true;
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
    /// Rejects unsupported versions and ambiguous sub-project or node-project identities before context
    /// publication. A snapshot without references only needs the version check.
    /// <para>在发布上下文之前拒绝不支持的版本与有歧义的子项目、节点项目标识。不含引用的快照只需通过
    /// 版本检查。</para>
    /// </summary>
    /// <param name="snapshot">Solution snapshot to validate. <para>待校验的解决方案快照。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.
    /// <para><paramref name="snapshot"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The format version is not 1, there are more than 10000
    /// sub-projects or more than 1000 node projects, a reference is <see langword="null"/>, two names collide
    /// case-insensitively, a relative path is blank or escapes the solution root, or more than one node
    /// project is active without being open.
    /// <para>格式版本不是 1、子项目超过 10000 个或节点项目超过 1000 个、引用为
    /// <see langword="null"/>、按大小写不敏感存在重名、相对路径为空或逃出解决方案根，或存在多个活动节点
    /// 项目/活动项未打开。</para></exception>
    /// <exception cref="ArgumentException">A sub-project or node-project name fails <see cref="ValidateName"/>.
    /// <para>某个子项目或节点项目名称未通过 <see cref="ValidateName"/>。</para></exception>
    public static void ValidateSolution(SolutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.FormatVersion != 1) throw new InvalidDataException("Unsupported solution format version.");
        if (snapshot.SubProjects is { } projects)
        {
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

        if (snapshot.NodeProjects is not { } nodeProjects) return;
        if (nodeProjects.Count > 1000) throw new InvalidDataException("Too many node projects.");
        var nodeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeCount = 0;
        foreach (var node in nodeProjects)
        {
            if (node is null) throw new InvalidDataException("Null node project reference.");
            ValidateName(node.Name);
            if (!nodeNames.Add(node.Name)) throw new InvalidDataException("Duplicate node project name.");
            if (string.IsNullOrWhiteSpace(node.RelativePath)) throw new InvalidDataException("Empty node project path.");
            if (!IsRelativePathWithinRoot(node.RelativePath)) throw new InvalidDataException("Node project path escapes the solution root.");
            if (!node.IsActive) continue;
            if (!node.IsOpen) throw new InvalidDataException("An active node project must be open.");
            activeCount++;
        }
        if (activeCount > 1) throw new InvalidDataException("More than one active node project.");
    }

    /// <summary>
    /// Checks that a stored <c>/</c>-separated relative path stays below the solution root.
    /// <para>校验存储的以 <c>/</c> 分隔的相对路径始终位于解决方案根之下。</para>
    /// </summary>
    /// <param name="relativePath">Path as stored in the solution file. <para>解决方案文件中存储的路径。</para></param>
    /// <returns><see langword="false"/> for a rooted path, an empty segment sequence, or any <c>..</c>
    /// segment; otherwise <see langword="true"/>.
    /// <para>绝对路径、空的路径序列或包含任一段 <c>..</c> 时返回 <see langword="false"/>，否则返回
    /// <see langword="true"/>。</para></returns>
    public static bool IsRelativePathWithinRoot(string relativePath)
        => relativePath.Length > 0
            && !Path.IsPathRooted(relativePath)
            && !relativePath.Split(['/', '\\']).Any(segment => segment == "..");

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