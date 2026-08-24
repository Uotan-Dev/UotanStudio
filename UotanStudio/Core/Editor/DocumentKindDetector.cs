using System.IO;

namespace UotanStudio.Core.Editor;

/// <summary>
/// Editor document kind enum, classifying how an opened file is presented.
/// <para>编辑器文档类型枚举，对打开的文件进行分类以选择展示视图。</para>
/// </summary>
public enum EditorDocumentKind
{
    /// <summary>Text document. 文本文档。</summary>
    Text,

    /// <summary>Binary document. 二进制文档。</summary>
    Hex,

    /// <summary>Structured document (JSON/XML). 结构化文档（JSON/XML）。</summary>
    Structured
}

/// <summary>
/// Detects the editor document kind from a file path by extension.
/// <para>根据文件扩展名识别编辑器的文档类型。</para>
/// </summary>
public static class DocumentKindDetector
{
    /// <summary>
    /// Returns the document kind for the given path.
    /// <para>返回指定路径对应的文档类型。</para>
    /// </summary>
    /// <param name="path"><para>文件路径。</para>The file path.</param>
    /// <returns><para>文档类型。</para>The document kind.</returns>
    public static EditorDocumentKind Detect(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".bin" or ".img" or ".cpio" or ".payload" or ".partition" or ".gz" or ".lz4" or ".zst" => EditorDocumentKind.Hex,
            ".json" or ".xml" or ".uotproj" or ".uotflow" => EditorDocumentKind.Structured,
            _ => EditorDocumentKind.Text
        };
    }
}