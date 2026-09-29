using System.Buffers.Binary;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Tests.Comm.TestDoubles;

/// <summary>
/// One CBW the host sent to the simulated rockusb device, decoded from the raw 31 wire bytes
/// using the literal field offsets of the upstream packed <c>CBW</c>/<c>CBWCB</c> structures.
/// <para>主机发给模拟 rockusb 设备的一条 CBW，按上游紧凑 <c>CBW</c>/<c>CBWCB</c> 结构的字面字段
/// 偏移从 31 个线上字节解码得到。</para>
/// </summary>
/// <param name="Tag">dwCBWTag, little-endian at byte 4. <para>dwCBWTag，字节 4 处小端。</para></param>
/// <param name="TransferLength">dwCBWTransferLength, little-endian at byte 8. <para>dwCBWTransferLength，字节 8 处小端。</para></param>
/// <param name="Flags">ucCBWFlags at byte 12 (0x80 = data in). <para>ucCBWFlags，字节 12（0x80 = 数据输入）。</para></param>
/// <param name="Lun">ucCBWLUN at byte 13. <para>ucCBWLUN，字节 13。</para></param>
/// <param name="CbwcbLength">ucCBWCBLength at byte 14 (0x06 or 0x0A). <para>ucCBWCBLength，字节 14（0x06 或 0x0A）。</para></param>
/// <param name="Opcode">cbwcb.ucOperCode at byte 15. <para>cbwcb.ucOperCode，字节 15。</para></param>
/// <param name="SubCode">cbwcb.ucReserved at byte 16. <para>cbwcb.ucReserved，字节 16。</para></param>
/// <param name="Address">cbwcb.dwAddress, big-endian at byte 17. <para>cbwcb.dwAddress，字节 17 处大端。</para></param>
/// <param name="SectorLength">cbwcb.usLength, big-endian at byte 22. <para>cbwcb.usLength，字节 22 处大端。</para></param>
/// <param name="RawBytes">The complete 31 wire bytes as written by the host. <para>主机写出的完整 31 个线上字节。</para></param>
public sealed record RkdlCbwRecord(
    uint Tag,
    uint TransferLength,
    byte Flags,
    byte Lun,
    byte CbwcbLength,
    byte Opcode,
    byte SubCode,
    uint Address,
    ushort SectorLength,
    byte[] RawBytes);

/// <summary>
/// A rockusb device simulator over <see cref="FakeCommTransport"/>: every 31-byte CBW is decoded
/// from literal wire offsets, its data phase is synthesized (READ_FLASH_INFO / READ_CHIP_INFO /
/// READ_CAPABILITY / READ_FLASH_ID / READ_LBA payloads, or the recorded OUT payload for
/// WRITE_LBA) and a 13-byte CSW echoing the CBW tag is queued back, so tests can assert the exact
/// byte stream the host produced and inject malformed or failing device responses.
/// <para>基于 <see cref="FakeCommTransport"/> 的 rockusb 设备模拟器：每条 31 字节 CBW 都按字面线上
/// 偏移解码，随后合成它的数据阶段（READ_FLASH_INFO / READ_CHIP_INFO / READ_CAPABILITY /
/// READ_FLASH_ID / READ_LBA 负载，或为 WRITE_LBA 记录 OUT 负载），并回队一条回显 CBW 标签的
/// 13 字节 CSW，便于测试断言主机产生的确切字节流并注入畸形或失败的设备响应。</para>
/// </summary>
public sealed class FakeRkdlDeviceTransport : FakeCommTransport
{
    /// <summary>Serialized CBW length in bytes. <para>序列化 CBW 的字节长度。</para></summary>
    public const int CbwWireLength = 31;

    /// <summary>Serialized CSW length in bytes. <para>序列化 CSW 的字节长度。</para></summary>
    public const int CswWireLength = 13;

    /// <summary>Opcode byte of TEST_UNIT_READY. <para>TEST_UNIT_READY 的操作码字节。</para></summary>
    public const byte OpcodeTestUnitReady = 0x00;

    /// <summary>Opcode byte of READ_FLASH_ID. <para>READ_FLASH_ID 的操作码字节。</para></summary>
    public const byte OpcodeReadFlashId = 0x01;

    /// <summary>Opcode byte of ERASE_NORMAL. <para>ERASE_NORMAL 的操作码字节。</para></summary>
    public const byte OpcodeEraseNormal = 0x06;

    /// <summary>Opcode byte of READ_LBA. <para>READ_LBA 的操作码字节。</para></summary>
    public const byte OpcodeReadLba = 0x14;

    /// <summary>Opcode byte of WRITE_LBA. <para>WRITE_LBA 的操作码字节。</para></summary>
    public const byte OpcodeWriteLba = 0x15;

    /// <summary>Opcode byte of ERASE_SYSTEMDISK. <para>ERASE_SYSTEMDISK 的操作码字节。</para></summary>
    public const byte OpcodeEraseSystemDisk = 0x16;

    /// <summary>Opcode byte of READ_FLASH_INFO. <para>READ_FLASH_INFO 的操作码字节。</para></summary>
    public const byte OpcodeReadFlashInfo = 0x1A;

    /// <summary>Opcode byte of READ_CHIP_INFO. <para>READ_CHIP_INFO 的操作码字节。</para></summary>
    public const byte OpcodeReadChipInfo = 0x1B;

    /// <summary>Opcode byte of ERASE_LBA. <para>ERASE_LBA 的操作码字节。</para></summary>
    public const byte OpcodeEraseLba = 0x25;

    /// <summary>Opcode byte of READ_CAPABILITY. <para>READ_CAPABILITY 的操作码字节。</para></summary>
    public const byte OpcodeReadCapability = 0xAA;

    /// <summary>Opcode byte of DEVICE_RESET. <para>DEVICE_RESET 的操作码字节。</para></summary>
    public const byte OpcodeDeviceReset = 0xFF;

    /// <summary>
    /// The CSW signature the simulated device emits, in the transmission order a real rockusb device
    /// uses: the DWORD <c>0x53425355</c> of rkdeveloptool <c>CSW_SIGN 0x53425355 /* "USBS" */</c>
    /// stored little-endian gives <c>55 53 42 53</c> = ASCII "USBS", the same bytes u-boot rockusb
    /// produces with <c>csw-&gt;signature = cpu_to_le32(USB_BULK_CS_SIG)</c>. Keeping this literal
    /// independent of the production constant is what makes the simulator able to reject a wrong
    /// production signature instead of agreeing with it.
    /// <para>模拟设备发出的 CSW 签名，采用真实 rockusb 设备的传输顺序：rkdeveloptool 的 DWORD
    /// <c>0x53425355</c>（<c>CSW_SIGN 0x53425355 /* "USBS" */</c>）按小端存储得到
    /// <c>55 53 42 53</c> = ASCII "USBS"，与 u-boot rockusb 的
    /// <c>csw-&gt;signature = cpu_to_le32(USB_BULK_CS_SIG)</c> 产生的字节相同。让该字面量独立于生产常量，
    /// 模拟器才能拒绝错误的生产签名，而不是与之自洽。</para>
    /// </summary>
    private static readonly byte[] DeviceCswSignature = [0x55, 0x53, 0x42, 0x53];

    /// <summary>
    /// The CBW signature the simulator expects at the head of every host write, in the same
    /// transmission order as <see cref="DeviceCswSignature"/>: the DWORD <c>0x43425355</c> of
    /// rkdeveloptool <c>CBW_SIGN 0x43425355 /* "USBC" */</c> stored little-endian gives
    /// <c>55 53 42 43</c> = ASCII "USBC".
    /// <para>模拟器在每条主机写入的开头所期望的 CBW 签名，传输顺序与
    /// <see cref="DeviceCswSignature"/> 一致：rkdeveloptool 的 DWORD <c>0x43425355</c>
    /// （<c>CBW_SIGN 0x43425355 /* "USBC" */</c>）按小端存储得到 <c>55 53 42 43</c> = ASCII "USBC"。</para>
    /// </summary>
    private static readonly byte[] ExpectedCbwSignature = [0x55, 0x53, 0x42, 0x43];

    private uint? _pendingOutLength;
    private byte _pendingOutOpcode;

    /// <summary>Initializes a new rockusb device simulator. <para>初始化新的 rockusb 设备模拟器。</para></summary>
    public FakeRkdlDeviceTransport()
    {
        OnWrite = HandleHostWrite;
    }

    /// <summary>Gets every decoded CBW the host sent, in order. <para>获取主机按序发出的全部已解码 CBW。</para></summary>
    public List<RkdlCbwRecord> Cbws { get; } = [];

    /// <summary>Gets every data-phase OUT payload the host sent, in order. <para>获取主机按序发出的全部数据阶段 OUT 负载。</para></summary>
    public List<byte[]> OutPayloads { get; } = [];

    /// <summary>Gets every CSW the device sent back, in order. <para>获取设备按序回送的全部 CSW。</para></summary>
    public List<byte[]> CswsSent { get; } = [];

    /// <summary>
    /// Gets or sets the total sector count reported in the leading DWORD of READ_FLASH_INFO;
    /// ignored once <see cref="FlashInfoPayload"/> is set explicitly.
    /// <para>获取或设置 READ_FLASH_INFO 首 DWORD 报告的总扇区数；一旦显式设置
    /// <see cref="FlashInfoPayload"/> 即被忽略。</para>
    /// </summary>
    public uint FlashInfoSectorCount { get; set; } = 0x2000;

    /// <summary>
    /// Gets or sets the raw READ_FLASH_INFO data phase, or <c>null</c> to synthesize the 11-byte
    /// default from <see cref="FlashInfoSectorCount"/>. Set it to inject a malformed response.
    /// <para>获取或设置 READ_FLASH_INFO 的原始数据阶段；为 <c>null</c> 时按
    /// <see cref="FlashInfoSectorCount"/> 合成 11 字节默认响应。设置它可注入畸形响应。</para>
    /// </summary>
    public byte[]? FlashInfoPayload { get; set; }

    /// <summary>
    /// Gets or sets the raw READ_CHIP_INFO data phase (16 bytes; the first four are the ASCII chip
    /// tag). <para>获取或设置 READ_CHIP_INFO 的原始数据阶段（16 字节；前 4 字节为 ASCII 芯片标签）。</para>
    /// </summary>
    public byte[] ChipInfoPayload { get; set; } =
        [0x41, 0x30, 0x32, 0x33, 0x34, 0x31, 0x30, 0x32, 0x33, 0x38, 0x31, 0x33, 0x56, 0x32, 0x30, 0x30];

    /// <summary>
    /// Gets or sets the raw READ_CAPABILITY data phase (8 bytes).
    /// <para>获取或设置 READ_CAPABILITY 的原始数据阶段（8 字节）。</para>
    /// </summary>
    public byte[] CapabilityPayload { get; set; } = [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>
    /// Gets or sets the raw READ_FLASH_ID data phase (5 bytes).
    /// <para>获取或设置 READ_FLASH_ID 的原始数据阶段（5 字节）。</para>
    /// </summary>
    public byte[] FlashIdPayload { get; set; } = [0xEC, 0xA1, 0x00, 0x00, 0x00];

    /// <summary>
    /// Gets or sets the flash contents served for READ_LBA data phases; shorter reads are zero
    /// filled. <para>获取或设置 READ_LBA 数据阶段提供的 flash 内容；不足部分补零。</para>
    /// </summary>
    public byte[] FlashContents { get; set; } = [];

    /// <summary>
    /// Gets or sets the ucCSWStatus byte the device reports (0 = pass, 1 = fail).
    /// <para>获取或设置设备报告的 ucCSWStatus 字节（0 = 成功，1 = 失败）。</para>
    /// </summary>
    public byte CswStatus { get; set; }

    /// <summary>
    /// Gets or sets an optional per-CBW override of <see cref="CswStatus"/>, for example to fail
    /// only the second WRITE_LBA of a sequence.
    /// <para>获取或设置可选的按 CBW 覆盖 <see cref="CswStatus"/> 的委托，例如只让序列中第二条
    /// WRITE_LBA 失败。</para>
    /// </summary>
    public Func<RkdlCbwRecord, byte>? CswStatusResolver { get; set; }

    /// <summary>
    /// Gets or sets the dwCBWDataResidue the device reports in every CSW.
    /// <para>获取或设置设备在每条 CSW 中报告的 dwCBWDataResidue。</para>
    /// </summary>
    public uint CswDataResidue { get; set; }

    /// <summary>
    /// When set, the device stops synthesizing responses after this many CBWs, so a host that
    /// keeps talking to a silent device observes the transport timeout instead of a fake reply.
    /// <para>置位时，设备在收到该数量的 CBW 之后停止合成响应，使继续与静默设备通信的主机观察到
    /// 传输超时而非伪造应答。</para>
    /// </summary>
    public int? GoSilentAfterCbwCount { get; set; }

    /// <summary>
    /// Builds the 11-byte READ_FLASH_INFO response for a sector count.
    /// <para>为给定扇区数构建 11 字节的 READ_FLASH_INFO 响应。</para>
    /// </summary>
    /// <param name="sectorCount">The total sector count reported in the leading DWORD. <para>首 DWORD 报告的总扇区数。</para></param>
    /// <returns>The 11-byte device payload. <para>11 字节的设备负载。</para></returns>
    public static byte[] BuildFlashInfoPayload(uint sectorCount)
    {
        var payload = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, sectorCount);
        return payload;
    }

    private IEnumerable<byte[]> HandleHostWrite(byte[] data)
    {
        if (_pendingOutLength is uint pending)
        {
            _pendingOutLength = null;
            OutPayloads.Add(data);
            yield return BuildCsw(pending, _pendingOutOpcode);
            yield break;
        }

        if (data.Length != CbwWireLength || !data.AsSpan(0, 4).SequenceEqual(ExpectedCbwSignature))
        {
            yield break;
        }

        var cbw = DecodeCbw(data);
        Cbws.Add(cbw);
        if (GoSilentAfterCbwCount is int limit && Cbws.Count > limit)
        {
            yield break;
        }

        switch (cbw.Opcode)
        {
            case OpcodeReadFlashInfo:
                yield return FlashInfoPayload ?? BuildFlashInfoPayload(FlashInfoSectorCount);
                yield return BuildCsw(cbw.Tag, cbw.Opcode);
                break;

            case OpcodeReadChipInfo:
                yield return ChipInfoPayload;
                yield return BuildCsw(cbw.Tag, cbw.Opcode);
                break;

            case OpcodeReadCapability:
                yield return CapabilityPayload;
                yield return BuildCsw(cbw.Tag, cbw.Opcode);
                break;

            case OpcodeReadFlashId:
                yield return FlashIdPayload;
                yield return BuildCsw(cbw.Tag, cbw.Opcode);
                break;

            case OpcodeReadLba:
                yield return BuildFlashSlice(cbw.Address, cbw.TransferLength);
                yield return BuildCsw(cbw.Tag, cbw.Opcode);
                break;

            case OpcodeWriteLba:
                _pendingOutLength = cbw.Tag;
                _pendingOutOpcode = cbw.Opcode;
                break;

            default:
                yield return BuildCsw(cbw.Tag, cbw.Opcode);
                break;
        }
    }

    private byte[] BuildCsw(uint tag, byte opcode)
    {
        var csw = new byte[CswWireLength];
        DeviceCswSignature.CopyTo(csw, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(csw.AsSpan(4), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(csw.AsSpan(8), CswDataResidue);
        csw[12] = ResolveCswStatus(tag, opcode);
        CswsSent.Add(csw);
        return csw;
    }

    private byte ResolveCswStatus(uint tag, byte opcode)
    {
        if (CswStatusResolver is null)
        {
            return CswStatus;
        }

        var match = Cbws.LastOrDefault(c => c.Tag == tag && c.Opcode == opcode);
        return match is null ? CswStatus : CswStatusResolver(match);
    }

    private byte[] BuildFlashSlice(uint address, uint transferLength)
    {
        var slice = new byte[transferLength];
        var start = (long)address * 512;
        for (long i = 0; i < transferLength; i++)
        {
            var source = start + i;
            slice[i] = source >= 0 && source < FlashContents.Length ? FlashContents[source] : (byte)0;
        }

        return slice;
    }

    private static RkdlCbwRecord DecodeCbw(byte[] data) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)),
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)),
        data[12],
        data[13],
        data[14],
        data[15],
        data[16],
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(17)),
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(22)),
        data);
}

/// <summary>
/// A minimal <see cref="IUsbDeviceSession"/> for RKDL loader-download tests: it reports a
/// Rockchip maskrom vendor/product pair so <c>RkdlSession.IsMaskromMode</c> is satisfied, records
/// every control transfer and bulk write, and never needs to answer because the loader path under
/// test must fail before touching the device.
/// <para>用于 RKDL loader 下载测试的最小 <see cref="IUsbDeviceSession"/>：它报告 Rockchip maskrom
/// 的厂商/产品 ID 组合以满足 <c>RkdlSession.IsMaskromMode</c>，记录每次控制传输与批量写入，并且
/// 无需应答，因为被测 loader 路径必须在接触设备之前就失败。</para>
/// </summary>
internal sealed class FakeRkdlUsbSession : IUsbDeviceSession
{
    /// <summary>Gets the control transfers the host issued, in order. <para>获取主机按序发出的控制传输。</para></summary>
    public List<(UsbSetupPacket Setup, byte[] Data)> ControlTransfers { get; } = [];

    /// <summary>Gets the bulk OUT payloads the host wrote, in order. <para>获取主机按序写出的批量 OUT 负载。</para></summary>
    public List<byte[]> BulkWrites { get; } = [];

    /// <summary>Gets or sets the device metadata reported to the session owner. <para>获取或设置向会话所有者报告的设备元数据。</para></summary>
    public UsbDeviceInfo DeviceInfo { get; set; } = new()
    {
        VendorId = 0x2207,
        ProductId = 0x350B,
        ApiName = "fake-rkdl",
        DevicePath = "fake://rkdl",
        DeviceKey = "fake-rkdl-key",
    };

    /// <inheritdoc />
    public int DefaultTimeoutMs => 1000;

    /// <inheritdoc />
    public byte EndpointIn => 0x82;

    /// <inheritdoc />
    public byte EndpointOut => 0x02;

    /// <inheritdoc />
    public byte[] Read(int length) => throw new NotSupportedException();

    /// <inheritdoc />
    public byte[] Read(int length, int timeoutMs) => throw new NotSupportedException();

    /// <inheritdoc />
    public int ReadInto(byte[] buffer, int offset, int length) => throw new NotSupportedException();

    /// <inheritdoc />
    public int ReadInto(byte[] buffer, int offset, int length, int timeoutMs) => throw new NotSupportedException();

    /// <inheritdoc />
    public UsbReadResult ReadPacket(byte[] buffer, int offset, int length, int timeoutMs) => throw new NotSupportedException();

    /// <inheritdoc />
    public int ReadInto(Span<byte> buffer, int timeoutMs) => throw new NotSupportedException();

    /// <inheritdoc />
    public long Write(byte[] data, int length) => Write(data, 0, length, DefaultTimeoutMs);

    /// <inheritdoc />
    public long Write(byte[] data, int length, int timeoutMs) => Write(data, 0, length, timeoutMs);

    /// <inheritdoc />
    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
        var copy = new byte[length];
        Array.Copy(data, offset, copy, 0, length);
        BulkWrites.Add(copy);
        return length;
    }

    /// <inheritdoc />
    public void WriteZlp(int timeoutMs)
    {
    }

    /// <inheritdoc />
    public int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        var copy = new byte[length];
        if (buffer is not null && length > 0)
        {
            Array.Copy(buffer, offset, copy, 0, length);
        }

        ControlTransfers.Add((setupPacket, copy));
        return length;
    }

    /// <inheritdoc />
    public void SetInterfaceAltSetting(byte interfaceNumber, byte altSetting)
    {
    }

    /// <inheritdoc />
    public void SetConfiguration(byte configuration)
    {
    }

    /// <inheritdoc />
    public UsbReadResult ReadInterrupt(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs) => throw new NotSupportedException();

    /// <inheritdoc />
    public long WriteInterrupt(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs) => throw new NotSupportedException();

    /// <inheritdoc />
    public void Reset()
    {
    }

    /// <inheritdoc />
    public Task<byte[]> ReadAsync(int length, int timeoutMs, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<int> ReadIntoAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<long> WriteAsync(byte[] data, int length, int timeoutMs, CancellationToken cancellationToken = default) => Task.FromResult(Write(data, length, timeoutMs));

    /// <inheritdoc />
    public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default) => Task.FromResult(Write(data, offset, length, timeoutMs));

    /// <inheritdoc />
    public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken = default) => Task.FromResult(Write(data, offset, length, timeoutMs));

    /// <inheritdoc />
    public Task WriteZlpAsync(int timeoutMs, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<int> ControlTransferAsync(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default) => Task.FromResult(ControlTransfer(setupPacket, buffer, offset, length, timeoutMs));

    /// <inheritdoc />
    public Task SetInterfaceAltSettingAsync(byte interfaceNumber, byte altSetting, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task SetConfigurationAsync(byte configuration, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<UsbReadResult> ReadInterruptAsync(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<long> WriteInterruptAsync(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}