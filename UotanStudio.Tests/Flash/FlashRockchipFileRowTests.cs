using System.Buffers.Binary;
using System.Text;
using UotanStudio.Hash.RockchipCrc32;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// Cases for the Rockchip configuration rows that carry a file instead of an image: the row is selectable, selecting it
/// checks the file it points at, and neither row is ever handed to a flash or erase action as a partition.
/// <para>承载文件而非镜像的瑞芯微配置行用例：该行可勾选，勾选即检查它指向的文件，且这些行永远不会作为分区交给刷写或擦除动作。</para>
/// </summary>
public sealed class FlashRockchipFileRowTests : IDisposable
{
    private const int HeaderSize = 102;
    private const int EntrySize = 57;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-flash-rk-file-" + Guid.NewGuid().ToString("N"));

    public FlashRockchipFileRowTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ImportRkConfigAsync_LoaderRowWithValidImage_BecomesSelectableAndAccepted()
    {
        var loader = Path.Combine(_root, "MiniLoaderAll.bin");
        await File.WriteAllBytesAsync(loader, BuildLoader(), TestContext.Current.CancellationToken);
        var config = await WriteConfigAsync("valid.cfg",
            new RockchipConfigEntry { Name = RockchipConfigFile.LoaderRowName, Path = loader, Selected = true },
            new RockchipConfigEntry { Name = "boot", Path = string.Empty, Selected = true });
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

        await model.ImportRkConfigAsync(config);
        await model.SpecialFileRowCheck;

        var loaderRow = Assert.Single(model.Partitions, row => row.Command == "loader");
        Assert.Equal(loader, model.EffectiveRkLoaderPath);
        Assert.Equal(LanguageService.Instance.GetString("Flash_RkLoaderValid"), loaderRow.StatusText);
        Assert.False(FlashViewModel.IsWritableEntry(loaderRow));
    }

    /// <remarks>
    /// With no device bound every grammar is tried in turn, so a file matched by none is rejected in generic wording
    /// rather than Rockchip-specific wording.
    /// <para>没有任何设备绑定时会逐种语法尝试；一种语法都不命中的文件以通用措辞拒绝，而不是按瑞芯微专用措辞。</para>
    /// </remarks>
    [Fact]
    public async Task ImportRkConfigAsync_LoaderRowWithNonLoaderContent_StaysSelectableAndIsRejected()
    {
        var loader = Path.Combine(_root, "MiniLoaderAll.bin");
        await File.WriteAllBytesAsync(loader, "LDR "u8.ToArray(), TestContext.Current.CancellationToken);
        var config = await WriteConfigAsync("damaged.cfg",
            new RockchipConfigEntry { Name = RockchipConfigFile.LoaderRowName, Path = loader, Selected = true },
            new RockchipConfigEntry { Name = "boot", Path = string.Empty, Selected = true });
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

        await model.ImportRkConfigAsync(config);
        await model.SpecialFileRowCheck;

        var loaderRow = Assert.Single(model.Partitions, row => row.Command == "loader");

        Assert.Equal(LanguageService.Instance.GetString("Flash_LoaderInvalidStatus"), loaderRow.StatusText);
    }

    [Fact]
    public async Task ImportRkConfigAsync_LoaderRowWithDamagedChecksum_IsRejected()
    {
        var image = BuildLoader();
        image[^1] ^= 0xFF;
        var loader = Path.Combine(_root, "MiniLoaderAll.bin");
        await File.WriteAllBytesAsync(loader, image, TestContext.Current.CancellationToken);
        var config = await WriteConfigAsync("checksum.cfg",
            new RockchipConfigEntry { Name = RockchipConfigFile.LoaderRowName, Path = loader, Selected = true },
            new RockchipConfigEntry { Name = "boot", Path = string.Empty, Selected = true });
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

        await model.ImportRkConfigAsync(config);
        await model.SpecialFileRowCheck;

        var loaderRow = Assert.Single(model.Partitions, row => row.Command == "loader");
        Assert.Equal(LanguageService.Instance.GetString("Flash_RkLoaderInvalidStatus"), loaderRow.StatusText);
    }

    [Fact]
    public async Task ImportRkConfigAsync_ParameterRow_BecomesSelectableAndItsTableIsCounted()
    {
        var parameter = Path.Combine(_root, "parameter.txt");
        await File.WriteAllTextAsync(parameter,
            "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot),0x00002000@0x00006000(misc)\n",
            TestContext.Current.CancellationToken);
        var config = await WriteConfigAsync("parameter.cfg",
            new RockchipConfigEntry { Name = RockchipConfigFile.ParameterRowName, Path = parameter, Selected = true },
            new RockchipConfigEntry { Name = "boot", Path = string.Empty, Selected = true });
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

        await model.ImportRkConfigAsync(config);

        var parameterRow = Assert.Single(model.Partitions, row => row.Command == "parameter");
        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            LanguageService.Instance.GetString("Flash_RkParameterValid"), 2), parameterRow.StatusText);
        Assert.False(FlashViewModel.IsWritableEntry(parameterRow));
    }

    [Fact]
    public async Task ImportRkConfigAsync_ParameterRowWithForeignFile_IsRejected()
    {
        var parameter = Path.Combine(_root, "parameter.txt");
        await File.WriteAllTextAsync(parameter, "not a partition table\n", TestContext.Current.CancellationToken);
        var config = await WriteConfigAsync("parameter-bad.cfg",
            new RockchipConfigEntry { Name = RockchipConfigFile.ParameterRowName, Path = parameter, Selected = true },
            new RockchipConfigEntry { Name = "boot", Path = string.Empty, Selected = true });
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

        await model.ImportRkConfigAsync(config);

        var parameterRow = Assert.Single(model.Partitions, row => row.Command == "parameter");
        Assert.Equal(LanguageService.Instance.GetString("Flash_RkParameterInvalidStatus"), parameterRow.StatusText);
    }

    /// <remarks>
    /// A file no grammar recognizes is rejected in generic wording: at that point its format cannot even be named.
    /// <para>未被任何语法认出的文件以通用措辞拒绝：此时连它属于哪种格式都无从判断。</para>
    /// </remarks>
    [Fact]
    public async Task SelectingLoaderRow_AfterItsFileChanged_ReportsTheNewVerdict()
    {
        var loader = Path.Combine(_root, "MiniLoaderAll.bin");
        await File.WriteAllBytesAsync(loader, BuildLoader(), TestContext.Current.CancellationToken);
        var config = await WriteConfigAsync("reselect.cfg",
            new RockchipConfigEntry { Name = RockchipConfigFile.LoaderRowName, Path = loader, Selected = true },
            new RockchipConfigEntry { Name = "boot", Path = string.Empty, Selected = true });
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());
        await model.ImportRkConfigAsync(config);
        await model.SpecialFileRowCheck;
        var loaderRow = Assert.Single(model.Partitions, row => row.Command == "loader");
        Assert.Equal(LanguageService.Instance.GetString("Flash_RkLoaderValid"), loaderRow.StatusText);

        await File.WriteAllBytesAsync(loader, "LDR "u8.ToArray(), TestContext.Current.CancellationToken);
        loaderRow.IsSelected = true;
        await model.SpecialFileRowCheck;

        Assert.True(loaderRow.IsSelected);

        Assert.Equal(LanguageService.Instance.GetString("Flash_LoaderInvalidStatus"), loaderRow.StatusText);
    }

    [Fact]
    public async Task ImportRkConfigAsync_MissingLoaderFile_ReportsTheMissingFileOnce()
    {
        var missing = Path.Combine(_root, "MiniLoaderAll.bin");
        var config = await WriteConfigAsync("missing-loader.cfg",
            new RockchipConfigEntry { Name = RockchipConfigFile.LoaderRowName, Path = missing, Selected = true },
            new RockchipConfigEntry { Name = "boot", Path = string.Empty, Selected = true });
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

        await model.ImportRkConfigAsync(config);
        await model.SpecialFileRowCheck;
        model.FlushPendingLog();

        Assert.Equal(1, CountReportedLines(model.LogText, LanguageService.Instance.GetString("Flash_LoaderMissing")));
    }

    [Fact]
    public async Task ImportRkConfigAsync_ValidLoaderFile_ReportsTheAcceptedLoaderOnce()
    {
        var loader = Path.Combine(_root, "MiniLoaderAll.bin");
        await File.WriteAllBytesAsync(loader, BuildLoader(), TestContext.Current.CancellationToken);
        var config = await WriteConfigAsync("single-verdict.cfg",
            new RockchipConfigEntry { Name = RockchipConfigFile.LoaderRowName, Path = loader, Selected = true },
            new RockchipConfigEntry { Name = "boot", Path = string.Empty, Selected = true });
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

        await model.ImportRkConfigAsync(config);
        await model.SpecialFileRowCheck;
        model.FlushPendingLog();

        Assert.Equal(1, CountReportedLines(model.LogText, LanguageService.Instance.GetString("Flash_RkLoaderChecked")));
    }

    [Fact]
    public async Task ParameterRow_InAnotherDirectory_SuppliesTheTableTheFlashActionsResolve()
    {
        var parameterDirectory = Path.Combine(_root, "table");
        var imageDirectory = Path.Combine(_root, "images");
        Directory.CreateDirectory(parameterDirectory);
        Directory.CreateDirectory(imageDirectory);
        var parameter = Path.Combine(parameterDirectory, "parameter.txt");
        await File.WriteAllTextAsync(parameter,
            "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot),0x00008000@0x00006000(boot)\n",
            TestContext.Current.CancellationToken);
        var image = Path.Combine(imageDirectory, "boot.img");
        await File.WriteAllBytesAsync(image, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var config = await WriteConfigAsync("split-table.cfg",
            new RockchipConfigEntry { Name = RockchipConfigFile.ParameterRowName, Path = parameter, Selected = true },
            new RockchipConfigEntry { Name = "boot", Path = image, Selected = true });
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

        await model.ImportRkConfigAsync(config);

        Assert.Equal(parameter, model.RkParameterTablePath());
    }

    [Fact]
    public async Task MissingLoaderRow_LeavesThePartitionTableResolvable()
    {
        var parameter = Path.Combine(_root, "parameter.txt");
        await File.WriteAllTextAsync(parameter,
            "CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00004000(uboot),0x00008000@0x00006000(boot)\n",
            TestContext.Current.CancellationToken);
        var missing = Path.Combine(_root, "MiniLoaderAll.bin");
        var config = await WriteConfigAsync("missing-loader-table.cfg",
            new RockchipConfigEntry { Name = RockchipConfigFile.LoaderRowName, Path = missing, Selected = true },
            new RockchipConfigEntry { Name = RockchipConfigFile.ParameterRowName, Path = parameter, Selected = true },
            new RockchipConfigEntry { Name = "boot", Path = string.Empty, Selected = true });
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new RecordingUnifiedLog());

        await model.ImportRkConfigAsync(config);
        await model.SpecialFileRowCheck;

        Assert.Null(model.EffectiveRkLoaderPath);
        Assert.Equal(parameter, model.RkParameterTablePath());
    }

    [Theory]
    [InlineData("", 0L)]
    [InlineData("0x00004000", 0x4000L)]
    [InlineData("0x100", 0x100L)]
    public void RkRowAddress_RowOffset_ResolvesToTheStartLba(string offset, long expected)
    {
        var row = new FlashPartitionEntry { Offset = offset };

        Assert.Equal(expected, FlashViewModel.RkRowAddress(row));
    }

    [Theory]
    [InlineData("EMMC", 1)]
    [InlineData("SPINOR", 9)]
    [InlineData("SD", 2)]
    [InlineData("", null)]
    [InlineData("unknown", null)]
    public void RkRowStorageId_RowMedium_ResolvesToTheProtocolIdentifier(string medium, int? expected)
    {
        var row = new FlashPartitionEntry { StorageMedium = medium };

        Assert.Equal(expected, FlashViewModel.RkRowStorageId(row));
    }

    [Fact]
    public void FlashModeAllowed_RockchipTarget_NeedsAnAddressingSource()
    {
        Assert.True(FlashViewModel.FlashModeAllowed(DeviceProtocolKind.Rkdl, flatBin: false,
            hasProgrammer: false, rkTargetResolved: true), "a resolved table or the forced-address switch must be enough");
        Assert.False(FlashViewModel.FlashModeAllowed(DeviceProtocolKind.Rkdl, flatBin: false,
            hasProgrammer: false, rkTargetResolved: false), "without an addressing source nothing can be written");
        Assert.True(FlashViewModel.FlashModeAllowed(DeviceProtocolKind.Fastboot, flatBin: false,
            hasProgrammer: false, rkTargetResolved: false));
    }

    [Fact]
    public void IsWritableEntry_ImageAndContainerRows_StayWritable()
    {
        Assert.True(FlashViewModel.IsWritableEntry(new FlashPartitionEntry { Command = "flash" }));
        Assert.True(FlashViewModel.IsWritableEntry(new FlashPartitionEntry { Command = "erase" }));
        Assert.True(FlashViewModel.IsWritableEntry(new FlashPartitionEntry()));
        Assert.False(FlashViewModel.IsWritableEntry(new FlashPartitionEntry { Command = "loader" }));
        Assert.False(FlashViewModel.IsWritableEntry(new FlashPartitionEntry { Command = "parameter" }));
    }

    private async Task<string> WriteConfigAsync(string name, params RockchipConfigEntry[] entries)
    {
        var path = Path.Combine(_root, name);
        RockchipConfigFile.Save(path, RockchipConfigFile.CreateHeader(), entries);
        await File.WriteAllTextAsync(path, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        return path;
    }

    private static int CountReportedLines(string log, string template)
    {
        var marker = template.Split("{0}")[0];
        return log.Split('\n').Count(line => line.Contains(marker, StringComparison.Ordinal));
    }

    private static byte[] BuildLoader()
    {
        string[] names = ["ddr800", "usbplug", "FlashData"];
        byte[][] payloads = [[1, 2, 3, 4], [5, 6, 7], [8, 9, 10, 11, 12]];
        var tableBytes = names.Length * EntrySize;
        var payloadBytes = payloads.Sum(static payload => payload.Length);
        var image = new byte[HeaderSize + tableBytes + payloadBytes + 4];

        Encoding.ASCII.GetBytes("BOOT").CopyTo(image, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(4), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(6), 0x010B);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(10), 0x01030000);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(14), 2024);
        image[16] = 3;
        image[17] = 12;
        image[18] = 9;
        image[19] = 30;
        image[20] = 15;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(21), 0x33353838);
        image[43] = (byte)'S';
        image[44] = 1;

        var offsets = new[] { HeaderSize, HeaderSize + EntrySize, HeaderSize + (2 * EntrySize) };
        var counts = new byte[] { 1, 1, 1 };
        image[25] = counts[0];
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(26), (uint)offsets[0]);
        image[30] = EntrySize;
        image[31] = counts[1];
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32), (uint)offsets[1]);
        image[36] = EntrySize;
        image[37] = counts[2];
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(38), (uint)offsets[2]);
        image[42] = EntrySize;

        var dataOffset = HeaderSize + tableBytes;
        for (var index = 0; index < names.Length; index++)
        {
            var entryOffset = HeaderSize + (index * EntrySize);
            image[entryOffset] = EntrySize;
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entryOffset + 1), index == 2 ? 4u : 1u);
            Encoding.Unicode.GetBytes(names[index]).CopyTo(image, entryOffset + 5);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entryOffset + 45), (uint)dataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entryOffset + 49), (uint)payloads[index].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entryOffset + 53), index == 0 ? 1u : 0u);
            payloads[index].CopyTo(image, dataOffset);
            dataOffset += payloads[index].Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(image.Length - 4),
            RockchipCrc32.Compute(image.AsSpan(0, image.Length - 4)));
        return image;
    }

    private sealed class RecordingUnifiedLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message)
        {
        }

        public void Write(LogLevel level, string source, string format, params object[] args)
        {
        }

        public void SetMinimumLevel(LogLevel level)
        {
        }
    }
}