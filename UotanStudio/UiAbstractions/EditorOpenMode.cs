namespace UotanStudio.UiAbstractions;

/// <summary>
/// The mode in which a document is opened.
/// <para>文档打开方式。</para>
/// </summary>
public enum EditorOpenMode
{
    /// <summary>
    /// Automatic: resolve via the extension whitelist, falling back to content sniffing when uncertain.
    /// <para>自动：先按扩展名白名单定向，不确定时回落到内容探测。</para>
    /// </summary>
    Auto,

    /// <summary>
    /// Force open in the text editor (with syntax highlighting).
    /// <para>强制以文本编辑器打开（带语法高亮）。</para>
    /// </summary>
    Text,

    /// <summary>
    /// Force open in the hex editor.
    /// <para>强制以 16 进制编辑器打开。</para>
    /// </summary>
    Hex,

    /// <summary>
    /// Open in the device-tree dual-pane editor (DTB/DTBO).
    /// <para>以设备树双栏编辑器打开（DTB/DTBO）。</para>
    /// </summary>
    Dtb,

    /// <summary>
    /// Open in the DEX dual-pane editor (structure tree + disassembly).
    /// <para>以 DEX 双栏编辑器打开（结构树 + 反汇编）。</para>
    /// </summary>
    Dex,

    /// <summary>
    /// Open in the Manifest editor (AXML: AndroidManifest.xml and other binary XML files).
    /// <para>以 Manifest 编辑器打开（AXML：AndroidManifest.xml 等二进制 XML）。</para>
    /// </summary>
    Axml,

    /// <summary>
    /// Open in the ARSC resource-table editor (resources.arsc).
    /// <para>以 ARSC 资源表编辑器打开（resources.arsc）。</para>
    /// </summary>
    Arsc,

    /// <summary>
    /// Open in the APK metadata editor (Android Package; shows icon/package/version/permissions/ABIs).
    /// <para>以 APK 元数据编辑器打开（Android Package，展示图标/包名/版本/权限/ABI）。</para>
    /// </summary>
    Apk,

    /// <summary>
    /// Open in the firmware metadata panel (format name/summary/metadata/entries, driven by the
    /// format-handler registry).
    /// <para>以固件元数据面板打开（格式名/摘要/元数据/条目，基于格式处理器注册表）。</para>
    /// </summary>
    Firmware,

    /// <summary>
    /// Open in the file-manager document editor (shows the internal directory tree of filesystem
    /// images such as ext4/EROFS/F2FS).
    /// <para>以文件管理器文档编辑器打开（展示 ext4/EROFS/F2FS 等文件系统镜像的内部目录树）。</para>
    /// </summary>
    FileSystem,

    /// <summary>
    /// Open in the partition-table editor (Android LP super metadata or Rockchip parameter partition
    /// tables, showing name/start/size/type with edit-and-save support).
    /// <para>以分区表编辑器打开（Android LP super 元数据或 Rockchip parameter 分区表，
    /// 展示分区名/起始/大小/类型并支持编辑保存）。</para>
    /// </summary>
    Partition,

    /// <summary>
    /// Open as a read-only log document.
    /// <para>以只读日志文档打开。</para>
    /// </summary>
    Log,

    /// <summary>Open validated ELF metadata in a read-only information panel.</summary>
    Elf,

    /// <summary>Open the Qualcomm image analysis workspace explicitly.
    /// <para>显式打开 Qualcomm 镜像分析工作区。</para></summary>
    Qualcomm,

    /// <summary>Open the Qualcomm QCN NV backup in the structured QCN editor.
    /// <para>以结构化 QCN 编辑器打开 Qualcomm QCN NV 备份。</para></summary>
    Qcn,

    /// <summary>Open an RKDevTool download-list configuration in the read-only configuration browser.
    /// <para>以只读配置浏览视图打开 RKDevTool 下载列表配置。</para></summary>
    RockchipConfig
}