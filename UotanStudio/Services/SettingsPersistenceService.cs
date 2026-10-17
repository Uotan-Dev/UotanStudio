using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services.Mcp;

namespace UotanStudio.Services;

/// <summary>
/// Application settings data model, containing all settings that need to be persisted.
/// <para>应用程序设置数据模型，包含所有需要持久化的设置项。</para>
/// </summary>
public class AppSettings : IJsonOnDeserialized
{
    /// <summary>Discards the HDC path keys and the retired EROFS legacy layout switch kept in extension data, and initializes missing collections.<para>移除扩展数据里残留的 HDC 路径键与已退役的 EROFS legacy 布局开关，并初始化缺失的集合。</para></summary>
    void IJsonOnDeserialized.OnDeserialized()
    {
        AdditionalSettings?.Remove("hdcPath");
        AdditionalSettings?.Remove("HdcPath");
        AdditionalSettings?.Remove("erofsLegacyCompatibility");
        DeviceCommunication ??= new();
        FavoriteNodeTypes ??= [];
        RecentNodeTypes ??= [];
        ExpandedNodeCategories ??= [];
        PairedNetworkTargets ??= [];
    }

    /// <summary>
    /// USB backend policy, applied at the next application startup.
    /// <para>USB 后端策略，在下次应用启动时生效。</para>
    /// </summary>
    /// <value>The persisted device-communication policy. <para>持久化的设备通信策略。</para></value>
    [JsonPropertyName("deviceCommunication")]
    public Comm.DeviceCommunicationConfiguration DeviceCommunication { get; set; } = new();

    /// <summary>
    /// Maximum concurrent firmware workers. Missing or invalid persisted values use the detected physical core count.
    /// <para>并发固件工作线程上限；缺失或无效的持久值使用检测到的物理核心数。</para>
    /// </summary>
    /// <value>The worker count ceiling. <para>工作线程数量上限。</para></value>
    [JsonPropertyName("firmwareParallelism")]
    public int FirmwareParallelism
    {
        get;
        set => field = value is >= 1 and <= UotanStudio.Firmware.Core.FirmwareParallelism.MaximumWorkers
            ? value : UotanStudio.Firmware.Core.FirmwareParallelism.DefaultWorkers;
    } = UotanStudio.Firmware.Core.FirmwareParallelism.DefaultWorkers;

    /// <summary>Copies settings with independently editable collections.<para>复制设置，隔离可编辑集合。</para></summary>
    /// <returns>The copied settings instance. <para>复制出的设置实例。</para></returns>
    internal AppSettings Copy()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.RecentProjects = [.. RecentProjects];
        copy.FavoriteNodeTypes = [.. FavoriteNodeTypes];
        copy.RecentNodeTypes = [.. RecentNodeTypes];
        copy.ExpandedNodeCategories = [.. ExpandedNodeCategories];
        copy.PairedNetworkTargets = [.. PairedNetworkTargets];
        copy.McpServers = [.. McpServers];
        copy.AdditionalSettings = AdditionalSettings is null ? null : new(AdditionalSettings);
        return copy;
    }

    /// <summary>
    /// Gets or sets whether a preserved EROFS rebuild searches the captured compression estimates for the smallest image.
    /// <para>获取或设置 EROFS 受保留重建是否在采集到的压缩估算之间搜索最小镜像。</para>
    /// </summary>
    /// <value>True when the smallest-image search runs. <para>为真时执行最小镜像搜索。</para></value>
    [JsonPropertyName("erofsRebuildOptimization")]
    public bool ErofsRebuildOptimization { get; set; } =
        UotanStudio.Firmware.FileSystem.Erofs.Builder.ErofsBuildDefaults.DefaultRebuildOptimization;

    /// <summary>
    /// Gets or sets the culture name of the current language, such as "en-US" or "zh-CN"; empty follows the system.
    /// <para>获取或设置当前语言的文化名称，例如 "en-US"、"zh-CN"；空字符串表示跟随系统。</para>
    /// </summary>
    /// <value>The culture name, or an empty string to follow the system. <para>文化名称，空字符串表示跟随系统。</para></value>
    [JsonPropertyName("cultureName")]
    public string CultureName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the theme mode.
    /// <para>获取或设置主题模式。</para>
    /// </summary>
    /// <value>The selected theme mode. <para>选定的主题模式。</para></value>
    [JsonPropertyName("themeMode")]
    [JsonConverter(typeof(JsonStringEnumConverter<ThemeMode>))]
    public ThemeMode ThemeMode { get; set; }

    /// <summary>
    /// Gets or sets whether auto-save is enabled.
    /// <para>获取或设置是否启用自动保存。</para>
    /// </summary>
    /// <value>True when edits are saved automatically. <para>为真时自动保存修改。</para></value>
    [JsonPropertyName("autoSave")]
    public bool AutoSave { get; set; }

    /// <summary>
    /// Gets or sets the default unpack tool path.
    /// <para>获取或设置默认解包工具路径。</para>
    /// </summary>
    /// <value>The configured tool path, or an empty string. <para>配置的工具路径，或空字符串。</para></value>
    [JsonPropertyName("unpackToolPath")]
    public string UnpackToolPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the temporary file directory.
    /// <para>获取或设置临时文件目录。</para>
    /// </summary>
    /// <value>The configured directory, or an empty string. <para>配置的目录，或空字符串。</para></value>
    [JsonPropertyName("tempDirectory")]
    public string TempDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether to auto-verify after unpacking.
    /// <para>获取或设置解包后是否自动校验。</para>
    /// </summary>
    /// <value>True when unpacking is followed by verification. <para>为真时解包后自动校验。</para></value>
    [JsonPropertyName("autoVerifyAfterUnpack")]
    public bool AutoVerifyAfterUnpack { get; set; } = true;

    /// <summary>
    /// Gets or sets the ADB path.
    /// <para>获取或设置 ADB 路径。</para>
    /// </summary>
    /// <value>The configured ADB path, or an empty string. <para>配置的 ADB 路径，或空字符串。</para></value>
    [JsonPropertyName("adbPath")]
    public string AdbPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Fastboot path.
    /// <para>获取或设置 Fastboot 路径。</para>
    /// </summary>
    /// <value>The configured Fastboot path, or an empty string. <para>配置的 Fastboot 路径，或空字符串。</para></value>
    [JsonPropertyName("fastbootPath")]
    public string FastbootPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device timeout in seconds.
    /// <para>获取或设置设备超时时间（秒）。</para>
    /// </summary>
    /// <value>The timeout length in seconds. <para>以秒计的超时长度。</para></value>
    [JsonPropertyName("deviceTimeoutSeconds")]
    public int DeviceTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets whether to auto-backup before flashing.
    /// <para>获取或设置烧录前是否自动备份。</para>
    /// </summary>
    /// <value>True when flashing is preceded by a backup. <para>为真时烧录前先备份。</para></value>
    [JsonPropertyName("autoBackupBeforeFlash")]
    public bool AutoBackupBeforeFlash { get; set; }

    /// <summary>
    /// Gets or sets whether expert flashing controls are enabled: raw-address reads and writes, secure-boot
    /// configuration writes, OEM key programming, BootROM bypass and diagnostic crash.
    /// <para>获取或设置是否启用专家级烧录控制：原始地址读写、安全配置写入、OEM 密钥烧录、BootROM 绕过与诊断崩溃。</para>
    /// </summary>
    /// <value>True when the expert control groups are enabled. <para>为真时启用专家级控制分组。</para></value>
    [JsonPropertyName("flashExpertMode")]
    public bool FlashExpertMode { get; set; }

    /// <summary>
    /// Gets or sets whether snap-to-grid is enabled.
    /// <para>获取或设置是否启用网格对齐。</para>
    /// </summary>
    /// <value>True when positions snap to the grid. <para>为真时坐标对齐到网格。</para></value>
    [JsonPropertyName("snapToGrid")]
    public bool SnapToGrid { get; set; } = true;

    /// <summary>
    /// Gets or sets the grid size.
    /// <para>获取或设置网格大小。</para>
    /// </summary>
    /// <value>The grid spacing. <para>网格间距。</para></value>
    [JsonPropertyName("gridSize")]
    public double GridSize { get; set; } = 20;

    /// <summary>
    /// Gets or sets the connection line style index.
    /// <para>获取或设置连接线样式索引。</para>
    /// </summary>
    /// <value>The index into the connection styles. <para>连接样式列表中的索引。</para></value>
    [JsonPropertyName("connectionStyleIndex")]
    public int ConnectionStyleIndex { get; set; }

    /// <summary>
    /// Gets or sets the list of recently opened solution paths (most recent first).
    /// <para>获取或设置最近打开的解决方案路径列表（最新在前）。</para>
    /// </summary>
    /// <value>The remembered solution paths. <para>记住的解决方案路径。</para></value>
    [JsonPropertyName("recentProjects")]
    public List<string> RecentProjects { get; set; } = [];

    /// <summary>
    /// Stable node keys pinned in the toolbox, independent of project workflow data.
    /// <para>固定在工具箱中的稳定节点键，与工程工作流数据无关。</para>
    /// </summary>
    /// <value>The pinned node keys. <para>被固定的节点键。</para></value>
    public List<string> FavoriteNodeTypes { get; set; } = [];

    /// <summary>
    /// Recently inserted node keys, newest first.
    /// <para>最近插入的节点键，最新在前。</para>
    /// </summary>
    /// <value>The remembered node keys. <para>记住的节点键。</para></value>
    public List<string> RecentNodeTypes { get; set; } = [];

    /// <summary>
    /// Expanded toolbox category and family identities.
    /// <para>已展开的工具箱分类与族群标识。</para>
    /// </summary>
    /// <value>The expanded category and family identities. <para>展开的分类与族群标识。</para></value>
    public List<string> ExpandedNodeCategories { get; set; } = [];

    /// <summary>
    /// Gets or sets the network debugging targets remembered after pairing, used to re-discover each
    /// daemon's secure connect port by IP address after a restart.
    /// <para>获取或设置配对成功后记住的网络调试目标，用于重启后按 IP 重新发现 adbd 的安全连接端口。</para>
    /// </summary>
    /// <value>The remembered pairing targets. <para>记住的配对目标。</para></value>
    [JsonPropertyName("pairedNetworkTargets")]
    public List<PairedNetworkTarget> PairedNetworkTargets { get; set; } = [];

    /// <summary>
    /// Gets or sets the Id of the ADB key library entry set as the "current signature"; empty string means the runtime identity auto-generated at startup.
    /// <para>获取或设置设为"当前签名"的 ADB 密钥库条目 Id；空字符串表示使用启动自动生成的运行时身份。</para>
    /// </summary>
    /// <value>The selected ADB key Id, or an empty string. <para>选定的 ADB 密钥 Id，或空字符串。</para></value>
    public string AdbDefaultKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Id of the HDC key library entry set as the "current signature"; empty string means the runtime identity auto-generated at startup.
    /// <para>获取或设置设为"当前签名"的 HDC 密钥库条目 Id；空字符串表示使用启动自动生成的运行时身份。</para>
    /// </summary>
    /// <value>The selected HDC key Id, or an empty string. <para>选定的 HDC 密钥 Id，或空字符串。</para></value>
    public string HdcDefaultKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Preserves settings from temporarily unavailable features without activating them.
    /// <para>保留暂时不可用功能的设置项，但不启用它们。</para>
    /// </summary>
    /// <value>The unmatched JSON extension data, or null. <para>未匹配的 JSON 扩展数据，或 null。</para></value>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    /// <summary>
    /// Gets or sets whether debug mode is enabled (when on, logs show Trace/Debug level diagnostic details).
    /// <para>获取或设置是否启用调试模式（开启后日志显示 Trace/Debug 级别详细诊断信息）。</para>
    /// </summary>
    /// <value>True when Trace/Debug details are logged. <para>为真时记录 Trace/Debug 级别细节。</para></value>
    [JsonPropertyName("debugMode")]
    public bool DebugMode { get; set; }

    /// <summary>
    /// Gets or sets the screen mirroring video bit rate in bits per second.
    /// <para>获取或设置投屏视频码率（比特每秒）。</para>
    /// </summary>
    /// <value>The configured video bit rate. <para>配置的视频码率。</para></value>
    [JsonPropertyName("screenMirrorBitRate")]
    public int ScreenMirrorBitRate { get; set; } = 8_000_000;

    /// <summary>
    /// Gets or sets the screen mirroring maximum frame rate; zero means no limit.
    /// <para>获取或设置投屏最大帧率，零表示不限制。</para>
    /// </summary>
    /// <value>The configured frame-rate ceiling. <para>配置的帧率上限。</para></value>
    [JsonPropertyName("screenMirrorMaxFps")]
    public int ScreenMirrorMaxFps { get; set; } = 60;

    /// <summary>
    /// Gets or sets the screen mirroring maximum size; zero means no limit.
    /// <para>获取或设置投屏最大尺寸，零表示不限制。</para>
    /// </summary>
    /// <value>The configured size ceiling. <para>配置的尺寸上限。</para></value>
    [JsonPropertyName("screenMirrorMaxSize")]
    public int ScreenMirrorMaxSize { get; set; }

    /// <summary>
    /// Gets or sets the screen mirroring video codec, "h264" or "h265".
    /// <para>获取或设置投屏视频编码格式，"h264" 或 "h265"。</para>
    /// </summary>
    /// <value>The configured codec identifier. <para>配置的视频编码标识。</para></value>
    [JsonPropertyName("screenMirrorVideoCodec")]
    public string ScreenMirrorVideoCodec { get; set; } = "h264";

    /// <summary>
    /// Gets or sets whether screen mirroring forwards audio.
    /// <para>获取或设置投屏是否转发音频。</para>
    /// </summary>
    /// <value>True when audio is forwarded. <para>为真时转发音频。</para></value>
    [JsonPropertyName("screenMirrorAudioEnabled")]
    public bool ScreenMirrorAudioEnabled { get; set; }
    /// <summary>
    /// Gets or sets the video decoder backend.
    /// <para>获取或设置视频解码后端。</para>
    /// </summary>
    /// <value>The selected video decoder backend. <para>选定的视频解码后端。</para></value>
    public Media.Decoding.VideoDecoderBackend ScreenMirrorDecoderBackend { get; set; }
    /// <summary>
    /// Gets or sets the source color matrix override.
    /// <para>获取或设置源色彩矩阵覆盖。</para>
    /// </summary>
    /// <value>The color matrix override. <para>色彩矩阵的覆盖值。</para></value>
    public Media.Decoding.VideoColorMatrix ScreenMirrorColorMatrix { get; set; }
    /// <summary>
    /// Gets or sets the source color range override.
    /// <para>获取或设置源色彩范围覆盖。</para>
    /// </summary>
    /// <value>The color range override. <para>色彩范围的覆盖值。</para></value>
    public Media.Decoding.VideoColorRange ScreenMirrorColorRange { get; set; }
    /// <summary>
    /// Gets or sets the audio codec name.
    /// <para>获取或设置音频编码格式。</para>
    /// </summary>
    /// <value>The configured audio codec identifier. <para>配置的音频编码标识。</para></value>
    public string ScreenMirrorAudioCodec { get; set; } = "aac";
    /// <summary>
    /// Gets or sets the audio decoder backend.
    /// <para>获取或设置音频解码后端。</para>
    /// </summary>
    /// <value>The selected audio decoder backend. <para>选定的音频解码后端。</para></value>
    public Media.Audio.AudioDecoderBackend ScreenMirrorAudioBackend { get; set; }
    /// <summary>
    /// Gets or sets the playback volume.
    /// <para>获取或设置播放音量。</para>
    /// </summary>
    /// <value>The configured playback volume. <para>配置的播放音量。</para></value>
    public float ScreenMirrorVolume { get; set; } = 1;
    /// <summary>
    /// Gets or sets whether remote input is allowed.
    /// <para>获取或设置是否允许远程输入。</para>
    /// </summary>
    /// <value>True when remote input is accepted. <para>为真时接受远程输入。</para></value>
    public bool ScreenMirrorControl { get; set; } = true;
    /// <summary>
    /// Gets or sets the screen mirror GPU zero-copy presentation preference: Auto probes capability, On forces the
    /// export path (falling back to NV12 readback when unsupported), Off forces the readback path.
    /// <para>获取或设置投屏 GPU 零拷贝呈现偏好：Auto 按能力探测，On 强制启用（不支持时回落 NV12 回读），Off 强制沿用回读路径。</para>
    /// </summary>
    /// <value>The preferred GPU zero-copy path. <para>偏好的 GPU 零拷贝路径。</para></value>
    public GpuExportPreference ScreenMirrorGpuExport { get; set; }

    /// <summary>
    /// Gets or sets the MCP server connection entries.
    /// <para>获取或设置 MCP 服务器连接条目。</para>
    /// </summary>
    /// <value>The configured server entries. <para>已配置的服务器条目。</para></value>
    [JsonPropertyName("mcpServers")]
    public List<McpServerConfig> McpServers { get; set; } = [];

    /// <summary>
    /// Gets or sets whether to expose this application's firmware capabilities as an MCP server
    /// (port in <see cref="McpExposePort"/>).
    /// <para>获取或设置是否把本应用的固件能力暴露为 MCP 服务器（端口见 <see cref="McpExposePort"/>）。</para>
    /// </summary>
    /// <value>True when the MCP endpoint is exposed. <para>为真时暴露 MCP 服务端点。</para></value>
    [JsonPropertyName("mcpExposeEnabled")]
    public bool McpExposeEnabled { get; set; }

    /// <summary>Default port of the embedded MCP server. <para>内嵌 MCP 服务器的默认端口。</para></summary>
    public const int DefaultMcpExposePort = 18321;

    /// <summary>
    /// Gets or sets the MCP server exposure port.
    /// <para>获取或设置 MCP 服务器暴露端口。</para>
    /// </summary>
    /// <value>The TCP port the MCP server listens on. <para>MCP 服务器监听的 TCP 端口。</para></value>
    [JsonPropertyName("mcpExposePort")]
    public int McpExposePort { get; set; } = DefaultMcpExposePort;

    /// <summary>
    /// Gets or sets the directory the embedded MCP server confines every client-supplied path to; empty
    /// leaves the server unrestricted.
    /// <para>获取或设置内嵌 MCP 服务器限制客户端路径的根目录；空字符串表示不限制。</para>
    /// </summary>
    /// <value>The confinement root directory, or an empty string. <para>限制根目录，或空字符串。</para></value>
    [JsonPropertyName("mcpExposeRootDirectory")]
    public string McpExposeRootDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the embedded MCP server registers the flashing, wiping and reboot tools;
    /// <see langword="false"/> keeps those destructive tools absent.
    /// <para>获取或设置内嵌 MCP 服务器是否注册刷写、擦除与重启类工具；<see langword="false"/> 时不暴露这些破坏性工具。</para>
    /// </summary>
    /// <value>True when destructive tools are registered. <para>为真时注册破坏性工具。</para></value>
    [JsonPropertyName("mcpExposeAllowFlash")]
    public bool McpExposeAllowFlash { get; set; }
}

/// <summary>
/// User preference for the GPU zero-copy presentation path of screen mirroring.
/// <para>投屏 GPU 零拷贝呈现路径的用户偏好。</para>
/// </summary>
public enum GpuExportPreference
{
    /// <summary>Enable when the compositor interop probe succeeds. <para>合成器互操作探测成功时启用。</para></summary>
    Auto,
    /// <summary>Request the export path; unsupported platforms keep the NV12 readback. <para>请求导出路径；不支持的平台保持 NV12 回读。</para></summary>
    On,
    /// <summary>Always decode to CPU-visible planes. <para>始终解码为 CPU 可读的像素平面。</para></summary>
    Off,
}

/// <summary>
/// A network debugging target remembered after a successful pairing for IP-based re-discovery of
/// its secure connect port.
/// <para>记住配对成功的网络调试目标，便于按 IP 地址重新发现其安全连接端口。</para>
/// </summary>
/// <param name="Address">The endpoint address as last observed.
/// <para>最后一次观测到的端点地址。</para></param>
/// <param name="RequiresTls">Whether the endpoint needs the TLS transport recorded at pairing time.
/// <para>该端点是否需要使用配对时记录的 TLS 传输。</para></param>
public sealed record PairedNetworkTarget(string Address, bool RequiresTls);

/// <summary>
/// JSON serialization context for <see cref="AppSettings"/>, providing source generation support for AOT compatibility.
/// <para><see cref="AppSettings"/> 的 JSON 序列化上下文，为 AOT 兼容提供源生成支持。</para>
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Workspace.WorkspaceSessionSnapshot))]
[JsonSerializable(typeof(ThemeMode))]
[JsonSerializable(typeof(McpServerConfig))]
[JsonSerializable(typeof(McpTransportKind))]
[JsonSerializable(typeof(Logging.LogFileRecord))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<ThemeMode>), typeof(JsonStringEnumConverter<McpTransportKind>)])]
internal partial class AppSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Settings persistence service, responsible for serialization, deserialization, and file storage of application settings.
/// <para>设置持久化服务，负责应用程序设置的序列化、反序列化及文件存储。</para>
/// </summary>
public class SettingsPersistenceService
{
    private static readonly Lazy<SettingsPersistenceService> LazyInstance = new(() => new SettingsPersistenceService());

    private readonly string _settingsDirectory;
    private readonly string _settingsFilePath;

    /// <summary>
    /// Gets the singleton instance of the settings persistence service.
    /// <para>获取设置持久化服务的单例实例。</para>
    /// </summary>
    /// <value>The lazily created singleton. <para>惰性创建的单例实例。</para></value>
    public static SettingsPersistenceService Instance => LazyInstance.Value;

    /// <summary>
    /// Gets the full path of the settings file.
    /// <para>获取设置文件的完整路径。</para>
    /// </summary>
    /// <value>The absolute file path used for persistence. <para>用于持久化的绝对文件路径。</para></value>
    public string SettingsFilePath => _settingsFilePath;

    private SettingsPersistenceService()
    {
        _settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UotanStudio");
        _settingsFilePath = Path.Combine(_settingsDirectory, "settings.json");
    }

    /// <summary>
    /// Loads application settings from file. Returns default settings if the file does not exist or is corrupted.
    /// <para>从文件加载应用程序设置。若文件不存在或已损坏，则返回默认设置。</para>
    /// </summary>
    /// <returns>
    /// The loaded <see cref="AppSettings"/> instance; returns a default instance on failure.
    /// <para>加载的 <see cref="AppSettings"/> 实例；加载失败时返回默认值实例。</para>
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
    /// Serializes and saves application settings to file.
    /// <para>将应用程序设置序列化并保存到文件。</para>
    /// </summary>
    /// <param name="settings">
    /// The application settings instance to save.
    /// <para>要保存的应用程序设置实例。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="settings"/> is null.
    /// <para>当 <paramref name="settings"/> 为 null 时抛出。</para>
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
            var tempPath = TempFileHelper.CreateAdjacentPath(_settingsFilePath);
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _settingsFilePath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); } catch { }
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
    /// Updates the recently opened solutions list: merges on top of the latest settings on disk so other
    /// settings are never overwritten.
    /// <para>更新最近打开的解决方案列表：以磁盘上最新设置为基础合并写入，避免覆盖其他设置项。</para>
    /// </summary>
    /// <param name="recentProjects">
    /// The new recent project paths (most recent first).
    /// <para>新的最近打开路径列表（最新在前）。</para>
    /// </param>
    public void UpdateRecentProjects(List<string> recentProjects)
    {
        var settings = LoadSettings();
        settings.RecentProjects = recentProjects ?? [];
        SaveSettings(settings);
    }

    /// <summary>
    /// Replaces the remembered pairing targets, merging on top of the latest settings on disk so
    /// other settings are never overwritten.
    /// <para>替换记住的配对目标：以磁盘上最新设置为基础合并写入，避免覆盖其他设置项。</para>
    /// </summary>
    /// <param name="targets">The current remembered targets; <c>null</c> clears them.
    /// <para>当前应记住的目标；为 <c>null</c> 时清空。</para></param>
    public void UpdatePairedNetworkTargets(List<PairedNetworkTarget> targets)
    {
        var settings = LoadSettings();
        settings.PairedNetworkTargets = targets ?? [];
        SaveSettings(settings);
    }

    /// <summary>
    /// Updates the default key Id set as the "current signature": merges on top of the latest settings on
    /// disk so other settings are never overwritten.
    /// <para>更新设为"当前签名"的默认密钥 Id：以磁盘上最新设置为基础合并写入，避免覆盖其他设置项。</para>
    /// </summary>
    /// <param name="kind">
    /// The key kind: "Adb" writes <see cref="AdbDefaultKeyId"/>, "Hdc" writes <see cref="HdcDefaultKeyId"/>.
    /// <para>密钥类型，"Adb" 写入 <see cref="AdbDefaultKeyId"/>，"Hdc" 写入 <see cref="HdcDefaultKeyId"/>。</para>
    /// </param>
    /// <param name="keyId">
    /// The default key Id; null is treated as an empty string (fall back to the runtime identity).
    /// <para>默认密钥 Id；null 视为空字符串（恢复使用运行时身份）。</para>
    /// </param>
    /// <returns>
    /// Whether the settings were saved successfully.
    /// <para>是否保存成功。</para>
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
    /// Resets all settings to default values and saves to file.
    /// <para>重置所有设置为默认值并保存到文件。</para>
    /// </summary>
    /// <returns>
    /// A new <see cref="AppSettings"/> instance with default values.
    /// <para>包含默认值的新 <see cref="AppSettings"/> 实例。</para>
    /// </returns>
    public AppSettings ResetToDefaults()
    {
        var defaults = new AppSettings();
        SaveSettings(defaults);
        return defaults;
    }
}