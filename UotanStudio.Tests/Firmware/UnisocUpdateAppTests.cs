using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.VendorFirmware.UpdateApp;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class UnisocUpdateAppTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-updateapp-unisoc-" + Guid.NewGuid().ToString("N"));

    public UnisocUpdateAppTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void DetectFlavor_LegacyShortHeaders_ReturnsUnisocVariant()
    {
        var path = Write("unisoc.app", [.. BuildLegacyEntry("modem", [1, 2, 3, 4]), .. BuildLegacyEntry("nv", [5, 6])]);

        Assert.Equal(UpdateAppFlavor.UnisocLegacy, UpdateAppProcessor.DetectFlavor(path));
    }

    [Fact]
    public void DetectFlavor_CanonicalCrcHeaders_ReturnsHuaweiVariant()
    {
        var path = Write("huawei.app", [.. BuildCanonicalEntry("boot", [1, 2, 3, 4, 5, 6, 7, 8], 4)]);

        Assert.Equal(UpdateAppFlavor.HuaweiVerified, UpdateAppProcessor.DetectFlavor(path));
    }

    [Fact]
    public void DetectFlavor_MixedHeaderGenerations_ReturnsMixedVariant()
    {
        var path = Write("mixed.app",
            [.. BuildLegacyEntry("modem", [1, 2, 3, 4]), .. BuildCanonicalEntry("boot", [1, 2, 3, 4], 4)]);

        Assert.Equal(UpdateAppFlavor.Mixed, UpdateAppProcessor.DetectFlavor(path));
    }

    [Fact]
    public void DetectFlavor_ContainerWithoutNamedEntries_ReturnsNotApplicable()
    {
        var path = Write("blank.app", new byte[64]);

        Assert.Equal(UpdateAppFlavor.NotApplicable, UpdateAppProcessor.DetectFlavor(path));
    }

    [Fact]
    public async Task Handler_ReadMetadata_LegacyContainer_ReportsUnisocFlavor()
    {
        var path = Write("flavor-metadata.app", [.. BuildLegacyEntry("dsp", [7, 7, 7, 7])]);

        var result = await new UpdateAppFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("UnisocLegacy", result.Metadata["Flavor"]);
        Assert.Equal(["dsp"], result.Entries);
    }

    [Fact]
    public void LegacyFramedContainer_ExtractsPayloadsWithoutCrcVerification()
    {
        var payload = Encoding.ASCII.GetBytes("encrypted-ish blob");
        var path = Write("legacy-extract.app", [.. BuildLegacyEntry("system", payload)]);
        var output = Path.Combine(_root, "legacy-out");

        Assert.Equal(1, UpdateAppProcessor.Extract(path, output, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(output, "system.img")));
    }

    [Fact]
    public void Handler_CanHandle_LegacyAndCanonicalContainers_ShareOneFraming()
    {
        var legacy = Write("legacy-claim.app", [.. BuildLegacyEntry("modem", [1, 2, 3, 4])]);
        var canonical = Write("canonical-claim.app", [.. BuildCanonicalEntry("boot", [1, 2, 3, 4], 4)]);
        var handler = new UpdateAppFormatHandler();

        Assert.True(handler.CanHandle(legacy));
        Assert.True(handler.CanHandle(canonical));
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] BuildLegacyEntry(string name, byte[] payload)
    {
        var bytes = new byte[76 + payload.Length];
        UpdateAppProcessor.Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 76);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)payload.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(bytes, 60);
        payload.CopyTo(bytes, 76);
        var padding = (4 - bytes.Length % 4) % 4;
        return [.. bytes, .. new byte[padding]];
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
        byte[] bytes = [.. header, .. payload];
        var padding = (4 - bytes.Length % 4) % 4;
        return [.. bytes, .. new byte[padding]];
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