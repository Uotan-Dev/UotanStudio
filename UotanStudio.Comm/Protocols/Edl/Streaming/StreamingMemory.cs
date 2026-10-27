using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Edl.Streaming;

/// <summary>
/// Selects which streaming loader answers memory accesses: the stock Qualcomm command path with
/// DIAG 0x03 page reads, or the patched loader's ARM 0x11 execute path.
/// <para>选择应答内存访问的流式 loader 路径：原版 Qualcomm 命令路径用 DIAG 0x03 分页读取，
/// 打补丁 loader 路径用 ARM 0x11 执行。</para>
/// </summary>
public enum StreamingMemoryLinkMode
{
    /// <summary>
    /// Resolve the mode from the negotiated session, identifying the chipset first when nothing
    /// has been negotiated yet. <para>按已协商的会话解析模式；尚未协商时先执行芯片识别。</para></summary>
    Auto = 0,

    /// <summary>
    /// Stock Qualcomm loader: peek-only support through the DIAG 0x03 512-byte page loop
    ///. <para>原版 Qualcomm loader：仅支持经 DIAG 0x03 512 字节
    /// 页循环的 peek。</para></summary>
    Qualcomm = 1,

    /// <summary>
    /// Patched loader: full read and write support through the ARM 0x11 execute loop
    ///. <para>打补丁 loader：经 ARM 0x11 执行循环提供完整读写
    /// 。</para></summary>
    Patched = 2
}

/// <summary>Memory reads, peek and poke, and chipset identification for the streaming protocol. <para>流式协议的内存读取、peek/poke 与芯片组识别。</para></summary>
public sealed partial class StreamingProtocol
{
    /// <summary>
    /// The marker byte the chipset search blob stores at the front of its reply before the
    /// chipset identifier; the blob writes it with <c>MOV R0, #0xAA</c> and <c>STRB R0, [R1]</c>.
    /// <para>芯片搜索机器码写入应答最前面、位于芯片标识之前的标记字节；该机器码以
    /// <c>MOV R0, #0xAA</c> 与 <c>STRB R0, [R1]</c> 写入它。</para>
    /// </summary>
    private const byte ChipsetSearchMarker = 0xAA;

    /// <summary>
    /// The little-endian ARM blob the patched loader executes to dump memory: it stores the
    /// <c>0x12</c> status word and then copies words from the requested address onward.
    /// <para>打补丁 loader 用于转储内存的小端 ARM 机器码：先存 <c>0x12</c> 状态字，再从请求地址开始
    /// 逐字复制。</para>
    /// </summary>
    internal static readonly byte[] ReadBlob = Convert.FromHexString(
        "24309FE524409FE51200A0E3040081E4040083E0042093E4042081E4000053E1FBFFFF3A044084E21EFF2FE1");

    /// <summary>
    /// The little-endian ARM blob the patched loader executes to copy host-supplied words into
    /// memory.
    /// <para>打补丁 loader 用于把主机提供的字复制进内存的小端 ARM 机器码。</para>
    /// </summary>
    internal static readonly byte[] WriteBlob = Convert.FromHexString(
        "380080E224309FE524409FE5044083E0042090E4042083E4040053E1FBFFFF3A1200A0E30000C1E50140A0E31EFF2FE1");

    /// <summary>
    /// The little-endian ARM blob that scans memory for the <c>0xDEADBEEF</c> marker to reveal the
    /// chipset identifier.
    /// <para>在内存中搜索 <c>0xDEADBEEF</c> 标记以揭示芯片标识的小端 ARM 机器码。</para>
    /// </summary>
    internal static readonly byte[] SearchBlob = Convert.FromHexString(
        "04102DE50E00A0E10300C0E3FF3080E234109FE5042090E4010052E10300000A030050E1FAFFFF3A0000A0E3" +
        "000000EA000090E504109DE40100C1E5AA00A0E30000C1E50240A0E31EFF2FE1EFBEADDE");

    /// <summary>Reads target memory.
    /// <para>读取目标内存。</para>The Qualcomm path loops DIAG 0x03 requests of at most <see cref="StreamingConstants.MemoryReadPageBytes"/> bytes whose reply echoes the response identifier and the requested address before the page data; the patched path loops the ARM read blob with 1000-byte blocks, retrying short replies a bounded number of times. Address and length are bound before the operation lease and any transport write, and the whole request must stay inside the 32-bit DIAG address space.<para>Qualcomm 路径循环发送至多 <see cref="StreamingConstants.MemoryReadPageBytes"/> 字节的 DIAG 0x03 请求，回包先回显响应号与请求地址再跟页数据；打补丁路径用 ARM 读取机器码按 1000 字节 块循环，并对短回包做有界次数的重试。地址与长度在取得操作租约与任何传输写出之前完成边界检查， 整个请求必须落在 32 位 DIAG 地址空间内。</para></summary>
    /// <param name="address">The start address to read.<para>读取起始地址。</para></param>
    /// <param name="length">The number of bytes to read, at most <see cref="StreamingConstants.MaxMemoryReadBytes"/>.
    /// <para>读取字节数，至多 <see cref="StreamingConstants.MaxMemoryReadBytes"/>。</para></param>
    /// <param name="mode">The loader link mode; <see cref="StreamingMemoryLinkMode.Auto"/> resolves the negotiated session first.
    /// <para>loader 链接模式； <see cref="StreamingMemoryLinkMode.Auto"/> 会先解析已协商的会话。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The exact number of bytes requested.<para>与请求数量完全一致的字节。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the length is outside (0, <see cref="StreamingConstants.MaxMemoryReadBytes"/>] or the read window leaves the 32-bit DIAG address space; the refusal happens before the operation lease.
    /// <para>长度超出 (0, <see cref="StreamingConstants.MaxMemoryReadBytes"/>] 或读取窗口超出 32 位 DIAG 地址空间时抛出；拒绝发生在取得操作租约之前。</para></exception>
    /// <exception cref="StreamingException">Thrown when the target rejects a page, replies with a mismatched identifier, or a patched-block reply stays short after the retry budget.
    /// <para>目标拒绝某页、以不符的响应号应答，或打补丁块回包在重试预算后仍然过短 时抛出。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<byte[]> ReadMemoryAsync(uint address, int length,
        StreamingMemoryLinkMode mode = StreamingMemoryLinkMode.Auto, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, StreamingConstants.MaxMemoryReadBytes);
        if (checked((long)address + length) > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(address),
                "The memory read window leaves the 32-bit DIAG address space.");
        }

        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveLinkModeOwnedAsync(mode, cancellationToken).ConfigureAwait(false);
        return resolved == StreamingMemoryLinkMode.Qualcomm
            ? await ReadMemoryQualcommOwnedAsync(address, length, cancellationToken).ConfigureAwait(false)
            : await ReadMemoryPatchedOwnedAsync(address, length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one little-endian 32-bit word from target memory via
    /// <see cref="ReadMemoryAsync"/>.
    /// <para>经 <see cref="ReadMemoryAsync"/> 读取目标内存中的一个小端 32 位字。</para>
    /// </summary>
    /// <param name="address">The word address to peek. <para>要 peek 的字地址。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The word read from the target. <para>从目标读到的字。</para></returns>
    /// <exception cref="StreamingException">Thrown when the read fails.
    /// <para>读取失败时抛出。</para></exception>
    public async Task<uint> MemPeekAsync(uint address, CancellationToken cancellationToken = default)
    {
        var data = await ReadMemoryAsync(address, 4, StreamingMemoryLinkMode.Auto, cancellationToken)
            .ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    /// <summary>Writes one little-endian 32-bit word into target memory through the patched loader's ARM write blob.
    /// <para>经打补丁 loader 的 ARM 写入机器码把一个小端 32 位字写入目标内存。</para>The word window and the confirmation gate are both checked before the operation lease and before any byte reaches the wire.<para>字窗口与确认门都在取得操作租约之前、任何字节上线之前检查。</para></summary>
    /// <param name="address">The word address to poke.<para>要 poke 的字地址。</para></param>
    /// <param name="value">The 32-bit value to store.<para>要存储的 32 位值。</para></param>
    /// <param name="confirm">Pass <see langword="true"/> to confirm the memory write.
    /// <para>传 <see langword="true"/> 以确认这次内存写入。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns><see langword="true"/> when the write was submitted.
    /// <para>写入已提交时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="confirm"/> is <see langword="false"/>.
    /// <para><paramref name="confirm"/> 为 <see langword="false"/> 时抛出。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the poke window leaves the 32-bit DIAG address space.
    /// <para>poke 窗口超出 32 位 DIAG 地址空间时抛出。</para></exception>
    /// <exception cref="StreamingException">Thrown when the session has not resolved the patched link mode, or the transport rejects the frame.
    /// <para>会话尚未解析为打补丁链接模式，或 传输拒绝该帧时抛出。</para></exception>
    public async Task<bool> MemPokeAsync(uint address, uint value, bool confirm,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            throw new ArgumentException("Poking target memory requires explicit confirmation.", nameof(confirm));
        }

        if (checked((long)address + 4) > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(address),
                "The poke window leaves the 32-bit DIAG address space.");
        }

        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        return await MemWriteAsync(address, data, confirm, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes up to <see cref="StreamingConstants.PatchedMemoryBlockBytes"/> bytes into target memory with the patched loader's ARM write blob. Longer payloads are rejected instead of being silently truncated.
    /// <para>用打补丁 loader 的 ARM 写入机器码向目标内存写入至多 <see cref="StreamingConstants.PatchedMemoryBlockBytes"/> 字节。更长数据会被直接拒绝而不静默截断。</para></summary>
    /// <param name="address">The start address of the write.<para>写入起始地址。</para></param>
    /// <param name="data">The bytes to store.<para>要存储的字节。</para></param>
    /// <param name="confirm">Pass <see langword="true"/> to confirm the memory write.
    /// <para>传 <see langword="true"/> 以确认这次内存写入。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns><see langword="true"/> when the write was submitted.
    /// <para>写入已提交时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="confirm"/> is <see langword="false"/> or the data is empty or oversized.
    /// <para><paramref name="confirm"/> 为 <see langword="false"/>，或数据为空或超长时抛出。</para></exception>
    public async Task<bool> MemWriteAsync(uint address, byte[] data, bool confirm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!confirm)
        {
            throw new ArgumentException("Writing target memory requires explicit confirmation.", nameof(confirm));
        }

        if (data.Length == 0 || data.Length > StreamingConstants.PatchedMemoryBlockBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(data),
                "A streaming memory write must contain between 1 and the patched block size in bytes.");
        }
        if (checked((long)address + data.Length) > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(address),
                "The write window leaves the 32-bit DIAG address space.");
        }

        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await MemWriteOwnedAsync(address, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Identifies the patched loader's chipset by executing the search blob.
    /// <para>通过执行搜索机器码识别打补丁 loader 的芯片。</para>A reply containing "Power off not supported" means the stock Qualcomm loader (returned as 0xFF), the marker byte 0xAA followed by the chipset identifier means a patched loader, and one mismatched reply is retried once. The resolved mode is cached in <see cref="NegotiatedLinkMode"/>.<para>回包含 “Power off not supported” 表示原版 Qualcomm loader（返回 0xFF），标记字节 0xAA 后跟芯片标识表示打补丁 loader；对一个不符回包只重试一次。解析出的模式缓存到 <see cref="NegotiatedLinkMode"/>。</para></summary>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The chipset identifier byte, 0xFF for the stock Qualcomm loader, or -1 when the target never produced the search marker.
    /// <para>芯片标识字节；原版 Qualcomm loader 为 0xFF； 目标始终未给出搜索标记时为 -1。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    public async Task<int> IdentifyChipsetAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await IdentifyChipsetOwnedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adopts a memory link mode without running the chipset search, for hosts that already
    /// know which loader variant owns the transport.
    /// <para>在不执行芯片搜索的情况下采用内存链接模式，供已知传输层上 loader 变体的宿主使用。</para>
    /// </summary>
    /// <param name="mode">The link mode to adopt; <see cref="StreamingMemoryLinkMode.Auto"/>
    /// re-enables search-on-demand. <para>要采用的模式；<see cref="StreamingMemoryLinkMode.Auto"/>
    /// 恢复按需搜索。</para></param>
    internal void AdoptLinkMode(StreamingMemoryLinkMode mode) => _linkMode = mode;

    /// <summary>
    /// Resolves <see cref="StreamingMemoryLinkMode.Auto"/> against the negotiated session,
    /// running chipset identification when nothing was resolved yet.
    /// <para>按已协商会话解析 <see cref="StreamingMemoryLinkMode.Auto"/>；尚未解析时执行芯片识别。</para>
    /// </summary>
    private async Task<StreamingMemoryLinkMode> ResolveLinkModeOwnedAsync(StreamingMemoryLinkMode mode,
        CancellationToken cancellationToken)
    {
        if (mode != StreamingMemoryLinkMode.Auto)
        {
            return mode;
        }

        if (_linkMode == StreamingMemoryLinkMode.Auto)
        {
            var chipset = await IdentifyChipsetOwnedAsync(cancellationToken).ConfigureAwait(false);
            _linkMode = chipset == 0xFF
                ? StreamingMemoryLinkMode.Qualcomm
                : chipset >= 0
                    ? StreamingMemoryLinkMode.Patched
                    : throw new StreamingException(EdlErrorCode.TargetNak,
                        "The streaming target did not answer the chipset search blob.");
        }

        return _linkMode;
    }

    /// <summary>
    /// Runs the chipset search exchange on an already-leased session.
    /// <para>在已持有租约的会话上执行芯片搜索交换。</para>
    /// </summary>
    private async Task<int> IdentifyChipsetOwnedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var command = new byte[2 + SearchBlob.Length];
        command[0] = StreamingConstants.DiagArmExecuteCommand;
        SearchBlob.CopyTo(command.AsSpan(2));

        var reply = await SendDiagOwnedAsync(command, verifyResponseCrc: false,
            tolerateNoResponse: false, cancellationToken).ConfigureAwait(false);
        await DrainOptionalFrameOwnedAsync(cancellationToken).ConfigureAwait(false);
        if (reply.IndexOf("Power off not supported"u8) >= 0)
        {
            _linkMode = StreamingMemoryLinkMode.Qualcomm;
            Log("Stock Qualcomm streaming loader detected; only peek-style reads are supported.");
            return 0xFF;
        }

        if (reply.Length < 2 || reply[0] != ChipsetSearchMarker)
        {
            reply = await SendDiagOwnedAsync(command, verifyResponseCrc: false,
                tolerateNoResponse: false, cancellationToken).ConfigureAwait(false);
            await DrainOptionalFrameOwnedAsync(cancellationToken).ConfigureAwait(false);
            if (reply.Length < 2 || reply[0] != ChipsetSearchMarker)
            {
                return -1;
            }
        }

        _linkMode = StreamingMemoryLinkMode.Patched;
        Log($"Patched streaming loader reports chipset id 0x{reply[1]:X2}.");
        return reply[1];
    }

    /// <summary>
    /// Reads one more framed reply within a 5-second budget and discards it,
    /// tolerating a target that sends nothing.
    /// <para>在 5 秒预算内再读取一个封装回包并丢弃，容忍目标不再发送。</para>
    /// </summary>
    private async Task DrainOptionalFrameOwnedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReceiveDiagFrameOwnedAsync(verifyCrc: false, cancellationToken).ConfigureAwait(false);
        }
        catch (EdlException)
        {
        }
    }

    /// <summary>
    /// Runs the Qualcomm 0x03 page loop on an already-leased session: request identifier,
    /// little-endian uint32 address and uint16 size per page; the normalized reply carries the
    /// 0x04 identifier, the echoed address, then the page data.
    /// <para>在已持有租约的会话上运行 Qualcomm 0x03 页循环：每页请求号、小端 uint32 地址与
    /// uint16 长度；规范化后的回包携带 0x04 响应号、回显地址，随后是页数据
    /// 。</para>
    /// </summary>
    private async Task<byte[]> ReadMemoryQualcommOwnedAsync(uint address, int length,
        CancellationToken cancellationToken)
    {
        var result = new byte[length];
        var written = 0;
        var command = new byte[7];
        command[0] = StreamingConstants.DiagMemReadCommand;
        while (written < length)
        {
            var size = Math.Min(StreamingConstants.MemoryReadPageBytes, length - written);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(1), address + (uint)written);
            BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(5), (ushort)size);
            var reply = await SendDiagOwnedAsync(command, verifyResponseCrc: false,
                tolerateNoResponse: false, cancellationToken).ConfigureAwait(false);
            if (reply.Length < 5 || reply[0] != StreamingConstants.DiagMemReadResponse)
            {
                throw new StreamingException(EdlErrorCode.TargetNak,
                    "The streaming target did not acknowledge the DIAG memory read.");
            }

            var available = Math.Min(size, reply.Length - 5);
            reply.AsSpan(5, available).CopyTo(result.AsSpan(written));
            if (available < size)
            {
                throw new StreamingException(EdlErrorCode.InvalidParameter,
                    "The streaming memory reply carried fewer bytes than the requested page.");
            }

            written += size;
        }

        return result;
    }

    /// <summary>
    /// Runs the patched ARM read loop on an already-leased session: the read blob with the
    /// little-endian address and block length, replies whose first four bytes are the 0x12
    /// status word followed by block data; short replies are re-read up to a 20-try
    /// budget.
    /// <para>在已持有租约的会话上运行打补丁 ARM 读循环：读取机器码附小端地址与块长；回包前四个
    /// 字节为 0x12 状态字，其后是块数据；短回包按 20 次重试预算补读。</para>
    /// </summary>
    private async Task<byte[]> ReadMemoryPatchedOwnedAsync(uint address, int length,
        CancellationToken cancellationToken)
    {
        var result = new byte[length];
        var written = 0;
        var command = new byte[2 + ReadBlob.Length + 8];
        command[0] = StreamingConstants.DiagArmExecuteCommand;
        ReadBlob.CopyTo(command, 2);
        var parameterStart = 2 + ReadBlob.Length;
        while (written < length)
        {
            var size = Math.Min(StreamingConstants.PatchedMemoryBlockBytes, length - written);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(parameterStart), address + (uint)written);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(parameterStart + 4), (uint)size);
            var reply = await SendDiagOwnedAsync(command, verifyResponseCrc: false,
                tolerateNoResponse: false, cancellationToken).ConfigureAwait(false);
            var attempts = 0;
            while (reply.Length < 4 + size && attempts < 20)
            {
                attempts++;
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                var more = await ReceiveDiagFrameOwnedAsync(verifyCrc: false, cancellationToken)
                    .ConfigureAwait(false);
                reply = [.. reply, .. more];
            }

            if (reply.Length < 4 + size)
            {
                throw new StreamingException(EdlErrorCode.ReadPortTimeout,
                    $"The patched streaming read reply stayed short: {reply.Length} bytes for a {size}-byte block.");
            }

            reply.AsSpan(4, size).CopyTo(result.AsSpan(written));
            written += size;
        }

        return result;
    }

    /// <summary>
    /// Executes one patched ARM write on an already-leased session and consumes the loader's
    /// acknowledgement.
    /// <para>在已持有租约的会话上执行一次打补丁 ARM 写，并消费 loader 的确认
    /// 。</para>
    /// </summary>
    private async Task<bool> MemWriteOwnedAsync(uint address, byte[] data, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var mode = await ResolveLinkModeOwnedAsync(StreamingMemoryLinkMode.Auto, cancellationToken)
            .ConfigureAwait(false);
        if (mode != StreamingMemoryLinkMode.Patched)
        {
            throw new StreamingException(EdlErrorCode.NotSupported,
                "Memory writes require the patched streaming loader; the stock Qualcomm loader is peek-only.");
        }

        var command = new byte[2 + WriteBlob.Length + 8 + data.Length];
        command[0] = StreamingConstants.DiagArmExecuteCommand;
        WriteBlob.CopyTo(command, 2);
        var parameterStart = 2 + WriteBlob.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(parameterStart), address);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(parameterStart + 4), (uint)data.Length);
        data.CopyTo(command, parameterStart + 8);

        var reply = await SendDiagOwnedAsync(command, verifyResponseCrc: false,
            tolerateNoResponse: true, cancellationToken).ConfigureAwait(false);
        if (reply.Length > 0 && reply[0] != StreamingConstants.DiagArmExecuteResponse)
        {
            Log($"The patched loader answered the memory write with id 0x{reply[0]:X2}.");
        }

        return true;
    }
}