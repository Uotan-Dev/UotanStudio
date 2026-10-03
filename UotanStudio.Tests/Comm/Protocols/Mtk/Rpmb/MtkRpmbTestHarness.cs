using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk.Rpmb;

internal static class MtkRpmbTestHarness
{
    internal const int FrameLength = 256;
    internal const int LegacyBlockSize = 512;
    internal const long DefaultEmmcRpmbBytes = 1536;
    internal const long DefaultLegacyRpmbBytes = 3072;

    internal static byte[] RpmbFrame(byte fill) => Enumerable.Repeat(fill, FrameLength).ToArray();

    internal static void EnqueueXFlashFrame(ScriptedCommTransport transport, byte[] payload, uint type = 1)
    {
        var header = Convert.FromHexString("EFEEEEFE0100000000000000");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)payload.Length);
        transport.Enqueue([.. header, .. payload]);
    }

    internal static void EnqueueXFlashValue(ScriptedCommTransport transport, uint value)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        EnqueueXFlashFrame(transport, payload);
    }

    internal static void EnqueueXFlashResult(ScriptedCommTransport transport, ushort code)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, code);
        EnqueueXFlashFrame(transport, payload);
    }

    internal static void EnqueueControlReply(ScriptedCommTransport transport, byte[] payload)
    {
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashFrame(transport, payload);
        EnqueueXFlashValue(transport, 0);
    }

    internal static void EnqueueRpmbReadReply(ScriptedCommTransport transport, byte[] frame, ushort code = 0)
    {
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashResult(transport, code);
        if (code != 0)
        {
            EnqueueXFlashValue(transport, 0);
            return;
        }
        EnqueueXFlashFrame(transport, frame);
        EnqueueXFlashValue(transport, 0);
    }

    internal static byte[] XFlashGeometry(MtkXFlashStorageKind kind, long rpmbBytes)
    {
        var emmc = kind == MtkXFlashStorageKind.Emmc;
        var geometry = new byte[emmc ? 96 : 48];
        BinaryPrimitives.WriteUInt32LittleEndian(geometry, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), LegacyBlockSize);
        var count = emmc ? 8 : 3;
        for (var index = 0; index < count; index++)
        {
            var size = emmc && index == 2 ? rpmbBytes : (long)(index + 1) * LegacyBlockSize;
            BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8 + index * 8), (ulong)size);
        }
        return geometry;
    }

    internal static async Task<MtkSession> CreateXFlashRpmbSessionAsync(ScriptedCommTransport transport,
        MtkXFlashStorageKind kind = MtkXFlashStorageKind.Emmc, long rpmbBytes = DefaultEmmcRpmbBytes)
    {
        var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, XFlashGeometry(kind, rpmbBytes));
        EnqueueControlReply(transport, Convert.FromHexString("0002000000020000"));
        await session.DiscoverXFlashStorageAsync(kind, TestContext.Current.CancellationToken);
        return session;
    }

    internal static async Task<MtkSession> CreateStageTwoSessionAsync(ScriptedCommTransport transport)
    {
        var session = await CreateStageOneSessionAsync(transport);
        EnqueueControlReply(transport, "preloader"u8.ToArray());
        await session.GetXFlashConnectionAgentAsync(TestContext.Current.CancellationToken);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        using var image = new MemoryStream(new byte[8]);
        await session.UploadXFlashStageTwoAsync(image, 0x40000000, 8, cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }

    internal static async Task<MtkSession> CreateStageOneSessionAsync(ScriptedCommTransport transport)
    {
        var session = await CreateStartedSessionAsync(transport);
        transport.Enqueue([0xC0]);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashFrame(transport, new byte[2]);
        EnqueueXFlashFrame(transport, Convert.FromHexString("53594E43"));
        await session.InitializeXFlashAsync(cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }

    internal static async Task<MtkSession> CreateStartedSessionAsync(ScriptedCommTransport transport)
    {
        EnqueueIdentity(transport);
        var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        transport.Enqueue(Convert.FromHexString("D7002000000000000800000000000000000000"));
        using var image = new MemoryStream(new byte[8]);
        await session.UploadDownloadAgentAsync(image, 0x200000, 8, cancellationToken: TestContext.Current.CancellationToken);
        transport.Enqueue(Convert.FromHexString("D5002000000000"));
        await session.StartDownloadAgentAsync(TestContext.Current.CancellationToken);
        return session;
    }

    internal static void EnqueueIdentity(ScriptedCommTransport transport) => transport.Enqueue([
        0x5F, 0xF5, 0xAF, 0xFA,
        0xFD, 0x07, 0x66, 0, 0,
        0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
        0xD8, 0, 0, 0, 0x17, 0, 0]);

    internal static byte[] LegacyEmmcGeometry(long rpmbBytes)
    {
        var bytes = new byte[92];
        for (var index = 0; index < 8; index++)
        {
            var size = index == 2 ? (ulong)rpmbBytes : (ulong)(index + 1) * 1024;
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(4 + index * 8), size);
        }
        for (var index = 0; index < 16; index++)
        {
            bytes[68 + index] = (byte)index;
        }
        return bytes;
    }

    internal static async Task<MtkSession> CreateLegacyRpmbSessionAsync(ScriptedCommTransport transport,
        long rpmbBytes = DefaultLegacyRpmbBytes)
    {
        var session = await CreateStartedBootloaderSessionAsync(transport);
        transport.Enqueue([0xFE, 0x05]);
        await session.QueryLegacyBootVersionsAsync(TestContext.Current.CancellationToken);
        transport.Enqueue(Convert.FromHexString("D7002000000000000800000000000000000000"));
        using (var daOne = new MemoryStream(new byte[8]))
        {
            await session.UploadDownloadAgentAsync(daOne, 0x200000, 8, cancellationToken: TestContext.Current.CancellationToken);
        }
        transport.Enqueue(Convert.FromHexString("D5002000000000"));
        await session.StartDownloadAgentAsync(TestContext.Current.CancellationToken);
        transport.Enqueue([0xC0]);
        transport.Enqueue(new byte[4]);
        transport.Enqueue([0, 1]);
        transport.Enqueue([0xAA, 0xBB]);
        transport.Enqueue(new byte[20]);
        transport.Enqueue([0xC1, 0, 0]);
        transport.Enqueue(new byte[4]);
        for (var index = 0; index < 4; index++)
        {
            transport.Enqueue([0x5A]);
        }
        transport.Enqueue(new byte[28]);
        var nand = new byte[13];
        nand[^1] = 1;
        transport.Enqueue(nand);
        transport.Enqueue([0xAA, 0xBB]);
        transport.Enqueue(new byte[9]);
        transport.Enqueue(LegacyEmmcGeometry(rpmbBytes));
        transport.Enqueue(new byte[28]);
        transport.Enqueue(new byte[38]);
        var completion = new byte[10];
        completion[0] = 0x5A;
        completion[9] = 0xC1;
        transport.Enqueue(completion);
        using var stageTwo = new MemoryStream(new byte[8]);
        await session.InitializeLegacyAgentAsync(stageTwo, 0x200000, 8, LegacyProfile(),
            cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }

    internal static MtkLegacyOptions LegacyProfile() => new()
    {
        StageOneResponseHex = "C10000",
        NandSize64Bit = false,
        HostOs = MtkXmlHostOs.Linux,
        AgentPacketLength = 4,
        StoragePacketLength = LegacyBlockSize
    };

    internal static async Task<MtkSession> CreateStartedBootloaderSessionAsync(ScriptedCommTransport transport)
    {
        EnqueueIdentity(transport);
        var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        return session;
    }

    internal static void EnqueueLegacyRpmbRead(ScriptedCommTransport transport, byte[] data,
        int packetLength = LegacyBlockSize, bool corruptChecksum = false)
    {
        transport.Enqueue([0x5A]);
        transport.Enqueue([0x02]);
        transport.Enqueue([0x5A]);
        transport.Enqueue([0x5A]);
        transport.Enqueue([0x5A]);
        for (var offset = 0; offset < data.Length; offset += packetLength)
        {
            var chunk = data.AsSpan(offset, Math.Min(packetLength, data.Length - offset)).ToArray();
            transport.Enqueue(chunk);
            var sum = (ushort)(chunk.Aggregate(0, (total, value) => total + value) & 0xFFFF);
            var checksum = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(checksum, corruptChecksum ? (ushort)(sum ^ 0x01) : sum);
            transport.Enqueue(checksum);
        }
    }

    internal static string XmlControl(string command, string? result = null) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + new XElement("host", new XElement("version", "1.0"),
            new XElement("command", "CMD:" + command), result is null ? null : new XElement("result", result))
        .ToString(SaveOptions.DisableFormatting);

    internal static void EnqueueXmlText(ScriptedCommTransport transport, string text) =>
        EnqueueXFlashFrame(transport, [.. Encoding.UTF8.GetBytes(text), 0]);

    internal static void EnqueueXmlCompletion(ScriptedCommTransport transport)
    {
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }

    internal static XElement XmlRuntimeRequest() => new("host", new XElement("version", "1.0"),
        new XElement("command", "CMD:DOWNLOAD-FILE"), new XElement("arg",
            new XElement("checksum", "CHK_NO"), new XElement("info", "1st-DA"),
            new XElement("source_file", "MEM://0x0:0x8"), new XElement("packet_length", "0x4")));

    internal static XElement XmlUploadRequest(int length, int packetLength) => new("host",
        new XElement("version", "1.0"), new XElement("command", "CMD:DOWNLOAD-FILE"),
        new XElement("arg", new XElement("checksum", "CHK_NO"), new XElement("info", "2nd-DA"),
            new XElement("source_file", $"MEM://0x0:0x{length:x}"), new XElement("packet_length", $"0x{packetLength:x}")));

    internal static void EnqueueXmlUpload(ScriptedCommTransport transport, int length, int packetLength)
    {
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlUploadRequest(length, packetLength).ToString(SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, "OK");
        for (var offset = 0; offset < length; offset += packetLength)
        {
            EnqueueXmlText(transport, "OK");
            EnqueueXmlText(transport, "OK");
        }
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }

    internal static XElement XmlHardwareDocument(bool ufs, long rpmbBytes = 8192)
    {
        string[] names = ufs
            ? ["lua0_size", "lua1_size", "lua2_size", "lua3_size"]
            : ["boot1_size", "boot2_size", "rpmb_size", "user_size", "gp1_size", "gp2_size", "gp3_size", "gp4_size"];
        var sizes = ufs
            ? new[] { "0x4000", "0x8000", "0x10000", $"0x{rpmbBytes:x}" }
            : new[] { "0x4000", "0x8000", $"0x{rpmbBytes:x}", "0x10000", "0", "0", "0", "0" };
        return new XElement("da_hw_info", new XElement("version", "1.2"),
            new XElement("ram_size", "0x100000000"), new XElement("battery_voltage", "3800"),
            new XElement("storage", ufs ? "UFS" : "EMMC"),
            new XElement(ufs ? "ufs" : "emmc", new XElement("block_size", "0x200"),
                names.Select((name, index) => new XElement(name, sizes[index])), new XElement("id", "0011aabb")),
            new XElement("product_id", ""));
    }

    internal static XElement XmlStorageRequest() => new("host", new XElement("version", "1.0"),
        new XElement("command", "CMD:UPLOAD-FILE"), new XElement("arg",
            new XElement("checksum", "CHK_NO"), new XElement("info", "hardware-info"),
            new XElement("target_file", "MEM://0x0:0x10000"), new XElement("packet_length", "0x11")));

    internal static void EnqueueXmlStorage(ScriptedCommTransport transport, XElement document)
    {
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlStorageRequest().ToString(SaveOptions.DisableFormatting));
        var bytes = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting) + "\0");
        EnqueueXmlText(transport, $"OK@0x{bytes.Length:x}");
        EnqueueXmlText(transport, "OK");
        for (var offset = 0; offset < bytes.Length; offset += 17)
        {
            EnqueueXFlashFrame(transport, bytes.AsSpan(offset, Math.Min(17, bytes.Length - offset)).ToArray());
        }
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }

    internal static async Task<MtkSession> CreateXmlRpmbSessionAsync(ScriptedCommTransport transport, bool ufs = false,
        long rpmbBytes = 8192)
    {
        var session = await CreateXmlControlSessionAsync(transport);
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
        using var source = new MemoryStream(new byte[8]);
        EnqueueXmlUpload(transport, 8, 8);
        await session.UploadXmlStageTwoAsync(source, 0x400000, 8, cancellationToken: TestContext.Current.CancellationToken);
        EnqueueXmlCompletion(transport);
        EnqueueXmlCompletion(transport);
        await session.InitializeXmlStageTwoHardwareAsync(cancellationToken: TestContext.Current.CancellationToken);
        EnqueueXmlStorage(transport, XmlHardwareDocument(ufs, rpmbBytes));
        await session.DiscoverXmlStorageAsync(cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }

    internal static async Task<MtkSession> CreateXmlControlSessionAsync(ScriptedCommTransport transport)
    {
        var session = await CreateStartedSessionAsync(transport);
        EnqueueXmlText(transport, XmlControl("START"));
        await session.AttachXmlAgentAsync(TestContext.Current.CancellationToken);
        return session;
    }

    internal static void EnqueueXmlRpmbReadReply(ScriptedCommTransport transport, byte[] frame, ushort code = 0)
    {
        EnqueueXmlText(transport, "OK");
        EnqueueXFlashResult(transport, code);
        if (code != 0)
        {
            EnqueueXmlText(transport, XmlControl("END", "OK"));
            EnqueueXmlText(transport, XmlControl("START"));
            return;
        }
        EnqueueXFlashFrame(transport, frame);
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }

    internal static void EnqueueXmlRpmbWriteReply(ScriptedCommTransport transport, ushort code = 0)
    {
        EnqueueXmlText(transport, "OK");
        EnqueueXFlashResult(transport, code);
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }

    internal static byte[] Header(int payloadLength)
    {
        var header = Convert.FromHexString("EFEEEEFE0100000000000000");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)payloadLength);
        return header;
    }

    internal static byte[] Frame(byte[] payload) => [.. Header(payload.Length), .. payload];

    internal static byte[] Word(uint value)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        return Frame(payload);
    }

    internal static byte[] StatusFrame => Frame(new byte[4]);

    internal static byte[] Flatten(ScriptedCommTransport transport, int from)
    {
        var bytes = new List<byte>();
        foreach (var write in transport.Writes.Skip(from))
        {
            bytes.AddRange(write);
        }
        return [.. bytes];
    }
}