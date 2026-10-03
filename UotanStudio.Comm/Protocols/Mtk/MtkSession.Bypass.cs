using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Exploit;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Explicitly authorised Boot ROM and preloader bypass transport for an opened MTK session.
/// <para>在已打开的 MTK 会话上执行显式授权的 Boot ROM 与 preloader 绕过传输。</para>
/// </summary>
/// <remarks>
/// <para>The reference client performs the same transition in three visible steps: run a stage-one payload through
/// an exploit driver, re-handshake the port, and continue with the download-agent upload only if the target now
/// accepts it (<c>mtkclient/Library/mtk_class.py</c>, lines 194-213). This partial keeps those steps in the open
/// and adds a fourth: the target configuration word is re-queried and reported, so the caller decides whether to
/// continue instead of inheriting an assumption. Nothing here is reachable from <c>InitializeAsync</c>, the DA
/// upload paths, or any bootstrap overload; a caller must construct <see cref="MtkBypassOptions"/> and set both
/// permission flags.</para>
/// <para>参考实现的这一转换有三个可见步骤：通过漏洞驱动运行阶段一负载、重新握手端口、只有在目标现在接受时才继续上传下载
/// 代理（<c>mtkclient/Library/mtk_class.py</c> 第 194-213 行）。本分部文件把这些步骤公开保留，并增加第四步：重新查询并上报
/// 目标配置字，因此由调用方决定是否继续，而不是继承某个假设。<c>InitializeAsync</c>、DA 上传路径以及任何 bootstrap 重载都
/// 不会走到这里；调用方必须自行构造 <see cref="MtkBypassOptions"/> 并同时设置两个授权标志。</para>
/// </remarks>
public sealed partial class MtkSession
{
    /// <summary>Gets the chip table entry for the identity this session already queried.
    /// <para>获取本会话已查询身份对应的芯片表条目。</para></summary>
    /// <value>
    /// The documented entry, or <see langword="null"/> before initialization or when the reference table records no
    /// entry for the reported hardware code. Reading this property contacts no device.
    /// <para>记录的条目；初始化之前，或参考表未记录上报硬件码时返回 <see langword="null"/>。读取该属性不会联系设备。</para>
    /// </value>
    public MtkBromChipProfile? BypassChipProfile => DeviceInfo is { } device
        ? MtkBromChipTable.Find(device.HardwareCode)
        : null;

    /// <summary>Gets the frozen result of the last successful bypass on this session.
    /// <para>获取本会话最近一次成功绕过的定格结果。</para></summary>
    /// <value>
    /// The reported security state, or <see langword="null"/> when no bypass has succeeded on this session.
    /// <para>上报的安全状态；本会话没有成功绕过时为 <see langword="null"/>。</para>
    /// </value>
    public MtkBypassResult? LastBypassResult { get; private set; }

    /// <summary>Runs the stage-one exploit payload the caller asked for and re-queries the target security word.
    /// <para>运行调用方要求的阶段一漏洞负载，并重新查询目标安全字。</para></summary>
    /// <param name="options">The explicit permission, family and payload selection.
    /// <para>显式的授权、家族与负载选择。</para></param>
    /// <param name="cancellationToken">Cancellation for the whole transition.
    /// <para>整个转换过程的取消令牌。</para></param>
    /// <returns>The payload outcome together with the refreshed identity and security bits.
    /// <para>负载执行结果，以及刷新后的身份与安全位。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.
    /// <para><paramref name="options"/> 为 null。</para></exception>
    /// <exception cref="InvalidOperationException">Either permission flag is <see langword="false"/>, or the
    /// session is not in the bootloader stage; no byte is written.
    /// <para>任一授权标志为 <see langword="false"/>，或会话未处于 bootloader 阶段；不写出任何字节。</para></exception>
    /// <exception cref="NotSupportedException">The hardware code is absent from the reference chip table, its entry
    /// lacks a register the selected family reads, the family is documented but unimplemented, no control seam is
    /// available for a family that needs one, or the requested payload is not vendored.
    /// <para>硬件码不在参考芯片表内、条目缺少所选家族读取的寄存器、家族有记录但未实现、需要控制接缝的家族没有可用接缝，
    /// 或请求的负载未再分发。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The payload exceeds
    /// <see cref="MtkBypassOptions.MaximumPayloadLength"/>. <para>负载超过
    /// <see cref="MtkBypassOptions.MaximumPayloadLength"/>。</para></exception>
    /// <exception cref="MtkProtocolException">The target refused the exploit sequence or did not acknowledge the
    /// payload; the session faults and must not be replayed.
    /// <para>目标拒绝漏洞序列或不确认负载；会话进入故障状态，不得重放。</para></exception>
    public Task<MtkBypassResult> RunBypassAsync(MtkBypassOptions options,
        CancellationToken cancellationToken = default) => RunBypassCoreAsync(options, null, cancellationToken);

    /// <summary>Runs a bypass with a caller-supplied stage-one payload instead of the vendored catalog.
    /// <para>使用调用方提供的阶段一负载执行绕过，而不是使用再分发目录中的负载。</para></summary>
    /// <param name="options">The explicit permission, family and payload selection.
    /// <para>显式的授权、家族与负载选择。</para></param>
    /// <param name="payload">The raw payload bytes to prepare and upload.
    /// <para>要准备并上传的原始负载字节。</para></param>
    /// <param name="cancellationToken">Cancellation for the whole transition.
    /// <para>整个转换过程的取消令牌。</para></param>
    /// <returns>The payload outcome together with the refreshed identity and security bits.
    /// <para>负载执行结果，以及刷新后的身份与安全位。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.
    /// <para><paramref name="options"/> 为 null。</para></exception>
    /// <exception cref="InvalidOperationException">Either permission flag is <see langword="false"/>, or the
    /// session is not in the bootloader stage; no byte is written.
    /// <para>任一授权标志为 <see langword="false"/>，或会话未处于 bootloader 阶段；不写出任何字节。</para></exception>
    /// <exception cref="NotSupportedException">The chip entry lacks a register the selected family reads, or the
    /// family needs a control seam the transport does not expose.
    /// <para>芯片条目缺少所选家族读取的寄存器，或家族需要的控制接缝未被该传输暴露。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The payload exceeds
    /// <see cref="MtkBypassOptions.MaximumPayloadLength"/>. <para>负载超过
    /// <see cref="MtkBypassOptions.MaximumPayloadLength"/>。</para></exception>
    /// <exception cref="MtkProtocolException">The target refused the exploit sequence or did not acknowledge the
    /// payload; the session faults and must not be replayed.
    /// <para>目标拒绝漏洞序列或不确认负载；会话进入故障状态，不得重放。</para></exception>
    public Task<MtkBypassResult> RunBypassAsync(MtkBypassOptions options, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) => RunBypassCoreAsync(options, payload, cancellationToken);

    /// <summary>Runs a bypass using the payload selection carried by <paramref name="options"/>.
    /// <para>使用 <paramref name="options"/> 携带的负载选择执行绕过。</para></summary>
    /// <param name="options">The explicit permission, family and payload selection.
    /// <para>显式的授权、家族与负载选择。</para></param>
    /// <param name="payloadOverride">Raw bytes to use instead of the catalog, or <see langword="null"/>.
    /// <para>代替再分发目录使用的原始字节，可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancellation for the whole transition.
    /// <para>整个转换过程的取消令牌。</para></param>
    /// <returns>The payload outcome together with the refreshed identity and security bits.
    /// <para>负载执行结果，以及刷新后的身份与安全位。</para></returns>
    private async Task<MtkBypassResult> RunBypassCoreAsync(MtkBypassOptions options,
        ReadOnlyMemory<byte>? payloadOverride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!options.AllowExploitBypass)
        {
            throw new InvalidOperationException(
                "The exploit bypass subsystem is not authorised; set the bypass permission flag explicitly.");
        }

        if (!options.AllowVolatileMemoryWrite)
        {
            throw new InvalidOperationException(
                "Every documented bypass family writes target registers or descriptors; authorise the volatile memory write explicitly.");
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.Bootloader);
            var device = DeviceInfo ?? throw new InvalidOperationException(
                "Query the boot identity before requesting a bypass.");
            var profile = MtkBromChipTable.Find(device.HardwareCode) ?? throw new NotSupportedException(
                $"The reference chip table records no Boot ROM sites for hardware code 0x{device.HardwareCode:X4}; no exploit family is attempted.");
            var family = options.Family ?? SelectSoleDocumentedFamily(profile);
            var controlChannel = TryCreateControlChannel();
            var prepared = family == MtkExploitFamily.KamakiriPreloader
                ? default
                : MtkExploitPayloadPreparer.Prepare(
                    payloadOverride ?? SelectPayload(profile, options), profile, options.MaximumPayloadLength);
            var configurationBefore = device.TargetConfiguration;
            MtkExploitRunner.EnsureCanDrive(family, profile, controlChannel, prepared.Length);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var report = await MtkExploitRunner.RunAsync(family, profile, new BypassByteChannel(this),
                    controlChannel, prepared.Bytes, options.TimeoutMs, cancellationToken).ConfigureAwait(false);
                var refreshed = await RehandshakeAndRequeryAsync(options.TimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                var result = new MtkBypassResult(family, profile.HardwareCode, profile.Name, report.LandingAddress,
                    report.PayloadLength, report.ControlProbeStalled, configurationBefore,
                    refreshed.TargetConfiguration, refreshed);
                LastBypassResult = result;
                _evidence.Add(new ProtocolEvidence
                {
                    Protocol = "mtk",
                    Phase = "exploit-bypass",
                    Outcome = CommandOutcomeKind.Completed,
                    Detail = $"{family} accepted {report.PayloadLength} byte(s) at 0x{report.LandingAddress:X8}; "
                        + $"target configuration {configurationBefore:X8} to {refreshed.TargetConfiguration:X8}, "
                        + $"signature checks open: {result.SignatureChecksOpen}."
                });
                return result;
            }
            catch (Exception error)
            {
                _evidence.Add(new ProtocolEvidence
                {
                    Protocol = "mtk",
                    Phase = "exploit-bypass",
                    Outcome = ClassifyUploadOutcome(error),
                    Detail = $"{family} bypass failed: {error.Message}"
                });
                _state = MtkSessionState.Faulted;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Selects the only exploit family whose recorded registers are complete for this chip.
    /// <para>挑选本芯片记录寄存器齐全的唯一个漏洞家族。</para></summary>
    /// <param name="profile">The chip entry from the reference table. <para>参考表中的芯片条目。</para></param>
    /// <returns>The single implemented family the entry supports. <para>该条目支持的唯一已实现家族。</para></returns>
    /// <exception cref="NotSupportedException">No family, or more than one family, is documented for the entry; the
    /// caller must then name one. <para>该条目没有任何家族，或有多个家族被记录；此时必须由调用方指名。</para></exception>
    private static MtkExploitFamily SelectSoleDocumentedFamily(MtkBromChipProfile profile)
    {
        var candidates = profile.DocumentedFamilies.Where(MtkExploitRunner.IsImplemented).ToArray();
        return candidates switch
        {
            [var single] => single,
            [] => throw new NotSupportedException(
                $"The reference table records no implemented exploit family for hardware code 0x{profile.HardwareCode:X4}; name a family explicitly."),
            _ => throw new NotSupportedException(
                $"Hardware code 0x{profile.HardwareCode:X4} documents {candidates.Length} selectable exploit families ({string.Join(", ", candidates)}); name one explicitly instead of relying on a preference order.")
        };
    }

    /// <summary>Resolves the payload selection against the vendored payload catalog.
    /// <para>在再分发负载目录中解析负载选择。</para></summary>
    /// <param name="profile">The chip entry, consulted for a per-chip payload name.
    /// <para>芯片条目，用于查询按芯片负载名称。</para></param>
    /// <param name="options">The caller's payload selection. <para>调用方的负载选择。</para></param>
    /// <returns>The vendored bytes. <para>再分发的字节。</para></returns>
    /// <exception cref="NotSupportedException">The requested payload is not vendored.
    /// <para>请求的负载未再分发。</para></exception>
    private static ReadOnlyMemory<byte> SelectPayload(MtkBromChipProfile profile, MtkBypassOptions options)
    {
        if (options.Payload == MtkExploitPayloadSource.ChipProfilePayload)
        {
            return MtkExploitPayloadCatalog.GetChipPayload(profile.ChipPayloadName) ?? throw new NotSupportedException(
                profile.ChipPayloadName is null
                    ? $"The reference table records no per-chip payload for hardware code 0x{profile.HardwareCode:X4}; select a generic payload explicitly."
                    : $"The per-chip payload {profile.ChipPayloadName} is not vendored; select a generic payload explicitly.");
        }

        return MtkExploitPayloadCatalog.GetGenericPayload(options.Payload) ?? throw new NotSupportedException(
            $"The payload selection {options.Payload} has no vendored bytes.");
    }

    /// <summary>Binds the exploit control seam to the USB session the transport already owns.
    /// <para>把漏洞控制接缝绑定到传输已经拥有的 USB 会话。</para></summary>
    /// <returns>The control seam, or <see langword="null"/> when the transport is not a USB device session.
    /// <para>控制接缝；传输不是 USB 设备会话时返回 <see langword="null"/>。</para></returns>
    private IMtkExploitControlChannel? TryCreateControlChannel() =>
        Transport is UsbDeviceTransport usb ? new UsbControlChannel(usb.Session) : null;

    /// <summary>Repeats the complemented-byte handshake and re-reads the identity and security word.
    /// <para>重复反码字节握手，并重新读取身份与安全字。</para></summary>
    /// <param name="timeoutMs">Budget for each exchange. <para>每次交换的毫秒预算。</para></param>
    /// <param name="cancellationToken">Cancellation for the sequence. <para>序列的取消令牌。</para></param>
    /// <returns>The refreshed identity. <para>刷新后的设备身份。</para></returns>
    private async Task<MtkDeviceInfo> RehandshakeAndRequeryAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        foreach (var value in new byte[] { 0xA0, 0x0A, 0x50, 0x05 })
        {
            await WriteAsync([value], timeoutMs, cancellationToken).ConfigureAwait(false);
            var reply = await ReadAsync(1, timeoutMs, cancellationToken).ConfigureAwait(false);
            if (reply[0] != (byte)(value ^ 0xFF))
            {
                throw new MtkProtocolException(
                    "The boot handshake after the stage-one payload did not echo the complemented byte.");
            }
        }

        await EchoAsync([0xFD], cancellationToken).ConfigureAwait(false);
        var hardware = await ReadAsync(4, timeoutMs, cancellationToken).ConfigureAwait(false);
        await EchoAsync([0xFC], cancellationToken).ConfigureAwait(false);
        var versions = await ReadAsync(8, timeoutMs, cancellationToken).ConfigureAwait(false);
        await EchoAsync([0xD8], cancellationToken).ConfigureAwait(false);
        var configuration = await ReadAsync(6, timeoutMs, cancellationToken).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadUInt16BigEndian(configuration.AsSpan(4));
        CheckStatus(status, "post-exploit target configuration query");
        ThrowIfDisposed();
        var refreshed = new MtkDeviceInfo(
            BinaryPrimitives.ReadUInt16BigEndian(hardware),
            BinaryPrimitives.ReadUInt16BigEndian(versions),
            BinaryPrimitives.ReadUInt16BigEndian(versions.AsSpan(2)),
            BinaryPrimitives.ReadUInt16BigEndian(versions.AsSpan(4)),
            BinaryPrimitives.ReadUInt32BigEndian(configuration));
        DeviceInfo = refreshed;
        _state = MtkSessionState.Bootloader;
        return refreshed;
    }

    private sealed class BypassByteChannel(MtkSession session) : IMtkExploitByteChannel
    {
        public Task WriteAsync(byte[] data, int timeoutMs, CancellationToken cancellationToken) =>
            session.WriteAsync(data, timeoutMs, cancellationToken);

        public Task<byte[]> ReadAsync(int count, int timeoutMs, CancellationToken cancellationToken) =>
            session.ReadAsync(count, timeoutMs, cancellationToken);
    }

    private sealed class UsbControlChannel(IUsbDeviceSession device) : IMtkExploitControlChannel
    {
        public async Task<int> ControlTransferAsync(MtkUsbControlRequest request, byte[]? buffer, int offset,
            int length, int timeoutMs, CancellationToken cancellationToken)
        {
            var setup = new UsbSetupPacket
            {
                RequestType = request.RequestType,
                Request = request.Request,
                Value = request.Value,
                Index = request.Index,
                Length = (ushort)length
            };
            var transferred = await device.ControlTransferAsync(setup, buffer, offset, length, timeoutMs,
                cancellationToken).ConfigureAwait(false);
            if (transferred < 0 || transferred > length)
            {
                throw new IOException(
                    $"The vendor control request 0x{request.Request:X2} transferred {transferred} of {length} byte(s).");
            }

            return transferred;
        }
    }
}