using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services.Mcp;

namespace UotanStudio.Services;

/// <summary>
/// <para>应用程序设置数据模型，包含所有需要持久化的设置项。</para>
/// Application settings data model, containing all settings that need to be persisted.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// <para>获取或设置当前语言的文化名称（如 "en-US"、"zh-CN"）。空字符串表示跟随系统。</para>
    /// Gets or sets the culture name of the current language (e.g., "en-US", "zh-CN"). Empty string means follow system.
    /// </summary>
    [JsonPropertyName("cultureName")]
    public string CultureName { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置主题模式。</para>
    /// Gets or sets the theme mode.
    /// </summary>
    [JsonPropertyName("themeMode")]
    [JsonConverter(typeof(JsonStringEnumConverter<ThemeMode>))]
    public ThemeMode ThemeMode { get; set; }

    /// <summary>
    /// <para>获取或设置是否启用自动保存。</para>
    /// Gets or sets whether auto-save is enabled.
    /// </summary>
    [JsonPropertyName("autoSave")]
    public bool AutoSave { get; set; }

    /// <summary>
    /// <para>获取或设置默认解包工具路径。</para>
    /// Gets or sets the default unpack tool path.
    /// </summary>
    [JsonPropertyName("unpackToolPath")]
    public string UnpackToolPath { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置临时文件目录。</para>
    /// Gets or sets the temporary file directory.
    /// </summary>
    [JsonPropertyName("tempDirectory")]
    public string TempDirectory { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置解包后是否自动校验。</para>
    /// Gets or sets whether to auto-verify after unpacking.
    /// </summary>
    [JsonPropertyName("autoVerifyAfterUnpack")]
    public bool AutoVerifyAfterUnpack { get; set; } = true;

    /// <summary>
    /// <para>获取或设置 ADB 路径。</para>
    /// Gets or sets the ADB path.
    /// </summary>
    [JsonPropertyName("adbPath")]
    public string AdbPath { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置 Fastboot 路径。</para>
    /// Gets or sets the Fastboot path.
    /// </summary>
    [JsonPropertyName("fastbootPath")]
    public string FastbootPath { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置设备超时时间（秒）。</para>
    /// Gets or sets the device timeout in seconds.
    /// </summary>
    [JsonPropertyName("deviceTimeoutSeconds")]
    public int DeviceTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// <para>获取或设置烧录前是否自动备份。</para>
    /// Gets or sets whether to auto-backup before flashing.
    /// </summary>
    [JsonPropertyName("autoBackupBeforeFlash")]
    public bool AutoBackupBeforeFlash { get; set; }

    /// <summary>
    /// <para>获取或设置是否启用网格对齐。</para>
    /// Gets or sets whether snap-to-grid is enabled.
    /// </summary>
    [JsonPropertyName("snapToGrid")]
    public bool SnapToGrid { get; set; } = true;

    /// <summary>
    /// <para>获取或设置网格大小。</para>
    /// Gets or sets the grid size.
    /// </summary>
    [JsonPropertyName("gridSize")]
    public double GridSize { get; set; } = 20;

    /// <summary>
    /// <para>获取或设置连接线样式索引。</para>
    /// Gets or sets the connection line style index.
    /// </summary>
    [JsonPropertyName("connectionStyleIndex")]
    public int ConnectionStyleIndex { get; set; }

    /// <summary>
    /// <para>获取或设置最近打开的解决方案路径列表（最新在前）。</para>
    /// Gets or sets the list of recently opened solution paths (most recent first).
    /// </summary>
    [JsonPropertyName("recentProjects")]
    public List<string> RecentProjects { get; set; } = [];

    /// <summary>
    /// <para>获取或设置 MCP 服务器连接列表（工具箱 MCP 节点按名称调用其工具）。</para>
    /// Gets or sets the MCP server connection list (the toolbox MCP nodes call their tools by name).
    /// </summary>
    [JsonPropertyName("mcpServers")]
    public List<McpServerConfig> McpServers { get; set; } = [];

    /// <summary>
    /// <para>获取或设置是否把本应用固件能力暴露为 MCP 服务器（HTTP 端口见 <see cref="McpExposePort"/>）。</para>
    /// Gets or sets whether to expose this application's firmware capabilities as an MCP server
    /// (HTTP port in <see cref="McpExposePort"/>).
    /// </summary>
    [JsonPropertyName("mcpExposeEnabled")]
    public bool McpExposeEnabled { get; set; }

    /// <summary>
    /// <para>获取或设置 MCP 服务器暴露端口。</para>
    /// Gets or sets the MCP server exposure port.
    /// </summary>
    [JsonPropertyName("mcpExposePort")]
    public int McpExposePort { get; set; } = 18321;
}

/// <summary>
/// <para><see cref="AppSettings"/> 的 JSON 序列化上下文，为 AOT 兼容提供源生成支持。</para>
/// JSON serialization context for <see cref="AppSettings"/>, providing source generation support for AOT compatibility.
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ThemeMode))]
[JsonSerializable(typeof(McpServerConfig))]
[JsonSerializable(typeof(McpTransportKind))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<ThemeMode>), typeof(JsonStringEnumConverter<McpTransportKind>)])]
internal partial class AppSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// <para>设置持久化服务，负责应用程序设置的序列化、反序列化及文件存储。</para>
/// Settings persistence service, responsible for serialization, deserialization, and file storage of application settings.
/// </summary>
public class SettingsPersistenceService
{
    private static readonly Lazy<SettingsPersistenceService> LazyInstance = new(() => new SettingsPersistenceService());

    private readonly string _settingsDirectory;
    private readonly string _settingsFilePath;

    /// <summary>
    /// <para>获取设置持久化服务的单例实例。</para>
    /// Gets the singleton instance of the settings persistence service.
    /// </summary>
    public static SettingsPersistenceService Instance => LazyInstance.Value;

    /// <summary>
    /// <para>获取设置文件的完整路径。</para>
    /// Gets the full path of the settings file.
    /// </summary>
    public string SettingsFilePath => _settingsFilePath;

    private SettingsPersistenceService()
    {
        _settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UotanStudio");
        _settingsFilePath = Path.Combine(_settingsDirectory, "settings.json");
    }

    /// <summary>
    /// <para>从文件加载应用程序设置。若文件不存在或已损坏，则返回默认设置。</para>
    /// Loads application settings from file. Returns default settings if the file does not exist or is corrupted.
    /// </summary>
    /// <returns>
    /// <para>加载的 <see cref="AppSettings"/> 实例；加载失败时返回默认值实例。</para>
    /// The loaded <see cref="AppSettings"/> instance; returns a default instance on failure.
    /// </returns>
    public AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsFilePath);
            var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();

            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// <para>将应用程序设置序列化并保存到文件。</para>
    /// Serializes and saves application settings to file.
    /// </summary>
    /// <param name="settings">
    /// <para>要保存的应用程序设置实例。</para>
    /// The application settings instance to save.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <para>当 <paramref name="settings"/> 为 null 时抛出。</para>
    /// Thrown when <paramref name="settings"/> is null.
    /// </exception>
    public void SaveSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            if (!Directory.Exists(_settingsDirectory))
            {
                Directory.CreateDirectory(_settingsDirectory);
            }

            var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
            var tempPath = _settingsFilePath + ".tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _settingsFilePath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* cleanup best-effort */ }
                throw;
            }
        }
        catch (IOException ex)
        {
            WeakReferenceMessenger.Default.Send(new SettingsSaveFailedMessage(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            WeakReferenceMessenger.Default.Send(new SettingsSaveFailedMessage(ex.Message));
        }
    }

    /// <summary>
    /// <para>更新最近打开的解决方案列表：以磁盘上最新设置为基础合并写入，避免覆盖其他设置项。</para>
    /// Updates the recently opened solutions list: merges on top of the latest settings on disk so other
    /// settings are never overwritten.
    /// </summary>
    /// <param name="recentProjects">
    /// <para>新的最近打开路径列表（最新在前）。</para>
    /// The new recent project paths (most recent first).
    /// </param>
    public void UpdateRecentProjects(List<string> recentProjects)
    {
        var settings = LoadSettings();
        settings.RecentProjects = recentProjects ?? [];
        SaveSettings(settings);
    }

    /// <summary>
    /// <para>重置所有设置为默认值并保存到文件。</para>
    /// Resets all settings to default values and saves to file.
    /// </summary>
    /// <returns>
    /// <para>包含默认值的新 <see cref="AppSettings"/> 实例。</para>
    /// A new <see cref="AppSettings"/> instance with default values.
    /// </returns>
    public AppSettings ResetToDefaults()
    {
        var defaults = new AppSettings();
        SaveSettings(defaults);
        return defaults;
    }
}