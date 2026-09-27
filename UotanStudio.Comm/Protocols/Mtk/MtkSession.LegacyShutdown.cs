using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Selects the explicit boot request carried by a Legacy FINISH command.
/// <para>选择 Legacy FINISH 命令携带的显式启动请求。</para></summary>
public enum MtkLegacyShutdownMode
{
    /// <summary>Requests the agent's normal shutdown behavior.
    /// <para>请求代理的正常关闭行为。</para></summary>
    PowerOff,
    /// <summary>Requests normal system startup.
    /// <para>请求正常系统启动。</para></summary>
    NormalBoot,
    /// <summary>Requests Fastboot.
    /// <para>请求 Fastboot。</para></summary>
    Fastboot
}

public sealed partial class MtkSession
{
    /// <summary>Explicitly finishes a Legacy session, entering a terminal state after both acknowledgements.
    /// <para>显式结束 Legacy 会话，在两个确认后进入终态。</para></summary>
    /// <remarks>No startup, storage transfer or disposal operation calls this automatically.
    /// Actual power or boot transition remains unverified; missing acknowledgements fault the session without retry.
    /// <para>启动、存储传输或释放操作均不会自动调用此方法。实际电源或启动转换仍未经验证；
    /// 缺失确认会使会话故障且不重试。</para></remarks>
    /// <param name="mode">The explicitly authorized shutdown or boot request.
    /// <para>显式授权的关闭或启动请求。</para></param>
    /// <param name="cancellationToken">Cancellation for the command exchange.
    /// <para>命令交换的取消令牌。</para></param>
    /// <returns>A task completing after acknowledgement and local disconnection.
    /// <para>确认并完成本地断开后完成的任务。</para></returns>
    public async Task ShutdownLegacyAsync(MtkLegacyShutdownMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.LegacyStorageReady);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await WriteAsync([0xD9], _timeoutMs, cancellationToken).ConfigureAwait(false);
                await RequireLegacyByteAsync(0x5A, "finish request", cancellationToken).ConfigureAwait(false);
                var value = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(value, (uint)mode);
                await WriteAsync(value, _timeoutMs, cancellationToken).ConfigureAwait(false);
                await RequireLegacyByteAsync(0x5A, "finish completion", cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
            }
            catch
            {
                FaultLegacy();
                throw;
            }
            _state = MtkSessionState.LegacyShutdownAcknowledged;
            LegacyStorage = null;
            LegacyCompletion = null;
            _legacyOptions = null;
            Transport.Disconnect();
        }
        finally
        {
            _operation.Release();
        }
    }
}