namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

/// <summary>Known all-in-one download-agent directory layouts. <para>已知的 all-in-one 下载代理目录布局。</para></summary>
public enum MtkDaDirectoryLayout
{
    /// <summary>
    /// Validate both layouts and reject ambiguous input.
    /// <para>同时校验两种布局，并拒绝存在歧义的输入。</para>
    /// </summary>
    Auto,
    /// <summary>A 216-byte entry with a 16-byte fixed header. <para>216 字节条目，配 16 字节固定头部。</para></summary>
    Legacy,
    /// <summary>
    /// A 220-byte entry with a 20-byte fixed header including software version.
    /// <para>220 字节条目，配 20 字节固定头部，其中包含软件版本。</para>
    /// </summary>
    Modern
}

/// <summary>
/// A loader region's on-disk fields, without implied execution or signature validation.
/// <para>加载器区域在磁盘上的字段，不隐含执行语义或签名校验。</para>
/// </summary>
/// <param name="FileOffset">The absolute byte offset in the container. <para>在容器内的绝对字节偏移。</para></param>
/// <param name="Length">The region byte count including any signature suffix. <para>区域字节数，包含签名后缀（若有）。</para></param>
/// <param name="LoadAddress">The unmodified target load address. <para>未经改写的目标加载地址。</para></param>
/// <param name="StartOffset">
/// The unmodified fourth region field; execution semantics are not inferred.
/// <para>未经改写的第四个区域字段；不推断其执行语义。</para>
/// </param>
/// <param name="SignatureLength">The declared signature suffix byte count. <para>声明的签名字节后缀字节数。</para></param>
public sealed record MtkDaRegion(uint FileOffset, uint Length, uint LoadAddress, uint StartOffset, uint SignatureLength);

/// <summary>
/// One explicitly identified download-agent directory entry.
/// <para>一个显式识别的下载代理目录条目。</para>
/// </summary>
/// <param name="HardwareCode">The target hardware code. <para>目标硬件代号。</para></param>
/// <param name="HardwareSubCode">The hardware subcode. <para>硬件子代号。</para></param>
/// <param name="HardwareVersion">The hardware version. <para>硬件版本。</para></param>
/// <param name="SoftwareVersion">
/// The software version, absent in the legacy layout.
/// <para>软件版本；在 legacy 布局中不存在。</para>
/// </param>
/// <param name="PageSize">
/// The unmodified page-size field; zero is preserved.
/// <para>未经改写的页大小字段；零值原样保留。</para>
/// </param>
/// <param name="EntryRegionIndex">
/// The entry-region index supplied by the container.
/// <para>容器提供的条目-区域索引。</para>
/// </param>
/// <param name="Regions">The validated, read-only region directory. <para>已通过校验的只读区域目录。</para></param>
public sealed record MtkDaEntry(ushort HardwareCode, ushort HardwareSubCode, ushort HardwareVersion,
    ushort? SoftwareVersion, ushort PageSize, ushort EntryRegionIndex, IReadOnlyList<MtkDaRegion> Regions);

/// <summary>
/// Validated metadata for an all-in-one MTK download-agent container.
/// <para>all-in-one MTK 下载代理容器的已校验元数据。</para>
/// </summary>
/// <param name="Layout">The unambiguous or explicitly selected directory layout. <para>无歧义或显式选定的目录布局。</para></param>
/// <param name="HeaderIndicatesV6">
/// Whether the header contains the V6 marker; this is not a protocol capability probe.
/// <para>头部是否包含 V6 标记；这不是协议能力探测。</para>
/// </param>
/// <param name="Entries">
/// The read-only entry directory; selection is left to the caller.
/// <para>只读条目目录；条目选取由调用方决定。</para>
/// </param>
public sealed record MtkDaContainer(MtkDaDirectoryLayout Layout, bool HeaderIndicatesV6, IReadOnlyList<MtkDaEntry> Entries);