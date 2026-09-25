using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public partial class FirehoseGptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgramRawPartitionFromFileAsync_ValidRange_PreservesOutsideBytesAndReportsEvidence(bool verify)
    {
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, 512).ToArray();
        var path = dir.WriteBytes("raw.bin", payload);
        var disk = GptReaderTests.CreateDisk(512);
        disk.AsSpan(40 * 512, 2048).Fill(0xA5);
        using var transport = ProgramTransport(disk);
        using var protocol = Protocol(transport, 512);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        var result = await protocol.ProgramRawPartitionFromFileAsync(snapshot, "boot", path, 512, verify,
            TestContext.Current.CancellationToken);

        Assert.Equal(new FirehosePartitionWriteResult(512, verify ? 512 : 0), result);
        Assert.Equal(payload, disk[(41 * 512)..(42 * 512)]);
        Assert.All(disk[(40 * 512)..(41 * 512)], value => Assert.Equal(0xA5, value));
        Assert.All(disk[(42 * 512)..(44 * 512)], value => Assert.Equal(0xA5, value));
        var commands = ProgramCommands(transport).ToArray();
        Assert.Single(commands, command => command.Name == "program");
        Assert.DoesNotContain(commands, command => command.Name == "erase");
        Assert.Equal(verify ? 8 : 7, transport.Writes.Count - before);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(511, 0)]
    [InlineData(2560, 0)]
    [InlineData(512, -1)]
    [InlineData(512, 1)]
    [InlineData(512, 2048)]
    public async Task ProgramRawPartitionFromFileAsync_InvalidSourceOrRange_DoesNotQueryOrWrite(int length, long offset)
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("raw.bin", new byte[length]);
        using var transport = ProgramTransport(GptReaderTests.CreateDisk(512));
        using var protocol = Protocol(transport, 512);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            protocol.ProgramRawPartitionFromFileAsync(snapshot, "boot", path, offset,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(error is ArgumentOutOfRangeException or InvalidDataException);
        Assert.Equal(before, transport.Writes.Count);
        Assert.False(protocol.RequiresReconnect);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task ProgramRawPartitionFromFileAsync_ChangedGpt_DoesNotProgram()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("raw.bin", new byte[512]);
        var disk = GptReaderTests.CreateDisk(512);
        using var transport = ProgramTransport(disk);
        using var protocol = Protocol(transport, 512);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);
        disk[1024 + 48] = 1;
        GptReaderTests.FixArrays(disk);

        await Assert.ThrowsAsync<InvalidDataException>(() => protocol.ProgramRawPartitionFromFileAsync(snapshot,
            "boot", path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain(ProgramCommands(transport), command => command.Name == "program");
        Assert.False(protocol.RequiresReconnect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgramRawPartitionFromFileAsync_RejectionOrMismatch_DoesNotRetry(bool rejectFinal)
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("raw.bin", Enumerable.Repeat((byte)0x42, 512).ToArray());
        using var transport = ProgramTransport(GptReaderTests.CreateDisk(512), rejectFinal, corruptWrite: !rejectFinal);
        using var protocol = Protocol(transport, 512);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAnyAsync<EdlException>(() => protocol.ProgramRawPartitionFromFileAsync(snapshot,
            "boot", path, verify: true, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(rejectFinal ? EdlErrorCode.ProgramFailed : EdlErrorCode.OnTargetVerificationFailed, error.ErrorCode);
        Assert.Single(ProgramCommands(transport), command => command.Name == "program");
        Assert.Equal(rejectFinal, protocol.RequiresReconnect);
        if (!rejectFinal)
        {
            Assert.True((await protocol.NopAsync(TestContext.Current.CancellationToken)).IsAck);
        }
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task ProgramRawPartitionFromFileAsync_QueuedProbe_CannotInterleaveWriteAndVerify()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("raw.bin", Enumerable.Repeat((byte)0x42, 512).ToArray());
        using var transport = ProgramTransport(GptReaderTests.CreateDisk(512));
        using var protocol = Protocol(transport, 512);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);
        var respond = transport.OnWrite!;
        Task<FirehoseResponse>? queued = null;
        transport.OnWrite = bytes =>
        {
            if (Encoding.UTF8.GetString(bytes).Contains("<program ", StringComparison.Ordinal) && queued is null)
            {
                queued = protocol.NopAsync(TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
            }
            return respond(bytes);
        };

        await protocol.ProgramRawPartitionFromFileAsync(snapshot, "boot", path, verify: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        Assert.True((await queued).IsAck);

        Assert.Equal(new[] { "program", "read", "nop" },
            ProgramCommands(transport).TakeLast(3).Select(command => command.Name.LocalName));
    }

    private static IEnumerable<XElement> ProgramCommands(ScriptedCommTransport transport) => transport.Writes
        .Where(bytes => bytes[0] == '<')
        .Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single());

    private static ScriptedCommTransport ProgramTransport(byte[] disk, bool rejectFinal = false, bool corruptWrite = false)
    {
        var transport = Transport(disk, 512);
        var respond = transport.OnWrite!;
        var remaining = 0;
        var position = 0;
        transport.OnWrite = bytes =>
        {
            if (remaining > 0)
            {
                Assert.InRange(bytes.Length, 1, remaining);
                bytes.CopyTo(disk, position);
                position += bytes.Length;
                remaining -= bytes.Length;
                if (remaining != 0)
                {
                    return [];
                }
                if (corruptWrite)
                {
                    disk[position - 1] ^= 1;
                }
                return [Encoding.UTF8.GetBytes($"<data><response value='{(rejectFinal ? "NAK" : "ACK")}' rawmode='false'/></data>")];
            }
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            if (command.Name == "program")
            {
                position = checked((int)command.Attribute("start_sector")! * 512);
                remaining = checked((int)command.Attribute("num_partition_sectors")! * 512);
                return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>")];
            }
            return respond(bytes);
        };
        return transport;
    }
}