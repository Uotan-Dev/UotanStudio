using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Firmware.VendorFirmware.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Resource and policy selection for a startup whose loader entry is chosen from the identity the
/// device reports during the Boot ROM handshake, instead of a caller-counted entry index.
/// <para>启动资源与策略选择；其加载器条目由设备在 Boot ROM 握手期间上报的身份决定，而不是由调用方手数索引决定。</para>
/// </summary>
/// <remarks>
/// <para>The handshake is the only device exchange performed before the entry is selected, so an
/// ambiguous or unmatched container stops the startup before any download agent byte is uploaded.</para>
/// <para>握手是条目选定前唯一的设备交互，因此容器匹配存在歧义或无匹配时，会在上传任何下载代理字节之前停止启动。</para>
/// </remarks>
public sealed record MtkAutoStartup
{
    /// <summary>Gets the parsed, hashed loader container the selection is evaluated against.
    /// <para>获取用于评估选择结果的、已解析并哈希的加载器容器。</para></summary>
    /// <value>The profile produced by <see cref="MtkLoaderProfile.FromFileAsync"/>.
    /// <para>由 <see cref="MtkLoaderProfile.FromFileAsync"/> 产生的配置。</para></value>
    public required MtkLoaderProfile Profile { get; init; }

    /// <summary>Gets the selected storage family the agent must report during discovery.
    /// <para>获取发现阶段代理必须上报的存储家族。</para></summary>
    /// <value>The explicit storage family; no fallback family is inferred.
    /// <para>显式的存储家族；不会推断任何回退家族。</para></value>
    public required MtkXFlashStorageKind StorageKind { get; init; }

    /// <summary>Gets the revision comparison policy applied after the hardware identity query.
    /// <para>获取在硬件身份查询之后应用的版本比较策略。</para></summary>
    /// <value>Exact or minimum-revision matching; the default is exact.
    /// <para>完全一致或最低版本匹配；默认完全一致。</para></value>
    public MtkDaIdentityMatch IdentityMatch { get; init; } = MtkDaIdentityMatch.Exact;

    /// <summary>Gets the optional explicitly extracted EMI parameters for the XFlash Boot ROM path.
    /// <para>获取 XFlash Boot ROM 路径可选的、显式提取的 EMI 参数。</para></summary>
    /// <value>The EMI source, or <see langword="null"/> when no explicit DRAM resource is supplied.
    /// <para>EMI 来源；未提供显式 DRAM 资源时为 <see langword="null"/>。</para></value>
    public MtkEmiParameters? EmiParameters { get; init; }

    /// <summary>Gets the optional XML protocol startup settings.
    /// <para>获取可选的 XML 协议启动设置。</para></summary>
    /// <value>Explicit XML settings, or <see langword="null"/> for the other protocols.
    /// <para>显式 XML 设置；其他协议使用 <see langword="null"/>。</para></value>
    public MtkXmlBootstrapOptions? Xml { get; init; }

    /// <summary>Gets the optional explicit non-IoT Legacy wire profile.
    /// <para>获取可选的显式非 IoT Legacy 线协议配置。</para></summary>
    /// <value>The Legacy profile required for Legacy startup; <see langword="null"/> for XML and XFlash.
    /// <para>Legacy 启动所需的配置；XML 与 XFlash 使用 <see langword="null"/>。</para></value>
    public MtkLegacyOptions? Legacy { get; init; }

    /// <summary>Gets the per-transfer timeout in milliseconds applied to the created session.
    /// <para>获取应用于新建会话、每次传输的超时时间（毫秒）。</para></summary>
    /// <value>The positive time budget applied to each data transfer.
    /// <para>应用于每次数据传输的正数时间预算。</para></value>
    public int TransferTimeoutMs { get; init; } = 5000;

    /// <summary>Gets the boot handshake's per-byte timeout in milliseconds.
    /// <para>获取 boot 握手的逐字节超时时间（毫秒）。</para></summary>
    /// <value>The positive per-byte budget used during the initial complemented-byte handshake.
    /// <para>在初始反码字节握手中使用的正数逐字节预算。</para></value>
    public int HandshakeTimeoutMs { get; init; } = 100;

    /// <summary>Gets whether the preloader watchdog is disabled after the identity query when the reference chip table records a sequence and the chip is not 64-bit.
    /// <para>获取是否在身份查询之后、参考芯片表记录了序列且芯片不是 64 位时禁用 preloader 看门狗。</para></summary>
    /// <value><see langword="true"/> by default, matching the upstream initialization; <see langword="false"/> skips every watchdog register write.
    /// <para>默认 <see langword="true"/>，与上游初始化一致；<see langword="false"/> 会跳过所有看门狗寄存器写入。</para></value>
    public bool DisablePreloaderWatchdog { get; init; } = true;

    /// <summary>Gets the UART log level sent to XFlash DA1.
    /// <para>获取发送给 XFlash DA1 的 UART 日志级别。</para></summary>
    /// <value>The numeric verbosity applied to the initialized agent.
    /// <para>应用于已初始化代理的数值化详细级别。</para></value>
    public uint DaLogLevel { get; init; } = 2;

    /// <summary>Gets the cooperative SLA exchange deadline in milliseconds.
    /// <para>获取协作式 SLA 交换的截止时间（毫秒）。</para></summary>
    /// <value>The positive whole-exchange budget for the SLA handshake; no retry occurs on expiry.
    /// <para>SLA 握手整个交换过程的正数时间预算；超时后不会重试。</para></value>
    public int SlaTimeoutMs { get; init; } = 60000;

    /// <summary>Gets the optional provider for the boot byte-stream profile.
    /// <para>获取 boot 字节流配置的可选提供程序。</para></summary>
    /// <value>The boot resource provider, or <see langword="null"/> when no boot resources are selected.
    /// <para>boot 资源提供程序；未选择任何 boot 资源时为 <see langword="null"/>。</para></value>
    public IMtkBootResourceProvider? BootResourceProvider { get; init; }

    /// <summary>Gets zero to two distinct boot resource kinds in caller-selected upload order.
    /// <para>获取零到两个互不相同的 boot 资源种类，按调用方选定的上传顺序排列。</para></summary>
    /// <value>The caller-selected resource kinds; empty when no boot resources are used.
    /// <para>调用方选定的资源种类；不使用 boot 资源时为空。</para></value>
    public IReadOnlyList<MtkBootResourceKind> BootResourceOrder { get; init; } = [];
}

/// <summary>
/// Identity-driven startup overloads that select the loader entry from the hardware identity the device
/// reports, and answer a Boot ROM SLA request when the caller supplies a key store.
/// <para>身份驱动的启动重载：由设备上报的硬件身份选定加载器条目，并在调用方提供密钥库时应答 Boot ROM SLA 请求。</para>
/// </summary>
public static partial class MtkBootstrap
{
    /// <summary>Handshakes over an already-open transport, selects the loader entry from the reported identity and starts the selected stock DA protocol. The session takes ownership of the transport, and every failure path disposes it.
    /// <para>在已打开的传输上握手，由上报的身份选定加载器条目并启动所选标准 DA 协议。会话接管传输所有权， 所有失败路径都会释放它。</para></summary>
    /// <param name="transport">The open transport selected by physical identity or explicit COM/TTY name.
    /// <para>按物理标识或显式 COM/TTY 名称选定的已打开传输。</para></param>
    /// <param name="protocol">The explicitly selected protocol; no detection or fallback is performed.
    /// <para>显式选定的协议；不进行探测或回退。</para></param>
    /// <param name="startup">Container, storage, identity policy and companion resources.
    /// <para>容器、存储、身份策略与伴随资源。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>A caller-owned storage-ready session; this workflow issues no storage write or erase command.
    /// <para>由调用方持有、已就绪存储的会话；本流程不下发任何存储写入或擦除命令。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> or <paramref name="startup"/> is null.
    /// <para><paramref name="transport"/> 或 <paramref name="startup"/> 为 null。</para></exception>
    /// <exception cref="InvalidOperationException">The container does not resolve to exactly one entry for the reported identity; no DA byte was uploaded.
    /// <para>容器未针对上报的身份解析为唯一条目；未上传任何 DA 字节。</para></exception>
    public static Task<MtkSession> MatchAsync(ICommTransport transport, MtkDaProtocol protocol,
        MtkAutoStartup startup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(startup);
        return MatchCoreAsync(protocol, startup, cancellationToken,
            async () => transport, ownsTransport: false);
    }

    /// <summary>Opens one explicit serial port, handshakes, selects the loader entry from the reported identity and starts the selected stock DA protocol.
    /// <para>打开某一个显式串口并握手，由上报的身份选定加载器条目，随后启动所选标准 DA 协议。</para></summary>
    /// <param name="serial">Explicit port and line settings; no probing or fallback is performed.
    /// <para>显式的端口与线路参数；不进行探测或回退。</para></param>
    /// <param name="protocol">The explicitly selected protocol; no detection or fallback is performed.
    /// <para>显式选定的协议；不进行探测或回退。</para></param>
    /// <param name="startup">Container, storage, identity policy and companion resources.
    /// <para>容器、存储、身份策略与伴随资源。</para></param>
    /// <param name="cancellationToken">The startup cancellation token.<para>启动过程取消令牌。</para></param>
    /// <returns>A caller-owned storage-ready session; this workflow issues no storage write or erase command.
    /// <para>由调用方持有、已就绪存储的会话；本流程不下发任何存储写入或擦除命令。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="serial"/> or <paramref name="startup"/> is null.
    /// <para><paramref name="serial"/> 或 <paramref name="startup"/> 为 null。</para></exception>
    /// <exception cref="InvalidOperationException">The container does not resolve to exactly one entry for the reported identity; no DA byte was uploaded.
    /// <para>容器未针对上报的身份解析为唯一条目；未上传任何 DA 字节。</para></exception>
    public static Task<MtkSession> MatchSerialAsync(SerialConnectionOptions serial, MtkDaProtocol protocol,
        MtkAutoStartup startup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(startup);
        serial.Validate();
        return MatchCoreAsync(protocol, startup, cancellationToken,
            async () => await serial.ConnectAsync(cancellationToken).ConfigureAwait(false), ownsTransport: true);
    }

    private static async Task<MtkSession> MatchCoreAsync(MtkDaProtocol protocol, MtkAutoStartup startup,
        CancellationToken cancellationToken, Func<Task<ICommTransport>> openTransport, bool ownsTransport)
    {
        ValidateStartup(startup);
        var transport = await openTransport().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The transport connector returned no transport.");
        MtkSession? session = null;
        try
        {
            session = new MtkSession(transport, startup.TransferTimeoutMs, startup.HandshakeTimeoutMs);
            if (transport is UsbDeviceTransport usb)
            {
                session.NoteUsbDescriptor(usb.Session.DeviceInfo);
            }

            var device = await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var options = BuildMatchedOptions(startup, device, protocol);
            return await InitializeCoreAsync(null, protocol, options, null, cancellationToken,
                startedSession: session, startedDevice: device).ConfigureAwait(false);
        }
        catch
        {
            if (session is null)
            {
                if (ownsTransport)
                {
                    transport.Dispose();
                }
            }
            else
            {
                session.Dispose();
            }

            throw;
        }
    }

    /// <summary>Handshakes a caller-owned session, selects the loader entry from the identity the device reports and starts the selected stock DA protocol on that same session.
    /// <para>在调用方持有的会话上握手，由设备上报的身份选定加载器条目，并在同一会话上启动所选标准 DA 协议。</para></summary>
    /// <remarks>
    /// <para>The session keeps the transport and timeout choices made when its lease was created, so the startup timeouts are validated here but never re-applied. A failure disposes the borrowed session because the protocol state after a partial startup is unknown.</para>
    /// <para>会话保留其租约创建时确定的传输与超时选择，因此这里的启动超时只做校验而不重新套用。 失败会释放所借用的会话，因为部分启动完成后的协议状态未知。</para></remarks>
    /// <param name="session">A session in the created state which the caller owns.
    /// <para>处于已创建状态、由调用方持有的会话。</para></param>
    /// <param name="protocol">The explicitly selected protocol; no detection or fallback is performed.
    /// <para>显式选定的协议；不进行探测或回退。</para></param>
    /// <param name="startup">Container, storage, identity policy and companion resources.
    /// <para>容器、存储、身份策略与伴随资源。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>操作取消令牌。</para></param>
    /// <returns>The same session once storage discovery has completed; no storage write or erase command is issued.
    /// <para>存储发现完成后的同一会话；不下发任何存储写入或擦除命令。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="startup"/> is null.
    /// <para><paramref name="session"/> 或 <paramref name="startup"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException">The identity or storage policy is not a defined value.
    /// <para>身份或存储策略不是已定义的值。</para></exception>
    /// <exception cref="InvalidOperationException">The container does not resolve to exactly one entry for the reported identity; no DA byte was uploaded.
    /// <para>容器未针对上报的身份解析为唯一条目；未上传任何 DA 字节。</para></exception>
    public static async Task<MtkSession> MatchAsync(MtkSession session, MtkDaProtocol protocol,
        MtkAutoStartup startup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(startup);
        ValidateStartup(startup);
        var device = await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var options = BuildMatchedOptions(startup, device, protocol);
        return await InitializeCoreAsync(null, protocol, options, null, cancellationToken,
            startedSession: session, startedDevice: device).ConfigureAwait(false);
    }

    private static void ValidateStartup(MtkAutoStartup startup)
    {
        ArgumentNullException.ThrowIfNull(startup.Profile);
        if (!Enum.IsDefined(startup.IdentityMatch) || !Enum.IsDefined(startup.StorageKind))
        {
            throw new ArgumentException("The startup identity or storage policy is not supported.", nameof(startup));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startup.TransferTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startup.HandshakeTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startup.SlaTimeoutMs);
    }

    private static MtkBootstrapOptions BuildMatchedOptions(MtkAutoStartup startup, MtkDeviceInfo device,
        MtkDaProtocol protocol)
    {
        var selection = startup.Profile.SelectEntry(device, startup.IdentityMatch);
        if (selection.Decision != MtkProfileDecision.SingleMatch
            || selection.EntryIndex is null
            || selection.StageOneRegionIndex is null
            || selection.StageTwoRegionIndex is null)
        {
            throw new InvalidOperationException(
                $"The loader container did not resolve to one entry for hardware code 0x{device.HardwareCode:X4}: {string.Join("; ", selection.Reasons)}.");
        }

        return MtkBootstrapOptions.From(startup.Profile, selection, startup.StorageKind, startup.IdentityMatch) with
        {
            StageTwoContent = protocol == MtkDaProtocol.Legacy
                ? MtkDaRegionContent.Complete
                : MtkDaRegionContent.WithoutSignatureSuffix,
            Xml = startup.Xml,
            Legacy = startup.Legacy,
            EmiParameters = startup.EmiParameters,
            DaLogLevel = startup.DaLogLevel,
            TransferTimeoutMs = startup.TransferTimeoutMs,
            HandshakeTimeoutMs = startup.HandshakeTimeoutMs,
            DisablePreloaderWatchdog = startup.DisablePreloaderWatchdog,
            SlaTimeoutMs = startup.SlaTimeoutMs,
            BootResourceProvider = startup.BootResourceProvider,
            BootResourceOrder = startup.BootResourceOrder
        };
    }
}