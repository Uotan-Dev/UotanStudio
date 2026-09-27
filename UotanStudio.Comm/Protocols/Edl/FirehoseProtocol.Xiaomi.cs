using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class FirehoseProtocol
{
    /// <summary>
    /// Completes one Xiaomi challenge/signature exchange under exclusive transport ownership.
    /// No compatibility signatures, credential fallback or automatic replay are used.
    /// <para>在独占传输期间完成一次小米挑战签名交换，不使用兼容签名、凭据回退或自动重放。</para>
    /// </summary>
    /// <param name="provider">The explicitly authorized signature provider.
    /// <para>显式授权的签名提供方。</para></param>
    /// <param name="identity">The optional identity obtained from this connection before programmer upload.
    /// <para>可选的、本连接在上传 programmer 前取得的身份。</para></param>
    /// <param name="timeoutMs">The cooperative deadline, between 1 ms and 24 hours.
    /// <para>范围为 1 毫秒至 24 小时的协作式截止时间。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the entire exchange.
    /// <para>等待与完整交换的取消令牌。</para></param>
    /// <returns>The final non-raw ACK accepting the signature, not proof of device authenticity.
    /// <para>接受签名的最终非 raw ACK，并非设备真实性证明。</para></returns>
    /// <exception cref="FirehoseException">The challenge, signature or acknowledgement is invalid.
    /// <para>挑战、签名或确认无效。</para></exception>
    /// <exception cref="TimeoutException">The cooperative deadline expired.
    /// <para>协作式截止时间已到。</para></exception>
    public async Task<FirehoseResponse> AuthenticateXiaomiAsync(IEdlXiaomiAuthenticationProvider provider,
        SaharaDeviceIdentity? identity = null, int timeoutMs = 60000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateAuthenticationTimeout(timeoutMs);
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await AuthenticateXiaomiOwnedAsync(provider, identity, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FirehoseResponse> AuthenticateXiaomiOwnedAsync(IEdlXiaomiAuthenticationProvider provider,
        SaharaDeviceIdentity? identity, int timeoutMs, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        byte[]? challenge = null;
        byte[]? signature = null;
        try
        {
            var fields = new AuthenticationFields();
            var request = BuildAuthenticationCommand("sig", ("TargetName", "req"));
            var challengeResponse = await SendCommandAndWaitResponseAsync(request, token,
                blockObserver: fields.Observe).ConfigureAwait(false);
            RequireAuthenticationAck(challengeResponse);
            var text = fields.GetRequired("sig", "value", "sig");
            if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 16384)
            {
                throw new FirehoseException("The Xiaomi challenge is empty or oversized.");
            }
            challenge = Encoding.UTF8.GetBytes(text);
            signature = await provider.CreateSignatureAsync(identity, challenge, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (signature is null || signature.Length != 256)
            {
                throw new FirehoseException("A Xiaomi authentication signature must contain exactly 256 bytes.");
            }
            var command = BuildAuthenticationCommand("sig", ("TargetName", "sig"), ("size_in_bytes", "256"), ("verbose", "1"));
            var ready = await SendCommandAndWaitResponseAsync(command, token).ConfigureAwait(false);
            RequireRawMode(ready, EdlErrorCode.InvalidParameter);
            await WriteDataAsync(signature, GetZlpTimeoutMs(), token).ConfigureAwait(false);
            var complete = await ReceiveResponseAsync(token).ConfigureAwait(false);
            RequireAuthenticationAck(complete);
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return complete;
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _faulted = true;
            throw new TimeoutException("The Xiaomi authentication deadline expired; reconnect before continuing.", error);
        }
        catch
        {
            _faulted = true;
            throw;
        }
        finally
        {
            if (challenge is not null)
            {
                CryptographicOperations.ZeroMemory(challenge);
            }
            if (signature is not null)
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }
    }

    internal static void ValidateAuthenticationTimeout(int timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutMs, 86400000);
    }

    private static string BuildAuthenticationCommand(string name, params (string Name, string Value)[] attributes)
        => EdlConstants.XmlHeader + new XElement("data",
            new XElement(name, attributes.Select(attribute => new XAttribute(attribute.Name, attribute.Value))))
            .ToString(SaveOptions.DisableFormatting);

    private static void RequireAuthenticationAck(FirehoseResponse response)
    {
        if (!response.IsAck || response.IsRawMode)
        {
            throw new FirehoseException("The target rejected the authentication exchange or remained in raw mode.");
        }
    }

    private sealed class AuthenticationFields
    {
        private readonly Dictionary<string, Dictionary<string, string>> _payloads = new(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, string>? _response;

        internal void Observe(FirehoseDataBlock block)
        {
            foreach (var payload in block.PayloadElements)
            {
                if (_payloads.Count >= 32 || !_payloads.TryAdd(payload.Key, payload.Value))
                {
                    throw new FirehoseException("Repeated or excessive authentication payloads are ambiguous.");
                }
            }
            if (block.Response is { } response)
            {
                _response = response.Attributes;
            }
        }

        internal string GetRequired(string element, string attribute, string responseAttribute)
        {
            string? payloadValue = null;
            string? responseValue = null;
            if (_payloads.TryGetValue(element, out var fields))
            {
                fields.TryGetValue(attribute, out payloadValue);
            }
            _response?.TryGetValue(responseAttribute, out responseValue);
            if (payloadValue is not null && responseValue is not null)
            {
                throw new FirehoseException("Authentication fields were reported more than once.");
            }
            return payloadValue ?? responseValue ?? throw new FirehoseException("A required authentication field is missing.");
        }
    }
}