using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;
using UotanStudio.Services.Checksum;

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
public sealed partial class ChecksumViewModel : ObservableObject
{
    /// <summary>
    /// <para>当前校验任务的取消源；文件切换清空结果时取消进行中的校验，旧结果不得写回界面。</para>
    /// The cancellation source of the running verification; switching files cancels it so stale
    /// results never reach the UI.
    /// </summary>
    private CancellationTokenSource? _verifyCts;

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

    /// <summary>
    /// <para>是否有校验正在进行（期间禁用校验按钮）。</para>
    /// Whether a verification is running (the verify button stays disabled meanwhile).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanVerify))]
    private bool _isBusy;

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

    /// <summary>
    /// <para>「校验」是否可用：必须已选择文件且当前没有校验在跑。</para>
    /// Whether "verify" is available: a file must be selected and no verification may be running.
    /// </summary>
    public bool CanVerify => HasFile && !IsBusy;

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
    /// <para>执行「校验」：单次流式读取同时计算勾选的算法，未勾选的行清空；校验期间按钮禁用。</para>
    /// Runs "verify": one streaming pass computes every checked algorithm; unchecked rows are cleared.
    /// The verify button stays disabled while a verification runs.
    /// </summary>
    public async Task VerifyAsync()
    {
        if (!HasFile || IsBusy)
        {
            return;
        }

        // 每次校验使用独立取消源：文件切换清空结果时旧任务被取消，其结果不会写回界面。
        // Each run gets its own cancellation source: clearing results on a file switch cancels the
        // previous run, whose results then never reach the UI.
        var cts = new CancellationTokenSource();
        _verifyCts = cts;
        var path = FilePath;
        var algorithms = new ChecksumAlgorithms(Md5Selected, Sha1Selected, Sha256Selected, CrcSelected);
        IsBusy = true;
        try
        {
            if (!algorithms.Any)
            {
                // 未勾选任何算法：只清空结果，不读文件。
                // No algorithm selected: just clear the rows without reading the file.
                Md5Value = string.Empty;
                Sha1Value = string.Empty;
                Sha256Value = string.Empty;
                CrcValue = string.Empty;
                return;
            }

            var result = await ChecksumService.ComputeAsync(path, algorithms, cts.Token);
            if (cts.IsCancellationRequested)
            {
                // 校验期间文件被切换：丢弃结果，保持清空状态。
                // The file was switched mid-run: discard the results and keep the cleared state.
                return;
            }

            Md5Value = Md5Selected ? result.Md5 : string.Empty;
            Sha1Value = Sha1Selected ? result.Sha1 : string.Empty;
            Sha256Value = Sha256Selected ? result.Sha256 : string.Empty;
            CrcValue = CrcSelected ? result.Crc32 : string.Empty;
        }
        catch (OperationCanceledException)
        {
            // 取消：丢弃部分结果，保持当前状态。
            // Cancelled: discard the partial results and keep the current state.
        }
        catch (Exception ex)
        {
            // 文件读取失败（被删除、被占用等）：清空结果并记录日志。
            // Reading failed (file deleted, locked, ...): clear the rows and log the error.
            LogService.Instance.Write(LogLevel.Error, "Checksum", $"Verify failed: {ex.Message}");
            ClearResults();
        }
        finally
        {
            // 仅当本次校验仍是当前校验时才复位 busy；被取代的旧任务不得干扰新一轮的状态。
            // Reset busy only while this run is still the current one; a superseded run must not
            // clear the newer run's busy state.
            if (ReferenceEquals(_verifyCts, cts))
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// <para>清空全部结果、粘贴内容与比对结论（重选文件、清空文件名、校验失败时调用），并取消进行中的校验。</para>
    /// Clears every result row, the pasted text and the verdict (on file switch, cleared file name or
    /// verification failure), and cancels any running verification.
    /// </summary>
    public void ClearResults()
    {
        _verifyCts?.Cancel();
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
        OnPropertyChanged(nameof(CanVerify));
        OnPropertyChanged(nameof(HasAnyValue));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanCopyMd5));
        OnPropertyChanged(nameof(CanCopySha1));
        OnPropertyChanged(nameof(CanCopySha256));
        OnPropertyChanged(nameof(CanCopyCrc));
        OnPropertyChanged(nameof(HasMatchResult));
        OnPropertyChanged(nameof(PasteIsCorrect));
    }
}