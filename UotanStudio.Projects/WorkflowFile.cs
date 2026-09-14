using System.Text.Json;

namespace UotanStudio.Services.Projects;

/// <summary>Persists workflow data without a node editor or node factory.</summary>
public static class WorkflowFile
{
    /// <summary>Saves an unchanged-format workflow through an atomic temporary file.</summary>
    public static async Task<bool> SaveAsync(string path, WorkflowSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            await AtomicJsonFile.WriteIfChangedAsync(path, snapshot, WorkflowJsonContext.Default.WorkflowSnapshot, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence", ex.Message);
            return false;
        }
    }

    /// <summary>Loads workflow data; missing or invalid files return null, cancellation propagates.</summary>
    public static async Task<WorkflowSnapshot?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("Project state exceeds 32 MiB.");
            return await JsonSerializer.DeserializeAsync(stream, WorkflowJsonContext.Default.WorkflowSnapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence", ex.Message);
            return null;
        }
    }
}