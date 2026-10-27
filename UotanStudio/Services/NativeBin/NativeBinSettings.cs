namespace UotanStudio.Services.NativeBin;

/// <summary>
/// Native-binary configuration contract read while provisioning.
/// <para>探测与下载期间读取的原生二进制配置契约。</para>
/// </summary>
public interface INativeBinSettings
{
    /// <summary>
    /// Directory under which per-RID native libraries are cached. Empty or
    /// <see cref="string.Empty"/> falls back to
    /// <c>%LOCALAPPDATA%/UotanStudio/Native</c>.
    /// <para>各 RID 原生库的缓存目录。空字符串表示使用
    /// <c>%LOCALAPPDATA%/UotanStudio/Native</c> 默认路径。</para>
    /// </summary>
    /// <value>Absolute or empty cache root directory.<para>绝对缓存根目录，允许为空。</para></value>
    string CacheDir { get; }

    /// <summary>
    /// Whether the provisioner may fall back to non-official mirrors when the
    /// official release URL is unreachable. <see langword="true"/> by default.
    /// <para>当官方 Release 地址不可达时是否回落到非官方镜像，默认为 <c>true</c>。</para>
    /// </summary>
    /// <value>True when mirror URLs may be tried.<para>允许尝试镜像 URL 时为 true。</para></value>
    bool UseMirrorList { get; }
}

/// <summary>
/// Production <see cref="INativeBinSettings"/> adapter that reads
/// <see cref="SettingsPersistenceService"/> on demand. Values are resolved per
/// <see cref="CacheDir"/> / <see cref="UseMirrorList"/> access, so settings
/// changes made during a running session are picked up on the next provision
/// attempt.
/// <para>生产环境的 <see cref="INativeBinSettings"/> 适配器，按需从
/// <see cref="SettingsPersistenceService"/> 读取。每次访问属性都重新解析，
/// 以便会话期间的设置变更在下一次探测/下载前生效。</para>
/// </summary>
public sealed class NativeBinSettingsStore : INativeBinSettings
{
    private static string DefaultCacheDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "UotanStudio", "Native");
    }

    private readonly SettingsPersistenceService _settings;

    /// <summary>
    /// Initializes a new adapter backed by <see cref="SettingsPersistenceService"/>.
    /// <para>创建基于 <see cref="SettingsPersistenceService"/> 的适配器。</para>
    /// </summary>
    /// <param name="settings">Settings persistence service to read from.
    /// <para>设置持久化服务。</para></param>
    public NativeBinSettingsStore(SettingsPersistenceService? settings = null)
        => _settings = settings ?? SettingsPersistenceService.Instance;

    /// <summary>
    /// Cache root read from persisted settings on every access.
    /// <para>每次访问都从持久化设置读取的缓存根目录。</para>
    /// </summary>
    /// <value>Absolute directory path, defaulting when unset.<para>绝对目录路径，未设置时使用默认位置。</para></value>
    public string CacheDir
    {
        get
        {
            var settings = _settings.LoadSettings();
            var value = settings.AdditionalSettings is not null
                && settings.AdditionalSettings.TryGetValue("nativeBin.cacheDir", out var element)
                && element.ValueKind == System.Text.Json.JsonValueKind.String
                    ? element.GetString()
                    : null;
            return string.IsNullOrEmpty(value) ? DefaultCacheDir() : Path.GetFullPath(value);
        }
    }

    /// <summary>
    /// Mirror permission read from persisted settings on every access.
    /// <para>每次访问都从持久化设置读取的镜像许可开关。</para>
    /// </summary>
    /// <value>True when mirrors may be tried, true when unset.<para>允许尝试镜像时为 true，未设置时同样为 true。</para></value>
    public bool UseMirrorList
    {
        get
        {
            var settings = _settings.LoadSettings();
            if (settings.AdditionalSettings is null ||
                !settings.AdditionalSettings.TryGetValue("nativeBin.useMirrorList", out var element))
            {
                return true;
            }
            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => true,
            };
        }
    }
}