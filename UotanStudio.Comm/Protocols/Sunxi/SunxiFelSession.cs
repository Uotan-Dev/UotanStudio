namespace UotanStudio.Comm.Protocols.Sunxi;

/// <summary>
/// FEL session over the USB boot ROM: identity handshake, chunked memory write and read, and address
/// execution. FEL runs before any firmware does, so every transfer is split into 64 KiB stages that the
/// boot ROM acknowledges with a status read.
/// <para>USB boot ROM 之上的 FEL 会话：身份握手、分块内存读写与地址执行。FEL 先于任何固件运行，
/// 因此每次传输都拆成 64 KiB 阶段，由 boot ROM 以一次状态读取确认。</para>
/// </summary>
public sealed class SunxiFelSession : SunxiEfexSession
{
    /// <summary>Creates a FEL session over one bulk transport. <para>在一条批量传输之上创建 FEL 会话。</para></summary>
    /// <param name="transport">Bulk transport the session runs on. <para>会话所使用的批量传输。</para></param>
    internal SunxiFelSession(ISunxiEfexTransport transport)
        : base(transport)
    {
    }

    /// <summary>
    /// Performs the identity handshake and rejects a device that is not in FEL mode.
    /// <para>执行身份握手，并拒绝不在 FEL 模式下的设备。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the handshake. <para>取消本次握手。</para></param>
    /// <returns>The reported identity. <para>上报的身份信息。</para></returns>
    /// <exception cref="SunxiEfexException">The device is not in FEL mode, or answered with a non-zero status.
    /// <para>设备不在 FEL 模式，或以非零状态应答。</para></exception>
    public async Task<SunxiDeviceIdentity> OpenAsync(CancellationToken cancellationToken = default)
    {
        var identity = await VerifyDeviceAsync(cancellationToken).ConfigureAwait(false);
        if (identity.Mode != SunxiDeviceMode.Fel)
        {
            throw new SunxiEfexException(
                $"The device reports mode {identity.Mode} but the FEL session requires FEL mode.");
        }

        return identity;
    }

    /// <summary>
    /// Calls an address the host already wrote, which is how a FES service or boot loader is started.
    /// <para>调用主机已写入的地址，用于启动 FES 服务或引导程序。</para>
    /// </summary>
    /// <param name="address">Entry address. <para>入口地址。</para></param>
    /// <param name="cancellationToken">Cancels the command. <para>取消本次命令。</para></param>
    /// <returns>A task that completes when the device acknowledged the call.
    /// <para>设备确认调用后完成的任务。</para></returns>
    /// <exception cref="SunxiEfexException">The device answered with a non-zero status.
    /// <para>设备以非零状态应答。</para></exception>
    public async Task ExecuteAsync(uint address, CancellationToken cancellationToken = default)
    {
        await SendFelRequestAsync(SunxiEfexCommand.FelExec, address, 0, cancellationToken).ConfigureAwait(false);
        await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes host bytes into device memory in 64 KiB stages.
    /// <para>以 64 KiB 阶段把主机字节写入设备内存。</para>
    /// </summary>
    /// <param name="address">First destination address. <para>首个目标地址。</para></param>
    /// <param name="data">Bytes to write. <para>待写入的字节。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消本次写入。</para></param>
    /// <returns>A task that completes when every stage is acknowledged. <para>所有阶段都被确认后完成的任务。</para></returns>
    /// <exception cref="OverflowException">The address range passes the 32-bit address space.
    /// <para>地址范围越过 32 位地址空间。</para></exception>
    /// <exception cref="SunxiEfexException">A stage was answered with a non-zero status.
    /// <para>某个阶段以非零状态应答。</para></exception>
    public async Task WriteAsync(uint address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        var current = address;
        while (offset < data.Length)
        {
            var length = Math.Min(SunxiEfexConstants.MaximumTransfer, data.Length - offset);
            await SendFelRequestAsync(SunxiEfexCommand.FelWrite, current, (uint)length, cancellationToken)
                .ConfigureAwait(false);
            await WriteStageAsync(data.Slice(offset, length), cancellationToken).ConfigureAwait(false);
            await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            current = checked(current + (uint)length);
            offset += length;
        }
    }

    /// <summary>
    /// Reads device memory into host bytes in 64 KiB stages.
    /// <para>以 64 KiB 阶段把设备内存读到主机。</para>
    /// </summary>
    /// <param name="address">First source address. <para>首个源地址。</para></param>
    /// <param name="buffer">Destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>A task that completes when the buffer is full. <para>缓冲区填满后完成的任务。</para></returns>
    /// <exception cref="OverflowException">The address range passes the 32-bit address space.
    /// <para>地址范围越过 32 位地址空间。</para></exception>
    /// <exception cref="SunxiEfexException">A stage was answered with a non-zero status.
    /// <para>某个阶段以非零状态应答。</para></exception>
    public async Task ReadAsync(uint address, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        var current = address;
        while (offset < buffer.Length)
        {
            var length = Math.Min(SunxiEfexConstants.MaximumTransfer, buffer.Length - offset);
            await SendFelRequestAsync(SunxiEfexCommand.FelRead, current, (uint)length, cancellationToken)
                .ConfigureAwait(false);
            await ReadStageAsync(buffer.Slice(offset, length), cancellationToken).ConfigureAwait(false);
            await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            current = checked(current + (uint)length);
            offset += length;
        }
    }
}