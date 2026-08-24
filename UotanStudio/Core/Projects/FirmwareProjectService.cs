using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Services;

namespace UotanStudio.Core.Projects;

/// <summary>
/// <para>固件工程服务：创建、打开、保存工程文件与维护"最近工程"列表。</para>
/// Firmware project service: create, open, save project files and maintain the
/// "recent projects" list.
/// </summary>
public static class FirmwareProjectService
{
    /// <summary>
    /// <para>工程文件扩展名（含点）。</para>
    /// Project file extension (with dot).
    /// </summary>
    public const string Extension = ".uotproj";

    private const string RecentListFile = "recent_projects.json";

    /// <summary>
    /// <para>创建指定目录下一个新工程模型（未落盘）。</para>
    /// Creates a new project model in the given directory (not yet saved).
    /// </summary>
    /// <param name="directory"><para>工程目录。</para>Project directory.</param>
    /// <param name="name"><para>工程名称。</para>Project name.</param>
    /// <returns>新建的工程模型。</returns>
    public static FirmwareProject CreateProject(string directory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Directory.CreateDirectory(directory);
        return new FirmwareProject
        {
            Name = name,
            RootDirectory = Path.GetFullPath(directory),
            LastOpenedUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// <para>保存工程到磁盘，返回是否成功；成功后更新最近列表。</para>
    /// Saves the project to disk, returning success; on success updates the recent list.
    /// </summary>
    /// <param name="project"><para>要保存的工程。</para>The project to save.</param>
    /// <returns>保存是否成功。</returns>
    public static bool SaveProject(FirmwareProject project)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(project);
            project.RootDirectory = Path.GetFullPath(project.RootDirectory);
            Directory.CreateDirectory(project.RootDirectory);
            var path = ProjectFilePath(project);
            var json = JsonSerializer.Serialize(project, FirmwareProjectJsonContext.Default.FirmwareProject);
            File.WriteAllText(path, json);
            OpenPath = path;
            CurrentProject = project;
            RecordRecent(project);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "ProjectService", $"Save project failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// <para>从磁盘加载工程，返回模型；文件无效时返回 null。</para>
    /// Loads a project from disk, returning the model; null if the file is invalid.
    /// </summary>
    /// <param name="path"><para>工程文件路径。</para>The project file path.</param>
    /// <returns>加载的工程或 null。</returns>
    public static FirmwareProject? LoadProject(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var project = JsonSerializer.Deserialize(json, FirmwareProjectJsonContext.Default.FirmwareProject);
            if (project is null)
            {
                return null;
            }

            project.RootDirectory = Path.GetFullPath(project.RootDirectory);
            OpenPath = path;
            CurrentProject = project;
            RecordRecent(project);
            return project;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "ProjectService", $"Load project failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// <para>读取"最近工程"列表（按最后打开时间倒序，需健在）。</para>
    /// Reads the "recent projects" list (newest first, keeping only existing files).
    /// </summary>
    /// <returns>最近工程路径列表。</returns>
    public static IReadOnlyList<string> RecentProjects()
    {
        try
        {
            var path = RecentListPath();
            if (!File.Exists(path))
            {
                return [];
            }

            var json = File.ReadAllText(path);
            var listType = typeof(List<string>);
            var items = JsonSerializer.Deserialize(json, listType, FirmwareProjectJsonContext.Default) as List<string> ?? [];
            return [.. items.Where(File.Exists)];
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "ProjectService", $"Read recent projects failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// <para>获取当前打开的工程路径（含扩展名）；无打开工程时为空。</para>
    /// Gets the currently open project path (with extension); empty if none is open.
    /// </summary>
    public static string? OpenPath { get; private set; } = null;

    /// <summary>
    /// <para>获取当前打开工程的内存缓存模型；无打开工程时为空。
    /// 在 SaveProject/LoadProject 成功时刷新，供运行时门面直接读取，
    /// 避免每次访问都重新反序列化工程文件（内含最近工程写盘副作用）。</para>
    /// Gets the in-memory cached model of the currently open project; null when none is
    /// open. Refreshed on successful SaveProject/LoadProject so runtime facades can read
    /// it without re-deserializing the project file on every access (which carries a
    /// recent-projects write side effect).
    /// </summary>
    public static FirmwareProject? CurrentProject { get; private set; }

    /// <summary>
    /// <para>从模型推导工程文件完整路径。</para>
    /// Derives the full project file path from the model.
    /// </summary>
    public static string ProjectFilePath(FirmwareProject project) =>
        Path.Combine(project.RootDirectory, SafeFileName(project.Name)) + Extension;

    private static void RecordRecent(FirmwareProject project)
    {
        var path = ProjectFilePath(project);
        var list = RecentProjects().ToList();
        list.Remove(path);
        list.Insert(0, path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecentListPath())!);
            File.WriteAllText(RecentListPath(),
                JsonSerializer.Serialize(list, typeof(List<string>), FirmwareProjectJsonContext.Default));
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Warn, "ProjectService", $"Write recent projects failed: {ex.Message}");
        }
    }

    private static string RecentListPath() =>
        Path.Combine(AppDataDirectory(), RecentListFile);

    private static string AppDataDirectory()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(dir, "UotanStudio");
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Where(c => !invalid.Contains(c))]);
        return string.IsNullOrWhiteSpace(cleaned) ? "firmware" : cleaned;
    }
}

/// <summary>
/// <para><see cref="FirmwareProject"/> 与最近列表的 JSON 序列化上下文，AOT 兼容。</para>
/// JSON serialization context for <see cref="FirmwareProject"/> and the recent list; AOT compatible.
/// </summary>
[JsonSerializable(typeof(FirmwareProject))]
[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class FirmwareProjectJsonContext : JsonSerializerContext;