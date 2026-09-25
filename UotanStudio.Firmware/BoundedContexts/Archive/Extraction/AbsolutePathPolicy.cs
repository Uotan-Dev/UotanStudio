namespace UotanStudio.Firmware.BoundedContexts.Archive.Extraction;

/// <summary>
/// How the extractor treats archive entries whose stored path is absolute.
/// <para>解包器如何处理归档中以绝对路径存储的条目。</para>
/// </summary>
public enum AbsolutePathPolicy
{
    /// <summary>
    /// Extraction of an absolute entry aborts with an <c>InvalidDataException</c>.
    /// <para>遇到绝对路径条目时以 <c>InvalidDataException</c> 中止解包。</para>
    /// </summary>
    Reject = 0,

    /// <summary>
    /// The path root (drive letter or leading slash) is stripped and the remainder extracts
    /// relative to the destination; an entry that becomes empty after stripping also throws.
    /// <para>剥离路径根（盘符或起始斜杠）后按相对路径解到目标目录下；剥离后为空的条目同样抛出异常。</para>
    /// </summary>
    StripRoot,
}