namespace UotanStudio.Mcp;

/// <summary>
/// Confines every file path accepted from an MCP client to the configured root directory.
/// <para>把 MCP 客户端传入的所有文件路径限制在配置的根目录内。</para>
/// Absolute paths outside the root and traversal escapes (<c>..</c>) are rejected; the full path
/// is canonicalized before the containment check so segments cannot smuggle past comparison.
/// <para>根目录外的绝对路径与目录穿越（<c>..</c>）都会被拒绝；先规范化全路径再做包含比较，
/// 防止路径段绕过检查。</para>
/// </summary>
public sealed class PathSandbox
{
    private readonly string? _root;

    /// <summary>
    /// <para>不受限沙箱：接受任意路径，供桌面宿主启用的本地 MCP 暴露使用（监听回环地址、由用户显式开启）。</para>
    /// Unrestricted sandbox: accepts any path; used by the local MCP exposure the desktop host starts
    /// when the user turns it on (loopback listener, explicit opt-in).
    /// </summary>
    /// <value>A shared instance whose <see cref="Root"/> is <see langword="null"/> and whose containment checks always pass.
    /// <para>共享实例：其 <see cref="Root"/> 为 <see langword="null"/>，包含性检查恒通过。</para></value>
    public static PathSandbox Unrestricted { get; } = new();

    /// <summary>
    /// Creates a sandbox confined to the given root directory.
    /// <para>创建限定在给定根目录内的沙箱。</para>
    /// </summary>
    /// <param name="root">Directory treated as the containment boundary; it is normalized to a full path with any trailing separator removed.
    /// <para>作为包含边界的目录；会被规范为完整路径并去掉尾部分隔符。</para></param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is empty or only whitespace.
    /// <para><paramref name="root"/> 为空或仅包含空白。</para></exception>
    public PathSandbox(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private PathSandbox() => _root = null;

    /// <summary>Gets the canonical sandbox root, or null when unrestricted.
    /// <para>获取规范化的沙箱根目录；不受限时为 null。</para></summary>
    /// <value>The normalized, separator-trimmed absolute root that <see cref="Resolve"/> compares against; <see langword="null"/> marks the unrestricted sandbox.
    /// <para>已规范化并去掉尾分隔符的绝对根目录，<see cref="Resolve"/> 以此比较；不受限沙箱时为 <see langword="null"/>。</para></value>
    public string? Root => _root;

    /// <summary>Gets a value indicating whether every path is accepted.
    /// <para>获取是否接受任意路径。</para></summary>
    /// <value><see langword="true"/> for the unrestricted sandbox, where only normalization and the existence check apply.
    /// <para>不受限沙箱为 <see langword="true"/>，此时只应用规范化与存在性检查。</para></value>
    public bool IsUnrestricted => _root is null;

    /// <summary>
    /// Resolves a client-supplied path against the sandbox and verifies containment.
    /// <para>按沙箱解析客户端路径并验证包含关系。</para>
    /// </summary>
    /// <example><code>
    /// var sandbox = new PathSandbox(@"D:\firmware");
    /// string image = sandbox.Resolve("images/boot.img", mustExist: true);
    /// </code></example>
    /// <param name="path">Absolute path (must stay inside the root) or root-relative path. <para>绝对路径（必须位于根内）或相对根目录的路径。</para></param>
    /// <param name="mustExist">When true, requires the resolved path to exist. <para>为 true 时要求解析后的路径必须存在。</para></param>
    /// <returns>The normalized absolute path. Containment compares case-insensitively on Windows and case-sensitively elsewhere, and the root itself counts as contained.
    /// <para>规范化后的绝对路径。包含比较在 Windows 上不区分大小写、在其他平台区分大小写，根目录本身也算被包含。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or only whitespace. <para><paramref name="path"/> 为空或仅包含空白。</para></exception>
    /// <exception cref="UnauthorizedAccessException">The path escapes the sandbox. <para>路径逃逸沙箱时抛出。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="mustExist"/> is <see langword="true"/> and neither a file nor a directory exists at the resolved path.
    /// <para><paramref name="mustExist"/> 为 <see langword="true"/> 且解析路径上既无文件也无目录。</para></exception>
    public string Resolve(string path, bool mustExist = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (_root is null)
        {
            var unreserved = Path.GetFullPath(path);
            if (mustExist && !File.Exists(unreserved) && !Directory.Exists(unreserved))
            {
                throw new FileNotFoundException($"File not found: {unreserved}", unreserved);
            }

            return unreserved;
        }

        var full = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_root, path));

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var contained = string.Equals(full, _root, comparison)
            || (full.StartsWith(_root, comparison)
                && full.Length > _root.Length
                && (full[_root.Length] == Path.DirectorySeparatorChar || full[_root.Length] == Path.AltDirectorySeparatorChar));

        if (!contained)
        {
            throw new UnauthorizedAccessException($"Path is outside the MCP sandbox root: {path}");
        }

        if (mustExist && !File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException($"File not found within the sandbox: {full}", full);
        }

        return full;
    }

    /// <summary>
    /// Resolves an output path for creation: containment is enforced, the file may not exist yet.
    /// <para>解析用于创建的输出路径：强制包含关系，文件可以尚不存在。</para>
    /// </summary>
    /// <param name="path">Absolute or root-relative destination path. <para>绝对路径或相对根目录的目标路径。</para></param>
    /// <returns>The normalized absolute destination path, with no requirement that it already exists.
    /// <para>规范化后的绝对目标路径，不要求该路径已存在。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or only whitespace.
    /// <para><paramref name="path"/> 为空或仅包含空白。</para></exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="path"/> resolves outside the sandbox root.
    /// <para><paramref name="path"/> 解析到沙箱根目录之外。</para></exception>
    public string ResolveOutput(string path) => Resolve(path);
}