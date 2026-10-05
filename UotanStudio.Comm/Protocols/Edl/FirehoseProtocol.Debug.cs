namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Debug-stage memory access: <c>&lt;poke&gt;</c> writes and peek-driven <c>&lt;memcpy&gt;</c>,
/// mirroring the reference client (bkerler/edl firehose.py cmd_poke/cmd_memcpy: up to 8 bytes per
/// poke command, payload carried as a big-endian hex literal, and an uppercase-attribute retry when
/// the target rejects the lowercase form).
/// <para>调试级内存访问：<c>&lt;poke&gt;</c> 写入与基于 peek 的 <c>&lt;memcpy&gt;</c>，镜像参考
/// 客户端（bkerler/edl firehose.py 的 cmd_poke/cmd_memcpy：每条 poke 命令最多 8 字节，负载以
/// 大端十六进制字面量携带，目标拒绝小写属性名时按大写属性名重试一次）。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    /// <summary>
    /// Writes up to 8 bytes to target memory with a single <c>&lt;poke&gt;</c> command.
    /// <para>用单条 <c>&lt;poke&gt;</c> 命令向目标内存写入最多 8 字节。</para>
    /// </summary>
    /// <param name="address">The target memory address; non-negative. <para>目标内存地址；非负。</para></param>
    /// <param name="data">The bytes to write; 1 to 8 bytes. <para>待写入字节；1 到 8 字节。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The final target acknowledgement. <para>目标的最终确认响应。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="address"/> is negative or the data
    /// is empty or longer than 8 bytes.
    /// <para><paramref name="address"/> 为负，或数据为空/超过 8 字节。</para></exception>
    /// <exception cref="FirehoseException">The target rejects the poke in both attribute spellings.
    /// <para>目标在两种属性拼写下都拒绝 poke。</para></exception>
    /// <exception cref="EdlException">The transport fails or times out. <para>传输层失败或超时。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled. <para>令牌被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The protocol has been disposed.
    /// <para>协议已被释放。</para></exception>
    public async Task<FirehoseResponse> PokeAsync(long address, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await PokeCoreAsync(address, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a bounded memory range on the target by peeking the source and poking the destination
    /// in 8-byte steps.
    /// <para>通过读取源地址并按 8 字节步长写入目标地址，在目标上复制一段有界内存。</para>
    /// </summary>
    /// <param name="destinationAddress">The destination address; non-negative. <para>目标端地址；非负。</para></param>
    /// <param name="sourceAddress">The source address; non-negative. <para>源端地址；非负。</para></param>
    /// <param name="sizeInBytes">The byte count; 1 to 16 MiB. <para>字节数；1 到 16 MiB。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The final poke acknowledgement of the last chunk.
    /// <para>最后一个数据块的 poke 最终确认。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Any address is negative or the size is outside
    /// 1..16 MiB. <para>任一地址为负，或大小不在 1..16 MiB 范围内。</para></exception>
    /// <exception cref="FirehoseException">The peek or a poke is rejected.
    /// <para>peek 或某次 poke 被目标拒绝。</para></exception>
    /// <exception cref="EdlException">The transport fails or times out. <para>传输层失败或超时。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled. <para>令牌被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The protocol has been disposed.
    /// <para>协议已被释放。</para></exception>
    public async Task<FirehoseResponse> MemcpyAsync(long destinationAddress, long sourceAddress, long sizeInBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(destinationAddress);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceAddress);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeInBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeInBytes, 16 * 1024 * 1024);
        _ = checked(sourceAddress + sizeInBytes);
        _ = checked(destinationAddress + sizeInBytes);
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        var (_, data) = await PeekCoreAsync(sizeInBytes, sourceAddress, cancellationToken)
            .ConfigureAwait(false);
        FirehoseResponse last = new() { IsAck = true };
        for (var offset = 0L; offset < sizeInBytes; offset += 8)
        {
            var chunkLength = (int)Math.Min(8, sizeInBytes - offset);
            last = await PokeCoreAsync(destinationAddress + offset,
                data.AsMemory((int)offset, chunkLength), cancellationToken).ConfigureAwait(false);
            if (!last.IsAck)
            {
                throw new FirehoseException(
                    $"The target rejected the memcpy poke at 0x{destinationAddress + offset:X}.");
            }
        }

        return last;
    }

    /// <summary>
    /// Sends one bounded poke on an owned lease, retrying once with the uppercase attribute spelling
    /// some programmers require.
    /// <para>在已持有的租约上发送一条有界 poke，部分 programmer 要求大写属性名时按其重试一次。</para>
    /// </summary>
    private async Task<FirehoseResponse> PokeCoreAsync(long address, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(address);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(data.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, 8);
        _ = checked(address + data.Length);
        var content = Convert.ToHexStringLower(data.Span);
        var xml = FirehoseXmlBuilder.BuildPoke(address, data.Length, $"0x{content}");
        var response = await SendCommandAndWaitResponseAsync(xml, cancellationToken).ConfigureAwait(false);
        if (response.IsAck)
        {
            return response;
        }

        var retry = FirehoseXmlBuilder.BuildPoke(address, data.Length, $"0x{content}", uppercaseSize: true);
        return await SendCommandAndWaitResponseAsync(retry, cancellationToken).ConfigureAwait(false);
    }
}