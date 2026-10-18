namespace UotanStudio.Services.Projects;

/// <summary>Immutable view of a session's committed directory context. The sub-project list is a snapshot copy, so later context mutations do not change an already-captured view.
/// <para>会话已提交目录上下文的不可变视图。子项目列表是快照副本，因此上下文的后续修改不会影响已捕获的 视图。</para></summary>
/// <param name="SolutionPath">Absolute path of the opened <c>.uotan</c> solution file, or <see langword="null"/> when no solution is open.
/// <para>已打开 <c>.uotan</c> 解决方案文件的绝对路径；未打开解决方案时为 <see langword="null"/>。</para></param>
/// <param name="RootDirectory">Absolute data root directory (sharing the solution file's base name) that stored project paths resolve against, or <see langword="null"/> when no solution is open.
/// <para>持久化项目路径所解析的数据根目录绝对路径（与解决方案文件同名）；未打开解决方案时为 <see langword="null"/>。</para></param>
/// <param name="ActiveSubProjectName">Name of the currently active sub-project, or <see langword="null"/> when the solution has no active selection.
/// <para>当前活动子项目的名称；解决方案没有选中项时为 <see langword="null"/>。</para></param>
/// <param name="SubProjects">Snapshot of the per-sub-project directory contexts at capture time; empty when the session holds no sub-projects.
/// <para>捕获时刻各子项目目录上下文的快照；会话不含子项目时为 空列表。</para></param>
public sealed record ProjectSessionState(string? SolutionPath, string? RootDirectory,
    string? ActiveSubProjectName, IReadOnlyList<SubProjectContext> SubProjects);