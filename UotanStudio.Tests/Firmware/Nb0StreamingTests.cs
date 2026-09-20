using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Extraction;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Models;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Packing;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Parsing;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// NB0 streaming regressions: chunked FileStream pack/extract, incremental MD5,
/// and truncated-archive rejection via range checks.
/// <para>NB0 流式回归：分块 FileStream 打包/解包、增量 MD5 与经范围校验的截断拒绝。</para>
/// </summary>
public class Nb0StreamingTests
{
    [Fact]
    public void PackThenExtract_RoundTripsFileEntries()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"nb0_stream_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var srcDir = Path.Combine(tmp, "src");
            Directory.CreateDirectory(srcDir);
            var contentA = BuildBytes(200_000, 3);
            var contentB = BuildBytes(50_000, 7);
            var fileA = Path.Combine(srcDir, "a.bin");
            var fileB = Path.Combine(srcDir, "b.bin");
            File.WriteAllBytes(fileA, contentA);
            File.WriteAllBytes(fileB, contentB);

            var nb0 = Path.Combine(tmp, "firmware.nb0");
            var builder = Nb0Packer.CreateBuilder();
            builder.AddFile(fileA, "a.bin");
            builder.AddFile(fileB, "b.bin");
            builder.Build(nb0);

            var outDir = Path.Combine(tmp, "out");
            var result = new Nb0Extractor().Extract(nb0, outDir);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(2, result.ExtractedFileCount);
            Assert.Equal(contentA, File.ReadAllBytes(Path.Combine(outDir, "a.bin")));
            Assert.Equal(contentB, File.ReadAllBytes(Path.Combine(outDir, "b.bin")));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ExtractEntryToFile_ComputesIncrementalMd5()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"nb0_md5_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var src = Path.Combine(tmp, "payload.bin");
            var content = BuildBytes(300_000, 5);
            File.WriteAllBytes(src, content);

            var nb0 = Path.Combine(tmp, "single.nb0");
            var builder = Nb0Packer.CreateBuilder();
            builder.AddFile(src, "payload.bin");
            builder.Build(nb0);

            using var stream = File.OpenRead(nb0);
            var metadata = Nb0Parser.ParseFromStream(stream);
            var entry = Assert.Single(metadata.Entries);

            var outPath = Path.Combine(tmp, "extracted.bin");
            var md5 = new Nb0Extractor().ExtractEntryToFile(stream, entry, outPath);

            Assert.Equal(MD5.HashData(content), md5);
            Assert.Equal(content, File.ReadAllBytes(outPath));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void PackWithMd5Records_ExtractVerifiesIncrementalMd5()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"nb0_md5rec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var contentA = BuildBytes(4096, 1);
            var contentB = BuildBytes(8192, 2);

            var entryCount = 3; // a.bin, b.bin, checksums.md5
            var dataSectionOffset = 4L + (entryCount * Nb0EntryHeader.StructSize);

            var md5A = MD5.HashData(contentA);
            var md5B = MD5.HashData(contentB);

            var md5Entry = BuildMd5Entry(
                dataSectionOffset,
                (0, (uint)contentA.Length, md5A),
                (contentA.Length, (uint)contentB.Length, md5B));

            var builder = Nb0Packer.CreateBuilder();
            builder.AddEntry("a.bin", contentA);
            builder.AddEntry("b.bin", contentB);
            builder.AddEntry("checksums.md5", md5Entry);

            var nb0 = Path.Combine(tmp, "verified.nb0");
            builder.Build(nb0);

            // 解析元数据应能读取 MD5 记录。
            // Parsing the metadata must surface the MD5 records.
            using var parseStream = File.OpenRead(nb0);
            var metadata = Nb0Parser.ParseFromStream(parseStream);
            Assert.True(metadata.HasMd5Records);
            Assert.Equal(2, metadata.Md5Records.Count);

            // 提取时增量计算每个条目的 MD5 并与记录比对，不应出现不匹配警告。
            // Extraction computes each entry's MD5 incrementally and compares it with the
            // records; no mismatch warnings are expected.
            var outDir = Path.Combine(tmp, "out");
            var result = new Nb0Extractor().Extract(nb0, outDir);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.DoesNotContain(result.Warnings, w => w.Contains("MD5 mismatch", StringComparison.Ordinal));
            Assert.Equal(contentA, File.ReadAllBytes(Path.Combine(outDir, "a.bin")));
            Assert.Equal(contentB, File.ReadAllBytes(Path.Combine(outDir, "b.bin")));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Extract_TruncatedData_ReportsRangeError()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"nb0_trunc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var contentA = BuildBytes(4096, 4);
            var contentB = BuildBytes(8192, 6);

            var builder = Nb0Packer.CreateBuilder();
            builder.AddEntry("a.bin", contentA);
            builder.AddEntry("b.bin", contentB);

            var nb0 = Path.Combine(tmp, "full.nb0");
            builder.Build(nb0);

            // 截断到 b.bin 数据中途：b.bin 的范围校验必须拒绝。
            // Truncate into the middle of b.bin's data: the range check must reject it.
            using (var fs = File.OpenWrite(nb0))
            {
                fs.SetLength(4L + (2 * Nb0EntryHeader.StructSize) + contentA.Length + 100);
            }

            var outDir = Path.Combine(tmp, "out");
            var result = new Nb0Extractor().Extract(nb0, outDir);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("b.bin", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Extract_TruncatedHeader_Throws()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"nb0_trunchdr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var builder = Nb0Packer.CreateBuilder();
            builder.AddEntry("a.bin", BuildBytes(1024, 8));
            var nb0 = Path.Combine(tmp, "full.nb0");
            builder.Build(nb0);

            using (var fs = File.OpenWrite(nb0))
            {
                fs.SetLength(64); // 不足以容纳声明条目头部
            }

            var outDir = Path.Combine(tmp, "out");
            Assert.Throws<Nb0CorruptedException>(() => new Nb0Extractor().Extract(nb0, outDir));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    private static byte[] BuildMd5Entry(long dataSectionOffset, params (long Offset, uint Length, byte[] Md5)[] records)
    {
        var data = new byte[records.Length * Nb0Md5Record.StructSize];
        var offset = 0;
        foreach (var (relativeOffset, length, md5) in records)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), checked((uint)(dataSectionOffset + relativeOffset)));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4, 4), length);
            md5.CopyTo(data, offset + 8);
            offset += Nb0Md5Record.StructSize;
        }
        return data;
    }

    private static byte[] BuildBytes(int length, int factor)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * factor) & 0xFF);
        }
        return data;
    }
}