using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;
using UotanStudio.Services.Projects.Schemes;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Operator view of one declared scheme asset, carrying its declared identity and the latest materialization state.
/// <para>单个声明 Scheme 资产的操作员视图，携带其声明身份与最近一次物化状态。</para>
/// </summary>
public sealed partial class SchemeAssetRunViewModel : ObservableObject
{
    /// <summary>
    /// Creates the view from a declared asset.
    /// <para>按声明的资产创建视图。</para>
    /// </summary>
    /// <param name="asset">The declared asset; must not be <see langword="null"/>.
    /// <para>声明的资产；不能为 <see langword="null"/>。</para></param>
    public SchemeAssetRunViewModel(SchemeAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        AssetId = asset.Id;
        Name = asset.Name;
        RelativePath = asset.RelativePath;
        Sha256 = asset.Sha256;
        Length = asset.Length;
    }

    /// <summary>
    /// Stable asset identifier.
    /// <para>稳定的资产标识符。</para>
    /// </summary>
    public string AssetId { get; }

    /// <summary>
    /// Declared display name.
    /// <para>声明的显示名称。</para>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Path declared relative to the scheme file.
    /// <para>相对 Scheme 文件声明的路径。</para>
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Declared lower-case SHA-256 digest, when the author declared one.
    /// <para>作者声明时的小写 SHA-256 摘要。</para>
    /// </summary>
    public string? Sha256 { get; }

    /// <summary>
    /// Declared length in bytes, or <c>0</c> when the author declared none.
    /// <para>声明的字节长度；作者未声明时为 <c>0</c>。</para>
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Caption used by the asset list, falling back to <see cref="AssetId"/> when the declaration has no name.
    /// <para>资产列表使用的标题；声明没有名称时回退到 <see cref="AssetId"/>。</para>
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? AssetId : Name;

    /// <summary>
    /// Latest materialization state.
    /// <para>最近一次物化状态。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(StatusKey))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private SchemeAssetState _state = SchemeAssetState.NotChecked;

    /// <summary>
    /// Technical failure description, or <see langword="null"/> when the asset is valid.
    /// <para>技术性失败说明；资产有效时为 <see langword="null"/>。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    /// <summary>
    /// Absolute path resolved by the latest inspection or materialization.
    /// <para>最近一次检查或物化解析得到的绝对路径。</para>
    /// </summary>
    [ObservableProperty]
    private string? _resolvedPath;

    /// <summary>
    /// Whether the asset is available and matches its declaration.
    /// <para>资产可用且与声明一致。</para>
    /// </summary>
    public bool IsValid => State == SchemeAssetState.Valid;

    /// <summary>
    /// Whether the latest inspection reported a problem.
    /// <para>最近一次检查是否报告了问题。</para>
    /// </summary>
    public bool HasError => !IsValid && State != SchemeAssetState.NotChecked;

    /// <summary>
    /// Localization key describing <see cref="State"/>.
    /// <para>描述 <see cref="State"/> 的本地化键。</para>
    /// </summary>
    public string StatusKey => State switch
    {
        SchemeAssetState.Valid => "Node_Status_Succeeded",
        SchemeAssetState.NotChecked => "Node_Status_Idle",
        _ => "Node_Status_Failed"
    };

    /// <summary>
    /// Localized status caption.
    /// <para>本地化的状态标题。</para>
    /// </summary>
    public string StatusText => LanguageService.Instance.GetString(StatusKey);

    /// <summary>
    /// Applies an inspection result.
    /// <para>应用一次检查结果。</para>
    /// </summary>
    /// <param name="check">The inspection result; must not be <see langword="null"/>.
    /// <para>检查结果；不能为 <see langword="null"/>。</para></param>
    public void ApplyCheck(SchemeAssetCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        State = check.State;
        ErrorMessage = check.Error;
        ResolvedPath = check.SourcePath;
    }

    /// <summary>
    /// Restores the pre-run state.
    /// <para>还原运行前状态。</para>
    /// </summary>
    public void Reset()
    {
        State = SchemeAssetState.NotChecked;
        ErrorMessage = null;
        ResolvedPath = null;
    }
}