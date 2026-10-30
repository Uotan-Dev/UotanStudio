using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// One row of the settings page driver installation list: the package identity, its localized texts and
/// whether an operation is currently running on it.
/// <para>设置页驱动安装列表的一行：驱动包身份、其本地化文本，以及当前是否有操作在进行。</para>
/// </summary>
public sealed partial class DriverPackageItemViewModel : ObservableObject
{
    /// <summary>
    /// Whether an install or removal is running for this row; the row buttons stay disabled until it
    /// completes.
    /// <para>该行是否有安装或移除正在进行；完成前该行按钮保持禁用。</para>
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Creates one driver package row.<para>创建一个驱动包行。</para></summary>
    /// <param name="id">The stable package identifier.<para>稳定的驱动包标识。</para></param>
    /// <param name="nameText">The localized package name.<para>本地化的驱动包名。</para></param>
    /// <param name="coverageText">The localized device coverage description.
    /// <para>本地化的设备覆盖范围说明。</para></param>
    /// <param name="detailText">The localized file and size summary.<para>本地化的文件与体积摘要。</para></param>
    /// <param name="isAvailable">Whether this build carries the package payload.
    /// <para>本构建是否携带该包负载。</para></param>
    /// <param name="unavailableText">The localized reason shown instead of the buttons when the payload is
    /// missing; ignored for an available package.
    /// <para>缺包时代替按钮显示的本地化原因；包可用时忽略。</para></param>
    public DriverPackageItemViewModel(string id, string nameText, string coverageText, string detailText,
        bool isAvailable, string unavailableText)
    {
        Id = id;
        NameText = nameText;
        CoverageText = coverageText;
        DetailText = detailText;
        IsAvailable = isAvailable;
        UnavailableText = unavailableText;
    }

    /// <summary>Gets the stable package identifier passed back to the install and removal commands.
    /// <para>获取回传给安装与移除命令的稳定驱动包标识。</para>
    /// </summary>
    /// <value>The package identifier.<para>驱动包标识。</para></value>
    public string Id { get; }

    /// <summary>Gets the localized package name.<para>获取本地化的驱动包名。</para></summary>
    /// <value>The display name.<para>显示名。</para></value>
    public string NameText { get; }

    /// <summary>Gets the localized device coverage description.
    /// <para>获取本地化的设备覆盖范围说明。</para>
    /// </summary>
    /// <value>The coverage text.<para>覆盖范围文本。</para></value>
    public string CoverageText { get; }

    /// <summary>Gets the localized file count and payload size summary.
    /// <para>获取本地化的文件数量与负载体积摘要。</para>
    /// </summary>
    /// <value>The detail text.<para>摘要文本。</para></value>
    public string DetailText { get; }

    /// <summary>Gets whether this build carries the package payload.
    /// <para>获取本构建是否携带该包负载。</para>
    /// </summary>
    /// <value><see langword="true"/> when the package can be installed here.
    /// <para>该包可在当前环境安装时为 <see langword="true"/>。</para></value>
    public bool IsAvailable { get; }

    /// <summary>Gets whether the row offers its install and removal buttons.
    /// <para>获取该行是否提供安装与移除按钮。</para>
    /// </summary>
    /// <value><see langword="true"/> for an available package with no running operation.
    /// <para>已内置且当前无进行中操作时为 <see langword="true"/>。</para></value>
    public bool CanOperate => IsAvailable && !IsBusy;

    /// <summary>Gets whether this build omits the package payload, so the row states a reason instead of
    /// offering its buttons.
    /// <para>获取本构建是否缺少该包负载；缺包时该行说明原因而不提供按钮。</para>
    /// </summary>
    /// <value><see langword="true"/> when the package cannot be installed here.
    /// <para>该包无法在当前环境安装时为 <see langword="true"/>。</para></value>
    public bool IsUnavailable => !IsAvailable;

    /// <summary>Gets the localized reason shown in place of the buttons when this build omits the payload.
    /// <para>获取本构建缺包时代替按钮显示的本地化原因。</para>
    /// </summary>
    /// <value>The reason text, or an empty string for an available package.
    /// <para>原因文本；包可用时为空字符串。</para></value>
    public string UnavailableText { get; }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanOperate));
}