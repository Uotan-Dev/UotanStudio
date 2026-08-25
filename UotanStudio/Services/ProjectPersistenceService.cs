using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.Services;

/// <summary>
/// <para>工程快照 JSON 序列化上下文（AOT 源生成）。</para>
/// Project snapshot JSON serialization context (AOT source-generated).
/// </summary>
[JsonSerializable(typeof(ProjectSnapshot))]
[JsonSerializable(typeof(List<ImageEntryDto>))]
[JsonSerializable(typeof(ImageEntryDto))]
[JsonSerializable(typeof(ProjectSettingsDto))]
[JsonSerializable(typeof(List<NodeDto>))]
[JsonSerializable(typeof(List<ConnectionDto>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class ProjectJsonContext : JsonSerializerContext;

/// <summary>
/// <para>工程文件持久化服务，将完整工程状态（工程名、镜像清单、打包设置、节点画布）序列化/反序列化到
/// <c>.uotan</c> 工程文件。AOT 友好：使用 <see cref="ProjectJsonContext"/> 源生成 JSON，零反射。</para>
/// Project file persistence service, serializing/deserializing the complete project state (project name,
/// image list, packing settings, node canvas) to a <c>.uotan</c> project file. AOT-friendly: uses the
/// <see cref="ProjectJsonContext"/> source-generated JSON, no reflection.
/// </summary>
public static class ProjectPersistenceService
{
    /// <summary>
    /// <para>异步保存工程快照到指定路径。</para>
    /// Asynchronously saves the project snapshot to the specified path.
    /// </summary>
    /// <param name="path">
    /// <para>目标工程文件路径。</para>
    /// Target project file path.
    /// </param>
    /// <param name="snapshot">
    /// <para>要保存的工程快照。</para>
    /// The project snapshot to save.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// Cancellation token.
    /// </param>
    /// <returns>
    /// <para>保存是否成功。</para>
    /// Whether the save succeeded.
    /// </returns>
    public static async Task<bool> SaveAsync(string path, ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var log = LogService.Instance;
        if (string.IsNullOrEmpty(path))
        {
            log.Write(LogLevel.Warn, "Persistence", "Save aborted: empty path");
            return false;
        }

        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, snapshot, ProjectJsonContext.Default.ProjectSnapshot, cancellationToken).ConfigureAwait(false);
            log.Write(LogLevel.Info, "Persistence", $"Saved project: {path} ({snapshot.Images.Count} images, {snapshot.Nodes.Count} nodes, {snapshot.Connections.Count} connections)");
            return true;
        }
        catch (System.Exception ex)
        {
            log.Write(LogLevel.Error, "Persistence", $"Save failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// <para>异步从指定路径加载工程快照；文件缺失或解析失败时返回 <c>null</c>。</para>
    /// Asynchronously loads the project snapshot from the specified path; returns <c>null</c> when the
    /// file is missing or fails to parse.
    /// </summary>
    /// <param name="path">
    /// <para>工程文件路径。</para>
    /// Project file path.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// Cancellation token.
    /// </param>
    /// <returns>
    /// <para>加载的工程快照；失败时为 <c>null</c>。</para>
    /// The loaded project snapshot; <c>null</c> on failure.
    /// </returns>
    public static async Task<ProjectSnapshot?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var log = LogService.Instance;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            log.Write(LogLevel.Warn, "Persistence", $"Load aborted: missing file '{path}'");
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer.DeserializeAsync(stream, ProjectJsonContext.Default.ProjectSnapshot, cancellationToken).ConfigureAwait(false);
            log.Write(LogLevel.Info, "Persistence", $"Loaded project: {path} ({snapshot?.Images.Count ?? 0} images, {snapshot?.Nodes.Count ?? 0} nodes, {snapshot?.Connections.Count ?? 0} connections)");
            return snapshot;
        }
        catch (System.Exception ex)
        {
            log.Write(LogLevel.Error, "Persistence", $"Load failed: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// <para>工程快照，承载 <c>.uotan</c> 工程文件的完整结构。</para>
/// Project snapshot, carrying the complete structure of the <c>.uotan</c> project file.
/// </summary>
public sealed class ProjectSnapshot
{
    /// <summary>
    /// <para>工程文件格式版本（当前为 2：含工程信息）。</para>
    /// Project file format version (currently 2: includes project info).
    /// </summary>
    public int FormatVersion { get; set; } = 2;

    /// <summary>
    /// <para>工程名（不含扩展名）。</para>
    /// Project name (without extension).
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// <para>镜像清单（全部为相对工程根目录的路径）。</para>
    /// Image list (all paths relative to the project root directory).
    /// </summary>
    public List<ImageEntryDto> Images { get; set; } = [];

    /// <summary>
    /// <para>打包设置快照。</para>
    /// Packing settings snapshot.
    /// </summary>
    public ProjectSettingsDto? Settings { get; set; }

    /// <summary>
    /// <para>节点 DTO 列表。</para>
    /// List of node DTOs.
    /// </summary>
    public List<NodeDto> Nodes { get; set; } = [];

    /// <summary>
    /// <para>连接 DTO 列表。</para>
    /// List of connection DTOs.
    /// </summary>
    public List<ConnectionDto> Connections { get; set; } = [];
}

/// <summary>
/// <para>镜像条目数据传输对象，承载镜像在项目内的相对路径与元信息。</para>
/// Image entry data transfer object, carrying the image's project-relative path and metadata.
/// </summary>
public sealed class ImageEntryDto
{
    /// <summary>
    /// <para>相对工程根目录的镜像路径。</para>
    /// Image path relative to the project root directory.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// <para>镜像文件大小（字节）。</para>
    /// Image file size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// <para>镜像格式标识（boot/payload/cpio/oppo/sparse/raw/unknown 等）。</para>
    /// Image format identifier (boot/payload/cpio/oppo/sparse/raw/unknown etc.).
    /// </summary>
    public string Format { get; set; } = string.Empty;
}

/// <summary>
/// <para>打包设置数据传输对象，与打包设置面板的字段一一对应。</para>
/// Packing settings data transfer object, mapping one-to-one with the packing settings panel fields.
/// </summary>
public sealed class ProjectSettingsDto
{
    /// <summary>
    /// <para>镜像类型（"EXT4" 或 "EROFS"）。</para>
    /// Image type ("EXT4" or "EROFS").
    /// </summary>
    public string ImageType { get; set; } = "EROFS";

    /// <summary>
    /// <para>镜像格式（"RAW" 或 "SPARSE"）。</para>
    /// Image format ("RAW" or "SPARSE").
    /// </summary>
    public string ImageFormat { get; set; } = "RAW";

    /// <summary>
    /// <para>EXT4 是否可读写（RO 时为 <c>false</c>）。</para>
    /// Whether EXT4 is read-write (<c>false</c> means read-only).
    /// </summary>
    public bool Ext4ReadWrite { get; set; } = true;

    /// <summary>
    /// <para>大小计算是否自动（否则使用原大小）。</para>
    /// Whether size calculation is automatic (otherwise the original size is used).
    /// </summary>
    public bool SizeAuto { get; set; } = true;

    /// <summary>
    /// <para>EXT4 是否压缩。</para>
    /// Whether EXT4 is compressed.
    /// </summary>
    public bool Ext4Compress { get; set; } = true;

    /// <summary>
    /// <para>EROFS 压缩算法（"lz4" 或 "lz4hc"）。</para>
    /// EROFS compression algorithm ("lz4" or "lz4hc").
    /// </summary>
    public string ErofsAlgorithm { get; set; } = "lz4";

    /// <summary>
    /// <para>EROFS 压缩等级。</para>
    /// EROFS compression level.
    /// </summary>
    public int ErofsCompLevel { get; set; } = 8;

    /// <summary>
    /// <para>SUPER 分区大小（字符串）。</para>
    /// SUPER partition size (string).
    /// </summary>
    public string SuperSize { get; set; } = "9126805504";

    /// <summary>
    /// <para>SUPER 分区簇名。</para>
    /// SUPER partition cluster name.
    /// </summary>
    public string SuperName { get; set; } = "qti_dynamic_partitions";

    /// <summary>
    /// <para>SUPER 格式（"RAW" 或 "SPARSE"）。</para>
    /// SUPER format ("RAW" or "SPARSE").
    /// </summary>
    public string SuperFormat { get; set; } = "RAW";

    /// <summary>
    /// <para>BR 压缩等级。</para>
    /// BR compression level.
    /// </summary>
    public int BrCompLevel { get; set; } = 3;

    /// <summary>
    /// <para>UTC 时间戳（字符串）。</para>
    /// UTC timestamp (string).
    /// </summary>
    public string UtcTime { get; set; } = "1230768000";

    /// <summary>
    /// <para>DAT 分段数量（字符串）。</para>
    /// DAT segment count (string).
    /// </summary>
    public string DatSegments { get; set; } = "15";
}