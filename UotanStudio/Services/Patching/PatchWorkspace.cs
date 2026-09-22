namespace UotanStudio.Services.Patching;

/// <summary>A directory bound to one execution. It never exposes a writable staging directory.</summary>
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

    /// <summary>Directory selected by the user, not a machine-specific plugin default.</summary>
    public string Root { get; }

    internal PatchRun Run { get; }

    /// <summary>Resolves a confined target path; the execution resolves its staged content separately.</summary>
    public string Resolve(string relative) => PatchPath.Resolve(Root, relative);
}