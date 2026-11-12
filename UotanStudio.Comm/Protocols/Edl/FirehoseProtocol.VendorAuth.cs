namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// The result of offering one vendor authentication signature to a programmer that refuses commands until
/// authentication completes.
/// <para>向一个在认证完成前拒绝命令的 programmer 提交一份厂商认证签名所得的结果。</para>
/// </summary>
/// <remarks>
/// Every outcome except <see cref="Accepted"/> leaves the session usable, because the target answered the
/// exchange instead of dropping the link; a transport failure is reported as an exception instead.
/// <para>除 <see cref="Accepted"/> 外的每种结果都保持会话可用，因为目标应答了本次交换而非断开链路；
/// 传输失败改为以异常上报。</para>
/// </remarks>
public enum VendorSignatureOutcome
{
    /// <summary>
    /// The target reported the signature as accepted and now answers ordinary commands.
    /// <para>目标报告签名已被接受，此后应答普通命令。</para>
    /// </summary>
    Accepted,

    /// <summary>
    /// The target accepted the signature command but rejected the signature bytes; another signature may be tried.
    /// <para>目标接受了签名命令但拒绝了签名字节；可以继续尝试其它签名。</para>
    /// </summary>
    SignatureRejected,

    /// <summary>
    /// The target refused the signature command itself, so no signature bytes were written; the inline-value
    /// form or another signature may be tried.
    /// <para>目标拒绝了签名命令本身，因此没有写入签名字节；可以改用内联 value 形式或其它签名。</para>
    /// </summary>
    CommandRejected
}

public partial class FirehoseProtocol
{
    /// <summary>
    /// The signature payload width every inspected vendor authentication flow declares.
    /// <para>所有已核对厂商认证流程声明的签名负载宽度。</para>
    /// </summary>
    public const int VendorSignatureLength = 256;

    /// <summary>
    /// The target log token that marks a completed authentication; a refusal uses the noun form
    /// "authentication" and therefore never matches it.
    /// <para>标记认证完成的目标日志记号；拒绝时使用的是名词形式 "authentication"，因此不会与之匹配。</para>
    /// </summary>
    internal const string AuthenticatedLogToken = "authenticated";

    /// <summary>
    /// Offers one signature to a programmer that requires vendor authentication, using the size-delimited form
    /// first and the inline-value form when the target refuses the command itself.
    /// <para>向要求厂商认证的 programmer 提交一份签名：先使用按长度声明负载的形式，目标拒绝命令本身时改用内联
    /// value 形式。</para>
    /// </summary>
    /// <param name="signature">Exactly <see cref="VendorSignatureLength"/> signature bytes.
    /// <para>恰好 <see cref="VendorSignatureLength"/> 字节的签名。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交换。</para></param>
    /// <returns>The outcome of the last attempt. <para>最后一次尝试的结果。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="signature"/> is not <see cref="VendorSignatureLength"/> bytes.
    /// <para><paramref name="signature"/> 不是 <see cref="VendorSignatureLength"/> 字节。</para></exception>
    /// <exception cref="EdlException">The transport failed, the target disconnected, or no completion arrived;
    /// the session then requires a reconnect. <para>传输失败、目标断开或没有收到完成响应；此后会话需要重连。</para></exception>
    public async Task<VendorSignatureOutcome> SendVendorSignatureAsync(ReadOnlyMemory<byte> signature,
        CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await SendVendorSignatureOwnedAsync(signature, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Offers one signature while the caller holds the operation lease.
    /// <para>在调用方持有操作租约时提交一份签名。</para>
    /// </summary>
    /// <param name="signature">Exactly <see cref="VendorSignatureLength"/> signature bytes.
    /// <para>恰好 <see cref="VendorSignatureLength"/> 字节的签名。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交换。</para></param>
    /// <returns>The outcome of the last attempt. <para>最后一次尝试的结果。</para></returns>
    internal async Task<VendorSignatureOutcome> SendVendorSignatureOwnedAsync(ReadOnlyMemory<byte> signature,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (signature.Length != VendorSignatureLength)
        {
            throw new ArgumentException(
                $"A vendor signature must be exactly {VendorSignatureLength} bytes.", nameof(signature));
        }

        var outcome = await SendSizeDelimitedSignatureOwnedAsync(signature, cancellationToken).ConfigureAwait(false);
        if (outcome != VendorSignatureOutcome.CommandRejected)
        {
            return outcome;
        }

        Log("The target refused the size-delimited sig command; retrying the signature inline.");
        return await SendInlineSignatureOwnedAsync(signature, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the size-delimited signature command, writes the raw bytes it announces, and reads the completion.
    /// <para>发送按长度声明负载的签名命令，写入它声明的裸字节，并读取完成响应。</para>
    /// </summary>
    /// <param name="signature">The signature bytes written after the command. <para>命令之后写入的签名字节。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交换。</para></param>
    /// <returns>The outcome for this form. <para>该形式的结果。</para></returns>
    private async Task<VendorSignatureOutcome> SendSizeDelimitedSignatureOwnedAsync(ReadOnlyMemory<byte> signature,
        CancellationToken cancellationToken)
    {
        var preambleLogs = new List<string>();
        var preamble = await SendCommandAndWaitResponseAsync(
            FirehoseXmlBuilder.BuildSig("sig", VendorSignatureLength), cancellationToken,
            logObserver: preambleLogs.Add).ConfigureAwait(false);
        if (!preamble.IsAck)
        {
            Log($"The target refused the sig command: {preamble.RawValue ?? "no response value"}.");
            return VendorSignatureOutcome.CommandRejected;
        }

        var completionLogs = new List<string>();
        try
        {
            await WriteDataAsync(signature.ToArray(), GetZlpTimeoutMs(), cancellationToken).ConfigureAwait(false);
            var completion = await ReceiveResponseAsync(cancellationToken, null, completionLogs.Add)
                .ConfigureAwait(false);
            return ClassifySignatureCompletion(completion, preambleLogs, completionLogs);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>
    /// Sends the signature inside the command's <c>value</c> attribute and reads its single response.
    /// <para>把签名放入命令的 <c>value</c> 属性发送，并读取其单次响应。</para>
    /// </summary>
    /// <param name="signature">The signature bytes carried by the command. <para>由命令携带的签名字节。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交换。</para></param>
    /// <returns>The outcome for this form. <para>该形式的结果。</para></returns>
    private async Task<VendorSignatureOutcome> SendInlineSignatureOwnedAsync(ReadOnlyMemory<byte> signature,
        CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        var response = await SendCommandAndWaitResponseAsync(
            FirehoseXmlBuilder.BuildSigValue("sig", Convert.ToHexString(signature.Span)),
            cancellationToken, logObserver: logs.Add).ConfigureAwait(false);
        return ClassifySignatureCompletion(response, [], logs);
    }

    /// <summary>
    /// Decides whether a completed signature exchange was accepted: only a plain acknowledgement accompanied by
    /// the target's own completion log counts, because a bare acknowledgement is also what a refusal returns.
    /// <para>判定签名交换是否被接受：只有伴随目标自身完成日志的普通确认才算通过，因为拒绝也会返回裸确认。</para>
    /// </summary>
    /// <param name="response">The completion response. <para>完成响应。</para></param>
    /// <param name="preambleLogs">Logs received before the signature bytes. <para>签名字节之前收到的日志。</para></param>
    /// <param name="completionLogs">Logs received with the completion. <para>与完成响应一起收到的日志。</para></param>
    /// <returns><see cref="VendorSignatureOutcome.Accepted"/> only on positive completion evidence.
    /// <para>仅在有正面完成证据时返回 <see cref="VendorSignatureOutcome.Accepted"/>。</para></returns>
    private VendorSignatureOutcome ClassifySignatureCompletion(FirehoseResponse response,
        List<string> preambleLogs, List<string> completionLogs)
    {
        var accepted = response.IsAck && !response.IsRawMode
            && preambleLogs.Concat(completionLogs)
                .Any(log => log.Contains(AuthenticatedLogToken, StringComparison.OrdinalIgnoreCase));
        if (accepted)
        {
            Log("The target reported the signature as accepted.");
            return VendorSignatureOutcome.Accepted;
        }

        Log($"The target did not report a completed authentication; response: {response.RawValue ?? "none"}.");
        return VendorSignatureOutcome.SignatureRejected;
    }
}