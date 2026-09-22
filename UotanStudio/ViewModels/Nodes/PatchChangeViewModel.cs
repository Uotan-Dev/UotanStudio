using UotanStudio.Services;
using UotanStudio.Services.Patching;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Localized review surface for immutable patch execution evidence.</summary>
public sealed record PatchChangeViewModel(PatchChange Change)
{
    /// <summary>Target path retained independently from its localized change description.</summary>
    public string Path => Change.Path;

    /// <summary>Localized net operation.</summary>
    public string Kind => LanguageService.Instance.GetString("Node_PatchChange_" + Change.Kind);

    /// <summary>Staging participants, independent of net effect and publication.</summary>
    public string StagedBy => LanguageService.Instance.GetString("Node_Patch_StagedBy",
        string.Join(", ", Change.StagingInvocations.Select(invocation => invocation == 0
            ? LanguageService.Instance.GetString("Node_Patch_CompositeRoot") : "#" + invocation)));

    /// <summary>Original SHA-256, or the localized absent marker.</summary>
    public string BeforeHash => Change.BeforeSha256 ?? LanguageService.Instance.GetString("Node_Patch_Absent");

    /// <summary>Result SHA-256, or the localized absent marker.</summary>
    public string AfterHash => Change.AfterSha256 ?? LanguageService.Instance.GetString("Node_Patch_Absent");

    /// <summary>Old text prefix, when this is a text change.</summary>
    public string BeforeText => Change.BeforePreview ?? string.Empty;

    /// <summary>New text prefix, when this is a text change.</summary>
    public string AfterText => Change.AfterPreview ?? string.Empty;

    /// <summary>Whether operation byte snapshots or omitted-window evidence are available.</summary>
    public bool HasBinaryPreview => Change.BinaryPreviews.Count > 0 || Change.BinaryPreviewTruncated;

    /// <summary>Bounded localized replacement-window evidence, independent from the final file hashes.</summary>
    public string BinaryPreview => string.Join(Environment.NewLine, Change.BinaryPreviews.Select(preview =>
        LanguageService.Instance.GetString("Node_Patch_BinaryPreviewRow", "0x" + preview.Offset.ToString("X"), preview.Length,
            preview.Invocation == 0 ? LanguageService.Instance.GetString("Node_Patch_CompositeRoot") : "#" + preview.Invocation,
            preview.BeforeHex, preview.AfterHex, preview.Length > 64 ? LanguageService.Instance.GetString("Node_Patch_BytesTruncated") : string.Empty)))
        + (Change.BinaryPreviewTruncated ? Environment.NewLine + LanguageService.Instance.GetString("Node_Patch_WindowsTruncated") : string.Empty);
}