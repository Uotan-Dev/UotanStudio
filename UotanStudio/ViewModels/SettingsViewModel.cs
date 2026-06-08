using System.Collections.Generic;
using System.Globalization;
using UotanStudio.Localization;

namespace UotanStudio.ViewModels
{
    /// <summary>
    /// <para>设置视图模型，管理应用程序的通用设置、固件工具设置、烧录设置和节点编辑器设置。</para>
    /// Settings view model, managing general settings, firmware tool settings, flash settings, and node editor settings.
    /// </summary>
    public partial class SettingsViewModel : ViewModelBase
    {
        private int _selectedLanguageIndex;
        private int _selectedThemeIndex;
        private bool _autoSave;
        private string _unpackToolPath = string.Empty;
        private string _tempDirectory = string.Empty;
        private bool _autoVerifyAfterUnpack = true;
        private string _adbPath = string.Empty;
        private string _fastbootPath = string.Empty;
        private int _deviceTimeoutSeconds = 30;
        private bool _autoBackupBeforeFlash;
        private bool _snapToGrid = true;
        private double _gridSize = 20;
        private int _connectionStyleIndex;

        /// <summary>
        /// <para>获取可用语言列表。</para>
        /// Gets the list of available languages.
        /// </summary>
        public List<LanguageOption> Languages { get; } =
        [
            new LanguageOption("English", "en-US"),
            new LanguageOption("简体中文", "zh-CN")
        ];

        /// <summary>
        /// <para>获取可用主题列表。</para>
        /// Gets the list of available themes.
        /// </summary>
        public List<string> Themes { get; } =
        [
            LocalizationService.Instance["Settings_ThemeDefault"],
            LocalizationService.Instance["Settings_ThemeLight"],
            LocalizationService.Instance["Settings_ThemeDark"]
        ];

        /// <summary>
        /// <para>获取可用连接线样式列表。</para>
        /// Gets the list of available connection line styles.
        /// </summary>
        public List<string> ConnectionStyles { get; } =
        [
            LocalizationService.Instance["Settings_ConnBezier"],
            LocalizationService.Instance["Settings_ConnLine"],
            LocalizationService.Instance["Settings_ConnStep"]
        ];

        /// <summary>
        /// <para>获取或设置选中的语言索引。</para>
        /// Gets or sets the selected language index.
        /// </summary>
        public int SelectedLanguageIndex
        {
            get => _selectedLanguageIndex;
            set
            {
                if (SetProperty(ref _selectedLanguageIndex, value) && value >= 0 && value < Languages.Count)
                {
                    LocalizationService.Instance.Culture = new CultureInfo(Languages[value].Code);
                }
            }
        }

        /// <summary>
        /// <para>获取或设置选中的主题索引。</para>
        /// Gets or sets the selected theme index.
        /// </summary>
        public int SelectedThemeIndex
        {
            get => _selectedThemeIndex;
            set => SetProperty(ref _selectedThemeIndex, value);
        }

        /// <summary>
        /// <para>获取或设置是否启用自动保存。</para>
        /// Gets or sets whether auto-save is enabled.
        /// </summary>
        public bool AutoSave
        {
            get => _autoSave;
            set => SetProperty(ref _autoSave, value);
        }

        /// <summary>
        /// <para>获取或设置默认解包工具路径。</para>
        /// Gets or sets the default unpack tool path.
        /// </summary>
        public string UnpackToolPath
        {
            get => _unpackToolPath;
            set => SetProperty(ref _unpackToolPath, value);
        }

        /// <summary>
        /// <para>获取或设置临时文件目录。</para>
        /// Gets or sets the temporary file directory.
        /// </summary>
        public string TempDirectory
        {
            get => _tempDirectory;
            set => SetProperty(ref _tempDirectory, value);
        }

        /// <summary>
        /// <para>获取或设置解包后是否自动校验。</para>
        /// Gets or sets whether to auto-verify after unpacking.
        /// </summary>
        public bool AutoVerifyAfterUnpack
        {
            get => _autoVerifyAfterUnpack;
            set => SetProperty(ref _autoVerifyAfterUnpack, value);
        }

        /// <summary>
        /// <para>获取或设置 ADB 路径。</para>
        /// Gets or sets the ADB path.
        /// </summary>
        public string AdbPath
        {
            get => _adbPath;
            set => SetProperty(ref _adbPath, value);
        }

        /// <summary>
        /// <para>获取或设置 Fastboot 路径。</para>
        /// Gets or sets the Fastboot path.
        /// </summary>
        public string FastbootPath
        {
            get => _fastbootPath;
            set => SetProperty(ref _fastbootPath, value);
        }

        /// <summary>
        /// <para>获取或设置设备超时时间（秒）。</para>
        /// Gets or sets the device timeout in seconds.
        /// </summary>
        public int DeviceTimeoutSeconds
        {
            get => _deviceTimeoutSeconds;
            set => SetProperty(ref _deviceTimeoutSeconds, value);
        }

        /// <summary>
        /// <para>获取或设置烧录前是否自动备份。</para>
        /// Gets or sets whether to auto-backup before flashing.
        /// </summary>
        public bool AutoBackupBeforeFlash
        {
            get => _autoBackupBeforeFlash;
            set => SetProperty(ref _autoBackupBeforeFlash, value);
        }

        /// <summary>
        /// <para>获取或设置是否启用网格对齐。</para>
        /// Gets or sets whether snap-to-grid is enabled.
        /// </summary>
        public bool SnapToGrid
        {
            get => _snapToGrid;
            set => SetProperty(ref _snapToGrid, value);
        }

        /// <summary>
        /// <para>获取或设置网格大小。</para>
        /// Gets or sets the grid size.
        /// </summary>
        public double GridSize
        {
            get => _gridSize;
            set => SetProperty(ref _gridSize, value);
        }

        /// <summary>
        /// <para>获取或设置连接线样式索引。</para>
        /// Gets or sets the connection line style index.
        /// </summary>
        public int ConnectionStyleIndex
        {
            get => _connectionStyleIndex;
            set => SetProperty(ref _connectionStyleIndex, value);
        }

        /// <summary>
        /// <para>初始化设置视图模型，根据当前系统语言设置默认语言索引。</para>
        /// Initializes the settings view model, setting the default language index based on the current system language.
        /// </summary>
        public SettingsViewModel()
        {
            var currentCulture = LocalizationService.Instance.Culture?.Name ?? "en-US";
            _selectedLanguageIndex = Languages.FindIndex(l => l.Code == currentCulture);
            if (_selectedLanguageIndex < 0) _selectedLanguageIndex = 0;
        }
    }

    /// <summary>
    /// <para>语言选项，包含显示名称和语言代码。</para>
    /// Language option, containing display name and language code.
    /// </summary>
    public class LanguageOption
    {
        /// <summary>
        /// <para>获取语言显示名称。</para>
        /// Gets the language display name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// <para>获取语言代码。</para>
        /// Gets the language code.
        /// </summary>
        public string Code { get; }

        /// <summary>
        /// <para>创建语言选项。</para>
        /// Creates a language option.
        /// </summary>
        /// <param name="name">
        /// <para>语言显示名称。</para>
        /// The language display name.
        /// </param>
        /// <param name="code">
        /// <para>语言代码。</para>
        /// The language code.
        /// </param>
        public LanguageOption(string name, string code)
        {
            Name = name;
            Code = code;
        }

        /// <summary>
        /// <para>返回语言显示名称。</para>
        /// Returns the language display name.
        /// </summary>
        public override string ToString() => Name;
    }
}
