using UotanStudio.Comm.Protocols.Mtk.Upload;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Firmware.VendorFirmware.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// One explicit loader-to-storage startup path without loader fallback, patching or authorization bypass.
/// <para>一条从加载器到存储的显式启动路径，不进行加载器回退、镜像修补或授权绕过。</para>
/// </summary>
public static partial class MtkBootstrap
{
    /// <summary>
    /// Takes ownership of a selected open transport, validates resources and initializes the selected stock DA protocol.
    /// The session is returned only after storage discovery; all failures dispose the owned transport.
    /// Null argument checks occur before ownership transfer.
    /// <para>接管所选定的已打开传输，校验资源并初始化所选标准 DA 协议。仅在存储发现完成后才返回会话；
    /// 所有失败路径都会释放所接管的传输。空值参数检查发生在所有权转移之前。</para>
    /// </summary>
    /// <param name="transport">
    /// The open transport selected by physical identity or explicit COM/TTY name.
    /// <para>按物理标识或显式 COM/TTY 名称选定的已打开传输。</para>
    /// </param>
    /// <param name="protocol">The explicitly selected protocol; no detection or fallback is performed.
    /// <para>显式选定的协议；不进行探测或回退。</para></param>
    /// <param name="options">
    /// Explicit resource selections and identity policy.
    /// <para>显式的资源选择与身份策略。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The operation cancellation token.
    /// <para>操作取消令牌。</para>
    /// </param>
    /// <returns>
    /// A caller-owned storage-ready session; this workflow issues no storage write or erase command.
    /// <para>由调用方持有、已就绪存储的会话；本流程不下发任何存储写入或擦除命令。</para>
    /// </returns>
    public static Task<MtkSession> InitializeAsync(ICommTransport transport, MtkDaProtocol protocol, MtkBootstrapOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        return InitializeCoreAsync(transport, protocol, options, null, cancellationToken);
    }

    /// <summary>
    /// Preflights and retains the selected loader before opening one explicit serial port, then starts the selected DA.
    /// <para>在打开某一个显式串口之前预检并保留所选加载器，随后启动所选 DA。</para>
    /// </summary>
    /// <param name="serial">
    /// Explicit port and line settings; no probing or fallback is performed.
    /// <para>显式的端口与线路参数；不进行探测或回退。</para>
    /// </param>
    /// <param name="protocol">The explicitly selected protocol; no detection or fallback is performed.
    /// <para>显式选定的协议；不进行探测或回退。</para></param>
    /// <param name="options">
    /// Explicit loader, stages, identity and storage selections.
    /// <para>显式的加载器、阶段、身份与存储选择。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The startup cancellation token.
    /// <para>启动过程取消令牌。</para>
    /// </param>
    /// <returns>
    /// A caller-owned storage-ready session. Starting agents is not inherently side-effect-free.
    /// <para>由调用方持有、已就绪存储的会话。启动代理本身并非无副作用。</para>
    /// </returns>
    public static Task<MtkSession> ConnectSerialAsync(SerialConnectionOptions serial, MtkDaProtocol protocol, MtkBootstrapOptions options,
        CancellationToken cancellationToken = default) =>
        ConnectSerialCoreAsync(serial, protocol, options, static async (settings, token) =>
            await settings.ConnectAsync(token).ConfigureAwait(false), cancellationToken);

    internal static Task<MtkSession> ConnectSerialCoreAsync(SerialConnectionOptions serial, MtkDaProtocol protocol, MtkBootstrapOptions options,
        Func<SerialConnectionOptions, CancellationToken, Task<ICommTransport>> connect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connect);
        serial.Validate();
        return InitializeCoreAsync(null, protocol, options, token => connect(serial, token), cancellationToken);
    }

    private static async Task<MtkSession> InitializeCoreAsync(ICommTransport? transport, MtkDaProtocol protocol, MtkBootstrapOptions options,
        Func<CancellationToken, Task<ICommTransport>>? connect, CancellationToken cancellationToken,
        MtkSession? startedSession = null, MtkDeviceInfo? startedDevice = null)
    {
        MtkSession? session = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateProtocolOptions(protocol, options);
            var resourceOrder = CaptureBootResourceOrder(options);
            MtkSession.ValidateSlaTimeout(options.SlaTimeoutMs);
            if (!Enum.IsDefined(options.IdentityMatch) || !Enum.IsDefined(options.StorageKind))
            {
                throw new ArgumentException("The startup identity/storage policy is not supported.", nameof(options));
            }
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.TransferTimeoutMs);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.HandshakeTimeoutMs);
            using var loader = MtkDaFile.Open(options.LoaderPath, options.DirectoryLayout, cancellationToken);
            if (options.StageOneRegionIndex == options.StageTwoRegionIndex)
            {
                throw new ArgumentException("DA1 and DA2 must be explicitly selected distinct regions.", nameof(options));
            }
            using var rawStageOne = loader.OpenRegion(options.EntryIndex, options.StageOneRegionIndex, MtkDaRegionContent.Complete);
            using var rawStageTwo = loader.OpenRegion(options.EntryIndex, options.StageTwoRegionIndex, options.StageTwoContent);
            Stream stageOne = rawStageOne;
            Stream stageTwo = rawStageTwo;
            var entry = loader.Metadata.Entries[options.EntryIndex];
            var first = entry.Regions[options.StageOneRegionIndex];
            var second = entry.Regions[options.StageTwoRegionIndex];
            ValidateRegion(first.LoadAddress, stageOne.Length, wordLength: true);
            ValidateRegion(second.LoadAddress, stageTwo.Length, wordLength: false);
            if (entry.HardwareCode == 0 || entry.SoftwareVersion is null)
            {
                throw new InvalidDataException("The selected entry lacks an explicit hardware/software identity.");
            }
            if (options.EmiParameters is { Length: <= 0 })
            {
                throw new InvalidDataException("The selected EMI payload is empty.");
            }
            if (protocol == MtkDaProtocol.Legacy)
            {
                _ = options.Legacy!.BuildRuntime(entry.HardwareCode, new(0, 0));
            }

            MtkDeviceInfo device;
            if (startedSession is not null)
            {
                session = startedSession;
                device = startedDevice
                    ?? throw new ArgumentException(
                        "A pre-started session must carry the identity queried from the device.", nameof(startedSession));
            }
            else
            {
                if (transport is null)
                {
                    transport = await connect!(cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The serial connector returned no transport.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!transport.IsConnected)
                {
                    throw new InvalidOperationException("The selected transport is not connected.");
                }

                session = new MtkSession(transport, options.TransferTimeoutMs, options.HandshakeTimeoutMs);
                device = await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }

            ValidateIdentity(entry, device, options.IdentityMatch);
            if (protocol == MtkDaProtocol.Legacy)
            {
                await session.QueryLegacyBootVersionsAsync(cancellationToken).ConfigureAwait(false);
            }
            await UploadBootResourcesAsync(session, device, options.BootResourceProvider,
                ResolveBootResourceOrder(resourceOrder, options.BootResourceProvider, device),
                cancellationToken).ConfigureAwait(false);
            await session.UploadDownloadAgentAsync(stageOne, first.LoadAddress, checked((int)stageOne.Length),
                checked((int)first.SignatureLength), cancellationToken: cancellationToken).ConfigureAwait(false);
            await session.StartDownloadAgentAsync(cancellationToken).ConfigureAwait(false);
            if (protocol == MtkDaProtocol.Legacy)
            {
                await session.InitializeLegacyAgentAsync(stageTwo, second.LoadAddress, checked((int)stageTwo.Length),
                    options.StorageKind, options.Legacy!, options.EmiParameters, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                session.NoteUploadStage(MtkDaUploadStage.StorageDiscovered);
                session.NoteUploadStage(MtkDaUploadStage.Completed);
                return session;
            }
            if (protocol == MtkDaProtocol.Xml)
            {
                await InitializeXmlAsync(session, stageTwo, second.LoadAddress, options, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                session.NoteUploadStage(MtkDaUploadStage.StorageDiscovered);
                session.NoteUploadStage(MtkDaUploadStage.Completed);
                return session;
            }
            await session.InitializeXFlashAsync(options.DaLogLevel, cancellationToken).ConfigureAwait(false);
            var connection = await session.GetXFlashConnectionAgentAsync(cancellationToken).ConfigureAwait(false);
            if (connection == MtkXFlashConnectionAgent.BootRom && options.EmiParameters is null)
            {
                throw new InvalidOperationException("The Boot ROM path requires explicitly selected EMI parameters before DA2.");
            }
            if (options.EmiParameters is { } emi)
            {
                using var parameters = emi.OpenRead();
                await session.InitializeXFlashExternalRamAsync(parameters, emi.Length, cancellationToken).ConfigureAwait(false);
            }
            await session.UploadXFlashStageTwoAsync(stageTwo, second.LoadAddress, checked((int)stageTwo.Length),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await session.AuthenticateXFlashStageTwoAsync(options.DaSlaResponder, options.SlaTimeoutMs,
                cancellationToken).ConfigureAwait(false);
            if (await session.DiscoverXFlashStorageAsync(options.StorageKind, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException("The selected storage family was not reported by the agent.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            session.NoteUploadStage(MtkDaUploadStage.StorageDiscovered);
            session.NoteUploadStage(MtkDaUploadStage.Completed);
            return session;
        }
        catch
        {
            if (session is null)
            {
                transport?.Dispose();
            }
            else
            {
                session.Dispose();
            }
            throw;
        }
    }

    private static void ValidateProtocolOptions(MtkDaProtocol protocol, MtkBootstrapOptions options)
    {
        if (!Enum.IsDefined(protocol))
        {
            throw new ArgumentOutOfRangeException(nameof(protocol));
        }
        if (protocol == MtkDaProtocol.Legacy)
        {
            if (options.Legacy is null || options.Xml is not null
                || options.StorageKind is not (MtkXFlashStorageKind.Emmc or MtkXFlashStorageKind.Nor or MtkXFlashStorageKind.Nand)
                || options.StageTwoContent != MtkDaRegionContent.Complete || options.DaSlaResponder is not null)
            {
                throw new ArgumentException("Legacy startup requires an explicit eMMC, NOR or NAND profile and complete DA2; XML settings and XFlash/XML DA2 SLA responders are not applicable.", nameof(options));
            }
            options.Legacy.Validate();
            MtkSession.ValidateLegacyEmi(options.EmiParameters);
            return;
        }
        if (options.Legacy is not null)
        {
            throw new ArgumentException("Legacy settings cannot be used with the selected protocol.", nameof(options));
        }
        if (protocol == MtkDaProtocol.XFlash)
        {
            if (options.Xml is not null)
            {
                throw new ArgumentException("XML settings cannot be used with XFlash.", nameof(options));
            }
            return;
        }
        if (options.Xml is null || options.EmiParameters is not null
            || options.StorageKind is not (MtkXFlashStorageKind.Emmc or MtkXFlashStorageKind.Ufs))
        {
            throw new ArgumentException("XML startup requires XML settings and eMMC/UFS selection; DRAM resources use its host file store, not XFlash EMI.", nameof(options));
        }
        options.Xml.Validate();
    }

    private static byte[] ReadAll(Stream image)
    {
        image.Position = 0;
        using var buffer = new MemoryStream();
        image.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void ValidateRegion(uint address, long length, bool wordLength)
    {
        if (length <= 0 || length > 64 * 1024 * 1024 || (address & 3) != 0
            || (wordLength && ((length & 1) != 0 || (ulong)address + (ulong)length > (ulong)uint.MaxValue + 1)))
        {
            throw new InvalidDataException("A selected DA region is empty, oversized, unaligned or outside the supported load range.");
        }
    }

    private static void ValidateIdentity(MtkDaEntry entry, MtkDeviceInfo device, MtkDaIdentityMatch policy)
    {
        var revisionsMatch = policy == MtkDaIdentityMatch.Exact
            ? entry.HardwareVersion == device.HardwareVersion && entry.SoftwareVersion == device.SoftwareVersion
            : entry.HardwareVersion <= device.HardwareVersion && entry.SoftwareVersion <= device.SoftwareVersion;
        if (entry.HardwareCode != device.HardwareCode || entry.HardwareSubCode != device.HardwareSubCode || !revisionsMatch)
        {
            throw new InvalidOperationException("The selected loader entry does not match the queried hardware identity and revision policy.");
        }
    }
}