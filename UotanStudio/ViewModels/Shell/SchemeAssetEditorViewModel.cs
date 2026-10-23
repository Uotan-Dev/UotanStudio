using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Projects.Schemes;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Editable view of one declared scheme asset.
/// <para>单个声明 Scheme 资产的可编辑视图。</para>
/// </summary>
public sealed partial class SchemeAssetEditorViewModel : ObservableObject
{
    /// <summary>
    /// Creates an editor row from a declared asset.
    /// <para>按声明资产创建编辑行。</para>
    /// </summary>
    /// <param name="asset">The asset to edit; must not be <see langword="null"/>.
    /// <para>要编辑的资产；不能为 <see langword="null"/>。</para></param>
    public SchemeAssetEditorViewModel(SchemeAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        _id = asset.Id;
        _name = asset.Name;
        _relativePath = asset.RelativePath;
        _sha256 = asset.Sha256 ?? string.Empty;
        _lengthText = asset.Length.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Stable asset identifier.
    /// <para>稳定的资产标识符。</para>
    /// </summary>
    [ObservableProperty]
    private string _id;

    /// <summary>
    /// Display name of the asset.
    /// <para>资产的显示名称。</para>
    /// </summary>
    [ObservableProperty]
    private string _name;

    /// <summary>
    /// Path relative to the scheme directory.
    /// <para>相对 Scheme 目录的路径。</para>
    /// </summary>
    [ObservableProperty]
    private string _relativePath;

    /// <summary>
    /// Expected lower-case SHA-256 digest; empty when the asset declares no guard.
    /// <para>预期的小写 SHA-256 摘要；资产未声明校验值时为空。</para>
    /// </summary>
    [ObservableProperty]
    private string _sha256;

    /// <summary>
    /// Expected asset length as editable text.
    /// <para>以可编辑文本表示的预期资产长度。</para>
    /// </summary>
    [ObservableProperty]
    private string _lengthText;

    /// <summary>
    /// Projects the row back to a persistence asset.
    /// <para>把编辑行投影回持久化资产。</para>
    /// </summary>
    /// <returns>The edited asset. <para>编辑后的资产。</para></returns>
    public SchemeAsset ToAsset() => new()
    {
        Id = Id.Trim(),
        Name = Name.Trim(),
        RelativePath = RelativePath.Trim().Replace('\\', '/'),
        Sha256 = string.IsNullOrWhiteSpace(Sha256) ? null : Sha256.Trim(),
        Length = long.TryParse(LengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && length > 0
            ? length
            : 0
    };
}