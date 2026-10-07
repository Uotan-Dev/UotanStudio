namespace UotanStudio.Firmware.FileSystem.Ext4.Journal;

internal sealed class JournalOverlay
{
    private readonly Dictionary<long, byte[]> _overlay;

    /// <summary>
    /// Constructs an overlay with the specified block mappings.
    /// <para>以指定覆盖映射构造覆盖层。</para>
    /// </summary>
    /// <param name="overlay">Journaled block map keyed by filesystem block number, retained by the overlay; a null map is rejected.
    /// <para>以文件系统块号为键的日志块映射，由覆盖层直接持有；不接受 null 映射。</para></param>
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
    /// <param name="blockNumber">Filesystem block number whose journaled bytes are requested.
    /// <para>需要查询日志字节的文件系统块号。</para></param>
    /// <param name="data">Journaled bytes recorded for that block number, or <see langword="null"/> when the block carries no overlay.
    /// <para>该块号记录的日志字节；块没有覆盖数据时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the overlay holds bytes for <paramref name="blockNumber"/>.
    /// <para>覆盖层持有 <paramref name="blockNumber"/> 的字节时返回 <see langword="true"/>。</para></returns>
    public bool TryGetBlock(long blockNumber, out byte[] data)
    {
        return _overlay.TryGetValue(blockNumber, out data!);
    }
}