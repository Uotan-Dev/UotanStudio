namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Journal;

internal sealed class JournalOverlay
{
    private readonly Dictionary<long, byte[]> _overlay;

    /// <summary>
    /// Constructs an overlay with the specified block mappings.
    /// <para>以指定覆盖映射构造覆盖层。</para>
    /// </summary>
    public JournalOverlay(Dictionary<long, byte[]> overlay)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
    }

    public static JournalOverlay Empty { get; } = new(new Dictionary<long, byte[]>());

    public bool HasOverlay => _overlay.Count > 0;

    public int Count => _overlay.Count;

    /// <summary>
    /// Tries to get the overlay data for the specified block number.
    /// <para>尝试获取指定块号的覆盖数据。</para>
    /// </summary>
    public bool TryGetBlock(long blockNumber, out byte[] data)
    {
        return _overlay.TryGetValue(blockNumber, out data!);
    }
}