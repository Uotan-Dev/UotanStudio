using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk.Sla;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Queries XML DA2 SLA and completes one challenge-response exchange under exclusive session ownership.
    /// <para>查询 XML DA2 SLA，并在独占会话期间完成一次挑战响应交换。</para>
    /// </summary>
    /// <param name="expectedEnvelopeName">The property envelope asserted from the loader profile, or
    /// <see langword="null"/> to accept the envelope the agent reports.
    /// <para>加载器配置断言的属性信封名称；为 <see langword="null"/> 时接受代理实际上报的信封。</para></param>
    /// <param name="responder">The authorized provider, required when SLA is enabled.
    /// It receives UTF-8 device information and the decoded rnd field.
    /// <para>启用 SLA 时必需的授权提供方，接收 UTF-8 设备信息与解码的 rnd 字段。</para></param>
    /// <param name="timeoutMs">The cooperative deadline from 1 ms through 24 hours.
    /// <para>范围为 1 毫秒至 24 小时的协作式截止时间。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the complete exchange.
    /// <para>等待与完整交换的取消令牌。</para></param>
    /// <returns>True after the policy response is accepted; false only for an explicit DISABLED report.
    /// <para>策略响应被接受后返回 true；仅在明确报告 DISABLED 时返回 false。</para></returns>
    /// <exception cref="InvalidOperationException">DA2 hardware is not ready or a required responder is missing.
    /// <para>DA2 硬件未就绪或缺少必需的响应提供方。</para></exception>
    /// <exception cref="MtkProtocolException">The status is unknown or a protocol exchange is rejected or malformed.
    /// <para>状态未知，或协议交换被拒绝或格式错误。</para></exception>
    /// <exception cref="InvalidDataException">The challenge document is not the supported SLA schema.
    /// <para>挑战文档不符合支持的 SLA 架构。</para></exception>
    /// <exception cref="TimeoutException">The cooperative deadline expired.
    /// <para>协作式截止时间已到。</para></exception>
    public async Task<bool> AuthenticateXmlStageTwoAsync(string? expectedEnvelopeName,
        IMtkDaSlaResponder? responder = null, int timeoutMs = 60000, CancellationToken cancellationToken = default)
    {
        if (expectedEnvelopeName is { } asserted)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(asserted);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(asserted.Length, 128);
            XmlConvert.VerifyNCName(asserted);
        }
        ValidateSlaTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlStageTwoHardwareInitialized);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            byte[]? information = null;
            byte[]? challenge = null;
            byte[]? response = null;
            string? challengeDigest = null;
            var noProviderRecorded = false;
            try
            {
                var property = await QueryXmlSystemPropertyOwnedAsync(channel, "DA.SLA",
                    expectedEnvelopeName, token).ConfigureAwait(false);
                var indication = new MtkXmlSlaReport(property).Indication;
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (indication == MtkXmlSlaIndication.DisabledReported)
                {
                    return false;
                }
                if (indication != MtkXmlSlaIndication.EnabledReported)
                {
                    throw new MtkProtocolException("The XML DA2 returned an unknown SLA indication.");
                }
                RecordSlaEvidence(MtkSlaFlowKind.DaSlaXml, MtkSlaEvidenceStage.DeviceRequired,
                    ClassifySlaResponder(responder), null, CommandOutcomeKind.Completed,
                    "sla-required; XML DA2 reported SLA enabled.");
                if (responder is null)
                {
                    RecordSlaFailure(MtkSlaFlowKind.DaSlaXml, responder: null, MtkSlaEvidenceStage.Rejected,
                        "SlaNoProvider", null,
                        "XML DA2 requires an explicitly authorized SLA response provider; no challenge response bytes were written.");
                    noProviderRecorded = true;
                    throw new InvalidOperationException("XML DA2 requires an explicitly authorized SLA response provider.");
                }
                const string destination = "MEM://0x0:0x10000";
                var command = new XElement("da", new XElement("version", "1.0"),
                    new XElement("command", "CMD:SECURITY-GET-DEV-FW-INFO"),
                    new XElement("arg", new XElement("target_file", destination)));
                await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: false, upload: true), token).ConfigureAwait(false);
                await channel.SendCommandAsync(command, token).ConfigureAwait(false);
                await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
                var text = await ReceiveXmlDocumentOwnedAsync(channel, destination, 64 * 1024, token).ConfigureAwait(false);
                challenge = ParseXmlSlaChallenge(text);
                challengeDigest = MtkSlaEvidence.ComputeChallengeSha256Hex(challenge);
                await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                information = Encoding.UTF8.GetBytes(text);
                response = await responder.CreateResponseAsync(DeviceInfo!, information, challenge, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (response is null || response.Length is 0 or > 4096)
                {
                    throw new MtkProtocolException("The XML DA2 SLA response provider returned an invalid length.");
                }
                var sourceId = $"MEM://0x0:0x{response.Length:x}";
                var policy = new XElement("da", new XElement("version", "1.0"),
                    new XElement("command", "CMD:SECURITY-SET-FLASH-POLICY"),
                    new XElement("arg", new XElement("source_file", sourceId)));
                using var source = new MemoryStream(response, writable: false);
                await UploadXmlSecurityResourceOwnedAsync(channel, policy, sourceId, source,
                    response.Length, null, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                RecordSlaEvidence(MtkSlaFlowKind.DaSlaXml, MtkSlaEvidenceStage.ResponseAccepted,
                    ClassifySlaResponder(responder), challengeDigest, CommandOutcomeKind.Completed, "sla-accepted");
                return true;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                RecordSlaFailure(MtkSlaFlowKind.DaSlaXml, responder, MtkSlaEvidenceStage.Timeout, "SlaTimeout",
                    challengeDigest, error.Message);
                CaptureUploadFailure(error);
                FaultXml();
                throw new TimeoutException("The XML DA2 SLA deadline expired; reconnect before continuing.", error);
            }
            catch (Exception error)
            {
                if (!noProviderRecorded)
                {
                    var (stage, classification) = MtkSlaEvidence.ClassifyFailure(error, deadlineExpired: false);
                    RecordSlaFailure(MtkSlaFlowKind.DaSlaXml, responder, stage, classification, challengeDigest, error.Message);
                }
                CaptureUploadFailure(error);
                FaultXml();
                throw;
            }
            finally
            {
                if (information is not null)
                {
                    CryptographicOperations.ZeroMemory(information);
                }
                if (challenge is not null)
                {
                    CryptographicOperations.ZeroMemory(challenge);
                }
                if (response is not null)
                {
                    CryptographicOperations.ZeroMemory(response);
                }
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    internal static byte[] ParseXmlSlaChallenge(string text)
    {
        if (text.Length is 0 or > 64 * 1024)
        {
            throw new InvalidDataException("Invalid XML SLA document length.");
        }
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024,
            IgnoreWhitespace = true
        });
        var nodes = 0;
        while (reader.Read())
        {
            if (++nodes > 64 || reader.Depth > 2
                || reader.NodeType is XmlNodeType.Comment or XmlNodeType.ProcessingInstruction)
            {
                throw new InvalidDataException("Unsupported XML SLA structure.");
            }
        }
        var root = XDocument.Parse(text).Root!;
        if (root.Name != "sla" || root.Attribute("version")?.Value != "1.0"
            || root.Attributes().Count() != 1
            || root.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value)))
        {
            throw new InvalidDataException("Unsupported XML SLA envelope.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? random = null;
        foreach (var field in root.Elements())
        {
            if (field.Name.NamespaceName.Length != 0 || field.HasAttributes || field.HasElements
                || field.Name.LocalName is not ("rnd" or "hrid" or "socid")
                || !seen.Add(field.Name.LocalName) || field.Value.Length is 0 or > 512
                || (field.Value.Length & 1) != 0 || !field.Value.All(char.IsAsciiHexDigit))
            {
                throw new InvalidDataException("Invalid or duplicate XML SLA field.");
            }
            if (field.Name == "rnd")
            {
                random = field.Value;
            }
        }
        if (random is null || random.Length != 32)
        {
            throw new InvalidDataException("XML SLA requires a 16-byte challenge.");
        }
        return Convert.FromHexString(random);
    }
}