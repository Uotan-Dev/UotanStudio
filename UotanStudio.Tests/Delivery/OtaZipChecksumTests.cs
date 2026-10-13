using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using UotanStudio.Services.Delivery;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class OtaZipChecksumTests : IDisposable
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralDirectorySignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const string MetadataEntry = "META-INF/com/android/metadata";
    private const int ClassicFieldLimit = ushort.MaxValue;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ota-checksum-" + Guid.NewGuid().ToString("N"));

    public OtaZipChecksumTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class NoTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task MetadataEntryHeaderChecksums_MatchStoredEntryBytesAfterInPlacePropertyPatch()
    {
        var zipPath = await BuildOtaPackageAsync();
        var bytes = File.ReadAllBytes(zipPath);

        var eocd = FindEndOfCentralDirectory(bytes);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocd + 16));
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(eocd + 10));
        Assert.True(directoryOffset != uint.MaxValue && entryCount != ClassicFieldLimit,
            "this package is expected to use the classic end-of-central-directory record; ZIP64 layout is covered separately");

        var records = ReadCentralDirectoryRecords(bytes, (int)directoryOffset, entryCount);
        var metadata = records.Single(r => r.Name == MetadataEntry);
        Assert.Equal(LocalHeaderSignature, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(metadata.LocalHeaderOffset)));

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(metadata.LocalHeaderOffset + 26));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(metadata.LocalHeaderOffset + 28));
        var dataStart = metadata.LocalHeaderOffset + 30 + nameLength + extraLength;
        Assert.True(dataStart + metadata.UncompressedSize <= bytes.Length, "metadata entry extends past the archive");

        var expected = Crc32.HashToUInt32(bytes.AsSpan(dataStart, (int)metadata.UncompressedSize));

        Assert.Equal(expected, metadata.Crc32);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(metadata.LocalHeaderOffset + 14)));
    }

    [Fact]
    public async Task MetadataEntry_ReadsThroughValidatingZipArchiveAfterInPlacePropertyPatch()
    {
        var zipPath = await BuildOtaPackageAsync();

        using var archive = ZipFile.OpenRead(zipPath);
        using var stream = archive.GetEntry(MetadataEntry)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Contains("ota-streaming-property-files=payload.bin:", text);
    }

    [Fact]
    public void RepairFixesChecksumsInBothHeadersOfAZip64StoredEntry()
    {
        const uint StaleChecksum = 0xDEADBEEF;
        var path = Path.Combine(_root, "zip64-stored.zip");
        var archive = BuildZip64StoredArchive(StaleChecksum);
        File.WriteAllBytes(path, archive.Bytes);

        DeliveryPackageBuilder.RepairStoredEntryChecksum(path, MetadataEntry);

        var repaired = File.ReadAllBytes(path);
        var expected = Crc32.HashToUInt32(Encoding.UTF8.GetBytes("uotan"));
        Assert.NotEqual(StaleChecksum, expected);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(archive.LocalHeaderStart + 14)));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(archive.CentralRecordStart + 16)));
        Assert.Equal(archive.Bytes.Length, repaired.Length);
    }

    [Fact]
    public void RepairRejectsAnEntryNameThatIsAbsentFromAZip64Archive()
    {
        var path = Path.Combine(_root, "zip64-missing-entry.zip");
        File.WriteAllBytes(path, BuildZip64StoredArchive(0xDEADBEEF).Bytes);

        Assert.Throws<InvalidDataException>(() => DeliveryPackageBuilder.RepairStoredEntryChecksum(path, "payload.bin"));
    }

    /// <summary>
    /// A hand-authored ZIP64 stored-entry archive whose central directory size and local-offset fields hold
    /// placeholders with the real values only in the ZIP64 extra field, whose classic end record also carries
    /// placeholders so resolution must go through the ZIP64 end-of-central-directory record, and whose two CRC
    /// fields start at the given stale value.
    /// <para>手写的 ZIP64 stored 条目归档：中央目录记录的大小与本地头偏移都是占位值，真实值只存在于 ZIP64 扩展字段，
    /// 经典结束记录的大小/偏移/条目数同样置为占位值以迫使解析走 ZIP64 结束记录，两个头部的 CRC 先写成给定过期值。</para>
    /// </summary>
    private static (byte[] Bytes, int LocalHeaderStart, int CentralRecordStart) BuildZip64StoredArchive(uint staleChecksum)
    {
        var name = Encoding.UTF8.GetBytes(MetadataEntry);
        var data = Encoding.UTF8.GetBytes("uotan");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        var localHeaderStart = (int)stream.Position;
        writer.Write(LocalHeaderSignature); writer.Write((ushort)45); writer.Write((ushort)0);
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write(staleChecksum); writer.Write(uint.MaxValue); writer.Write(uint.MaxValue);
        writer.Write((ushort)name.Length); writer.Write((ushort)20);
        writer.Write(name);
        writer.Write((ushort)1); writer.Write((ushort)16);
        writer.Write((ulong)data.Length); writer.Write((ulong)data.Length);
        writer.Write(data);

        var centralRecordStart = (int)stream.Position;
        writer.Write(CentralDirectorySignature); writer.Write((ushort)45); writer.Write((ushort)45);
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write(staleChecksum); writer.Write(uint.MaxValue); writer.Write(uint.MaxValue);
        writer.Write((ushort)name.Length); writer.Write((ushort)28); writer.Write((ushort)0);
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((uint)0); writer.Write(uint.MaxValue);
        writer.Write(name);
        writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write((ulong)data.Length); writer.Write((ulong)data.Length); writer.Write((ulong)localHeaderStart);

        var centralSize = (int)stream.Position - centralRecordStart;

        var zip64RecordStart = (int)stream.Position;
        writer.Write(0x06064b50u); writer.Write(44UL); writer.Write((ushort)45); writer.Write((ushort)45);
        writer.Write(0u); writer.Write(0u); writer.Write(1UL); writer.Write(1UL);
        writer.Write((ulong)centralSize); writer.Write((ulong)centralRecordStart);

        writer.Write(0x07064b50u); writer.Write(0u); writer.Write((ulong)zip64RecordStart); writer.Write(1u);

        writer.Write(EndOfCentralDirectorySignature); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write((ushort)ClassicFieldLimit); writer.Write((ushort)ClassicFieldLimit);
        writer.Write(uint.MaxValue); writer.Write(uint.MaxValue); writer.Write((ushort)0);

        writer.Flush();
        return (stream.ToArray(), localHeaderStart, centralRecordStart);
    }

    private async Task<string> BuildOtaPackageAsync()
    {
        var source = Path.Combine(_root, "boot.img");
        File.WriteAllBytes(source, new byte[8192]);
        var plan = new DeliveryPlan
        {
            PackPayload = true,
            OrganizeDirectory = true,
            ContainerConfirmed = true,
            Txt = false,
            OutputDirectory = Path.Combine(_root, "output"),
            Container = [new() { SourcePath = source, FileName = "boot.img", Partition = "boot" }],
            Files = [new() { Generated = true, FileName = "ota.zip", Partition = "payload" }]
        };

        await new DeliveryOutputService(new NoTools()).ExportAsync(plan, token: TestContext.Current.CancellationToken);

        var zipPath = Path.Combine(plan.OutputDirectory, "ota.zip");
        Assert.True(File.Exists(zipPath));
        return zipPath;
    }

    private static int FindEndOfCentralDirectory(byte[] bytes)
    {
        var floor = Math.Max(0, bytes.Length - ClassicFieldLimit - 22);
        for (var position = bytes.Length - 22; position >= floor; position--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position)) == EndOfCentralDirectorySignature)
            {
                return position;
            }
        }

        throw new InvalidDataException("ZIP end-of-central-directory record not found.");
    }

    private static List<(string Name, uint Crc32, uint UncompressedSize, int LocalHeaderOffset)> ReadCentralDirectoryRecords(
        byte[] bytes, int start, ushort count)
    {
        var records = new List<(string, uint, uint, int)>(count);
        var position = start;
        for (var index = 0; index < count; index++)
        {
            Assert.Equal(CentralDirectorySignature, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position)));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 32));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 16));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 24));
            var localHeaderOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 42));
            var name = Encoding.UTF8.GetString(bytes, position + 46, nameLength);
            records.Add((name, crc, size, localHeaderOffset));
            position += 46 + nameLength + extraLength + commentLength;
        }

        return records;
    }
}