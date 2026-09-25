namespace UotanStudio.Services.Projects;

/// <summary>
/// A file-backed workflow value. The path is explicit and never inferred from ordinary text.
/// <para>以文件为载体的工作流值。路径是显式给出的，绝不从普通文本推断。</para>
/// </summary>
/// <param name="Path">
/// Path to the file the artifact refers to, as recorded by the node that produced it.
/// <para>工件所指向文件的路径，由产生它的节点原样记录。</para>
/// </param>
public sealed record FileArtifact(string Path);