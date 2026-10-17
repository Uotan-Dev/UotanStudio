using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BootSecurity.Patcher;
using Xunit;
using KsudAsset = UotanStudio.Firmware.BootSecurity.Patcher.Asset;
using KsudExtractor = UotanStudio.Firmware.BootSecurity.Patcher.Extractor;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Characterization tests for the KernelSU asset extractor. The extractor restores <c>*_kernelsu.ko</c> payloads from
/// a KernelSU-patched kernel module and has no production consumer, so these tests pin the observable contract of each
/// shipped entry point over synthetic ELF64 images: name sanitization against traversal and illegal characters,
/// deflate probing including its cap and malformed-input rejection, the address-mapping based reference scans for both
/// supported machines, table-entry discovery and de-duplication, static-thunk discovery, and the asset shape reported
/// by <c>ExtractAssets</c> when no payload can be located.
/// <para>KernelSU 资源提取器的表征测试。该提取器从 KernelSU 打过补丁的内核模块中恢复 <c>*_kernelsu.ko</c> 数据，
/// 且在生产代码中没有消费者，因此本测试在合成 ELF64 镜像上钉住每个已发布入口的可观察契约：
/// 针对路径穿越与非法字符的名称净化、deflate 探测（含其上限与畸形输入拒绝）、基于地址映射的两种受支持机器
/// 引用扫描、表条目发现与去重、静态跳板发现，以及无法定位数据时 <c>ExtractAssets</c> 报告的资源形态。</para>
/// </summary>
public class KsudExtractorCharacterizationTests
{
    private const int ExecFileOffset = 0x200;
    private const int RodataFileOffset = 0x400;
    private const int WritableFileOffset = 0x600;
    private const int SegmentFileSize = 0x200;
    private const int ImageLength = 0x800;
    private const ulong ExecVa = 0x1000;
    private const ulong RodataVa = 0x2000;
    private const ulong WritableVa = 0x3000;

    private const ushort MachineAarch64 = 183;
    private const ushort MachineX86_64 = 62;

    private const ulong AdrpX0Page2 = 0xD0000000;
    private const ulong AddX0X0Immediate16 = 0x91004000;
    private const ulong AddX0X0Immediate16Shifted = 0x91404000;
    private const uint Ret = 0xD65F03C0;

    private const string AssetName = "android13-5.15_kernelsu.ko";

    [Theory]
    [InlineData("android13-5.15_kernelsu.ko", "android13-5.15_kernelsu.ko")]
    [InlineData("../../etc/passwd", ".._.._etc_passwd")]
    [InlineData("..\\..\\windows\\system32", ".._.._windows_system32")]
    [InlineData("/absolute/path.ko", "absolute_path.ko")]
    [InlineData("na me.ko", "na_me.ko")]
    [InlineData("na\u0000me.ko", "na_me.ko")]
    [InlineData("na:me*.ko", "na_me_.ko")]
    [InlineData("keep+these@chars%-.ko", "keep+these@chars%-.ko")]
    [InlineData("///multi///slash", "multi___slash")]
    [InlineData("", "")]
    public void SanitizeName_RawTableNames_FlattenToASingleFileName(string raw, string expected)
        => Assert.Equal(expected, KsudExtractor.SanitizeName(raw));

    [Fact]
    public void SanitizeName_PathTraversal_LeavesNoSeparatorAndNoRootedPath()
    {
        var sanitized = KsudExtractor.SanitizeName("..\\..\\..\\..\\boot.img");

        Assert.Equal(".._.._.._.._boot.img", sanitized);
        Assert.DoesNotContain('\\', sanitized);
        Assert.DoesNotContain('/', sanitized);
        Assert.False(Path.IsPathRooted(sanitized));
        Assert.Equal(sanitized, Path.GetFileName(sanitized));
        Assert.StartsWith(Path.GetFullPath("out") + Path.DirectorySeparatorChar,
            Path.GetFullPath(Path.Combine("out", sanitized)), StringComparison.Ordinal);
    }

    [Fact]
    public void TryDeflate_ValidRawDeflateStream_ReportsConsumedBytesAndOutput()
    {
        var payload = Encoding.UTF8.GetBytes(new string('K', 8192));
        var stream = Deflate(payload);
        var data = new byte[stream.Length + 64];
        stream.CopyTo(data, 16);

        var result = KsudExtractor.TryDeflate(data, 16);

        Assert.NotNull(result);
        Assert.Equal(payload, result!.Value.Output);
        Assert.True(result.Value.Consumed > 0);
        Assert.True(result.Value.Consumed <= stream.Length);
    }

    [Fact]
    public void TryDeflate_TrailingBytes_ReportsExactlyTheDeflateStreamLength()
    {
        var payload = Encoding.UTF8.GetBytes(new string('K', 8192));
        var stream = Deflate(payload);
        var data = new byte[stream.Length + 4096];
        stream.CopyTo(data, 0);
        data.AsSpan(stream.Length).Fill(0x5A);

        var result = KsudExtractor.TryDeflate(data, 0);

        Assert.NotNull(result);
        Assert.Equal(stream.Length, result!.Value.Consumed);
        Assert.Equal(payload, result.Value.Output);
    }

    [Fact]
    public void TryDeflate_GzipContainerHeader_IsRejected()
    {
        var payload = Encoding.UTF8.GetBytes(new string('G', 4096));
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(payload);
        }

        Assert.Null(KsudExtractor.TryDeflate(memory.ToArray(), 0));
    }

    [Fact]
    public void TryDeflate_EmptyOrMalformedRegion_ReturnsNull()
    {
        Assert.Null(KsudExtractor.TryDeflate([], 0));
        Assert.Null(KsudExtractor.TryDeflate(new byte[32], 32));
        Assert.Null(KsudExtractor.TryDeflate(new byte[32], 40));
        Assert.Null(KsudExtractor.TryDeflate([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], 0));
    }

    [Fact]
    public void TryDeflate_OutputAboveCap_IsRejected()
    {
        var payload = new byte[4096];
        payload.AsSpan().Fill(0x41);
        var stream = Deflate(payload);

        Assert.Null(KsudExtractor.TryDeflate(stream, 0, cap: 1024));
        Assert.NotNull(KsudExtractor.TryDeflate(stream, 0, cap: 8192));
    }

    [Fact]
    public void TryDeflate_EmptyOutput_ReturnsNull()
        => Assert.Null(KsudExtractor.TryDeflate(Deflate([]), 0));

    [Fact]
    public void GetOutBytes_MissingDataOffset_ReturnsEmptyArray()
        => Assert.Empty(KsudExtractor.GetOutBytes(new byte[64], new KsudAsset()));

    [Fact]
    public void GetOutBytes_UndecodableDataOffset_ReturnsEmptyArray()
    {
        var data = new byte[64];
        data.AsSpan().Fill(0xFF);

        Assert.Empty(KsudExtractor.GetOutBytes(data, new KsudAsset { DataOff = 0 }));
    }

    [Fact]
    public void GetOutBytes_ValidStream_ReturnsDecompressedPayload()
    {
        var payload = Encoding.UTF8.GetBytes("restored-kernelsu-ko");
        var stream = Deflate(payload);

        Assert.Equal(payload, KsudExtractor.GetOutBytes(stream, new KsudAsset { DataOff = 0 }));
    }

    [Fact]
    public void ScanRefs_UnsupportedMachine_ThrowsInvalidOperationException()
    {
        var image = ModuleImage(MachineX86_64);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18), 3);

        Assert.Throws<InvalidOperationException>(() => KsudExtractor.ScanRefs(new ElfFile(image), ExecVa));
    }

    [Theory]
    [InlineData(MachineAarch64)]
    [InlineData(MachineX86_64)]
    public void ScanRefs_UnmappedFunctionAddress_ReturnsEmpty(ushort machine)
        => Assert.Empty(KsudExtractor.ScanRefs(new ElfFile(ModuleImage(machine)), 0x9000));

    [Fact]
    public void ScanRefs_Aarch64AdrpAddPair_ResolvesWritableDataReference()
    {
        var elf = new ElfFile(ModuleImage(MachineAarch64, exec =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(exec, (uint)AdrpX0Page2);
            BinaryPrimitives.WriteUInt32LittleEndian(exec[4..], (uint)AddX0X0Immediate16);
        }));

        Assert.Equal([WritableVa + 0x10], KsudExtractor.ScanRefs(elf, ExecVa, window: 8));
    }

    [Fact]
    public void ScanRefs_Aarch64AdrpWithoutMatchingAdd_ResolvesNothing()
    {
        var elf = new ElfFile(ModuleImage(MachineAarch64, exec =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(exec, (uint)AdrpX0Page2);
            BinaryPrimitives.WriteUInt32LittleEndian(exec[4..], Ret);
        }));

        Assert.Empty(KsudExtractor.ScanRefs(elf, ExecVa, window: 8));
    }

    [Fact]
    public void ScanRefs_Aarch64ShiftedAddResolvingOutsideEverySegment_IsIgnored()
    {
        var elf = new ElfFile(ModuleImage(MachineAarch64, exec =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(exec, (uint)AdrpX0Page2);
            BinaryPrimitives.WriteUInt32LittleEndian(exec[4..], (uint)AddX0X0Immediate16Shifted);
        }));

        Assert.Empty(KsudExtractor.ScanRefs(elf, ExecVa, window: 8));
    }

    [Fact]
    public void ScanRefs_X86_64LeaRipRelative_ResolvesWritableDataReference()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64, exec => WriteLeaRip(exec, 0, WritableVa + 0x10)));

        Assert.Equal([WritableVa + 0x10], KsudExtractor.ScanRefs(elf, ExecVa, window: 16));
    }

    [Fact]
    public void ScanRefs_X86_64DuplicateTargets_AreReportedOnce()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64, exec =>
        {
            WriteLeaRip(exec, 0, WritableVa + 0x10);
            WriteLeaRip(exec, 16, WritableVa + 0x10);
        }));

        Assert.Equal([WritableVa + 0x10], KsudExtractor.ScanRefs(elf, ExecVa, window: 32));
    }

    [Fact]
    public void ScanRefs_X86_64TargetOutsideEverySegment_IsIgnored()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64, exec => WriteLeaRip(exec, 0, 0x9000)));

        Assert.Empty(KsudExtractor.ScanRefs(elf, ExecVa, window: 16));
    }

    [Fact]
    public void ScanRefs_WindowShorterThanTheInstruction_StopsBeforeReading()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64, exec => WriteLeaRip(exec, 0, WritableVa + 0x10)));

        Assert.Empty(KsudExtractor.ScanRefs(elf, ExecVa, window: 4));
    }

    [Fact]
    public void FindStaticFns_WordsPointingIntoExecutableCode_AreCollectedInScanOrder()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64, exec: null, writable =>
        {
            BinaryPrimitives.WriteUInt64LittleEndian(writable, ExecVa + 0x20);
            BinaryPrimitives.WriteUInt64LittleEndian(writable[8..], RodataVa);
            BinaryPrimitives.WriteUInt64LittleEndian(writable[16..], ExecVa + 0x40);
        }));

        Assert.Equal(
            [ExecVa + 0x20, ExecVa + 0x40],
            KsudExtractor.FindStaticFns(elf.Data, elf, WritableVa, scanBytes: 24));
    }

    [Fact]
    public void FindStaticFns_UnmappedStaticAddress_ReturnsEmpty()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64));

        Assert.Empty(KsudExtractor.FindStaticFns(elf.Data, elf, 0x9000));
    }

    [Fact]
    public void FindEntries_SyntheticAssetTable_ReportsNameFactoryAndOffset()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64));
        var view = WithAssetTable(elf.Data, AssetName, RodataVa + 0x20, ExecVa + 0x40);

        var (name, fnVa, tableOff) = Assert.Single(KsudExtractor.FindEntries(view, elf));

        Assert.Equal(AssetName, name);
        Assert.Equal(ExecVa + 0x40, fnVa);
        Assert.Equal(WritableFileOffset, tableOff);
    }

    [Theory]
    [InlineData("libkernelsu.ko")]
    [InlineData("android13_kernelsu.ko")]
    [InlineData("android13-5_kernelsu.ko")]
    [InlineData("kernel.ko")]
    [InlineData("not-an-asset.bin")]
    public void FindEntries_NameOutsideTheKernelSuShape_IsIgnored(string name)
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64));
        var view = WithAssetTable(elf.Data, name, RodataVa + 0x20, ExecVa + 0x40);

        Assert.Empty(KsudExtractor.FindEntries(view, elf));
    }

    [Theory]
    [InlineData("ANDROID13-5.15_KERNELSU.KO")]
    [InlineData("5.15-android13_kernelsu.ko")]
    [InlineData("android13.2-5.15_kernelsu.ko")]
    public void FindEntries_NameMatchingEitherVersionShape_IsAccepted(string name)
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64));
        var view = WithAssetTable(elf.Data, name, RodataVa + 0x20, ExecVa + 0x40);

        Assert.Equal(name, Assert.Single(KsudExtractor.FindEntries(view, elf)).Name);
    }

    [Fact]
    public void FindEntries_WritableNamePointer_IsRejected()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64));
        var view = WithAssetTable(elf.Data, AssetName, WritableVa + 0x20, ExecVa + 0x40);
        Encoding.ASCII.GetBytes(AssetName).CopyTo(view, WritableFileOffset + 0x80);

        Assert.Empty(KsudExtractor.FindEntries(view, elf));
    }

    [Fact]
    public void FindEntries_NonExecutableFactoryPointer_IsRejected()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64));
        var view = WithAssetTable(elf.Data, AssetName, RodataVa + 0x20, RodataVa + 0x40);

        Assert.Empty(KsudExtractor.FindEntries(view, elf));
    }

    [Theory]
    [InlineData(0UL, 256)]
    [InlineData(25UL, 8)]
    public void FindEntries_NameLengthOutsideBounds_IsRejected(ulong nameLength, int maxNameLen)
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64));
        var view = WithAssetTable(elf.Data, AssetName, RodataVa + 0x20, ExecVa + 0x40, nameLength);

        Assert.Empty(KsudExtractor.FindEntries(view, elf, maxNameLen));
    }

    [Fact]
    public void FindEntries_NonPrintableNameBytes_AreRejected()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64));
        var view = WithAssetTable(elf.Data, AssetName, RodataVa + 0x20, ExecVa + 0x40);
        view[RodataFileOffset + 0x20 + 3] = 0x01;

        Assert.Empty(KsudExtractor.FindEntries(view, elf));
    }

    [Fact]
    public void FindEntries_DuplicateNames_AreReportedOnce()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64));
        var view = WithAssetTable(elf.Data, AssetName, RodataVa + 0x20, ExecVa + 0x40);
        WriteTableEntry(view, WritableFileOffset + 0x40, RodataVa + 0x20, (ulong)AssetName.Length, ExecVa + 0x40);

        var entries = KsudExtractor.FindEntries(view, elf);

        Assert.Equal(AssetName, Assert.Single(entries).Name);
    }

    [Fact]
    public void ExtractAssets_TableEntryWithoutLocatablePayload_ReportsUnverifiedAsset()
    {
        var elf = new ElfFile(ModuleImage(MachineAarch64));
        var view = WithAssetTable(elf.Data, AssetName, RodataVa + 0x20, ExecVa + 0x40);

        var (assets, log) = KsudExtractor.ExtractAssets(new ElfFile(view), verbose: true);

        var asset = Assert.Single(assets);
        Assert.Equal(AssetName, asset.Name);
        Assert.Equal(ExecVa + 0x40, asset.FnVa);
        Assert.Equal(WritableFileOffset, asset.TableOff);
        Assert.Null(asset.DataOff);
        Assert.Null(asset.CompLen);
        Assert.Null(asset.Size);
        Assert.Equal(string.Empty, asset.Sha256);
        Assert.Equal(string.Empty, asset.Method);
        Assert.Equal(string.Empty, asset.OutFile);
        Assert.False(asset.Verified);
        Assert.False(asset.HashInBinary);
        Assert.Null(asset.HashRefVa);
        Assert.Equal($"ENTRIES: 1 [{AssetName}]", Assert.Single(log));
    }

    [Fact]
    public void ExtractAssets_QuietMode_EmitsNoLogLine()
    {
        var elf = new ElfFile(ModuleImage(MachineAarch64));
        var view = WithAssetTable(elf.Data, AssetName, RodataVa + 0x20, ExecVa + 0x40);

        var (assets, log) = KsudExtractor.ExtractAssets(new ElfFile(view));

        Assert.Single(assets);
        Assert.Empty(log);
    }

    [Fact]
    public void ExtractAssets_NoTableEntries_ReturnsEmptyAssetsAndLog()
    {
        var (assets, log) = KsudExtractor.ExtractAssets(new ElfFile(ModuleImage(MachineX86_64)));

        Assert.Empty(assets);
        Assert.Empty(log);
    }

    [Fact]
    public void Asset_CopyConstructor_ClonesEveryField()
    {
        var source = new KsudAsset
        {
            Name = AssetName,
            FnVa = 0x1000,
            TableOff = 0x600,
            DataOff = 0x610,
            CompLen = 32,
            Size = 4096,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3])),
            Verified = true,
            Method = "entry-ref",
            HashRefVa = 0x2020,
            HashInBinary = true,
            OutFile = "out.ko"
        };

        var copy = new KsudAsset(source);

        Assert.Equal(source.Name, copy.Name);
        Assert.Equal(source.FnVa, copy.FnVa);
        Assert.Equal(source.TableOff, copy.TableOff);
        Assert.Equal(source.DataOff, copy.DataOff);
        Assert.Equal(source.CompLen, copy.CompLen);
        Assert.Equal(source.Size, copy.Size);
        Assert.Equal(source.Sha256, copy.Sha256);
        Assert.Equal(source.Verified, copy.Verified);
        Assert.Equal(source.Method, copy.Method);
        Assert.Equal(source.HashRefVa, copy.HashRefVa);
        Assert.Equal(source.HashInBinary, copy.HashInBinary);
        Assert.Equal(source.OutFile, copy.OutFile);
        Assert.NotSame(source, copy);
    }

    [Fact]
    public void Asset_Defaults_AreEmptyAndUnverified()
    {
        var asset = new KsudAsset();

        Assert.Equal(string.Empty, asset.Name);
        Assert.Equal(0UL, asset.FnVa);
        Assert.Equal(0L, asset.TableOff);
        Assert.Null(asset.DataOff);
        Assert.Null(asset.CompLen);
        Assert.Null(asset.Size);
        Assert.Equal(string.Empty, asset.Sha256);
        Assert.False(asset.Verified);
        Assert.Equal(string.Empty, asset.Method);
        Assert.Null(asset.HashRefVa);
        Assert.False(asset.HashInBinary);
        Assert.Equal(string.Empty, asset.OutFile);
    }

    [Fact]
    public void SkippedAssetAndReport_Defaults_AreEmpty()
    {
        var skipped = new SkippedAsset();
        var report = new Report();

        Assert.Equal(string.Empty, skipped.Name);
        Assert.Equal(string.Empty, skipped.Reason);
        Assert.Equal(string.Empty, report.Source);
        Assert.Equal(0, report.Machine);
        Assert.Equal(0, report.TotalEntries);
        Assert.Equal(0, report.Extracted);
        Assert.Empty(report.Assets);
        Assert.Empty(report.Skipped);
        Assert.Empty(report.Log);
    }

    [Fact]
    public void ElfFile_RejectsNonElfThirtyTwoBitAndBigEndianInput()
    {
        var elf32 = ModuleImage(MachineX86_64);
        elf32[4] = 1;
        var bigEndian = ModuleImage(MachineX86_64);
        bigEndian[5] = 2;

        Assert.Throws<InvalidDataException>(() => new ElfFile(new byte[128]));
        Assert.Throws<InvalidDataException>(() => new ElfFile(elf32));
        Assert.Throws<InvalidDataException>(() => new ElfFile(bigEndian));
    }

    [Fact]
    public void ElfFile_AddressMapping_ClassifiesSegmentsByPermissionFlags()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64));

        Assert.Equal(ExecFileOffset, elf.VaToOff(ExecVa));
        Assert.Equal(RodataFileOffset, elf.VaToOff(RodataVa));
        Assert.Equal(WritableFileOffset, elf.VaToOff(WritableVa));
        Assert.Null(elf.VaToOff(0x9000));
        Assert.Null(elf.VaToOff(0));
        Assert.Equal(ExecVa, elf.OffToVa(ExecFileOffset));
        Assert.Null(elf.OffToVa(-1));
        Assert.True(elf.IsExecVa(ExecVa));
        Assert.False(elf.IsExecVa(RodataVa));
        Assert.False(elf.IsExecVa(WritableVa));
        Assert.True(elf.IsWritableVa(WritableVa));
        Assert.False(elf.IsWritableVa(RodataVa));
        Assert.False(elf.IsWritableVa(0x9000));
        Assert.Equal(WritableFileOffset + 0x10, elf.ResolveRef(0x1_0000_0000 + WritableVa + 0x10));
        Assert.Null(elf.ResolveRef(0x9000));
    }

    [Fact]
    public void ElfFile_ReadableRanges_ExcludesExecutableAndWritableSegments()
    {
        var elf = new ElfFile(ModuleImage(MachineX86_64));

        Assert.Equal([(RodataFileOffset, RodataFileOffset + SegmentFileSize)], elf.ReadableRanges());
    }

    [Fact]
    public void ElfFile_SectionTable_AbsentTableReportsNoSections()
        => Assert.Empty(new ElfFile(ModuleImage(MachineX86_64)).SectionTable());

    private static void WriteLeaRip(Span<byte> exec, int offset, ulong targetVa)
    {
        exec[offset] = 0x48;
        exec[offset + 1] = 0x8D;
        exec[offset + 2] = 0x05;
        var nextVa = ExecVa + (ulong)offset + 7;
        BinaryPrimitives.WriteInt32LittleEndian(exec[(offset + 3)..], checked((int)(targetVa - nextVa)));
    }

    private static byte[] Deflate(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(payload);
        }

        return output.ToArray();
    }

    private static byte[] WithAssetTable(byte[] image, string name, ulong nameVa, ulong fnVa, ulong? nameLength = null)
    {
        var view = (byte[])image.Clone();
        var nameBytes = Encoding.ASCII.GetBytes(name);
        nameBytes.CopyTo(view, RodataFileOffset + 0x20);
        view[RodataFileOffset + 0x20 + nameBytes.Length] = 0;
        WriteTableEntry(view, WritableFileOffset, nameVa, nameLength ?? (ulong)nameBytes.Length, fnVa);
        return view;
    }

    private static void WriteTableEntry(byte[] view, int offset, ulong nameVa, ulong nameLength, ulong fnVa)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(view.AsSpan(offset), nameVa);
        BinaryPrimitives.WriteUInt64LittleEndian(view.AsSpan(offset + 8), nameLength);
        BinaryPrimitives.WriteUInt64LittleEndian(view.AsSpan(offset + 16), fnVa);
    }

    private static byte[] ModuleImage(ushort machine, Action<Span<byte>>? exec = null, Action<Span<byte>>? writable = null)
    {
        var image = new byte[ImageLength];
        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 2;
        image[5] = 1;
        image[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18), machine);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(56), 3);

        WriteSegment(image, 64, ExecVa, ExecFileOffset, 0x5);
        WriteSegment(image, 64 + 56, RodataVa, RodataFileOffset, 0x4);
        WriteSegment(image, 64 + 112, WritableVa, WritableFileOffset, 0x6);

        exec?.Invoke(image.AsSpan(ExecFileOffset, SegmentFileSize));
        writable?.Invoke(image.AsSpan(WritableFileOffset, SegmentFileSize));
        return image;
    }

    private static void WriteSegment(byte[] image, int offset, ulong vaddr, ulong fileOffset, uint flags)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 4), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 8), fileOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 16), vaddr);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 24), vaddr);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 32), SegmentFileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 40), SegmentFileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 48), 0x1000);
    }
}