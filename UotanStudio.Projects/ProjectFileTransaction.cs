using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace UotanStudio.Services.Projects;

/// <summary>Stages metadata files and restores earlier files when publication throws. Not crash recovery.</summary>
internal sealed class ProjectFileTransaction : IDisposable
{
    private readonly List<(string Path, string Temporary, string Backup, bool Existed)> _files = [];
    private bool _retainBackups;

    internal async Task StageAsync<T>(string path, T value, JsonTypeInfo<T> type, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        if (_files.Any(f => string.Equals(f.Path, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new InvalidDataException("Duplicate transaction destination.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var suffix = ".transaction-" + Guid.NewGuid().ToString("N");
        var temporary = path + suffix;
        _files.Add((path, temporary, temporary + ".backup", File.Exists(path)));
        await AtomicJsonFile.WriteIfChangedAsync(temporary, value, type, token);
        if (File.Exists(path) && new FileInfo(path).Length <= 32 * 1024 * 1024
            && await File.ReadAllTextAsync(path, token) == await File.ReadAllTextAsync(temporary, token))
        {
            File.Delete(temporary);
            _files.RemoveAt(_files.Count - 1);
        }
    }

    internal void Commit(CancellationToken token)
    {
        var published = new List<(string Path, string Temporary, string Backup, bool Existed)>();
        try
        {
            foreach (var file in _files)
            {
                token.ThrowIfCancellationRequested();
                if (file.Existed) File.Copy(file.Path, file.Backup, overwrite: false);
                File.Move(file.Temporary, file.Path, overwrite: true);
                published.Add(file);
            }
        }
        catch (Exception publicationError)
        {
            var failures = new List<Exception> { publicationError };
            foreach (var file in published.AsEnumerable().Reverse())
            {
                try
                {
                    if (file.Existed) File.Move(file.Backup, file.Path, overwrite: true);
                    else File.Delete(file.Path);
                }
                catch (Exception rollbackError) { _retainBackups = true; failures.Add(rollbackError); }
            }
            if (failures.Count > 1) throw new AggregateException("Publication and rollback failed; transaction backups were retained.", failures);
            if (publicationError is UnauthorizedAccessException) throw new IOException("Cannot publish project metadata.", publicationError);
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            try { File.Delete(file.Temporary); if (!_retainBackups) File.Delete(file.Backup); }
            catch (IOException ex) { ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Projects", ex.Message); }
        }
    }
}
