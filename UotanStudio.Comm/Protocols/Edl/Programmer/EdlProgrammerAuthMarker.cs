namespace UotanStudio.Comm.Protocols.Edl.Programmer;

/// <summary>
/// Detects the refusal marker a programmer carries when it refuses every command except <c>nop</c> and
/// <c>sig</c> until vendor authentication completes.
/// <para>检测 programmer 是否携带“在厂商认证完成前只接受 <c>nop</c> 与 <c>sig</c>”的拒绝标记。</para>
/// </summary>
/// <remarks>
/// The marker is the literal the programmer embeds for the message it logs when it refuses a command, so its
/// presence is evidence about the loader bytes, not about a specific device or a specific signature. Absence is
/// not evidence of the opposite: a loader may compress or strip its strings, so only a positive hit is recorded.
/// <para>该标记是 programmer 内嵌、在拒绝命令时写进日志的字面量，因此它的存在是有关 loader 字节的证据，而不是
/// 关于特定设备或特定签名的证据。不存在反向证据：loader 可能压缩或剥离字符串，因此只记录命中结果。</para>
/// </remarks>
public static class EdlProgrammerAuthMarker
{
    /// <summary>
    /// The byte marker every inspected authentication-gated programmer contains.
    /// <para>所有已核对的、受认证门控的 programmer 都包含的字节标记。</para>
    /// </summary>
    public static ReadOnlySpan<byte> Marker => "Only nop and sig"u8;

    /// <summary>
    /// The number of leading bytes a detection reads before giving up.
    /// <para>检测在放弃之前读取的前导字节数。</para>
    /// </summary>
    public const int MaximumScanBytes = 8 * 1024 * 1024;

    private const int ChunkBytes = 64 * 1024;

    /// <summary>
    /// Reports whether the leading bytes of a programmer contain the authentication refusal marker.
    /// <para>报告 programmer 的前导字节是否包含认证拒绝标记。</para>
    /// </summary>
    /// <param name="source">The programmer stream; it must be readable and is left open.
    /// <para>programmer 流；必须可读，且不会被关闭。</para></param>
    /// <param name="cancellationToken">Cancels the bounded scan. <para>取消有界扫描。</para></param>
    /// <returns><see langword="true"/> when the marker occurs inside <see cref="MaximumScanBytes"/>.
    /// <para>标记出现在 <see cref="MaximumScanBytes"/> 之内时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.
    /// <para><paramref name="source"/> 为 <see langword="null"/>。</para></exception>
    public static bool Detect(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var marker = Marker.ToArray();
        var window = new byte[ChunkBytes + marker.Length - 1];
        var carried = 0;
        var total = 0;
        while (total < MaximumScanBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(window, carried, Math.Min(ChunkBytes, MaximumScanBytes - total));
            if (read <= 0)
            {
                return false;
            }

            total += read;
            var span = window.AsSpan(0, carried + read);
            if (span.IndexOf(marker) >= 0)
            {
                return true;
            }

            carried = Math.Min(marker.Length - 1, span.Length);
            span[^carried..].CopyTo(window);
        }

        return false;
    }

    /// <summary>
    /// Reports whether a programmer file contains the authentication refusal marker.
    /// <para>报告 programmer 文件是否包含认证拒绝标记。</para>
    /// </summary>
    /// <param name="path">The programmer file path. <para>programmer 文件路径。</para></param>
    /// <param name="cancellationToken">Cancels the bounded scan. <para>取消有界扫描。</para></param>
    /// <returns><see langword="true"/> when the marker occurs inside <see cref="MaximumScanBytes"/>.
    /// <para>标记出现在 <see cref="MaximumScanBytes"/> 之内时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.
    /// <para><paramref name="path"/> 为空白。</para></exception>
    /// <exception cref="IOException">The file cannot be opened or read.
    /// <para>文件无法打开或读取。</para></exception>
    public static bool DetectFile(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Detect(stream, cancellationToken);
    }
}