using Avalonia.Media;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>资源管理器图标配色：每个文件家族一个固定色，参考 VS / VS Code 解决方案管理器的着色方式，
/// 由文件树节点与右键菜单共用。颜色取中等明度，在浅色与深色主题下都可辨识，因此不随后续主题切换重算，
/// 与 VS 保持一致；这些颜色只用于展示，不参与任何格式或归属判定。</para>
/// Explorer icon palette: one fixed colour per file family, following the way VS / VS Code colour their
/// explorer icons, shared by the file-tree nodes and the context menu. The colours are mid-tone so they
/// stay legible in both the light and dark themes and therefore do not change with the theme, matching VS.
/// Display-only; no format or ownership decision uses them.
/// </summary>
public static class ExplorerIconPalette
{
    /// <summary>目录，包括普通目录与项目文件夹。 Folders, both plain and project folders.</summary>
    public static IBrush Folder { get; } = Create("#D9A03C");

    /// <summary>分区镜像与裸块数据（.img/.bin/.dat 等）。 Partition images and raw block data (.img/.bin/.dat).</summary>
    public static IBrush Image { get; } = Create("#4C8DF6");

    /// <summary>固件载荷：原生二进制与厂商整包。 Firmware payloads: native binaries and vendor bundles.</summary>
    public static IBrush Firmware { get; } = Create("#F0813C");

    /// <summary>设备树源文件与编译产物。 Device-tree sources and their compiled artifacts.</summary>
    public static IBrush DeviceTree { get; } = Create("#2FB89A");

    /// <summary>Android 安装包。 Android packages.</summary>
    public static IBrush Android { get; } = Create("#3DDC84");

    /// <summary>源码与字节码（.dex/.jar/.java 等）。 Source and bytecode (.dex/.jar/.java).</summary>
    public static IBrush Code { get; } = Create("#A98BF5");

    /// <summary>资源表（.arsc）。 Resource tables (.arsc).</summary>
    public static IBrush Resource { get; } = Create("#2BB6C9");

    /// <summary>XML 与 Manifest 清单。 XML and Manifest documents.</summary>
    public static IBrush Markup { get; } = Create("#8CBF3F");

    /// <summary>JSON 数据文件。 JSON data files.</summary>
    public static IBrush Json { get; } = Create("#CBCB41");

    /// <summary>脚本文件。 Script files.</summary>
    public static IBrush Script { get; } = Create("#6FBF73");

    /// <summary>归档容器与压缩流。 Archive containers and compressed streams.</summary>
    public static IBrush Archive { get; } = Create("#BFA07A");

    /// <summary>签名证书与密钥库。 Signing certificates and keystores.</summary>
    public static IBrush Security { get; } = Create("#E06C9F");

    /// <summary>图片、音频与视频资源。 Image, audio and video assets.</summary>
    public static IBrush Media { get; } = Create("#A074C4");

    /// <summary>文本、配置与文档（.txt/.log/.prop/.md/.csv/.docx 等）。 Text, configuration and documents.</summary>
    public static IBrush Document { get; } = Create("#8A93A5");

    /// <summary>PDF 文档。 PDF documents.</summary>
    public static IBrush Pdf { get; } = Create("#E05252");

    private static IBrush Create(string hexColor) => new SolidColorBrush(Color.Parse(hexColor));
}