using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.Services;

/// <summary>
/// Solution/project JSON serialization context (AOT source-generated).
/// <para>解决方案/项目 JSON 序列化上下文（AOT 源生成）。</para>
/// </summary>
[JsonSerializable(typeof(SolutionSnapshot))]
[JsonSerializable(typeof(FirmwareProjectSnapshot))]
[JsonSerializable(typeof(NodeProjectSnapshot))]
[JsonSerializable(typeof(List<ImageEntryDto>))]
[JsonSerializable(typeof(ImageEntryDto))]
[JsonSerializable(typeof(ProjectSettingsDto))]
[JsonSerializable(typeof(List<NodeDto>))]
[JsonSerializable(typeof(List<ConnectionDto>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SolutionJsonContext : JsonSerializerContext;

/// <summary>
/// Solution-level persistence service, serializing/deserializing the VS-like project layout: a
/// <c>.uotan</c> solution file referencing a firmware project (<c>.uotanfw</c>) and a node project
/// (<c>.uotannode</c>). AOT-friendly: uses the <see cref="SolutionJsonContext"/> source-generated JSON,
/// no reflection.
/// <para>解决方案级持久化服务，序列化/反序列化 VS 式工程布局：<c>.uotan</c> 解决方案文件引用固件项目
/// （<c>.uotanfw</c>）与节点项目（<c>.uotannode</c>）。AOT 友好：使用 <see cref="SolutionJsonContext"/>
/// 源生成 JSON，零反射。</para>
/// </summary>
public static class SolutionPersistenceService
{
    /// <summary>
    /// Asynchronously saves the solution snapshot to the specified path.
    /// <para>异步保存解决方案快照到指定路径。</para>
    /// </summary>
    public static async Task<bool> SaveSolutionAsync(string path, SolutionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        return await SaveAsync(path, snapshot, SolutionJsonContext.Default.SolutionSnapshot, "solution", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously loads the solution snapshot from the specified path; returns <c>null</c> on failure.
    /// <para>异步从指定路径加载解决方案快照；失败时返回 <c>null</c>。</para>
    /// </summary>
    public static async Task<SolutionSnapshot?> LoadSolutionAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await LoadAsync(path, SolutionJsonContext.Default.SolutionSnapshot, "solution", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously saves the firmware project snapshot (images + packing settings) to the specified path.
    /// <para>异步保存固件项目快照（镜像清单 + 打包设置）到指定路径。</para>
    /// </summary>
    public static async Task<bool> SaveFirmwareProjectAsync(string path, FirmwareProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        return await SaveAsync(path, snapshot, SolutionJsonContext.Default.FirmwareProjectSnapshot, "firmware project", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously loads the firmware project snapshot from the specified path; returns <c>null</c> on failure.
    /// <para>异步从指定路径加载固件项目快照；失败时返回 <c>null</c>。</para>
    /// </summary>
    public static async Task<FirmwareProjectSnapshot?> LoadFirmwareProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await LoadAsync(path, SolutionJsonContext.Default.FirmwareProjectSnapshot, "firmware project", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously saves the node project snapshot (node canvas) to the specified path.
    /// <para>异步保存节点项目快照（节点画布）到指定路径。</para>
    /// </summary>
    public static async Task<bool> SaveNodeProjectAsync(string path, NodeProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        return await SaveAsync(path, snapshot, SolutionJsonContext.Default.NodeProjectSnapshot, "node project", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously loads the node project snapshot from the specified path; returns <c>null</c> on failure.
    /// <para>异步从指定路径加载节点项目快照；失败时返回 <c>null</c>。</para>
    /// </summary>
    public static async Task<NodeProjectSnapshot?> LoadNodeProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await LoadAsync(path, SolutionJsonContext.Default.NodeProjectSnapshot, "node project", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> SaveAsync<T>(string path, T snapshot, JsonTypeInfo<T> typeInfo, string label, CancellationToken cancellationToken)
    {
        var log = LogService.Instance;
        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, snapshot, typeInfo, cancellationToken).ConfigureAwait(false);
            log.Write(LogLevel.Info, "Persistence", $"Saved {label}: {path}");
            return true;
        }
        catch (Exception ex)
        {
            log.Write(LogLevel.Error, "Persistence", $"Save {label} failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<T?> LoadAsync<T>(string path, JsonTypeInfo<T> typeInfo, string label, CancellationToken cancellationToken)
    {
        var log = LogService.Instance;
        if (!File.Exists(path))
        {
            log.Write(LogLevel.Warn, "Persistence", $"Load {label} aborted: missing file '{path}'");
            return default;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var result = await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
            log.Write(LogLevel.Info, "Persistence", $"Loaded {label}: {path}");
            return result;
        }
        catch (Exception ex)
        {
            log.Write(LogLevel.Error, "Persistence", $"Load {label} failed: {ex.Message}");
            return default;
        }
    }
}

/// <summary>
/// Solution snapshot, carrying the <c>.uotan</c> solution file structure: solution name and the project
/// files it references (relative to the solution file's directory).
/// <para>解决方案快照，承载 <c>.uotan</c> 解决方案文件结构：解决方案名及其引用的项目文件（相对解决方案文件目录）。</para>
/// </summary>
public sealed class SolutionSnapshot
{
    /// <summary>
    /// Solution file format version (currently 1).
    /// <para>解决方案文件格式版本（当前为 1）。</para>
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// Solution name (without extension).
    /// <para>解决方案名（不含扩展名）。</para>
    /// </summary>
    public string SolutionName { get; set; } = string.Empty;

    /// <summary>
    /// Firmware project file path relative to the solution file's directory.
    /// <para>固件项目文件路径（相对解决方案文件目录）。</para>
    /// </summary>
    public string FirmwareProjectPath { get; set; } = string.Empty;

    /// <summary>
    /// Node project file path relative to the solution file's directory.
    /// <para>节点项目文件路径（相对解决方案文件目录）。</para>
    /// </summary>
    public string NodeProjectPath { get; set; } = string.Empty;
}

/// <summary>
/// Firmware project snapshot, carrying the <c>.uotanfw</c> project file structure: image list and packing settings.
/// <para>固件项目快照，承载 <c>.uotanfw</c> 项目文件结构：镜像清单与打包设置。</para>
/// </summary>
public sealed class FirmwareProjectSnapshot
{
    /// <summary>
    /// Project file format version (currently 1).
    /// <para>项目文件格式版本（当前为 1）。</para>
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// Project name (without extension).
    /// <para>项目名（不含扩展名）。</para>
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Image list (all paths relative to the project root directory).
    /// <para>镜像清单（全部为相对工程根目录的路径）。</para>
    /// </summary>
    public List<ImageEntryDto> Images { get; set; } = [];

    /// <summary>
    /// Packing settings snapshot.
    /// <para>打包设置快照。</para>
    /// </summary>
    public ProjectSettingsDto? Settings { get; set; }
}

/// <summary>
/// Node project snapshot, carrying the <c>.uotannode</c> project file structure: node canvas.
/// <para>节点项目快照，承载 <c>.uotannode</c> 项目文件结构：节点画布。</para>
/// </summary>
public sealed class NodeProjectSnapshot
{
    /// <summary>
    /// Project file format version (currently 1).
    /// <para>项目文件格式版本（当前为 1）。</para>
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// Project name (without extension).
    /// <para>项目名（不含扩展名）。</para>
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Node DTO list.
    /// <para>节点 DTO 列表。</para>
    /// </summary>
    public List<NodeDto> Nodes { get; set; } = [];

    /// <summary>
    /// Connection DTO list.
    /// <para>连接 DTO 列表。</para>
    /// </summary>
    public List<ConnectionDto> Connections { get; set; } = [];
}