using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Messages;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// Packing settings panel view model (singleton), holding image type, format, compression, super and
/// DAT options, mapping bidirectionally with <see cref="ProjectSettingsDto"/> to support project
/// snapshot save/restore. Automatically loads settings when the active sub-project changes.
/// <para>打包设置面板视图模型（单例），承载镜像类型、格式、压缩、super 与 DAT 等打包选项，并负责
/// 与 <see cref="ProjectSettingsDto"/> 双向映射以支持工程快照的保存与恢复。切换活动子项目时
/// 自动加载对应设置。</para>
/// </summary>
internal partial class ProjectSettingViewModel : Tool
{
    /// <summary>
    /// Gets the active packing settings panel singleton, shared by the workspace dock and the menu.
    /// <para>获取当前打包设置面板单例实例，供工作区停靠面板与菜单共享。</para>
    /// </summary>
    public static ProjectSettingViewModel? Instance { get; private set; }

    /// <summary>
    /// Ensures the packing settings panel singleton exists and returns it; the first call constructs the
    /// instance (registered on <see cref="Instance"/> in the constructor).
    /// <para>确保打包设置面板单例已创建并返回它；首次调用时构造并在构造函数中注册到 <see cref="Instance"/>。</para>
    /// </summary>
    internal static ProjectSettingViewModel EnsureInstance() => Instance ??= new();

    [ObservableProperty]
    private bool typeEXT4 = false, typeF2FS = false, formatSPARSE = false, eXT4RO = false, sizeOri = false,
        eXT4Compress = false, sUPERSPARSE = false, typeEROFS = true, formatRAW = true, eXT4RW = true,
        sizeAuto = true, eXT4NoCompress = true, sUPERRAW = true;
    [ObservableProperty]
    private string selectedErofsAlgorithm = ErofsAlgorithmSelectors.Lz4HighCompression;
    [ObservableProperty]
    private int eROFSComGrade = 8, bRComGrade = 3;
    [ObservableProperty]
    private string erofsFeatureOverrides = "";
    [ObservableProperty]
    private bool f2fsCompressionEnabled;
    [ObservableProperty]
    private string selectedF2fsCompressionAlgorithm = nameof(CompressionAlgorithm.Lz4);
    [ObservableProperty]
    private int f2fsCompressionLevel;
    [ObservableProperty]
    private bool f2fsAndroidNativeProfile;
    [ObservableProperty]
    private long? f2fsAndroidImageSizeBytes;
    [ObservableProperty]
    private string f2fsNativeWslDistribution = "";
    [ObservableProperty]
    private Ext4CapacityPolicy ext4CapacityPolicy = Ext4CapacityPolicy.Inherit;
    [ObservableProperty]
    private long? ext4TargetSizeBytes;
    [ObservableProperty]
    private Ext4BlockSharingMode ext4BlockSharingMode;
    [ObservableProperty]
    private string projectName = "", sUPERSize = "9126805504", sUPERName = "qti_dynamic_partitions", uTCTime = "1230768000", dATSeg = "15";

    /// <summary>
    /// Picker choices for the EROFS codec combo, in display order.
    /// <para>供 EROFS 算法下拉框绑定：按显示顺序排列的可选编解码器令牌。</para>
    /// </summary>
    public IReadOnlyList<string> ErofsAlgorithmOptions => ErofsAlgorithmSelectors.Ordered;

    /// <summary>Compression algorithms supported by the managed F2FS builder.</summary>
    public IReadOnlyList<string> F2fsCompressionAlgorithmOptions { get; } = Enum.GetNames<CompressionAlgorithm>();

    /// <summary>Whether the F2FS compression values are an explicit user override.</summary>
    internal bool HasF2fsCompressionOverride { get; private set; }
    internal bool HasF2fsAndroidProfileOverride { get; private set; }

    /// <summary>Whether an EROFS feature declaration edit should override the preserved source words.</summary>
    internal bool HasErofsFeatureOverride { get; private set; }

    /// <summary>Whether EXT4 policy values should override preserved source parameters.</summary>
    internal bool HasExt4PolicyOverride { get; private set; }

    /// <summary>Whether RAW/Android Sparse output was explicitly selected.</summary>
    internal bool HasImageFormatOverride { get; private set; }

    private bool _applyingDto;

    /// <summary>
    /// Creates the packing settings panel view model and registers the singleton instance; subscribes to
    /// project-context-changed messages to auto-load settings on sub-project switch.
    /// <para>创建打包设置面板视图模型并注册单例实例；订阅工程上下文变更消息以在子项目切换时自动加载设置。</para>
    /// </summary>
    public ProjectSettingViewModel()
    {
        Instance = this;
        WeakReferenceMessenger.Default.Register<ProjectContextChangedMessage>(this, OnProjectContextChanged);
    }

    /// <summary>
    /// Responds to project-context changes: loads the packing settings from the corresponding
    /// <c>.uotanfw</c> when the active sub-project changes.
    /// <para>响应工程上下文变更：切换活动子项目时加载对应 <c>.uotanfw</c> 中的打包设置。</para>
    /// </summary>
    private async void OnProjectContextChanged(object recipient, ProjectContextChangedMessage message)
    {
        var context = ProjectContextService.Instance;
        if (!context.IsProjectOpen || context.FirmwareProjectPath is null)
        {
            return;
        }

        var path = context.FirmwareProjectPath;
        var version = ++_loadVersion;
        var firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(path);
        if (version == _loadVersion && context.FirmwareProjectPath == path && firmware?.Settings is not null)
        {
            ApplyFromDto(firmware.Settings);
        }
    }

    private int _loadVersion;

    /// <summary>
    /// Exports the current panel state as a packing settings data transfer object.
    /// <para>将当前面板状态导出为打包设置数据传输对象。</para>
    /// </summary>
    /// <returns>
    /// A settings DTO mapping one-to-one with the current panel fields.
    /// <para>与当前面板字段一一对应的设置 DTO。</para>
    /// </returns>
    public ProjectSettingsDto ToDto() => new()
    {
        ImageType = TypeEXT4 ? "EXT4" : TypeF2FS ? "F2FS" : "EROFS",
        ImageFormat = FormatSPARSE ? "SPARSE" : "RAW",
        Ext4ReadWrite = EXT4RW,
        SizeAuto = SizeAuto,
        Ext4Compress = HasExt4PolicyOverride
            ? Ext4CapacityPolicy == global::UotanStudio.Services.Projects.Ext4CapacityPolicy.Minimize
            : null,
        Ext4CapacityPolicy = HasExt4PolicyOverride ? Ext4CapacityPolicy : null,
        Ext4TargetSizeBytes = HasExt4PolicyOverride ? Ext4TargetSizeBytes : null,
        Ext4ReadOnly = HasExt4PolicyOverride ? EXT4RO : null,
        Ext4BlockSharingMode = HasExt4PolicyOverride ? Ext4BlockSharingMode.ToString() : null,
        SparseOutput = HasImageFormatOverride ? FormatSPARSE : null,
        ErofsAlgorithm = SelectedErofsAlgorithm,
        ErofsCompLevel = EROFSComGrade,
        ErofsFeatureOverrides = HasErofsFeatureOverride && !string.IsNullOrWhiteSpace(ErofsFeatureOverrides)
            ? ErofsFeatureOverrides.Trim()
            : null,
        F2fsCompressionEnabled = HasF2fsCompressionOverride ? F2fsCompressionEnabled : null,
        F2fsCompressionAlgorithm = HasF2fsCompressionOverride ? SelectedF2fsCompressionAlgorithm : null,
        F2fsCompressionLevel = HasF2fsCompressionOverride ? F2fsCompressionLevel : null,
        F2fsAndroidNativeProfile = HasF2fsAndroidProfileOverride ? F2fsAndroidNativeProfile : null,
        F2fsAndroidImageSizeBytes = HasF2fsAndroidProfileOverride ? F2fsAndroidImageSizeBytes : null,
        F2fsNativeWslDistribution = HasF2fsAndroidProfileOverride ? F2fsNativeWslDistribution : null,
        BrCompLevel = BRComGrade,
        SuperSize = SUPERSize,
        SuperName = SUPERName,
        SuperFormat = SUPERSPARSE ? "SPARSE" : "RAW",
        UtcTime = UTCTime,
        DatSegments = DATSeg
    };

    /// <summary>
    /// Applies a packing settings data transfer object to the current panel fields (also updating each
    /// mutually exclusive toggle group).
    /// <para>将打包设置数据传输对象应用到当前面板字段（同时更新互斥的一组互斥开关）。</para>
    /// </summary>
    /// <param name="dto">
    /// The DTO whose settings are applied.
    /// <para>要应用设置的 DTO。</para>
    /// </param>
    public void ApplyFromDto(ProjectSettingsDto dto)
    {
        if (dto is null)
        {
            return;
        }

        if (dto.Ext4CapacityPolicy is { } ext4Policy && !Enum.IsDefined(ext4Policy))
        {
            throw new InvalidDataException($"Unsupported EXT4 capacity policy '{ext4Policy}'.");
        }

        var blockSharingMode = global::UotanStudio.Firmware.FileSystem.Ext4.Ext4BlockSharingMode.Independent;
        if (dto.Ext4BlockSharingMode is { } blockSharingValue
            && (!Enum.TryParse(blockSharingValue, ignoreCase: true, out blockSharingMode)
                || !Enum.IsDefined(blockSharingMode)))
        {
            throw new InvalidDataException($"Unsupported EXT4 block sharing mode '{blockSharingValue}'.");
        }

        _applyingDto = true;
        var isExt4 = string.Equals(dto.ImageType, "EXT4", StringComparison.OrdinalIgnoreCase);
        var isF2fs = string.Equals(dto.ImageType, "F2FS", StringComparison.OrdinalIgnoreCase);
        var isSparse = dto.SparseOutput
            ?? string.Equals(dto.ImageFormat, "SPARSE", StringComparison.OrdinalIgnoreCase);
        var isSuperSparse = string.Equals(dto.SuperFormat, "SPARSE", StringComparison.OrdinalIgnoreCase);

        TypeEXT4 = isExt4;
        TypeEROFS = !isExt4 && !isF2fs;
        TypeF2FS = isF2fs;
        FormatRAW = !isSparse;
        FormatSPARSE = isSparse;
        HasImageFormatOverride = dto.SparseOutput.HasValue
            || string.Equals(dto.ImageFormat, "SPARSE", StringComparison.OrdinalIgnoreCase);
        EXT4RO = !dto.Ext4ReadWrite;
        EXT4RW = dto.Ext4ReadWrite;
        SizeAuto = dto.SizeAuto;
        SizeOri = !dto.SizeAuto;
        EXT4NoCompress = dto.Ext4Compress != true;
        EXT4Compress = dto.Ext4Compress == true;
        Ext4CapacityPolicy = dto.Ext4CapacityPolicy ?? (dto.Ext4Compress == true
            ? global::UotanStudio.Services.Projects.Ext4CapacityPolicy.Minimize
            : global::UotanStudio.Services.Projects.Ext4CapacityPolicy.Inherit);
        Ext4TargetSizeBytes = dto.Ext4TargetSizeBytes;
        Ext4BlockSharingMode = blockSharingMode;
        HasExt4PolicyOverride = dto.Ext4CapacityPolicy.HasValue
            || dto.Ext4TargetSizeBytes.HasValue
            || dto.Ext4ReadOnly.HasValue
            || dto.Ext4BlockSharingMode is not null
            || dto.Ext4Compress.HasValue;
        if (dto.Ext4ReadOnly.HasValue)
        {
            EXT4RO = dto.Ext4ReadOnly.Value;
            EXT4RW = !dto.Ext4ReadOnly.Value;
        }

        SelectedErofsAlgorithm = string.IsNullOrWhiteSpace(dto.ErofsAlgorithm)
            ? ErofsAlgorithmSelectors.Lz4HighCompression
            : ErofsAlgorithmSelectors.Normalize(dto.ErofsAlgorithm);
        EROFSComGrade = dto.ErofsCompLevel;
        ErofsFeatureOverrides = dto.ErofsFeatureOverrides ?? "";
        HasErofsFeatureOverride = !string.IsNullOrWhiteSpace(dto.ErofsFeatureOverrides);
        F2fsCompressionEnabled = dto.F2fsCompressionEnabled ?? false;
        SelectedF2fsCompressionAlgorithm = NormalizeF2fsCompressionAlgorithm(dto.F2fsCompressionAlgorithm);
        F2fsCompressionLevel = dto.F2fsCompressionLevel ?? 0;
        F2fsAndroidNativeProfile = dto.F2fsAndroidNativeProfile ?? false;
        F2fsAndroidImageSizeBytes = dto.F2fsAndroidImageSizeBytes;
        F2fsNativeWslDistribution = dto.F2fsNativeWslDistribution ?? "";
        HasF2fsAndroidProfileOverride = dto.F2fsAndroidNativeProfile.HasValue
            || dto.F2fsAndroidImageSizeBytes.HasValue
            || dto.F2fsNativeWslDistribution is not null;
        HasF2fsCompressionOverride = dto.F2fsCompressionEnabled.HasValue
            || dto.F2fsCompressionAlgorithm is not null
            || dto.F2fsCompressionLevel.HasValue;
        BRComGrade = dto.BrCompLevel;
        SUPERSize = dto.SuperSize;
        SUPERName = dto.SuperName;
        SUPERRAW = !isSuperSparse;
        SUPERSPARSE = isSuperSparse;
        UTCTime = dto.UtcTime;
        DATSeg = dto.DatSegments;
        _applyingDto = false;
    }

    partial void OnErofsFeatureOverridesChanged(string value) => MarkErofsFeatureOverride();

    partial void OnF2fsCompressionEnabledChanged(bool value) => MarkF2fsCompressionOverride();

    partial void OnSelectedF2fsCompressionAlgorithmChanged(string value) => MarkF2fsCompressionOverride();

    partial void OnF2fsCompressionLevelChanged(int value) => MarkF2fsCompressionOverride();

    partial void OnF2fsAndroidNativeProfileChanged(bool value) => MarkF2fsAndroidProfileOverride();

    partial void OnF2fsAndroidImageSizeBytesChanged(long? value) => MarkF2fsAndroidProfileOverride();

    partial void OnF2fsNativeWslDistributionChanged(string value) => MarkF2fsAndroidProfileOverride();

    private void MarkF2fsAndroidProfileOverride()
    {
        if (!_applyingDto)
        {
            HasF2fsAndroidProfileOverride = true;
        }
    }

    partial void OnExt4CapacityPolicyChanged(Ext4CapacityPolicy value) => MarkExt4PolicyOverride();

    partial void OnExt4TargetSizeBytesChanged(long? value) => MarkExt4PolicyOverride();

    partial void OnExt4BlockSharingModeChanged(Ext4BlockSharingMode value) => MarkExt4PolicyOverride();

    partial void OnEXT4CompressChanged(bool value)
    {
        if (_applyingDto)
        {
            return;
        }

        Ext4CapacityPolicy = value
            ? global::UotanStudio.Services.Projects.Ext4CapacityPolicy.Minimize
            : global::UotanStudio.Services.Projects.Ext4CapacityPolicy.Inherit;
        MarkExt4PolicyOverride();
    }

    partial void OnEXT4ROChanged(bool value) => MarkExt4PolicyOverride();

    partial void OnEXT4RWChanged(bool value) => MarkExt4PolicyOverride();

    partial void OnFormatSPARSEChanged(bool value) => MarkImageFormatOverride();

    partial void OnFormatRAWChanged(bool value) => MarkImageFormatOverride();

    private void MarkErofsFeatureOverride()
    {
        if (!_applyingDto)
        {
            HasErofsFeatureOverride = true;
        }
    }

    private void MarkF2fsCompressionOverride()
    {
        if (!_applyingDto)
        {
            HasF2fsCompressionOverride = true;
        }
    }

    private void MarkExt4PolicyOverride()
    {
        if (!_applyingDto)
        {
            HasExt4PolicyOverride = true;
        }
    }

    private void MarkImageFormatOverride()
    {
        if (!_applyingDto)
        {
            HasImageFormatOverride = true;
        }
    }

    private static string NormalizeF2fsCompressionAlgorithm(string? value)
        => value is not null
            && Enum.TryParse<CompressionAlgorithm>(value, ignoreCase: true, out var algorithm)
            && Enum.IsDefined(algorithm)
                ? algorithm.ToString()
                : value ?? nameof(CompressionAlgorithm.Lz4);

    /// <summary>
    /// Infers the packing type from the source image format: ext4 → EXT4, erofs → EROFS;
    /// other formats leave the current setting unchanged. Only takes effect when the panel is
    /// still at its default values (i.e. not manually overridden).
    /// <para>根据源镜像格式自动推断打包类型：ext4 → EXT4，erofs → EROFS，其余不变。
    /// 仅在用户未手动覆盖时生效（面板当前值为默认值时才自动切换）。</para>
    /// </summary>
    /// <param name="format">Format string from DetectFormat. <para>DetectFormat 返回的格式字符串。</para></param>
    public void ApplySourceFormat(string format)
    {
        if (string.IsNullOrEmpty(format))
        {
            return;
        }

        bool isDefaultState = TypeEROFS && !TypeEXT4 && !TypeF2FS && FormatRAW && !FormatSPARSE;

        if (!isDefaultState)
        {
            return;
        }

        switch (format)
        {
            case "ext4":
                TypeEXT4 = true;
                TypeEROFS = false;
                TypeF2FS = false;
                break;
            case "erofs":
                TypeEROFS = true;
                TypeEXT4 = false;
                TypeF2FS = false;
                break;
            case "f2fs":
                TypeF2FS = true;
                TypeEXT4 = false;
                TypeEROFS = false;
                break;
        }
    }
}