using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Sends explicitly selected EMI bytes to DA1 and requires its DRAM initialization acknowledgement.
    /// <para>向 DA1 发送显式选定的 EMI 字节，并要求其 DRAM 初始化确认。</para></summary>
    /// <param name="parameters">The readable EMI parameter stream, not an entire preloader container.
    /// <para>可读的 EMI 参数流，而不是整个 preloader 容器。</para></param>
    /// <param name="length">The parameter byte count, limited to 1 MiB.
    /// <para>参数字节数，上限为 1 MiB。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task that completes once DA1 acknowledges external RAM initialization.
    /// <para>在 DA1 确认外部 RAM 初始化完成后返回的异步任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is null.
    /// <para><paramref name="parameters"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive or exceeds 1 MiB.
    /// <para><paramref name="length"/> 不是正数或超过 1 MiB。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="parameters"/> is unreadable or shorter than <paramref name="length"/>.
    /// <para><paramref name="parameters"/> 不可读或短于 <paramref name="length"/>。</para></exception>
    /// <exception cref="InvalidOperationException">DA1 is not stage-one ready or the channel is gone.
    /// <para>DA1 未处于 stage-one ready 状态或通道已失效。</para></exception>
    /// <exception cref="MtkProtocolException">DA1 rejected the request; the session faults.
    /// <para>DA1 拒绝请求；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled waiting or the exchange.
    /// <para>调用方取消了等待或交换。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task InitializeXFlashExternalRamAsync(Stream parameters, int length,
        CancellationToken cancellationToken = default)
    {
        MtkXFlashChannel.ValidateSource(parameters, length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 1024 * 1024);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStageOneReady);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            try
            {
                await SendXFlashWordAsync(channel, 0x01000A, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "external RAM request", cancellationToken).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, (uint)length, cancellationToken).ConfigureAwait(false);
                await channel.SendStreamAsync(1, parameters, length, null, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "external RAM initialization", cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _xflashExternalRamInitialized = true;
            }
            catch (Exception error)
            {
                CaptureUploadFailure(error);
                _state = MtkSessionState.Faulted;
                _xflash = null;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Streams an explicitly selected DA2 image to its profile-provided address and waits
    /// for upload and execution acknowledgement. No signature stripping or patching is performed.
    /// <para>将显式选定的 DA2 镜像流式传输到其配置文件给出的地址，并等待上传与执行确认。
    /// 不执行任何签名剥离或修补。</para>
    /// </summary>
    /// <param name="image">The exact executable DA2 region, positioned at its first byte.
    /// <para>精确的可执行 DA2 区域，定位在其首字节处。</para></param>
    /// <param name="loadAddress">The word-aligned 64-bit load address from the selected device profile.
    /// <para>来自所选设备配置文件的按字对齐的 64 位加载地址。</para></param>
    /// <param name="length">The exact byte count to send, limited to 64 MiB.
    /// <para>要发送的精确字节数，上限 64 MiB。</para></param>
    /// <param name="progress">Optional cumulative payload-byte progress.
    /// <para>可选的累计有效载荷字节进度。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.
    /// <para>操作取消令牌。</para></param>
    /// <returns>A task that completes after DA2 upload and execution are acknowledged.
    /// <para>在 DA2 上传与执行均被确认后返回的异步任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.
    /// <para><paramref name="image"/> 为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive, exceeds 64 MiB,
    /// or <paramref name="loadAddress"/> is unaligned or its range overflows.
    /// <para><paramref name="length"/> 不是正数、超过 64 MiB，或 <paramref name="loadAddress"/> 未对齐、范围溢出。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="image"/> is unreadable or shorter than <paramref name="length"/>.
    /// <para><paramref name="image"/> 不可读或短于 <paramref name="length"/>。</para></exception>
    /// <exception cref="InvalidOperationException">DA1 is not stage-one ready, EMI setup is unacknowledged
    /// on a non-preloader connection, or the channel is gone.
    /// <para>DA1 未处于 stage-one ready 状态、非 preloader 连接下 EMI 初始化未确认，或通道已失效。</para></exception>
    /// <exception cref="MtkProtocolException">The agent rejected the upload or execution; the session faults.
    /// <para>agent 拒绝上传或执行；会话进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">The caller cancelled waiting or the exchange.
    /// <para>调用方取消了等待或交换。</para></exception>
    /// <exception cref="ObjectDisposedException">The session is disposed.
    /// <para>会话已被释放。</para></exception>
    public async Task UploadXFlashStageTwoAsync(Stream image, ulong loadAddress, int length,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        MtkXFlashChannel.ValidateSource(image, length);
        if ((loadAddress & 3) != 0 || loadAddress > ulong.MaxValue - (uint)length)
        {
            throw new ArgumentOutOfRangeException(nameof(loadAddress), "The DA2 address is unaligned or its range overflows.");
        }
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStageOneReady);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_xflashExternalRamInitialized && _xflashConnectionAgent != MtkXFlashConnectionAgent.Preloader)
            {
                throw new InvalidOperationException("DA2 requires acknowledged EMI setup or a queried preloader connection.");
            }
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            try
            {
                await SendXFlashWordAsync(channel, 0x010008, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "DA2 load request", cancellationToken).ConfigureAwait(false);
                var range = new byte[16];
                BinaryPrimitives.WriteUInt64LittleEndian(range, loadAddress);
                BinaryPrimitives.WriteUInt64LittleEndian(range.AsSpan(8), (uint)length);
                await channel.SendAsync(1, range, cancellationToken).ConfigureAwait(false);
                await channel.SendStreamAsync(1, image, length, progress, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "DA2 upload", cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "DA2 execution", cancellationToken, allowSync: true).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _state = MtkSessionState.XFlashStageTwoStarted;
            }
            catch (Exception error)
            {
                CaptureUploadFailure(error);
                _state = MtkSessionState.Faulted;
                _xflash = null;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }
}