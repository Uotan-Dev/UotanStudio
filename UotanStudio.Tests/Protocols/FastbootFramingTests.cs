using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Fastboot wire-framing checks for bootloaders that answer with more than their reply: the SpacemiT
/// BootROM FDL sends its whole status buffer, so stale bytes of earlier replies sit behind the
/// terminating NUL of the current one. Every case drives a scripted transport that reproduces that
/// shape without contacting a device.
/// <para>针对“应答长于实际回复”的 bootloader 的 fastboot 线上组帧校验：进迭时空 BootROM FDL 会把它整个
/// 状态缓冲区发出来，因此当前应答的终止 NUL 之后残留着更早应答的陈旧字节。每个用例都用脚本化传输复现该形态，
/// 不接触设备。</para>
/// </summary>
public sealed class FastbootFramingTests
{
    [Fact]
    public void GetVariable_DiscardsStaleBytesBehindTerminalFrame_KeepsEveryReplyAligned()
    {
        var transport = new StaleTailTransport(new Dictionary<string, string>
        {
            ["getvar:version-brom"] = "1.0",
            ["getvar:version"] = "0.4",
            ["getvar:product"] = "k1",
            ["getvar:max-download-size"] = "0x00036000",
        });
        using var session = new FastbootSession(transport, timeoutMs: 2000);

        Assert.Equal("1.0", session.GetVariable("version-brom", useCache: false));
        Assert.Equal("0.4", session.GetVariable("version", useCache: false));
        Assert.Equal("k1", session.GetVariable("product", useCache: false));
        Assert.Equal("0x00036000", session.GetVariable("max-download-size", useCache: false));
    }

    [Fact]
    public void GetVariable_FailedReplyBehindStaleBytes_StillReportsTheFollowingValue()
    {
        var transport = new StaleTailTransport(new Dictionary<string, string>
        {
            ["getvar:version"] = "0.4",
        });
        using var session = new FastbootSession(transport, timeoutMs: 2000);

        var missing = session.GetVarWithResult("product", useCache: false);
        Assert.False(missing.Success);
        Assert.Equal("0.4", session.GetVariable("version", useCache: false));
    }

    /// <summary>
    /// Reproduces a bootloader that answers with its whole status buffer: the reply, a terminating NUL,
    /// and whatever the previous reply left behind.
    /// <para>复现“把整个状态缓冲区发出来”的 bootloader：真实回复、终止 NUL，以及更早应答残留的字节。</para>
    /// </summary>
    private sealed class StaleTailTransport(Dictionary<string, string> answers, int tailLength = 40) : ICommTransport
    {
        private readonly Queue<byte[]> _replies = [];
        private int _staleSeed;

        public bool IsConnected => true;

        public void Dispose()
        {
        }

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        public void Reset()
        {
        }

        public long Write(byte[] data, int offset, int count, int timeoutMs)
        {
            var command = Encoding.UTF8.GetString(data, offset, count);
            var reply = answers.TryGetValue(command, out var answer)
                ? $"OKAY{answer}"
                : "FAILVariable not implemented";
            _replies.Enqueue(WithStaleTail(Encoding.ASCII.GetBytes(reply)));
            return count;
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMs)
        {
            if (_replies.Count == 0)
            {
                return 0;
            }

            var reply = _replies.Dequeue();
            var copied = Math.Min(count, reply.Length);
            reply.AsSpan(0, copied).CopyTo(buffer.AsSpan(offset));
            if (copied < reply.Length)
            {
                _replies.Enqueue(reply[copied..]);
            }

            return copied;
        }

        public Task<int> ReadAsync(byte[] buffer, int offset, int count, int timeoutMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(Read(buffer, offset, count, timeoutMs));

        public Task<long> WriteAsync(byte[] data, int offset, int count, int timeoutMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(Write(data, offset, count, timeoutMs));

        public Stream GetStream() => throw new NotSupportedException();

        /// <summary>
        /// Appends the NUL plus a stale tail whose content changes per reply, so a shifted parser reads a
        /// value that provably belongs to another command instead of accidentally matching.
        /// <para>追加 NUL 与逐条变化的陈旧尾部，使错位的解析器读到可证明属于另一条命令的值，而不是偶然匹配。</para>
        /// </summary>
        private byte[] WithStaleTail(byte[] reply)
        {
            _staleSeed++;
            var tail = new byte[tailLength];
            for (var i = 0; i < tail.Length; i++)
            {
                tail[i] = (byte)('a' + ((_staleSeed + i) % 26));
            }

            var result = new byte[reply.Length + 1 + tail.Length];
            reply.CopyTo(result, 0);
            tail.CopyTo(result, reply.Length + 1);
            return result;
        }
    }
}