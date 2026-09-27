using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Queries DA2 SLA and, when enabled, completes one authorized challenge-response exchange.
    /// Unknown or rejected status never means authentication is disabled. No key fallback is attempted.
    /// <para>查询 DA2 SLA，并在启用时完成一次授权的挑战响应交换。
    /// 未知或被拒绝的状态不表示认证已禁用；不会尝试回退密钥。</para>
    /// </summary>
    /// <param name="responder">The explicit signing provider, required only when the device reports SLA enabled.
    /// <para>显式签名提供方，仅在设备报告启用 SLA 时必需。</para></param>
    /// <param name="timeoutMs">The cooperative exchange deadline, from 1 ms to 24 hours.
    /// <para>协作式交换截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and the exchange.
    /// <para>等待与交换的取消令牌。</para></param>
    /// <returns>True after an accepted response; false only when DA2 explicitly reports SLA disabled.
    /// <para>响应被接受后返回 true；仅当 DA2 明确报告禁用 SLA 时返回 false。</para></returns>
    /// <exception cref="MtkProtocolException">The reply is malformed or the device rejects the exchange.
    /// <para>回复格式错误或设备拒绝交换。</para></exception>
    /// <exception cref="InvalidOperationException">DA2 is not ready or a required responder is missing.
    /// <para>DA2 未就绪或缺少必需的响应提供方。</para></exception>
    /// <exception cref="TimeoutException">The cooperative exchange deadline expired.
    /// <para>协作式交换截止时间已到。</para></exception>
    public async Task<bool> AuthenticateXFlashStageTwoAsync(IMtkDaSlaResponder? responder = null,
        int timeoutMs = 60000, CancellationToken cancellationToken = default)
    {
        ValidateSlaTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStageTwoStarted);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            byte[]? information = null;
            byte[]? response = null;
            try
            {
                var status = await QueryXFlashControlAsync(channel, 0x040016, token, 4).ConfigureAwait(false);
                if (status.Length != 4)
                {
                    throw new MtkProtocolException("The DA2 SLA status must contain one word.");
                }
                var enabled = BinaryPrimitives.ReadUInt32LittleEndian(status);
                if (enabled == 0)
                {
                    return false;
                }
                if (enabled != 1)
                {
                    throw new MtkProtocolException("The DA2 returned an unknown SLA status.");
                }
                if (responder is null)
                {
                    throw new InvalidOperationException("DA2 requires an explicitly authorized SLA response provider.");
                }
                information = await QueryXFlashControlAsync(channel, 0x040013, token, 64 * 1024).ConfigureAwait(false);
                if (information.Length < 20)
                {
                    throw new MtkProtocolException("The DA2 device information does not contain the full SLA challenge.");
                }
                response = await responder.CreateResponseAsync(DeviceInfo!, information,
                    information.AsMemory(4, 16), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (response is null || response.Length is 0 or > 4096)
                {
                    throw new MtkProtocolException("The DA2 SLA response provider returned an invalid length.");
                }
                await SendXFlashWordAsync(channel, 0x010009, token).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "SLA device control", token).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, 0x02000B, token).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "SLA remote policy request", token).ConfigureAwait(false);
                await channel.SendAsync(1, response, token).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "SLA response", token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                return true;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _state = MtkSessionState.Faulted;
                _xflash = null;
                throw new TimeoutException("The DA2 SLA exchange deadline expired; reconnect before continuing.", error);
            }
            catch
            {
                _state = MtkSessionState.Faulted;
                _xflash = null;
                throw;
            }
            finally
            {
                if (information is not null)
                {
                    CryptographicOperations.ZeroMemory(information);
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
}