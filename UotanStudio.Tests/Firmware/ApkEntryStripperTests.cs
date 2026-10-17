using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Apk;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// APK ZIP-layer rewrite tests over hand-built archives. <c>ApkEntryStripper</c> parses the central directory itself,
/// so these tests assemble local headers, central records and the EOCD directly to pin the two documented operations:
/// dropping stale v1 signature artifacts and appending new STORED entries, both while every other entry keeps its
/// bytes, its alignment padding contract and its central-record fields.
/// <para>在手工构造的归档上进行的 APK ZIP 层重写测试。<c>ApkEntryStripper</c> 自行解析中央目录，
/// 因此本测试直接组装本地文件头、中央记录与 EOCD，钉住两项已记录的操作：剔除旧 v1 签名工件与追加新的 STORED
/// 条目；两者都必须让其余条目保留其字节、对齐填充契约与中央记录字段。</para>
/// </summary>
public class ApkEntryStripperTests
{
    [Theory]
    [InlineData("META-INF/MANIFEST.MF", true)]
    [InlineData("META-INF/UOTAN.SF", true)]
    [InlineData("META-INF/UOTAN.RSA", true)]
    [InlineData("META-INF/UOTAN.DSA", true)]
    [InlineData("META-INF/UOTAN.EC", true)]
    [InlineData("meta-inf/manifest.mf", true)]
    [InlineData("META-INF/nested/UOTAN.SF", true)]
    [InlineData("META-INF/", false)]
    [InlineData("META-INF/services/javax.script.ScriptEngineFactory", false)]
    [InlineData("META-INF/UOTAN.DSA.bak", false)]
    [InlineData("META-INF/NOTES.TXT", false)]
    [InlineData("MANIFEST.MF", false)]
    [InlineData("classes.dex", false)]
    [InlineData("resources.arsc", false)]
    [InlineData("lib/arm64-v8a/libnative.so", false)]
    [InlineData("", false)]
    public void IsV1SignatureEntry_StoredEntryNames_MatchTheDocumentedRules(string entryName, bool expected)
        => Assert.Equal(expected, ApkEntryStripper.IsV1SignatureEntry(entryName));

    [Fact]
    public void Rewrite_MissingInput_ThrowsFileNotFoundAndCreatesNoOutput()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "out.apk");

        Assert.Throws<FileNotFoundException>(() => ApkEntryStripper.Rewrite(
            Path.Combine(temp.Path, "absent.apk"), output,
            new ApkEntryStripper.RewriteRequest(), TestContext.Current.CancellationToken));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Rewrite_EmptyRequest_ReproducesEveryEntryByteForByte()
    {
        var entries = new[]
        {
            ("classes.dex", Encoding.UTF8.GetBytes("dex-bytes")),
            ("META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes("Manifest-Version: 1.0\r\n\r\n")),
            ("resources.arsc", new byte[8192])
        };
        using var temp = new TempDirectory();
        var input = WriteApk(temp, "input.apk", entries);
        var output = Path.Combine(temp.Path, "output.apk");

        ApkEntryStripper.Rewrite(input, output, new ApkEntryStripper.RewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(
            entries.Select(entry => entry.Item1).ToArray(),
            ReadArchive(output));
        Assert.Equal(
            entries.ToDictionary(entry => entry.Item1, entry => entry.Item2),
            ReadArchiveContent(output));
    }

    [Fact]
    public void Rewrite_StripV1SignatureEntries_DropsOnlySignatureArtifacts()
    {
        var entries = new[]
        {
            ("classes.dex", Encoding.UTF8.GetBytes("dex-bytes")),
            ("META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes("Manifest-Version: 1.0\r\n\r\n")),
            ("META-INF/UOTAN.SF", Encoding.UTF8.GetBytes("Signature-Version: 1.0\r\n\r\n")),
            ("META-INF/UOTAN.RSA", new byte[] { 0x30, 0x82, 0x01, 0x22 }),
            ("META-INF/services/javax.script.ScriptEngineFactory", Encoding.UTF8.GetBytes("com.example.Engine")),
            ("lib/arm64-v8a/libnative.so", new byte[4096])
        };
        using var temp = new TempDirectory();
        var input = WriteApk(temp, "input.apk", entries);
        var output = Path.Combine(temp.Path, "output.apk");

        ApkEntryStripper.Rewrite(input, output,
            new ApkEntryStripper.RewriteRequest { StripV1SignatureEntries = true },
            TestContext.Current.CancellationToken);

        var rewritten = ReadArchive(output);
        Assert.Equal(
            ["classes.dex", "META-INF/services/javax.script.ScriptEngineFactory", "lib/arm64-v8a/libnative.so"],
            rewritten);
        Assert.Equal(Encoding.UTF8.GetBytes("dex-bytes"), ReadArchiveContent(output)["classes.dex"]);
        Assert.Equal(new byte[4096], ReadArchiveContent(output)["lib/arm64-v8a/libnative.so"]);
    }

    [Fact]
    public void Rewrite_StripV1SignatureEntries_LeavesExistingOutputUntouchedOnFailure()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "output.apk");
        File.WriteAllText(output, "preserve");

        Assert.Throws<FileNotFoundException>(() => ApkEntryStripper.Rewrite(
            Path.Combine(temp.Path, "absent.apk"), output,
            new ApkEntryStripper.RewriteRequest { StripV1SignatureEntries = true },
            TestContext.Current.CancellationToken));

        Assert.Equal("preserve", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void Rewrite_AppendedStoredEntries_AreReadableWithCorrectCrc()
    {
        using var temp = new TempDirectory();
        var input = WriteApk(temp, "input.apk", [("classes.dex", Encoding.UTF8.GetBytes("dex-bytes"))]);
        var output = Path.Combine(temp.Path, "nested", "output.apk");
        var appended = Encoding.UTF8.GetBytes("added-asset");

        ApkEntryStripper.Rewrite(input, output, new ApkEntryStripper.RewriteRequest
        {
            EntriesToAppend = [("assets/added.txt", appended), ("META-INF/EXTRA.SF", appended)]
        }, TestContext.Current.CancellationToken);

        var rewritten = ReadArchive(output);
        Assert.Equal(["classes.dex", "assets/added.txt", "META-INF/EXTRA.SF"], rewritten);
        var content = ReadArchiveContent(output);
        Assert.Equal(appended, content["assets/added.txt"]);
        Assert.Equal(appended, content["META-INF/EXTRA.SF"]);
        using var stream = File.OpenRead(output);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.All(archive.Entries, entry => Assert.Equal(Crc32Of(ReadEntry(entry)), entry.Crc32));
    }

    [Fact]
    public void Rewrite_AppendedEntryNameIsNotDeduplicatedAgainstExistingEntries()
    {
        using var temp = new TempDirectory();
        var input = WriteApk(temp, "input.apk", [("assets/value.txt", "original"u8.ToArray())]);
        var output = Path.Combine(temp.Path, "output.apk");

        ApkEntryStripper.Rewrite(input, output, new ApkEntryStripper.RewriteRequest
        {
            EntriesToAppend = [("assets/value.txt", "replacement"u8.ToArray())]
        }, TestContext.Current.CancellationToken);

        using var stream = File.OpenRead(output);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var duplicates = archive.Entries.Where(entry => entry.FullName == "assets/value.txt").ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.Equal("original"u8.ToArray(), ReadEntry(duplicates[0]));
        Assert.Equal("replacement"u8.ToArray(), ReadEntry(duplicates[1]));
    }

    [Fact]
    public void Rewrite_StrippingAndAppendingTogether_PutsAppendedEntriesLast()
    {
        using var temp = new TempDirectory();
        var input = WriteApk(temp, "input.apk",
        [
            ("classes.dex", "dex"u8.ToArray()),
            ("META-INF/MANIFEST.MF", "manifest"u8.ToArray()),
            ("resources.arsc", "arsc"u8.ToArray())
        ]);
        var output = Path.Combine(temp.Path, "output.apk");

        ApkEntryStripper.Rewrite(input, output, new ApkEntryStripper.RewriteRequest
        {
            StripV1SignatureEntries = true,
            EntriesToAppend = [("META-INF/UOTAN.SF", "new-signature"u8.ToArray())]
        }, TestContext.Current.CancellationToken);

        using var stream = File.OpenRead(output);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(
            ["classes.dex", "resources.arsc", "META-INF/UOTAN.SF"],
            archive.Entries.Select(entry => entry.FullName).ToArray());
        Assert.Equal("new-signature"u8.ToArray(), ReadEntry(archive.Entries[^1]));
    }

    [Fact]
    public void Rewrite_StoredNativeLibraryEntry_KeepsContentAndFourByteDataAlignment()
    {
        var native = new byte[64 * 1024];
        RandomNumberGenerator.Fill(native);
        using var temp = new TempDirectory();
        var input = WriteApk(temp, "input.apk",
        [
            ("assets/prefix.txt", "abcde"u8.ToArray()),
            ("lib/arm64-v8a/libnative.so", native)
        ]);
        var output = Path.Combine(temp.Path, "output.apk");

        ApkEntryStripper.Rewrite(input, output, new ApkEntryStripper.RewriteRequest
        {
            EntriesToAppend = [("assets/appended.txt", "appended"u8.ToArray())]
        }, TestContext.Current.CancellationToken);

        Assert.Equal(native, ReadArchiveContent(output)["lib/arm64-v8a/libnative.so"]);
        Assert.Equal(0, DataOffsetOf(output, "lib/arm64-v8a/libnative.so") % 4);
        Assert.Equal(0, DataOffsetOf(input, "lib/arm64-v8a/libnative.so") % 4);
    }

    [Fact]
    public void RewriteWithSigningBlock_MatchesRewriteThenManualBlockInsertion()
    {
        var entries = new[]
        {
            ("classes.dex", Encoding.UTF8.GetBytes("dex-bytes")),
            ("META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes("Manifest-Version: 1.0\r\n\r\n")),
            ("lib/arm64-v8a/libnative.so", new byte[4096])
        };
        using var temp = new TempDirectory();
        var input = WriteApk(temp, "input.apk", entries);
        var request = new ApkEntryStripper.RewriteRequest
        {
            StripV1SignatureEntries = true,
            EntriesToAppend = [("META-INF/UOTAN.SF", "new-signature"u8.ToArray())],
        };
        var stripped = Path.Combine(temp.Path, "stripped.apk");
        ApkEntryStripper.Rewrite(input, stripped, request, TestContext.Current.CancellationToken);

        byte[]? digest = null;
        var signed = Path.Combine(temp.Path, "signed.apk");
        ApkEntryStripper.RewriteWithSigningBlock(input, signed, request, "SHA256", contentDigest =>
        {
            digest = contentDigest;
            return ApkSigningBlock.Build([(0x12345678u, contentDigest)]);
        }, TestContext.Current.CancellationToken);

        using var stream = File.OpenRead(stripped);
        var sections = ApkZipSections.Locate(stream);
        var eocd = new byte[sections.EocdSize];
        stream.Seek(sections.EocdOffset, SeekOrigin.Begin);
        stream.ReadExactly(eocd);
        var expectedDigest = ApkContentDigest.ComputeApkDigest(
            stream, sections.CentralDirectoryOffset, sections.CentralDirectoryOffset,
            sections.CentralDirectoryOffset, sections.CentralDirectorySize, eocd, "SHA256",
            TestContext.Current.CancellationToken);
        Assert.NotNull(digest);
        Assert.Equal(expectedDigest, digest);

        var block = ApkSigningBlock.Build([(0x12345678u, expectedDigest)]);
        var patchedEocd = (byte[])eocd.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(patchedEocd.AsSpan(16, 4),
            (uint)(sections.CentralDirectoryOffset + block.Length));
        var strippedBytes = File.ReadAllBytes(stripped);
        var expected = new List<byte>();
        expected.AddRange(strippedBytes.AsSpan(0, (int)sections.CentralDirectoryOffset).ToArray());
        expected.AddRange(block);
        expected.AddRange(strippedBytes.AsSpan((int)sections.CentralDirectoryOffset, (int)sections.CentralDirectorySize).ToArray());
        expected.AddRange(patchedEocd);

        Assert.Equal(expected.ToArray(), File.ReadAllBytes(signed));
    }

    [Fact]
    public void Rewrite_CancelledToken_ThrowsBeforePublishingOutput()
    {
        using var temp = new TempDirectory();
        var input = WriteApk(temp, "input.apk", [("classes.dex", new byte[512 * 1024])]);
        var output = Path.Combine(temp.Path, "output.apk");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ApkEntryStripper.Rewrite(input, output,
            new ApkEntryStripper.RewriteRequest { EntriesToAppend = [("assets/added.txt", new byte[64])] },
            cancellation.Token));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void Rewrite_CorruptCentralDirectory_ThrowsInvalidData()
    {
        using var temp = new TempDirectory();
        var input = WriteApk(temp, "input.apk", [("classes.dex", "dex"u8.ToArray())]);
        var bytes = File.ReadAllBytes(input);
        var central = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 22 + 16, 4));
        bytes[central] ^= 0xFF;
        var corrupt = temp.WriteBytes("corrupt.apk", bytes);

        Assert.Throws<InvalidDataException>(() => ApkEntryStripper.Rewrite(corrupt,
            Path.Combine(temp.Path, "output.apk"), new ApkEntryStripper.RewriteRequest(), TestContext.Current.CancellationToken));
    }

    private static string WriteApk(TempDirectory temp, string fileName, IReadOnlyList<(string Name, byte[] Content)> entries)
    {
        var path = Path.Combine(temp.Path, fileName);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        var records = new List<byte[]>();
        foreach (var (name, content) in entries)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var crc = Crc32Of(content);
            var local = new byte[30];
            BinaryPrimitives.WriteUInt32LittleEndian(local, 0x04034b50);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(4), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(8), 0x0800);
            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(14), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(18), (uint)content.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(22), (uint)content.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(26), (ushort)nameBytes.Length);
            stream.Write(local);
            stream.Write(nameBytes);
            stream.Write(content);

            var record = new byte[46 + nameBytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (3 << 8) | 20);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), 0x0800);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), (uint)content.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)content.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42), (uint)(stream.Position - 30 - nameBytes.Length - content.Length));
            nameBytes.CopyTo(record, 46);
            records.Add(record);
        }

        var centralStart = stream.Position;
        var centralSize = 0L;
        foreach (var record in records)
        {
            stream.Write(record);
            centralSize += record.Length;
        }

        var eocd = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(8), (ushort)records.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(10), (ushort)records.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(12), (uint)centralSize);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(16), (uint)centralStart);
        stream.Write(eocd);
        return path;
    }

    private static long DataOffsetOf(string apkPath, string entryName)
    {
        var bytes = File.ReadAllBytes(apkPath);
        var central = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6, 4));
        var offset = (long)central;
        while (offset + 46 <= bytes.Length && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset)) == 0x02014b50)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)offset + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)offset + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)offset + 32));
            var name = Encoding.UTF8.GetString(bytes, (int)offset + 46, nameLength);
            if (string.Equals(name, entryName, StringComparison.Ordinal))
            {
                var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset + 42));
                Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)localOffset)));
                return localOffset + 30
                    + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)localOffset + 26))
                    + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)localOffset + 28));
            }

            offset += 46 + nameLength + extraLength + commentLength;
        }

        throw new InvalidDataException($"Central record for '{entryName}' was not found.");
    }

    private static IReadOnlyList<string> ReadArchive(string apkPath)
    {
        using var stream = File.OpenRead(apkPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return archive.Entries.Select(entry => entry.FullName).ToArray();
    }

    private static IReadOnlyDictionary<string, byte[]> ReadArchiveContent(string apkPath)
    {
        using var stream = File.OpenRead(apkPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(entry => entry.FullName, ReadEntry);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var content = entry.Open();
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static uint Crc32Of(byte[] content)
    {
        var crc = new System.IO.Hashing.Crc32();
        crc.Append(content);
        return BitConverter.ToUInt32(crc.GetCurrentHash());
    }
}