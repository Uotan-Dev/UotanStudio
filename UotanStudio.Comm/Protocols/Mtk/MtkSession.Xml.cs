using System.Xml;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Storage;
using UotanStudio.Comm.Protocols.Mtk.XmlProfile;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private MtkXmlChannel? _xml;

    /// <summary>
    /// Gets or sets how device-authored XML DA messages grade unknown top-level fields.
    /// <para>获取或设置设备侧 XML DA 消息对未知顶层字段的分级方式。</para>
    /// </summary>
    /// <value>
    /// <see cref="MtkXmlFieldPolicy.Strict"/> (default) preserves the historical reject-everything
    /// behavior; <see cref="MtkXmlFieldPolicy.TolerantUnknown"/> downgrades bounded, scalar,
    /// unqualified, non-duplicate unknown fields to warning evidence. Must be configured before the
    /// XML agent is attached; structural violations, <c>CMD:</c> prefix violations, duplicates of
    /// known fields and unknown profile versions are rejected under both policies.
    /// <para>默认的 <see cref="MtkXmlFieldPolicy.Strict"/> 保持历史的全量拒绝行为；
    /// <see cref="MtkXmlFieldPolicy.TolerantUnknown"/> 将有界、标量、无命名空间、不重复的未知字段降级为
    /// warning 证据。必须在附加 XML agent 之前配置；两种策略下，结构违规、<c>CMD:</c> 前缀违规、
    /// 已知字段重名与未知配置版本一律拒绝。</para>
    /// </value>
    public MtkXmlFieldPolicy XmlFieldPolicy { get; set; } = MtkXmlFieldPolicy.Strict;

    /// <summary>
    /// Parses one device-authored message under the configured field policy and records warning
    /// evidence for every field the tolerant policy ignored.
    /// <para>按已配置的字段策略解析一条设备侧消息，并为容忍策略忽略的每个字段记录 warning 证据。
    /// </para>
    /// </summary>
    /// <param name="text">The received message text. 接收到的消息文本。</param>
    /// <returns>The parsed message. 解析后的消息。</returns>
    private MtkXmlMessage ParseXmlMessageOwned(string text)
    {
        var message = MtkXmlMessage.Parse(text, XmlFieldPolicy);
        if (message.IgnoredFields.Count > 0)
        {
            _evidence.Add(new ProtocolEvidence
            {
                Protocol = "mtk",
                Phase = "xml-profile-warning",
                Outcome = CommandOutcomeKind.Completed,
                Detail = $"warning: ignored XML DA field(s) {string.Join(",", message.IgnoredFields)} under {XmlFieldPolicy}."
            });
        }
        return message;
    }

    private MtkXmlMessage ParseXmlControlReplyOwned(string text)
    {
        try
        {
            return ParseXmlMessageOwned(text);
        }
        catch (InvalidDataException error)
        {
            throw new MtkProtocolException($"The XML DA sent a message outside its protocol: {error.Message}", inner: error);
        }
    }


    /// <summary>
    /// Accepts and acknowledges an XML DA's initial START after an explicitly uploaded and
    /// started agent. This only establishes the control channel, not hardware, DRAM, DA2
    /// or storage readiness. No XFlash/XML fallback or loader autodetection is performed.
    /// <para>在显式上传并启动的 agent 之后，接受并确认 XML DA 的初始 START。
    /// 这仅建立控制通道，不代表硬件、DRAM、DA2 或存储已就绪。不进行 XFlash/XML
    /// 回退，也不自动探测 loader。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation for waiting and the bounded initial exchange.
    /// <para>用于等待与有界初始交换的取消令牌。</para></param>
    /// <returns>A task that completes once the control channel is attached.
    /// <para>在控制通道附加完成后返回的异步任务。</para></returns>
    /// <exception cref="InvalidOperationException">The agent has not been started or the session is disposed.
    /// <para>agent 尚未启动或会话已被释放。</para></exception>
    /// <exception cref="MtkProtocolException">The first message was not CMD:START or the exchange faulted.
    /// <para>首条消息不是 CMD:START，或交换进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">The session deadline for the initial exchange expired.
    /// <para>初始交换的会话截止时间到期。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled waiting or the exchange.
    /// <para>调用方取消了等待或交换。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task AttachXmlAgentAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.AgentStarted);
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeoutMs);
            try
            {
                var channel = new MtkXmlChannel(Transport, _timeoutMs);
                var initial = ParseXmlMessageOwned(await channel.ReceiveTextAsync(deadline.Token).ConfigureAwait(false));
                RequireXmlCommand(initial, "CMD:START");
                await channel.AcknowledgeAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _xml = channel;
                _state = MtkSessionState.XmlControlReady;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML DA initial exchange deadline expired.", error);
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

    /// <summary>Sends explicit host metadata and requires OK, END/OK and the next START before completing.
    /// <para>发送显式的主机元数据，并在完成前要求 OK、END/OK 与下一个 START。</para></summary>
    /// <remarks>No storage commands or target-requested file operations are performed. Unknown
    /// subcommands fault the session. Progress reports are consumed with bounded acknowledgements.</remarks>
    /// <param name="hostInfo">Explicit metadata of at most 256 characters, without automatic timestamps.
    /// <para>最多 256 个字符的显式元数据，不自动附加时间戳。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the whole control exchange.
    /// <para>用于等待与整个控制交换的取消令牌。</para></param>
    /// <returns>A task that completes after the control exchange is finished.
    /// <para>在控制交换完成后返回的异步任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="hostInfo"/> is null.
    /// <para><paramref name="hostInfo"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hostInfo"/> exceeds 256 characters.
    /// <para><paramref name="hostInfo"/> 超过 256 个字符。</para></exception>
    /// <exception cref="XmlException"><paramref name="hostInfo"/> contains characters invalid in XML.
    /// <para><paramref name="hostInfo"/> 包含 XML 非法字符。</para></exception>
    /// <exception cref="InvalidOperationException">No attached XML DA control stage is available.
    /// <para>没有可用的 XML DA 控制阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The DA rejected the command or the exchange faulted.
    /// <para>DA 拒绝命令或交换进入 fault 状态。</para></exception>
    /// <exception cref="TimeoutException">The session deadline for the control exchange expired.
    /// <para>控制交换的会话截止时间到期。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled waiting or the exchange.
    /// <para>调用方取消了等待或交换。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task SetXmlHostInfoAsync(string hostInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostInfo);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hostInfo.Length, 256);
        XmlConvert.VerifyXmlChars(hostInfo);
        var command = new XElement("da", new XElement("version", "1.0"),
            new XElement("command", "CMD:SET-HOST-INFO"), new XElement("arg", new XElement("info", hostInfo)));
        return ExecuteXmlControlCommandsAsync([command], null, null, cancellationToken);
    }

    private async Task ExecuteXmlControlCommandsAsync(XElement[] commands, MtkSessionState? requiredState,
        MtkSessionState? nextState, CancellationToken cancellationToken, int? timeoutMs = null)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (requiredState is { } expected)
            {
                RequireState(expected);
            }
            else
            {
                ThrowIfDisposed();
                if (!Transport.IsConnected || _state is not (MtkSessionState.XmlControlReady
                    or MtkSessionState.XmlRuntimeConfigured or MtkSessionState.XmlHardwareInitialized
                    or MtkSessionState.XmlStageTwoStarted or MtkSessionState.XmlStageTwoHardwareInitialized
                    or MtkSessionState.XmlStorageGeometryKnown))
                {
                    throw new InvalidOperationException("An attached XML DA control stage is required.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs ?? _timeoutMs);
            var token = deadline.Token;
            try
            {
                foreach (var command in commands)
                {
                    await SendXmlControlCommandOwnedAsync(channel, command, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (nextState is { } completed)
                {
                    _state = completed;
                }
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML DA control exchange deadline expired.", error);
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

    private async Task SendXmlControlCommandOwnedAsync(MtkXmlChannel channel, XElement command, CancellationToken token)
    {
        await channel.SendCommandAsync(command, token).ConfigureAwait(false);
        for (var serviced = 0; ; serviced++)
        {
            var reply = await channel.ReceiveTextAsync(token).ConfigureAwait(false);
            if (reply == "OK")
            {
                break;
            }

            if (serviced < 16 && reply.Contains("<command", StringComparison.Ordinal) &&
                await TryServiceXmlFileRequestOwnedAsync(channel, ParseXmlControlReplyOwned(reply), token).ConfigureAwait(false))
            {
                continue;
            }

            throw new MtkProtocolException("The XML DA did not accept the control command.");
        }

        await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
    }

    private async Task CompleteXmlCommandOwnedAsync(MtkXmlChannel channel, CancellationToken token)
    {
        var end = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
        await AcknowledgeXmlEndOwnedAsync(channel, end, token).ConfigureAwait(false);
        await AcknowledgeXmlStartOwnedAsync(channel, token).ConfigureAwait(false);
    }

    private async Task AcknowledgeXmlEndOwnedAsync(MtkXmlChannel channel, MtkXmlMessage end, CancellationToken token)
    {
        RequireXmlCommand(end, "CMD:END");
        if (end.Result != "OK")
        {
            throw new MtkProtocolException("The XML DA control command failed.");
        }
        await channel.AcknowledgeAsync(token).ConfigureAwait(false);
    }

    private async Task AcknowledgeXmlStartOwnedAsync(MtkXmlChannel channel, CancellationToken token)
    {
        var start = await ReceiveServicedXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
        RequireXmlCommand(start, "CMD:START");
        await channel.AcknowledgeAsync(token).ConfigureAwait(false);
    }

    private Task<MtkXmlMessage> ReceiveXmlCommandOwnedAsync(MtkXmlChannel channel, CancellationToken token)
        => ReceiveServicedXmlCommandOwnedAsync(channel, token, expectFileRequests: false);

    /// <summary>Receives the next non-progress command, servicing device-initiated file requests
    /// when the host file store is attached (bounded, like the progress-report budget).
    /// 接收下一条非进度命令；在附加主机文件存储时服务设备发起的文件请求（与进度报告一样有界）。</summary>
    private async Task<MtkXmlMessage> ReceiveServicedXmlCommandOwnedAsync(MtkXmlChannel channel, CancellationToken token,
        bool expectFileRequests = true)
    {
        var reports = 0;
        var serviced = 0;
        while (true)
        {
            var message = ParseXmlMessageOwned(await channel.ReceiveTextAsync(token).ConfigureAwait(false));
            if (message.Command == "CMD:PROGRESS-REPORT")
            {
                if (++reports > 16)
                {
                    throw new MtkProtocolException("Too many XML DA progress reports.");
                }

                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                var completed = false;
                for (var packet = 0; packet < 256; packet++)
                {
                    var text = await channel.ReceiveTextAsync(token).ConfigureAwait(false);
                    if (text.StartsWith("ERR!", StringComparison.Ordinal))
                    {
                        throw new MtkProtocolException("The XML DA progress phase failed.");
                    }
                    await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                    if (text == "OK!EOT")
                    {
                        completed = true;
                        break;
                    }
                }
                if (!completed)
                {
                    throw new MtkProtocolException("The XML DA progress phase exceeded its packet limit.");
                }

                continue;
            }

            if (expectFileRequests && IsXmlFileRequest(message))
            {
                if (++serviced > 16)
                {
                    throw new MtkProtocolException("Too many XML DA device-initiated file requests.");
                }

                if (await TryServiceXmlFileRequestOwnedAsync(channel, message, token).ConfigureAwait(false))
                {
                    continue;
                }
            }

            return message;
        }
    }

    private static void RequireXmlCommand(MtkXmlMessage message, string command)
    {
        if (message.Command != command || message.Result is not (null or "OK"))
        {
            throw new MtkProtocolException($"Expected XML DA {command}, received {message.Command}.");
        }
    }

    private void FaultXml()
    {
        XmlStorage = null;
        ClearStorageCapability(MtkStorageDiscoverySource.Xml);
        _xml = null;
        _state = MtkSessionState.Faulted;
    }
}