using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Messages;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>打包设置面板视图模型（单例），承载镜像类型、格式、压缩、super 与 DAT 等打包选项，并负责
/// 与 <see cref="ProjectSettingsDto"/> 双向映射以支持工程快照的保存与恢复。切换活动子项目时
/// 自动加载对应设置。</para>
/// Packing settings panel view model (singleton), holding image type, format, compression, super and
/// DAT options, mapping bidirectionally with <see cref="ProjectSettingsDto"/> to support project
/// snapshot save/restore. Automatically loads settings when the active sub-project changes.
/// </summary>
internal partial class ProjectSettingViewModel : Tool
{
    /// <summary>
    /// <para>获取当前打包设置面板单例实例，供工作区停靠面板与菜单共享。</para>
    /// Gets the active packing settings panel singleton, shared by the workspace dock and the menu.
    /// </summary>
    public static ProjectSettingViewModel? Instance { get; private set; }

    /// <summary>
    /// <para>确保打包设置面板单例已创建并返回它；首次调用时构造并在构造函数中注册到 <see cref="Instance"/>。</para>
    /// Ensures the packing settings panel singleton exists and returns it; the first call constructs the
    /// instance (registered on <see cref="Instance"/> in the constructor).
    /// </summary>
    internal static ProjectSettingViewModel EnsureInstance() => Instance ??= new();

    [ObservableProperty]
    private bool typeEXT4 = false, formatSPARSE = false, eXT4RO = false, sizeOri = false, eXT4Compress = false, eROFSLZ4HC = false, sUPERSPARSE = false,
        typeEROFS = true, formatRAW = true, eXT4RW = true, sizeAuto = true, eXT4NoCompress = true, eROFSLZ4 = true, sUPERRAW = true;
    [ObservableProperty]
    private int eROFSComGrade = 8, bRComGrade = 3;
    [ObservableProperty]
    private string projectName = "", sUPERSize = "9126805504", sUPERName = "qti_dynamic_partitions", uTCTime = "1230768000", dATSeg = "15";

    /// <summary>
    /// <para>创建打包设置面板视图模型并注册单例实例；订阅工程上下文变更消息以在子项目切换时自动加载设置。</para>
    /// Creates the packing settings panel view model and registers the singleton instance; subscribes to
    /// project-context-changed messages to auto-load settings on sub-project switch.
    /// </summary>
    public ProjectSettingViewModel()
    {
        Instance = this;
        WeakReferenceMessenger.Default.Register<ProjectContextChangedMessage>(this, OnProjectContextChanged);
    }

    /// <summary>
    /// <para>响应工程上下文变更：切换活动子项目时加载对应 <c>.uotanfw</c> 中的打包设置。</para>
    /// Responds to project-context changes: loads the packing settings from the corresponding
    /// <c>.uotanfw</c> when the active sub-project changes.
    /// </summary>
    private async void OnProjectContextChanged(object recipient, ProjectContextChangedMessage message)
    {
        var context = ProjectContextService.Instance;
        if (!context.IsProjectOpen || context.FirmwareProjectPath is null)
        {
            return;
        }

        var firmware = await SolutionPersistenceService.LoadFirmwareProjectAsync(context.FirmwareProjectPath);
        if (firmware?.Settings is not null)
        {
            ApplyFromDto(firmware.Settings);
        }
    }

    /// <summary>
    /// <para>将当前面板状态导出为打包设置数据传输对象。</para>
    /// Exports the current panel state as a packing settings data transfer object.
    /// </summary>
    /// <returns>
    /// <para>与当前面板字段一一对应的设置 DTO。</para>
    /// A settings DTO mapping one-to-one with the current panel fields.
    /// </returns>
    public ProjectSettingsDto ToDto() => new()
    {
        ImageType = TypeEXT4 ? "EXT4" : "EROFS",
        ImageFormat = FormatSPARSE ? "SPARSE" : "RAW",
        Ext4ReadWrite = EXT4RW,
        SizeAuto = SizeAuto,
        Ext4Compress = EXT4Compress,
        ErofsAlgorithm = EROFSLZ4HC ? "lz4hc" : "lz4",
        ErofsCompLevel = EROFSComGrade,
        BrCompLevel = BRComGrade,
        SuperSize = SUPERSize,
        SuperName = SUPERName,
        SuperFormat = SUPERSPARSE ? "SPARSE" : "RAW",
        UtcTime = UTCTime,
        DatSegments = DATSeg
    };

    /// <summary>
    /// <para>将打包设置数据传输对象应用到当前面板字段（同时更新互斥的一组互斥开关）。</para>
    /// Applies a packing settings data transfer object to the current panel fields (also updating each
    /// mutually exclusive toggle group).
    /// </summary>
    /// <param name="dto">
    /// <para>要应用设置的 DTO。</para>
    /// The DTO whose settings are applied.
    /// </param>
    public void ApplyFromDto(ProjectSettingsDto dto)
    {
        if (dto is null)
        {
            return;
        }

        var isExt4 = string.Equals(dto.ImageType, "EXT4", StringComparison.OrdinalIgnoreCase);
        var isSparse = string.Equals(dto.ImageFormat, "SPARSE", StringComparison.OrdinalIgnoreCase);
        var isLz4Hc = string.Equals(dto.ErofsAlgorithm, "lz4hc", StringComparison.OrdinalIgnoreCase);
        var isSuperSparse = string.Equals(dto.SuperFormat, "SPARSE", StringComparison.OrdinalIgnoreCase);

        TypeEXT4 = isExt4;
        TypeEROFS = !isExt4;
        FormatRAW = !isSparse;
        FormatSPARSE = isSparse;
        EXT4RO = !dto.Ext4ReadWrite;
        EXT4RW = dto.Ext4ReadWrite;
        SizeAuto = dto.SizeAuto;
        SizeOri = !dto.SizeAuto;
        EXT4NoCompress = !dto.Ext4Compress;
        EXT4Compress = dto.Ext4Compress;
        EROFSLZ4 = !isLz4Hc;
        EROFSLZ4HC = isLz4Hc;
        EROFSComGrade = dto.ErofsCompLevel;
        BRComGrade = dto.BrCompLevel;
        SUPERSize = dto.SuperSize;
        SUPERName = dto.SuperName;
        SUPERRAW = !isSuperSparse;
        SUPERSPARSE = isSuperSparse;
        UTCTime = dto.UtcTime;
        DATSeg = dto.DatSegments;
    }

    /// <summary>
    /// <para>根据源镜像格式自动推断打包类型：ext4 → EXT4，erofs → EROFS，其余不变。
    /// 仅在用户未手动覆盖时生效（面板当前值为默认值时才自动切换）。</para>
    /// Infers the packing type from the source image format: ext4 → EXT4, erofs → EROFS;
    /// other formats leave the current setting unchanged. Only takes effect when the panel is
    /// still at its default values (i.e. not manually overridden).
    /// </summary>
    /// <param name="format"><para>DetectFormat 返回的格式字符串。</para>Format string from DetectFormat.</param>
    public void ApplySourceFormat(string format)
    {
        if (string.IsNullOrEmpty(format))
        {
            return;
        }

        // 仅当面板处于默认状态（EROFS + RAW）时才自动切换，避免覆盖用户手动选择。
        // Only auto-switch when the panel is at its default state (EROFS + RAW) to avoid
        // overriding a manual selection.
        bool isDefaultState = TypeEROFS && !TypeEXT4 && FormatRAW && !FormatSPARSE;

        if (!isDefaultState)
        {
            return;
        }

        switch (format)
        {
            case "ext4":
                TypeEXT4 = true;
                TypeEROFS = false;
                break;
            case "erofs":
                TypeEROFS = true;
                TypeEXT4 = false;
                break;
            case "f2fs":
                // F2FS 无打包支持，保持当前设置。
                // F2FS has no pack support; leave settings unchanged.
                break;
        }
    }
}