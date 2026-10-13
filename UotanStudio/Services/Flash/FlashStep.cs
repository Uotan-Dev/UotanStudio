namespace UotanStudio.Services.Flash;

/// <summary>A single validated, ordered operation in an imported flash plan.
/// <para>导入刷写计划中经过校验的单个有序操作。</para></summary>
/// <param name="Command">The flash command text of the step. <para>该步骤的刷写命令文本。</para></param>
/// <param name="Partition">The target partition name. <para>目标分区名。</para></param>
/// <param name="ImagePath">Optional local image path for the step. <para>该步骤使用的可选本地镜像路径。</para></param>
/// <param name="ImageLength">The image byte length when known. <para>已知时的镜像字节长度。</para></param>
public sealed record FlashStep(string Command, string Partition, string ImagePath = "", long ImageLength = 0);