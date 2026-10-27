using Avalonia.Media;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// Explorer icon palette: one fixed colour per file family, following the way VS / VS Code colour their
/// explorer icons, shared by the file-tree nodes and the context menu. The colours are mid-tone so they
/// stay legible in both the light and dark themes and therefore do not change with the theme, matching VS.
/// Display-only; no format or ownership decision uses them.
/// <para>资源管理器图标配色：每个文件家族一个固定色，参考 VS / VS Code 解决方案管理器的着色方式，
/// 由文件树节点与右键菜单共用。颜色取中等明度，在浅色与深色主题下都可辨识，因此不随后续主题切换重算，
/// 与 VS 保持一致；这些颜色只用于展示，不参与任何格式或归属判定。</para>
/// </summary>
public static class ExplorerIconPalette
{
    /// <summary>
    /// Folders, both plain and project folders.
    /// <para>目录，包括普通目录与项目文件夹。</para>
    /// </summary>
    public static IBrush Folder { get; } = Create("#D9A03C");

    /// <summary>
    /// Partition images and raw block data (.img/.bin/.dat).
    /// <para>分区镜像与裸块数据（.img/.bin/.dat 等）。</para>
    /// </summary>
    public static IBrush Image { get; } = Create("#4C8DF6");

    /// <summary>
    /// Firmware payloads: native binaries and vendor bundles.
    /// <para>固件载荷：原生二进制与厂商整包。</para>
    /// </summary>
    public static IBrush Firmware { get; } = Create("#F0813C");

    /// <summary>
    /// Device-tree sources and their compiled artifacts.
    /// <para>设备树源文件与编译产物。</para>
    /// </summary>
    public static IBrush DeviceTree { get; } = Create("#2FB89A");

    /// <summary>
    /// Android packages.
    /// <para>Android 安装包。</para>
    /// </summary>
    public static IBrush Android { get; } = Create("#3DDC84");

    /// <summary>
    /// Source and bytecode (.dex/.jar/.java).
    /// <para>源码与字节码（.dex/.jar/.java 等）。</para>
    /// </summary>
    public static IBrush Code { get; } = Create("#A98BF5");

    /// <summary>
    /// Resource tables (.arsc).
    /// <para>资源表（.arsc）。</para>
    /// </summary>
    public static IBrush Resource { get; } = Create("#2BB6C9");

    /// <summary>
    /// XML and Manifest documents.
    /// <para>XML 与 Manifest 清单。</para>
    /// </summary>
    public static IBrush Markup { get; } = Create("#8CBF3F");

    /// <summary>
    /// JSON data files.
    /// <para>JSON 数据文件。</para>
    /// </summary>
    public static IBrush Json { get; } = Create("#CBCB41");

    /// <summary>
    /// Script files.
    /// <para>脚本文件。</para>
    /// </summary>
    public static IBrush Script { get; } = Create("#6FBF73");

    /// <summary>
    /// Archive containers and compressed streams.
    /// <para>归档容器与压缩流。</para>
    /// </summary>
    public static IBrush Archive { get; } = Create("#BFA07A");

    /// <summary>
    /// Signing certificates and keystores.
    /// <para>签名证书与密钥库。</para>
    /// </summary>
    public static IBrush Security { get; } = Create("#E06C9F");

    /// <summary>
    /// Image, audio and video assets.
    /// <para>图片、音频与视频资源。</para>
    /// </summary>
    public static IBrush Media { get; } = Create("#A074C4");

    /// <summary>
    /// Text, configuration and documents.
    /// <para>文本、配置与文档（.txt/.log/.prop/.md/.csv/.docx 等）。</para>
    /// </summary>
    public static IBrush Document { get; } = Create("#8A93A5");

    /// <summary>
    /// PDF documents.
    /// <para>PDF 文档。</para>
    /// </summary>
    public static IBrush Pdf { get; } = Create("#E05252");

    private static IBrush Create(string hexColor) => new SolidColorBrush(Color.Parse(hexColor));
}