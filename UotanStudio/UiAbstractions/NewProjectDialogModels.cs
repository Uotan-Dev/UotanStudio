using UotanStudio.Firmware.Application.Containers;

namespace UotanStudio.UiAbstractions;

/// <summary>
/// Semantic mode of the new-project form: the File-menu entry creates a new solution; the entry on an
/// open solution creates a sub-project.
/// <para>新建表单的语义模式：顶部菜单入口创建新解决方案；已打开解决方案的入口创建子项目。</para>
/// </summary>
public enum NewProjectWindowMode
{
    /// <summary>New solution (first sub-project shares its name). <para>新建解决方案（首个子项目与解决方案同名）。</para></summary>
    Solution = 0,

    /// <summary>Add a sub-project to the current solution. <para>向当前解决方案添加子项目。</para></summary>
    SubProject
}

/// <summary>
/// Submitted new-project form result; non-null only after Create is clicked with valid input.
/// When <see cref="ContainerKind"/> is <see cref="ContainerKind.None"/>, <see cref="SelectedImagePaths"/>
/// carries plain multi-selected images; for containers, <see cref="ContainerPath"/> points at the
/// container file and <see cref="SelectedZipMembers"/> / <see cref="SelectedPartitions"/> carry the
/// selected inner entries.
/// <para>新建项目表单的提交结果；仅在点击创建且校验通过后非空。
/// <see cref="ContainerKind"/> 为 <see cref="ContainerKind.None"/> 时 <see cref="SelectedImagePaths"/>
/// 承载普通多选镜像；为容器时 <see cref="ContainerPath"/> 指向容器文件，
/// <see cref="SelectedZipMembers"/> / <see cref="SelectedPartitions"/> 承载勾选的内部条目。</para>
/// </summary>
/// <param name="ProjectName">Solution or sub-project name. <para>解决方案/子项目名称。</para></param>
/// <param name="Location">
/// Combined full directory (<c>base/name</c>); must be missing or empty.
/// <para>组合后的完整目录（<c>基础目录/名称</c>），必须不存在或为空。</para>
/// </param>
/// <param name="ContainerKind">Single-file container kind (none / super / payload / zip). <para>单文件容器类型（无/ super / payload / zip）。</para></param>
/// <param name="ContainerPath">Container file path; null when not a container. <para>容器文件路径；非容器时为 null。</para></param>
/// <param name="SelectedImagePaths">Full paths of plain multi-selected images. <para>普通多选镜像的完整路径。</para></param>
/// <param name="SelectedZipMembers">Selected ZIP member full paths. <para>ZIP 容器中勾选的条目完整路径。</para></param>
/// <param name="SelectedPartitions">
/// Selected partition names (super logical partitions or payload partitions, per <see cref="ContainerKind"/>).
/// <para>勾选的分区名（super 逻辑分区或 payload 分区，按 <see cref="ContainerKind"/> 区分）。</para>
/// </param>
/// <param name="UnpackImages">Whether to unpack images. <para>是否解压镜像。</para></param>
/// <param name="UnpackFully">Whether to unpack extracted supported imgs into same-name folders. <para>是否把解出的受支持 img 继续解压为同名文件夹。</para></param>
/// <param name="MergeMyPartitions">Whether my_* partition trees are merged into system after unpacking. <para>解包完成后是否把 my_* 分区树合并进 system。</para></param>
public sealed record NewProjectFormResult(
    string ProjectName,
    string Location,
    ContainerKind ContainerKind,
    string? ContainerPath,
    string[] SelectedImagePaths,
    string[] SelectedZipMembers,
    string[] SelectedPartitions,
    bool UnpackImages,
    bool UnpackFully,
    bool CopyOriginal = true,
    string? RemoteUrl = null,
    bool MergeMyPartitions = false);