using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Explicit XML security-resource commands; these are not boot-ROM E0/E2 resources.
/// <para>显式的 XML 安全资源命令；它们不是 Boot ROM 的 E0/E2 资源。</para></summary>
public enum MtkXmlSecurityResourceKind
{
    /// <summary>A caller-supplied flash-policy response, including any required device-specific signature.
    /// <para>由调用方提供的 flash-policy 响应，包含任何所需的设备专用签名。</para></summary>
    FlashPolicy,
    /// <summary>A caller-supplied all-in-one signature resource.
    /// <para>由调用方提供的一体化（all-in-one）签名资源。</para></summary>
    AllInOneSignature
}

public sealed partial class MtkSession
{
    /// <summary>Uploads an explicitly selected XML security resource without generating or guessing credentials.
    /// <para>上传显式选定的 XML 安全资源，不生成也不猜测凭据。</para></summary>
    /// <remarks>
    /// Only initialized DA1 or DA2 hardware-control stages are accepted, before storage discovery.
    /// The caller must select resources for the exact device/session and keep their source stable.
    /// Completion means END/OK and START were acknowledged; it does not authenticate the device,
    /// prove SLA is disabled or authorize subsequent storage operations. No key lookup, patching,
    /// fallback credentials, target-provided host paths or automatic replay is performed.
    /// </remarks>
    /// <param name="kind">The explicitly selected security command.
    /// <para>显式选定的安全命令。</para></param>
    /// <param name="resource">Readable stream at the selected resource bytes; remains caller-owned.
    /// <para>定位在选定资源字节处的可读流；所有权仍属于调用方。</para></param>
    /// <param name="length">Positive selected byte count, limited to 1 MiB.
    /// <para>正数的选定字节数，上限为 1 MiB。</para></param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.
    /// <para>整次操作的协作式截止时间，范围从 1 毫秒到 24 小时。</para></param>
    /// <param name="progress">Cumulative acknowledged bytes; full progress is not final command completion.
    /// <para>累计已确认字节数；进度跑满并不代表命令最终完成。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the complete resource exchange.
    /// <para>用于等待与完整资源交换过程的取消令牌。</para></param>
    /// <returns>A task that completes when the resource exchange has been acknowledged end to end.
    /// <para>在资源交换被端到端确认后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is undefined, <paramref name="length"/> exceeds 1 MiB,
    /// or <paramref name="timeoutMs"/> is out of bounds.
    /// <para><paramref name="kind"/> 未定义、<paramref name="length"/> 超过 1 MiB，或 <paramref name="timeoutMs"/> 越界。</para></exception>
    /// <exception cref="ArgumentException">The resource is unreadable or shorter than the declared length.
    /// <para>资源流不可读，或短于声明的长度。</para></exception>
    /// <exception cref="InvalidOperationException">The transport is disconnected, the DA stage is wrong, or no XML channel is attached.
    /// <para>传输已断开、DA 阶段不符，或未附加 XML 通道。</para></exception>
    /// <exception cref="MtkProtocolException">The device rejected or malformed the exchange.
    /// <para>设备拒绝或破坏了本次交换。</para></exception>
    /// <exception cref="TimeoutException">The deadline expired; the session faults and the exchange must not be replayed.
    /// <para>截止时间到期；会话进入 fault 状态，且不得重放本次交换。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled waiting or the exchange.
    /// <para>调用方取消了等待或交换过程。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task UploadXmlSecurityResourceAsync(MtkXmlSecurityResourceKind kind, Stream resource, int length,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var name = kind switch
        {
            MtkXmlSecurityResourceKind.FlashPolicy => "CMD:SECURITY-SET-FLASH-POLICY",
            MtkXmlSecurityResourceKind.AllInOneSignature => "CMD:SECURITY-SET-ALLINONE-SIGNATURE",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 1024 * 1024);
        MtkXFlashChannel.ValidateSource(resource, length);
        ValidateXmlSetupTimeout(timeoutMs);
        var sourceId = $"MEM://0x0:0x{length:x}";
        var command = new XElement("da", new XElement("version", "1.0"), new XElement("command", name),
            new XElement("arg", new XElement("source_file", sourceId)));
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!Transport.IsConnected || _state is not (MtkSessionState.XmlHardwareInitialized
                or MtkSessionState.XmlStageTwoHardwareInitialized))
            {
                throw new InvalidOperationException("XML security resources require initialized DA1 or DA2 hardware before storage discovery.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            MtkXFlashChannel.ValidateSource(resource, length);
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: true), token).ConfigureAwait(false);
                await channel.SendCommandAsync(command, token).ConfigureAwait(false);
                await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
                var request = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                var packetLength = ValidateXmlDownloadRequest(request, sourceId);
                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                await SendXmlSourceOwnedAsync(channel, resource, length, packetLength, progress, token).ConfigureAwait(false);
                await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML security-resource deadline expired; reconnect rather than replaying the exchange.", error);
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
}