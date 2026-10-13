using UotanStudio.Services;
using UotanStudio.Services.Patching;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Localized review surface for immutable patch execution evidence.<para>面向不可变补丁执行证据的本地化审阅呈现。</para></summary>
/// <param name="Change">The immutable patch change supplying the evidence.<para>提供证据的不可变补丁变更。</para></param>
public sealed record PatchChangeViewModel(PatchChange Change)
{
    /// <summary>Target path retained independently from its localized change description.<para>目标路径独立保留，不随本地化变更描述而变。</para></summary>
    /// <value>The retained target path.<para>保留的目标路径。</para></value>
    public string Path => Change.Path;

    /// <summary>Localized net operation.<para>本地化的净操作。</para></summary>
    /// <value>The localized label for the net operation kind.<para>净操作类型的本地化标签。</para></value>
    public string Kind => LanguageService.Instance.GetString("Node_PatchChange_" + Change.Kind);

    /// <summary>Staging participants, independent of net effect and publication.<para>暂存参与者，与净效果和发布无关。</para></summary>
    /// <value>The localized list of invocations that staged the change.<para>已暂存该变更的调用的本地化列表。</para></value>
    public string StagedBy => LanguageService.Instance.GetString("Node_Patch_StagedBy",
        string.Join(", ", Change.StagingInvocations.Select(invocation => invocation == 0
            ? LanguageService.Instance.GetString("Node_Patch_CompositeRoot") : "#" + invocation)));

    /// <summary>Original SHA-256, or the localized absent marker.<para>原始 SHA-256，或本地化的缺失标记。</para></summary>
    /// <value>The original hash text or the absent marker.<para>原始哈希文本或缺失标记。</para></value>
    public string BeforeHash => Change.BeforeSha256 ?? LanguageService.Instance.GetString("Node_Patch_Absent");

    /// <summary>Result SHA-256, or the localized absent marker.<para>结果 SHA-256，或本地化的缺失标记。</para></summary>
    /// <value>The resulting hash text or the absent marker.<para>结果哈希文本或缺失标记。</para></value>
    public string AfterHash => Change.AfterSha256 ?? LanguageService.Instance.GetString("Node_Patch_Absent");

    /// <summary>Old text prefix, when this is a text change.<para>文本变更时的旧文本前缀。</para></summary>
    /// <value>The old text prefix, or empty for non-text changes.<para>旧文本前缀，非文本变更时为空。</para></value>
    public string BeforeText => Change.BeforePreview ?? string.Empty;

    /// <summary>New text prefix, when this is a text change.<para>文本变更时的新文本前缀。</para></summary>
    /// <value>The new text prefix, or empty for non-text changes.<para>新文本前缀，非文本变更时为空。</para></value>
    public string AfterText => Change.AfterPreview ?? string.Empty;

    /// <summary>Whether operation byte snapshots or omitted-window evidence are available.<para>是否存在操作字节快照或被省略窗口的证据。</para></summary>
    /// <value>True when binary evidence exists.<para>存在二进制证据时为 True。</para></value>
    public bool HasBinaryPreview => Change.BinaryPreviews.Count > 0 || Change.BinaryPreviewTruncated;

    /// <summary>Bounded localized replacement-window evidence, independent from the final file hashes.<para>有界的本地化替换窗口证据，与最终文件哈希无关。</para></summary>
    /// <value>The formatted binary preview lines.<para>格式化后的二进制预览行。</para></value>
    public string BinaryPreview => string.Join(Environment.NewLine, Change.BinaryPreviews.Select(preview =>
        LanguageService.Instance.GetString("Node_Patch_BinaryPreviewRow", "0x" + preview.Offset.ToString("X"), preview.Length,
            preview.Invocation == 0 ? LanguageService.Instance.GetString("Node_Patch_CompositeRoot") : "#" + preview.Invocation,
            preview.BeforeHex, preview.AfterHex, preview.Length > 64 ? LanguageService.Instance.GetString("Node_Patch_BytesTruncated") : string.Empty)))
        + (Change.BinaryPreviewTruncated ? Environment.NewLine + LanguageService.Instance.GetString("Node_Patch_WindowsTruncated") : string.Empty);
}