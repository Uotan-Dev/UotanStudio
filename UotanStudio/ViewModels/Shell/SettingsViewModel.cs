using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.Services.Mcp;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// <para>设置视图模型，管理应用程序的通用设置、固件工具设置、烧录设置和节点编辑器设置。</para>
/// Settings view model, managing general settings, firmware tool settings, flash settings, and node editor settings.
/// </summary>
public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private AppSettings _appSettings;
    private CancellationTokenSource? _saveCts;
    private Timer? _toastTimer;
    private bool _disposed;

    private LanguageInfo _selectedLanguage = LanguageInfo.SystemLanguage;
    private ThemeMode _selectedThemeMode;
    private bool _autoSave;
    private bool _isToastVisible;
    private string _toastMessage = string.Empty;

    // MCP 设置字段
    // MCP settings fields
    private bool _mcpExposeEnabled;
    private int _mcpExposePort = 18321;
    private McpServerConfig? _selectedMcpServer;

    /// <summary>
    /// <para>获取可用语言列表，从语言服务动态获取。</para>
    /// Gets the list of available languages, dynamically retrieved from the language service.
    /// </summary>
    public ObservableCollection<LanguageInfo> Languages { get; }

    /// <summary>
    /// <para>获取可用主题模式列表。</para>
    /// Gets the list of available theme modes.
    /// </summary>
    public List<ThemeMode> Themes { get; } =
        [ThemeMode.System, ThemeMode.Light, ThemeMode.Dark];

    /// <summary>
    /// <para>获取 MCP 服务器连接列表（MCP 设置区块数据源；工具箱 MCP 节点按名称调用其工具）。</para>
    /// Gets the MCP server connection list (data source for the MCP settings section; the toolbox MCP
    /// nodes call their tools by name).
    /// </summary>
    public ObservableCollection<McpServerConfig> McpServers { get; } = [];

    /// <summary>
    /// <para>获取 MCP 传输类型列表（传输下拉框数据源）。</para>
    /// Gets the MCP transport kind list (data source for the transport combo box).
    /// </summary>
    public IReadOnlyList<McpTransportKind> McpTransportKinds { get; } = Enum.GetValues<McpTransportKind>();

    /// <summary>
    /// <para>获取或设置是否把本应用固件能力暴露为 MCP 服务器。</para>
    /// Gets or sets whether to expose this application's firmware capabilities as an MCP server.
    /// </summary>
    public bool McpExposeEnabled
    {
        get => _mcpExposeEnabled;
        set => SetProperty(ref _mcpExposeEnabled, value);
    }

    /// <summary>
    /// <para>获取或设置 MCP 服务器暴露端口。</para>
    /// Gets or sets the MCP server exposure port.
    /// </summary>
    public int McpExposePort
    {
        get => _mcpExposePort;
        set => SetProperty(ref _mcpExposePort, value);
    }

    /// <summary>
    /// <para>获取或设置当前选中的 MCP 服务器配置（删除按钮目标）。</para>
    /// Gets or sets the currently selected MCP server config (the remove-button target).
    /// </summary>
    public McpServerConfig? SelectedMcpServer
    {
        get => _selectedMcpServer;
        set => SetProperty(ref _selectedMcpServer, value);
    }

    /// <summary>
    /// <para>获取本项目使用或参考的开源项目列表（开源致谢区块数据源，静态只读，AOT 友好）。</para>
    /// Gets the list of open-source projects used by or referenced in this application
    /// (data source for the open-source credits section; static and read-only, AOT friendly).
    /// </summary>
    public static IReadOnlyList<OpenSourceProject> OpenSourceProjects { get; } =
    [
        new OpenSourceProject
        {
            Name = "Avalonia",
            Description = "跨平台 UI 框架，本项目桌面端 UI 基于 Avalonia 构建",
            RepositoryUrl = "https://github.com/AvaloniaUI/Avalonia",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md",
            AuthorName = "AvaloniaUI",
            AuthorGitHubUrl = "https://github.com/AvaloniaUI",
            AuthorAvatarUrl = "https://github.com/AvaloniaUI.png"
        },
        new OpenSourceProject
        {
            Name = "AvaloniaEdit",
            Description = "Avalonia 文本编辑器控件，用于代码/文本文件查看与编辑",
            RepositoryUrl = "https://github.com/AvaloniaUI/AvaloniaEdit",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/AvaloniaUI/AvaloniaEdit/blob/master/license.txt",
            AuthorName = "AvaloniaUI",
            AuthorGitHubUrl = "https://github.com/AvaloniaUI",
            AuthorAvatarUrl = "https://github.com/AvaloniaUI.png"
        },
        new OpenSourceProject
        {
            Name = "Semi.Avalonia",
            Description = "Semantic UI 风格的 Avalonia 主题库（本项目的主题与样式层）",
            RepositoryUrl = "https://github.com/irihitech/Semi.Avalonia",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/irihitech/Semi.Avalonia/blob/main/LICENSE",
            AuthorName = "IRIHI Technology",
            AuthorGitHubUrl = "https://github.com/irihitech",
            AuthorAvatarUrl = "https://github.com/irihitech.png"
        },
        new OpenSourceProject
        {
            Name = "Irihi.Ursa",
            Description = "Avalonia 交互控件库（对话框、Toast 等高级控件）",
            RepositoryUrl = "https://github.com/irihitech/Ursa.Avalonia",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/irihitech/Ursa.Avalonia/blob/main/LICENSE",
            AuthorName = "IRIHI Technology",
            AuthorGitHubUrl = "https://github.com/irihitech",
            AuthorAvatarUrl = "https://github.com/irihitech.png"
        },
        new OpenSourceProject
        {
            Name = "Dock",
            Description = "Avalonia 停靠布局系统（工作区面板 Dock 布局）",
            RepositoryUrl = "https://github.com/wieslawsoltes/Dock",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/wieslawsoltes/Dock/blob/master/LICENSE.TXT",
            AuthorName = "Wiesław Šoltés",
            AuthorGitHubUrl = "https://github.com/wieslawsoltes",
            AuthorAvatarUrl = "https://github.com/wieslawsoltes.png"
        },
        new OpenSourceProject
        {
            Name = "NodifyM.Avalonia",
            Description = "节点编辑器控件库（可视化编程画布）",
            RepositoryUrl = "https://github.com/Maklith/NodifyM.Avalonia",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/Maklith/NodifyM.Avalonia/blob/master/LICENSE",
            AuthorName = "MakesYT",
            AuthorGitHubUrl = "https://github.com/Maklith",
            AuthorAvatarUrl = "https://github.com/Maklith.png"
        },
        new OpenSourceProject
        {
            Name = "Material.Icons",
            Description = "Material 设计图标集合（工具栏/菜单图标）",
            RepositoryUrl = "https://github.com/SkankyDev/Material.Icons",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/SkankyDev/Material.Icons/blob/main/LICENSE",
            AuthorName = "SkankyDev",
            AuthorGitHubUrl = "https://github.com/SkankyDev",
            AuthorAvatarUrl = "https://github.com/SkankyDev.png"
        },
        new OpenSourceProject
        {
            Name = "CommunityToolkit.Mvvm",
            Description = "MVVM 工具包（ObservableObject / RelayCommand / WeakReferenceMessenger）",
            RepositoryUrl = "https://github.com/CommunityToolkit/dotnet",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/CommunityToolkit/dotnet/blob/main/LICENSE",
            AuthorName = ".NET Community Toolkit",
            AuthorGitHubUrl = "https://github.com/CommunityToolkit",
            AuthorAvatarUrl = "https://github.com/CommunityToolkit.png"
        },
        new OpenSourceProject
        {
            Name = "AdvancedSharpAdbClient",
            Description = "ADB 协议客户端库（与 adb server 交互、设备状态监测）",
            RepositoryUrl = "https://github.com/SharpAdb/AdvancedSharpAdbClient",
            LicenseName = "Apache-2.0",
            LicenseUrl = "https://github.com/SharpAdb/AdvancedSharpAdbClient/blob/main/LICENSE",
            AuthorName = "SharpAdb",
            AuthorGitHubUrl = "https://github.com/SharpAdb",
            AuthorAvatarUrl = "https://github.com/SharpAdb.png"
        },
        new OpenSourceProject
        {
            Name = "LibUsbDotNet",
            Description = ".NET USB 访问库（WinUSB/LibUsb 后端，USB 设备通信）",
            RepositoryUrl = "https://github.com/LibUsbDotNet/LibUsbDotNet",
            LicenseName = "LGPL-3.0",
            LicenseUrl = "https://github.com/LibUsbDotNet/LibUsbDotNet/blob/master/LICENSE",
            AuthorName = "LibUsbDotNet 团队",
            AuthorGitHubUrl = "https://github.com/LibUsbDotNet",
            AuthorAvatarUrl = "https://github.com/LibUsbDotNet.png"
        },
        new OpenSourceProject
        {
            Name = "SharpCompress",
            Description = "压缩/归档库（zip 等格式读写）",
            RepositoryUrl = "https://github.com/adamhathcock/sharpcompress",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/adamhathcock/sharpcompress/blob/master/LICENSE.txt",
            AuthorName = "Adam Hathcock",
            AuthorGitHubUrl = "https://github.com/adamhathcock",
            AuthorAvatarUrl = "https://github.com/adamhathcock.png"
        },
        new OpenSourceProject
        {
            Name = "SharpZipLib",
            Description = "压缩库（LZOP 容器解析 / zlib 解压辅助）",
            RepositoryUrl = "https://github.com/icsharpcode/SharpZipLib",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/icsharpcode/SharpZipLib/blob/master/LICENSE.txt",
            AuthorName = "ICSharpCode",
            AuthorGitHubUrl = "https://github.com/icsharpcode",
            AuthorAvatarUrl = "https://github.com/icsharpcode.png"
        },
        new OpenSourceProject
        {
            Name = "BouncyCastle.Cryptography",
            Description = "加密算法库（AVB RSASSA-PSS / PKCS#1 v1.5 签名校验）",
            RepositoryUrl = "https://github.com/bcgit/bc-csharp",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/bcgit/bc-csharp/blob/master/LICENSE.md",
            AuthorName = "The Legion of the Bouncy Castle",
            AuthorGitHubUrl = "https://github.com/bcgit",
            AuthorAvatarUrl = "https://github.com/bcgit.png"
        },
        new OpenSourceProject
        {
            Name = "BsDiff",
            Description = "二进制差分算法（OTA 差分生成/应用）",
            RepositoryUrl = "https://github.com/LogosBible/bsdiff.net",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/LogosBible/bsdiff.net/blob/master/LICENSE",
            AuthorName = "Logos Bible Software",
            AuthorGitHubUrl = "https://github.com/LogosBible",
            AuthorAvatarUrl = "https://github.com/LogosBible.png"
        },
        new OpenSourceProject
        {
            Name = "BrotliSharpLib",
            Description = "Brotli 压缩库（.new.dat.br 解压）",
            RepositoryUrl = "https://github.com/master131/BrotliSharpLib",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/master131/BrotliSharpLib/blob/master/LICENSE",
            AuthorName = "master131",
            AuthorGitHubUrl = "https://github.com/master131",
            AuthorAvatarUrl = "https://github.com/master131.png"
        },
        new OpenSourceProject
        {
            Name = "K4os.Compression.LZ4",
            Description = "LZ4 压缩库（LZ4 数据解压）",
            RepositoryUrl = "https://github.com/MiloszKrajewski/K4os.Compression.LZ4",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/master/LICENSE",
            AuthorName = "Milosz Krajewski",
            AuthorGitHubUrl = "https://github.com/MiloszKrajewski",
            AuthorAvatarUrl = "https://github.com/MiloszKrajewski.png"
        },
        new OpenSourceProject
        {
            Name = "ZstdSharp.Port",
            Description = "Zstandard 压缩库（zstd 数据解压）",
            RepositoryUrl = "https://github.com/oleg-st/ZstdSharp",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/oleg-st/ZstdSharp/blob/master/LICENSE",
            AuthorName = "Oleg Stepanov",
            AuthorGitHubUrl = "https://github.com/oleg-st",
            AuthorAvatarUrl = "https://github.com/oleg-st.png"
        },
        new OpenSourceProject
        {
            Name = "LZMA-SDK",
            Description = "LZMA 压缩算法 SDK（LZMA/LZMA2 解压）",
            RepositoryUrl = "https://www.7-zip.org/sdk.html",
            LicenseName = "公有领域",
            LicenseUrl = "https://www.7-zip.org/sdk.html",
            AuthorName = "Igor Pavlov",
            AuthorGitHubUrl = "https://github.com/ip7z",
            AuthorAvatarUrl = "https://github.com/ip7z.png"
        },
        new OpenSourceProject
        {
            Name = "Google.Protobuf",
            Description = "Protocol Buffers 序列化库（OTA payload 等 protobuf 解析）",
            RepositoryUrl = "https://github.com/protocolbuffers/protobuf",
            LicenseName = "BSD-3-Clause",
            LicenseUrl = "https://github.com/protocolbuffers/protobuf/blob/main/LICENSE",
            AuthorName = "Google",
            AuthorGitHubUrl = "https://github.com/protocolbuffers",
            AuthorAvatarUrl = "https://github.com/protocolbuffers.png"
        },
        new OpenSourceProject
        {
            Name = "Makaretu.Dns.Multicast",
            Description = "mDNS/DNS-SD 库（本地网络设备发现）",
            RepositoryUrl = "https://github.com/sanjaymak/Dns",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/sanjaymak/Dns/blob/master/LICENSE",
            AuthorName = "Sanjay Mak",
            AuthorGitHubUrl = "https://github.com/sanjaymak",
            AuthorAvatarUrl = "https://github.com/sanjaymak.png"
        },
        new OpenSourceProject
        {
            Name = "xunit",
            Description = "单元测试框架（xunit.v3，项目测试套件）",
            RepositoryUrl = "https://github.com/xunit/xunit",
            LicenseName = "Apache-2.0",
            LicenseUrl = "https://github.com/xunit/xunit/blob/main/LICENSE",
            AuthorName = ".NET Foundation",
            AuthorGitHubUrl = "https://github.com/xunit",
            AuthorAvatarUrl = "https://github.com/xunit.png"
        },
        new OpenSourceProject
        {
            Name = ".NET Runtime",
            Description = ".NET 基础类库与运行时（System.IO.Ports / System.IO.Hashing 等）",
            RepositoryUrl = "https://github.com/dotnet/runtime",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT",
            AuthorName = ".NET Foundation",
            AuthorGitHubUrl = "https://github.com/dotnet",
            AuthorAvatarUrl = "https://github.com/dotnet.png"
        }
    ];

    /// <summary>
    /// <para>获取语言预览文本，根据当前语言动态更新。</para>
    /// Gets the language preview text, dynamically updated based on the current language.
    /// </summary>
    public string LanguagePreviewText => LanguageService.Instance.GetString("Settings_LanguagePreviewText");

    /// <summary>
    /// <para>获取或设置选中的语言信息。</para>
    /// Gets or sets the selected language information.
    /// </summary>
    public LanguageInfo SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value))
            {
                ApplyLanguageInfo(value);
                OnPropertyChanged(nameof(LanguagePreviewText));
                DebouncedSaveSettings();
            }
        }
    }

    /// <summary>
    /// <para>获取或设置选中的主题模式。</para>
    /// Gets or sets the selected theme mode.
    /// </summary>
    public ThemeMode SelectedThemeMode
    {
        get => _selectedThemeMode;
        set
        {
            if (SetProperty(ref _selectedThemeMode, value))
            {
                // 通知三个 RadioButton 绑定属性同步更新，避免快速来回点击时 UI 选中态与 VM 脱节导致主题切换丢失
                OnPropertyChanged(nameof(IsLightTheme));
                OnPropertyChanged(nameof(IsDarkTheme));
                OnPropertyChanged(nameof(IsSystemTheme));
                WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(value));
                DebouncedSaveSettings();
            }
        }
    }

    /// <summary>
    /// <para>获取或设置是否为浅色主题（用于 RadioButton 绑定）。</para>
    /// Gets or sets whether the light theme is selected (for RadioButton binding).
    /// </summary>
    public bool IsLightTheme
    {
        get => _selectedThemeMode == ThemeMode.Light;
        set
        {
            if (value && _selectedThemeMode != ThemeMode.Light)
                SelectedThemeMode = ThemeMode.Light;
        }
    }

    /// <summary>
    /// <para>获取或设置是否为深色主题（用于 RadioButton 绑定）。</para>
    /// Gets or sets whether the dark theme is selected (for RadioButton binding).
    /// </summary>
    public bool IsDarkTheme
    {
        get => _selectedThemeMode == ThemeMode.Dark;
        set
        {
            if (value && _selectedThemeMode != ThemeMode.Dark)
                SelectedThemeMode = ThemeMode.Dark;
        }
    }

    /// <summary>
    /// <para>获取或设置是否跟随系统主题（用于 RadioButton 绑定）。</para>
    /// Gets or sets whether to follow the system theme (for RadioButton binding).
    /// </summary>
    public bool IsSystemTheme
    {
        get => _selectedThemeMode == ThemeMode.System;
        set
        {
            if (value && _selectedThemeMode != ThemeMode.System)
                SelectedThemeMode = ThemeMode.System;
        }
    }

    /// <summary>
    /// <para>获取或设置是否启用自动保存。</para>
    /// Gets or sets whether auto-save is enabled.
    /// </summary>
    public bool AutoSave
    {
        get => _autoSave;
        set { if (SetProperty(ref _autoSave, value)) DebouncedSaveSettings(); }
    }

    /// <summary>
    /// <para>获取或设置 Toast 通知是否可见。</para>
    /// Gets or sets whether the toast notification is visible.
    /// </summary>
    public bool IsToastVisible
    {
        get => _isToastVisible;
        set => SetProperty(ref _isToastVisible, value);
    }

    /// <summary>
    /// <para>获取或设置 Toast 通知消息内容。</para>
    /// Gets or sets the toast notification message content.
    /// </summary>
    public string ToastMessage
    {
        get => _toastMessage;
        set => SetProperty(ref _toastMessage, value);
    }

    /// <summary>
    /// <para>初始化设置视图模型，从持久化服务加载设置并初始化所有属性。</para>
    /// Initializes the settings view model, loading settings from the persistence service and initializing all properties.
    /// </summary>
    public SettingsViewModel()
    {
        _appSettings = SettingsPersistenceService.Instance.LoadSettings();

        // 初始化语言列表（从 LanguageService 动态获取）
        Languages = new ObservableCollection<LanguageInfo>(LanguageService.Instance.AvailableLanguages);

        InitializeFromAppSettings(_appSettings);

        LanguageService.Instance.LanguageChanged += OnLanguageChanged;
        LanguagePackRegistry.Instance.LanguagePackChanged += OnLanguagePackChanged;
    }

    /// <summary>
    /// <para>从 <see cref="AppSettings"/> 实例初始化所有属性值。</para>
    /// Initializes all property values from an <see cref="AppSettings"/> instance.
    /// </summary>
    /// <param name="settings">
    /// <para>要加载的应用程序设置实例。</para>
    /// The application settings instance to load from.
    /// </param>
    private void InitializeFromAppSettings(AppSettings settings)
    {
        // 根据 CultureName 查找或创建 LanguageInfo
        _selectedLanguage = string.IsNullOrEmpty(settings.CultureName)
            ? LanguageInfo.SystemLanguage
            : LanguageService.Instance.AvailableLanguages.FirstOrDefault(l => l.CultureName == settings.CultureName)
              ?? new LanguageInfo(settings.CultureName, settings.CultureName, settings.CultureName);
        _selectedThemeMode = settings.ThemeMode;
        _autoSave = settings.AutoSave;

        // Apply language based on loaded preference
        ApplyLanguageInfo(_selectedLanguage);

        // 初始化 MCP 设置
        McpServers.Clear();
        foreach (var server in settings.McpServers)
        {
            McpServers.Add(new McpServerConfig
            {
                Name = server.Name,
                Transport = server.Transport,
                Command = server.Command,
                Arguments = server.Arguments,
                Url = server.Url,
                Enabled = server.Enabled
            });
        }

        _mcpExposeEnabled = settings.McpExposeEnabled;
        _mcpExposePort = settings.McpExposePort > 0 ? settings.McpExposePort : 18321;
    }

    /// <summary>
    /// <para>根据语言信息应用语言设置。</para>
    /// Applies the language setting based on the language information.
    /// </summary>
    /// <param name="languageInfo">
    /// <para>语言信息。</para>
    /// The language information.
    /// </param>
    private void ApplyLanguageInfo(LanguageInfo languageInfo)
    {
        if (languageInfo.IsSystemLanguage)
        {
            LanguageService.Instance.InitializeFromSystemLanguage();
        }
        else if (!string.IsNullOrEmpty(languageInfo.CultureName))
        {
            LanguageService.Instance.ApplyLanguage(languageInfo.CultureName);
        }
    }

    /// <summary>
    /// <para>使用防抖策略延迟保存设置，在 500 毫秒无变更后执行持久化。</para>
    /// Saves settings with a debounced strategy, persisting after 500ms of no changes.
    /// </summary>
    private void DebouncedSaveSettings()
    {
        var oldCts = _saveCts;
        _saveCts = new CancellationTokenSource();
        oldCts?.Cancel();
        oldCts?.Dispose();

        _ = DebouncedSaveSettingsAsync(_saveCts.Token);
    }

    /// <summary>
    /// <para>防抖保存的异步实现，等待指定时间后执行保存。</para>
    /// Async implementation of debounced save, waiting for the specified duration before saving.
    /// </summary>
    /// <param name="cancellationToken">
    /// <para>用于取消本次保存操作的取消令牌。</para>
    /// Cancellation token to cancel this save operation.
    /// </param>
    private async Task DebouncedSaveSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        SaveSettings();
    }

    /// <summary>
    /// <para>将当前属性值同步到 <see cref="AppSettings"/> 并通过持久化服务保存，同时显示 Toast 通知。</para>
    /// Syncs current property values to <see cref="AppSettings"/> and saves via the persistence service, then shows a toast notification.
    /// </summary>
    private void SaveSettings()
    {
        _appSettings.CultureName = _selectedLanguage.IsSystemLanguage ? string.Empty : _selectedLanguage.CultureName;
        _appSettings.ThemeMode = _selectedThemeMode;
        _appSettings.AutoSave = _autoSave;

        // 合并磁盘上最新的最近打开项目列表，避免防抖保存覆盖「最近打开」记录。
        // Merge the latest recent-projects list from disk so the debounced save never wipes the MRU record.
        var latest = SettingsPersistenceService.Instance.LoadSettings();
        _appSettings.RecentProjects = latest.RecentProjects;

        // MCP 设置
        _appSettings.McpServers = [.. McpServers];
        _appSettings.McpExposeEnabled = _mcpExposeEnabled;
        _appSettings.McpExposePort = _mcpExposePort;

        SettingsPersistenceService.Instance.SaveSettings(_appSettings);

        ShowToast(LanguageService.Instance.GetString("Settings_SettingsSaved"));
    }

    /// <summary>
    /// <para>显示 Toast 通知，2 秒后在 UI 线程上自动隐藏。</para>
    /// Shows a toast notification that automatically hides after 2 seconds on the UI thread.
    /// </summary>
    /// <param name="message">
    /// <para>要显示的通知消息。</para>
    /// The notification message to display.
    /// </param>
    private void ShowToast(string message)
    {
        // 取消上一次的 Toast 计时，避免遗留回调造成抖动
        _toastTimer?.Dispose();

        // Toast 是一个非模态覆盖层，设置可见性后立即返回，不阻塞调用方
        ToastMessage = message;
        IsToastVisible = true;

        // Toast 计时器回调在 UI 线程上执行隐藏操作，避免跨线程访问绑定属性。
        // 先把当前计时器存入字段，再在回调里比对引用，防止并发 ShowToast 时
        // 旧的 Dispatcher.Post 回调误将新计时器置空。
        Timer? createdTimer = null;
        createdTimer = new Timer(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // 仅当仍然指向同一计时器时才隐藏，防止竞态条件下覆盖较新的 Toast
                if (ReferenceEquals(_toastTimer, createdTimer))
                {
                    IsToastVisible = false;
                    _toastTimer?.Dispose();
                    _toastTimer = null;
                }
            });
        }, null, 2000, Timeout.Infinite);
        _toastTimer = createdTimer;
    }

    /// <summary>
    /// <para>监听语言服务变更事件，更新语言相关属性。</para>
    /// Listens to language service change events and updates language-related properties.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LanguagePreviewText));
    }

    /// <summary>
    /// <para>监听语言包注册中心变更事件，动态更新可用语言列表。</para>
    /// Listens to language pack registry change events and dynamically updates the available languages list.
    /// </summary>
    private void OnLanguagePackChanged(object? sender, LanguagePackChangedEventArgs e)
    {
        // 同步更新语言列表
        Languages.Clear();
        foreach (var lang in LanguageService.Instance.AvailableLanguages)
        {
            Languages.Add(lang);
        }
    }

    /// <summary>
    /// <para>添加一条默认的 MCP 服务器配置（HTTP 传输）。</para>
    /// Adds a default MCP server config (HTTP transport).
    /// </summary>
    [RelayCommand]
    private void AddMcpServer()
    {
        var index = McpServers.Count + 1;
        McpServers.Add(new McpServerConfig
        {
            Name = $"MCP Server {index}",
            Transport = McpTransportKind.Http,
            Url = "http://localhost:8080/mcp",
            Enabled = true
        });
    }

    /// <summary>
    /// <para>删除选中的 MCP 服务器配置。</para>
    /// Removes the selected MCP server config.
    /// </summary>
    [RelayCommand]
    private void RemoveMcpServer(McpServerConfig? server)
    {
        if (server is not null)
        {
            McpServers.Remove(server);
        }
    }

    /// <summary>
    /// <para>保存 MCP 设置：同步到 <see cref="AppSettings"/> 并持久化，重置客户端连接缓存，
    /// 按「暴露为 MCP 服务器」开关与端口启动/停止服务器宿主。</para>
    /// Saves the MCP settings: syncs into <see cref="AppSettings"/> and persists, resets the client
    /// connection cache, and starts/stops the server host per the expose toggle and port.
    /// </summary>
    [RelayCommand]
    private void SaveMcpSettings()
    {
        SaveSettings();
        McpClientService.Instance.Reset();
        if (_mcpExposeEnabled && _mcpExposePort > 0)
        {
            McpHostService.Instance.Start(_mcpExposePort);
        }
        else
        {
            McpHostService.Instance.Stop();
        }
    }

    /// <summary>
    /// <para>重置所有设置为默认值，重新加载并通知主题变更。</para>
    /// Resets all settings to default values, reloads them, and notifies of theme changes.
    /// </summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        _appSettings = SettingsPersistenceService.Instance.ResetToDefaults();
        InitializeFromAppSettings(_appSettings);

        // 同步语言列表
        Languages.Clear();
        foreach (var lang in LanguageService.Instance.AvailableLanguages)
        {
            Languages.Add(lang);
        }

        // Notify all properties changed
        OnPropertyChanged(string.Empty);
        WeakReferenceMessenger.Default.Send(new ThemeChangedMessage(_selectedThemeMode));
    }

    /// <summary>
    /// <para>释放所有资源，包括取消令牌源、定时器和事件订阅。</para>
    /// Releases all resources, including cancellation token sources, timers, and event subscriptions.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// <para>释放资源的核心方法。</para>
    /// Core method for releasing resources.
    /// </summary>
    /// <param name="disposing">
    /// <para>是否释放托管资源。</para>
    /// Whether to release managed resources.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _toastTimer?.Dispose();
            LanguageService.Instance.LanguageChanged -= OnLanguageChanged;
            LanguagePackRegistry.Instance.LanguagePackChanged -= OnLanguagePackChanged;
        }

        _disposed = true;
    }
}