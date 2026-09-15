using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// <para>「粘贴并校验」的比对结果。</para>
/// The comparison result of the "paste and verify" row.
/// </summary>
public enum ChecksumMatchResult
{
    /// <summary>尚未比对（没有文件或粘贴内容为空）。 Not compared yet (no file or empty pasted text).</summary>
    None,

    /// <summary>粘贴内容与任一校验值一致。 The pasted text matches one of the computed values.</summary>
    Correct,

    /// <summary>粘贴内容与所有校验值都不一致。 The pasted text matches none of the computed values.</summary>
    Wrong,
}

/// <summary>
/// <para>校验窗口视图模型：文件名选择、MD5/SHA1/SHA256/CRC 勾选与结果显示，以及「粘贴并校验」比对。</para>
/// Checksum window view model: file selection, the MD5/SHA1/SHA256/CRC checkboxes and result rows, plus the
/// "paste and verify" comparison.
/// </summary>
/// <remarks>
/// <para>按需求本窗口暂不接入真实校验后端：点「校验」只填入占位文本，使复制/粘贴/比对流程可用；
/// 接入后端时只需替换 <see cref="CreatePlaceholderValue"/>。</para>
/// The real checksum backends are intentionally not wired up yet (per request): "verify" fills placeholder
/// text so the copy/paste/compare flow stays usable. Wiring a backend only requires replacing
/// <see cref="CreatePlaceholderValue"/>.
/// </remarks>
public sealed partial class ChecksumViewModel : ObservableObject
{
    /// <summary>
    /// <para>已选择的文件名（完整路径）。</para>
    /// The selected file name (full path).
    /// </summary>
    [ObservableProperty] private string _filePath = string.Empty;

    /// <summary>MD5 复选框。 The MD5 checkbox.</summary>
    [ObservableProperty] private bool _md5Selected = true;

    /// <summary>SHA1 复选框。 The SHA1 checkbox.</summary>
    [ObservableProperty] private bool _sha1Selected = true;

    /// <summary>SHA256 复选框。 The SHA256 checkbox.</summary>
    [ObservableProperty] private bool _sha256Selected = true;

    /// <summary>CRC 复选框。 The CRC checkbox.</summary>
    [ObservableProperty] private bool _crcSelected = true;

    /// <summary>MD5 结果（只读）。 The MD5 result (read-only).</summary>
    [ObservableProperty] private string _md5Value = string.Empty;

    /// <summary>SHA1 结果（只读）。 The SHA1 result (read-only).</summary>
    [ObservableProperty] private string _sha1Value = string.Empty;

    /// <summary>SHA256 结果（只读）。 The SHA256 result (read-only).</summary>
    [ObservableProperty] private string _sha256Value = string.Empty;

    /// <summary>CRC 结果（只读）。 The CRC result (read-only).</summary>
    [ObservableProperty] private string _crcValue = string.Empty;

    /// <summary>「粘贴并校验」输入内容。 The "paste and verify" input text.</summary>
    [ObservableProperty] private string _pastedText = string.Empty;

    /// <summary>比对结果（驱动勾/叉指示）。 The comparison result (drives the tick/cross indicator).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatchResult))]
    [NotifyPropertyChangedFor(nameof(PasteIsCorrect))]
    private ChecksumMatchResult _matchResult;

    /// <summary>
    /// <para>是否已选择有效文件（决定复制与粘贴是否可用）。</para>
    /// Whether a valid file is selected (decides whether copy and paste are available).
    /// </summary>
    public bool HasFile => !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);

    /// <summary>MD5 复制是否可用。 Whether the MD5 copy button is enabled.</summary>
    public bool CanCopyMd5 => HasFile && Md5Value.Length > 0;

    /// <summary>SHA1 复制是否可用。 Whether the SHA1 copy button is enabled.</summary>
    public bool CanCopySha1 => HasFile && Sha1Value.Length > 0;

    /// <summary>SHA256 复制是否可用。 Whether the SHA256 copy button is enabled.</summary>
    public bool CanCopySha256 => HasFile && Sha256Value.Length > 0;

    /// <summary>CRC 复制是否可用。 Whether the CRC copy button is enabled.</summary>
    public bool CanCopyCrc => HasFile && CrcValue.Length > 0;

    /// <summary>
    /// <para>是否已有校验值（四种结果里至少有一个非空）。</para>
    /// Whether any checksum value exists (at least one of the four result rows is filled).
    /// </summary>
    public bool HasAnyValue => Md5Value.Length > 0 || Sha1Value.Length > 0 ||
        Sha256Value.Length > 0 || CrcValue.Length > 0;

    /// <summary>
    /// <para>粘贴是否可用：必须先选出文件并已得到至少一个校验值，否则「粘贴并校验」的按钮与输入框都禁用。</para>
    /// Whether paste is available: a file must be selected and at least one checksum value must exist,
    /// otherwise both the "paste and verify" button and its text box stay disabled.
    /// </summary>
    public bool CanPaste => HasFile && HasAnyValue;

    /// <summary>是否已有比对结论（驱动勾/叉显示）。 Whether a comparison verdict exists.</summary>
    public bool HasMatchResult => MatchResult != ChecksumMatchResult.None;

    /// <summary>比对是否一致（绿勾），否则显示红叉。 Whether the pasted text matched (green tick otherwise red cross).</summary>
    public bool PasteIsCorrect => MatchResult == ChecksumMatchResult.Correct;

    /// <summary>
    /// <para>选择/切换文件：重选文件时清空下方结果与粘贴内容，并禁用复制与粘贴。</para>
    /// Selects or switches the file: re-selecting clears the result rows and the pasted text and disables
    /// copy and paste.
    /// </summary>
    /// <param name="path"><para>文件路径。</para>The file path.</param>
    public void SetFilePath(string? path)
    {
        FilePath = path ?? string.Empty;
    }

    /// <summary>
    /// <para>执行「校验」：为勾选的算法填入结果，未勾选的清空。</para>
    /// Runs "verify": fills results for the checked algorithms and clears the unchecked ones.
    /// </summary>
    public void Verify()
    {
        Md5Value = Md5Selected ? CreatePlaceholderValue("MD5") : string.Empty;
        Sha1Value = Sha1Selected ? CreatePlaceholderValue("SHA1") : string.Empty;
        Sha256Value = Sha256Selected ? CreatePlaceholderValue("SHA256") : string.Empty;
        CrcValue = CrcSelected ? CreatePlaceholderValue("CRC") : string.Empty;
    }

    /// <summary>
    /// <para>清空全部结果、粘贴内容与比对结论（重选文件、清空文件名时调用）。</para>
    /// Clears every result row, the pasted text and the verdict (used when the file changes or is cleared).
    /// </summary>
    public void ClearResults()
    {
        Md5Value = string.Empty;
        Sha1Value = string.Empty;
        Sha256Value = string.Empty;
        CrcValue = string.Empty;
        PastedText = string.Empty;
        MatchResult = ChecksumMatchResult.None;
        NotifyDerived();
    }

    partial void OnFilePathChanged(string value) => ClearResults();

    partial void OnMd5ValueChanged(string value) => NotifyDerived();

    partial void OnSha1ValueChanged(string value) => NotifyDerived();

    partial void OnSha256ValueChanged(string value) => NotifyDerived();

    partial void OnCrcValueChanged(string value) => NotifyDerived();

    partial void OnPastedTextChanged(string value) => EvaluatePaste();

    /// <summary>
    /// <para>把粘贴内容与上方四种校验值比对：命中任意一项显示正确，否则显示错误；无文件或内容为空时不显示结论。</para>
    /// Compares the pasted text against the four computed values: a hit shows "correct", otherwise "wrong";
    /// nothing is shown without a file or with empty text.
    /// </summary>
    private void EvaluatePaste()
    {
        var text = PastedText.Trim();
        if (!HasFile || text.Length == 0)
        {
            MatchResult = ChecksumMatchResult.None;
            return;
        }

        var matched = Values().Any(value => value.Length > 0 &&
            string.Equals(value, text, StringComparison.OrdinalIgnoreCase));
        MatchResult = matched ? ChecksumMatchResult.Correct : ChecksumMatchResult.Wrong;
    }

    private IEnumerable<string> Values()
    {
        yield return Md5Value;
        yield return Sha1Value;
        yield return Sha256Value;
        yield return CrcValue;
    }

    /// <summary>
    /// <para>通知所有派生属性（可用性、比对结论）刷新。</para>
    /// Raises change notifications for every derived property (availability, verdict).
    /// </summary>
    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(HasAnyValue));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanCopyMd5));
        OnPropertyChanged(nameof(CanCopySha1));
        OnPropertyChanged(nameof(CanCopySha256));
        OnPropertyChanged(nameof(CanCopyCrc));
        OnPropertyChanged(nameof(HasMatchResult));
        OnPropertyChanged(nameof(PasteIsCorrect));
    }

    /// <summary>
    /// <para>占位校验值：按需求校验后端尚未接入，这里只返回可复制的占位文本，使复制/粘贴/比对流程可用。</para>
    /// Placeholder checksum value: the backend is not wired up yet (per request), so this returns copyable
    /// placeholder text that keeps the copy/paste/compare flow usable.
    /// </summary>
    /// <param name="algorithm"><para>算法名（MD5/SHA1/SHA256/CRC）。</para>The algorithm name.</param>
    private static string CreatePlaceholderValue(string algorithm) => $"<{algorithm} 待接入>";
}