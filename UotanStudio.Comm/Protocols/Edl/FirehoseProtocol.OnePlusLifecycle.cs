namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class FirehoseProtocol
{
    /// <summary>Sends the explicitly selected OnePlus SetNetType command without claiming authentication.
    /// <para>发送显式选择的 OnePlus SetNetType 命令，不将其视为认证。</para></summary>
    /// <remarks>The caller must select a programmer known to support the case-sensitive, parameterless command.
    /// No capability probing, project fallback, credential generation or automatic retry occurs.
    /// An ACK only confirms this command; it does not replace project verification or per-command tokens.
    /// <para>调用方必须选择已知支持此大小写敏感、无参数命令的 programmer。
    /// 不探测能力、不回退项目、不生成凭据，也不自动重试。ACK 仅确认此命令，不能替代项目校验或逐命令令牌。</para></remarks>
    /// <param name="timeoutMs">The cooperative deadline from 1 ms through 24 hours.
    /// <para>1 毫秒至 24 小时的协作式截止时间。</para></param>
    /// <param name="cancellationToken">Cancellation for the operation lease and command exchange.
    /// <para>操作锁等待与命令交换的取消令牌。</para></param>
    /// <returns>The non-raw ACK response, not an authentication result.
    /// <para>非原始模式的 ACK 响应，而非认证结果。</para></returns>
    public async Task<FirehoseResponse> SetOnePlusNetTypeAsync(int timeoutMs = 60000,
        CancellationToken cancellationToken = default)
    {
        ValidateAuthenticationTimeout(timeoutMs);
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await SetOnePlusNetTypeOwnedAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FirehoseResponse> SetOnePlusNetTypeOwnedAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        try
        {
            var response = await SendCommandAndWaitResponseAsync(BuildAuthenticationCommand("SetNetType"),
                token, timeoutMs, timeoutMs).ConfigureAwait(false);
            if (!response.IsAck || response.IsRawMode)
            {
                throw new FirehoseException("OnePlus SetNetType was rejected or unexpectedly entered raw mode.");
            }
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return response;
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _faulted = true;
            throw new TimeoutException("The OnePlus SetNetType deadline expired; reconnect before continuing.", error);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }
}