namespace UotanStudio.Services.Patching;

/// <summary>Net file effect, compared with the original filesystem state.<para>与原始文件系统状态比较后的净文件效果。</para></summary>
public enum PatchChangeKind
{
    Added,
    Modified,
    Deleted,
    Unchanged
}

/// <summary>Publication state of the patch-owned effects of an execution.<para>一次执行中补丁相关效果的发布状态。</para></summary>
public enum PatchRunOutcome
{
    None,
    Staged,
    Previewed,
    Applied,
    AlreadyApplied,
    Discarded,
    Conflict,
    RolledBack,
    RecoveryRequired,
    NotApplicable,
    NoChanges
}

/// <summary>Immutable review data; hashes identify complete files, previews are deliberately bounded.<para>不可变审查数据；摘要标识完整文件，预览刻意设有边界。</para></summary>
/// <param name="Path">Path of the affected file.<para>受影响文件的路径。</para></param>
/// <param name="Kind">Net file effect.<para>净文件效果。</para></param>
/// <param name="BeforeBytes">Length before the change.<para>变更前的长度。</para></param>
/// <param name="AfterBytes">Length after the change.<para>变更后的长度。</para></param>
/// <param name="BeforeSha256">Digest of the original file.<para>原文件的摘要。</para></param>
/// <param name="AfterSha256">Digest of the resulting file.<para>结果文件的摘要。</para></param>
/// <param name="BeforePreview">Bounded original preview text.<para>有界的原内容预览。</para></param>
/// <param name="AfterPreview">Bounded resulting preview text.<para>有界的结果内容预览。</para></param>
public sealed record PatchChange(
    string Path, PatchChangeKind Kind, long BeforeBytes, long AfterBytes,
    string? BeforeSha256, string? AfterSha256, string? BeforePreview = null, string? AfterPreview = null)
{
    /// <summary>Direct composite invocations that staged this path; zero denotes the root graph.
    /// Includes no-op staging and does not imply that an invocation's bytes survived later edits.
    /// <para>直接暂存该路径的组合调用序号，零表示根图；包含空操作暂存，不代表字节在后续编辑后仍然保留。</para></summary>
    /// <value>Invocation numbers that staged this path.<para>已暂存该路径的调用序号。</para></value>
    public IReadOnlyList<int> StagingInvocations { get; init; } = [];

    /// <summary>Bounded chronological replacement-window snapshots, not the final net byte diff.<para>按时间排列的有界替换窗口快照，不是最终的净字节差异。</para></summary>
    /// <value>Window snapshots taken while staging.<para>暂存期间取得的窗口快照。</para></value>
    public IReadOnlyList<PatchBinaryPreview> BinaryPreviews { get; init; } = [];

    /// <summary>Whether additional replacement windows were omitted from this review.<para>本次审查是否省略了更多替换窗口。</para></summary>
    /// <value>True when windows were omitted.<para>有窗口被省略时为 true。</para></value>
    public bool BinaryPreviewTruncated { get; init; }
}

/// <summary>First bytes of one replacement window; invocation zero denotes the root workflow.<para>单个替换窗口的起始字节；零序号表示根工作流。</para></summary>
/// <param name="Offset">Start offset of the window.<para>窗口的起始偏移。</para></param>
/// <param name="Length">Byte length of the window.<para>窗口的字节长度。</para></param>
/// <param name="Invocation">Composite invocation number.<para>组合调用序号。</para></param>
/// <param name="BeforeHex">Original bytes as hexadecimal text.<para>原字节的十六进制文本。</para></param>
/// <param name="AfterHex">Replacement bytes as hexadecimal text.<para>替换字节的十六进制文本。</para></param>
public sealed record PatchBinaryPreview(long Offset, int Length, int Invocation, string BeforeHex, string AfterHex);

/// <summary>A file does not match the state against which the patch was prepared.<para>文件与补丁准备时所依据的状态不一致。</para></summary>
/// <param name="path">Path of the mismatching file.<para>状态不一致的文件路径。</para></param>
public sealed class PatchConflictException(string path) : IOException($"Patch input changed: {path}")
{
    /// <summary>Conflicting target or source.<para>冲突的目标或来源。</para></summary>
    /// <value>Path reported by the conflict.<para>冲突所报告的路径。</para></value>
    public string Path { get; } = path;
}

/// <summary>A compatible target has an ambiguous or unrecognized complete patch state.<para>兼容目标存在含糊或无法识别的完整补丁状态。</para></summary>
/// <param name="message">Description of the ambiguous state.<para>对歧义状态的描述。</para></param>
public sealed class PatchApplicabilityConflictException(string message) : IOException(message);

/// <summary>Explicit overwrite policy for a staged file operation.<para>暂存文件操作的显式覆盖策略。</para></summary>
public enum PatchFileMode
{
    Add,
    Replace,
    Upsert
}