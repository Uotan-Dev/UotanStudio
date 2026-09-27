using System.Globalization;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk.Storage;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Gets validated XML geometry after discovery, or null if unavailable or the session faulted.
    /// <para>获取发现阶段之后已校验的 XML 几何信息；若不可用或会话已故障则为 null。</para>
    /// </summary>
    /// <remarks>Device-reported geometry is not proof of authentication, successful I/O or identity.
    /// <para>设备上报的几何信息并不能证明认证通过、I/O 成功或设备身份。</para></remarks>
    /// <value>
    /// The immutable geometry published by <c>DiscoverXmlStorageAsync</c>, or <see langword="null"/> before discovery succeeds.
    /// <para>由 <c>DiscoverXmlStorageAsync</c> 发布的不可变几何信息；发现成功之前为 <see langword="null"/>。</para>
    /// </value>
    public MtkXmlStorageInfo? XmlStorage { get; private set; }

    /// <summary>
    /// Queries bounded eMMC/UFS geometry after DA2 hardware initialization.
    /// <para>在 DA2 硬件初始化之后查询有界的 eMMC/UFS 几何信息。</para>
    /// </summary>
    /// <remarks>Accepts only the selected in-memory destination, never a target-supplied host path.
    /// No storage data is read or written. NAND, absent storage and unknown schemas are rejected.
    /// Geometry is published only after the complete END/START exchange, without granting authorization.
    /// <para>仅接受选定的内存内目标地址，绝不使用目标提供的主机路径。不读取或写入任何存储数据。
    /// NAND、缺失存储和未知 schema 均会被拒绝。几何信息只在完整的 END/START 交换结束后发布，且不会授予授权。</para></remarks>
    /// <param name="timeoutMs">
    /// Cooperative whole-operation deadline, from one millisecond through 24 hours.
    /// <para>整个操作的协作式截止时间，从 1 毫秒到 24 小时。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation for waiting, capability negotiation and discovery.
    /// <para>用于等待、能力协商和发现过程的取消令牌。</para>
    /// </param>
    /// <returns>
    /// Validated immutable geometry reported by the XML agent.
    /// <para>由 XML 代理上报、经过校验的不可变几何信息。</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">The XML DA channel is not attached or storage geometry was not in the required state.
    /// <para>XML DA 通道未附加，或存储几何信息不处于所需状态。</para></exception>
    /// <exception cref="TimeoutException">The cooperative discovery deadline expired before the exchange completed.
    /// <para>在交换完成之前协作式发现截止时间已到期。</para></exception>
    public async Task<MtkXmlStorageInfo> DiscoverXmlStorageAsync(int timeoutMs = 120000,
        CancellationToken cancellationToken = default)
    {
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlStageTwoHardwareInitialized);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                var storage = await QueryXmlStorageOwnedAsync(channel, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                XmlStorage = storage;
                _state = MtkSessionState.XmlStorageGeometryKnown;
                PublishStorageCapability(MtkStorageDiscoverySource.Xml,
                    (generation, evidence) => MtkStorageCapabilitySnapshot.FromXml(storage, generation, evidence));
                return storage;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML storage discovery deadline expired; reconnect before continuing.", error);
            }
            catch
            {
                FaultXml();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<MtkXmlStorageInfo> QueryXmlStorageOwnedAsync(MtkXmlChannel channel, CancellationToken token)
    {
        const string destination = "MEM://0x0:0x10000";
        var command = new XElement("da", new XElement("version", "1.0"), new XElement("command", "CMD:GET-HW-INFO"),
            new XElement("arg", new XElement("target_file", destination)));
        await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: false, upload: true), token).ConfigureAwait(false);
        await channel.SendCommandAsync(command, token).ConfigureAwait(false);
        await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
        var text = await ReceiveXmlDocumentOwnedAsync(channel, destination, MtkXmlStorageInfo.MaximumDocumentLength, token).ConfigureAwait(false);
        var storage = ParseXmlStorageInfoNormalised(text);
        await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
        return storage;
    }

    private static MtkXmlStorageInfo ParseXmlStorageInfoNormalised(string text)
    {
        try
        {
            return MtkXmlStorageInfo.Parse(text);
        }
        catch (InvalidDataException error)
        {
            throw new MtkProtocolException($"The XML DA returned an invalid hardware-info document: {error.Message}", inner: error);
        }
    }

    /// <summary>
    /// Denies XML read, write and erase against regions the unified storage snapshot marks as
    /// authenticated-only or replay-protected, before any wire byte of the operation is sent.
    /// 在 XML 读、写、擦除发送任何线上字节之前，拒绝统一存储快照标记为需认证或重放保护的区域。
    /// </summary>
    /// <param name="regionName">The exact XML region token. 精确的 XML 区域标记。</param>
    private void RequireXmlOrdinaryRegionSnapshot(string regionName)
    {
        var snapshot = StorageCapability;
        if (snapshot is null)
        {
            return;
        }
        var region = snapshot.FindRegion(regionName);
        if (region is { AuthenticatedOnly: true } or { Kind: MtkStorageRegionKind.Rpmb })
        {
            throw new NotSupportedException(
                $"The storage capability snapshot marks the XML region {regionName} as authenticated or replay-protected storage; XML DA I/O is denied by default.");
        }
    }

    /// <summary>
    /// Validates one device-initiated XML upload callback against the negotiated bounded
    /// destination. The checks are intentional and must be kept: the checksum argument must be the
    /// exact <c>CHK_NO</c> token (file transfers carry no cryptographic integrity claim, and a
    /// device must never upgrade the negotiated mode), the packet length must be a bounded
    /// hexadecimal value within the selected binary frame limit, and the direction must match the
    /// destination the host declared in the control command. The target file must equal that
    /// exact host-selected destination identifier.
    /// 校验设备发起的 XML 上传回调是否符合协商出的有界目标。以下检查为刻意保留项：校验和参数必须是精确的
    /// <c>CHK_NO</c> 标记（文件传输不携带密码学完整性声明，设备不得升级已协商的模式）、包长必须是有界
    /// 十六进制值且不超过选定的二进制帧上限、方向必须与宿主在控制命令中声明的目标一致；目标文件必须与
    /// 宿主选定的目标标识完全相同。
    /// </summary>
    /// <param name="request">The parsed device command message. 已解析的设备命令消息。</param>
    /// <param name="destination">The host-declared destination identifier. 宿主声明的目标标识。</param>
    /// <param name="maximumPacketLength">The inclusive packet length bound. 包长包含式上限。</param>
    /// <returns>The validated packet length in bytes. 校验通过的包长（字节）。</returns>
    private static int ValidateXmlUploadRequest(MtkXmlMessage request, string destination, int maximumPacketLength)
    {
        RequireXmlCommand(request, "CMD:UPLOAD-FILE");
        var arguments = request.Arguments;
        if (arguments.Count != 4
            || !arguments.TryGetValue("checksum", out var checksum) || checksum != "CHK_NO"
            || !arguments.TryGetValue("info", out var info) || info.Length == 0
            || !arguments.TryGetValue("target_file", out var target) || target != destination
            || !arguments.TryGetValue("packet_length", out var packet)
            || !TryXmlHexLength(packet, maximumPacketLength, out var packetLength))
        {
            throw new MtkProtocolException("The XML upload callback does not match the bounded destination.");
        }
        return packetLength;
    }

    private static bool TryXmlHexLength(string text, int maximum, out int length)
    {
        length = 0;
        if (text.Length is < 3 or > 10 || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            || value == 0 || value > maximum)
        {
            return false;
        }
        length = (int)value;
        return true;
    }
}