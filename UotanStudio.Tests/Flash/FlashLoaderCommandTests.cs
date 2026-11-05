using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// Cases for the <c>loader</c> row carrying a different boot image per device: the row is parsed with the grammar the
/// selected device imposes, and an accepted file configures that device's backend.
/// <para>承载按设备而变的引导镜像的 <c>loader</c> 行用例：该行用所选设备施加的语法解析，被接受的文件配置该设备的后端。</para>
/// </summary>
public sealed class FlashLoaderCommandTests : IDisposable
{
    private const int ElfHeaderSize = 52;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-flash-loader-" + Guid.NewGuid().ToString("N"));

    public FlashLoaderCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <remarks>
    /// Other protocols name no boot-image grammar, so every grammar is tried instead of guessing one.
    /// <para>其余协议不声明引导镜像语法，因此逐种尝试而不是猜测。</para>
    /// </remarks>
    [Fact]
    public void ResolveLoaderTarget_BindsTheGrammarToTheDevice()
    {
        Assert.Equal(FlashViewModel.LoaderTarget.Rockchip,
            FlashViewModel.ResolveLoaderTarget(DeviceProtocolKind.Rkdl));
        Assert.Equal(FlashViewModel.LoaderTarget.EdlProgrammer,
            FlashViewModel.ResolveLoaderTarget(DeviceProtocolKind.Edl));
        Assert.Equal(FlashViewModel.LoaderTarget.MtkPreloader,
            FlashViewModel.ResolveLoaderTarget(DeviceProtocolKind.Mtk));

        Assert.Equal(FlashViewModel.LoaderTarget.Any,
            FlashViewModel.ResolveLoaderTarget(DeviceProtocolKind.Adb));
        Assert.Equal(FlashViewModel.LoaderTarget.Any,
            FlashViewModel.ResolveLoaderTarget(DeviceProtocolKind.Fastboot));
        Assert.Equal(FlashViewModel.LoaderTarget.Any, FlashViewModel.ResolveLoaderTarget(null));
    }

    [Fact]
    public async Task CheckLoaderFileAsync_MelfProgrammerForAnEdlDevice_IsAcceptedAndNamed()
    {
        var path = WriteFile("prog_firehose_ddr.melf", BuildMelf(components: 3));
        var log = new CapturingLog();
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, log);

        var verdict = await model.CheckLoaderFileAsync(path, FlashViewModel.LoaderTarget.EdlProgrammer,
            TestContext.Current.CancellationToken);

        Assert.True(verdict.Valid);
        Assert.Equal(FlashViewModel.LoaderTarget.EdlProgrammer, verdict.Target);
        Assert.Contains("MELF", verdict.Summary, StringComparison.Ordinal);
        Assert.Contains(log.Lines, line => line.Contains("MELF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckLoaderFileAsync_SingleElfAndMbn_AreAcceptedForAnEdlDevice()
    {
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new CapturingLog());
        var elf = WriteFile("prog_firehose_ddr.elf", BuildElf());
        var mbn = WriteFile("prog_firehose_ddr.mbn", BuildMbn());

        var elfVerdict = await model.CheckLoaderFileAsync(elf, FlashViewModel.LoaderTarget.EdlProgrammer,
            TestContext.Current.CancellationToken);
        var mbnVerdict = await model.CheckLoaderFileAsync(mbn, FlashViewModel.LoaderTarget.EdlProgrammer,
            TestContext.Current.CancellationToken);

        Assert.True(elfVerdict.Valid);
        Assert.Equal("ELF", elfVerdict.Summary);
        Assert.True(mbnVerdict.Valid);
        Assert.Equal("MBN", mbnVerdict.Summary);
    }

    [Fact]
    public async Task CheckLoaderFileAsync_RockchipLoaderForAnEdlDevice_IsRefusedByTheProgrammerGrammar()
    {
        var path = WriteFile("MiniLoaderAll.bin", "BOOT"u8.ToArray());
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new CapturingLog());

        var verdict = await model.CheckLoaderFileAsync(path, FlashViewModel.LoaderTarget.EdlProgrammer,
            TestContext.Current.CancellationToken);

        Assert.False(verdict.Valid);
        Assert.Equal("Flash_EdlLoaderNotProgrammer", verdict.ProblemKey);
    }

    [Fact]
    public async Task CheckLoaderFileAsync_ProgrammerForARockchipDevice_IsRefusedByTheLoaderGrammar()
    {
        var path = WriteFile("prog_firehose_ddr.melf", BuildMelf(components: 2));
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new CapturingLog());

        var verdict = await model.CheckLoaderFileAsync(path, FlashViewModel.LoaderTarget.Rockchip,
            TestContext.Current.CancellationToken);

        Assert.False(verdict.Valid);
        Assert.Equal("Flash_RkLoaderNotLoader", verdict.ProblemKey);
    }

    /// <remarks>
    /// A file the EDL grammar accepts is not also a Rockchip boot: only the matched grammar configures a backend.
    /// <para>被 EDL 语法接受的文件不应同时成为瑞芯微引导：只有匹配的那一种语法会配置后端。</para>
    /// </remarks>
    [Fact]
    public async Task ImportRkConfigAsync_MelfLoaderRowWithoutADevice_ConfiguresTheProgrammer()
    {
        var path = WriteFile("prog_firehose_ddr.melf", BuildMelf(components: 2));
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new CapturingLog());

        await model.ImportRkConfigAsync(await WriteConfigAsync("melf.cfg", path));
        await model.SpecialFileRowCheck;

        var row = Assert.Single(model.Partitions, entry => entry.Command == "loader");
        Assert.Equal(LanguageService.Instance.GetString("Flash_LoaderValid"), row.StatusText);
        Assert.Equal(path, model.EffectiveEdlProgrammer);

        Assert.Null(model.EffectiveRkLoaderPath);
    }

    [Fact]
    public async Task ImportRkConfigAsync_UnknownLoaderRowContent_ConfiguresNoBackend()
    {
        var path = WriteFile("unknown.bin", "not a boot image"u8.ToArray());
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new CapturingLog());

        await model.ImportRkConfigAsync(await WriteConfigAsync("unknown.cfg", path));
        await model.SpecialFileRowCheck;

        var row = Assert.Single(model.Partitions, entry => entry.Command == "loader");
        Assert.Equal(LanguageService.Instance.GetString("Flash_LoaderInvalidStatus"), row.StatusText);
        Assert.Null(model.EffectiveEdlProgrammer);
        Assert.Null(model.EffectiveRkLoaderPath);
    }

    [Fact]
    public async Task ImportRkConfigAsync_MissingLoaderRowFile_IsReportedWithoutABackend()
    {
        var missing = Path.Combine(_root, "absent.elf");
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new CapturingLog());

        await model.ImportRkConfigAsync(await WriteConfigAsync("missing.cfg", missing));
        await model.SpecialFileRowCheck;

        var row = Assert.Single(model.Partitions, entry => entry.Command == "loader");
        Assert.Equal(LanguageService.Instance.GetString("Flash_LoaderInvalidStatus"), row.StatusText);
        Assert.Null(model.EffectiveEdlProgrammer);
        Assert.Null(model.EffectiveRkLoaderPath);
    }

    /// <remarks>
    /// A multi-image manifest carries several Sahara image identifiers, not one programmer blob, so the row has to
    /// accept it as its own form instead of reporting an unknown boot image.
    /// <para>多镜像清单携带多个 Sahara 镜像标识而不是一个 programmer 块，因此该行必须把它当作自身形态接受，
    /// 而不是报告为未知引导文件。</para>
    /// </remarks>
    [Fact]
    public async Task CheckLoaderFileAsync_MultiImageManifestForAnEdlDevice_IsAcceptedWithTheImageCount()
    {
        var manifest = await WriteManifestAsync("qsahara_device_programmer.xml",
            ("13", WriteFile("prog_firehose_ddr.elf", BuildElf())),
            ("36", WriteFile("multi_image_qti.mbn", BuildMelf(components: 2))));
        var log = new CapturingLog();
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, log);

        var verdict = await model.CheckLoaderFileAsync(manifest, FlashViewModel.LoaderTarget.EdlProgrammer,
            TestContext.Current.CancellationToken);

        Assert.True(verdict.Valid);
        Assert.Equal(FlashViewModel.LoaderTarget.EdlProgrammer, verdict.Target);
        Assert.Contains("2", verdict.Summary, StringComparison.Ordinal);
        Assert.Contains(log.Lines, line => line.Contains("2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckLoaderFileAsync_ManifestWithMissingImage_IsRejectedAsAManifest()
    {
        var manifest = await WriteManifestAsync("broken.xml",
            ("13", Path.Combine(_root, "absent_programmer.elf")));
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new CapturingLog());

        var verdict = await model.CheckLoaderFileAsync(manifest, FlashViewModel.LoaderTarget.EdlProgrammer,
            TestContext.Current.CancellationToken);

        Assert.False(verdict.Valid);
        Assert.Equal("Flash_EdlLoaderManifestInvalid", verdict.ProblemKey);
    }

    [Fact]
    public async Task ImportRkConfigAsync_ManifestLoaderRowWithoutADevice_ConfiguresTheProgrammer()
    {
        var manifest = await WriteManifestAsync("multi.xml",
            ("13", WriteFile("prog_firehose_ddr.elf", BuildElf())),
            ("21", WriteFile("xbl_sc.elf", BuildElf())));
        var model = new FlashViewModel(FlashService.Default, LanguageService.Instance, new CapturingLog());

        await model.ImportRkConfigAsync(await WriteConfigAsync("manifest.cfg", manifest));
        await model.SpecialFileRowCheck;

        var row = Assert.Single(model.Partitions, entry => entry.Command == "loader");
        Assert.Equal(LanguageService.Instance.GetString("Flash_LoaderManifestValid"), row.StatusText);
        Assert.Equal(manifest, model.EffectiveEdlProgrammer);
        Assert.Null(model.EffectiveRkLoaderPath);
    }

    private async Task<string> WriteManifestAsync(string name, params (string ImageId, string ImagePath)[] images)
    {
        var path = Path.Combine(_root, name);
        var lines = new List<string>
        {
            "<?xml version=\"1.0\" ?>",
            "<sahara_config>",
            "\t<chipset>test</chipset>",
            "\t<images>"
        };
        lines.AddRange(images.Select(image =>
            $"\t\t<image image_id=\"{image.ImageId}\" image_path=\"{image.ImagePath}\" />"));
        lines.Add("\t</images>");
        lines.Add("</sahara_config>");
        await File.WriteAllLinesAsync(path, lines, TestContext.Current.CancellationToken);
        return path;
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private async Task<string> WriteConfigAsync(string name, string loaderPath)
    {
        var path = Path.Combine(_root, name);
        RockchipConfigFile.Save(path, RockchipConfigFile.CreateHeader(),
        [
            new RockchipConfigEntry { Name = RockchipConfigFile.LoaderRowName, Path = loaderPath, Selected = true },
            new RockchipConfigEntry { Name = "boot", Path = string.Empty, Selected = true }
        ]);
        await File.WriteAllTextAsync(path, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        return path;
    }

    /// <summary>
    /// Builds a scattered multi-ELF programmer: components separated by padding, the first one not at offset zero,
    /// which is the layout this row has to accept as a container rather than as an unknown file.
    /// <para>构造零散的多 ELF programmer：组件之间由填充隔开，首个组件不在偏移零处，正是该行必须当作容器而不是未知
    /// 文件接受的布局。</para>
    /// </summary>
    /// <param name="components">Component count to place.<para>要放置的组件数。</para></param>
    private static byte[] BuildMelf(int components)
    {
        const int prefix = 96;
        const int stride = 512;
        var image = new byte[prefix + (components * stride)];
        for (var index = 0; index < components; index++)
        {
            BuildElf().CopyTo(image, prefix + (index * stride));
        }

        return image;
    }

    /// <summary>
    /// Builds the smallest ELF header the reader accepts: 32-bit little endian, no program headers and no section
    /// headers, which is all a shape probe needs.
    /// <para>构造读取器能接受的最小 ELF 头：32 位小端、无程序头与节头，形态探测只需要这些。</para>
    /// </summary>
    private static byte[] BuildElf()
    {
        var header = new byte[ElfHeaderSize];
        "\u007fELF"u8.CopyTo(header);
        header[4] = 1;
        header[5] = 1;
        header[6] = 1;
        header[16] = 2;
        header[18] = 40;
        header[20] = 1;
        header[40] = ElfHeaderSize;
        return header;
    }

    /// <summary>
    /// Builds a minimal version 3 MBN whose declared length and hash length both cover the trailing hash table.
    /// <para>构造最小版本 3 MBN，其声明长度与哈希长度都正好覆盖尾部哈希表。</para>
    /// </summary>
    private static byte[] BuildMbn()
    {
        const int hashLength = 32;
        var image = new byte[40 + hashLength];
        BitConverter.TryWriteBytes(image.AsSpan(4), 3u);
        BitConverter.TryWriteBytes(image.AsSpan(16), (uint)hashLength);
        BitConverter.TryWriteBytes(image.AsSpan(20), (uint)hashLength);
        return image;
    }

    /// <summary>
    /// Collects the log lines a check writes, so a verdict can be asserted to reach the log rather than only the row.
    /// <para>收集检查写出的日志行，使结论可以被断言为到达日志而不只到达行。</para>
    /// </summary>
    private sealed class CapturingLog : IUnifiedLog
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines => _lines;

        public void Write(LogLevel level, string source, string message) => _lines.Add(message);

        public void Write(LogLevel level, string source, string format, params object[] args)
            => _lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));

        public void SetMinimumLevel(LogLevel level)
        {
        }
    }
}