namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Compression codec for ADB sync v2 send/recv data streams.
/// <para>ADB sync v2 收发数据流的压缩编解码器。</para>
/// </summary>
public enum AdbSyncCompression
{
    /// <summary>Pick the strongest codec the device advertises (zstd &gt; lz4 &gt; brotli), or none.
    /// <para>选择设备通告的最强编解码器（zstd &gt; lz4 &gt; brotli），无则不压缩。</para></summary>
    Auto,
    /// <summary>No compression; raw byte blocks. <para>不压缩；原始字节块。</para></summary>
    None,
    /// <summary>Brotli stream. <para>Brotli 流。</para></summary>
    Brotli,
    /// <summary>LZ4 frame stream. <para>LZ4 帧流。</para></summary>
    Lz4,
    /// <summary>Zstd stream. <para>Zstd 流。</para></summary>
    Zstd,
}

/// <summary>
/// Sync v2 capabilities negotiated from the peer's CNXN feature list. A device that did not
/// announce a feature keeps the matching capability off, which routes the client to the v1 wire
/// format for that operation.
/// <para>从对端 CNXN 特性列表协商出的 sync v2 能力。设备未通告的特性保持关闭，客户端对该操作回退到 v1 线上格式。</para>
/// </summary>
public sealed class AdbSyncCapabilities
{
    /// <summary>Feature-free capability set for server-mediated connections and pre-handshake states.
    /// <para>面向经 adb server 中转的连接与握手前状态的零能力集合。</para></summary>
    public static AdbSyncCapabilities None { get; } = new();

    /// <summary>Gets whether the peer advertises <c>stat_v2</c>. <para>对端是否通告 <c>stat_v2</c>。</para></summary>
    public bool StatV2 { get; private init; }

    /// <summary>Gets whether the peer advertises <c>ls_v2</c>. <para>对端是否通告 <c>ls_v2</c>。</para></summary>
    public bool LsV2 { get; private init; }

    /// <summary>Gets whether the peer advertises <c>sendrecv_v2</c>. <para>对端是否通告 <c>sendrecv_v2</c>。</para></summary>
    public bool SendRecvV2 { get; private init; }

    /// <summary>Gets whether the peer advertises <c>sendrecv_v2_brotli</c>. <para>对端是否通告 <c>sendrecv_v2_brotli</c>。</para></summary>
    public bool Brotli { get; private init; }

    /// <summary>Gets whether the peer advertises <c>sendrecv_v2_lz4</c>. <para>对端是否通告 <c>sendrecv_v2_lz4</c>。</para></summary>
    public bool Lz4 { get; private init; }

    /// <summary>Gets whether the peer advertises <c>sendrecv_v2_zstd</c>. <para>对端是否通告 <c>sendrecv_v2_zstd</c>。</para></summary>
    public bool Zstd { get; private init; }

    /// <summary>Gets whether the peer advertises <c>sendrecv_v2_dry_run_send</c>. <para>对端是否通告 <c>sendrecv_v2_dry_run_send</c>。</para></summary>
    public bool DryRunSend { get; private init; }

    /// <summary>
    /// Parses the peer's CNXN feature list into sync capabilities.
    /// <para>把对端的 CNXN 特性列表解析为 sync 能力。</para>
    /// </summary>
    /// <param name="features">Feature names from the peer banner; unknown entries are ignored.
    /// <para>对端 banner 中的特性名；未知条目被忽略。</para></param>
    /// <returns>The negotiated capabilities. <para>协商出的能力。</para></returns>
    public static AdbSyncCapabilities FromFeatures(IEnumerable<string>? features)
    {
        if (features is null)
        {
            return None;
        }

        var statV2 = false;
        var lsV2 = false;
        var sendRecvV2 = false;
        var brotli = false;
        var lz4 = false;
        var zstd = false;
        var dryRunSend = false;
        foreach (var feature in features)
        {
            switch (feature)
            {
                case "stat_v2":
                    statV2 = true;
                    break;
                case "ls_v2":
                    lsV2 = true;
                    break;
                case "sendrecv_v2":
                    sendRecvV2 = true;
                    break;
                case "sendrecv_v2_brotli":
                    brotli = true;
                    break;
                case "sendrecv_v2_lz4":
                    lz4 = true;
                    break;
                case "sendrecv_v2_zstd":
                    zstd = true;
                    break;
                case "sendrecv_v2_dry_run_send":
                    dryRunSend = true;
                    break;
            }
        }

        if (!statV2 && !lsV2 && !sendRecvV2)
        {
            return None;
        }

        return new AdbSyncCapabilities
        {
            StatV2 = statV2,
            LsV2 = lsV2,
            SendRecvV2 = sendRecvV2,
            Brotli = brotli,
            Lz4 = lz4,
            Zstd = zstd,
            DryRunSend = dryRunSend,
        };
    }

    /// <summary>
    /// Resolves a requested codec against the negotiated capabilities:
    /// <see cref="AdbSyncCompression.Auto"/> prefers zstd, then lz4, then brotli, else none; an explicit
    /// codec the device did not announce is rejected.
    /// <para>按协商能力解析请求的编解码器：<see cref="AdbSyncCompression.Auto"/> 优先 zstd，其次 lz4、brotli，
    /// 无则不压缩；设备未通告的显式编解码器会被拒绝。</para>
    /// </summary>
    /// <param name="requested">The requested codec. <para>请求的编解码器。</para></param>
    /// <returns>The codec to use on the wire. <para>线上使用的编解码器。</para></returns>
    /// <exception cref="NotSupportedException">An explicit codec was requested that the device does not advertise.
    /// <para>请求了设备未通告的显式编解码器。</para></exception>
    public AdbSyncCompression ResolveCompression(AdbSyncCompression requested) => requested switch
    {
        AdbSyncCompression.None => AdbSyncCompression.None,
        AdbSyncCompression.Brotli when Brotli => AdbSyncCompression.Brotli,
        AdbSyncCompression.Lz4 when Lz4 => AdbSyncCompression.Lz4,
        AdbSyncCompression.Zstd when Zstd => AdbSyncCompression.Zstd,
        AdbSyncCompression.Auto => Zstd ? AdbSyncCompression.Zstd
            : Lz4 ? AdbSyncCompression.Lz4
            : Brotli ? AdbSyncCompression.Brotli
            : AdbSyncCompression.None,
        _ => throw new NotSupportedException(
            $"The device does not advertise sync v2 compression '{requested}'."),
    };

    /// <summary>
    /// Encodes the sync v2 setup flag word for the resolved codec; at most one compression bit plus
    /// the optional dry-run bit is set, matching the daemon's single-codec validation.
    /// <para>编码解析后编解码器的 sync v2 setup 旗标字；至多一个压缩位加可选 dry-run 位，匹配 daemon 的单编解码器校验。</para>
    /// </summary>
    /// <param name="compression">The resolved codec. <para>已解析的编解码器。</para></param>
    /// <param name="dryRun">Whether to request a dry-run push; requires <see cref="DryRunSend"/>.
    /// <para>是否请求推送试运行；需要 <see cref="DryRunSend"/>。</para></param>
    /// <returns>The flag word. <para>旗标字。</para></returns>
    /// <exception cref="NotSupportedException">Dry run was requested without the <c>sendrecv_v2_dry_run_send</c> feature.
    /// <para>未通告 <c>sendrecv_v2_dry_run_send</c> 却请求了 dry run。</para></exception>
    public uint ResolveFlags(AdbSyncCompression compression, bool dryRun)
    {
        if (dryRun && !DryRunSend)
        {
            throw new NotSupportedException("The device does not advertise sync v2 dry-run push.");
        }

        uint flags = compression switch
        {
            AdbSyncCompression.Brotli => SyncFlags.Brotli,
            AdbSyncCompression.Lz4 => SyncFlags.Lz4,
            AdbSyncCompression.Zstd => SyncFlags.Zstd,
            _ => 0,
        };
        if (dryRun)
        {
            flags |= SyncFlags.DryRun;
        }

        return flags;
    }
}