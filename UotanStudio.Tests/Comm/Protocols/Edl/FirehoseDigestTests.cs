using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public class FirehoseDigestTests
{
    [Fact]
    public async Task ProgramRawPartitionFromFileAsync_TargetDigestMatches_SkipsReadback()
    {
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, 512).ToArray();
        var path = dir.WriteBytes("raw.bin", payload);
        var disk = GptReaderTests.CreateDisk(512);
        using var transport = DigestProgramTransport(disk, rejectDigest: false, mismatchDigest: false);
        using var protocol = Protocol(transport);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);

        var result = await protocol.ProgramRawPartitionFromFileAsync(snapshot, "boot", path, 0, verify: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(new FirehosePartitionWriteResult(512, 512), result);
        var commands = Commands(transport).ToArray();
        var programIndex = Array.FindLastIndex(commands, command => command.Name.LocalName == "program");
        Assert.DoesNotContain(commands[(programIndex + 1)..], command => command.Name.LocalName == "read");
        Assert.Contains(commands[(programIndex + 1)..], command => command.Name.LocalName == "getsha256digest");
        Assert.False(protocol.RequiresReconnect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgramRawPartitionFromFileAsync_TargetDigestMissingOrMismatched_FallsBackToReadback(bool mismatch)
    {
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, 512).ToArray();
        var path = dir.WriteBytes("raw.bin", payload);
        var disk = GptReaderTests.CreateDisk(512);
        using var transport = DigestProgramTransport(disk, rejectDigest: false, mismatchDigest: mismatch,
            omitDigest: !mismatch);
        using var protocol = Protocol(transport);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);

        var result = await protocol.ProgramRawPartitionFromFileAsync(snapshot, "boot", path, 0, verify: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(new FirehosePartitionWriteResult(512, 512), result);
        Assert.Contains(Commands(transport), command => command.Name.LocalName == "read");
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProgramRawPartitionFromFileAsync_DigestRejected_FallsBackToReadback()
    {
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, 512).ToArray();
        var path = dir.WriteBytes("raw.bin", payload);
        var disk = GptReaderTests.CreateDisk(512);
        using var transport = DigestProgramTransport(disk, rejectDigest: true, mismatchDigest: false);
        using var protocol = Protocol(transport);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);

        var result = await protocol.ProgramRawPartitionFromFileAsync(snapshot, "boot", path, 0, verify: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(new FirehosePartitionWriteResult(512, 512), result);
        Assert.Contains(Commands(transport), command => command.Name.LocalName == "read");
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProgramRawPartitionFromFileAsync_DigestMismatchAndCorruptWrite_FailsVerification()
    {
        using var dir = new TempDirectory();
        var payload = Enumerable.Repeat((byte)0x42, 512).ToArray();
        var path = dir.WriteBytes("raw.bin", payload);
        var disk = GptReaderTests.CreateDisk(512);
        using var transport = DigestProgramTransport(disk, rejectDigest: false, mismatchDigest: false,
            corruptWrite: true);
        using var protocol = Protocol(transport);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(() => protocol.ProgramRawPartitionFromFileAsync(snapshot, "boot",
            path, 0, verify: true, TestContext.Current.CancellationToken));
        Assert.Contains(Commands(transport), command => command.Name.LocalName == "read");
    }

    [Fact]
    public async Task GetSha256DigestAsync_DigestInResponseAttribute_IsParsed()
    {
        var disk = GptReaderTests.CreateDisk(512);
        using var transport = new ScriptedCommTransport();
        var respond = transport.OnWrite;
        transport.OnWrite = bytes =>
        {
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            if (command.Name == "getsha256digest")
            {
                return [Encoding.UTF8.GetBytes(
                    "<data><response value='ACK' Digest='00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff'/></data>")];
            }
            return respond(bytes);
        };
        using var protocol = Protocol(transport);

        var digest = await protocol.GetSha256DigestAsync(40, 1, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(digest);
        Assert.Equal(32, digest.Length);
        Assert.Equal(0x00, digest[0]);
        Assert.Equal(0xff, digest[31]);
    }

    [Fact]
    public async Task GetSha256DigestAsync_DigestInLog_IsParsed()
    {
        var disk = GptReaderTests.CreateDisk(512);
        using var transport = new ScriptedCommTransport();
        var respond = transport.OnWrite;
        transport.OnWrite = bytes =>
        {
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            if (command.Name == "getsha256digest")
            {
                return [Encoding.UTF8.GetBytes(
                    "<data><log value='Digest 00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff'/><response value='ACK' rawmode='false'/></data>")];
            }
            return respond(bytes);
        };
        using var protocol = Protocol(transport);

        var digest = await protocol.GetSha256DigestAsync(40, 1, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(digest);
        Assert.Equal(32, digest.Length);
    }

    [Fact]
    public async Task TryVerifyProgramWithTargetDigestOwnedAsync_MatchingDigest_ReturnsTrue()
    {
        var payload = Enumerable.Repeat((byte)0x42, 512).ToArray();
        using var source = new MemoryStream(payload);
        var disk = GptReaderTests.CreateDisk(512);
        Array.Copy(payload, 0, disk, 40 * 512, 512);
        using var transport = new ScriptedCommTransport();
        var respond = transport.OnWrite;
        transport.OnWrite = bytes =>
        {
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            if (command.Name == "getsha256digest")
            {
                var start = checked((int)command.Attribute("start_sector")! * 512);
                var count = checked((int)command.Attribute("num_partition_sectors")! * 512);
                var digest = SHA256.HashData(disk[start..(start + count)]);
                return [Encoding.UTF8.GetBytes(
                    $"<data><log value='Digest {Convert.ToHexString(digest).ToLowerInvariant()}'/><response value='ACK' rawmode='false'/></data>")];
            }
            return respond(bytes);
        };
        using var protocol = Protocol(transport);

        var verified = await protocol.TryVerifyProgramWithTargetDigestOwnedAsync(source, 40, 1, 0,
            TestContext.Current.CancellationToken);

        Assert.True(verified);
    }

    private static FirehoseProtocol Protocol(ScriptedCommTransport transport) => new(transport,
        new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false, MaxPayloadSizeToTarget = 65536 });

    private static XElement[] Commands(ScriptedCommTransport transport) => transport.Writes
        .Where(bytes => bytes[0] == '<')
        .Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single()).ToArray();

    private static ScriptedCommTransport Transport(byte[] disk, int sectorSize)
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            if (command.Name == "getstorageinfo")
            {
                return [Encoding.UTF8.GetBytes($$$"""<data><log value='{"storage_info":{"block_size":{{{sectorSize}}},"total_blocks":128}}'/><response value='ACK'/></data>""")];
            }
            if (command.Name == "read")
            {
                var offset = checked((int)command.Attribute("start_sector")! * sectorSize);
                var count = checked((int)command.Attribute("num_partition_sectors")! * sectorSize);
                return
                [
                    Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>"),
                    disk.AsSpan(offset, count).ToArray(),
                    Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")
                ];
            }
            return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
        };
        return transport;
    }

    private static ScriptedCommTransport DigestProgramTransport(byte[] disk, bool rejectDigest, bool mismatchDigest,
        bool corruptWrite = false, bool omitDigest = false)
    {
        var transport = Transport(disk, 512);
        var respond = transport.OnWrite!;
        var remaining = 0;
        var position = 0;
        transport.OnWrite = bytes =>
        {
            if (remaining > 0)
            {
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
                return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
            }
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            if (command.Name == "program")
            {
                position = checked((int)command.Attribute("start_sector")! * 512);
                remaining = checked((int)command.Attribute("num_partition_sectors")! * 512);
                return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>")];
            }
            if (command.Name == "getsha256digest")
            {
                if (rejectDigest)
                {
                    return [Encoding.UTF8.GetBytes("<data><response value='NAK' rawmode='false'/></data>")];
                }
                if (omitDigest)
                {
                    return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='false'/></data>")];
                }
                var start = checked((int)command.Attribute("start_sector")! * 512);
                var count = checked((int)command.Attribute("num_partition_sectors")! * 512);
                var digest = SHA256.HashData(disk[start..(start + count)]);
                if (mismatchDigest)
                {
                    digest[0] ^= 1;
                }
                return [Encoding.UTF8.GetBytes(
                    $"<data><log value='Digest {Convert.ToHexString(digest).ToLowerInvariant()}'/><response value='ACK' rawmode='false'/></data>")];
            }
            return respond(bytes);
        };
        return transport;
    }
}
