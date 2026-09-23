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
public class AppSettings : IJsonOnDeserialized
{
    /// <summary>Discards the removed executable override while preserving unrelated extension settings.</summary>
    void IJsonOnDeserialized.OnDeserialized()
    {
        AdditionalSettings?.Remove("hdcPath");
        AdditionalSettings?.Remove("HdcPath");
        DeviceCommunication ??= new();
        FavoriteNodeTypes ??= [];
        RecentNodeTypes ??= [];
        ExpandedNodeCategories ??= [];
    }

    /// <summary>USB backend policy, applied at the next application startup.</summary>
    [JsonPropertyName("deviceCommunication")]
    public global::UotanStudio.Comm.DeviceCommunicationConfiguration DeviceCommunication { get; set; } = new();

    /// <summary>Maximum concurrent firmware workers. Missing or invalid persisted values use the detected physical core count.</summary>
    [JsonPropertyName("firmwareParallelism")]
    public int FirmwareParallelism
    {
        get;
        set => field = value is >= 1 and <= global::UotanStudio.Firmware.SharedKernel.FirmwareParallelism.MaximumWorkers
            ? value : global::UotanStudio.Firmware.SharedKernel.FirmwareParallelism.DefaultWorkers;
    } = global::UotanStudio.Firmware.SharedKernel.FirmwareParallelism.DefaultWorkers;

    /// <summary>Copies settings with independently editable collections.<para>复制设置，隔离可编辑集合。</para></summary>
    internal AppSettings Copy()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.RecentProjects = [.. RecentProjects];
        copy.FavoriteNodeTypes = [.. FavoriteNodeTypes];
        copy.RecentNodeTypes = [.. RecentNodeTypes];
        copy.ExpandedNodeCategories = [.. ExpandedNodeCategories];
        copy.McpServers = [.. McpServers];
        copy.AdditionalSettings = AdditionalSettings is null ? null : new(AdditionalSettings);
        return copy;
    }

    /// <summary>
    /// <para>获取或设置新建 EROFS 镜像是否使用 legacy（pre-5.13）布局，供旧内核挂载。</para>
    /// Gets or sets whether newly built EROFS images use the legacy (pre-5.13) layout for older kernels.
    /// </summary>
    [JsonPropertyName("erofsLegacyCompatibility")]
    public bool ErofsLegacyCompatibility { get; set; } =
        global::UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder.ErofsBuildDefaults.DefaultLegacyCompatibility;

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

    /// <summary>Stable node keys pinned in the toolbox, independent of project workflow data.</summary>
    public List<string> FavoriteNodeTypes { get; set; } = [];

    /// <summary>Recently inserted node keys, newest first.</summary>
    public List<string> RecentNodeTypes { get; set; } = [];

    /// <summary>Expanded toolbox category and family identities.</summary>
    public List<string> ExpandedNodeCategories { get; set; } = [];

    /// <summary>
    /// <para>获取或设置设为"当前签名"的 ADB 密钥库条目 Id；空字符串表示使用启动自动生成的运行时身份。</para>
    /// Gets or sets the Id of the ADB key library entry set as the "current signature"; empty string means the runtime identity auto-generated at startup.
    /// </summary>
    public string AdbDefaultKeyId { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置设为"当前签名"的 HDC 密钥库条目 Id；空字符串表示使用启动自动生成的运行时身份。</para>
    /// Gets or sets the Id of the HDC key library entry set as the "current signature"; empty string means the runtime identity auto-generated at startup.
    /// </summary>
    public string HdcDefaultKeyId { get; set; } = string.Empty;

    /// <summary>Preserves settings from temporarily unavailable features without activating them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    /// <summary>
    /// <para>获取或设置是否启用调试模式（开启后日志显示 Trace/Debug 级别详细诊断信息）。</para>
    /// Gets or sets whether debug mode is enabled (when on, logs show Trace/Debug level diagnostic details).
    /// </summary>
    [JsonPropertyName("debugMode")]
    public bool DebugMode { get; set; }

    /// <summary>
    /// <para>获取或设置 scrcpy 投屏视频码率（bps）。</para>
    /// Gets or sets the scrcpy mirroring video bit rate (bps).
    /// </summary>
    [JsonPropertyName("screenMirrorBitRate")]
    public int ScreenMirrorBitRate { get; set; } = 8_000_000;

    /// <summary>
    /// <para>获取或设置 scrcpy 投屏最大帧率（0 表示不限制）。</para>
    /// Gets or sets the scrcpy mirroring max frame rate (0 = no limit).
    /// </summary>
    [JsonPropertyName("screenMirrorMaxFps")]
    public int ScreenMirrorMaxFps { get; set; } = 60;

    /// <summary>
    /// <para>获取或设置 scrcpy 投屏最大尺寸（0 表示不限制）。</para>
    /// Gets or sets the scrcpy mirroring max size (0 = no limit).
    /// </summary>
    [JsonPropertyName("screenMirrorMaxSize")]
    public int ScreenMirrorMaxSize { get; set; }

    /// <summary>
    /// <para>获取或设置 scrcpy 投屏视频编码格式（"h264" 或 "h265"）。</para>
    /// Gets or sets the scrcpy mirroring video codec ("h264" or "h265").
    /// </summary>
    [JsonPropertyName("screenMirrorVideoCodec")]
    public string ScreenMirrorVideoCodec { get; set; } = "h264";

    /// <summary>
    /// <para>获取或设置是否启用 scrcpy 投屏音频转发。</para>
    /// Gets or sets whether scrcpy mirroring forwards audio.
    /// </summary>
    [JsonPropertyName("screenMirrorAudioEnabled")]
    public bool ScreenMirrorAudioEnabled { get; set; }
    /// <summary>Video decoder backend. 视频解码后端。</summary>
    public UotanStudio.Media.Decoding.VideoDecoderBackend ScreenMirrorDecoderBackend { get; set; }
    /// <summary>Source color matrix override. 源色彩矩阵覆盖。</summary>
    public UotanStudio.Media.Decoding.VideoColorMatrix ScreenMirrorColorMatrix { get; set; }
    /// <summary>Source color range override. 源色彩范围覆盖。</summary>
    public UotanStudio.Media.Decoding.VideoColorRange ScreenMirrorColorRange { get; set; }
    /// <summary>Audio codec. 音频编码格式。</summary>
    public string ScreenMirrorAudioCodec { get; set; } = "aac";
    /// <summary>Audio decoder backend. 音频解码后端。</summary>
    public UotanStudio.Media.Audio.AudioDecoderBackend ScreenMirrorAudioBackend { get; set; }
    /// <summary>Playback volume. 播放音量。</summary>
    public float ScreenMirrorVolume { get; set; } = 1;
    /// <summary>Enable remote input. 是否允许远程输入。</summary>
    public bool ScreenMirrorControl { get; set; } = true;

    /// <summary>
    /// <para>获取或设置 MCP 服务器连接列表（设置页维护，MCP 客户端按名称调用其工具）。</para>
    /// Gets or sets the MCP server connection list (maintained by the settings page; MCP clients
    /// call their tools by name).
    /// </summary>
    [JsonPropertyName("mcpServers")]
    public List<McpServerConfig> McpServers { get; set; } = [];

    /// <summary>
    /// <para>获取或设置是否把本应用的固件能力暴露为 MCP 服务器（端口见 <see cref="McpExposePort"/>）。</para>
    /// Gets or sets whether to expose this application's firmware capabilities as an MCP server
    /// (port in <see cref="McpExposePort"/>).
    /// </summary>
    [JsonPropertyName("mcpExposeEnabled")]
    public bool McpExposeEnabled { get; set; }

    /// <summary>Default port of the embedded MCP server. <para>内嵌 MCP 服务器的默认端口。</para></summary>
    public const int DefaultMcpExposePort = 18321;

    /// <summary>
    /// <para>获取或设置 MCP 服务器暴露端口。</para>
    /// Gets or sets the MCP server exposure port.
    /// </summary>
    [JsonPropertyName("mcpExposePort")]
    public int McpExposePort { get; set; } = DefaultMcpExposePort;
}

/// <summary>
/// <para><see cref="AppSettings"/> 的 JSON 序列化上下文，为 AOT 兼容提供源生成支持。</para>
/// JSON serialization context for <see cref="AppSettings"/>, providing source generation support for AOT compatibility.
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Workspace.WorkspaceSessionSnapshot))]
[JsonSerializable(typeof(ThemeMode))]
[JsonSerializable(typeof(McpServerConfig))]
[JsonSerializable(typeof(McpTransportKind))]
[JsonSerializable(typeof(UotanStudio.Services.Logging.LogFileRecord))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
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
        => TrySaveSettings(settings, out _);

    internal bool TrySaveSettings(AppSettings settings, out string? error)
    {
        ArgumentNullException.ThrowIfNull(settings);
        error = null;

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
            return true;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            WeakReferenceMessenger.Default.Send(new SettingsSaveFailedMessage(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            WeakReferenceMessenger.Default.Send(new SettingsSaveFailedMessage(ex.Message));
        }
        return false;
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
    /// <para>更新设为"当前签名"的默认密钥 Id：以磁盘上最新设置为基础合并写入，避免覆盖其他设置项。</para>
    /// Updates the default key Id set as the "current signature": merges on top of the latest settings on
    /// disk so other settings are never overwritten.
    /// </summary>
    /// <param name="kind">
    /// <para>密钥类型，"Adb" 写入 <see cref="AdbDefaultKeyId"/>，"Hdc" 写入 <see cref="HdcDefaultKeyId"/>。</para>
    /// The key kind: "Adb" writes <see cref="AdbDefaultKeyId"/>, "Hdc" writes <see cref="HdcDefaultKeyId"/>.
    /// </param>
    /// <param name="keyId">
    /// <para>默认密钥 Id；null 视为空字符串（恢复使用运行时身份）。</para>
    /// The default key Id; null is treated as an empty string (fall back to the runtime identity).
    /// </param>
    /// <returns>
    /// <para>是否保存成功。</para>
    /// Whether the settings were saved successfully.
    /// </returns>
    public bool UpdateDefaultKeyId(string kind, string? keyId)
    {
        var settings = LoadSettings();
        var value = keyId ?? string.Empty;
        switch (kind)
        {
            case "Adb":
                settings.AdbDefaultKeyId = value;
                break;
            case "Hdc":
                settings.HdcDefaultKeyId = value;
                break;
            default:
                return false;
        }
        return TrySaveSettings(settings, out _);
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