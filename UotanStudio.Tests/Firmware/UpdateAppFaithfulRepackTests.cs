using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.VendorFirmware.UpdateApp;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class UpdateAppFaithfulRepackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-updateapp-faithful-" + Guid.NewGuid().ToString("N"));

    public UpdateAppFaithfulRepackTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task FaithfulRepack_UnpackThenPack_ProducesByteIdenticalContainer()
    {
        var source = Write("update.app", BuildMixedContainer());
        var extracted = Path.Combine(_root, "extract");

        var handler = new UpdateAppFormatHandler();
        var unpack = await handler.UnpackAsync(source, extracted, null, TestContext.Current.CancellationToken);
        Assert.True(unpack.Success, unpack.Summary);
        var packed = Path.Combine(_root, "repacked.app");
        var repack = await handler.PackAsync(extracted, packed, TestContext.Current.CancellationToken);
        Assert.True(repack.Success, repack.Summary);

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(packed));
    }

    [Fact]
    public async Task FaithfulRepack_TwoGenerations_PreserveEntryMetadataAndPayloadBytes()
    {
        var source = Write("two-generation.app", BuildMixedContainer());
        var first = Path.Combine(_root, "gen1");
        var second = Path.Combine(_root, "gen2");
        var handler = new UpdateAppFormatHandler();

        Assert.True((await handler.UnpackAsync(source, first, null, TestContext.Current.CancellationToken)).Success);
        var packed = Path.Combine(_root, "generation2.app");
        Assert.True((await handler.PackAsync(first, packed, TestContext.Current.CancellationToken)).Success);
        Assert.True((await handler.UnpackAsync(packed, second, null, TestContext.Current.CancellationToken)).Success);

        var original = UpdateAppProcessor.GetEntries(source).ToList();
        var regenerated = UpdateAppProcessor.GetEntries(packed).ToList();
        Assert.Equal(original.Select(entry => entry.Name), regenerated.Select(entry => entry.Name));
        for (var index = 0; index < original.Count; index++)
        {
            Assert.Equal(original[index].HeaderOffset, regenerated[index].HeaderOffset);
            Assert.Equal(original[index].HeaderSize, regenerated[index].HeaderSize);
            Assert.Equal(original[index].FileSize, regenerated[index].FileSize);
            Assert.Equal(original[index].ChecksumTable, regenerated[index].ChecksumTable);
        }

        Assert.Equal(File.ReadAllBytes(Path.Combine(first, "boot.img")), File.ReadAllBytes(Path.Combine(second, "boot.img")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(first, "vendor.img")), File.ReadAllBytes(Path.Combine(second, "vendor.img")));
        var metadata = RepackMetadata.Read(first);
        Assert.NotNull(metadata);
        Assert.Equal("Mixed", metadata!.SourceProperties["Flavor"]);
    }

    [Fact]
    public async Task FaithfulRepack_PayloadSizeChanged_PackFails()
    {
        var source = Write("size-drift.app", BuildMixedContainer());
        var extracted = Path.Combine(_root, "size-drift");
        var handler = new UpdateAppFormatHandler();
        Assert.True((await handler.UnpackAsync(source, extracted, null, TestContext.Current.CancellationToken)).Success);
        await File.AppendAllTextAsync(Path.Combine(extracted, "boot.img"), "x", TestContext.Current.CancellationToken);

        var result = await handler.PackAsync(extracted, Path.Combine(_root, "drift-out.app"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task FaithfulRepack_PayloadCrcAnomaly_PackFails()
    {
        var source = Write("crc-anomaly.app", BuildMixedContainer());
        var extracted = Path.Combine(_root, "crc-anomaly");
        var handler = new UpdateAppFormatHandler();
        Assert.True((await handler.UnpackAsync(source, extracted, null, TestContext.Current.CancellationToken)).Success);
        var payloadPath = Path.Combine(extracted, "vendor.img");
        var payload = File.ReadAllBytes(payloadPath);
        payload[3] ^= 0xFF;
        await File.WriteAllBytesAsync(payloadPath, payload, TestContext.Current.CancellationToken);

        var result = await handler.PackAsync(extracted, Path.Combine(_root, "crc-out.app"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("CRC", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unpack_CorruptedPayloadChecksum_FailsWithoutMetadata()
    {
        var vendor = BuildCanonicalEntry("vendor", "payload1"u8.ToArray(), 4);
        vendor[^2] ^= 0x01;
        var bytes = new List<byte>();
        bytes.AddRange(vendor);
        bytes.AddRange([0, 0, 0, 0]);
        var source = Write("bad-payload-crc.app", [.. bytes]);
        var output = Path.Combine(_root, "bad-payload");

        var result = await new UpdateAppFormatHandler().UnpackAsync(source, output, null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("CRC", result.Summary, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, RepackMetadata.FileName)));
    }

    [Fact]
    public async Task Unpack_CorruptedHeaderChecksum_FailsBeforeExtraction()
    {
        var vendor = BuildCanonicalEntry("vendor", "payload!"u8.ToArray(), 4);
        vendor[40] ^= 0xFF;
        var bytes = new List<byte>();
        bytes.AddRange(vendor);
        bytes.AddRange([0, 0, 0, 0]);
        var source = Write("bad-header-crc.app", [.. bytes]);

        var result = await new UpdateAppFormatHandler().UnpackAsync(source, Path.Combine(_root, "bad-header"),
            null, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("header CRC", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pack_WithoutRepackMetadata_BuildsCanonicalContainer()
    {
        var input = Path.Combine(_root, "flat");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "alpha.img"), "alpha-payload",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input, "beta.img"), "beta-payload",
            TestContext.Current.CancellationToken);
        var package = Path.Combine(_root, "canonical.app");

        var result = await new UpdateAppFormatHandler().PackAsync(input, package, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(["alpha", "beta"], UpdateAppProcessor.GetEntries(package).Select(entry => entry.Name));
        var output = Path.Combine(_root, "canonical-out");
        Assert.Equal(2, UpdateAppProcessor.Extract(package, output, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("alpha-payload", await File.ReadAllTextAsync(Path.Combine(output, "alpha.img"),
            TestContext.Current.CancellationToken));
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] BuildMixedContainer()
    {
        var bytes = new List<byte>();
        bytes.AddRange([0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04]);
        bytes.AddRange(BuildLegacyEntry("boot", [1, 2, 3, 4, 5, 6]));
        bytes.AddRange([0xAB, 0xCD]);
        bytes.AddRange(BuildCanonicalEntry("vendor", [10, 11, 12, 13, 14, 15, 16, 17], 4));
        bytes.AddRange([0xAB, 0xCD]);
        bytes.AddRange(BuildLegacyEntry(string.Empty, [9, 8, 7, 6]));
        bytes.AddRange([0x11, 0x22, 0x33, 0x44, 0x55]);
        return [.. bytes];
    }

    private static byte[] BuildLegacyEntry(string name, byte[] payload)
    {
        var bytes = new byte[76 + payload.Length];
        UpdateAppProcessor.Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 76);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)payload.Length);
        if (name.Length > 0)
            Encoding.ASCII.GetBytes(name).CopyTo(bytes, 60);
        payload.CopyTo(bytes, 76);
        return bytes;
    }

    private static byte[] BuildCanonicalEntry(string name, byte[] payload, ushort blockSize)
    {
        var blockCount = (payload.Length + blockSize - 1) / blockSize;
        var header = new byte[98 + blockCount * 2];
        UpdateAppProcessor.Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)header.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)payload.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(header, 60);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(94), blockSize);
        for (var block = 0; block < blockCount; block++)
        {
            var start = block * blockSize;
            var length = Math.Min(blockSize, payload.Length - start);
            BinaryPrimitives.WriteUInt16LittleEndian(
                header.AsSpan(98 + block * 2), ComputeCrc16(payload.AsSpan(start, length)));
        }

        var crc = ComputeCrc16(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(92), crc);
        return [.. header, .. payload];
    }

    private static ushort ComputeCrc16(ReadOnlySpan<byte> bytes)
    {
        var crc = (ushort)0xFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1);
        }

        return (ushort)(crc ^ 0xFFFF);
    }
}