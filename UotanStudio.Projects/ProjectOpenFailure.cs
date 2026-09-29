namespace UotanStudio.Services.Projects;

/// <summary>
/// Why the most recent <see cref="ProjectSession.OpenProjectFromPathAsync"/> attempt failed.
/// <para>最近一次 <see cref="ProjectSession.OpenProjectFromPathAsync"/> 尝试失败的原因。</para>
/// </summary>
public enum ProjectOpenFailure
{
    /// <summary>
    /// The open succeeded, or no open has been attempted yet.
    /// <para>打开成功，或尚未发起过打开操作。</para>
    /// </summary>
    None = 0,

    /// <summary>
    /// The requested path is blank or the solution file does not exist.
    /// <para>请求的路径为空，或解决方案文件不存在。</para>
    /// </summary>
    MissingFile,

    /// <summary>
    /// The solution file exists but cannot be read: invalid JSON, a format version this build cannot
    /// read, or a reference list that fails validation.
    /// <para>解决方案文件存在但无法读取：JSON 非法、格式版本超出本构建可读范围，或引用清单未通过校验。</para>
    /// </summary>
    InvalidSolution,

    /// <summary>
    /// A node project the solution registers as open exists but cannot be read, so the open was aborted
    /// before any session state was replaced.
    /// <para>解决方案登记的打开状态节点项目存在但无法读取，故在替换任何会话状态之前中止打开。</para>
    /// </summary>
    InvalidNodeProject
}