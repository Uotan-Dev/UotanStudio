using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Comm.Transports.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Checks how the SpacemiT boot stage is established: the descriptor serial separates the BootROM download mode
/// from the U-Boot gadget during discovery, and <c>getvar:version-brom</c> settles it on a live session.
/// <para>校验进迭时空启动阶段的确定方式：发现阶段由描述符序列号区分 BootROM 下载模式与 U-Boot gadget，
/// 实时会话上则由 <c>getvar:version-brom</c> 定论。</para>
/// </summary>
public sealed class SpacemiTBootStageTests
{
    [Theory]
    [InlineData(SpacemiTConstants.UsbVendorId, SpacemiTConstants.UsbProductId, "DFU-DEVICE", SpacemiTBootStage.BromFdl)]
    [InlineData(SpacemiTConstants.UsbVendorId, SpacemiTConstants.UsbProductId, "dfu-device", SpacemiTBootStage.BromFdl)]
    [InlineData(SpacemiTConstants.UsbVendorId, SpacemiTConstants.UsbProductId, " DFU-DEVICE ", SpacemiTBootStage.BromFdl)]
    [InlineData(SpacemiTConstants.UsbVendorId, SpacemiTConstants.UsbProductId, "10000030", SpacemiTBootStage.Uboot)]
    [InlineData(SpacemiTConstants.UsbVendorId, SpacemiTConstants.UsbProductId, "10000030-2", SpacemiTBootStage.Uboot)]
    [InlineData(SpacemiTConstants.UsbVendorId, SpacemiTConstants.UsbProductId, "UNKNOWN", SpacemiTBootStage.Unknown)]
    [InlineData(SpacemiTConstants.UsbVendorId, SpacemiTConstants.UsbProductId, null, SpacemiTBootStage.Unknown)]
    [InlineData(0x18D1, SpacemiTConstants.UsbProductId, "DFU-DEVICE", SpacemiTBootStage.Unknown)]
    [InlineData(SpacemiTConstants.UsbVendorId, 0x4EE7, "DFU-DEVICE", SpacemiTBootStage.Unknown)]
    public void FromIdentity_SeparatesBootromMarkerFromBoardSerial(ushort vendorId, ushort productId, string? serial,
        SpacemiTBootStage expected)
    {
        Assert.Equal(expected, SpacemiTBootStageClassifier.FromIdentity(vendorId, productId, serial));
    }

    [Fact]
    public void Probe_AnsweredVersionBrom_ReportsBootromFdlAndKeepsTheSessionUsable()
    {
        using var session = new FastbootSession(new ScriptedFastbootTransport(new Dictionary<string, string>
        {
            [SpacemiTConstants.VarBromVersion] = "1.0",
            ["version"] = "0.4",
            ["product"] = "k1-x",
        }), timeoutMs: 2000);

        Assert.Equal(SpacemiTBootStage.BromFdl, SpacemiTBootStageClassifier.Probe(session));
        Assert.Equal("0.4", session.GetVariable("version", useCache: false));
    }

    [Fact]
    public void Probe_OnlyUbootVariablesAnswer_ReportsUboot()
    {
        using var session = new FastbootSession(new ScriptedFastbootTransport(new Dictionary<string, string>
        {
            ["version"] = "0.4",
            ["product"] = "k1-x",
        }), timeoutMs: 2000);

        Assert.Equal(SpacemiTBootStage.Uboot, SpacemiTBootStageClassifier.Probe(session));
    }

    [Fact]
    public void Probe_NoVariableAnswers_ReportsUnknown()
    {
        using var session = new FastbootSession(new ScriptedFastbootTransport(new Dictionary<string, string>()),
            timeoutMs: 2000);

        Assert.Equal(SpacemiTBootStage.Unknown, SpacemiTBootStageClassifier.Probe(session));
    }

    /// <summary>
    /// Answers each <c>getvar</c> from a table and rejects everything else, reproducing a device that does not pad
    /// its replies.
    /// <para>按表应答每条 <c>getvar</c> 并拒绝其它命令，复现不补齐应答的设备。</para>
    /// </summary>
    private sealed class ScriptedFastbootTransport(Dictionary<string, string> answers) : ICommTransport
    {
        private readonly Queue<byte[]> _replies = [];

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
            var key = command.StartsWith("getvar:", StringComparison.Ordinal) ? command["getvar:".Length..] : command;
            _replies.Enqueue(Encoding.ASCII.GetBytes(answers.TryGetValue(key, out var answer)
                ? $"OKAY{answer}"
                : "FAILVariable not implemented"));
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

        public Task<int> ReadAsync(byte[] buffer, int offset, int count, int timeoutMs,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Read(buffer, offset, count, timeoutMs));

        public Task<long> WriteAsync(byte[] data, int offset, int count, int timeoutMs,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Write(data, offset, count, timeoutMs));

        public Stream GetStream() => throw new NotSupportedException();
    }
}