using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UotanStudio.Services;

/// <summary>
/// <para>应用程序设置数据模型，包含所有需要持久化的设置项。</para>
/// Application settings data model, containing all settings that need to be persisted.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// <para>获取或设置选中的语言索引。</para>
    /// Gets or sets the selected language index.
    /// </summary>
    [JsonPropertyName("selectedLanguageIndex")]
    public int SelectedLanguageIndex { get; set; }

    /// <summary>
    /// <para>获取或设置选中的主题索引。</para>
    /// Gets or sets the selected theme index.
    /// </summary>
    [JsonPropertyName("selectedThemeIndex")]
    public int SelectedThemeIndex { get; set; }

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
}

/// <summary>
/// <para><see cref="AppSettings"/> 的 JSON 序列化上下文，为 AOT 兼容提供源生成支持。</para>
/// JSON serialization context for <see cref="AppSettings"/>, providing source generation support for AOT compatibility.
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
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
            var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
            return settings ?? new AppSettings();
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
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (IOException)
        {
            // Silently ignore IO errors to avoid crashing the application.
            // Settings will be lost for this session but the app remains usable.
        }
        catch (UnauthorizedAccessException)
        {
            // Silently ignore access errors to avoid crashing the application.
        }
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
