using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Pins that a program payload block declared with <c>ZlpAwareHost=1</c> is really terminated by a zero-length packet
/// on the USB transport. An EDL session always exchanges through its Sahara probe wrapper, so a lookup that only
/// matched the bare USB transport silently dropped every ZLP while still promising it in the configure request, and
/// the target then kept waiting for the block to end.
/// <para>固定以 <c>ZlpAwareHost=1</c> 声明的 program 载荷块确实在 USB 传输上以零长度包收尾。EDL 会话一律经
/// Sahara 探测包装交换数据，因此只匹配裸 USB 传输的查找会在 configure 请求里承诺 ZLP 却在实际上静默丢弃，目标
/// 随后一直等待该块结束。</para>
/// </summary>
public sealed class FirehoseProgramZlpTransportTests
{
    private const int SectorSize = 4096;
    private const int PayloadBytes = 8192;

    [Fact]
    public async Task ProgramAsync_ZlpAwareHostThroughProbeWrapper_SendsOneZlpPerBlock()
    {
        var correlation = new ZlpRecordingSession();
        var inner = new UsbDeviceTransport(CorrelationSession.Create(correlation));
        var transport = new EdlProbeTransport(inner);
        using var protocol = new FirehoseProtocol(transport);
        protocol.Configuration.SectorSize = SectorSize;
        protocol.Configuration.ZlpAwareHost = true;
        using var payload = new MemoryStream(new byte[PayloadBytes]);

        var response = await protocol.ProgramAsync(payload, 0, PayloadBytes / SectorSize, "prog.bin", 0,
            TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(1, correlation.ZlpCalls);
    }

    [Fact]
    public async Task ProgramAsync_ZlpAwareHostDisabled_SendsNoZlp()
    {
        var correlation = new ZlpRecordingSession();
        var inner = new UsbDeviceTransport(CorrelationSession.Create(correlation));
        var transport = new EdlProbeTransport(inner);
        using var protocol = new FirehoseProtocol(transport);
        protocol.Configuration.SectorSize = SectorSize;
        protocol.Configuration.ZlpAwareHost = false;
        using var payload = new MemoryStream(new byte[PayloadBytes]);

        await protocol.ProgramAsync(payload, 0, PayloadBytes / SectorSize, "prog.bin", 0,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, correlation.ZlpCalls);
    }

    private sealed class ZlpRecordingSession
    {
        internal int ZlpCalls { get; set; }
    }

    /// <summary>
    /// USB session that records ZLP calls and answers the program command and its completion with the acknowledgements
    /// a Firehose programmer sends, so one program transfer can run end to end.
    /// <para>记录 ZLP 调用，并以 Firehose programmer 的确认应答 program 命令及其完成，使一次 program 传输可以端到端
    /// 运行。</para>
    /// </summary>
    private sealed class CorrelationSession : IUsbDeviceSession
    {
        private readonly ZlpRecordingSession _correlation;
        private readonly Queue<string> _responses = new();
        private int _writes;

        private CorrelationSession(ZlpRecordingSession correlation) => _correlation = correlation;

        internal static CorrelationSession Create(ZlpRecordingSession correlation) => new(correlation);

        public int DefaultTimeoutMs => 5000;

        public bool ResetReenumeratesDevice => false;

        public UsbDeviceInfo DeviceInfo => new();

        public byte EndpointIn => 0x81;

        public byte EndpointOut => 0x01;

        public byte[] Read(int length) => [];

        public byte[] Read(int length, int timeoutMs) => [];

        public int ReadInto(byte[] buffer, int offset, int length) => ReadInto(buffer, offset, length, 5000);

        public int ReadInto(byte[] buffer, int offset, int length, int timeoutMs)
        {
            var body = _responses.Count > 0
                ? _responses.Dequeue()
                : "<?xml version=\"1.0\" ?><data><response value=\"ACK\" rawmode=\"false\" /></data>";
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
            return bytes.Length;
        }

        public int ReadInto(Span<byte> buffer, int timeoutMs) => 0;

        public UsbReadResult ReadPacket(byte[] buffer, int offset, int length, int timeoutMs)
            => new(ReadInto(buffer, offset, length, timeoutMs), false, false);

        public long Write(byte[] data, int length) => Write(data, 0, length, 5000);

        public long Write(byte[] data, int length, int timeoutMs) => Write(data, 0, length, timeoutMs);

        public long Write(byte[] data, int offset, int length, int timeoutMs)
        {
            // Only the <program> command write is answered; the payload blocks that follow it are pure data. The
            // command is acknowledged in raw mode, and the completion read is served on the next transfer read
            // because that is exactly the order a Firehose programmer produces.
            var isPayload = length > 1024;
            if (!isPayload)
            {
                _writes++;
                _responses.Enqueue(
                    "<?xml version=\"1.0\" ?><data><response value=\"ACK\" rawmode=\"true\" /></data>");
            }

            return length;
        }

        public void WriteZlp(int timeoutMs) => _correlation.ZlpCalls++;

        public int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs) => 0;

        public void SetInterfaceAltSetting(byte interfaceNumber, byte altSetting) { }

        public void SetConfiguration(byte configuration) { }

        public UsbReadResult ReadInterrupt(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs)
            => new(0, false, false);

        public long WriteInterrupt(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs) => 0;

        public void Reset() { }

        public Task<byte[]> ReadAsync(int length, int timeoutMs, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<byte>());

        public Task<int> ReadIntoAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ReadInto(buffer, offset, length, timeoutMs));

        public Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new UsbReadResult(ReadInto(buffer, offset, length, timeoutMs), false, false));

        public Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs,
            IProgress<long>? progress, CancellationToken cancellationToken = default)
            => ReadPacketAsync(buffer, offset, length, timeoutMs, cancellationToken);

        public Task<long> WriteAsync(byte[] data, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Write(data, 0, length, timeoutMs));

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Write(data, offset, length, timeoutMs));

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, IProgress<long>? progress,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Write(data, offset, length, timeoutMs));

        public Task WriteZlpAsync(int timeoutMs, CancellationToken cancellationToken = default)
        {
            _correlation.ZlpCalls++;
            return Task.CompletedTask;
        }

        public Task<int> ControlTransferAsync(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length,
            int timeoutMs, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task SetInterfaceAltSettingAsync(byte interfaceNumber, byte altSetting,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetConfigurationAsync(byte configuration, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<UsbReadResult> ReadInterruptAsync(byte endpointAddress, byte[] buffer, int offset, int length,
            int timeoutMs, CancellationToken cancellationToken = default)
            => Task.FromResult(new UsbReadResult(0, false, false));

        public Task<long> WriteInterruptAsync(byte endpointAddress, byte[] data, int offset, int length,
            int timeoutMs, CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() { }
    }
}