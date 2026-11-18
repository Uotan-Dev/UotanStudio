namespace UotanStudio.DD.Drives;

/// <summary>
/// Category of recordable media exposed by a platform scanner.
/// <para>平台扫描器上报的可记录介质类别。</para>
/// </summary>
public enum FlashMediaKind
{
    /// <summary>
    /// A magnetic or flash block device written as raw sectors.
    /// <para>以裸扇区写入的磁盘或闪存块设备。</para>
    /// </summary>
    Disk,

    /// <summary>
    /// An optical disc writer (CD, DVD or Blu-ray) written through a session-based recorder.
    /// <para>通过基于会话的刻录机写入的光盘设备（CD、DVD 或蓝光）。</para>
    /// </summary>
    OpticalDisc,
}

/// <summary>
/// Optical disc family reported by a recorder, when known.
/// <para>刻录机上报的光盘类型；未知时为零值。</para>
/// </summary>
public enum OpticalDiscFormat
{
    /// <summary>
    /// The disc type is unknown or the recorder did not report one.
    /// <para>未知光盘类型，或刻录机未上报。</para>
    /// </summary>
    Unknown,

    /// <summary>
    /// Compact Disc (CD-R or CD-RW).
    /// <para>紧凑光盘（CD-R 或 CD-RW）。</para>
    /// </summary>
    Cd,

    /// <summary>
    /// Digital Versatile Disc (DVD-R, DVD+R or DVD-RW).
    /// <para>数字通用光盘（DVD-R、DVD+R 或 DVD-RW）。</para>
    /// </summary>
    Dvd,

    /// <summary>
    /// Blu-ray Disc (BD-R or BD-RE).
    /// <para>蓝光光盘（BD-R 或 BD-RE）。</para>
    /// </summary>
    BluRay,
}