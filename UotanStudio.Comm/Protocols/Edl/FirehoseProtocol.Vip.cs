namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class FirehoseProtocol
{
    private FirehoseVipOptions? _vip;
    private int _vipTableIndex;
    private int _vipPacketsRemaining;
    private long _vipSectorSize;
    private bool _outboundPacketStarted;

    /// <summary>
    /// Installs a VIP table sequence before any Firehose packet is sent.
    /// <para>在发送任何 Firehose 包前安装 VIP 表序列。</para>
    /// </summary>
    /// <remarks>
    /// <para>The signed table is sent and acknowledged immediately before the first outgoing packet. Requires
    /// an explicit sector size; automatic storage probes are not inserted into the signed sequence.</para>
    /// <para>首个出站包之前立即发送并确认签名表。必须显式指定扇区大小，不会向签名序列插入自动存储探测。</para>
    /// </remarks>
    /// <param name="options">The immutable table and packet profile.
    /// <para>不可变的表与包配置。</para></param>
    /// <param name="cancellationToken">Cancellation while waiting for exclusive transport ownership.
    /// <para>等待独占传输期间的取消令牌。</para></param>
    /// <returns>A task completing when the profile is installed without device I/O.
    /// <para>在不执行设备 I/O 的情况下安装配置后完成的任务。</para></returns>
    /// <exception cref="InvalidOperationException">Packets have already been sent, VIP is installed,
    /// or the sector and payload sizes do not match.
    /// <para>已经发送过包、已安装 VIP，或扇区与有效载荷大小不匹配。</para></exception>
    public async Task ConfigureVipAsync(FirehoseVipOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        ConfigureVipOwned(options);
    }

    internal void ConfigureVipOwned(FirehoseVipOptions options)
    {
        ThrowIfDisposed();
        if (_outboundPacketStarted || _vip is not null || Configuration.SectorSize is <= 0 or > 65536
            || options.ProgramPayloadSize % Configuration.SectorSize != 0)
        {
            throw new InvalidOperationException("VIP requires an unused session and an explicit matching sector size.");
        }
        _vip = options;
        _vipSectorSize = Configuration.SectorSize;
        Configuration.MaxPayloadSizeToTarget = options.ProgramPayloadSize;
    }

    private async Task SendVipTableIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_vip is not { } options || _vipPacketsRemaining != 0)
        {
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.TableTimeoutMs);
        var token = deadline.Token;
        try
        {
            using var source = await options.TableProvider.OpenTableAsync(_vipTableIndex, token).ConfigureAwait(false)
                ?? throw new FirehoseException("The required VIP table is missing; the packet was not sent.");
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (!source.CanRead || !source.CanSeek)
            {
                throw new FirehoseException("VIP table streams must be readable and seekable.");
            }
            var remaining = source.Length - source.Position;
            var maximum = _vipTableIndex == 0 ? 16 * 1024 * 1024 : 8192;
            if (remaining <= 0 || remaining > maximum || (_vipTableIndex != 0 && remaining % 32 != 0))
            {
                throw new FirehoseException("The VIP table length does not match the selected profile.");
            }
            var bytes = new byte[(int)remaining];
            await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var count = await _transport.WriteAsync(bytes, 0, bytes.Length, GetZlpTimeoutMs(), token).ConfigureAwait(false);
            if (count != bytes.Length)
            {
                throw new FirehoseException("The VIP table write was incomplete; reconnect before continuing.");
            }
            var response = await ReceiveResponseAsync(token).ConfigureAwait(false);
            if (!response.IsAck || response.IsRawMode)
            {
                throw new FirehoseException("The VIP table was not acknowledged outside raw mode.");
            }
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            _vipPacketsRemaining = _vipTableIndex == 0 ? 53 : 255;
            _vipTableIndex = checked(_vipTableIndex + 1);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _faulted = true;
            throw new TimeoutException("The VIP table deadline expired; reconnect before continuing.", error);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private async Task WritePacketAsync(byte[] bytes, int count, int timeoutMs, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        _outboundPacketStarted = true;
        await SendVipTableIfNeededAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var written = await _transport.WriteAsync(bytes, 0, count, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (written != count)
        {
            _faulted = true;
            throw new EdlException(EdlErrorCode.WritePortTimeout, "Incomplete Firehose packet write; reconnect before continuing.");
        }
        if (_vip is not null)
        {
            _vipPacketsRemaining--;
        }
    }

    private void ValidateVipPayloadSize()
    {
        if (_vip is { } vip && (Configuration.MaxPayloadSizeToTarget != vip.ProgramPayloadSize
            || Configuration.SectorSize != _vipSectorSize))
        {
            throw new FirehoseException("VIP packet sizes cannot change without regenerating and signing the packet sequence.");
        }
    }
}