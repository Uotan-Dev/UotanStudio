namespace UotanStudio.Messages;

/// <summary>
/// Firmware unpack request message, asking to unpack a component file into a directory.
/// <para>固件解包请求消息，请求将部件文件解包到指定目录。</para>
/// </summary>
/// <param name="componentPath">Path of the component file to unpack. <para>待解包部件文件的路径。</para></param>
/// <param name="outputDirectory">Directory that receives the unpacked contents. <para>接收解包内容的目录。</para></param>
public class UnpackRequestMessage(string componentPath, string outputDirectory)
{
    /// <summary>
    /// Gets the component file path.
    /// <para>部件文件路径。</para>
    /// </summary>
    /// <value>The path of the file to unpack. <para>待解包文件的路径。</para></value>
    public string ComponentPath { get; } = componentPath;

    /// <summary>
    /// Gets the output directory.
    /// <para>输出目录。</para>
    /// </summary>
    /// <value>The directory the contents are written to. <para>内容写入的目标目录。</para></value>
    public string OutputDirectory { get; } = outputDirectory;
}