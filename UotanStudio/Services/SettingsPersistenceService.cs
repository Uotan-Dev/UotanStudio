using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Messages;
using UotanStudio.Models;

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
}

/// <summary>
/// <para><see cref="AppSettings"/> 的 JSON 序列化上下文，为 AOT 兼容提供源生成支持。</para>
/// JSON serialization context for <see cref="AppSettings"/>, providing source generation support for AOT compatibility.
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ThemeMode))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<ThemeMode>)])]
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
            AppSettings settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();

            // 迁移旧格式：如果 cultureName 为空但 JSON 中存在 languagePreference 字段，则进行映射
            MigrateFromLegacyFormat(settings, json);

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
            File.WriteAllText(_settingsFilePath, json);
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

    /// <summary>
    /// <para>将旧版的 languagePreference 枚举值迁移到 cultureName 字符串格式。</para>
    /// Migrates the legacy languagePreference enum value to the cultureName string format.
    /// </summary>
    /// <param name="settings">
    /// <para>要迁移的设置实例。</para>
    /// The settings instance to migrate.
    /// </param>
    /// <param name="rawJson">
    /// <para>原始 JSON 字符串。</para>
    /// The raw JSON string.
    /// </param>
    private static void MigrateFromLegacyFormat(AppSettings settings, string? rawJson)
    {
        if (rawJson is null || !string.IsNullOrEmpty(settings.CultureName))
            return;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("languagePreference", out JsonElement prop))
            {
                var legacyValue = prop.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? prop.GetInt32()
                    : prop.ValueKind == System.Text.Json.JsonValueKind.String
#pragma warning disable CS0618 // Type or member is obsolete
                        ? (int)Enum.Parse(typeof(LanguagePreference), prop.GetString() ?? "0")
#pragma warning restore CS0618 // Type or member is obsolete
                        : -1;

                settings.CultureName = legacyValue switch
                {
                    1 => "en-US",     // LanguagePreference.English
                    2 => "zh-CN",     // LanguagePreference.Chinese
                    _ => string.Empty // LanguagePreference.System or unknown
                };

                Debug.WriteLine($"[SettingsPersistenceService] Migrated legacy languagePreference '{legacyValue}' to cultureName '{settings.CultureName}'");
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 忽略解析错误，使用默认值
        }
    }
}
