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
    /// <value>
    /// Version stamp serialized with the document; a freshly created instance reports 1.
    /// <para>随文档一起序列化的版本戳；新建实例的值为 1。</para>
    /// </value>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// Recently opened documents, stored as project-root-relative paths.
    /// <para>最近打开的文档，存为相对工程根目录的路径。</para>
    /// </summary>
    /// <value>
    /// Mutable list serialized as a JSON array; entries are written exactly as supplied.
    /// <para>可修改列表，序列化为 JSON 数组；条目按提供的原样写入。</para>
    /// </value>
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
    /// Asynchronously saves the solution settings to the specified path. The document is staged in a
    /// sibling temporary file and the destination is replaced only when the generated text differs from
    /// the existing content; a failed attempt leaves the previous file untouched.
    /// <para>异步保存解决方案设置到指定路径。文档先写入同级临时文件，仅当生成的文本与现有内容不同时才替换
    /// 目标；失败会保留既有文件。</para>
    /// </summary>
    /// <param name="path">
    /// Destination settings file path; must not be null or whitespace.
    /// <para>目标设置文件路径；不得为 null 或空白。</para>
    /// </param>
    /// <param name="settings">
    /// Settings instance to serialize; its contents are written as provided.
    /// <para>待序列化的设置实例；其内容按原样写入。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels staging or publishing; cancellation propagates and never replaces the destination.
    /// <para>取消暂存或发布；取消会向上传播且绝不替换目标文件。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the settings are persisted or the identical content was already on
    /// disk; <see langword="false"/> when the write failed and the error was logged instead.
    /// <para>设置已持久化、或磁盘上已有完全相同内容时返回 <see langword="true"/>；写入失败并已记录错误时
    /// 返回 <see langword="false"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or whitespace.
    /// <para><paramref name="path"/> 为 null、空或空白。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.
    /// <para><paramref name="settings"/> 为 null。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
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
    /// <param name="path">
    /// Settings file to read; must not be null or whitespace. The file is opened for shared reading and
    /// may be replaced concurrently.
    /// <para>待读取的设置文件；不得为 null 或空白。文件以共享读方式打开，允许被并发替换。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels deserialization; cancellation propagates instead of being reported as a parse failure.
    /// <para>取消反序列化；取消会向上传播，而不会被当作解析失败。</para>
    /// </param>
    /// <returns>
    /// The parsed settings, or <see langword="null"/> when the file does not exist, cannot be read,
    /// exceeds 32 MiB, or does not contain valid settings JSON; failures are logged.
    /// <para>解析后的设置；当文件不存在、无法读取、超过 32 MiB 或不是有效的设置 JSON 时返回
    /// <see langword="null"/>，失败会被记录。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or whitespace.
    /// <para><paramref name="path"/> 为 null、空或空白。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
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