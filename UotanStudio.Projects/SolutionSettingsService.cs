using System.Text.Json;
using System.Text.Json.Serialization;

namespace UotanStudio.Services.Projects;

/// <summary>
/// Solution settings JSON serialization context (AOT source-generated).
/// <para>解决方案设置 JSON 序列化上下文（AOT 源生成）。</para>
/// </summary>
[JsonSerializable(typeof(SolutionSettings))]
[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SolutionSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Per-sub-project settings persisted to <c>SubProjectRoot/.uotan/settings.json</c>; the public type name is retained for compatibility.
/// <para>持久化到子项目 <c>.uotan/settings.json</c> 的设置；保留公共类型名以兼容调用方。</para>
/// </summary>
public sealed class SolutionSettings
{
    /// <summary>
    /// Settings file format version (currently 1).
    /// <para>设置文件格式版本（当前为 1）。</para>
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// Recently opened documents, stored as project-root-relative paths.
    /// <para>最近打开的文档，存为相对工程根目录的路径。</para>
    /// </summary>
    public List<string> RecentDocuments { get; set; } = [];
}

/// <summary>
/// Persistence service for per-sub-project settings, reading/writing <c>settings.json</c> under the sub-project
/// state directory. AOT-friendly: uses the <see cref="SolutionSettingsJsonContext"/> source-generated JSON.
/// <para>子项目设置持久化服务，读写所属子项目状态目录下的 <c>settings.json</c>。AOT 友好：
/// 使用 <see cref="SolutionSettingsJsonContext"/> 源生成 JSON。</para>
/// </summary>
public static class SolutionSettingsService
{
    /// <summary>
    /// Asynchronously saves the solution settings to the specified path.
    /// <para>异步保存解决方案设置到指定路径。</para>
    /// </summary>
    public static async Task<bool> SaveAsync(string path, SolutionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            await AtomicJsonFile.WriteIfChangedAsync(path, settings, SolutionSettingsJsonContext.Default.SolutionSettings, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence", $"Save solution settings failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Asynchronously loads the solution settings from the specified path; returns <c>null</c> when the
    /// file is missing or fails to parse.
    /// <para>异步从指定路径加载解决方案设置；文件缺失或解析失败时返回 <c>null</c>。</para>
    /// </summary>
    public static async Task<SolutionSettings?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("Project state exceeds 32 MiB.");
            return await JsonSerializer.DeserializeAsync(stream, SolutionSettingsJsonContext.Default.SolutionSettings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence", $"Load solution settings failed: {ex.Message}");
            return null;
        }
    }
}