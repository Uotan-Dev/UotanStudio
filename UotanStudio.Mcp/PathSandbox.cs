using System.IO;

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
    private readonly string _root;

    public PathSandbox(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>Gets the canonical sandbox root. <para>获取规范化的沙箱根目录。</para></summary>
    public string Root => _root;

    /// <summary>
    /// Resolves a client-supplied path against the sandbox and verifies containment.
    /// <para>按沙箱解析客户端路径并验证包含关系。</para>
    /// </summary>
    /// <param name="path">Absolute path (must stay inside the root) or root-relative path. <para>绝对路径（必须位于根内）或相对根目录的路径。</para></param>
    /// <param name="mustExist">When true, requires the resolved path to exist. <para>为 true 时要求解析后的路径必须存在。</para></param>
    /// <exception cref="UnauthorizedAccessException">The path escapes the sandbox. <para>路径逃逸沙箱时抛出。</para></exception>
    public string Resolve(string path, bool mustExist = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_root, path));

        // Path prefix alone is not enough ("C:\root" vs "C:\root2"); require a separator at the
        // boundary or an exact root match, using case-insensitive comparison on Windows only.
        // 仅路径前缀不够（"C:\root" vs "C:\root2"）；要求边界处是分隔符或恰好等于根目录，
        // 仅在 Windows 上使用大小写不敏感比较。
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
    public string ResolveOutput(string path) => Resolve(path);
}