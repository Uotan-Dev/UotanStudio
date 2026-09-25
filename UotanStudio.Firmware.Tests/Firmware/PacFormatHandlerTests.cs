using System.Text;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Pac.Extraction;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Pac.Models;
using UotanStudio.Firmware.SharedKernel;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Format-level tests for the SPRD/UNISOC PAC handler, built around a minimal
/// synthetic PAC image (head + directory table + entry data + CRC-16 checksums).
/// <para>SPRD/UNISOC PAC 处理器的格式级测试，基于测试内合成的最小合法 PAC 镜像
/// （头部 + 目录表 + 条目数据 + CRC-16 校验和）。</para>
/// </summary>
public class PacFormatHandlerTests
{
    private const string PacFormatName = "SPRD/UNISOC PAC Firmware";

    /// <summary>
    /// Builds a minimal but fully valid PAC image: SprdHead (2124 bytes, magic 0xFFFAFFFA at
    /// Size-4, HeadCrc over the first 2120 bytes), one 2580-byte SprdFile entry per file,
    /// entry payloads, and DataCrc over everything after the header.
    /// <para>合成最小合法 PAC 镜像：SprdHead（2124 字节，魔数 0xFFFAFFFA 位于 Size-4，
    /// HeadCrc 覆盖前 2120 字节）、每个文件一个 2580 字节的 SprdFile 条目、条目数据，
    /// DataCrc 覆盖头部之后的全部内容。</para>
    /// </summary>
    internal static byte[] BuildPac(params (string Name, byte[] Data)[] entries)
    {
        var dirSize = entries.Length * SprdFile.StructFixedSize;
        var dataOffsets = new uint[entries.Length];
        uint offset = (uint)(SprdHead.Size + dirSize);
        for (var i = 0; i < entries.Length; i++)
        {
            dataOffsets[i] = offset;
            offset += (uint)entries[i].Data.Length;
        }

        var head = new SprdHead
        {
            PacSize = offset,
            FileCount = (uint)entries.Length,
            DirOffset = SprdHead.Size,
            PacMagic = 0xFFFAFFFA,
        };
        WriteUtf16(head.FwName, "SPRDTest");
        WriteUtf16(head.FwVersion, "V1.0");

        var headBytes = SerializeHead(head, headCrc: 0, dataCrc: 0);
        headBytes = PatchU16(headBytes, SprdHead.Size - 4, Crc16.Calculate(0, headBytes[..(SprdHead.Size - 4)]));

        using var bodyMs = new MemoryStream();
        for (var i = 0; i < entries.Length; i++)
        {
            var file = new SprdFile
            {
                StructSize = SprdFile.StructFixedSize,
                Size = (uint)entries[i].Data.Length,
                Type = 1,
                PacOffset = dataOffsets[i],
            };
            WriteUtf16(file.Name, entries[i].Name);
            SerializeEntry(bodyMs, file);
        }
        for (var i = 0; i < entries.Length; i++)
        {
            bodyMs.Write(entries[i].Data);
        }

        headBytes = PatchU16(headBytes, SprdHead.Size - 2, Crc16.Calculate(0, bodyMs.ToArray()));

        using var ms = new MemoryStream();
        ms.Write(headBytes);
        ms.Write(bodyMs.ToArray());
        return ms.ToArray();
    }

    private static byte[] SerializeHead(SprdHead head, ushort headCrc, ushort dataCrc)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        foreach (var v in head.PacVersion)
        {
            writer.Write(v);
        }
        writer.Write(head.PacSize);
        foreach (var v in head.FwName)
        {
            writer.Write(v);
        }
        foreach (var v in head.FwVersion)
        {
            writer.Write(v);
        }
        writer.Write(head.FileCount);
        writer.Write(head.DirOffset);
        foreach (var v in head.Unknown1)
        {
            writer.Write(v);
        }
        foreach (var v in head.FwAlias)
        {
            writer.Write(v);
        }
        foreach (var v in head.Unknown2)
        {
            writer.Write(v);
        }
        foreach (var v in head.Unknown)
        {
            writer.Write(v);
        }
        writer.Write(head.PacMagic);
        writer.Write(headCrc);
        writer.Write(dataCrc);
        Assert.Equal(SprdHead.Size, ms.Length);
        return ms.ToArray();
    }

    private static void SerializeEntry(MemoryStream ms, SprdFile file)
    {
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(file.StructSize);
        foreach (var v in file.Id)
        {
            writer.Write(v);
        }
        foreach (var v in file.Name)
        {
            writer.Write(v);
        }
        foreach (var v in file.Unknown1)
        {
            writer.Write(v);
        }
        writer.Write(file.Size);
        writer.Write(file.Type);
        writer.Write(file.FlashUse);
        writer.Write(file.PacOffset);
        writer.Write(file.OmitFlag);
        writer.Write(file.AddrNum);
        foreach (var v in file.Addr)
        {
            writer.Write(v);
        }
        foreach (var v in file.Unknown2)
        {
            writer.Write(v);
        }
    }

    private static byte[] PatchU16(byte[] bytes, int offset, ushort value)
    {
        var patched = bytes.Clone() as byte[] ?? throw new InvalidOperationException();
        patched[offset] = (byte)value;
        patched[offset + 1] = (byte)(value >> 8);
        return patched;
    }

    private static void WriteUtf16(ushort[] target, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        Assert.True(bytes.Length <= target.Length * 2, $"name too long: {value}");
        for (var i = 0; i < target.Length; i++)
        {
            target[i] = i * 2 + 1 < bytes.Length ? BitConverter.ToUInt16(bytes, i * 2) : (ushort)0;
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-pac-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Detect_PacExtension_IsPac()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.pac");
        try
        {
            File.WriteAllBytes(path, BuildPac(("nvram.bin", [1, 2, 3])));
            var handler = FormatDetector.Detect(path);
            Assert.Equal(PacFormatName, handler?.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detect_NoExtension_ContentMagic_IsPac()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware");
        try
        {
            File.WriteAllBytes(path, BuildPac(("nvram.bin", [1, 2, 3])));
            var handler = FormatDetector.Detect(path);
            Assert.Equal(PacFormatName, handler?.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Metadata_ReportsEntryNamesAndCount()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.pac");
        try
        {
            File.WriteAllBytes(path, BuildPac(
                ("nvram.bin", [0xAA, 0xBB, 0xCC]),
                ("boot.img", Enumerable.Range(0, 4096).Select(i => (byte)(i & 0xFF)).ToArray())));

            var handler = FormatDetector.Detect(path);
            Assert.NotNull(handler);
            var result = await handler.ReadMetadataAsync(path, TestContext.Current.CancellationToken);
            Assert.True(result.Success);
            Assert.Contains("2 files", result.Summary, StringComparison.Ordinal);
            Assert.Equal("2", result.Metadata?["FileCount"]);
            Assert.Contains("nvram.bin", result.Entries);
            Assert.Contains("boot.img", result.Entries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unpack_ExtractsEntriesByteExact()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.pac");
        var outDir = Path.Combine(root, "out");
        try
        {
            var bootData = Enumerable.Range(0, 8192).Select(i => (byte)(i * 7)).ToArray();
            File.WriteAllBytes(path, BuildPac(("nvram.bin", [0x01, 0x02]), ("boot.img", bootData)));

            var handler = FormatDetector.Detect(path);
            Assert.NotNull(handler);
            var result = await handler.UnpackAsync(path, outDir, null, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Summary);

            Assert.Equal([0x01, 0x02], await File.ReadAllBytesAsync(Path.Combine(outDir, "nvram.bin"), TestContext.Current.CancellationToken));
            Assert.Equal(bootData, await File.ReadAllBytesAsync(Path.Combine(outDir, "boot.img"), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unpack_SelectedEntries_ExtractsOnlyExactNames()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.pac");
        var outDir = Path.Combine(root, "out");
        try
        {
            File.WriteAllBytes(path, BuildPac(("boot.img", [1, 2, 3]), ("vendor.img", [4, 5, 6])));
            var handler = FormatDetector.Detect(path);
            Assert.NotNull(handler);

            var result = await handler.UnpackAsync(path, outDir,
                new UnpackOptions { SelectedEntries = ["VENDOR.IMG"] }, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Summary);
            Assert.False(File.Exists(Path.Combine(outDir, "boot.img")));
            Assert.Equal([4, 5, 6],
                await File.ReadAllBytesAsync(Path.Combine(outDir, "vendor.img"), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unpack_ChecksumVerificationFails_DoesNotPublishEntries()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.pac");
        var outDir = Path.Combine(root, "out");
        try
        {
            var bytes = BuildPac(("boot.img", [1, 2, 3]));
            bytes[SprdHead.Size - 1] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            var handler = FormatDetector.Detect(path);
            Assert.NotNull(handler);

            var result = await handler.UnpackAsync(path, outDir,
                new UnpackOptions { VerifyChecksums = true }, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Contains("checksum", result.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(outDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Check_Crc16ChecksumsMatch()
    {
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.pac");
        try
        {
            File.WriteAllBytes(path, BuildPac(("nvram.bin", [0x11, 0x22, 0x33])));
            var (headOk, dataOk) = PacExtractor.Check(path, TestContext.Current.CancellationToken);
            Assert.True(headOk);
            Assert.True(dataOk);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Check_CorruptDataCrc_IsReported()
    {
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.pac");
        try
        {
            var bytes = BuildPac(("nvram.bin", [0x11, 0x22, 0x33]));
            bytes[SprdHead.Size - 1] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            var (_, dataOk) = PacExtractor.Check(path, TestContext.Current.CancellationToken);
            Assert.False(dataOk);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unpack_TruncatedImage_FailsCleanly()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware.pac");
        var outDir = Path.Combine(root, "out");
        try
        {
            var bytes = BuildPac(("nvram.bin", [0x11, 0x22, 0x33]));
            File.WriteAllBytes(path, bytes[..(bytes.Length - 1)]);

            var handler = FormatDetector.Detect(path);
            Assert.NotNull(handler);
            var result = await handler.UnpackAsync(path, outDir, null, TestContext.Current.CancellationToken);
            Assert.False(result.Success);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detect_BadMagic_IsNotPac()
    {
        FirmwareFormatRegistry.RegisterAll();
        var root = CreateTempDir();
        var path = Path.Combine(root, "firmware");
        try
        {
            var bytes = BuildPac(("nvram.bin", [0x11]));
            bytes[SprdHead.Size - 8] = 0x00;
            File.WriteAllBytes(path, bytes);
            var handler = FormatDetector.Detect(path);
            Assert.NotEqual(PacFormatName, handler?.FormatName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}