using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Covers the Sahara-versus-Firehose decision: positive evidence, the wait-on-incomplete-document rule, the
/// single Firehose probe, memory-debug separation and the Sahara state-machine reset.
/// <para>覆盖 Sahara 与 Firehose 的判读：正向证据、未完成文档继续等待、单次 Firehose 探测、内存调试模式
/// 区分以及 Sahara 状态机复位。</para>
/// </summary>
public class EdlProbeDetectionTests
{
    private const uint HelloCommand = EdlConstants.SaharaCmdHello;
    private const uint ResetStateCommand = EdlConstants.SaharaCmdResetState;
    private const uint ExecuteDataCommand = EdlConstants.SaharaCmdExecuteData;

    [Fact]
    public async Task ProbeProtocol_SaharaHello_ReportsSaharaWithoutWriting()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(Hello(SaharaMode.ImageTxPending));
        using var session = new EdlSession(transport);

        var state = await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EdlProtocolState.Sahara, state);
        Assert.Empty(transport.Writes);
        Assert.Equal(SaharaMode.ImageTxPending, session.SaharaHandshake?.Mode);
        Assert.Equal(2u, session.SaharaHandshake?.ProtocolVersion);
    }

    [Fact]
    public async Task ProbeProtocol_HelloWithZeroMaxCommandLength_StillRecordsHandshake()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(Hello(SaharaMode.ImageTxPending, maxCommandLength: 0));
        using var session = new EdlSession(transport);

        await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(session.SaharaHandshake);
        Assert.Equal(0u, session.SaharaHandshake!.MaxCommandLength);
    }

    [Fact]
    public async Task ProbeProtocol_MemoryDebugHello_ReportsSaharaMemoryDebug()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(Hello(SaharaMode.MemoryDebug));
        using var session = new EdlSession(transport);

        var state = await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EdlProtocolState.SaharaMemoryDebug, state);
        Assert.Equal(SaharaMode.MemoryDebug, session.SaharaHandshake?.Mode);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ProbeProtocol_SilentTarget_WritesExactlyOneNopThenReportsUnknown()
    {
        var transport = new ScriptedCommTransport();
        using var session = new EdlSession(transport);

        var state = await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EdlProtocolState.Unknown, state);
        var probe = Assert.Single(transport.Writes);
        Assert.Contains("<nop", System.Text.Encoding.UTF8.GetString(probe), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeProtocol_PartialDocument_KeepsReadingInsteadOfGivingUp()
    {
        var prefix = System.Text.Encoding.UTF8.GetBytes(
            "<data><log value=\"" + new string('x', 90) + "\"/>");
        var completion = System.Text.Encoding.UTF8.GetBytes(
            "</data><data><response value=\"ACK\" rawmode=\"false\"/></data>");
        var transport = new GatedCommTransport([prefix, null, null, completion]);
        using var session = new EdlSession(transport);

        var state = await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EdlProtocolState.Firehose, state);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ProbeProtocol_NakToNop_ReportsFirehose()
    {
        var transport = new ScriptedCommTransport
        {
            OnWrite = _ => [System.Text.Encoding.UTF8.GetBytes(
                "<data><response value=\"NAK\" rawmode=\"false\"/></data>")]
        };
        using var session = new EdlSession(transport);

        var state = await session.ProbeProtocolAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EdlProtocolState.Firehose, state);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task Initialize_OnMemoryDebugTarget_FailsBeforeAnyWrite()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(Hello(SaharaMode.MemoryDebug));
        using var session = new EdlSession(transport);
        Assert.Equal(EdlProtocolState.SaharaMemoryDebug,
            await session.ProbeProtocolAsync(TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<SaharaException>(() =>
            session.InitializeAsync(new MemoryStream(new byte[64]), TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task Handshake_UnexpectedCommand_SendsResetStateMachine()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(Hello(SaharaMode.ImageTxPending));
        transport.Enqueue(Header(ExecuteDataCommand, 8));
        var sahara = new SaharaProtocol(transport);

        await Assert.ThrowsAsync<SaharaException>(() => sahara.HandshakeAsync(
            new MemoryStream(new byte[512]), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(transport.Writes, write =>
            write.Length == 8 && BinaryPrimitives.ReadUInt32LittleEndian(write) == ResetStateCommand);
    }

    [Theory]
    [InlineData(SaharaMode.ImageTxPending, 2u, 1u, 48u, EdlProtocolState.Sahara)]
    [InlineData(SaharaMode.MemoryDebug, 2u, 1u, 48u, EdlProtocolState.SaharaMemoryDebug)]
    [InlineData(SaharaMode.Command, 2u, 1u, 48u, EdlProtocolState.Sahara)]
    [InlineData(SaharaMode.ImageTxPending, 0u, 1u, 48u, EdlProtocolState.Unknown)]
    [InlineData(SaharaMode.ImageTxPending, 2u, 3u, 48u, EdlProtocolState.Unknown)]
    [InlineData(SaharaMode.ImageTxPending, 2u, 1u, 20u, EdlProtocolState.Unknown)]
    [InlineData(SaharaMode.ImageTxPending, 2u, 1u, 4u, EdlProtocolState.Unknown)]
    public void Classify_HelloRange_MatchesWireEvidence(
        SaharaMode mode, uint version, uint minVersion, uint lengthHint, EdlProtocolState expected)
    {
        var hello = Hello(mode, version, minVersion);
        var bytes = lengthHint == 48 ? hello : hello[..(int)Math.Min(lengthHint, hello.Length)];

        Assert.Equal(expected, EdlProbeTransport.Classify(bytes));
    }

    [Fact]
    public void Classify_CompleteAckDocument_ReportsFirehose()
        => Assert.Equal(EdlProtocolState.Firehose, EdlProbeTransport.Classify(
            System.Text.Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<data><response value=\"ACK\" rawmode=\"false\"/></data>")));

    [Fact]
    public void Classify_LogOnlyDocument_IsNotFirehoseEvidence()
        => Assert.Equal(EdlProtocolState.Unknown, EdlProbeTransport.Classify(
            System.Text.Encoding.UTF8.GetBytes("<data><log value=\"startup\"/></data>")));

    private static byte[] Hello(SaharaMode mode, uint version = 2, uint minVersion = 1,
        uint maxCommandLength = 0)
    {
        var packet = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, HelloCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), version);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), minVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), maxCommandLength);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), (uint)mode);
        return packet;
    }

    private static byte[] Header(uint command, uint length)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, command);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), length);
        return header;
    }

    /// <summary>
    /// Yields scripted chunks in order, where a <see langword="null"/> entry models a read that returns no
    /// bytes without blocking.
    /// <para>按顺序产出脚本化数据块，其中 <see langword="null"/> 条目模拟一次不阻塞且没有返回字节的读取。</para>
    /// </summary>
    private sealed class GatedCommTransport(IEnumerable<byte[]?> chunks) : ICommTransport
    {
        private readonly Queue<byte[]?> _chunks = new(chunks);
        private byte[]? _head;

        public List<byte[]> Writes { get; } = [];

        public bool IsConnected => true;

        public void Connect() { }

        public void Disconnect() { }

        public void Reset() { }

        public Stream GetStream() => throw new NotSupportedException();

        public void Dispose() { }

        private int Take(byte[] buffer, int offset, int length)
        {
            if (_head is null)
            {
                if (_chunks.Count == 0)
                {
                    return 0;
                }

                var next = _chunks.Dequeue();
                if (next is null)
                {
                    return 0;
                }

                _head = next;
            }

            var count = Math.Min(length, _head.Length);
            Array.Copy(_head, 0, buffer, offset, count);
            _head = count == _head.Length ? null : _head[count..];
            return count;
        }

        public int Read(byte[] buffer, int offset, int length, int timeoutMs) => Take(buffer, offset, length);

        public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => Task.FromResult(Take(buffer, offset, length));

        public long Write(byte[] data, int offset, int length, int timeoutMs)
        {
            Writes.Add(data[offset..(offset + length)]);
            return length;
        }

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => Task.FromResult(Write(data, offset, length, timeoutMs));
    }
}