using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Firmware.VendorFirmware.MediaTek;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class MtkFullDumpServiceTests
{
    private const int XFlashReadPacketBytes = 8192;
    private const int XFlashFrameBytes = 512;
    private const int XmlFrameBytes = 4096;
    private const int LegacyPacketBytes = 512;

    [Fact]
    public async Task CaptureAsync_XmlUserArea_ReadsReportedCapacityAndPublishesByteExactImage()
    {
        const long capacity = 65536;
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlSessionAsync(transport, capacity);
        var expected = Pattern(capacity);
        EnqueueXmlRead(transport, expected);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "user-area.img");
        var progress = new List<FlashProgress>();

        var record = await MtkFullDumpService.CaptureAsync(session, MtkDaProtocol.Xml, destination,
            new ListProgress(progress), TestContext.Current.CancellationToken);

        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.Equal(MtkDaProtocol.Xml, record.Family);
        Assert.Equal("EMMC-USER", record.RegionName);
        Assert.Equal(capacity, record.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), record.Sha256Hex);
        Assert.Equal(1024 * 1024, record.ChunkBytes);
        Assert.True(record.CapacityUsedAsReported);
        Assert.Equal(100, progress[^1].Percent);
        var commands = DecodeFrames(transport.Writes.Skip(before))
            .Select(frame => Encoding.UTF8.GetString(frame).TrimEnd('\0'))
            .Where(text => text.Contains("CMD:READ-FLASH", StringComparison.Ordinal))
            .Select(text => XDocument.Parse(text).Root!)
            .ToArray();
        var read = Assert.Single(commands);
        Assert.Equal("EMMC-USER", read.Element("arg")!.Element("partition")!.Value);
        Assert.Equal("0x0", read.Element("arg")!.Element("offset")!.Value);
        Assert.Equal("0x10000", read.Element("arg")!.Element("length")!.Value);
    }

    [Fact]
    public async Task CaptureAsync_XFlashUserArea_SplitsUnalignedCapacityAtTheReportedPacketBound()
    {
        const long capacity = 10240;
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport, MtkXFlashStorageKind.Emmc, capacity);
        var expected = Pattern(capacity);
        EnqueueXFlashRead(transport, expected[..XFlashReadPacketBytes]);
        EnqueueXFlashRead(transport, expected[XFlashReadPacketBytes..]);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "ufs-lua2.img");

        var record = await MtkFullDumpService.CaptureAsync(session, MtkDaProtocol.XFlash, destination,
            null, TestContext.Current.CancellationToken);

        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.Equal("EMMC-USER", record.RegionName);
        Assert.Equal(capacity, record.ByteLength);
        Assert.Equal(XFlashReadPacketBytes, record.ChunkBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), record.Sha256Hex);
        var requests = transport.Writes.Skip(before).Where(bytes => bytes.Length == 56).ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal([0L, XFlashReadPacketBytes],
            requests.Select(bytes => (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8))).ToArray());
        Assert.Equal([XFlashReadPacketBytes, 2048L],
            requests.Select(bytes => (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16))).ToArray());
        Assert.All(requests, bytes => Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4))));
    }

    [Fact]
    public async Task CaptureAsync_LegacyUserArea_ReadsReportedCapacityAndPublishesByteExactImage()
    {
        const long capacity = 8192;
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacySessionAsync(transport, capacity);
        var expected = Pattern(capacity);
        EnqueueLegacyRead(transport, expected);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "legacy-user.img");

        var record = await MtkFullDumpService.CaptureAsync(session, MtkDaProtocol.Legacy, destination,
            null, TestContext.Current.CancellationToken);

        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.Equal(MtkDaProtocol.Legacy, record.Family);
        Assert.Equal("EMMC-USER", record.RegionName);
        Assert.Equal(capacity, record.ByteLength);
        Assert.Equal(1024 * 1024, record.ChunkBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), record.Sha256Hex);
        var header = Assert.Single(transport.Writes.Skip(before), bytes => bytes.Length == 23);
        Assert.Equal(0xD6, header[0]);
        Assert.Equal(0L, (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(3)));
        Assert.Equal(capacity, (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(11)));
        Assert.Contains(transport.Writes.Skip(before), bytes => bytes.Length == 1 && bytes[0] == 8);
    }

    [Fact]
    public async Task CaptureAsync_XmlUserRegionWithoutCapacity_RefusesBeforeDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlSessionAsync(transport, userCapacity: 0);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MtkFullDumpService.CaptureAsync(
            session, MtkDaProtocol.Xml, Path.Combine(directory.Path, "user-area.img"), null,
            TestContext.Current.CancellationToken));

        Assert.Contains("reports 0 bytes for the EMMC-USER user area", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CaptureAsync_XFlashUserRegionWithoutCapacity_RefusesBeforeDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport, MtkXFlashStorageKind.Ufs, userCapacity: 0);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MtkFullDumpService.CaptureAsync(
            session, MtkDaProtocol.XFlash, Path.Combine(directory.Path, "ufs-lua2.img"), null,
            TestContext.Current.CancellationToken));

        Assert.Contains("reports 0 bytes for the UFS-LUA2 user area", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CaptureAsync_XFlashNandGeometry_RefusesBeforeDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport, MtkXFlashStorageKind.Nand,
            userCapacity: 786432);
        Assert.Equal(MtkXFlashStorageKind.Nand, session.XFlashStorage!.Kind);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkFullDumpService.CaptureAsync(
            session, MtkDaProtocol.XFlash, Path.Combine(directory.Path, "nand.img"), null,
            TestContext.Current.CancellationToken));

        Assert.Contains("cannot read a contiguous user-area image", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CaptureAsync_MissingDestinationDirectory_RefusesBeforeDeviceIo()
    {
        const long capacity = 10240;
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport, MtkXFlashStorageKind.Emmc, capacity);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();

        var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => MtkFullDumpService.CaptureAsync(
            session, MtkDaProtocol.XFlash,
            Path.Combine(directory.Path, "absent", "user-area.img"), null,
            TestContext.Current.CancellationToken));

        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task CaptureAsync_CancellationRequestedBeforeRead_RefusesBeforeDeviceIo()
    {
        const long capacity = 10240;
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport, MtkXFlashStorageKind.Emmc, capacity);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkFullDumpService.CaptureAsync(
            session, MtkDaProtocol.XFlash, Path.Combine(directory.Path, "user-area.img"), null, cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CaptureAsync_UnknownAgentFamily_RefusesBeforeDeviceIo()
    {
        const long capacity = 10240;
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport, MtkXFlashStorageKind.Emmc, capacity);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MtkFullDumpService.CaptureAsync(
            session, (MtkDaProtocol)99, Path.Combine(directory.Path, "user-area.img"), null,
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task CaptureAsync_CancellationBetweenChunks_PublishesNothingAndLeavesNoStagingFile()
    {
        const long capacity = 10240;
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport, MtkXFlashStorageKind.Emmc, capacity);
        var expected = Pattern(capacity);
        EnqueueXFlashRead(transport, expected[..XFlashReadPacketBytes]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "user-area.img");
        var progress = new CancellingProgress(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkFullDumpService.CaptureAsync(
            session, MtkDaProtocol.XFlash, destination, progress, cancellation.Token));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task CaptureAsync_ReadRejectedMidTransfer_KeepsExistingDestinationAndLeavesNoStagingFile()
    {
        const long capacity = 10240;
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport, MtkXFlashStorageKind.Emmc, capacity);
        var expected = Pattern(capacity);
        EnqueueXFlashRead(transport, expected[..XFlashReadPacketBytes]);
        for (var index = 0; index < 3; index++)
        {
            EnqueueFrame(transport, new byte[4]);
        }

        var failure = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(failure, 0xC0000001);
        EnqueueFrame(transport, failure);
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "user-area.img");
        var existing = "PRE-EXISTING IMAGE"u8.ToArray();
        File.WriteAllBytes(destination, existing);

        await Assert.ThrowsAsync<MtkProtocolException>(() => MtkFullDumpService.CaptureAsync(
            session, MtkDaProtocol.XFlash, destination, null, TestContext.Current.CancellationToken));

        Assert.Equal(existing, File.ReadAllBytes(destination));
        Assert.Single(Directory.GetFileSystemEntries(directory.Path));
    }

    private static async Task<MtkSession> CreateXmlSessionAsync(ScriptedCommTransport transport, long userCapacity)
    {
        EnqueueIdentity(transport);
        var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue(Convert.FromHexString("D7002000000000000800000000000000000000"));
        using (var agent = new MemoryStream(new byte[8]))
        {
            await session.UploadDownloadAgentAsync(agent, 0x200000, 8,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        transport.Enqueue(Convert.FromHexString("D5002000000000"));
        await session.StartDownloadAgentAsync(TestContext.Current.CancellationToken);
        EnqueueXmlText(transport, XmlControl("START"));
        await session.AttachXmlAgentAsync(TestContext.Current.CancellationToken);
        EnqueueXmlCompletion(transport);
        await session.ConfigureXmlRuntimeAsync(new MtkXmlRuntimeOptions
        {
            Checksum = MtkXmlChecksumLevel.UsbAndStorage,
            HostOs = MtkXmlHostOs.Windows,
            InitializeDram = false
        }, cancellationToken: TestContext.Current.CancellationToken);
        EnqueueXmlCompletion(transport);
        EnqueueXmlCompletion(transport);
        await session.InitializeXmlHardwareAsync(cancellationToken: TestContext.Current.CancellationToken);
        EnqueueXmlDownload(transport, 8, 8);
        using (var stage = new MemoryStream(new byte[8]))
        {
            await session.UploadXmlStageTwoAsync(stage, 0x400000, 8,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        EnqueueXmlCompletion(transport);
        EnqueueXmlCompletion(transport);
        await session.InitializeXmlStageTwoHardwareAsync(cancellationToken: TestContext.Current.CancellationToken);
        EnqueueXmlHardwareInfo(transport, userCapacity);
        await session.DiscoverXmlStorageAsync(cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }

    private static async Task<MtkSession> CreateXFlashSessionAsync(ScriptedCommTransport transport,
        MtkXFlashStorageKind kind, long userCapacity)
    {
        using var directory = new TempDirectory();
        var loaderPath = directory.WriteBytes("loader.bin", MtkSessionTests.BootstrapContainer());
        var profile = await MtkLoaderProfile.FromFileAsync(loaderPath, TestContext.Current.CancellationToken);
        var geometry = kind == MtkXFlashStorageKind.Nand
            ? NandGeometry(userCapacity)
            : MtkSessionTests.StorageGeometry(kind);
        if (kind != MtkXFlashStorageKind.Nand)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                geometry.AsSpan(kind == MtkXFlashStorageKind.Ufs ? 24 : 64), (ulong)userCapacity);
        }

        MtkSessionTests.EnqueueBootstrap(transport, bootRom: false, geometry: geometry);
        return await MtkBootstrap.MatchAsync(new MtkSession(transport), MtkDaProtocol.XFlash,
            new MtkAutoStartup { Profile = profile, StorageKind = kind }, TestContext.Current.CancellationToken);
    }

    private static async Task<MtkSession> CreateLegacySessionAsync(ScriptedCommTransport transport,
        long userCapacity)
    {
        using var directory = new TempDirectory();
        var loaderPath = directory.WriteBytes("loader.bin", MtkSessionTests.BootstrapContainer());
        EnqueueLegacyBootstrap(transport, LegacyInventory(userCapacity));
        return await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy, new MtkBootstrapOptions
        {
            LoaderPath = loaderPath,
            EntryIndex = 0,
            StageOneRegionIndex = 0,
            StageTwoRegionIndex = 1,
            StageTwoContent = MtkDaRegionContent.Complete,
            StorageKind = MtkXFlashStorageKind.Emmc,
            Legacy = new MtkLegacyOptions
            {
                StageOneResponseHex = "C10000",
                HostOs = MtkXmlHostOs.Linux,
                AgentPacketLength = 4,
                StoragePacketLength = LegacyPacketBytes
            }
        }, TestContext.Current.CancellationToken);
    }

    private static void EnqueueIdentity(ScriptedCommTransport transport) => transport.Enqueue(
    [
        0x5F, 0xF5, 0xAF, 0xFA,
        0xFD, 0x07, 0x66, 0, 0,
        0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
        0xD8, 0, 0, 0, 0x17, 0, 0
    ]);

    private static void EnqueueFrame(ScriptedCommTransport transport, byte[] payload, uint type = 1)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xFEEEEEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)payload.Length);
        transport.Enqueue([.. header, .. payload]);
    }

    private static List<byte[]> DecodeFrames(IEnumerable<byte[]> writes)
    {
        var wire = writes.SelectMany(value => value).ToArray();
        var frames = new List<byte[]>();
        for (var offset = 0; offset < wire.Length;)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(offset + 8));
            frames.Add(wire.AsSpan(offset + 12, length).ToArray());
            offset += length + 12;
        }

        return frames;
    }

    private static void EnqueueXFlashRead(ScriptedCommTransport transport, byte[] bytes)
    {
        for (var index = 0; index < 3; index++)
        {
            EnqueueFrame(transport, new byte[4]);
        }

        for (var offset = 0; offset < bytes.Length; offset += XFlashFrameBytes)
        {
            EnqueueFrame(transport, bytes.AsSpan(offset, Math.Min(XFlashFrameBytes, bytes.Length - offset)).ToArray());
        }

        EnqueueFrame(transport, new byte[4]);
    }

    private static void EnqueueLegacyRead(ScriptedCommTransport transport, byte[] bytes)
    {
        transport.Enqueue(Convert.FromHexString("5A015A5A5A"));
        for (var offset = 0; offset < bytes.Length; offset += LegacyPacketBytes)
        {
            var packet = bytes.AsSpan(offset, Math.Min(LegacyPacketBytes, bytes.Length - offset)).ToArray();
            var checksum = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(checksum, PacketSum(packet));
            transport.Enqueue(packet);
            transport.Enqueue(checksum);
        }
    }

    private static ushort PacketSum(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        foreach (var value in bytes)
        {
            sum += value;
        }

        return (ushort)(sum & 0xFFFF);
    }

    private static byte[] LegacyInventory(long userCapacity)
    {
        var bytes = new byte[92];
        for (var index = 0; index < 8; index++)
        {
            var capacity = index == 7 ? (ulong)userCapacity : (ulong)(index + 1) * 512;
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(4 + index * 8), capacity);
        }

        for (var index = 0; index < 16; index++)
        {
            bytes[68 + index] = (byte)index;
        }

        return bytes;
    }

    private static void EnqueueLegacyBootstrap(ScriptedCommTransport transport, byte[] emmcInventory)
    {
        EnqueueIdentity(transport);
        transport.Enqueue([0xFE, 0x05]);
        transport.Enqueue(Convert.FromHexString("D7002000000000000800000002000008000000D5002000000000"));
        transport.Enqueue([0xC0]);
        transport.Enqueue(Convert.FromHexString("00000BC4"));
        transport.Enqueue([0, 1]);
        transport.Enqueue([0xAA, 0xBB]);
        transport.Enqueue(new byte[20]);
        transport.Enqueue([0xC1, 0, 0]);
        transport.Enqueue(new byte[4]);
        for (var index = 0; index < 6; index++)
        {
            transport.Enqueue([0x5A]);
        }

        transport.Enqueue(new byte[28]);
        var nand = new byte[13];
        nand[^1] = 1;
        transport.Enqueue(nand);
        transport.Enqueue([0x11, 0x22]);
        transport.Enqueue(new byte[9]);
        transport.Enqueue(emmcInventory);
        transport.Enqueue(new byte[28]);
        transport.Enqueue(new byte[38]);
        var completion = new byte[10];
        completion[0] = 0x5A;
        completion[9] = 0xC1;
        transport.Enqueue(completion);
    }

    private static byte[] NandGeometry(long available)
    {
        var bytes = new byte[45];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 131072);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 64);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 1048576);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), (ulong)available);
        bytes[32] = 1;
        for (var index = 0; index < 12; index++)
        {
            bytes[33 + index] = (byte)(index + 1);
        }

        return bytes;
    }

    private static void EnqueueXmlText(ScriptedCommTransport transport, string text) =>
        EnqueueFrame(transport, [.. Encoding.UTF8.GetBytes(text), 0]);

    private static string XmlControl(string command, string? result = null) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + new XElement("host", new XElement("version", "1.0"),
                new XElement("command", "CMD:" + command), result is null ? null : new XElement("result", result))
            .ToString(SaveOptions.DisableFormatting);

    private static void EnqueueXmlCompletion(ScriptedCommTransport transport)
    {
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }

    private static void EnqueueXmlDownload(ScriptedCommTransport transport, int length, int packetLength)
    {
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, new XElement("host", new XElement("version", "1.0"),
            new XElement("command", "CMD:DOWNLOAD-FILE"), new XElement("arg",
                new XElement("checksum", "CHK_NO"), new XElement("info", "2nd-DA"),
                new XElement("source_file", $"MEM://0x0:0x{length:x}"),
                new XElement("packet_length", $"0x{packetLength:x}"))).ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, "OK");
        for (var offset = 0; offset < length; offset += packetLength)
        {
            EnqueueXmlText(transport, "OK");
            EnqueueXmlText(transport, "OK");
        }

        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }

    private static void EnqueueXmlHardwareInfo(ScriptedCommTransport transport, long userCapacity)
    {
        var document = new XElement("da_hw_info", new XElement("version", "1.2"),
            new XElement("ram_size", "0x100000000"), new XElement("battery_voltage", "3800"),
            new XElement("storage", "EMMC"),
            new XElement("emmc", new XElement("block_size", "0x200"),
                new XElement("boot1_size", "0x4000"), new XElement("boot2_size", "0x8000"),
                new XElement("rpmb_size", "0x2000"), new XElement("user_size", $"0x{userCapacity:x}"),
                new XElement("gp1_size", "0"), new XElement("gp2_size", "0"), new XElement("gp3_size", "0"),
                new XElement("gp4_size", "0"), new XElement("id", "0011aabb")),
            new XElement("product_id", ""));
        EnqueueXmlDocument(transport, XmlUploadRequest("hardware-info", "MEM://0x0:0x10000", 17),
            Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting) + "\0"));
    }

    private static void EnqueueXmlRead(ScriptedCommTransport transport, byte[] bytes) =>
        EnqueueXmlReply(transport, XmlUploadRequest("read-flash", "ROM_0", XmlFrameBytes), bytes);

    private static XElement XmlUploadRequest(string info, string target, int packetLength) => new("host",
        new XElement("version", "1.0"), new XElement("command", "CMD:UPLOAD-FILE"), new XElement("arg",
            new XElement("checksum", "CHK_NO"), new XElement("info", info),
            new XElement("target_file", target),
            new XElement("packet_length", $"0x{packetLength:x}")));

    private static void EnqueueXmlDocument(ScriptedCommTransport transport, XElement request, byte[] bytes)
    {
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, request.ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, $"OK@0x{bytes.Length:x}");
        EnqueueXmlText(transport, "OK");
        for (var offset = 0; offset < bytes.Length; offset += 17)
        {
            EnqueueFrame(transport, bytes.AsSpan(offset, Math.Min(17, bytes.Length - offset)).ToArray());
        }

        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }

    private static void EnqueueXmlReply(ScriptedCommTransport transport, XElement request, byte[] bytes)
    {
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, request.ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, $"OK@0x{bytes.Length:x}");
        EnqueueXmlText(transport, "OK");
        for (var offset = 0; offset < bytes.Length; offset += XmlFrameBytes)
        {
            var count = Math.Min(XmlFrameBytes, bytes.Length - offset);
            EnqueueFrame(transport, bytes.AsSpan(offset, count).ToArray());
            EnqueueXmlText(transport, offset + count == bytes.Length ? XmlControl("END", "OK") : "OK");
        }

        EnqueueXmlText(transport, XmlControl("START"));
    }

    private static byte[] Pattern(long length)
    {
        var bytes = new byte[(int)length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index * 31 + index / 512 + 7);
        }

        return bytes;
    }

    private sealed class ListProgress(List<FlashProgress> reports) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => reports.Add(value);
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value)
        {
            if (value.Percent is > 0 and < 100)
            {
                cancellation.Cancel();
            }
        }
    }
}