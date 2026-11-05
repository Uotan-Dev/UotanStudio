using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Collects the read-only information a connected EDL target reports, using whichever protocol stages
/// the target actually reaches. Sahara contributes the bootrom HELLO facts and, when the bootrom answers
/// the command-mode identity queries, the chip identity; Firehose contributes the negotiated capabilities
/// and the storage geometry. Nothing here writes storage, erases, or changes the boot slot.
/// <para>采集已连接 EDL 目标上报的只读信息，只使用目标实际抵达的协议阶段。Sahara 提供 bootrom 的
/// HELLO 信息，并在 bootrom 应答命令模式身份查询时提供芯片身份；Firehose 提供协商得到的能与存储几何
/// 信息。此处不做任何存储写入、擦除或启动槽位修改。</para>
/// </summary>
/// <remarks>
/// A Firehose query needs a running programmer, so <see cref="CollectAsync"/> never uploads one on its own:
/// the caller supplies the session the discovery layer already configured, exactly like every other region
/// operation. A target that stops at Sahara therefore reports only the Sahara rows instead of failing the
/// whole read.
/// <para>Firehose 查询需要运行中的 programmer，因此 <see cref="CollectAsync"/> 从不自行上传一个：调用方传入
/// 发现层已配置好的会话，与其它区域操作完全一致。停在 Sahara 的目标因此只报告 Sahara 行，而不是让整次读取失败。</para>
/// </remarks>
internal static class EdlInfoCollector
{
    /// <summary>
    /// Collects the Sahara rows from a session that has only been probed, without loading any programmer and
    /// without moving the target out of the state the probe left it in.
    /// <para>在只做过探测、尚未加载任何 programmer、也不把目标移出探测所留状态的会话上采集 Sahara 行。</para>
    /// </summary>
    /// <remarks>
    /// The Sahara identity query is deliberately not part of this read: answering it puts the bootrom into
    /// command mode and then back into image-transfer mode, after which the target starts requesting a
    /// programmer image. A read-only information pass must not create that state, so only the HELLO facts and
    /// the transport facts are reported here; the chip identity appears once a programmer is actually loaded,
    /// where the identity exchange is part of the normal handshake.
    /// <para>此处刻意不包含 Sahara 身份查询：应答该查询会把 bootrom 切到命令模式、再切回镜像传输模式，
    /// 之后目标就会开始请求 programmer 镜像。只读信息采集不得制造该状态，因此这里只报告 HELLO 信息与传输
    /// 信息；芯片身份在真正加载 programmer 后才出现，因为那时身份交换是正常握手的一部分。</para>
    /// </remarks>
    /// <param name="session">The probed EDL session. <para>已探测的 EDL 会话。</para></param>
    /// <returns>The Sahara facts. <para>Sahara 事实。</para></returns>
    internal static EdlInfoSnapshot CollectSahara(EdlSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new EdlInfoSnapshot
        {
            Protocol = session.ProtocolState,
            Sahara = session.SaharaHandshake,
            Identity = session.Sahara?.DeviceIdentity,
            SerialPortName = session.UnderlyingSerialPortName,
        };
    }

    /// <summary>
    /// Collects the Sahara rows, waking the bootrom once when the cached session lost the HELLO facts.
    /// <para>采集 Sahara 行；当已缓存的会话丢失了 HELLO 事实时，唤醒 bootrom 一次。</para>
    /// </summary>
    /// <remarks>
    /// A session that was parked after an earlier probe already consumed the target's single bootrom HELLO, so
    /// it reports a verified Sahara state with no captured HELLO. The probe only captures the HELLO it reads
    /// itself, which means the parked session carries no handshake facts and the information read would show
    /// nothing for a perfectly healthy target. Re-running the bounded probe is safe here because the probe
    /// writes at most one RESET_STATE_MACHINE wake and never a Sahara reply, and a target that is already in
    /// Firehose only answers that wake with a parse error it ignores.
    /// <para>早先探测之后被暂存的会话已经消费了目标唯一的一次 bootrom HELLO，因此它报告已验证的 Sahara
    /// 状态却没有任何已捕获的 HELLO。探测只捕获它自己读到的 HELLO，所以暂存会话不携带握手事实，信息读取
    /// 对一台完全正常的目标会显示空内容。此处重跑一次有界探测是安全的：探测至多写一条
    /// RESET_STATE_MACHINE 唤醒，且从不写 Sahara 应答；已经处于 Firehose 的目标只会用一条它自己忽略的
    /// 解析错误来回应这次唤醒。</para>
    /// </remarks>
    /// <param name="session">The probed EDL session. <para>已探测的 EDL 会话。</para></param>
    /// <param name="cancellationToken">Cancels the bounded re-probe. <para>取消有界重探。</para></param>
    /// <returns>The Sahara facts, with the handshake recovered when it was missing.
    /// <para>Sahara 事实；握手缺失时已恢复。</para></returns>
    internal static async Task<EdlInfoSnapshot> CollectSaharaAsync(EdlSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.SaharaHandshake is null)
        {
            try
            {
                await session.ProbeProtocolAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException and not ObjectDisposedException)
            {
                CommTrace.Debug("edl",
                    $"EDL information read could not probe the endpoint: {failure.Message}");
            }
        }

        return CollectSahara(session);
    }

    /// <summary>
    /// Collects the Firehose rows from an already configured session: the negotiated capability snapshot and
    /// the storage geometry of the requested physical region.
    /// <para>在已配置的会话上采集 Firehose 行：协商得到的能力快照与指定物理区域的存储几何信息。</para>
    /// </summary>
    /// <param name="session">The configured EDL session. <para>已配置的 EDL 会话。</para></param>
    /// <param name="physicalPartitionNumber">The physical region to query. <para>要查询的物理区域。</para></param>
    /// <param name="detectActiveSlot">
    /// Whether to read the GPT and resolve the active A/B slot. The read is a plain sector read, but it costs
    /// one extra round trip per region, so the caller opts in.
    /// <para>是否读取 GPT 并解析活动的 A/B 槽位。该操作只是普通扇区读取，但每个区域会多一次往返，
    /// 因此由调用方显式选择。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels the bounded queries. <para>取消有界的查询。</para></param>
    /// <returns>The Firehose facts, or an empty snapshot when the session is not configured.
    /// <para>Firehose 事实；会话未配置时为空快照。</para></returns>
    internal static async Task<EdlInfoSnapshot> CollectFirehoseAsync(EdlSession session,
        int physicalPartitionNumber = 0, bool detectActiveSlot = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var firehose = session.Firehose;
        if (firehose is null || !session.IsConfigured)
        {
            return new EdlInfoSnapshot
            {
                Protocol = session.ProtocolState,
                Sahara = session.SaharaHandshake,
                SerialPortName = session.UnderlyingSerialPortName,
            };
        }

        FirehoseStorageGeometry? geometry = null;
        FirehoseStorageDetails? storage = null;
        try
        {
            var response = await firehose.GetStorageInfoAsync(physicalPartitionNumber, cancellationToken)
                .ConfigureAwait(false);
            geometry = response.StorageGeometry;
            storage = firehose.LastStorageDetails;
        }
        catch (Exception failure) when (failure is not OperationCanceledException and not ObjectDisposedException)
        {
            geometry = null;
            storage = null;
        }

        var slot = detectActiveSlot
            ? await TryReadSlotLayoutAsync(firehose, physicalPartitionNumber, cancellationToken).ConfigureAwait(false)
            : (ActiveSlot: (string?)null, Layout: EdlSlotLayout.Unknown);
        return new EdlInfoSnapshot
        {
            Protocol = session.ProtocolState,
            Sahara = session.SaharaHandshake,
            Identity = session.Sahara?.DeviceIdentity,
            Capabilities = session.Capabilities,
            Geometry = geometry,
            Storage = storage,
            ActiveSlot = slot.ActiveSlot,
            SlotLayout = slot.Layout,
            SerialPortName = session.UnderlyingSerialPortName,
        };
    }

    /// <summary>
    /// Merges a Firehose snapshot onto a Sahara one so a device that was read at both stages reports every
    /// row it produced, with the Firehose stage winning for the fields both stages can answer.
    /// <para>把 Firehose 快照合并到 Sahara 快照上，使两个阶段都读过的设备报告它产出的每一行，
    /// 两个阶段都能应答的字段以 Firehose 为准。</para>
    /// </summary>
    /// <param name="sahara">The Sahara snapshot. <para>Sahara 快照。</para></param>
    /// <param name="firehose">The Firehose snapshot. <para>Firehose 快照。</para></param>
    /// <returns>The merged snapshot. <para>合并后的快照。</para></returns>
    internal static EdlInfoSnapshot Merge(EdlInfoSnapshot sahara, EdlInfoSnapshot firehose)
    {
        ArgumentNullException.ThrowIfNull(sahara);
        ArgumentNullException.ThrowIfNull(firehose);
        return new EdlInfoSnapshot
        {
            Protocol = firehose.Protocol != EdlProtocolState.Unknown ? firehose.Protocol : sahara.Protocol,
            Sahara = firehose.Sahara ?? sahara.Sahara,
            Identity = firehose.Identity ?? sahara.Identity,
            Capabilities = firehose.Capabilities ?? sahara.Capabilities,
            Geometry = firehose.Geometry ?? sahara.Geometry,
            Storage = firehose.Storage ?? sahara.Storage,
            ActiveSlot = firehose.ActiveSlot ?? sahara.ActiveSlot,
            SlotLayout = firehose.SlotLayout != EdlSlotLayout.Unknown ? firehose.SlotLayout : sahara.SlotLayout,
            SerialPortName = firehose.SerialPortName ?? sahara.SerialPortName,
        };
    }

    /// <summary>
    /// Reads the partition table once and reports both the A/B layout and, when slots exist, which one is
    /// marked active. A device whose table carries no <c>boot_a</c>/<c>boot_b</c> pair is reported as
    /// single-slot rather than as an unreadable slot.
    /// <para>读取一次分区表，报告 A/B 布局，并在存在槽位时报告被标记为活动的那一个。分区表中没有
    /// <c>boot_a</c>/<c>boot_b</c> 对的设备被报告为单槽位，而不是槽位不可读。</para>
    /// </summary>
    /// <param name="firehose">The configured Firehose protocol. <para>已配置的 Firehose 协议实例。</para></param>
    /// <param name="physicalPartitionNumber">The region whose table is read.
    /// <para>读取其分区表的区域。</para></param>
    /// <param name="cancellationToken">Cancels the bounded read. <para>取消有界读取。</para></param>
    /// <returns>The detected layout together with the active slot when the layout is dual.
    /// <para>检测到的布局，以及布局为双槽位时的活动槽位。</para></returns>
    private static async Task<(string? ActiveSlot, EdlSlotLayout Layout)> TryReadSlotLayoutAsync(
        FirehoseProtocol firehose, int physicalPartitionNumber, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await firehose.ReadGptAsync(physicalPartitionNumber, cancellationToken)
                .ConfigureAwait(false);
            var slotted = snapshot.Table.Partitions.Any(entry =>
                string.Equals(entry.Name, "boot_a", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Name, "boot_b", StringComparison.OrdinalIgnoreCase));
            if (!slotted)
            {
                return (null, EdlSlotLayout.Single);
            }

            return (FirehoseProtocol.DetectActiveSlot(snapshot), EdlSlotLayout.Dual);
        }
        catch (Exception failure) when (failure is not OperationCanceledException and not ObjectDisposedException)
        {
            return (null, EdlSlotLayout.Unknown);
        }
    }
}