using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Queries one XML system property using a bounded, source-bound document transfer.
    /// <para>使用有界、绑定来源的文档传输查询单个 XML 系统属性。</para></summary>
    /// <remarks>The generic envelope name is preserved, not guessed or treated as a trusted vendor schema.
    /// The accepted shape contains one direct item with an exact key and an optional scalar version.
    /// The result is returned only after END/START; no session authorization state is changed.</remarks>
    /// <param name="key">Exact nonblank property key, at most 128 XML-compatible characters.
    /// <para>精确的非空白属性键，最长 128 个 XML 兼容字符。</para></param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.
    /// <para>整次操作的协作式截止时间，范围从 1 毫秒到 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the complete query.
    /// <para>用于等待与完整查询的取消令牌。</para></param>
    /// <returns>An immutable, non-authoritative property report.
    /// <para>不可变、不具授权效力的属性报告。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is blank or contains characters invalid in XML.
    /// <para><paramref name="key"/> 为空白或包含 XML 非法字符。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="key"/> is longer than 128 characters or <paramref name="timeoutMs"/> is out of bounds.
    /// <para><paramref name="key"/> 超过 128 个字符，或 <paramref name="timeoutMs"/> 越界。</para></exception>
    /// <exception cref="InvalidOperationException">Initialized DA2 hardware or an attached XML channel is missing.
    /// <para>缺少已初始化的 DA2 硬件或已附加的 XML 通道。</para></exception>
    /// <exception cref="MtkProtocolException">The document exchange failed or the reply did not match the expected shape.
    /// <para>文档交换失败，或回复不符合预期结构。</para></exception>
    /// <exception cref="TimeoutException">The query deadline expired; the session faults.
    /// <para>查询截止时间到期；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled waiting or the query.
    /// <para>调用方取消了等待或查询。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public Task<MtkXmlSystemProperty> QueryXmlSystemPropertyAsync(string key, int timeoutMs = 120000,
        CancellationToken cancellationToken = default)
        => QueryXmlSystemPropertyCoreAsync(key, null, timeoutMs, cancellationToken);

    /// <summary>Queries DA.SLA against an explicitly selected property-envelope profile.
    /// <para>针对显式选定的属性信封（envelope）配置查询 DA.SLA。</para></summary>
    /// <remarks>No envelope is inferred from the reply. Only exact ENABLED and DISABLED values are
    /// classified; other nonempty values remain Unknown. Failure never returns a disabled indication.
    /// Even DisabledReported is an agent statement, not proof of authorization or device authenticity.</remarks>
    /// <param name="expectedEnvelopeName">Exact unqualified outer element name selected from the caller's loader profile.
    /// <para>来自调用方加载器配置、精确的不带命名空间前缀的外层元素名。</para></param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.
    /// <para>整次操作的协作式截止时间，范围从 1 毫秒到 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the complete query.
    /// <para>用于等待与完整查询的取消令牌。</para></param>
    /// <returns>The property and its conservative textual SLA indication.
    /// <para>属性本身及其保守的文本 SLA 指示。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="expectedEnvelopeName"/> is blank or not a valid XML local name.
    /// <para><paramref name="expectedEnvelopeName"/> 为空白或不是合法的 XML 局部名。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedEnvelopeName"/> is longer than 128 characters
    /// or <paramref name="timeoutMs"/> is out of bounds.
    /// <para><paramref name="expectedEnvelopeName"/> 超过 128 个字符，或 <paramref name="timeoutMs"/> 越界。</para></exception>
    /// <exception cref="System.Xml.XmlException"><paramref name="expectedEnvelopeName"/> fails XML name validation.
    /// <para><paramref name="expectedEnvelopeName"/> 未通过 XML 名称校验。</para></exception>
    public async Task<MtkXmlSlaReport> QueryXmlSlaStatusAsync(string expectedEnvelopeName, int timeoutMs = 120000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEnvelopeName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expectedEnvelopeName.Length, 128);
        XmlConvert.VerifyNCName(expectedEnvelopeName);
        var property = await QueryXmlSystemPropertyCoreAsync("DA.SLA", expectedEnvelopeName, timeoutMs, cancellationToken).ConfigureAwait(false);
        return new MtkXmlSlaReport(property);
    }

    private async Task<MtkXmlSystemProperty> QueryXmlSystemPropertyCoreAsync(string key, string? expectedEnvelope,
        int timeoutMs, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, 128);
        XmlConvert.VerifyXmlChars(key);
        ValidateXmlSetupTimeout(timeoutMs);
        const string destination = "MEM://0x0:0x10000";
        var command = new XElement("da", new XElement("version", "1.0"),
            new XElement("command", "CMD:GET-SYS-PROPERTY"), new XElement("arg",
                new XElement("key", key), new XElement("target_file", destination)));
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!Transport.IsConnected || _state is not (MtkSessionState.XmlStageTwoHardwareInitialized
                or MtkSessionState.XmlStorageGeometryKnown))
            {
                throw new InvalidOperationException("XML property queries require initialized DA2 hardware.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: false, upload: true), token).ConfigureAwait(false);
                await channel.SendCommandAsync(command, token).ConfigureAwait(false);
                await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
                var text = await ReceiveXmlDocumentOwnedAsync(channel, destination,
                    MtkXmlSystemProperty.MaximumDocumentLength, token).ConfigureAwait(false);
                var property = MtkXmlSystemProperty.Parse(text, key, expectedEnvelope);
                await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                return property;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML property query deadline expired; no security state can be inferred.", error);
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