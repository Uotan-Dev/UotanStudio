namespace UotanStudio.Firmware.Ext4.Journal;

/// <summary>
/// 日志覆盖层：存储 journal 重放后的脏块映射（块号 → 块数据）。
/// 构建后不可变，读端线程安全。
/// <para>Journal overlay: stores replayed dirty block mappings (block number → block data).
/// Immutable after construction, thread-safe for reads.</para>
/// </summary>
internal sealed class JournalOverlay
{
    private readonly Dictionary<long, byte[]> _overlay;

    /// <summary>
    /// 以指定覆盖映射构造覆盖层。
    /// <para>Constructs an overlay with the specified block mappings.</para>
    /// </summary>
    public JournalOverlay(Dictionary<long, byte[]> overlay)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
    }

    /// <summary>空覆盖层实例。<para>Empty overlay instance.</para></summary>
    public static JournalOverlay Empty { get; } = new(new Dictionary<long, byte[]>());

    /// <summary>是否有覆盖数据。<para>Whether the overlay contains any data.</para></summary>
    public bool HasOverlay => _overlay.Count > 0;

    /// <summary>覆盖的块数。<para>Number of overlaid blocks.</para></summary>
    public int Count => _overlay.Count;

    /// <summary>
    /// 尝试获取指定块号的覆盖数据。
    /// <para>Tries to get the overlay data for the specified block number.</para>
    /// </summary>
    public bool TryGetBlock(long blockNumber, out byte[] data)
    {
        return _overlay.TryGetValue(blockNumber, out data!);
    }
}
