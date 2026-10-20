using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Characterization tests for the boot-image ELF inspection and cleaning helpers. <c>ElfTool</c> and
/// <c>ElfCleaner</c> have no production consumer beyond each other, so these tests pin the observable behaviour of
/// the shipped API rather than judge its design: header detection, section enumeration, the zeroing strip semantics
/// that keep the file length stable, and the file-level clean/replace contract including its failure rollback.
/// <para>boot 镜像 ELF 检查与清理辅助类的表征测试。<c>ElfTool</c> 与 <c>ElfCleaner</c> 在生产代码中除彼此之外没有
/// 消费者，因此本测试钉住已发布 API 的可观察行为，而不评判其设计：头部检测、节枚举、保持文件长度不变的清零式剥离
/// 语义，以及文件级清理/替换契约（含失败时的回滚）。</para>
/// </summary>
public class ElfToolCharacterizationTests
{
    private static readonly byte[] TextPayload = BuildFill(0x11, 64);

    private static readonly byte[] DebugPayload = BuildFill(0x22, 32);

    private static readonly byte[] CommentPayload = BuildFill(0x33, 16);

    private static byte[] BuildFill(byte seed, int length)
    {
        var data = new byte[length];
        data.AsSpan().Fill(seed);
        return data;
    }

    private static SyntheticElfImage TwoSections(bool is64 = true)
        => SyntheticElf.Build(is64, [(".text", 1, TextPayload), (".debug_info", 1, DebugPayload)]);

    [Fact]
    public void DetectElfType_ClassAndMagicVariants_ReportsMatchingClass()
    {
        Assert.Equal(ElfTool.ElfType.Elf64, ElfTool.DetectElfType(SyntheticElf.Build(is64: true).Image));
        Assert.Equal(ElfTool.ElfType.Elf32, ElfTool.DetectElfType(SyntheticElf.Build(is64: false).Image));
        Assert.Equal(ElfTool.ElfType.Unknown, ElfTool.DetectElfType(null!));
        Assert.Equal(ElfTool.ElfType.Unknown, ElfTool.DetectElfType([0x7F, 0x45, 0x4C, 0x46]));
        Assert.Equal(ElfTool.ElfType.Unknown, ElfTool.DetectElfType([0x7F, 0x45, 0x4C, 0x46, 3, 1]));
        Assert.Equal(ElfTool.ElfType.Unknown, ElfTool.DetectElfType([0x00, 0x45, 0x4C, 0x46, 2, 1]));
    }

    [Fact]
    public void IsElf_RequiresMagicAndKnownClass()
    {
        Assert.True(ElfTool.IsElf(SyntheticElf.Build().Image));
        Assert.False(ElfTool.IsElf(null!));
        Assert.False(ElfTool.IsElf([0x7F, 0x45, 0x4C, 0x46]));
        Assert.False(ElfTool.IsElf(Encoding.ASCII.GetBytes("CrAU")));
    }

    [Fact]
    public void Parse_TooSmallOrNullBuffer_ReportsInvalidWithMessage()
    {
        Assert.Equal("Data is too small", ElfTool.Parse([0x7F, 0x45, 0x4C, 0x46, 2, 1]).ErrorMessage);
        Assert.Equal("Data is too small", ElfTool.Parse(null!).ErrorMessage);
        Assert.False(ElfTool.Parse(null!).IsValid);
        Assert.Equal(ElfTool.ElfType.Unknown, ElfTool.Parse(null!).Type);
    }

    [Fact]
    public void Parse_NonElfBuffer_ReportsNotAnElfFile()
    {
        var result = ElfTool.Parse(new byte[128]);

        Assert.False(result.IsValid);
        Assert.Equal("Not an ELF file", result.ErrorMessage);
    }

    [Fact]
    public void Parse_Elf64Header_ReportsClassMachineEntryPointAndCounts()
    {
        var image = SyntheticElf.Build(
            is64: true,
            sections: [(".text", 1, TextPayload)],
            machine: 183,
            entryPoint: 0x1000);

        var result = ElfTool.Parse(image.Image);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(ElfTool.ElfType.Elf64, result.Type);
        Assert.Equal(1, result.Version);
        Assert.Equal(183, result.Machine);
        Assert.Equal(0x1000UL, result.EntryPoint);
        Assert.Equal(1, result.ProgramHeaderCount);
        Assert.Equal(image.Count, result.SectionHeaderCount);
    }

    [Fact]
    public void Parse_Elf32Header_ReadsCountsFromTheThirtyTwoBitSlots()
    {
        var image = SyntheticElf.Build(is64: false, sections: [(".text", 1, TextPayload)], machine: 3);

        var result = ElfTool.Parse(image.Image);

        Assert.True(result.IsValid);
        Assert.Equal(ElfTool.ElfType.Elf32, result.Type);
        Assert.Equal(3, result.Machine);
        Assert.Equal(1, result.ProgramHeaderCount);
        Assert.Equal(image.Count, result.SectionHeaderCount);
    }

    [Fact]
    public void GetElfSize_ValidHeader_AddsSectionTableExtent()
    {
        var elf64 = TwoSections();
        var elf32 = TwoSections(is64: false);

        Assert.Equal(elf64.SizeFromHeader, ElfTool.GetElfSize(elf64.Image));
        Assert.Equal(elf32.SizeFromHeader, ElfTool.GetElfSize(elf32.Image));
    }

    [Fact]
    public void GetElfSize_UnparsableHeader_ReturnsZero()
    {
        Assert.Equal(0UL, ElfTool.GetElfSize(new byte[128]));
        Assert.Equal(0UL, ElfTool.GetElfSize(null!));
    }

    [Fact]
    public void GetSections_ValidTable_ReturnsResolvedNamesOffsetsAndSizes()
    {
        var image = SyntheticElf.Build(
            is64: true,
            sections: [(".text", 1, TextPayload), (".debug_info", 1, DebugPayload)]);

        var sections = ElfTool.GetSections(image.Image);

        Assert.Equal(image.Count, sections.Count);
        Assert.Equal(image.Sections.Select(expected => expected.Name), sections.Select(section => section.Name));
        Assert.Equal(image.Sections.Select(expected => expected.Type), sections.Select(section => section.Type));
        Assert.Equal(image.Sections.Select(expected => expected.Offset), sections.Select(section => section.Offset));
        Assert.Equal(image.Sections.Select(expected => expected.Size), sections.Select(section => section.Size));
        Assert.Equal([0, 1, 2, 3], sections.Select(section => section.Index).ToArray());
        Assert.Equal(TextPayload, image.Image.AsSpan((int)image.Section(".text").Offset, TextPayload.Length).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetSections_StringTableIndexOutOfRange_ReturnsEmpty(bool is64)
        => Assert.Empty(ElfTool.GetSections(SyntheticElf.Build(is64, sections: [], stringTableIndex: 0xFFFF).Image));

    [Fact]
    public void GetSections_ZeroEntrySizeOrZeroCount_ReturnsEmpty()
    {
        var zeroEntrySize = TwoSections();
        BinaryPrimitives.WriteUInt16LittleEndian(zeroEntrySize.Image.AsSpan(58), 0);
        var zeroCount = TwoSections();
        BinaryPrimitives.WriteUInt16LittleEndian(zeroCount.Image.AsSpan(60), 0);

        Assert.Empty(ElfTool.GetSections(zeroEntrySize.Image));
        Assert.Empty(ElfTool.GetSections(zeroCount.Image));
    }

    [Fact]
    public void GetSections_NullOrNonElfInput_ReturnsEmptyWithoutThrowing()
    {
        Assert.Empty(ElfTool.GetSections(null!));
        Assert.Empty(ElfTool.GetSections(new byte[256]));
    }

    [Fact]
    public void GetSections_TableTruncatedMidway_StopsAtLastCompleteEntry()
    {
        var image = SyntheticElf.Build(
            is64: true,
            sections: [(".text", 1, TextPayload), (".debug_info", 1, DebugPayload), (".comment", 1, CommentPayload)]);
        var truncated = image.Image.AsSpan(0, image.Image.Length - 64).ToArray();

        Assert.Equal(image.Count - 1, ElfTool.GetSections(truncated).Count);
    }

    [Fact]
    public void StripSection_ExistingName_ZeroesDataAndClearsHeaderEntry()
    {
        var image = TwoSections();
        var debug = image.Section(".debug_info");
        var headerEntry = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.Image.AsSpan(40)) + debug.Index * 64;

        var stripped = ElfTool.StripSection(image.Image, ".debug_info");

        Assert.NotSame(image.Image, stripped);
        Assert.Equal(image.Image.Length, stripped.Length);
        Assert.Equal(new byte[DebugPayload.Length], stripped.AsSpan((int)debug.Offset, DebugPayload.Length).ToArray());
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(stripped.AsSpan(headerEntry + 4)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(stripped.AsSpan(headerEntry + 32)));
        Assert.Equal(
            image.Section(".text").Offset,
            ElfTool.GetSections(stripped).Single(section => section.Name == ".text").Offset);
        Assert.Equal(TextPayload, stripped.AsSpan((int)image.Section(".text").Offset, TextPayload.Length).ToArray());
    }

    [Fact]
    public void StripSection_ThirtyTwoBitImage_ClearsTheThirtyTwoBitSizeSlot()
    {
        var image = TwoSections(is64: false);
        var debug = image.Section(".debug_info");
        var headerEntry = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.Image.AsSpan(32)) + debug.Index * 40;

        var stripped = ElfTool.StripSection(image.Image, ".debug_info");

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(stripped.AsSpan(headerEntry + 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(stripped.AsSpan(headerEntry + 20)));
        Assert.Equal(new byte[DebugPayload.Length], stripped.AsSpan((int)debug.Offset, DebugPayload.Length).ToArray());
    }

    [Fact]
    public void StripSection_UnknownName_ReturnsByteIdenticalCopy()
    {
        var image = SyntheticElf.Build(is64: true, sections: [(".text", 1, TextPayload)]);

        var stripped = ElfTool.StripSection(image.Image, ".does_not_exist");

        Assert.NotSame(image.Image, stripped);
        Assert.Equal(image.Image, stripped);
    }

    [Fact]
    public void StripSection_NonElfBuffer_ThrowsElfPatchingException()
        => Assert.Throws<ElfPatchingException>(() => ElfTool.StripSection(new byte[128], ".text"));

    [Fact]
    public void StripSection_NullArguments_ThrowArgumentNullException()
    {
        var image = SyntheticElf.Build().Image;

        Assert.Throws<ArgumentNullException>(() => ElfTool.StripSection(null!, ".text"));
        Assert.Throws<ArgumentNullException>(() => ElfTool.StripSection(image, null!));
    }

    [Fact]
    public void StripSectionsByName_WildcardPattern_ClearsEveryMatchingSection()
    {
        var image = SyntheticElf.Build(
            is64: true,
            sections: [(".text", 1, TextPayload), (".debug_info", 1, DebugPayload), (".debug_line", 1, DebugPayload)]);

        var stripped = ElfTool.StripSectionsByName(image.Image, [".debug_*"]);

        Assert.Equal(0u, ElfTool.GetSections(stripped).Single(section => section.Name == ".debug_info").Type);
        Assert.Equal(0u, ElfTool.GetSections(stripped).Single(section => section.Name == ".debug_line").Type);
        Assert.Equal(1u, ElfTool.GetSections(stripped).Single(section => section.Name == ".text").Type);
        Assert.Equal(new byte[DebugPayload.Length], stripped.AsSpan((int)image.Section(".debug_info").Offset, DebugPayload.Length).ToArray());
    }

    [Fact]
    public void StripSectionsByName_ExactPattern_IsCaseSensitive()
    {
        var image = SyntheticElf.Build(is64: true, sections: [(".debug_info", 1, DebugPayload)]);

        Assert.Equal(image.Image, ElfTool.StripSectionsByName(image.Image, [".DEBUG_INFO"]));
        Assert.Equal(0u, ElfTool.GetSections(ElfTool.StripSectionsByName(image.Image, [".debug_info"]))[1].Type);
    }

    [Fact]
    public void StripSectionsByName_NullArguments_ThrowArgumentNullException()
    {
        var image = SyntheticElf.Build().Image;

        Assert.Throws<ArgumentNullException>(() => ElfTool.StripSectionsByName(null!, [".text"]));
        Assert.Throws<ArgumentNullException>(() => ElfTool.StripSectionsByName(image, null!));
    }

    [Fact]
    public void StripSectionsByName_NonElfBuffer_ThrowsElfPatchingException()
        => Assert.Throws<ElfPatchingException>(() => ElfTool.StripSectionsByName(new byte[128], [".text"]));

    [Fact]
    public void IsCleanable_MirrorsElfDetection()
    {
        Assert.True(ElfCleaner.IsCleanable(SyntheticElf.Build().Image));
        Assert.False(ElfCleaner.IsCleanable(null!));
        Assert.False(ElfCleaner.IsCleanable(new byte[128]));
    }

    [Fact]
    public void GetCleanableSections_DefaultOptions_FlagsDebugNoteAndroidAndSymbolSections()
    {
        var image = SyntheticElf.Build(
            is64: true,
            sections:
            [
                (".text", 1, TextPayload),
                (".debug_info", 1, DebugPayload),
                (".note.gnu.build-id", 1, DebugPayload),
                (".symtab", 2, DebugPayload),
                (".strtab", 3, DebugPayload),
                (".got", 1, DebugPayload),
                (".comment", 1, CommentPayload)
            ]);

        var sections = ElfCleaner.GetCleanableSections(image.Image);

        Assert.Equal(image.Count, sections.Count);
        Assert.False(sections[0].IsCleanable);
        Assert.False(sections[1].IsCleanable);
        Assert.False(sections[^1].IsCleanable);
        Assert.All(sections.Skip(2).Take(image.Count - 3), section => Assert.True(section.IsCleanable));
        Assert.Equal(
            image.Sections.Select(expected => expected.Name),
            sections.Select(section => section.Name));
    }

    [Fact]
    public void GetCleanableSections_NonElfInput_ReturnsEmpty()
        => Assert.Empty(ElfCleaner.GetCleanableSections(new byte[128]));

    [Fact]
    public void Clean_DefaultOptions_ReportsEveryRemovedSectionName()
    {
        var image = SyntheticElf.Build(
            is64: true,
            sections: [(".text", 1, TextPayload), (".debug_info", 1, DebugPayload), (".symtab", 2, DebugPayload)]);

        var result = ElfCleaner.Clean(image.Image);

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(2, result.SectionsRemoved);
        Assert.Equal([".debug_info", ".symtab"], result.RemovedSectionNames);
        Assert.Equal(image.Image.Length, result.OriginalSize);
        Assert.Equal(image.Image.Length, result.CleanedSize);
        Assert.Equal(0, result.SpaceSaved);
    }

    [Fact]
    public void Clean_NoMatchingSection_ReportsSuccessWithoutRemovals()
    {
        var image = SyntheticElf.Build(is64: true, sections: [(".text", 1, TextPayload)]);

        var result = ElfCleaner.Clean(image.Image);

        Assert.True(result.Success);
        Assert.Equal(0, result.SectionsRemoved);
        Assert.Empty(result.RemovedSectionNames);
        Assert.Equal(image.Image.Length, result.CleanedSize);
    }

    [Fact]
    public void Clean_OptionsDisablingEveryCategory_RemovesNothing()
    {
        var image = SyntheticElf.Build(
            is64: true,
            sections: [(".debug_info", 1, DebugPayload), (".symtab", 2, DebugPayload), (".got", 1, DebugPayload)]);

        var result = ElfCleaner.Clean(image.Image, new ElfCleaner.CleanOptions
        {
            RemoveDebugSections = false,
            RemoveAndroidSections = false,
            CleanSymbolTable = false
        });

        Assert.True(result.Success);
        Assert.Equal(0, result.SectionsRemoved);
        Assert.Empty(result.RemovedSectionNames);
    }

    [Fact]
    public void Clean_SingleCategoryEnabled_RemovesOnlyThatCategory()
    {
        var image = SyntheticElf.Build(
            is64: true,
            sections: [(".debug_info", 1, DebugPayload), (".symtab", 2, DebugPayload), (".got", 1, DebugPayload)]);

        var debugOnly = ElfCleaner.Clean(image.Image, new ElfCleaner.CleanOptions
        {
            RemoveDebugSections = true,
            RemoveAndroidSections = false,
            CleanSymbolTable = false
        });

        Assert.Equal([".debug_info"], debugOnly.RemovedSectionNames);
        Assert.Equal([".got"], ElfCleaner.Clean(image.Image, new ElfCleaner.CleanOptions
        {
            RemoveDebugSections = false,
            RemoveAndroidSections = true,
            CleanSymbolTable = false
        }).RemovedSectionNames);
        Assert.Equal([".symtab"], ElfCleaner.Clean(image.Image, new ElfCleaner.CleanOptions
        {
            RemoveDebugSections = false,
            RemoveAndroidSections = false,
            CleanSymbolTable = true
        }).RemovedSectionNames);
    }

    [Fact]
    public void Clean_NullOrNonElfInput_ReportsFailureInsteadOfThrowing()
    {
        var fromNull = ElfCleaner.Clean(null!);
        var fromNonElf = ElfCleaner.Clean(new byte[128]);

        Assert.False(fromNull.Success);
        Assert.Equal(0, fromNull.OriginalSize);
        Assert.Equal("Not a cleanable ELF file", fromNull.ErrorMessage);
        Assert.False(fromNonElf.Success);
        Assert.Equal(128, fromNonElf.OriginalSize);
        Assert.Equal("Not a cleanable ELF file", fromNonElf.ErrorMessage);
        Assert.Empty(fromNonElf.RemovedSectionNames);
    }

    [Fact]
    public void CleanData_NullBecomesEmptyAndNonElfIsReturnedUnchanged()
    {
        var nonElf = new byte[64];

        Assert.Empty(ElfCleaner.CleanData(null!));
        Assert.Same(nonElf, ElfCleaner.CleanData(nonElf));
    }

    [Fact]
    public void CleanData_NoMatchingSection_ReturnsTheSameArrayReference()
    {
        var image = SyntheticElf.Build(is64: true, sections: [(".text", 1, TextPayload)]).Image;

        Assert.Same(image, ElfCleaner.CleanData(image));
    }

    [Fact]
    public void CleanData_MatchingSections_ReturnsStrippedCopyKeepingFileLength()
    {
        var image = TwoSections();

        var cleaned = ElfCleaner.CleanData(image.Image);

        Assert.NotSame(image.Image, cleaned);
        Assert.Equal(image.Image.Length, cleaned.Length);
        Assert.Equal(new byte[DebugPayload.Length], cleaned.AsSpan((int)image.Section(".debug_info").Offset, DebugPayload.Length).ToArray());
        Assert.Equal(TextPayload, cleaned.AsSpan((int)image.Section(".text").Offset, TextPayload.Length).ToArray());
    }

    [Fact]
    public void CleanFile_ValidInput_WritesCleanedBytesAndReportsRemovals()
    {
        var image = TwoSections();
        using var temp = new TempDirectory();
        var input = temp.WriteBytes("input.so", image.Image);
        var output = Path.Combine(temp.Path, "cleaned.so");

        var result = ElfCleaner.CleanFile(input, output);

        Assert.True(result.Success);
        Assert.Equal(1, result.SectionsRemoved);
        Assert.Equal([".debug_info"], result.RemovedSectionNames);
        Assert.Equal(ElfCleaner.CleanData(image.Image), File.ReadAllBytes(output));
    }

    [Fact]
    public void CleanFile_MissingOutputDirectory_ThrowsIoAndWritesNothing()
    {
        var image = TwoSections();
        using var temp = new TempDirectory();
        var input = temp.WriteBytes("input.so", image.Image);
        var absentDirectory = Path.Combine(temp.Path, "absent");

        Assert.Throws<DirectoryNotFoundException>(() => ElfCleaner.CleanFile(input, Path.Combine(absentDirectory, "cleaned.so")));
        Assert.False(Directory.Exists(absentDirectory));
    }

    [Fact]
    public void CleanFile_MissingInput_ReportsFailureAndWritesNothing()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "cleaned.so");

        var result = ElfCleaner.CleanFile(Path.Combine(temp.Path, "absent.so"), output);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(0, result.OriginalSize);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void CleanFile_NonElfInput_LeavesExistingOutputUntouched()
    {
        using var temp = new TempDirectory();
        var input = temp.WriteBytes("notelf.bin", new byte[128]);
        var output = temp.WriteBytes("cleaned.bin", "preserve"u8.ToArray());

        var result = ElfCleaner.CleanFile(input, output);

        Assert.False(result.Success);
        Assert.Equal("preserve"u8.ToArray(), File.ReadAllBytes(output));
    }

    [Fact]
    public void CleanFileInPlace_Success_ReplacesOriginalAndRemovesStagingFile()
    {
        var image = TwoSections();
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("kernel.so", image.Image);

        var result = ElfCleaner.CleanFileInPlace(path);

        Assert.True(result.Success);
        Assert.Equal(ElfCleaner.CleanData(image.Image), File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".clean.tmp"));
    }

    [Fact]
    public void CleanFileInPlace_Failure_KeepsOriginalContentAndDeletesStagingFile()
    {
        using var temp = new TempDirectory();
        var content = BuildFill(0x7B, 128);
        var path = temp.WriteBytes("notelf.bin", content);

        var result = ElfCleaner.CleanFileInPlace(path);

        Assert.False(result.Success);
        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".clean.tmp"));
    }

    [Fact]
    public void CleanOptions_Defaults_EnableEveryCategory()
    {
        var options = new ElfCleaner.CleanOptions();

        Assert.True(options.RemoveDebugSections);
        Assert.True(options.RemoveAndroidSections);
        Assert.True(options.CleanSymbolTable);
    }

    [Fact]
    public void CleanResult_SpaceSaved_NeverGoesNegative()
    {
        var result = new ElfCleaner.CleanResult { OriginalSize = 10, CleanedSize = 40 };

        Assert.Equal(0, result.SpaceSaved);
        Assert.Empty(result.RemovedSectionNames);
        Assert.False(result.Success);
    }
}