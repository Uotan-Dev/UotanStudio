namespace UotanStudio.Services.Patching;

/// <summary>A directory bound to one execution. It never exposes a writable staging directory.<para>绑定到某一次执行的目录，绝不暴露可写的暂存目录。</para></summary>
public sealed class PatchWorkspace
{
    internal PatchWorkspace(PatchRun run, string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        PatchPath.RejectLinks(Root);
        if (!Directory.Exists(Root))
        {
            throw new DirectoryNotFoundException(Root);
        }
        Run = run;
    }

    /// <summary>Directory selected by the user, not a machine-specific plugin default.<para>由用户选择的目录，而非与机器相关的插件默认值。</para></summary>
    /// <value>The normalized workspace root.<para>规范化后的工作区根目录。</para></value>
    public string Root { get; }

    internal PatchRun Run { get; }

    /// <summary>Resolves a confined target path; the execution resolves its staged content separately.<para>解析受限的目标路径；执行的暂存内容由执行自身另行解析。</para></summary>
    /// <param name="relative">The relative path to resolve.<para>要解析的相对路径。</para></param>
    /// <returns>The full confined path inside the workspace root.<para>工作区根目录内的完整受限路径。</para></returns>
    public string Resolve(string relative) => PatchPath.Resolve(Root, relative);
}