using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Streaming;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Drives the Streaming DLOAD NAND device entries: each opens a session, completes the handshake, takes the
/// streaming protocol and runs only protocol-level NAND operations.
/// <para>驱动 Streaming DLOAD NAND 的设备入口：每个入口打开会话、完成握手、取回流式协议，之后只执行协议级
/// NAND 操作。</para>
/// </summary>
internal static class EdlStreamingNandService
{
    /// <summary>
    /// The DIAG read address of the streaming backend's on-device partition table, read from offset 0.
    /// <para>流式后端设备分区表的 DIAG 读取地址，从偏移 0 起读取分区表。</para>
    /// </summary>
    private const uint StreamingPartitionTableAddress = 0u;

    /// <summary>
    /// Reads the bad-marker state of the first <paramref name="blockCount"/> erase blocks of the named
    /// partition, issuing only the <c>check_block</c> page-read sequence.
    /// <para>读取具名分区前 <paramref name="blockCount"/> 个擦除块的坏块标记状态，只发出 <c>check_block</c>
    /// 的页读序列。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="partition">The partition name to inspect, matched exactly. <para>要检查的分区名，按精确
    /// 名字匹配。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="blockCount">The number of blocks to inspect. <para>要检查的块数。</para></param>
    /// <param name="ct">Cancellation threaded through the session open, the handshake and every read.
    /// <para>贯穿会话打开、握手与每次读取的取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path; an existing file is the image uploaded
    /// during initialization. <para>可选的编程器覆盖路径；文件存在时在初始化阶段上传该镜像。</para></param>
    /// <param name="initialization">Optional session startup requirements; <see langword="null"/> uses the
    /// streaming-DLOAD-only defaults. <para>可选的会话启动选项；为空时使用只启用流式 DLOAD 的默认值。</para></param>
    /// <returns>The per-block verdicts plus a renderable summary. <para>逐块结论与可读摘要。</para></returns>
    /// <exception cref="ArgumentNullException">The device argument is null. <para>设备参数为空。</para></exception>
    /// <exception cref="ArgumentException">The supplied startup options disable the streaming DLOAD switch.
    /// <para>所传入的启动选项关闭了流式 DLOAD 开关。</para></exception>
    /// <exception cref="InvalidOperationException">The session cannot be initialized as a Streaming DLOAD
    /// session, or the reported table lacks that name. <para>会话无法初始化为 Streaming DLOAD 会话，或上报的
    /// 表里没有该分区名。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation lands while the session is opening,
    /// initializing or the table is read. <para>取消发生在会话打开、初始化或读取分区表期间。</para></exception>
    internal static Task<EdlStreamingBadBlockMapResult> ReadBadBlockMapAsync(DeviceTarget device, string partition,
        EdlStreamingNandLayout layout, int blockCount, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null)
        => RunStreamingAsync(device,
            streaming => ReadBadBlockMapCoreAsync(streaming, partition, layout, blockCount, ct), ct,
            programmerPath, initialization);

    /// <summary>
    /// Runs the by-name resolution and the bad-block scan over an already negotiated streaming protocol.
    /// <para>在已协商的流式协议上执行按名解析与坏块扫描。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="partition">The partition name to inspect. <para>要检查的分区名。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="blockCount">The number of blocks to inspect. <para>要检查的块数。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The per-block verdicts plus a renderable summary. <para>逐块结论与可读摘要。</para></returns>
    /// <exception cref="ArgumentException">The partition name is blank. <para>分区名为空白。</para></exception>
    /// <exception cref="InvalidOperationException">The reported table lacks that name, or the target reported
    /// no usable sector size. <para>上报的表里没有该分区名，或目标未上报可用的扇区尺寸。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">A layout field or the requested block count is out of
    /// range. <para>布局字段或请求的块数越界。</para></exception>
    /// <exception cref="OperationCanceledException">The partition-table read was cancelled.
    /// <para>分区表读取被取消。</para></exception>
    internal static async Task<EdlStreamingBadBlockMapResult> ReadBadBlockMapCoreAsync(StreamingProtocol streaming,
        string partition, EdlStreamingNandLayout layout, int blockCount, CancellationToken ct)
    {
        var entry = await RequireReportedPartitionAsync(streaming, partition, ct).ConfigureAwait(false);
        return await EdlStreamingFlashService.ReadBadBlockMapAsync(streaming, entry, layout, blockCount, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a bad marker into a currently good block; the write is irreversible, so an unconfirmed request
    /// is refused before the session is opened.
    /// <para>给当前完好的块写入坏块标记；这是不可逆写入，未确认时在打开会话之前拒绝。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="partition">The partition name the block index resolves against. <para>块号所参照的
    /// 分区名。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="block">The block to mark. <para>要标记的块号。</para></param>
    /// <param name="confirmed">Set to <see langword="true"/> to confirm this marker
    /// write. <para>置为 <see langword="true"/> 以确认这次标记写入。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path; an existing file is the image uploaded
    /// during initialization. <para>可选的编程器覆盖路径；文件存在时在初始化阶段上传该镜像。</para></param>
    /// <param name="initialization">Optional session startup requirements; <see langword="null"/> uses the
    /// streaming-DLOAD-only defaults. <para>可选的会话启动选项；为空时使用只启用流式 DLOAD 的默认值。</para></param>
    /// <returns>The mark outcome plus a renderable summary. <para>标记结果与可读摘要。</para></returns>
    /// <exception cref="ArgumentNullException">The device argument is null. <para>设备参数为空。</para></exception>
    /// <exception cref="ArgumentException">The request was not confirmed, or the startup options disable the
    /// streaming DLOAD switch. <para>请求未经确认，或启动选项关闭了流式 DLOAD 开关。</para></exception>
    /// <exception cref="InvalidOperationException">The session cannot be initialized as a Streaming DLOAD
    /// session, or the reported table lacks that name. <para>会话无法初始化为 Streaming DLOAD 会话，或上报的
    /// 表里没有该分区名。</para></exception>
    internal static Task<EdlStreamingBadBlockWriteResult> MarkBlockBadAsync(DeviceTarget device, string partition,
        EdlStreamingNandLayout layout, uint block, bool confirmed, CancellationToken ct,
        string? programmerPath = null, EdlInitializationOptions? initialization = null)
    {
        RequireMarkBadConfirmation(confirmed);
        return RunStreamingAsync(device,
            streaming => MarkBlockBadCoreAsync(streaming, partition, layout, block, confirmed, ct), ct,
            programmerPath, initialization);
    }

    /// <summary>
    /// Runs the confirmation gate, the by-name resolution and the marker write over an already negotiated
    /// streaming protocol.
    /// <para>在已协商的流式协议上执行标记坏块的确认门、按名解析与写入。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="partition">The partition name the block index resolves against. <para>块号所参照的
    /// 分区名。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="block">The block to mark. <para>要标记的块号。</para></param>
    /// <param name="confirmed">Set to <see langword="true"/> to confirm this marker
    /// write. <para>置为 <see langword="true"/> 以确认这次标记写入。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The mark outcome plus a renderable summary. <para>标记结果与可读摘要。</para></returns>
    /// <exception cref="ArgumentException">The request was not confirmed, or the partition name is blank.
    /// <para>请求未经确认，或分区名为空白。</para></exception>
    /// <exception cref="InvalidOperationException">The table lacks that partition name, the session has no
    /// usable geometry, or the loader is peek-only. <para>表里没有该分区名、会话没有可用几何信息，或 loader
    /// 仅支持 peek。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The block leaves the reported erase-block count, or a
    /// layout field is out of range. <para>块号超出上报的擦除块数，或布局字段越界。</para></exception>
    internal static async Task<EdlStreamingBadBlockWriteResult> MarkBlockBadCoreAsync(StreamingProtocol streaming,
        string partition, EdlStreamingNandLayout layout, uint block, bool confirmed, CancellationToken ct)
    {
        RequireMarkBadConfirmation(confirmed);
        var entry = await RequireReportedPartitionAsync(streaming, partition, ct).ConfigureAwait(false);
        return await EdlStreamingFlashService.MarkBlockBadAsync(streaming, entry, layout, block, confirmed, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Erases the whole block to clear a bad marker; the erase destroys every page in the block, so an
    /// unconfirmed request is refused before the session is opened.
    /// <para>擦除整块以解除坏块标记；擦除会毁掉该块内的全部页，未确认时在打开会话之前拒绝。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="partition">The partition name the block index resolves against. <para>块号所参照的
    /// 分区名。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="block">The block to erase. <para>要擦除的块号。</para></param>
    /// <param name="confirmed">Set to <see langword="true"/> to confirm this whole-block
    /// erase. <para>置为 <see langword="true"/> 以确认这次整块擦除。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path; an existing file is the image uploaded
    /// during initialization. <para>可选的编程器覆盖路径；文件存在时在初始化阶段上传该镜像。</para></param>
    /// <param name="initialization">Optional session startup requirements; <see langword="null"/> uses the
    /// streaming-DLOAD-only defaults. <para>可选的会话启动选项；为空时使用只启用流式 DLOAD 的默认值。</para></param>
    /// <returns>The un-mark outcome plus a renderable summary. <para>解除标记结果与可读摘要。</para></returns>
    /// <exception cref="ArgumentNullException">The device argument is null. <para>设备参数为空。</para></exception>
    /// <exception cref="ArgumentException">The request was not confirmed, or the startup options disable the
    /// streaming DLOAD switch. <para>请求未经确认，或启动选项关闭了流式 DLOAD 开关。</para></exception>
    /// <exception cref="InvalidOperationException">The session cannot be initialized as a Streaming DLOAD
    /// session, or the reported table lacks that name. <para>会话无法初始化为 Streaming DLOAD 会话，或上报的
    /// 表里没有该分区名。</para></exception>
    internal static Task<EdlStreamingBadBlockWriteResult> UnmarkBlockBadAsync(DeviceTarget device, string partition,
        EdlStreamingNandLayout layout, uint block, bool confirmed, CancellationToken ct,
        string? programmerPath = null, EdlInitializationOptions? initialization = null)
    {
        RequireUnmarkBadConfirmation(confirmed);
        return RunStreamingAsync(device,
            streaming => UnmarkBlockBadCoreAsync(streaming, partition, layout, block, confirmed, ct), ct,
            programmerPath, initialization);
    }

    /// <summary>
    /// Runs the confirmation gate, the by-name resolution and the whole-block erase of the un-mark over an
    /// already negotiated streaming protocol.
    /// <para>在已协商的流式协议上执行解除标记的确认门、按名解析与整块擦除。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="partition">The partition name the block index resolves against. <para>块号所参照的
    /// 分区名。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="block">The block to erase. <para>要擦除的块号。</para></param>
    /// <param name="confirmed">Set to <see langword="true"/> to confirm this whole-block
    /// erase. <para>置为 <see langword="true"/> 以确认这次整块擦除。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The un-mark outcome plus a renderable summary. <para>解除标记结果与可读摘要。</para></returns>
    /// <exception cref="ArgumentException">The request was not confirmed, or the partition name is blank.
    /// <para>请求未经确认，或分区名为空白。</para></exception>
    /// <exception cref="InvalidOperationException">The table lacks that partition name, the session has no
    /// usable geometry, or the loader is peek-only. <para>表里没有该分区名、会话没有可用几何信息，或 loader
    /// 仅支持 peek。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The block leaves the reported erase-block count, or a
    /// layout field is out of range. <para>块号超出上报的擦除块数，或布局字段越界。</para></exception>
    internal static async Task<EdlStreamingBadBlockWriteResult> UnmarkBlockBadCoreAsync(StreamingProtocol streaming,
        string partition, EdlStreamingNandLayout layout, uint block, bool confirmed, CancellationToken ct)
    {
        RequireUnmarkBadConfirmation(confirmed);
        var entry = await RequireReportedPartitionAsync(streaming, partition, ct).ConfigureAwait(false);
        return await EdlStreamingFlashService.UnmarkBlockBadAsync(streaming, entry, layout, block, confirmed, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads only the three NAND_DEV0 configuration words and reports the decoded fields and candidate values
    /// through the transcribed bit layouts; no register is written and no partition name is needed.
    /// <para>只读取三个 NAND_DEV0 配置字并按已转录的位域报告解码值与候选值；本方法不写任何寄存器，也不需要
    /// 分区名。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path; an existing file is the image uploaded
    /// during initialization. <para>可选的编程器覆盖路径；文件存在时在初始化阶段上传该镜像。</para></param>
    /// <param name="initialization">Optional session startup requirements; <see langword="null"/> uses the
    /// streaming-DLOAD-only defaults. <para>可选的会话启动选项；为空时使用只启用流式 DLOAD 的默认值。</para></param>
    /// <returns>The register snapshot, decoded fields and candidate values. <para>寄存器快照、解码字段与候选
    /// 值。</para></returns>
    /// <exception cref="ArgumentNullException">The device argument is null. <para>设备参数为空。</para></exception>
    /// <exception cref="ArgumentException">The supplied startup options disable the streaming DLOAD switch.
    /// <para>所传入的启动选项关闭了流式 DLOAD 开关。</para></exception>
    /// <exception cref="InvalidOperationException">The session cannot be initialized as a Streaming DLOAD
    /// session. <para>会话无法初始化为 Streaming DLOAD 会话。</para></exception>
    internal static Task<EdlStreamingNandRegisterResult> ReadRegisterReportAsync(DeviceTarget device,
        EdlStreamingNandLayout layout, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null)
        => RunStreamingAsync(device, streaming => ReadRegisterReportCoreAsync(streaming, layout, ct), ct,
            programmerPath, initialization);

    /// <summary>
    /// Reads the configuration-word snapshot and derives the candidate values over an already negotiated
    /// streaming protocol.
    /// <para>在已协商的流式协议上读取配置字快照并推导候选值。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The register snapshot, decoded fields and candidate values. <para>寄存器快照、解码字段与候选
    /// 值。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">A layout field is out of range. <para>布局字段越界。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">The session is not in streaming mode or reported no usable
    /// sector size. <para>会话不在流式模式，或未上报可用的扇区尺寸。</para></exception>
    internal static Task<EdlStreamingNandRegisterResult> ReadRegisterReportCoreAsync(StreamingProtocol streaming,
        EdlStreamingNandLayout layout, CancellationToken ct) =>
        EdlStreamingFlashService.ReadNandRegisterReportAsync(streaming, layout, ct);

    /// <summary>
    /// Runs one controller page-read sequence over a block of the named partition and reports the bad-block
    /// and ECC verdicts; the sequence pulses command registers, so confirmation is required.
    /// <para>对具名分区里的一个块执行一次控制器页读并回报坏块与 ECC 结论；该序列会脉冲命令寄存器，因此必须
    /// 确认。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="partition">The partition name the block index resolves against. <para>块号所参照的
    /// 分区名。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="block">The block to read. <para>要读取的块号。</para></param>
    /// <param name="confirmed">Set to <see langword="true"/> to confirm this controller command
    /// sequence. <para>置为 <see langword="true"/> 以确认这次控制器命令序列。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path; an existing file is the image uploaded
    /// during initialization. <para>可选的编程器覆盖路径；文件存在时在初始化阶段上传该镜像。</para></param>
    /// <param name="initialization">Optional session startup requirements; <see langword="null"/> uses the
    /// streaming-DLOAD-only defaults. <para>可选的会话启动选项；为空时使用只启用流式 DLOAD 的默认值。</para></param>
    /// <returns>The status words and decoded verdicts. <para>状态字与解码结论。</para></returns>
    /// <exception cref="ArgumentNullException">The device argument is null. <para>设备参数为空。</para></exception>
    /// <exception cref="ArgumentException">The request was not confirmed, or the startup options disable the
    /// streaming DLOAD switch. <para>请求未经确认，或启动选项关闭了流式 DLOAD 开关。</para></exception>
    /// <exception cref="InvalidOperationException">The session cannot be initialized as a Streaming DLOAD
    /// session, or the reported table lacks that name. <para>会话无法初始化为 Streaming DLOAD 会话，或上报的
    /// 表里没有该分区名。</para></exception>
    internal static Task<EdlStreamingNandEccResult> EvaluateCodewordStatusAsync(DeviceTarget device, string partition,
        EdlStreamingNandLayout layout, uint block, bool confirmed, CancellationToken ct,
        string? programmerPath = null, EdlInitializationOptions? initialization = null)
    {
        RequireCodewordConfirmation(confirmed);
        return RunStreamingAsync(device,
            streaming => EvaluateCodewordStatusCoreAsync(streaming, partition, layout, block, confirmed, ct), ct,
            programmerPath, initialization);
    }

    /// <summary>
    /// Runs the confirmation gate, the by-name resolution and the codeword page read over an already
    /// negotiated streaming protocol.
    /// <para>在已协商的流式协议上执行码字状态评估的确认门、按名解析与页读。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="partition">The partition name the block index resolves against. <para>块号所参照的
    /// 分区名。</para></param>
    /// <param name="layout">The streaming NAND controller layout. <para>流式 NAND 控制器布局。</para></param>
    /// <param name="block">The block to read. <para>要读取的块号。</para></param>
    /// <param name="confirmed">Set to <see langword="true"/> to confirm this controller command
    /// sequence. <para>置为 <see langword="true"/> 以确认这次控制器命令序列。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The status words and decoded verdicts. <para>状态字与解码结论。</para></returns>
    /// <exception cref="ArgumentException">The request was not confirmed, or the partition name is blank.
    /// <para>请求未经确认，或分区名为空白。</para></exception>
    /// <exception cref="InvalidOperationException">The table lacks that partition name, the session has no
    /// usable geometry, or the loader is peek-only. <para>表里没有该分区名、会话没有可用几何信息，或 loader
    /// 仅支持 peek。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The block leaves the reported erase-block count, or a
    /// layout field is out of range. <para>块号超出上报的擦除块数，或布局字段越界。</para></exception>
    internal static async Task<EdlStreamingNandEccResult> EvaluateCodewordStatusCoreAsync(
        StreamingProtocol streaming, string partition, EdlStreamingNandLayout layout, uint block, bool confirmed,
        CancellationToken ct)
    {
        RequireCodewordConfirmation(confirmed);
        var entry = await RequireReportedPartitionAsync(streaming, partition, ct).ConfigureAwait(false);
        return await EdlStreamingFlashService.EvaluateNandCodewordAsync(streaming, entry, layout, block, confirmed, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the secure-mode negotiation plus the qclose exchange that puts the target into flash mode; an
    /// unconfirmed request is refused before the session is opened.
    /// <para>执行安全模式协商加 qclose 收束，把目标切换到闪存模式；未确认时在打开会话之前拒绝。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="confirmed">Set to <see langword="true"/> to confirm the flash-mode
    /// transition. <para>置为 <see langword="true"/> 以确认切换到闪存模式。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path; an existing file is the image uploaded
    /// during initialization. <para>可选的编程器覆盖路径；文件存在时在初始化阶段上传该镜像。</para></param>
    /// <param name="initialization">Optional session startup requirements; <see langword="null"/> uses the
    /// streaming-DLOAD-only defaults. <para>可选的会话启动选项；为空时使用只启用流式 DLOAD 的默认值。</para></param>
    /// <returns>The transition result plus a renderable summary. <para>切换结果与可读摘要。</para></returns>
    /// <exception cref="ArgumentNullException">The device argument is null. <para>设备参数为空。</para></exception>
    /// <exception cref="ArgumentException">The request was not confirmed, or the startup options disable the
    /// streaming DLOAD switch. <para>请求未经确认，或启动选项关闭了流式 DLOAD 开关。</para></exception>
    /// <exception cref="InvalidOperationException">The session cannot be initialized as a Streaming DLOAD
    /// session. <para>会话无法初始化为 Streaming DLOAD 会话。</para></exception>
    internal static Task<EdlStreamingControlResult> EnterFlashModeAsync(DeviceTarget device, bool confirmed,
        CancellationToken ct, string? programmerPath = null, EdlInitializationOptions? initialization = null)
    {
        RequireFlashModeConfirmation(confirmed);
        return RunStreamingAsync(device, streaming => EnterFlashModeCoreAsync(streaming, confirmed, ct), ct,
            programmerPath, initialization);
    }

    /// <summary>
    /// Runs the confirmation gate and the closing exchange of the flash-mode transition over an already
    /// negotiated streaming protocol.
    /// <para>在已协商的流式协议上执行闪存模式切换的确认门与收束序列。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="confirmed">Set to <see langword="true"/> to confirm the flash-mode
    /// transition. <para>置为 <see langword="true"/> 以确认切换到闪存模式。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The transition result plus a renderable summary. <para>切换结果与可读摘要。</para></returns>
    /// <exception cref="ArgumentException">The request was not confirmed. <para>请求未经确认。</para></exception>
    /// <exception cref="InvalidOperationException">The session never completed the hello exchange or never
    /// reported a usable geometry. <para>会话从未完成 hello，或未上报可用的几何信息。</para></exception>
    internal static async Task<EdlStreamingControlResult> EnterFlashModeCoreAsync(StreamingProtocol streaming,
        bool confirmed, CancellationToken ct)
    {
        RequireFlashModeConfirmation(confirmed);
        return await EdlStreamingFlashService.EnterFlashModeAsync(streaming, confirmed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Negotiates secure mode with DIAG 0x17, accepting the trusted mode byte 0x01 only; a false selection is
    /// refused before the session is opened.
    /// <para>用 DIAG 0x17 协商安全模式，只接受受信模式字节 0x01；未选择受信时在打开会话之前拒绝。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="trusted">Set to <see langword="true"/> to select the trusted mode byte
    /// 0x01. <para>置为 <see langword="true"/> 以选择受信模式字节 0x01。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path; an existing file is the image uploaded
    /// during initialization. <para>可选的编程器覆盖路径；文件存在时在初始化阶段上传该镜像。</para></param>
    /// <param name="initialization">Optional session startup requirements; <see langword="null"/> uses the
    /// streaming-DLOAD-only defaults. <para>可选的会话启动选项；为空时使用只启用流式 DLOAD 的默认值。</para></param>
    /// <returns>Whether the target acknowledged the negotiation. <para>目标是否确认协商结果。</para></returns>
    /// <exception cref="ArgumentNullException">The device argument is null. <para>设备参数为空。</para></exception>
    /// <exception cref="ArgumentException">The trusted flag was not set, or the startup options disable the
    /// streaming DLOAD switch. <para>未选择受信模式，或启动选项关闭了流式 DLOAD 开关。</para></exception>
    /// <exception cref="InvalidOperationException">The session cannot be initialized as a Streaming DLOAD
    /// session. <para>会话无法初始化为 Streaming DLOAD 会话。</para></exception>
    internal static Task<EdlStreamingControlResult> EnterSecureModeAsync(DeviceTarget device, bool trusted,
        CancellationToken ct, string? programmerPath = null, EdlInitializationOptions? initialization = null)
    {
        RequireTrustedMode(trusted);
        return RunStreamingAsync(device, streaming => EnterSecureModeCoreAsync(streaming, trusted, ct), ct,
            programmerPath, initialization);
    }

    /// <summary>
    /// Runs the trusted-mode gate and the secure-mode negotiation over an already negotiated streaming
    /// protocol.
    /// <para>在已协商的流式协议上执行受信模式门与安全模式协商。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="trusted">Set to <see langword="true"/> to select the trusted mode byte
    /// 0x01. <para>置为 <see langword="true"/> 以选择受信模式字节 0x01。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Whether the target acknowledged the negotiation. <para>目标是否确认协商结果。</para></returns>
    /// <exception cref="ArgumentException">The trusted flag was not set. <para>未选择受信模式。</para></exception>
    /// <exception cref="InvalidOperationException">The session never completed the hello exchange, so it is
    /// not in streaming mode. <para>会话从未完成 hello，因此不在流式模式。</para></exception>
    internal static async Task<EdlStreamingControlResult> EnterSecureModeCoreAsync(StreamingProtocol streaming,
        bool trusted, CancellationToken ct)
    {
        RequireTrustedMode(trusted);
        return await EdlStreamingFlashService.EnterSecureModeAsync(streaming, trusted, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads the raw on-disk partition table to the target with DIAG 0x19. The file is checked for existence
    /// and length before a session opens, and no file on disk is deleted or overwritten.
    /// <para>把磁盘上的原始分区表按 DIAG 0x19 上传到目标。打开会话前先检查文件是否存在与长度，且不会删除或
    /// 覆盖磁盘上的任何文件。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="tablePath">The raw partition table file path. <para>原始分区表文件路径。</para></param>
    /// <param name="overrideExisting"><see langword="true"/> sends mode byte 0x01, letting the target override
    /// its existing table. <para>为 <see langword="true"/> 时发送模式字节 0x01，允许目标覆盖既有表。</para></param>
    /// <param name="confirmed">Set to <see langword="true"/> to confirm this partition-table
    /// write. <para>置为 <see langword="true"/> 以确认这次分区表写入。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path; an existing file is the image uploaded
    /// during initialization. <para>可选的编程器覆盖路径；文件存在时在初始化阶段上传该镜像。</para></param>
    /// <param name="initialization">Optional session startup requirements; <see langword="null"/> uses the
    /// streaming-DLOAD-only defaults. <para>可选的会话启动选项；为空时使用只启用流式 DLOAD 的默认值。</para></param>
    /// <returns>The target status plus a renderable summary. <para>目标状态与可读摘要。</para></returns>
    /// <exception cref="ArgumentNullException">The device argument is null. <para>设备参数为空。</para></exception>
    /// <exception cref="ArgumentException">The request was not confirmed, the table path is blank, or the
    /// startup options disable the streaming DLOAD switch. <para>请求未经确认、表路径为空白，或启动选项关闭了
    /// 流式 DLOAD 开关。</para></exception>
    /// <exception cref="FileNotFoundException">The table file does not exist. <para>表文件不存在。</para></exception>
    /// <exception cref="InvalidOperationException">The file leaves the protocol ceiling, or the session cannot
    /// be initialized as a Streaming DLOAD session. <para>文件长度超出协议上限，或会话无法初始化为 Streaming
    /// DLOAD 会话。</para></exception>
    internal static Task<EdlStreamingPartitionTableResult> SendPartitionTableAsync(DeviceTarget device,
        string tablePath, bool overrideExisting, bool confirmed, CancellationToken ct,
        string? programmerPath = null, EdlInitializationOptions? initialization = null)
    {
        RequirePartitionTableConfirmation(confirmed);
        var destination = RequireBoundedPartitionTableFile(tablePath);
        return RunStreamingAsync(device,
            streaming => SendPartitionTableCoreAsync(streaming, destination, overrideExisting, confirmed, ct), ct,
            programmerPath, initialization);
    }

    /// <summary>
    /// Runs the confirmation gate, the partition-table file pre-flight and the upload over an already
    /// negotiated streaming protocol.
    /// <para>在已协商的流式协议上执行分区表发送的确认门、文件预检与上传。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="tablePath">The raw partition table file path. <para>原始分区表文件路径。</para></param>
    /// <param name="overrideExisting"><see langword="true"/> sends the override mode
    /// byte. <para>为 <see langword="true"/> 时发送覆盖模式字节。</para></param>
    /// <param name="confirmed">Set to <see langword="true"/> to confirm this partition-table
    /// write. <para>置为 <see langword="true"/> 以确认这次分区表写入。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The target status plus a renderable summary. <para>目标状态与可读摘要。</para></returns>
    /// <exception cref="ArgumentException">The request was not confirmed, or the table path is blank.
    /// <para>请求未经确认，或表路径为空白。</para></exception>
    /// <exception cref="FileNotFoundException">The table file does not exist. <para>表文件不存在。</para></exception>
    /// <exception cref="InvalidOperationException">The file leaves the ceiling, its records are invalid, or
    /// the session has no usable geometry. <para>文件超出长度上限、记录无效，或会话没有可用几何信息。</para>
    /// </exception>
    internal static async Task<EdlStreamingPartitionTableResult> SendPartitionTableCoreAsync(
        StreamingProtocol streaming, string tablePath, bool overrideExisting, bool confirmed, CancellationToken ct)
    {
        RequirePartitionTableConfirmation(confirmed);
        var destination = RequireBoundedPartitionTableFile(tablePath);
        return await EdlStreamingFlashService.SendPartitionTableAsync(streaming, destination, overrideExisting,
            confirmed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Finalizes the current stream section with qclose, the teardown a streaming write always needs; the
    /// teardown only requires the completed hello exchange.
    /// <para>用 qclose 收束当前流式段，是流式写入之后的必备拆除步骤；拆除只要求会话完成过 hello。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path; an existing file is the image uploaded
    /// during initialization. <para>可选的编程器覆盖路径；文件存在时在初始化阶段上传该镜像。</para></param>
    /// <param name="initialization">Optional session startup requirements; <see langword="null"/> uses the
    /// streaming-DLOAD-only defaults. <para>可选的会话启动选项；为空时使用只启用流式 DLOAD 的默认值。</para></param>
    /// <returns>Whether the close was acknowledged. <para>关闭是否被确认。</para></returns>
    /// <exception cref="ArgumentNullException">The device argument is null. <para>设备参数为空。</para></exception>
    /// <exception cref="ArgumentException">The supplied startup options disable the streaming DLOAD switch.
    /// <para>所传入的启动选项关闭了流式 DLOAD 开关。</para></exception>
    /// <exception cref="InvalidOperationException">The session cannot be initialized as a Streaming DLOAD
    /// session. <para>会话无法初始化为 Streaming DLOAD 会话。</para></exception>
    internal static Task<EdlStreamingControlResult> CloseStreamAsync(DeviceTarget device, CancellationToken ct,
        string? programmerPath = null, EdlInitializationOptions? initialization = null)
        => RunStreamingAsync(device, streaming => CloseStreamCoreAsync(streaming, ct), ct,
            programmerPath, initialization);

    /// <summary>
    /// Issues the closing qclose over an already negotiated streaming protocol.
    /// <para>在已协商的流式协议上发出收尾的 qclose。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Whether the close was acknowledged. <para>关闭是否被确认。</para></returns>
    /// <exception cref="InvalidOperationException">The session never completed the hello exchange.
    /// <para>会话从未完成 hello。</para></exception>
    internal static Task<EdlStreamingControlResult> CloseStreamCoreAsync(StreamingProtocol streaming,
        CancellationToken ct) => EdlStreamingFlashService.CloseStreamAsync(streaming, ct);

    /// <summary>
    /// Resolves the startup requirements, opens the EDL session and initializes it inside the session
    /// callback, then screens out an unusable session before any streaming action runs.
    /// <para>解析启动选项、打开 EDL 会话并在会话回调里把它初始化到位，再在任何流式动作之前挡掉不可用的
    /// 会话。</para>
    /// </summary>
    /// <typeparam name="TResult">The result type the streaming action produces. <para>流式动作产生的结果
    /// 类型。</para></typeparam>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="operation">The action run on the negotiated streaming protocol. <para>在协商完成的流式
    /// 协议上执行的动作。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path; an existing file is the image uploaded
    /// during initialization. <para>可选的编程器覆盖路径；文件存在时在初始化阶段上传该镜像。</para></param>
    /// <param name="initialization">Optional session startup requirements; <see langword="null"/> uses the
    /// streaming-DLOAD-only defaults. <para>可选的会话启动选项；为空时使用只启用流式 DLOAD 的默认值。</para></param>
    /// <returns>The result produced by the streaming action. <para>流式动作产生的结果。</para></returns>
    private static Task<TResult> RunStreamingAsync<TResult>(DeviceTarget device,
        Func<StreamingProtocol, Task<TResult>> operation, CancellationToken ct, string? programmerPath,
        EdlInitializationOptions? initialization)
    {
        ArgumentNullException.ThrowIfNull(device);
        var options = RequireStreamDloadInitialization(initialization);
        return App.DeviceMonitor.RunEdlAsync(device.Endpoint, async session =>
        {
            var streaming = await InitializeStreamingSessionAsync(session, options, programmerPath, ct)
                .ConfigureAwait(false);
            return await operation(streaming).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// Initializes an EDL session that may never have completed a handshake and takes the streaming protocol
    /// back, screening the startup requirements before the device is touched.
    /// <para>把一个可能从未握手的 EDL 会话初始化到位并取回流式协议，在触碰设备之前先校验启动选项。</para>
    /// </summary>
    /// <param name="session">The EDL session handed out by the session cache, which may still be
    /// uninitialized. <para>会话缓存交出的 EDL 会话，可能尚未初始化。</para></param>
    /// <param name="initialization">Optional session startup requirements; <see langword="null"/> uses the
    /// streaming-DLOAD-only defaults. <para>可选的会话启动选项；为空时使用只启用流式 DLOAD 的默认值。</para></param>
    /// <param name="programmerPath">Optional programmer override path; the file is opened and uploaded only
    /// when it exists. <para>可选的编程器覆盖路径；只在文件存在时打开并上传。</para></param>
    /// <param name="ct">Cancellation threaded through the option check, the programmer open and the handshake.
    /// <para>贯穿选项校验、编程器打开与握手的取消令牌。</para></param>
    /// <returns>The negotiated streaming protocol. <para>已协商完成的流式协议实例。</para></returns>
    /// <exception cref="ArgumentNullException">The session argument is null. <para>会话参数为空。</para></exception>
    /// <exception cref="ArgumentException">The supplied startup options disable the streaming DLOAD switch.
    /// <para>所传入的启动选项关闭了流式 DLOAD 开关。</para></exception>
    /// <exception cref="InvalidOperationException">The session is still not the Streaming DLOAD backend once
    /// the handshake completes. <para>握手完成后会话仍不是 Streaming DLOAD 后端。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation lands during the handshake.
    /// <para>握手期间被取消。</para></exception>
    internal static async Task<StreamingProtocol> InitializeStreamingSessionAsync(EdlSession session,
        EdlInitializationOptions? initialization, string? programmerPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var options = RequireStreamDloadInitialization(initialization);
        using var programmer = EdlGptPartitionService.OpenProgrammer(programmerPath);
        await session.EnsureInitializedAsync(programmer, options, ct).ConfigureAwait(false);
        return EdlStreamingFlashService.RequireStreamingSession(session);
    }

    /// <summary>
    /// Screens the streaming startup requirements: <see langword="null"/> becomes the streaming-DLOAD-only
    /// defaults, an enabled instance passes unchanged, and a disabled one throws.
    /// <para>校验流式启动选项：为空时使用只启用流式 DLOAD 的默认值，已启用的实例原样通过，被关闭的实例
    /// 抛出异常。</para>
    /// </summary>
    /// <param name="initialization">The session startup requirements to screen; they may be
    /// <see langword="null"/>. <para>要校验的会话启动选项，可以为空。</para></param>
    /// <returns>The startup requirements that always select the streaming DLOAD
    /// backend. <para>总会话选择流式 DLOAD 后端的启动选项。</para></returns>
    /// <exception cref="ArgumentException">The supplied startup options disable the streaming DLOAD switch.
    /// <para>所传入的启动选项关闭了流式 DLOAD 开关。</para></exception>
    private static EdlInitializationOptions RequireStreamDloadInitialization(
        EdlInitializationOptions? initialization)
        => initialization switch
        {
            null => new EdlInitializationOptions { AllowStreamDload = true },
            { AllowStreamDload: true } => initialization,
            _ => throw new ArgumentException(
                "Streaming DLOAD NAND operations require EdlInitializationOptions.AllowStreamDload to be " +
                "enabled, so the supplied startup requirements were refused before any session opened. 流式 " +
                "DLOAD NAND 操作要求启用 EdlInitializationOptions.AllowStreamDload，因此所传入的启动选项在打开" +
                "任何会话之前就会被拒绝。", nameof(initialization))
        };

    /// <summary>
    /// Reads the device's own streaming partition table and resolves it by exact case-insensitive name,
    /// refusing before any NAND command when the name is absent and naming that partition in the message.
    /// <para>读取设备自身的流式分区表并按不区分大小写的精确名字解析条目；缺失时在任何 NAND 命令之前拒绝，
    /// 消息里点名该分区。</para>
    /// </summary>
    /// <param name="streaming">The negotiated streaming protocol. <para>已协商的流式协议实例。</para></param>
    /// <param name="partition">The partition name to resolve. <para>要解析的分区名。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The partition entry the device reported under that name. <para>设备按该名上报的分区
    /// 条目。</para></returns>
    private static async Task<StreamingPartitionEntry> RequireReportedPartitionAsync(StreamingProtocol streaming,
        string partition, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var entries = await EdlStreamingFlashService.ReadPartitionTableAsync(streaming,
            StreamingPartitionTableAddress, ct).ConfigureAwait(false);
        return EdlStreamingFlashService.ResolvePartition(entries, partition)
            ?? throw new InvalidOperationException(
                $"The Streaming DLOAD backend reported no partition named '{partition}'. 流式后端未上报名为 '{partition}' 的分区。");
    }

    /// <summary>
    /// The confirmation gate for write-capable actions: an unconfirmed request throws
    /// <see cref="ArgumentException"/> at a point where no byte has reached the wire yet.
    /// <para>写能力动作的确认门：未确认时抛出 <see cref="ArgumentException"/>，抛出点在任何传输写出之前。</para>
    /// </summary>
    /// <param name="confirmed">Whether the action was explicitly confirmed. <para>是否已显式确认该动作。</para></param>
    /// <param name="englishReason">The English action phrase used in the refusal. <para>拒绝文案里的英文动作
    /// 短语。</para></param>
    /// <param name="chineseReason">The Chinese action phrase used in the refusal. <para>拒绝文案里的中文动作
    /// 短语。</para></param>
    private static void RequireWriteConfirmation(bool confirmed, string englishReason, string chineseReason)
    {
        if (!confirmed)
        {
            throw new ArgumentException(
                $"{englishReason} requires explicit confirmation. {chineseReason}需要显式确认。",
                nameof(confirmed));
        }
    }

    /// <summary>
    /// The named confirmation gate for the bad-marker write, shared by the entry and the core over one
    /// refusal text.
    /// <para>标记坏块写入的具名确认门，入口与核心共用同一句拒绝文案。</para>
    /// </summary>
    /// <param name="confirmed">Whether the write was explicitly confirmed. <para>是否已显式确认这次写入。</para></param>
    private static void RequireMarkBadConfirmation(bool confirmed) =>
        RequireWriteConfirmation(confirmed, "Marking a NAND block bad", "标记坏块");

    /// <summary>
    /// The named confirmation gate for the un-mark whole-block erase.
    /// <para>解除标记（整块擦除）的具名确认门。</para>
    /// </summary>
    /// <param name="confirmed">Whether the erase was explicitly confirmed. <para>是否已显式确认这次擦除。</para></param>
    private static void RequireUnmarkBadConfirmation(bool confirmed) =>
        RequireWriteConfirmation(confirmed, "Un-marking a NAND block bad", "解除坏块标记");

    /// <summary>
    /// The named confirmation gate for the codeword page-read, whose sequence pulses the controller command
    /// registers.
    /// <para>码字状态页读的具名确认门；该序列会脉冲控制器命令寄存器。</para>
    /// </summary>
    /// <param name="confirmed">Whether this codeword read was explicitly confirmed. <para>是否已显式确认这次
    /// 码字读取。</para></param>
    private static void RequireCodewordConfirmation(bool confirmed) =>
        RequireWriteConfirmation(confirmed, "Pulsing the streaming NAND controller for a codeword read",
            "为码字读取脉冲流式 NAND 控制器");

    /// <summary>
    /// The named confirmation gate for the flash-mode transition.
    /// <para>闪存模式切换的具名确认门。</para>
    /// </summary>
    /// <param name="confirmed">Whether the transition was explicitly confirmed. <para>是否已显式确认这次切换。</para></param>
    private static void RequireFlashModeConfirmation(bool confirmed) =>
        RequireWriteConfirmation(confirmed, "Entering streaming flash mode", "进入流式闪存模式");

    /// <summary>
    /// The named confirmation gate for the partition-table write.
    /// <para>分区表写入的具名确认门。</para>
    /// </summary>
    /// <param name="confirmed">Whether the table write was explicitly confirmed. <para>是否已显式确认这次分区表写入。</para></param>
    private static void RequirePartitionTableConfirmation(bool confirmed) =>
        RequireWriteConfirmation(confirmed, "Writing the streaming partition table", "写入流式分区表");

    /// <summary>
    /// The trusted gate for secure-mode negotiation: only mode byte 0x01 passes, because an untrusted
    /// negotiation relaxes the target's execution environment.
    /// <para>安全模式协商的受信门：只放行模式字节 0x01，因为不受信协商会放宽目标的执行环境。</para>
    /// </summary>
    /// <param name="trusted">Whether the trusted mode byte 0x01 was selected. <para>是否选择受信模式字节
    /// 0x01。</para></param>
    private static void RequireTrustedMode(bool trusted)
    {
        if (!trusted)
        {
            throw new ArgumentException(
                "Streaming secure-mode negotiation is trusted-mode only, so the untrusted mode byte 0x00 is " +
                "refused before any byte reaches the wire. 流式安全模式协商只允许受信模式，因此不受信的 0x00 " +
                "模式字节在任何字节上线之前都会被拒绝。", nameof(trusted));
        }
    }

    /// <summary>
    /// The pre-flight for a partition-table file: the path is not blank, the file exists, and its declared
    /// length does not exceed the protocol ceiling.
    /// <para>分区表文件的预检：路径非空、文件存在，且声明长度不超过协议上限。</para>
    /// </summary>
    /// <param name="tablePath">The raw partition table file path. <para>原始分区表文件路径。</para></param>
    /// <returns>The full path of the validated file. <para>已通过校验的文件完整路径。</para></returns>
    private static string RequireBoundedPartitionTableFile(string tablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tablePath);
        if (!File.Exists(tablePath))
        {
            throw new FileNotFoundException($"Partition table file not found: {tablePath} 分区表文件不存在。",
                tablePath);
        }

        var declaredLength = new FileInfo(tablePath).Length;
        if (declaredLength > StreamingConstants.PartitionTableBytes)
        {
            throw new InvalidOperationException(
                $"The streaming partition table is capped at {StreamingConstants.PartitionTableBytes} bytes but " +
                $"'{tablePath}' holds {declaredLength}; the file was left untouched and no session was opened. " +
                $"流式分区表的长度上限是 {StreamingConstants.PartitionTableBytes} 字节，而该文件有 {declaredLength} " +
                "字节；文件保持不变，也未打开任何会话。");
        }

        return Path.GetFullPath(tablePath);
    }
}