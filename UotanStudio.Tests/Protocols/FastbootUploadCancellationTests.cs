using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// An abandoned fastboot upload still owes the bootloader its declared payload and terminal frame. These cases pin
/// the drain that keeps the next command aligned, plus the reset that takes over when the target never completes.
/// <para>被弃单的 fastboot 上传仍欠 bootloader 声明的负载与终止帧。这些用例固定住使下一条命令保持对齐的排空，
/// 以及目标始终不完成时的传输复位。</para>
/// </summary>
public sealed class FastbootUploadCancellationTests
{
    private const int ChunkBytes = 4096;
    private const int PayloadBytes = ChunkBytes * 8;

    [Fact]
    public void UploadData_CancelledMidPayload_DrainsRemainderAndConsumesTerminalFrame()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new UploadCancellationTransport(cancellation);
        using var session = new FastbootSession(transport, timeoutMs: 2000);
        using var output = new MemoryStream();

        Assert.Throws<OperationCanceledException>(() => session.UploadData(
            output, PayloadBytes, cancellationToken: cancellation.Token));

        Assert.Equal(PayloadBytes, transport.PayloadBytesRead);
        Assert.Equal(2 * ChunkBytes, output.Length);
        Assert.Equal(1, transport.TerminalFrames);
        Assert.Equal(0, transport.ResetCalls);
    }

    [Fact]
    public void UploadData_TerminalFrameNeverArrives_ResetsTransport()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new UploadCancellationTransport(cancellation, failTerminal: true);
        using var session = new FastbootSession(transport, timeoutMs: 2000);
        using var output = new MemoryStream();

        Assert.Throws<OperationCanceledException>(() => session.UploadData(
            output, PayloadBytes, cancellationToken: cancellation.Token));

        Assert.Equal(PayloadBytes, transport.PayloadBytesRead);
        Assert.Equal(1, transport.ResetCalls);
    }

    private sealed class UploadCancellationTransport(CancellationTokenSource cancellation, bool failTerminal = false)
        : ICommTransport
    {
        private readonly byte[] _payload = new byte[PayloadBytes];
        private int _delivered;
        private bool _terminalReturned;

        internal int PayloadBytesRead => _delivered;

        internal int TerminalFrames { get; private set; }

        internal int ResetCalls { get; private set; }

        public bool IsConnected => true;

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        public void Reset() => ResetCalls++;

        public int Read(byte[] buffer, int offset, int count, int timeoutMs)
        {
            if (_delivered < PayloadBytes)
            {
                var take = Math.Min(Math.Min(count, ChunkBytes), PayloadBytes - _delivered);
                Buffer.BlockCopy(_payload, _delivered, buffer, offset, take);
                _delivered += take;
                if (!cancellation.IsCancellationRequested && _delivered >= 2 * ChunkBytes)
                {
                    cancellation.Cancel();
                }

                return take;
            }

            if (_terminalReturned)
            {
                return 0;
            }

            _terminalReturned = true;
            if (failTerminal)
            {
                throw new TimeoutException("The bootloader never emitted the terminal frame.");
            }

            TerminalFrames++;
            var terminal = Encoding.ASCII.GetBytes("OKAY");
            Buffer.BlockCopy(terminal, 0, buffer, offset, terminal.Length);
            return terminal.Length;
        }

        public Task<int> ReadAsync(byte[] buffer, int offset, int count, int timeoutMs,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public long Write(byte[] data, int offset, int count, int timeoutMs) => count;

        public Task<long> WriteAsync(byte[] data, int offset, int count, int timeoutMs,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Stream GetStream() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}