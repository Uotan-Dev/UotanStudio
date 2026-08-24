namespace UotanStudio.Messages;

/// <summary>
/// Firmware unpack request message, asking to unpack a component file into a directory.
/// <para>固件解包请求消息，请求将部件文件解包到指定目录。</para>
/// </summary>
public class UnpackRequestMessage(string componentPath, string outputDirectory)
{
    /// <summary>
    /// Gets the component file path.
    /// <para>部件文件路径。</para>
    /// </summary>
    public string ComponentPath { get; } = componentPath;

    /// <summary>
    /// Gets the output directory.
    /// <para>输出目录。</para>
    /// </summary>
    public string OutputDirectory { get; } = outputDirectory;
}