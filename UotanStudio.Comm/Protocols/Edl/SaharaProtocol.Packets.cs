using System.Buffers;
using System.Runtime.InteropServices;

namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class SaharaProtocol
{
    /// <summary>
    /// Pause between two reads that returned nothing, so a transport that keeps answering a zero-length read
    /// immediately cannot spin the budget away in a tight loop.
    /// <para>两次零长度读之间的停顿，使立即返回零长度读的传输无法以紧循环耗掉预算。</para>
    /// </summary>
    private const int ZeroLengthReadRetryDelayMs = 5;

    /// <summary>
    /// Reads up to <paramref name="length"/> bytes, returning only the bytes actually received.
    /// <para>读取最多 <paramref name="length"/> 个字节，仅返回实际收到的字节。</para>
    /// </summary>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时（毫秒）。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The bytes actually read. <para>实际读取的字节。</para></returns>
    private async Task<byte[]> ReadChunkAsync(int length, int timeoutMs, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        int n;
        try
        {
            n = await _transport.ReadAsync(buffer, 0, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new EdlException(EdlErrorCode.ReadPortTimeout, ex.Message, ex);
        }
        if (n < 0 || n > length)
        {
            throw new SaharaException("The transport returned an invalid receive length.");
        }
        if (n == 0)
        {
            return [];
        }

        if (n == length)
        {
            return buffer;
        }

        var result = new byte[n];
        Array.Copy(buffer, result, n);
        return result;
    }

    /// <summary>
    /// Reads the first packet of a link into one buffer that can hold the largest packet this host dispatches,
    /// returning whatever arrived once the target has said something, and spending the whole budget a
    /// zero-length read would otherwise end.
    /// <para>把链路上的首个数据包读入一个足以容纳本主机分发的最大数据包的缓冲区，在目标开口后返回实际到达的字节，
    /// 并耗尽零长度读本会提前结束的整段预算。</para>
    /// </summary>
    /// <remarks>
    /// A USB bulk read completes on the packet boundary and never carries a second packet, and no target sends a
    /// second packet before the host answers the first, so a buffer wider than the header cannot steal bytes from
    /// the following packet. Asking for the header alone instead splits one packet across two reads; on a
    /// bootrom that announces HELLO as a single 48-byte packet that turns a one-round-trip handshake into two and
    /// leaves the excess bytes of the packet in the endpoint for a read that no longer describes them.
    /// <para>USB 批量读取在数据包边界完成且不会携带第二个数据包，且任何目标都不会在主机应答首个数据包之前发送
    /// 第二个数据包，因此比包头更宽的缓冲区不会窃取后续数据包的字节。只请求包头则会把同一个数据包拆成两次读取；
    /// 对以单个 48 字节包宣告 HELLO 的 bootrom，这会把一次往返的握手变成两次，并把该数据包多余的字节留在端点里，
    /// 交给一个已不再描述它们的读取。</para>
    /// </remarks>
    /// <param name="bound">Upper bound for the prefix in bytes.
    /// <para>前缀字节数上限。</para></param>
    /// <param name="timeoutMs">Total budget in milliseconds for the whole read.
    /// <para>整次读取的毫秒总预算。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Between one and <paramref name="bound"/> bytes, in the order the target sent them.
    /// <para>1 到 <paramref name="bound"/> 字节，按目标发送顺序排列。</para></returns>
    /// <exception cref="EdlException">The budget expired before any byte arrived, reported as
    /// <see cref="EdlErrorCode.ReadPortTimeout"/> so the Sahara recovery paths treat it as a silent link.
    /// <para>在任何字节到达之前预算已耗尽，以 <see cref="EdlErrorCode.ReadPortTimeout"/> 上报，使 Sahara 恢复路径
    /// 将其视为静默链路。</para></exception>
    /// <exception cref="SaharaException">The transport reported an invalid receive length.
    /// <para>传输报告了无效的接收长度。</para></exception>
    private async Task<byte[]> ReadPacketPrefixAsync(int bound, int timeoutMs, CancellationToken cancellationToken)
    {
        var buf = ArrayPool<byte>.Shared.Rent(bound);
        try
        {
            var total = 0;
            var zeroLengthReads = 0;
            var started = Environment.TickCount64;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remainingMs = timeoutMs - (Environment.TickCount64 - started);
                if (remainingMs <= 0)
                {
                    throw new EdlException(EdlErrorCode.ReadPortTimeout,
                        $"The Sahara packet receive deadline expired after {timeoutMs} ms with {total} of {bound} bytes received" +
                        (zeroLengthReads == 0 ? "." : $" and {zeroLengthReads} zero-length read(s)."));
                }

                int n;
                try
                {
                    n = await _transport.ReadAsync(buf, total, bound - total, (int)remainingMs, cancellationToken);
                }
                catch (TimeoutException ex)
                {
                    throw new EdlException(EdlErrorCode.ReadPortTimeout, ex.Message, ex);
                }
                if (n < 0 || n > bound - total)
                {
                    throw new SaharaException("The transport returned an invalid receive length.");
                }
                if (n == 0)
                {
                    // A zero-length read is "nothing arrived yet", not an expired deadline; spend the remaining
                    // budget so a cleared STALL is not reported as a timeout.
                    zeroLengthReads++;
                    Log($"Read returned 0 byte(s) with {remainingMs} ms of budget left " +
                        $"(occurrence {zeroLengthReads}); retrying instead of reporting a timeout.");
                    await Task.Delay(ZeroLengthReadRetryDelayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                total += n;
                if (total >= Marshal.SizeOf<SaharaPacketHeader>())
                {
                    return buf.AsSpan(0, total).ToArray();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>
    /// Reads an exact number of bytes from the transport, spending the whole budget a zero-length read would
    /// otherwise end.
    /// <para>从传输中读取精确数量的字节；零长度读只会消耗预算而不再结束读取。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">Total budget in milliseconds for the whole read. <para>整次读取的毫秒总预算。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The read bytes. <para>读取的字节。</para></returns>
    /// <exception cref="EdlException">The budget expired before <paramref name="length"/> bytes arrived, reported as
    /// <see cref="EdlErrorCode.ReadPortTimeout"/> so the Sahara recovery paths recognise it as a silent link rather
    /// than as a protocol violation they must not retry.
    /// <para>在 <paramref name="length"/> 字节到达之前预算已耗尽，以 <see cref="EdlErrorCode.ReadPortTimeout"/> 上报，
    /// 使 Sahara 恢复路径把它识别为静默链路，而不是它们不得重试的协议违规。</para></exception>
    /// <exception cref="SaharaException">The packet was truncated or the transport reported an invalid length.
    /// <para>数据包被截断，或传输报告了无效长度。</para></exception>
    private async Task<byte[]> ReadExactAsync(int length, int timeoutMs, CancellationToken cancellationToken)
    {
        var buf = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var total = 0;
            var zeroLengthReads = 0;
            var started = Environment.TickCount64;
            while (total < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remainingMs = timeoutMs - (Environment.TickCount64 - started);
                if (remainingMs <= 0)
                {
                    throw new EdlException(EdlErrorCode.ReadPortTimeout,
                        $"The Sahara packet receive deadline expired after {timeoutMs} ms with {total} of {length} bytes received" +
                        (zeroLengthReads == 0 ? "." : $" and {zeroLengthReads} zero-length read(s)."));
                }
                int n;
                try
                {
                    n = await _transport.ReadAsync(buf, total, length - total, (int)remainingMs, cancellationToken);
                }
                catch (TimeoutException ex)
                {
                    throw new EdlException(EdlErrorCode.ReadPortTimeout, ex.Message, ex);
                }
                if (n == 0)
                {
                    if (total != 0)
                    {
                        throw new SaharaException("The Sahara packet was truncated.");
                    }

                    // A zero-length read is "nothing arrived yet", not an expired deadline: usbfs reports a
                    // cleared STALL as a zero-length completion, so failing here would report a stall as a
                    // timeout. Spend the remaining budget instead and let the deadline decide.
                    zeroLengthReads++;
                    Log($"Read returned 0 byte(s) with {remainingMs} ms of budget left " +
                        $"(occurrence {zeroLengthReads}); retrying instead of reporting a timeout.");
                    await Task.Delay(ZeroLengthReadRetryDelayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (n < 0 || n > length - total)
                {
                    throw new SaharaException("The transport returned an invalid receive length.");
                }

                total += n;
            }
            var result = new byte[length];
            Array.Copy(buf, result, length);
            return result;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>
    /// Writes data to the transport.
    /// <para>将数据写入传输。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous operation. <para>表示异步操作的任务。</para></returns>
    private async Task WriteDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var buf = ArrayPool<byte>.Shared.Rent(data.Length);
        try
        {
            data.CopyTo(buf);
            long w;
            try
            {
                w = await _transport.WriteAsync(buf, 0, data.Length, _timeoutMs, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new EdlException(EdlErrorCode.WritePortTimeout, ex.Message, ex);
            }
            if (w != data.Length)
            {
                throw new EdlException(EdlErrorCode.WritePortTimeout);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>
    /// Writes a struct to the transport.
    /// <para>将结构体写入传输。</para>
    /// </summary>
    /// <param name="p">The struct to write. <para>要写入的结构体。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous operation. <para>表示异步操作的任务。</para></returns>
    private async Task WriteStructAsync<T>(T p, CancellationToken ct) where T : struct => await WriteDataAsync(StructToBytes(p), ct);

    /// <summary>
    /// Reads the remaining bytes of a packet after the header.
    /// <para>读取包头之后的剩余字节。</para>
    /// </summary>
    /// <param name="hd">The header bytes. <para>包头字节。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The complete packet bytes. <para>完整的包字节。</para></returns>
    private async Task<byte[]> ReadRemainingPacket<T>(byte[] hd, CancellationToken ct) where T : struct
    {
        var sz = Marshal.SizeOf<T>();
        var header = BytesToStruct<SaharaPacketHeader>(hd);
        if (header.Length != sz)
        {
            throw new SaharaException($"Invalid packet length for {header.Command}: expected {sz}, got {header.Length}.");
        }
        var fd = new byte[sz];
        Array.Copy(hd, fd, Math.Min(hd.Length, sz));
        var rem = sz - hd.Length;
        if (rem > 0)
        {
            var r = await ReadExactAsync(rem, _timeoutMs, ct);
            Array.Copy(r, 0, fd, hd.Length, Math.Min(r.Length, rem));
        }
        return fd;
    }

    /// <summary>
    /// Completes a packet whose already-read header declared its length, so a first packet that carries a
    /// known Sahara command is consumed to its boundary before the dispatch loop reuses it.
    /// <para>补读一个包头已读且已声明长度的数据包，使携带已知 Sahara 命令的首包在分发循环复用它之前被
    /// 消费到包边界。</para>
    /// </summary>
    /// <param name="declaredLength">The length the packet header declared; the caller has already bounded it.
    /// <para>包头声明的长度；调用方已对其设限。</para></param>
    /// <param name="headerBytes">The already-read prefix, which can already span the whole packet.
    /// <para>已读取的前缀；它可能已经覆盖整个数据包。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The complete packet bytes, header included.<para>含包头的完整包字节。</para></returns>
    private async Task<byte[]> ReadRemainingKnownLengthAsync(uint declaredLength, byte[] headerBytes, CancellationToken ct)
    {
        var remaining = (int)declaredLength - headerBytes.Length;
        if (remaining <= 0)
        {
            return headerBytes;
        }

        var body = await ReadExactAsync(remaining, _timeoutMs, ct).ConfigureAwait(false);
        var full = new byte[declaredLength];
        headerBytes.CopyTo(full, 0);
        body.CopyTo(full, headerBytes.Length);
        return full;
    }

    /// <summary>
    /// Validates that the actual command and length match expected values.
    /// <para>验证实际命令和长度是否与预期值匹配。</para>
    /// </summary>
    /// <param name="act">The actual command. <para>实际命令。</para></param>
    /// <param name="exp">The expected command. <para>预期命令。</para></param>
    /// <param name="len">The actual length. <para>实际长度。</para></param>
    /// <param name="expLen">The expected length. <para>预期长度。</para></param>
    /// <exception cref="SaharaException">Thrown on mismatch. <para>不匹配时抛出。</para></exception>
    private static void ValidateCommand(SaharaCommand act, SaharaCommand exp, uint len, uint expLen)
    {
        if (act != exp)
        {
            throw new SaharaException($"Expected {exp}, got {act}.");
        }

        if (len != expLen)
        {
            throw new SaharaException($"Invalid packet length: expected {expLen}, got {len}.");
        }
    }

    /// <summary>
    /// Converts a byte array to a struct.
    /// <para>将字节数组转换为结构体。</para>
    /// </summary>
    /// <param name="d">The byte array. <para>字节数组。</para></param>
    /// <returns>The converted struct. <para>转换后的结构体。</para></returns>
    /// <exception cref="SaharaException">Thrown if data is too short. <para>数据太短时抛出。</para></exception>
    private static T BytesToStruct<T>(byte[] d) where T : struct
    {
        return d.Length < Marshal.SizeOf<T>()
            ? throw new SaharaException($"Packet too short for {typeof(T).Name}: {d.Length}.")
            : MemoryMarshal.Read<T>(d);
    }

    /// <summary>
    /// Converts a struct to a byte array.
    /// <para>将结构体转换为字节数组。</para>
    /// </summary>
    /// <param name="s">The struct to convert. <para>要转换的结构体。</para></param>
    /// <returns>The byte array. <para>字节数组。</para></returns>
    private static byte[] StructToBytes<T>(T s) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var b = new byte[size];
        var h = GCHandle.Alloc(b, GCHandleType.Pinned);
        try { Marshal.StructureToPtr(s, h.AddrOfPinnedObject(), false); } finally { h.Free(); }
        return b;
    }
}

/// <summary>
/// Sahara packet header structure.
/// <para>Sahara 包头结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaPacketHeader
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
}

/// <summary>
/// Sahara HELLO packet structure (48 bytes: header + version fields + 6 reserved uints).
/// <para>Sahara HELLO 包结构（48 字节：包头 + 版本字段 + 6 个保留 uint）。</para>
/// Devices send Length=0x30; the trailing reserved words must be part of the struct or the
/// extra 24 bytes stay in the pipe and desynchronize every following packet.
/// <para>设备发送的 Length=0x30；保留字必须包含在结构体中，否则多出的 24 字节会留在管道
/// 中并使后续所有包失步。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaHelloPacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The protocol version. <para>协议版本。</para></summary>
    public uint Version;
    /// <summary>The minimum supported version. <para>最低支持版本。</para></summary>
    public uint MinVersion;
    /// <summary>The maximum command length (Sahara spec field max_cmd_len; NOT a packet size).
    /// <para>最大命令长度（Sahara 规范的 max_cmd_len；不是包长）。</para></summary>
    public uint MaxCommandLength;
    /// <summary>The operating mode. <para>操作模式。</para></summary>
    public SaharaMode Mode;
    /// <summary>Reserved word 0. <para>保留字 0。</para></summary>
    public uint Reserved0;
    /// <summary>Reserved word 1. <para>保留字 1。</para></summary>
    public uint Reserved1;
    /// <summary>Reserved word 2. <para>保留字 2。</para></summary>
    public uint Reserved2;
    /// <summary>Reserved word 3. <para>保留字 3。</para></summary>
    public uint Reserved3;
    /// <summary>Reserved word 4. <para>保留字 4。</para></summary>
    public uint Reserved4;
    /// <summary>Reserved word 5. <para>保留字 5。</para></summary>
    public uint Reserved5;
}

/// <summary>
/// Sahara HELLO_RESPONSE packet structure (48 bytes: header + response fields + 6 reserved uints).
/// <para>Sahara HELLO_RESPONSE 包结构（48 字节：包头 + 响应字段 + 6 个保留 uint）。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaHelloResponsePacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The protocol version. <para>协议版本。</para></summary>
    public uint Version;
    /// <summary>The minimum host protocol version (Sahara spec min_version).
    /// <para>主机最低协议版本（Sahara 规范的 min_version）。</para></summary>
    public uint MinVersion;
    /// <summary>The host status word (Sahara spec status; 0 means success).
    /// <para>主机状态字（Sahara 规范的 status；0 表示成功）。</para></summary>
    public uint Status;
    /// <summary>The operating mode. <para>操作模式。</para></summary>
    public SaharaMode Mode;
    /// <summary>Reserved word 0. <para>保留字 0。</para></summary>
    public uint Reserved0;
    /// <summary>Reserved word 1. <para>保留字 1。</para></summary>
    public uint Reserved1;
    /// <summary>Reserved word 2. <para>保留字 2。</para></summary>
    public uint Reserved2;
    /// <summary>Reserved word 3. <para>保留字 3。</para></summary>
    public uint Reserved3;
    /// <summary>Reserved word 4. <para>保留字 4。</para></summary>
    public uint Reserved4;
    /// <summary>Reserved word 5. <para>保留字 5。</para></summary>
    public uint Reserved5;
}

/// <summary>
/// Sahara READ_DATA packet structure.
/// <para>Sahara READ_DATA 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaReadDataPacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The image identifier. <para>镜像标识。</para></summary>
    public uint ImageId;
    /// <summary>The data offset. <para>数据偏移。</para></summary>
    public uint DataOffset;
    /// <summary>The data length. <para>数据长度。</para></summary>
    public uint DataLength;
}

/// <summary>
/// Sahara READ_DATA64 packet structure.
/// <para>Sahara READ_DATA64 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaReadData64Packet
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The image identifier (64-bit). <para>镜像标识（64 位）。</para></summary>
    public ulong ImageId;
    /// <summary>The data offset (64-bit). <para>数据偏移（64 位）。</para></summary>
    public ulong DataOffset;
    /// <summary>The data length (64-bit). <para>数据长度（64 位）。</para></summary>
    public ulong DataLength;
}

/// <summary>
/// Sahara END_OF_IMAGE packet structure.
/// <para>Sahara END_OF_IMAGE 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaEndOfImagePacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The image identifier. <para>镜像标识。</para></summary>
    public uint ImageId;
    /// <summary>The status code. <para>状态码。</para></summary>
    public uint Status;
}

/// <summary>
/// Sahara DONE packet structure.
/// <para>Sahara DONE 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaDonePacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
}

/// <summary>
/// Sahara DONE_RESPONSE packet structure.
/// <para>Sahara DONE_RESPONSE 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaDoneResponsePacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The status code. <para>状态码。</para></summary>
    public uint Status;
}

/// <summary>
/// Sahara RESET packet structure (8 bytes), the host request that makes the target leave its current
/// mode.
/// <para>Sahara RESET 包结构（8 字节），即让目标离开当前模式的主机请求
/// 。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaResetPacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
}

/// <summary>
/// Sahara RESET_RESPONSE packet structure.
/// <para>Sahara RESET_RESPONSE 包结构。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaResetResponsePacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
}

/// <summary>
/// Sahara MEMORY_DEBUG packet structure (16 bytes), carrying the address and length of the target's
/// debug region table.
/// <para>Sahara MEMORY_DEBUG 包结构（16 字节），携带目标调试区域表的地址与长度
/// 。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaMemoryDebugPacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The memory address of the region table. <para>区域表的内存地址。</para></summary>
    public uint MemoryTableAddress;
    /// <summary>The byte length of the region table. <para>区域表的字节长度。</para></summary>
    public uint MemoryTableLength;
}

/// <summary>
/// Sahara MEMORY_DEBUG64 packet structure (24 bytes), the widened variant of
/// <see cref="SaharaMemoryDebugPacket"/>.
/// <para>Sahara MEMORY_DEBUG64 包结构（24 字节），即 <see cref="SaharaMemoryDebugPacket"/> 的拓宽变体
/// 。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaMemoryDebug64Packet
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The memory address of the region table. <para>区域表的内存地址。</para></summary>
    public ulong MemoryTableAddress;
    /// <summary>The byte length of the region table. <para>区域表的字节长度。</para></summary>
    public ulong MemoryTableLength;
}

/// <summary>
/// Sahara MEMORY_READ packet structure (16 bytes), the host request that makes the target return the
/// addressed bytes with no packet framing.
/// <para>Sahara MEMORY_READ 包结构（16 字节），即让目标按地址回送裸字节且不再带包框的主机请求
/// 。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaMemoryReadPacket
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The memory address to read. <para>要读取的内存地址。</para></summary>
    public uint MemoryAddress;
    /// <summary>The number of bytes to read. <para>要读取的字节数。</para></summary>
    public uint MemoryLength;
}

/// <summary>
/// Sahara MEMORY_READ64 packet structure (24 bytes), the widened variant of
/// <see cref="SaharaMemoryReadPacket"/>.
/// <para>Sahara MEMORY_READ64 包结构（24 字节），即 <see cref="SaharaMemoryReadPacket"/> 的拓宽变体
/// 。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SaharaMemoryRead64Packet
{
    /// <summary>The command identifier. <para>命令标识。</para></summary>
    public SaharaCommand Command;
    /// <summary>The packet length. <para>包长度。</para></summary>
    public uint Length;
    /// <summary>The memory address to read. <para>要读取的内存地址。</para></summary>
    public ulong MemoryAddress;
    /// <summary>The number of bytes to read. <para>要读取的字节数。</para></summary>
    public ulong MemoryLength;
}