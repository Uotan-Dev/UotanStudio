using System.Globalization;
using UotanStudio.Services;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Display formatter for port runtime values: converts the value types written by the execution engine into
/// bounded single-line preview text; binary buffers are never expanded as text.
/// <para>端口运行值的展示格式化器：把执行引擎写入的各种值类型转成有界的单行预览文本，二进制缓冲永不按文本展开。</para>
/// </summary>
public static class NodeValueFormatter
{
    /// <summary>
    /// Formats one port value; unknown types yield an empty string and output beyond 800 characters is
    /// truncated with an ellipsis.
    /// <para>格式化一个端口值；未知类型返回空字符串，输出超过 800 字符时截断并附加省略号。</para>
    /// </summary>
    /// <param name="data">The runtime value carried by the port. <para>端口承载的运行值。</para></param>
    /// <param name="valueResourcePrefix">
    /// Optional localization prefix applied to string values only.
    /// <para>可选的取值本地化前缀，仅作用于字符串值。</para>
    /// </param>
    /// <returns>The bounded preview text. <para>有界的预览文本。</para></returns>
    public static string Describe(object? data, string? valueResourcePrefix)
    {
        var text = data switch
        {
            null => string.Empty,
            string value => valueResourcePrefix is null ? value : LanguageService.Instance.GetString(valueResourcePrefix + value),
            bool value => LanguageService.Instance.GetString(value ? "Node_Conn_True" : "Node_Conn_False"),
            PatchWorkspace workspace => workspace.Root,
            FileArtifact artifact => artifact.Path,
            StreamArtifact artifact => DescribeArtifact(artifact),
            PatchAsset asset => asset.Name,
            PatchImageEdits edits => string.Join(Environment.NewLine,
                edits.Items.Take(8).Select(edit => edit.Path[..Math.Min(edit.Path.Length, 160)])),
            IReadOnlyList<string> values => string.Join(Environment.NewLine, values.Take(8).Select(value => value[..Math.Min(value.Length, 160)])),
            byte[] bytes => $"{bytes.Length:N0} B",
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => string.Empty
        };
        return text.Length <= 800 ? text : text[..800] + "…";
    }

    private static string DescribeArtifact(StreamArtifact artifact)
    {
        var name = artifact.DisplayName ?? string.Empty;
        return artifact.Length is { } length ? $"{name} ({length:N0} B)" : name;
    }
}