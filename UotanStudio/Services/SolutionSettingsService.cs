using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.Services;

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
/// Per-solution settings persisted to <c>RootDirectory/.uotan/settings.json</c> (like VS's <c>.vs/</c> state).
/// <para>持久化到 <c>RootDirectory/.uotan/settings.json</c> 的解决方案级设置（类似 VS 的 <c>.vs/</c> 状态）。</para>
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
/// Persistence service for per-solution settings, reading/writing <c>settings.json</c> under the solution
/// state directory. AOT-friendly: uses the <see cref="SolutionSettingsJsonContext"/> source-generated JSON.
/// <para>解决方案级设置持久化服务，读写解决方案状态目录下的 <c>settings.json</c>。AOT 友好：
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
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, settings, SolutionSettingsJsonContext.Default.SolutionSettings, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Persistence", $"Save solution settings failed: {ex.Message}");
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
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, SolutionSettingsJsonContext.Default.SolutionSettings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Persistence", $"Load solution settings failed: {ex.Message}");
            return null;
        }
    }
}