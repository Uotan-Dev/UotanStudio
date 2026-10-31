using System.Buffers.Binary;
using System.Text;
using UotanStudio.Hash.RockchipCrc32;
using UotanStudio.Services;
using UotanStudio.Services.Flash;
using UotanStudio.Services.Logging;
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
        Assert.True(loaderRow.CanSelect);
        Assert.Equal(loader, model.EffectiveRkLoaderPath);
        Assert.Equal(LanguageService.Instance.GetString("Flash_RkLoaderValid"), loaderRow.StatusText);
        Assert.False(FlashViewModel.IsWritableEntry(loaderRow));
    }

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
        Assert.True(loaderRow.CanSelect);
        Assert.Equal(LanguageService.Instance.GetString("Flash_RkLoaderInvalidStatus"), loaderRow.StatusText);
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
        Assert.True(parameterRow.CanSelect);
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
        Assert.Equal(LanguageService.Instance.GetString("Flash_RkLoaderInvalidStatus"), loaderRow.StatusText);
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