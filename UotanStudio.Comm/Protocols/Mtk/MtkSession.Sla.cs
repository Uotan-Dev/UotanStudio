using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Uploads a DA with one explicitly configured Boot ROM SLA response if D7 reports
    /// 0x1D0D. The original upload is continued without resending D7 or trying another key.
    /// <para>当 D7 报告 0x1D0D 时，使用一个显式配置的 Boot ROM SLA 响应上传 DA。
    /// 原始上传会被继续，不会重发 D7，也不会尝试其他密钥。</para>
    /// </summary>
    /// <remarks>
    /// Uses the byte-stream profile. No credentials, signature algorithm or key selection
    /// are built in. The responder must not call back into this session or retain buffers.
    /// Cancellation remains cooperative while the responder owns the challenge; its task
    /// is awaited before buffers are cleared. A successful exchange is not a blanket grant
    /// of device authorization, and does not imply DA execution.
    /// </remarks>
    /// <param name="image">Caller-owned DA stream.
    /// <para>调用方拥有的 DA 流。</para></param>
    /// <param name="loadAddress">Selected DA load address.
    /// <para>选定的 DA 加载地址。</para></param>
    /// <param name="length">Total selected DA bytes, including the signature suffix.
    /// <para>选定的 DA 总字节数，包含签名为后缀的部分。</para></param>
    /// <param name="responder">Explicitly authorized response provider.
    /// <para>显式授权的响应提供方。</para></param>
    /// <param name="signatureLength">Signature suffix length included in the image.
    /// <para>镜像中包含的签名后缀长度。</para></param>
    /// <param name="slaTimeoutMs">Cooperative SLA exchange deadline, from 1 ms to 24 hours.
    /// <para>SLA 交换的协作式截止时间，范围从 1 毫秒到 24 小时。</para></param>
    /// <param name="progress">Cumulative DA bytes, excluding authentication traffic.
    /// <para>累计 DA 字节数，不含认证流量。</para></param>
    /// <param name="cancellationToken">Cancellation for the whole upload.
    /// <para>用于整个上传过程的取消令牌。</para></param>
    /// <returns>A task that completes when the SLA-gated DA upload has finished.
    /// <para>在受 SLA 门控的 DA 上传结束后完成的异步任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="responder"/> is null.
    /// <para><paramref name="responder"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slaTimeoutMs"/> is outside 1 ms to 24 hours.
    /// <para><paramref name="slaTimeoutMs"/> 超出 1 毫秒到 24 小时的范围。</para></exception>
    /// <exception cref="MtkProtocolException">The SLA exchange or the upload was rejected by the target.
    /// <para>SLA 交换或上传被目标拒绝。</para></exception>
    /// <exception cref="TimeoutException">The SLA deadline expired; the upload must not be replayed.
    /// <para>SLA 截止时间到期；不得重放本次上传。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled at any cooperative point.
    /// <para>调用方在任一协作点取消。</para></exception>
    public Task UploadDownloadAgentWithSlaAsync(Stream image, uint loadAddress, int length,
        IMtkBootRomSlaResponder responder, int signatureLength = 0, int slaTimeoutMs = 60000,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responder);
        ValidateSlaTimeout(slaTimeoutMs);
        return UploadDownloadAgentCoreAsync(image, loadAddress, length, signatureLength, progress,
            responder, slaTimeoutMs, cancellationToken);
    }

    internal static void ValidateSlaTimeout(int timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutMs, 86400000);
    }

    private async Task RespondToBootRomSlaOwnedAsync(IMtkBootRomSlaResponder responder, int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        byte[]? challenge = null;
        byte[]? response = null;
        try
        {
            await EchoAsync([0xE3], token).ConfigureAwait(false);
            var status = BinaryPrimitives.ReadUInt16BigEndian(
                await ReadAsync(2, _timeoutMs, token).ConfigureAwait(false));
            if (status == 0x7017)
            {
                return;
            }
            CheckStatus(status, "SLA challenge request");
            var challengeLength = BinaryPrimitives.ReadUInt32BigEndian(
                await ReadAsync(4, _timeoutMs, token).ConfigureAwait(false));
            if (challengeLength is 0 or > 4096)
            {
                throw new MtkProtocolException("The SLA challenge length is outside the supported bounds.");
            }
            challenge = await ReadAsync((int)challengeLength, _timeoutMs, token).ConfigureAwait(false);
            response = await responder.CreateResponseAsync(DeviceInfo!, challenge, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (response is null || response.Length is 0 or > 4096)
            {
                throw new MtkProtocolException("The SLA response provider returned an invalid length.");
            }
            var lengthField = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lengthField, (uint)response.Length);
            await WriteAsync(lengthField, _timeoutMs, token).ConfigureAwait(false);
            var echoedLength = BinaryPrimitives.ReadUInt32BigEndian(
                await ReadAsync(4, _timeoutMs, token).ConfigureAwait(false));
            if (echoedLength != response.Length)
            {
                throw new MtkProtocolException("The SLA response length acknowledgement did not match.");
            }
            await ReadStatusAsync("SLA response request", token).ConfigureAwait(false);
            await WriteAsync(response, _timeoutMs, token).ConfigureAwait(false);
            var finalStatus = BinaryPrimitives.ReadUInt32BigEndian(
                await ReadAsync(4, _timeoutMs, token).ConfigureAwait(false));
            if (finalStatus >= 0xFF)
            {
                throw new MtkProtocolException("The target rejected the SLA response.", finalStatus);
            }
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The SLA exchange deadline expired; the upload must not be replayed.", error);
        }
        finally
        {
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
}