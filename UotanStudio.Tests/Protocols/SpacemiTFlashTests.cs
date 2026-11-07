using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Compress;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Parsing and planning checks for the SpacemiT K1/K3 flash module: size strings, partition
/// tables, fastboot.yaml scripts, guard expressions and package resolution. No device is
/// contacted; every fixture lives under %TEMP% and is removed afterwards.
/// <para>SpacemiT K1/K3 烧录模块的解析与规划校验：尺寸字符串、分区表、fastboot.yaml 脚本、
/// 守卫表达式与包内解析。不接触设备；所有夹具位于 %TEMP% 并在使用后删除。</para>
/// </summary>
public sealed class SpacemiTFlashTests : IDisposable
{
    private readonly string _root;

    public SpacemiTFlashTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"uotan-spacemit-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Theory]
    [InlineData("640K", 655_360)]
    [InlineData("4M", 4 * 1024 * 1024)]
    [InlineData("2G", 2L * 1024 * 1024 * 1024)]
    [InlineData("512", 512)]
    [InlineData("2m", 2 * 1024 * 1024)]
    [InlineData("80B", 80)]
    [InlineData("80b", 80)]
    [InlineData("0B", 0)]
    [InlineData("-", -1)]
    public void SizeParser_ParsesVendorSizeStrings(string text, long expected)
    {
        Assert.Equal(expected, SpacemiTSizeParser.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12X")]
    [InlineData("-4M")]
    [InlineData("K")]
    [InlineData("9999999999999999999")]
    public void SizeParser_RejectsMalformedStrings(string text)
    {
        Assert.ThrowsAny<FormatException>(() => SpacemiTSizeParser.Parse(text));
    }

    [Fact]
    public void PartitionTable_ParsesGptEntriesWithSingleAndListImages()
    {
        const string json = """
        {
          "version": "1.0",
          "format": "gpt",
          "partitions": [
            { "name": "bootinfo", "hidden": true, "offset": "0K", "size": "512", "image": "factory/bootinfo_sd.bin" },
            { "name": "bootfs", "offset": "4M", "size": "256M", "image": ["a.img", "b.img"], "compress": "gzip-5" },
            { "name": "rootfs", "offset": "260M", "size": "-", "image": "rootfs.ext4" }
          ]
        }
        """;

        var table = SpacemiTPartitionTable.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("gpt", table.Format);
        Assert.Equal(3, table.Partitions.Count);
        Assert.True(table.Partitions[0].Hidden);
        Assert.Equal(512, table.Partitions[0].Size);
        Assert.Equal(["a.img", "b.img"], table.Partitions[1].Images);
        Assert.Equal("gzip-5", table.Partitions[1].Compress);
        Assert.Equal(-1, table.Partitions[2].Size);
    }

    [Fact]
    public void PartitionTable_RejectsMalformedJson()
    {
        Assert.Throws<InvalidDataException>(() =>
            SpacemiTPartitionTable.Parse(new MemoryStream(Encoding.UTF8.GetBytes("{\"format\":\"gpt\"}"))));
        Assert.Throws<InvalidDataException>(() =>
            SpacemiTPartitionTable.Parse(new MemoryStream(Encoding.UTF8.GetBytes("[]"))));
    }

    [Fact]
    public void FlashScript_ParsesVendorK3Actions()
    {
        const string yaml = """
        version: 1.0
        support:
          - 'k3'
        actions:
          - getvar:
              args: 'version-brom'
              set: 'version'
              skip_fail: true
              timeout:
                seconds: 1
          - stage:
              file: 'factory/FSBL.bin'
              skip_when: "not temp.version"
              timeout:
                minutes: 2
          - continue:
              skip_when: "not temp.version"
          - oem:
              args: 'speed:super-speed'
              skip_when: "not temp.version"
              skip_fail: true
          - multi_flash:
              timeout:
                minutes: 10
              retry: 3
              relate_partition: ['partition_{size0}.json', 'partition_{size1}.json']
        """;

        var script = SpacemiTFlashScript.Parse(new MemoryStream(Encoding.UTF8.GetBytes(yaml)));
        Assert.Equal("1.0", script.Version);
        Assert.Equal(["k3"], script.Support);
        Assert.Equal(5, script.Actions.Count);

        var getVar = Assert.IsType<SpacemiTGetVarAction>(script.Actions[0]);
        Assert.Equal("version-brom", getVar.Variable);
        Assert.Equal("version", getVar.BagKey);
        Assert.True(getVar.SkipFail);
        Assert.Equal(TimeSpan.FromSeconds(1), getVar.Timeout);

        var stage = Assert.IsType<SpacemiTStageAction>(script.Actions[1]);
        Assert.Equal("factory/FSBL.bin", stage.File);
        Assert.Equal("not temp.version", stage.SkipWhen);

        Assert.IsType<SpacemiTContinueAction>(script.Actions[2]);

        var oem = Assert.IsType<SpacemiTOemAction>(script.Actions[3]);
        Assert.Equal("speed:super-speed", oem.Args);

        var multi = Assert.IsType<SpacemiTMultiFlashAction>(script.Actions[4]);
        Assert.Equal(["partition_{size0}.json", "partition_{size1}.json"], multi.RelatePartition);
        Assert.Equal(3, multi.Retry);
    }

    [Fact]
    public void FlashScript_RejectsUnknownActionAndMissingActions()
    {
        Assert.Throws<InvalidDataException>(() => SpacemiTFlashScript.Parse(new MemoryStream(
            Encoding.UTF8.GetBytes("actions:\n  - magic:\n      args: x\n"))));
        Assert.Throws<InvalidDataException>(() => SpacemiTFlashScript.Parse(new MemoryStream(
            Encoding.UTF8.GetBytes("version: 1.0\n"))));
    }

    [Fact]
    public void FlashScript_BootChainEnd_EndsPastLastContinue()
    {
        // The vendor K3 shape: two staging phases separated by continues, with follow-up actions after the last one.
        // 厂商 K3 形态：两个由 continue 分隔的暂存阶段，最后一个 continue 之后还有后续动作。
        var script = SpacemiTFlashScript.Parse(new MemoryStream(Encoding.UTF8.GetBytes("""
        actions:
          - getvar:
              args: 'version-brom'
          - stage:
              file: 'factory/FSBL.bin'
          - continue: {}
          - stage:
              file: 'u-boot.itb'
          - continue: {}
          - oem:
              args: 'ec:flash'
        """)));
        Assert.Equal(5, script.BootChainEnd);
        Assert.IsType<SpacemiTContinueAction>(script.Actions[script.BootChainEnd - 1]);

        var withoutContinue = SpacemiTFlashScript.Parse(new MemoryStream(Encoding.UTF8.GetBytes("""
        actions:
          - getvar:
              args: 'mtd-size'
          - multi_flash:
              relate_partition: ['partition_{size1}.json']
        """)));
        Assert.Equal(0, withoutContinue.BootChainEnd);
    }

    [Fact]
    public void ConditionEvaluator_TruthinessAndNegation()
    {
        var vars = new Dictionary<string, string> { ["temp.version"] = "1.0" };
        Assert.False(SpacemiTConditionEvaluator.ShouldSkip("not temp.version", vars));
        Assert.True(SpacemiTConditionEvaluator.ShouldSkip("temp.version", vars));
        Assert.False(SpacemiTConditionEvaluator.ShouldSkip(null, vars));

        var empty = new Dictionary<string, string>();
        Assert.False(SpacemiTConditionEvaluator.ShouldSkip("temp.version", empty));
        Assert.True(SpacemiTConditionEvaluator.ShouldSkip("not temp.version", empty));

        Assert.Throws<InvalidDataException>(() =>
            SpacemiTConditionEvaluator.ShouldSkip("temp.version and temp.other", vars));
    }

    [Fact]
    public void Package_ResolvesFilesByRelativeAndBasenameAndRejectsEscapes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "factory"));
        File.WriteAllText(Path.Combine(_root, "factory", "FSBL.bin"), "fsbl");
        File.WriteAllText(Path.Combine(_root, "u-boot.itb"), "uboot");

        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), "actions:\n  - continue: {}\n");
        var package = SpacemiTFlashPackage.Load(_root);

        Assert.NotNull(package.ResolveFile("factory/FSBL.bin", TestContext.Current.CancellationToken));
        Assert.NotNull(package.ResolveFile("u-boot.itb", TestContext.Current.CancellationToken));
        Assert.Null(package.ResolveFile("/partition_.+\\.json$/", TestContext.Current.CancellationToken));
        Assert.Null(package.ResolveFile("missing.bin", TestContext.Current.CancellationToken));
        Assert.Null(package.ResolveFile("../outside.bin", TestContext.Current.CancellationToken));
    }

    /// <remarks>
    /// Regex image references match against package-root-relative paths with forward slashes, so
    /// files nested in subdirectories are reachable and the result is ordered by path.
    /// <para>正则镜像引用对统一 '/' 分隔的包根相对路径匹配，因此子目录中的文件可达，且结果按路径有序。</para>
    /// </remarks>
    [Fact]
    public void Package_ResolveImageMatches_MatchesRelativePathsAndRejectsInvalidPatterns()
    {
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), "actions:\n  - continue: {}\n");
        File.WriteAllText(Path.Combine(_root, "images", "boot_1.img"), "one");
        File.WriteAllText(Path.Combine(_root, "images", "boot_2.img"), "two");
        File.WriteAllText(Path.Combine(_root, "other.bin"), "other");
        var package = SpacemiTFlashPackage.Load(_root);

        Assert.Equal(
            [Path.Combine(_root, "images", "boot_1.img"), Path.Combine(_root, "images", "boot_2.img")],
            package.ResolveImageMatches("/boot_.+\\.img/", TestContext.Current.CancellationToken));
        var single = Assert.Single(package.ResolveImageMatches("/images/boot_1\\.img/", TestContext.Current.CancellationToken));
        Assert.Equal(Path.Combine(_root, "images", "boot_1.img"), single);
        Assert.Empty(package.ResolveImageMatches("/missing_.*\\.bin/", TestContext.Current.CancellationToken));
        Assert.Equal([Path.Combine(_root, "other.bin")], package.ResolveImageMatches("other.bin", TestContext.Current.CancellationToken));
        Assert.Empty(package.ResolveImageMatches("missing.bin", TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => package.ResolveImageMatches("/[unclosed/", TestContext.Current.CancellationToken));
    }

    /// <remarks>
    /// <see cref="SpacemiTFlashPackage.ResolveFile"/> only resolves a regex reference when exactly
    /// one package file matches; ambiguity returns null so callers cannot flash the wrong file.
    /// <para><see cref="SpacemiTFlashPackage.ResolveFile"/> 仅在正则引用恰好命中一个包内文件时解析；
    /// 歧义时返回 null，避免调用方刷错文件。</para>
    /// </remarks>
    [Fact]
    public void Package_ResolveFile_ResolvesUniqueRegexMatchAndNullForAmbiguityOrMiss()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), "actions:\n  - continue: {}\n");
        File.WriteAllText(Path.Combine(_root, "boot_1.img"), "one");
        var package = SpacemiTFlashPackage.Load(_root);

        Assert.Equal(Path.Combine(_root, "boot_1.img"), package.ResolveFile("/boot_.+\\.img/", TestContext.Current.CancellationToken));

        File.WriteAllText(Path.Combine(_root, "boot_2.img"), "two");
        Assert.Equal(2, package.ResolveImageMatches("/boot_.+\\.img/", TestContext.Current.CancellationToken).Count);
        Assert.Null(package.ResolveFile("/boot_.+\\.img/", TestContext.Current.CancellationToken));
    }

    /// <remarks>
    /// A regex image reference is untrusted package text, so an over-long pattern is rejected before
    /// it reaches the regex engine instead of being allowed to stall resolution.
    /// <para>正则镜像引用来自不可信包文本，因此超长模式在到达正则引擎之前即被拒绝，避免拖垮解析。</para>
    /// </remarks>
    [Fact]
    public void Package_ResolveImageMatches_RejectsOversizedReference()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), "actions:\n  - continue: {}\n");
        var package = SpacemiTFlashPackage.Load(_root);

        Assert.Throws<InvalidDataException>(() =>
            package.ResolveImageMatches("/" + new string('a', 300) + "/", TestContext.Current.CancellationToken));
    }

    /// <remarks>
    /// Regex image resolution enumerates package files and runs a bounded match per file, so a
    /// cancelled token must abort it before any enumeration work begins.
    /// <para>正则镜像解析会枚举包内文件并逐文件做有界匹配，因此已取消的令牌必须在任何枚举工作开始前中止它。</para>
    /// </remarks>
    [Fact]
    public void Package_ResolveImageMatches_HonorsCancellation()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), "actions:\n  - continue: {}\n");
        File.WriteAllText(Path.Combine(_root, "boot_1.img"), "one");
        var package = SpacemiTFlashPackage.Load(_root);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            package.ResolveImageMatches("/boot_.+img/", cts.Token));
    }

    [Fact]
    public void Package_ResolvesPartitionTableWithHalvingFallback()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), "actions:\n  - continue: {}\n");
        File.WriteAllText(Path.Combine(_root, "partition_2M.json"), "{\"format\":\"mtd\",\"partitions\":[{\"name\":\"env\",\"offset\":\"384K\",\"size\":\"64K\"}]}");
        var package = SpacemiTFlashPackage.Load(_root);

        Assert.NotNull(package.ResolvePartitionTable("2M"));
        Assert.NotNull(package.ResolvePartitionTable("4M"));
        Assert.NotNull(package.ResolvePartitionTable("16M"));
        Assert.Null(package.ResolvePartitionTable("universal"));
        Assert.Null(package.ResolvePartitionTable("NULL"));
        Assert.Null(package.ResolvePartitionTable(null));
    }

    [Fact]
    public void Flasher_RejectsUnconfirmedOptionsBeforeAnyIO()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), "actions:\n  - continue: {}\n");
        var package = SpacemiTFlashPackage.Load(_root);
        var flasher = new SpacemiTFlasher(package, new SpacemiTFlashOptions());

        Assert.Throws<InvalidOperationException>(() =>
            flasher.Execute(
                new FastbootSession(new ThrowingTransport()),
                _ => new FastbootSession(new ThrowingTransport()),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Flasher_ExecutesStagingWhenBromVariableAnswers()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'version-brom'
              set: 'version'
              skip_fail: true
              timeout:
                seconds: 1
          - stage:
              file: 'factory/FSBL.bin'
              skip_when: "not temp.version"
              timeout:
                minutes: 2
        """);
        Directory.CreateDirectory(Path.Combine(_root, "factory"));
        var fsbl = Encoding.ASCII.GetBytes("FSBL-PAYLOAD");
        File.WriteAllBytes(Path.Combine(_root, "factory", "FSBL.bin"), fsbl);

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:version-brom"] = "OKAY1.0",
        });
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("getvar:version-brom", transport.Commands);
        Assert.Contains(transport.Commands, command => command.StartsWith("download:", StringComparison.Ordinal));
        var download = Assert.Single(transport.Downloads);
        Assert.Equal(fsbl, download);
    }

    [Fact]
    public void Flasher_SkipsStagingWhenBromVariableFails()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'version-brom'
              set: 'version'
              skip_fail: true
              timeout:
                seconds: 1
          - stage:
              file: 'factory/FSBL.bin'
              skip_when: "not temp.version"
              timeout:
                minutes: 2
        """);
        Directory.CreateDirectory(Path.Combine(_root, "factory"));
        File.WriteAllBytes(Path.Combine(_root, "factory", "FSBL.bin"), "FSBL-PAYLOAD"u8.ToArray());

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:version-brom"] = "FAILVariable not implemented",
        });
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(transport.Commands, command => command.StartsWith("download:", StringComparison.Ordinal));
    }

    [Fact]
    public void Flasher_MultiFlashSkipsAbsentStorageAndFlashesPresentTable()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - getvar:
              args: 'blk-size'
              set_var: 'size1'
          - multi_flash:
              retry: 3
              relate_partition: ['partition_{size0}.json', 'partition_{size1}.json']
        """);
        const string tableJson = """
        {
          "version": "1.0",
          "format": "gpt",
          "partitions": [
            { "name": "env", "offset": "0", "size": "64K", "image": "env.bin" }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(_root, "partition_universal.json"), tableJson);
        var env = Encoding.ASCII.GetBytes("ENV-PAYLOAD");
        File.WriteAllBytes(Path.Combine(_root, "env.bin"), env);

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAYNULL",
            ["getvar:blk-size"] = "OKAYuniversal",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:gpt"] = "OKAYparse gpt table okay",
            ["flash:env"] = "OKAY",
        });
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("flash:mtd", transport.Commands);
        Assert.Contains("flash:gpt", transport.Commands);
        Assert.Contains("flash:env", transport.Commands);
        Assert.Equal(2, transport.Downloads.Count);
        Assert.Equal(tableJson, Encoding.UTF8.GetString(transport.Downloads[0]));
        Assert.Equal(env, transport.Downloads[1]);
    }

    [Fact]
    public void Flasher_PartitionWithoutPackageImage_FlashesHostPickedOverride()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - getvar:
              args: 'blk-size'
              set_var: 'size1'
          - multi_flash:
              retry: 3
              relate_partition: ['partition_{size0}.json', 'partition_{size1}.json']
        """);
        // The table places misc but names no image — the shape of a reserved slot such as the vendor's misc.
        // 分区表只给出 misc 的位置而不给镜像——正是厂商 misc 这类保留槽位的形态。
        File.WriteAllText(Path.Combine(_root, "partition_universal.json"), """
        {
          "version": "1.0",
          "format": "gpt",
          "partitions": [
            { "name": "misc", "offset": "268M", "size": "4M" }
          ]
        }
        """);
        var misc = Encoding.ASCII.GetBytes("MISC-USER-IMAGE");
        var overridePath = Path.Combine(_root, "misc.img");
        File.WriteAllBytes(overridePath, misc);

        // Without an override the image-less entry is skipped: the table write alone created the partition.
        // 无覆盖时跳过无镜像条目：仅写表时该分区只是被创建。
        var plainTransport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAYNULL",
            ["getvar:blk-size"] = "OKAYuniversal",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:gpt"] = "OKAY",
        });
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            new FastbootSession(plainTransport), _ => new FastbootSession(plainTransport),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain("flash:misc", plainTransport.Commands);

        // With the host-picked override the run writes that file to the partition.
        // 携带宿主选择的覆盖时，运行把该文件写入分区。
        var overrideOptions = ConfirmedOptions();
        overrideOptions.PartitionImageOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["misc"] = overridePath,
        };
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAYNULL",
            ["getvar:blk-size"] = "OKAYuniversal",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:gpt"] = "OKAY",
            ["flash:misc"] = "OKAY",
        });
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), overrideOptions).Execute(
            new FastbootSession(transport), _ => new FastbootSession(transport),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("flash:gpt", transport.Commands);
        Assert.Contains("flash:misc", transport.Commands);
        Assert.Equal(2, transport.Downloads.Count);
        Assert.Equal(misc, transport.Downloads[1]);
    }

    [Fact]
    public void Flasher_MultiFlashFailsWhenNoTableMatches()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - getvar:
              args: 'blk-size'
              set_var: 'size1'
          - multi_flash:
              retry: 3
              relate_partition: ['partition_{size0}.json', 'partition_{size1}.json']
        """);

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY8M",
            ["getvar:blk-size"] = "OKAYNULL",
        });
        var session = new FastbootSession(transport);
        Assert.Throws<FastbootException>(() =>
            new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
                session, _ => session, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Flasher_FailsOnMissingPartitionImage()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - multi_flash:
              relate_partition: ['partition_{size0}.json']
        """);
        File.WriteAllText(Path.Combine(_root, "partition_4M.json"),
            "{\"format\":\"mtd\",\"partitions\":[{\"name\":\"env\",\"offset\":\"0\",\"size\":\"64K\",\"image\":\"env.bin\"}]}");

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
        });
        var session = new FastbootSession(transport);
        Assert.Throws<FastbootException>(() =>
            new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
                session, _ => session, cancellationToken: TestContext.Current.CancellationToken));
        Assert.DoesNotContain("flash:mtd", transport.Commands);
    }

    [Fact]
    public void PartitionTable_RejectsUnknownFormat()
    {
        Assert.Throws<InvalidDataException>(() => SpacemiTPartitionTable.Parse(new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"format\":\"GPT\",\"partitions\":[{\"name\":\"env\"}]}"))));
        Assert.Throws<InvalidDataException>(() => SpacemiTPartitionTable.Parse(new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"format\":\"custom\",\"partitions\":[{\"name\":\"env\"}]}"))));
    }

    [Fact]
    public void PartitionTable_ParsesDeviceStyleHiddenFlag()
    {
        var table = SpacemiTPartitionTable.Parse(new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"format\":\"gpt\",\"partitions\":[" +
            "{\"name\":\"a\",\"hidden\":\"true\"}," +
            "{\"name\":\"b\",\"hidden\":\"false\"}," +
            "{\"name\":\"c\",\"hidden\":1}," +
            "{\"name\":\"d\",\"hidden\":true}]}")));
        Assert.True(table.Partitions[0].Hidden);
        Assert.False(table.Partitions[1].Hidden);
        Assert.False(table.Partitions[2].Hidden);
        Assert.True(table.Partitions[3].Hidden);
    }

    /// <remarks>
    /// Mirrors the device parser's optional-field handling: string values are trimmed, non-string
    /// and blank values are dropped, and a wrong-typed <c>volume_images</c> yields no volumes.
    /// <para>镜像设备端可选字段的处理方式：字符串值去除首尾空白，非字符串与空白值被丢弃，类型不符的
    /// <c>volume_images</c> 得到空卷表。</para>
    /// </remarks>
    [Fact]
    public void PartitionTable_ParsesVolumeImagesAndToleratesWrongTypes()
    {
        const string json = """
        {
          "format": "mtd",
          "partitions": [
            { "name": "user", "offset": "0", "size": "-",
              "volume_images": { "bootfs": " bootfs.img ", "rootfs": "rootfs.img", "bad": 3, "blank": "   " } },
            { "name": "plain", "volume_images": "not-an-object" },
            { "name": "absent" }
          ]
        }
        """;

        var table = SpacemiTPartitionTable.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        var volumes = table.Partitions[0].VolumeImages;
        Assert.Equal(2, volumes.Count);
        Assert.Equal("bootfs.img", volumes["bootfs"]);
        Assert.Equal("rootfs.img", volumes["rootfs"]);
        Assert.Empty(table.Partitions[1].VolumeImages);
        Assert.Empty(table.Partitions[2].VolumeImages);
    }

    [Fact]
    public void PartitionTable_RejectsVolumeImagesAboveLimit()
    {
        var volumes = string.Join(",", Enumerable.Range(0, 65).Select(i => $"\"v{i}\":\"a{i}.img\""));
        var json = $"{{\"format\":\"mtd\",\"partitions\":[{{\"name\":\"user\",\"volume_images\":{{{volumes}}}}}]}}";

        Assert.Throws<InvalidDataException>(() =>
            SpacemiTPartitionTable.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json))));
    }

    [Fact]
    public void FlashScript_RejectsOversizedScript()
    {
        var oversized = new MemoryStream(new byte[1024 * 1024 + 1]);
        Assert.Throws<InvalidDataException>(() => SpacemiTFlashScript.Parse(oversized));
    }

    [Fact]
    public void FlashScript_TrimsBagKeysAndTreatsBlankAsNull()
    {
        var script = SpacemiTFlashScript.Parse(new MemoryStream(Encoding.UTF8.GetBytes("""
        actions:
          - getvar:
              args: 'version'
              set: '  version  '
          - getvar:
              args: 'other'
              set: ''
          - reboot:
              target: '  '
        """)));
        var first = Assert.IsType<SpacemiTGetVarAction>(script.Actions[0]);
        Assert.Equal("version", first.BagKey);
        var second = Assert.IsType<SpacemiTGetVarAction>(script.Actions[1]);
        Assert.Null(second.BagKey);
        var reboot = Assert.IsType<SpacemiTRebootAction>(script.Actions[2]);
        Assert.Null(reboot.Target);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("NULL", true)]
    [InlineData("null", true)]
    [InlineData("Null", true)]
    [InlineData("2M", false)]
    [InlineData("universal", false)]
    public void Package_ClassifiesAbsentStorageAnswers(string? answer, bool expected)
    {
        Assert.Equal(expected, SpacemiTFlashPackage.IsAbsentStorageAnswer(answer));
    }

    [Fact]
    public void Flasher_SuperSpeedReEnumeratesEvenWithoutOkay()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - oem:
              args: 'speed:super-speed'
              skip_fail: true
        """);

        var reopens = 0;
        var transport = new ScriptedTransport(new Dictionary<string, string>());
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session,
            _ =>
            {
                reopens++;
                return new FastbootSession(transport);
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, reopens);
    }

    [Fact]
    public void Flasher_ContinueToleratesMissingOkay()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - continue: {}
        """);

        var reopens = 0;
        var transport = new ScriptedTransport(new Dictionary<string, string>());
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session,
            _ =>
            {
                reopens++;
                return new FastbootSession(transport);
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, reopens);
        Assert.Contains("continue", transport.Commands);
    }

    [Fact]
    public void Flasher_GetVarRetriesAgainstFreshSession()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'version'
              set: 'version'
              retry: 3
        """);

        var firstTransport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:version"] = "FAILnot ready",
        });
        var secondTransport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:version"] = "OKAY1.0",
        });
        var session = new FastbootSession(firstTransport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session,
            _ => new FastbootSession(secondTransport),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("getvar:version", firstTransport.Commands);
        Assert.Contains("getvar:version", secondTransport.Commands);
    }

    [Fact]
    public void Flasher_MultiFlashRetriesEachPartitionPerActionRetry()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - getvar:
              args: 'blk-size'
              set_var: 'size1'
          - multi_flash:
              retry: 3
              relate_partition: ['partition_{size0}.json', 'partition_{size1}.json']
        """);
        const string tableJson = """
        {
          "version": "1.0",
          "format": "gpt",
          "partitions": [
            { "name": "env", "offset": "0", "size": "64K", "image": "env.bin" }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(_root, "partition_universal.json"), tableJson);
        var env = Encoding.ASCII.GetBytes("ENV-PAYLOAD");
        File.WriteAllBytes(Path.Combine(_root, "env.bin"), env);

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAYNULL",
            ["getvar:blk-size"] = "OKAYuniversal",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:gpt"] = "OKAYparse gpt table okay",
            ["flash:env"] = "FAILwrite error",
        });
        var session = new FastbootSession(transport);
        Assert.Throws<FastbootException>(() =>
            new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
                session, _ => session, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(3, transport.Commands.Count(command => command == "flash:env"));
    }

    /// <summary>
    /// Writes the regex-image fixture: an mtd table whose "boot" partition references
    /// <c>/boot_.+\.img/</c> instead of a concrete file, plus the vendor two-action flow.
    /// <para>写入正则镜像夹具：mtd 表的 "boot" 分区以 <c>/boot_.+\.img/</c> 而非具体文件引用镜像，
    /// 并带厂商的双动作流程。</para>
    /// </summary>
    private void WriteRegexFlashFixture()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - multi_flash:
              relate_partition: ['partition_{size0}.json']
        """);
        File.WriteAllText(Path.Combine(_root, "partition_4M.json"),
            """{"format":"mtd","partitions":[{"name":"boot","offset":"0","size":"64K","image":"/boot_.+\\.img/"}]}""");
    }

    /// <remarks>
    /// A regex image reference that matches exactly one package file is now resolved and flashed
    /// instead of failing, which is what vendor packages such as the K1 speaker image rely on.
    /// <para>恰好命中一个包内文件的正则镜像引用现在会被解析并烧写而不是失败，这正是 K1 speaker 等
    /// 厂商包所依赖的行为。</para>
    /// </remarks>
    [Fact]
    public void Flasher_RegexImageReference_FlashesTheUniqueMatch()
    {
        WriteRegexFlashFixture();
        File.WriteAllText(Path.Combine(_root, "boot_k1.img"), "BOOT-IMAGE");

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:mtd"] = "OKAY",
            ["flash:boot"] = "OKAY",
        });
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("flash:mtd", transport.Commands);
        Assert.Contains("flash:boot", transport.Commands);
        var downloads = PairDownloadsWithPayloads(transport);
        Assert.Equal(2, downloads.Count);
        Assert.Equal("BOOT-IMAGE", Encoding.UTF8.GetString(downloads[1].Payload));
    }

    /// <remarks>
    /// Zero regex matches must keep the "missing from the package" failure semantics instead of
    /// silently dropping the partition.
    /// <para>正则零命中必须保持"missing from the package"的失败语义，而不是静默丢弃分区。</para>
    /// </remarks>
    [Fact]
    public void Flasher_RegexImageReference_FailsWhenNoPackageFileMatches()
    {
        WriteRegexFlashFixture();

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
        });
        var session = new FastbootSession(transport);
        var ex = Assert.Throws<FastbootException>(() =>
            new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
                session, _ => session, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("missing from the package", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Commands, command => command.StartsWith("flash:", StringComparison.Ordinal));
    }

    /// <remarks>
    /// Two or more regex matches make the target ambiguous; flashing either pick silently could
    /// brick the board, so the run fails with the partition, reference and match count.
    /// <para>命中两个及以上时目标存在歧义；静默任选一个刷写可能损坏板卡，因此运行失败并指明分区、
    /// 引用与命中数。</para>
    /// </remarks>
    [Fact]
    public void Flasher_RegexImageReference_FailsWhenMultiplePackageFilesMatch()
    {
        WriteRegexFlashFixture();
        File.WriteAllText(Path.Combine(_root, "boot_a.img"), "one");
        File.WriteAllText(Path.Combine(_root, "boot_b.img"), "two");

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
        });
        var session = new FastbootSession(transport);
        var ex = Assert.Throws<FastbootException>(() =>
            new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
                session, _ => session, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("matches 2 files", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'boot'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Commands, command => command.StartsWith("flash:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Writes the volume_images fixture: an mtd "user" partition whose
    /// <c>volume_images</c> maps "bootfs"/"rootfs" to images, with no plain image reference.
    /// <para>写入 volume_images 夹具：mtd 的 "user" 分区以 <c>volume_images</c> 把 "bootfs"/"rootfs"
    /// 映射到镜像，且没有普通镜像引用。</para>
    /// </summary>
    private void WriteVolumeImagesFixture()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - multi_flash:
              retry: 1
              relate_partition: ['partition_{size0}.json']
        """);
        File.WriteAllText(Path.Combine(_root, "partition_4M.json"), """
        {"format":"mtd","partitions":[{"name":"user","offset":"0","size":"-","volume_images":{"bootfs":"bootfs.img","rootfs":"rootfs.img"}}]}
        """);
        File.WriteAllBytes(Path.Combine(_root, "bootfs.img"), "BOOTFS-CONTENT"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_root, "rootfs.img"), "ROOTFS-CONTENT"u8.ToArray());
    }

    /// <remarks>
    /// The device flow writes NAND volumes as <c>fastboot flash &lt;partition&gt;-&lt;volume&gt;</c>,
    /// so each volume image must become a download+flash pair against the synthesized target name in
    /// table order.
    /// <para>设备端以 <c>fastboot flash &lt;分区&gt;-&lt;卷&gt;</c> 写 NAND 卷，因此每个卷镜像必须按表内
    /// 顺序以合成目标名成为一对 download+flash。</para>
    /// </remarks>
    [Fact]
    public void Flasher_VolumeImages_FlashEachVolumeAsSynthesizedTarget()
    {
        WriteVolumeImagesFixture();

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:mtd"] = "OKAY",
            ["flash:user-bootfs"] = "OKAY",
            ["flash:user-rootfs"] = "OKAY",
        });
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("flash:mtd", transport.Commands);
        Assert.Contains("flash:user-bootfs", transport.Commands);
        Assert.Contains("flash:user-rootfs", transport.Commands);
        var downloads = PairDownloadsWithPayloads(transport);
        Assert.Equal(3, downloads.Count);
        Assert.Equal("BOOTFS-CONTENT", Encoding.UTF8.GetString(downloads[1].Payload));
        Assert.Equal("ROOTFS-CONTENT", Encoding.UTF8.GetString(downloads[2].Payload));
    }

    /// <remarks>
    /// Volume targets are filtered by their synthesized <c>&lt;partition&gt;-&lt;volume&gt;</c> name,
    /// so OnlyPartitions can select a single volume of a multi-volume partition.
    /// <para>卷目标按合成的 <c>&lt;分区&gt;-&lt;卷&gt;</c> 名过滤，因此 OnlyPartitions 可以只选多卷分区中的
    /// 单个卷。</para>
    /// </remarks>
    [Fact]
    public void Flasher_VolumeImages_OnlyPartitionsLimitsToTheNamedVolume()
    {
        WriteVolumeImagesFixture();

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:mtd"] = "OKAY",
            ["flash:user-bootfs"] = "OKAY",
        });
        var session = new FastbootSession(transport);
        var options = ConfirmedOptions();
        options.OnlyPartitions = ["user-bootfs"];
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), options).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("flash:user-bootfs", transport.Commands);
        Assert.DoesNotContain("flash:user-rootfs", transport.Commands);
        Assert.Equal(2, PairDownloadsWithPayloads(transport).Count);
    }

    /// <remarks>
    /// The device answers <c>OKAY&lt;hex&gt;</c> with the staged byte count (fb_mmc without, fb_mtd
    /// and fb_blk with a 0x prefix) and then serves the payload on a plain <c>upload</c>; the reader
    /// must advance the offset by the staged size and stop once the device stages nothing.
    /// <para>设备以 <c>OKAY&lt;十六进制&gt;</c> 应答暂存字节数（fb_mmc 不带、fb_mtd 与 fb_blk 带 0x
    /// 前缀），随后以普通 <c>upload</c> 送出负载；读取器必须按暂存长度推进偏移，并在设备暂存为空时停止。</para>
    /// </remarks>
    [Fact]
    public void PartitionReader_ReadsStagedRoundsUntilDeviceStagesNothing()
    {
        var first = "FIRST-STAGE"u8.ToArray();
        var second = "SECOND-STAGE"u8.ToArray();
        var transport = new ScriptedTransport(new Dictionary<string, string>());
        transport.StagedReads["oem read:data"] = first;
        transport.StagedReads["oem read:data 0xb"] = second;
        using var session = new FastbootSession(transport);
        using var output = new MemoryStream();

        var total = SpacemiTPartitionReader.Read(session, "data", output, null, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Length + second.Length, total);
        Assert.Equal(first.Concat(second).ToArray(), output.ToArray());
        Assert.Contains("oem read:data", transport.Commands);
        Assert.Contains("oem read:data 0xb", transport.Commands);
        Assert.Equal(2, transport.Commands.Count(command => command == "upload"));
    }

    /// <remarks>
    /// fb_mtd answers the staged size with a 0x prefix and zero padding; the reader must accept that
    /// form and derive the next offset from it.
    /// <para>fb_mtd 以带 0x 前缀且补零的形式应答暂存长度；读取器必须接受该形式并据此推出下一偏移。</para>
    /// </remarks>
    [Fact]
    public void PartitionReader_AcceptsHexSizeWithPrefixAndPadding()
    {
        var payload = "0123"u8.ToArray();
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["oem read:p"] = "OKAY0x00000004",
        });
        transport.StagedReads["oem read:p"] = payload;
        using var session = new FastbootSession(transport);
        using var output = new MemoryStream();

        var total = SpacemiTPartitionReader.Read(session, "p", output, null, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(payload.Length, total);
        Assert.Equal(payload, output.ToArray());
        Assert.Contains("oem read:p 0x4", transport.Commands);
    }

    /// <remarks>
    /// A device staging more than the requested limit (an MTD NOR read rounds the length up to four
    /// bytes) must not fail the read: the reader stops at the limit and the excess never enters the
    /// output the caller hashes.
    /// <para>设备暂存超过请求上限（MTD NOR 读取长度向上取整到四字节）不得使回读失败：读取器在达到上限时
    /// 停止，超出部分绝不进入调用方哈希的输出。</para>
    /// </remarks>
    [Fact]
    public void PartitionReader_StopsAtMaximumBytesWhenDeviceStagesMore()
    {
        var payload = "PAYLOAD-BYTES"u8.ToArray();
        byte[] staged = [.. payload, (byte)'x', (byte)'y', (byte)'z'];
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["oem read:p"] = $"OKAY0x{staged.Length:x}",
        });
        transport.StagedReads["oem read:p"] = staged;
        using var session = new FastbootSession(transport);
        using var output = new MemoryStream();

        var total = SpacemiTPartitionReader.Read(session, "p", output, payload.Length, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(payload.Length, total);
        Assert.Equal(payload, output.ToArray());
        Assert.Single(transport.Commands, command => command.StartsWith("oem read:", StringComparison.Ordinal));
    }

    /// <remarks>
    /// A staged byte count above the gadget's 256 MiB per-round bound is a malformed answer; it must
    /// fail the read before an upload is attempted, rather than overflowing the running offset.
    /// <para>暂存字节数超过 gadget 单轮 256 MiB 上限即为异常应答；回读必须在发起 upload 之前失败，
    /// 而不是让运行偏移溢出。</para>
    /// </remarks>
    [Fact]
    public void PartitionReader_RejectsOversizedStagedRound()
    {
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["oem read:p"] = "OKAY0x10000005",
        });
        using var session = new FastbootSession(transport);
        using var output = new MemoryStream();

        Assert.Throws<FastbootException>(() =>
            SpacemiTPartitionReader.Read(session, "p", output, null, null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("upload", transport.Commands);
    }

    /// <remarks>
    /// When a multi-round read hits its byte limit in the middle of the last staged payload, only the
    /// remaining allowance reaches the output; the excess is drained from the wire but never appended.
    /// <para>多轮回读在最后一轮暂存负载中途达到字节上限时，只有剩余额度进入输出；超出部分从线路排空
    /// 但绝不追加到输出。</para>
    /// </remarks>
    [Fact]
    public void PartitionReader_TruncatesFinalRoundAtMaximumBytes()
    {
        var first = "0123456789"u8.ToArray();
        var second = "ABCDEFGHIJ"u8.ToArray();
        var transport = new ScriptedTransport(new Dictionary<string, string>());
        transport.StagedReads["oem read:p"] = first;
        transport.StagedReads["oem read:p 0xa"] = second;
        using var session = new FastbootSession(transport);
        using var output = new MemoryStream();

        var total = SpacemiTPartitionReader.Read(session, "p", output, 15, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(15, total);
        Assert.Equal(first.Concat(second.Take(5)).ToArray(), output.ToArray());
        Assert.Equal(2, transport.Commands.Count(command => command.StartsWith("oem read:", StringComparison.Ordinal)));
        Assert.Equal(2, transport.Commands.Count(command => command == "upload"));
    }

    /// <remarks>
    /// The gadget answers a partition name missing from its GPT by reading the whole storage instead
    /// of failing, so a partition the device does not report through getvar partition-size must be
    /// refused before any oem read round is sent.
    /// <para>gadget 对其 GPT 中不存在的分区名的应答是读整盘而不是报错，因此设备未经 getvar
    /// partition-size 上报的分区必须在发出任何 oem read 轮之前被拒绝。</para>
    /// </remarks>
    [Fact]
    public void PartitionReader_RefusesPartitionTheDeviceDoesNotReport()
    {
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:partition-size:ghost"] = "FAILno such partition",
        });
        using var session = new FastbootSession(transport);
        using var output = new MemoryStream();

        Assert.Throws<FastbootException>(() =>
            SpacemiTPartitionReader.Read(session, "ghost", output, null, null,
                TestContext.Current.CancellationToken));
        Assert.DoesNotContain(transport.Commands, command => command.StartsWith("oem read:", StringComparison.Ordinal));
        Assert.Empty(output.ToArray());
    }

    /// <remarks>
    /// A read without an explicit byte limit is bounded at the size the device reports for the
    /// partition: excess staged bytes are drained from the wire but never appended, and the loop
    /// stops once the bound is reached.
    /// <para>未显式给出字节上限的回读按设备为该分区上报的大小限定：超出的暂存字节从线路排空但不追加，
    /// 达到上限后循环即停止。</para>
    /// </remarks>
    [Fact]
    public void PartitionReader_BoundsOpenEndedReadAtDeviceReportedSize()
    {
        var payload = "0123456789"u8.ToArray();
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:partition-size:p"] = "OKAY0x0000000000000004",
        });
        transport.StagedReads["oem read:p"] = payload;
        using var session = new FastbootSession(transport);
        using var output = new MemoryStream();

        var total = SpacemiTPartitionReader.Read(session, "p", output, null, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, total);
        Assert.Equal(payload.Take(4).ToArray(), output.ToArray());
        Assert.Single(transport.Commands, command => command.StartsWith("oem read:", StringComparison.Ordinal));
    }

    /// <remarks>
    /// Partition names are concatenated into the <c>oem read</c> command line, so whitespace,
    /// control characters, emptiness and over-length names must be rejected before any wire I/O.
    /// <para>分区名会被拼接进 <c>oem read</c> 命令行，因此空白、控制字符、空串与超长名必须在任何线上
    /// I/O 之前被拒绝。</para>
    /// </remarks>
    [Fact]
    public void PartitionReader_RejectsInvalidPartitionNamesBeforeAnyWireIO()
    {
        var transport = new ScriptedTransport(new Dictionary<string, string>());
        using var session = new FastbootSession(transport);
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() =>
            SpacemiTPartitionReader.Read(session, "", output, null, null, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() =>
            SpacemiTPartitionReader.Read(session, "d ata", output, null, null, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() =>
            SpacemiTPartitionReader.Read(session, new string('a', 65), output, null, null, TestContext.Current.CancellationToken));
        Assert.Empty(transport.Commands);
    }

    [Fact]
    public void PartitionReader_FailsWhenOemReadIsRejected()
    {
        var transport = new ScriptedTransport(new Dictionary<string, string>());
        transport.FailOnce("oem read:data");
        using var session = new FastbootSession(transport);
        using var output = new MemoryStream();

        Assert.Throws<FastbootException>(() =>
            SpacemiTPartitionReader.Read(session, "data", output, null, null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("upload", transport.Commands);
    }

    /// <summary>
    /// Writes the verify fixture: a 600-byte random image on partition "data" plus an "env"
    /// partition, and the vendor flow that flashes both.
    /// <para>写入校验夹具：分区 "data" 上 600 字节随机镜像与 "env" 分区，以及刷写两者的厂商流程。</para>
    /// </summary>
    /// <returns>The image bytes written for partition "data". <para>为分区 "data" 写入的镜像字节。</para></returns>
    private byte[] WriteVerifyFixture()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - multi_flash:
              retry: 1
              relate_partition: ['partition_{size0}.json']
        """);
        File.WriteAllText(Path.Combine(_root, "partition_4M.json"), """
        {"format":"mtd","partitions":[{"name":"data","offset":"0","size":"-","image":"data.bin"},{"name":"env","offset":"64K","size":"64K","image":"env.bin"}]}
        """);
        var image = new byte[600];
        RandomNumberGenerator.Fill(image);
        File.WriteAllBytes(Path.Combine(_root, "data.bin"), image);
        File.WriteAllBytes(Path.Combine(_root, "env.bin"), "ENV-PAYLOAD"u8.ToArray());
        return image;
    }

    /// <remarks>
    /// The read-back matches the source image, so verification passes; the "env" partition is in the
    /// fixed skip set and must never be read back.
    /// <para>回读内容与源镜像一致，校验通过；"env" 分区在固定跳过集内，绝不被回读。</para>
    /// </remarks>
    [Fact]
    public void Flasher_VerifyAfterFlash_PassesWhenReadBackMatchesTheImage()
    {
        var image = WriteVerifyFixture();
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:mtd"] = "OKAY",
            ["flash:data"] = "OKAY",
            ["flash:env"] = "OKAY",
        });
        transport.StagedReads["oem read:data"] = image;
        var progress = new RecordingFlashProgress();
        var session = new FastbootSession(transport);
        var options = ConfirmedOptions();
        options.VerifyAfterFlash = true;
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), options).Execute(
            session, _ => session, progress, TestContext.Current.CancellationToken);

        Assert.Contains("oem read:data", transport.Commands);
        Assert.Contains("upload", transport.Commands);
        Assert.DoesNotContain(transport.Commands, command => command.StartsWith("oem read:env", StringComparison.Ordinal));
        Assert.Contains(progress.Reports, report => report.Phase == "verify-partition" && report.Partition == "data");
        Assert.Contains(progress.Reports, report => report.Phase == "verify-partition" && report.BytesCompleted == image.LongLength);
    }

    /// <remarks>
    /// A read-back differing from the source image must fail the run with a verify mismatch; retrying
    /// cannot change the content, so there is no retry around the comparison.
    /// <para>回读与源镜像不一致时必须以校验不匹配终止运行；重试无法改变内容，因此比对外层没有重试。</para>
    /// </remarks>
    [Fact]
    public void Flasher_VerifyAfterFlash_FailsWhenReadBackDiffersFromTheImage()
    {
        var image = WriteVerifyFixture();
        var corrupted = (byte[])image.Clone();
        corrupted[100] ^= 0xff;
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:mtd"] = "OKAY",
            ["flash:data"] = "OKAY",
            ["flash:env"] = "OKAY",
        });
        transport.StagedReads["oem read:data"] = corrupted;
        var session = new FastbootSession(transport);
        var options = ConfirmedOptions();
        options.VerifyAfterFlash = true;
        var ex = Assert.Throws<FastbootException>(() =>
            new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), options).Execute(
                session, _ => session, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("verify mismatch for partition 'data'", ex.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// MTD NOR read-backs round the staged length up to four bytes and fb_mtd prefixes the size with
    /// 0x; only the first N bytes enter the digest, so the padded tail must not fail verification.
    /// <para>MTD NOR 回读把暂存长度向上取整到四字节且 fb_mtd 以 0x 前缀标记长度；摘要只覆盖前 N 字节，
    /// 因此补齐的尾部不得使校验失败。</para>
    /// </remarks>
    [Fact]
    public void Flasher_VerifyAfterFlash_ToleratesPaddedReadBackBeyondImageLength()
    {
        var image = WriteVerifyFixture();
        var staged = new byte[image.Length + 3];
        image.CopyTo(staged, 0);
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:mtd"] = "OKAY",
            ["flash:data"] = "OKAY",
            ["flash:env"] = "OKAY",
            ["oem read:data"] = $"OKAY0x{staged.Length:x}",
        });
        transport.StagedReads["oem read:data"] = staged;
        var session = new FastbootSession(transport);
        var options = ConfirmedOptions();
        options.VerifyAfterFlash = true;
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), options).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(transport.Commands, command => command.StartsWith("oem read:", StringComparison.Ordinal));
        Assert.Single(transport.Commands, command => command == "upload");
    }

    /// <remarks>
    /// A device with a smaller staging buffer than the image forces several offset-advancing rounds;
    /// every round after the first carries the 0x-prefixed offset of the next unread byte.
    /// <para>暂存缓冲小于镜像的设备会迫使多次推进偏移的轮次；首轮之后的每一轮都携带下一个未读字节的
    /// 0x 前缀偏移。</para>
    /// </remarks>
    [Fact]
    public void Flasher_VerifyAfterFlash_ReadsBackInOffsetAdvancingRounds()
    {
        var image = WriteVerifyFixture();
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:mtd"] = "OKAY",
            ["flash:data"] = "OKAY",
            ["flash:env"] = "OKAY",
        });
        transport.StagedReads["oem read:data"] = image[..100];
        transport.StagedReads["oem read:data 0x64"] = image[100..200];
        transport.StagedReads["oem read:data 0xc8"] = image[200..300];
        transport.StagedReads["oem read:data 0x12c"] = image[300..];
        var session = new FastbootSession(transport);
        var options = ConfirmedOptions();
        options.VerifyAfterFlash = true;
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), options).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("oem read:data 0x64", transport.Commands);
        Assert.Contains("oem read:data 0xc8", transport.Commands);
        Assert.Contains("oem read:data 0x12c", transport.Commands);
        Assert.Equal(4, transport.Commands.Count(command => command == "upload"));
    }

    /// <remarks>
    /// The vendor verify_board script skips exactly <c>env</c> (U-Boot re-saves it with runtime state) and verifies
    /// <c>bootinfo</c> like any other MTD partition, so on an MTD table the run reads bootinfo back and skips env.
    /// <para>厂商 verify_board 脚本恰好只跳过 <c>env</c>（U-Boot 会以运行时状态重存它），并像其它 MTD 分区一样
    /// 校验 <c>bootinfo</c>，因此 MTD 表上运行会回读 bootinfo 并跳过 env。</para>
    /// </remarks>
    [Fact]
    public void Flasher_VerifyAfterFlash_VerifiesBootinfoAndSkipsEnvOnMtd()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - multi_flash:
              retry: 1
              relate_partition: ['partition_{size0}.json']
        """);
        File.WriteAllText(Path.Combine(_root, "partition_4M.json"), """
        {"format":"mtd","partitions":[{"name":"bootinfo","offset":"0","size":"512","image":"bootinfo.bin"},{"name":"env","offset":"512","size":"64K","image":"env.bin"}]}
        """);
        var bootinfo = "BOOTINFO"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_root, "bootinfo.bin"), bootinfo);
        File.WriteAllBytes(Path.Combine(_root, "env.bin"), "ENV"u8.ToArray());

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:mtd"] = "OKAY",
            ["flash:bootinfo"] = "OKAY",
            ["flash:env"] = "OKAY",
        });
        transport.StagedReads["oem read:bootinfo"] = bootinfo;
        var session = new FastbootSession(transport);
        var options = ConfirmedOptions();
        options.VerifyAfterFlash = true;
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), options).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("flash:bootinfo", transport.Commands);
        Assert.Contains("flash:env", transport.Commands);
        Assert.Contains("oem read:bootinfo", transport.Commands);
        Assert.DoesNotContain(transport.Commands, command => command.StartsWith("oem read:env", StringComparison.Ordinal));
    }

    /// <remarks>
    /// A block-device partition the tables mark hidden has no device GPT entry, so its read-back probe answers
    /// "no such partition"; the run skips its verification while still verifying the visible ones.
    /// <para>布局表标记 hidden 的块设备分区在设备 GPT 中没有条目，回读探测会应答 "no such partition"；运行跳过其
    /// 校验，同时照常校验可见分区。</para>
    /// </remarks>
    [Fact]
    public void Flasher_VerifyAfterFlash_SkipsHiddenPartitionsOnBlockTables()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - getvar:
              args: 'blk-size'
              set_var: 'size1'
          - multi_flash:
              retry: 1
              relate_partition: ['partition_{size0}.json', 'partition_{size1}.json']
        """);
        File.WriteAllText(Path.Combine(_root, "partition_universal.json"), """
        {"format":"gpt","partitions":[{"name":"bootinfo","hidden":true,"offset":"0K","size":"512","image":"bootinfo.bin"},{"name":"data","offset":"4M","size":"-","image":"data.bin"}]}
        """);
        File.WriteAllBytes(Path.Combine(_root, "bootinfo.bin"), "BOOTINFO"u8.ToArray());
        var data = "DATA-PAYLOAD"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_root, "data.bin"), data);

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAYNULL",
            ["getvar:blk-size"] = "OKAYuniversal",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:gpt"] = "OKAY",
            ["flash:bootinfo"] = "OKAY",
            ["flash:data"] = "OKAY",
            ["getvar:partition-size:bootinfo"] = "FAILno such partition",
        });
        transport.StagedReads["oem read:data"] = data;
        var session = new FastbootSession(transport);
        var options = ConfirmedOptions();
        options.VerifyAfterFlash = true;
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), options).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("flash:bootinfo", transport.Commands);
        Assert.Contains("flash:data", transport.Commands);
        Assert.DoesNotContain(transport.Commands, command => command.StartsWith("oem read:bootinfo", StringComparison.Ordinal));
        Assert.Contains("oem read:data", transport.Commands);
    }

    /// <remarks>
    /// Verification is opt-in: with <see cref="SpacemiTFlashOptions.VerifyAfterFlash"/> unset the run
    /// must not touch the <c>oem read</c>/<c>upload</c> path at all.
    /// <para>校验是显式开启的：未设置 <see cref="SpacemiTFlashOptions.VerifyAfterFlash"/> 时运行完全不
    /// 触碰 <c>oem read</c>/<c>upload</c> 路径。</para>
    /// </remarks>
    [Fact]
    public void Flasher_VerifyAfterFlashDisabled_SendsNoReadCommands()
    {
        WriteVerifyFixture();
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:mtd"] = "OKAY",
            ["flash:data"] = "OKAY",
            ["flash:env"] = "OKAY",
        });
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("flash:data", transport.Commands);
        Assert.DoesNotContain(transport.Commands, command => command.StartsWith("oem read:", StringComparison.Ordinal));
        Assert.DoesNotContain("upload", transport.Commands);
    }

    /// <remarks>
    /// Mirrors FlashGzipChunks via <see cref="SpacemiTFlasher.ComputeGzipChunkBytes"/>: the expansion overhead
    /// (GzipCompressBound(x) - x) plus a safety margin is reserved, the raw size is floored to the device block
    /// size and the worst-case compressed size of the chosen chunk must still fit the budget. The headroom must scale
    /// with the image size so it covers worst-case compression expansion, where incompressible data at 256 MiB expands
    /// by roughly 80 KiB. Budgets cover 1 MiB, 8 MiB, 64 MiB, 128 MiB and 256 MiB (== MaxGzipChunkBytes).
    /// <para>经 <see cref="SpacemiTFlasher.ComputeGzipChunkBytes"/> 镜像 FlashGzipChunks：预留膨胀开销
    /// （GzipCompressBound(x) - x）加安全余量，原始尺寸向下取整到设备块大小，所选分块的最坏压缩尺寸仍不超出预算。
    /// 余量必须随镜像大小伸缩，以覆盖最坏情况的压缩膨胀——256 MiB 下不可压缩数据约膨胀 80 KiB。预算覆盖
    /// 1 MiB、8 MiB、64 MiB、128 MiB 与 256 MiB（== MaxGzipChunkBytes）。</para>
    /// </remarks>
    [Theory]
    [InlineData(1L * 1024 * 1024)]
    [InlineData(8L * 1024 * 1024)]
    [InlineData(64L * 1024 * 1024)]
    [InlineData(128L * 1024 * 1024)]
    [InlineData(256L * 1024 * 1024)]
    public void GzipChunkHeadroom_KeepsCompressedMemberWithinBudget(long budget)
    {
        var chunkBytes = SpacemiTFlasher.ComputeGzipChunkBytes(budget);
        Assert.True(chunkBytes > 0, $"raw chunk would be non-positive for budget {budget}");
        Assert.Equal(0, chunkBytes % 512);
        Assert.True(SpacemiTFlasher.GzipCompressBound(chunkBytes) <= budget,
            $"worst-case compressed size {SpacemiTFlasher.GzipCompressBound(chunkBytes)} exceeds budget {budget}");
    }

    /// <remarks>
    /// The payload is incompressible and larger than max-download-size, so it is flashed as gzip chunks; the dynamic
    /// headroom must keep every member within max-download-size.
    /// <para>载荷不可压缩且大于 max-download-size，因此按 gzip 分块烧写；动态余量必须使每个分块都不超过
    /// max-download-size。</para>
    /// </remarks>
    [Fact]
    public void Flasher_GzipChunkedFlashNeverExceedsMaxDownloadSize()
    {
        const long maxDownload = 2L * 1024 * 1024;
        WriteChunkedGzipPackage();

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAYNULL",
            ["getvar:blk-size"] = "OKAYuniversal",
            ["getvar:max-download-size"] = $"OKAY0x{maxDownload:x8}",
            ["flash:gpt"] = "OKAY",
            ["flash:big"] = "OKAY",
        });
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(transport.Downloads);
        foreach (var command in transport.Commands)
        {
            if (command.StartsWith("download:", StringComparison.Ordinal) &&
                long.TryParse(command["download:".Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size))
            {
                Assert.True(size <= maxDownload, $"download {size} exceeds max-download-size {maxDownload}");
            }
        }

        Assert.Contains(transport.Downloads, download =>
            download.Length >= 3 && download[0] == 0x1f && download[1] == 0x8b && download[2] == 0x08);
    }

    /// <summary>
    /// The chunked gzip path must report byte progress against the whole image instead of one text line per chunk: the
    /// page binds that number to its progress bar, so a transfer that only says "chunk sent" leaves the bar at zero for
    /// the entire image.
    /// <para>gzip 分块路径必须按整幅镜像上报字节进度，而不是每个分块只给一行文本：页面把该数值绑定到进度条，只报“分块已发送”
    /// 会让进度条在整幅镜像期间停在零。</para>
    /// </summary>
    [Fact]
    public void Flasher_GzipChunkedFlashReportsByteProgressAgainstTheWholeImage()
    {
        var payload = WriteChunkedGzipPackage();
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAYNULL",
            ["getvar:blk-size"] = "OKAYuniversal",
            ["getvar:max-download-size"] = "OKAY0x00200000",
            ["flash:gpt"] = "OKAY",
            ["flash:big"] = "OKAY",
        });
        var progress = new RecordingFlashProgress();

        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            new FastbootSession(transport), _ => _, progress,
            TestContext.Current.CancellationToken);

        var reports = progress.Reports
            .Where(report => report.Partition == "big" && report.BytesCompleted is not null)
            .ToArray();
        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.Equal(payload.LongLength, report.BytesTotal));

        var completed = reports.Select(report => report.BytesCompleted!.Value).ToArray();
        Assert.True(completed.SequenceEqual(completed.Order()), "progress must never move backwards");
        Assert.True(completed[^1] >= payload.LongLength,
            $"the last report covers {completed[^1]} of {payload.LongLength} bytes");
        Assert.True(completed.Distinct().Count() > 1,
            "progress must advance inside the chunked transfer, not jump once at the end");
    }

    /// <summary>
    /// Writes the chunked-gzip fixture the two chunk tests share: a 4 MiB image larger than the 2 MiB max-download-size,
    /// a table that leaves its size open-ended, and the vendor two-table flash action.
    /// <para>写入两个分块用例共用的 gzip 夹具：大于 2 MiB 下载上限的 4 MiB 镜像、不限长度的分区表，以及厂商的双表刷写动作。</para>
    /// </summary>
    /// <returns>The image bytes written to the package. <para>写入包内的镜像字节。</para></returns>
    private byte[] WriteChunkedGzipPackage()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - getvar:
              args: 'blk-size'
              set_var: 'size1'
          - multi_flash:
              timeout:
                minutes: 5
              retry: 1
              relate_partition: ['partition_{size0}.json', 'partition_{size1}.json']
        """);
        File.WriteAllText(Path.Combine(_root, "partition_universal.json"), """
        {
          "version": "1.0",
          "format": "gpt",
          "partitions": [
            { "name": "big", "offset": "0", "size": "-", "image": "big.bin" }
          ]
        }
        """);

        var payload = new byte[4L * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        File.WriteAllBytes(Path.Combine(_root, "big.bin"), payload);
        return payload;
    }

    /// <remarks>
    /// The device floors the running gzip append offset to the storage block size and rounds each write up to it,
    /// so the chunked stream must decompress to block-aligned members and start with one raw zero block that zeroes
    /// the append offset; the round trip must reproduce the source image content exactly.
    /// <para>设备端把 gzip 追加偏移向下取整到存储块大小并把每次写入向上取整到块，因此分块流必须解压为块对齐的成员，
    /// 并以一个清零追加偏移的 raw 零块开头；往返内容必须与源镜像逐字节一致。</para>
    /// </remarks>
    [Fact]
    public void Flasher_GzipChunkRoundTripIsBlockAlignedAndContentPreserving()
    {
        WriteChunkedFlashFixture();
        var payload = new byte[4 * 1024 * 1024 + 100];
        RandomNumberGenerator.Fill(payload);
        File.WriteAllBytes(Path.Combine(_root, "big.bin"), payload);
        const long maxDownload = 2L * 1024 * 1024;
        var transport = NewChunkedTransport(maxDownload);
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        AssertChunkedSequenceStartsWithBlockReset(transport, "big");
        var roundTrip = DecompressMembersAfterLastReset(transport);
        Assert.True(roundTrip.Length >= payload.Length);
        Assert.True(roundTrip.Length % 512 == 0);
        Assert.Equal(payload, roundTrip.AsSpan(0, payload.Length).ToArray());
        Assert.True(roundTrip[payload.Length..].All(b => b == 0));
    }

    /// <remarks>
    /// An oversized image that is itself gzip would be decompressed by the device on a single-shot transfer, so the
    /// chunked path must flash the decompressed content, not the compressed bytes.
    /// <para>本身即为 gzip 的超大镜像在单次传输时会被设备端解压，因此分块路径必须烧写解压后的内容而不是压缩字节。</para>
    /// </remarks>
    [Fact]
    public void Flasher_OversizedGzipSourceFlashesDecompressedContent()
    {
        WriteChunkedFlashFixture();
        var payload = new byte[3 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        File.WriteAllBytes(Path.Combine(_root, "big.bin"), CompressionService.Compress(payload, CompressionFormat.Gzip));
        const long maxDownload = 1L * 1024 * 1024;
        var transport = NewChunkedTransport(maxDownload);
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        AssertChunkedSequenceStartsWithBlockReset(transport, "big");
        var roundTrip = DecompressMembersAfterLastReset(transport);
        Assert.Equal(payload, roundTrip);
    }

    /// <remarks>
    /// A gzip image within max-download-size is forwarded unchanged: the device decompresses it before writing, so the
    /// host must not decompress, re-compress or pad it.
    /// <para>未超过 max-download-size 的 gzip 镜像应原样转发：设备端写入前自行解压，宿主不得解压、重压缩或补零。</para>
    /// </remarks>
    [Fact]
    public void Flasher_SmallGzipSourceSentUnchanged()
    {
        WriteChunkedFlashFixture();
        var payload = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        var gzipFile = CompressionService.Compress(payload, CompressionFormat.Gzip);
        File.WriteAllBytes(Path.Combine(_root, "big.bin"), gzipFile);
        const long maxDownload = 4L * 1024 * 1024;
        var transport = NewChunkedTransport(maxDownload);
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        var downloads = PairDownloadsWithPayloads(transport);
        Assert.Equal(2, downloads.Count);
        Assert.Equal(gzipFile, downloads[1].Payload);
        Assert.DoesNotContain(downloads, pair => IsBlockResetPayload(pair.Payload));
    }

    /// <remarks>
    /// The device keeps the gzip append offset across flash calls in static state, so every fresh chunked attempt
    /// must begin with the raw zero block that zeroes it; a failed first attempt must not leave the retry writing at
    /// the stale offset.
    /// <para>设备端把 gzip 追加偏移保存在跨 flash 调用的静态状态中，因此每次新的分块尝试都必须以清零偏移的 raw
    /// 零块开头；首次尝试失败不能让重试从遗留偏移继续写入。</para>
    /// </remarks>
    [Fact]
    public void Flasher_ChunkedSequenceResetsAppendStateAfterRetry()
    {
        WriteChunkedFlashFixture();
        var payload = new byte[4 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        File.WriteAllBytes(Path.Combine(_root, "big.bin"), payload);
        const long maxDownload = 2L * 1024 * 1024;
        var transport = NewChunkedTransport(maxDownload);
        transport.FailOnce("flash:big");
        var session = new FastbootSession(transport);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, PairDownloadsWithPayloads(transport).Count(pair => IsBlockResetPayload(pair.Payload)));
        Assert.Equal(payload, DecompressMembersAfterLastReset(transport));
    }

    /// <remarks>
    /// The decompiled vendor flasher defaults its gzip level to 2, a value that is only safe on its
    /// pgzip codec; the equivalent fastest setting that still satisfies the zlib expansion bound on
    /// the BCL gzip stack is 4, so the option default must be 4.
    /// <para>反编译的厂商烧录器把 gzip 级别默认为 2，但该值仅在其 pgzip 编解码器上安全；BCL gzip 栈上
    /// 仍满足 zlib 膨胀上界的等价最快档为 4，因此选项默认值必须为 4。</para>
    /// </remarks>
    [Fact]
    public void Options_DefaultGzipLevel_MatchesVendorDefault()
    {
        Assert.Equal(4, new SpacemiTFlashOptions().GzipLevel);
    }

    private void WriteChunkedFlashFixture()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - getvar:
              args: 'blk-size'
              set_var: 'size1'
          - multi_flash:
              timeout:
                minutes: 5
              retry: 2
              relate_partition: ['partition_{size0}.json', 'partition_{size1}.json']
        """);
        const string tableJson = """
        {
          "version": "1.0",
          "format": "gpt",
          "partitions": [
            { "name": "big", "offset": "0", "size": "-", "image": "big.bin" }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(_root, "partition_universal.json"), tableJson);
    }

    private static ScriptedTransport NewChunkedTransport(long maxDownload) => new(new Dictionary<string, string>
    {
        ["getvar:mtd-size"] = "OKAYNULL",
        ["getvar:blk-size"] = "OKAYuniversal",
        ["getvar:max-download-size"] = $"OKAY0x{maxDownload:x8}",
        ["flash:gpt"] = "OKAY",
        ["flash:big"] = "OKAY",
    });

    private static List<(int CommandIndex, byte[] Payload)> PairDownloadsWithPayloads(ScriptedTransport transport)
    {
        var pairs = new List<(int, byte[])>();
        var ordinal = 0;
        for (var i = 0; i < transport.Commands.Count; i++)
        {
            if (transport.Commands[i].StartsWith("download:", StringComparison.Ordinal))
            {
                pairs.Add((i, transport.Downloads[ordinal]));
                ordinal++;
            }
        }

        return pairs;
    }

    private static bool IsGzipMember(byte[] payload) =>
        payload.Length >= 4 && payload[0] == 0x1f && payload[1] == 0x8b &&
        payload[2] == 0x08 && (payload[3] & 0x20) == 0;

    private static bool IsBlockResetPayload(byte[] payload) =>
        payload.Length == 512 && payload.All(b => b == 0);

    private static void AssertChunkedSequenceStartsWithBlockReset(ScriptedTransport transport, string partition)
    {
        var firstFlash = transport.Commands.IndexOf($"flash:{partition}");
        Assert.True(firstFlash > 0, $"flash:{partition} was never sent");
        var preceding = PairDownloadsWithPayloads(transport)
            .Where(pair => pair.CommandIndex < firstFlash)
            .ToList();
        Assert.True(preceding.Count >= 2, "no block reset download before the first chunked flash");
        Assert.True(IsBlockResetPayload(preceding[^1].Payload), "the download before the first chunked flash is not a 512-byte zero block");
    }

    private static byte[] DecompressMembersAfterLastReset(ScriptedTransport transport)
    {
        var downloads = PairDownloadsWithPayloads(transport);
        var lastReset = downloads.FindLastIndex(pair => IsBlockResetPayload(pair.Payload));
        Assert.True(lastReset >= 0, "no block reset download found");
        using var output = new MemoryStream();
        foreach (var pair in downloads.Skip(lastReset + 1))
        {
            Assert.True(IsGzipMember(pair.Payload), $"download at command #{pair.CommandIndex} is not a gzip member");
            var raw = CompressionService.Decompress(pair.Payload, CompressionFormat.Gzip);
            Assert.Equal(0, raw.Length % 512);
            output.Write(raw);
        }

        return output.ToArray();
    }

    [Fact]
    public void Flasher_BootChainScope_StagesTheChainAndStopsBeforeAnyStorageWrite()
    {
        WriteBootChainPackage();
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:version-brom"] = "OKAY1.0",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["continue"] = "OKAY",
            ["getvar:mtd-size"] = "OKAYNULL",
            ["getvar:blk-size"] = "OKAYuniversal",
            ["flash:gpt"] = "OKAYparse gpt table okay",
            ["flash:env"] = "OKAY",
        });
        var session = new FastbootSession(transport);
        var options = ConfirmedOptions();
        options.Scope = SpacemiTFlashScope.BootChain;

        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), options).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(transport.Downloads);
        Assert.Contains("continue", transport.Commands);
        Assert.DoesNotContain("getvar:mtd-size", transport.Commands);
        Assert.DoesNotContain(transport.Commands, command => command.StartsWith("flash:", StringComparison.Ordinal));
    }

    [Fact]
    public void Flasher_BootChainScope_NeedsNoFlashConfirmation()
    {
        WriteBootChainPackage();
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:version-brom"] = "OKAY1.0",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["continue"] = "OKAY",
        });
        var session = new FastbootSession(transport);
        var options = new SpacemiTFlashOptions { Scope = SpacemiTFlashScope.BootChain, ReEnumerateSettleMs = 0 };

        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), options).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(transport.Downloads);
    }

    [Fact]
    public void Flasher_StorageScope_SkipsStagingAndWritesTheTables()
    {
        WriteBootChainPackage();
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:version-brom"] = "FAILVariable not implemented",
            ["getvar:version"] = "OKAY0.4",
            ["getvar:mtd-size"] = "OKAYNULL",
            ["getvar:blk-size"] = "OKAYuniversal",
            ["getvar:max-download-size"] = "OKAY0x00100000",
            ["flash:gpt"] = "OKAYparse gpt table okay",
            ["flash:env"] = "OKAY",
        });
        var session = new FastbootSession(transport);
        var options = ConfirmedOptions();
        options.Scope = SpacemiTFlashScope.Storage;

        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), options).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("continue", transport.Commands);
        Assert.Contains("flash:gpt", transport.Commands);
        Assert.Contains("flash:env", transport.Commands);
        Assert.Equal(2, transport.Downloads.Count);
    }

    [Fact]
    public void Flasher_StorageScope_OnBootromFdl_RefusesBeforeWriting()
    {
        WriteBootChainPackage();
        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:version-brom"] = "OKAY1.0",
        });
        var session = new FastbootSession(transport);
        var options = ConfirmedOptions();
        options.Scope = SpacemiTFlashScope.Storage;

        Assert.Throws<FastbootException>(() => new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), options).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(transport.Downloads);
        Assert.DoesNotContain("continue", transport.Commands);
    }

    /// <summary>
    /// Writes the vendor flow shape both scopes split at: a BootROM version probe, one staged payload, the handover, and
    /// the storage actions behind it.
    /// <para>写入两个范围所切分的厂商流程形态：BootROM 版本探测、一次暂存负载、移交，以及其后的存储动作。</para>
    /// </summary>
    private void WriteBootChainPackage()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - getvar:
              args: 'version-brom'
              set: 'version'
              skip_fail: true
              timeout:
                seconds: 1
          - stage:
              file: 'factory/FSBL.bin'
              skip_when: "not temp.version"
              timeout:
                minutes: 2
          - continue:
              skip_when: "not temp.version"
              timeout:
                seconds: 1
          - getvar:
              args: 'mtd-size'
              set_var: 'size0'
              skip_fail: true
          - getvar:
              args: 'blk-size'
              set_var: 'size1'
          - multi_flash:
              retry: 3
              relate_partition: ['partition_{size0}.json', 'partition_{size1}.json']
        """);
        Directory.CreateDirectory(Path.Combine(_root, "factory"));
        File.WriteAllBytes(Path.Combine(_root, "factory", "FSBL.bin"), "FSBL-PAYLOAD"u8.ToArray());
        File.WriteAllText(Path.Combine(_root, "partition_universal.json"), """
        {
          "version": "1.0",
          "format": "gpt",
          "partitions": [
            { "name": "env", "offset": "0", "size": "64K", "image": "env.bin" }
          ]
        }
        """);
        File.WriteAllBytes(Path.Combine(_root, "env.bin"), "ENV-PAYLOAD"u8.ToArray());
    }

    private static SpacemiTFlashOptions ConfirmedOptions() => new()
    {
        FlashConfirmation = SpacemiTFlashOptions.RequiredConfirmation,
        ReEnumerateSettleMs = 0,
    };

    [Fact]
    public void SendCommand_ZeroByteWrite_SilentDeviceResetsAndRewritesOnce()
    {
        var transport = new ZeroWriteTransport(new Dictionary<string, string>())
        {
            RefuseAlways = true,
            SilentWhenRefused = true,
        };
        using var session = new FastbootSession(transport, timeoutMs: 50);

        var failure = Assert.Throws<FastbootException>(() => session.ExecuteCommand("getvar:version", TestContext.Current.CancellationToken));

        Assert.Equal(2, transport.Commands.Count);
        Assert.All(transport.Commands, command => Assert.Equal("getvar:version", command));
        Assert.Equal(1, transport.ResetCount);
        Assert.Contains("no byte", failure.Message, StringComparison.Ordinal);
        Assert.Contains("2 attempt(s)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteCommand_ZeroByteWriteWithDeliveredReply_ReturnsThatReplyWithoutRewriting()
    {
        var transport = new ZeroWriteTransport(new Dictionary<string, string>
        {
            ["getvar:version"] = "OKAY1.0",
        })
        {
            RefuseCount = 1,
        };
        using var session = new FastbootSession(transport, timeoutMs: 50);

        var response = session.ExecuteCommand("getvar:version", TestContext.Current.CancellationToken);

        Assert.Equal(FastbootResponseState.Success, response.State);
        Assert.Equal("1.0", response.Info);
        Assert.Single(transport.Commands);
        Assert.Single(transport.Refused);
        Assert.Equal(0, transport.ResetCount);
    }

    [Fact]
    public void ExecuteCommand_ZeroByteWriteThenSilentProbe_RewritesAndSucceeds()
    {
        var transport = new ZeroWriteTransport(new Dictionary<string, string>
        {
            ["getvar:product"] = "OKAYorangepi-rv2",
        })
        {
            RefuseCount = 1,
            SilentWhenRefused = true,
        };
        using var session = new FastbootSession(transport, timeoutMs: 50);

        var response = session.ExecuteCommand("getvar:product", TestContext.Current.CancellationToken);

        Assert.Equal(FastbootResponseState.Success, response.State);
        Assert.Equal("orangepi-rv2", response.Info);
        Assert.Equal(2, transport.Commands.Count);
        Assert.Equal(1, transport.ResetCount);
    }

    [Fact]
    public void StageZeroByteDownloadCommand_RecoversAndStagesPayload()
    {
        File.WriteAllText(Path.Combine(_root, "fastboot.yaml"), """
        version: 1.0
        actions:
          - stage:
              file: 'factory/FSBL.bin'
              retry: 2
        """);
        Directory.CreateDirectory(Path.Combine(_root, "factory"));
        var fsbl = Encoding.ASCII.GetBytes("FSBL-PAYLOAD");
        File.WriteAllBytes(Path.Combine(_root, "factory", "FSBL.bin"), fsbl);

        var transport = new ZeroWriteTransport(new Dictionary<string, string>
        {
            ["getvar:max-download-size"] = "OKAY0x00100000",
        })
        {
            RefuseCount = 1,
            SilentWhenRefused = true,
        };
        var session = new FastbootSession(transport, timeoutMs: 50);
        new SpacemiTFlasher(SpacemiTFlashPackage.Load(_root), ConfirmedOptions()).Execute(
            session, _ => session, cancellationToken: TestContext.Current.CancellationToken);

        var download = Assert.Single(transport.Downloads);
        Assert.Equal(fsbl, download);
        Assert.Contains(transport.Commands, command => command.StartsWith("download:", StringComparison.Ordinal));
    }

    [Fact]
    public void SendCommand_PartialWrite_IsNeverReIssued()
    {
        var partial = new PartialCommandTransport(accepted: 5);
        using var session = new FastbootSession(partial, timeoutMs: 50);

        var failure = Assert.Throws<FastbootException>(() => session.SendCommand("download:0002d460"));

        Assert.Equal(1, partial.WriteCount);
        Assert.Equal(0, partial.ResetCount);
        Assert.Contains("Short write: 5/17", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Transport that accepts a fixed prefix of every command write and reports it as the write result,
    /// which is how a partial bulk transfer reaches the protocol layer.
    /// <para>每次命令写入只接受固定前缀并以该数量作为写入结果，模拟部分完成的批量传输到达协议层的方式。</para>
    /// </summary>
    private sealed class PartialCommandTransport(int accepted) : ICommTransport
    {
        public int WriteCount { get; private set; }

        public int ResetCount { get; private set; }

        public bool IsConnected => true;

        public void Dispose()
        {
        }

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        public void Reset() => ResetCount++;

        public long Write(byte[] data, int offset, int length, int timeoutMs)
        {
            WriteCount++;
            return Math.Min(accepted, length);
        }

        public int Read(byte[] buffer, int offset, int length, int timeoutMs) => 0;

        public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(Write(data, offset, length, timeoutMs));

        public Stream GetStream() => throw new NotSupportedException();
    }

    private sealed class ThrowingTransport : ICommTransport
    {
        public bool IsConnected => false;
        public void Dispose()
        {
        }
        public void Connect() => throw new InvalidOperationException("no device in tests");
        public void Disconnect()
        {
        }
        public void Reset() => throw new InvalidOperationException("no device in tests");
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => throw new InvalidOperationException("no device in tests");
        public Task<int> ReadAsync(byte[] buffer, int offset, int count, int timeoutMs, CancellationToken cancellationToken = default) => throw new InvalidOperationException("no device in tests");
        public long Write(byte[] buffer, int offset, int count, int timeoutMs) => throw new InvalidOperationException("no device in tests");
        public Task<long> WriteAsync(byte[] buffer, int offset, int count, int timeoutMs, CancellationToken cancellationToken = default) => throw new InvalidOperationException("no device in tests");
        public Stream GetStream() => throw new InvalidOperationException("no device in tests");
    }

    /// <summary>
    /// Minimal scripted fastboot wire peer: every written command is recorded and answered from
    /// the scripted table (or by the DATA/OKAY download dance, the oem read staging table or the
    /// upload dance), so flasher decisions such as skip_when guards, multi_flash table selection
    /// and post-flash verification can be asserted end to end.
    /// </summary>
    /// <summary>
    /// Collects progress reports synchronously so a test can read them once the run returns; <see cref="Progress{T}"/>
    /// would post them to a thread pool and leave the assertions racing the callbacks.
    /// <para>同步收集进度报告，使测试在运行返回后即可读取；<see cref="Progress{T}"/> 会把它们投递到线程池，使断言与回调竞争。</para>
    /// </summary>
    private sealed class RecordingFlashProgress : IProgress<SpacemiTFlashProgress>
    {
        private readonly List<SpacemiTFlashProgress> _reports = [];

        public IReadOnlyList<SpacemiTFlashProgress> Reports
        {
            get
            {
                lock (_reports)
                {
                    return [.. _reports];
                }
            }
        }

        public void Report(SpacemiTFlashProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
            }
        }
    }

    private sealed class ScriptedTransport(Dictionary<string, string> answers) : ICommTransport
    {
        private readonly Queue<byte[]> _replies = [];
        private readonly List<byte> _payload = [];
        private readonly HashSet<string> _failOnce = [];
        private long _dataExpected;
        private byte[]? _pendingUpload;

        public List<string> Commands { get; } = [];

        public List<byte[]> Downloads { get; } = [];

        /// <summary>
        /// Read-back payloads keyed by the <c>oem read:…</c> command that stages them: writing that
        /// command answers OKAY with the scripted text (or an auto-computed hex size) and holds the
        /// payload for the following <c>upload</c>. An unstaged <c>oem read</c> without a scripted
        /// answer reports OKAY00000000 (nothing staged), like a device at the partition end.
        /// <para>以暂存它们的 <c>oem read:…</c> 命令为键的回读负载：写入该命令时以脚本化文本（或自动
        /// 计算的十六进制长度）应答 OKAY，并为随后的 <c>upload</c> 持有负载。无脚本应答且未暂存的
        /// <c>oem read</c> 应答 OKAY00000000（暂存为空），如同设备到达分区末尾。</para>
        /// </summary>
        public Dictionary<string, byte[]> StagedReads { get; } = [];

        public bool IsConnected => true;

        public void Dispose()
        {
        }

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        public void Reset() => _replies.Clear();

        /// <summary>
        /// Scripts one injected FAIL for the next matching command; later matches fall through to
        /// the scripted table.
        /// <para>为下一次匹配的命令注入一次 FAIL；之后的匹配按脚本表应答。</para>
        /// </summary>
        public void FailOnce(string command) => _failOnce.Add(command);

        public long Write(byte[] buffer, int offset, int count, int timeoutMs)
        {
            if (_dataExpected > 0)
            {
                _payload.AddRange(buffer[offset..(offset + count)]);
                if (_payload.Count >= _dataExpected)
                {
                    Downloads.Add([.. _payload]);
                    _payload.Clear();
                    _dataExpected = 0;
                    _replies.Enqueue("OKAY"u8.ToArray());
                }

                return count;
            }

            var command = Encoding.UTF8.GetString(buffer, offset, count);
            Commands.Add(command);
            if (command.StartsWith("download:", StringComparison.Ordinal) &&
                long.TryParse(command["download:".Length..], NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var size))
            {
                _dataExpected = size;
                _replies.Enqueue(Encoding.ASCII.GetBytes($"DATA{size:x8}"));
            }
            else if (_failOnce.Remove(command))
            {
                _replies.Enqueue(Encoding.UTF8.GetBytes($"FAILinjected failure for '{command}'"));
            }
            else if (command.StartsWith("oem read:", StringComparison.Ordinal) &&
                StagedReads.TryGetValue(command, out var stagedPayload))
            {
                _pendingUpload = stagedPayload;
                _replies.Enqueue(answers.TryGetValue(command, out var readAnswer)
                    ? Encoding.UTF8.GetBytes(readAnswer)
                    : Encoding.ASCII.GetBytes($"OKAY{stagedPayload.Length:x}"));
            }
            else if (command == "upload" && _pendingUpload is not null)
            {
                var payload = _pendingUpload;
                _pendingUpload = null;
                _replies.Enqueue(Encoding.ASCII.GetBytes($"DATA{payload.Length:x8}"));
                _replies.Enqueue(payload);
                _replies.Enqueue("OKAY"u8.ToArray());
            }
            else if (answers.TryGetValue(command, out var answer))
            {
                _replies.Enqueue(Encoding.UTF8.GetBytes(answer));
            }
            else if (command.StartsWith("getvar:partition-size:", StringComparison.Ordinal))
            {
                // The reader probes the partition through getvar partition-size before its first
                // oem read round; unless the test scripts its own answer, the fake reports a present
                // 1 MiB partition in the device's "0x%016zx" answer shape.
                // 回读器在首个 oem read 轮之前经 getvar partition-size 探测分区；测试未脚本化应答时，
                // 该 fake 按设备端 "0x%016zx" 的应答形状报告一个存在的 1 MiB 分区。
                _replies.Enqueue("OKAY0x0000000000100000"u8.ToArray());
            }
            else if (command.StartsWith("oem read:", StringComparison.Ordinal))
            {
                _replies.Enqueue("OKAY00000000"u8.ToArray());
            }
            else
            {
                _replies.Enqueue(Encoding.UTF8.GetBytes($"FAILno scripted answer for '{command}'"));
            }

            return count;
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMs)
        {
            if (_replies.Count == 0)
            {
                return 0;
            }

            var reply = _replies.Dequeue();
            reply.CopyTo(buffer, offset);
            return reply.Length;
        }

        public Task<int> ReadAsync(byte[] buffer, int offset, int count, int timeoutMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(Read(buffer, offset, count, timeoutMs));

        public Task<long> WriteAsync(byte[] buffer, int offset, int count, int timeoutMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(Write(buffer, offset, count, timeoutMs));

        public Stream GetStream() => throw new NotSupportedException();
    }

    /// <summary>
    /// Scripted fastboot peer that answers like <see cref="ScriptedTransport"/> but can report a write as
    /// accepting no byte, which is how a USB bulk-OUT timeout surfaces to the protocol layer. Records the
    /// command writes it refused so a test can assert the frame is re-issued exactly once.
    /// </para>
    /// </summary>
    private sealed class ZeroWriteTransport(Dictionary<string, string> answers) : ICommTransport
    {
        private readonly Queue<byte[]> _replies = [];
        private readonly List<byte> _payload = [];
        private long _dataExpected;

        public List<string> Commands { get; } = [];

        public List<byte[]> Downloads { get; } = [];

        public int ResetCount { get; private set; }

        /// <summary>Command frames reported as accepting no byte, in write order.
        /// <para>按写入顺序记录被报告为零字节接受的命令帧。</para></summary>
        public List<string> Refused { get; } = [];

        /// <summary>Number of command frames left to report as a zero-byte write; they are still delivered on the wire.
        /// <para>接下来仍要报告为零字节写入的命令帧数量；这些帧在线路上仍会被送达。</para></summary>
        public int RefuseCount { get; set; }

        /// <summary>Whether the transport keeps refusing every command frame after <see cref="RefuseCount"/> is spent.
        /// <para><see cref="RefuseCount"/> 用尽后是否继续拒绝每一个命令帧。</para></summary>
        public bool RefuseAlways { get; set; }

        /// <summary>
        /// Whether a refused frame is also withheld from the device, so a probe finds nothing. The default
        /// delivers the frame and only misreports its completion, which is the case the probe must detect.
        /// <para>被拒绝的帧是否同时不送达设备，使探测一无所获。默认会送达该帧而只误报其完成，这正是探测必须
        /// 识别的情形。</para>
        /// </summary>
        public bool SilentWhenRefused { get; set; }

        public bool IsConnected => true;

        public void Dispose()
        {
        }

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        public void Reset() => ResetCount++;

        public long Write(byte[] buffer, int offset, int count, int timeoutMs)
        {
            if (_dataExpected > 0)
            {
                _payload.AddRange(buffer[offset..(offset + count)]);
                if (_payload.Count >= _dataExpected)
                {
                    Downloads.Add([.. _payload]);
                    _payload.Clear();
                    _dataExpected = 0;
                    _replies.Enqueue("OKAY"u8.ToArray());
                }

                return count;
            }

            var command = Encoding.UTF8.GetString(buffer, offset, count);
            Commands.Add(command);
            var refuse = RefuseAlways || RefuseCount > 0;
            if (refuse)
            {
                RefuseCount = Math.Max(0, RefuseCount - 1);
                Refused.Add(command);
                if (SilentWhenRefused)
                {
                    return 0;
                }
            }

            if (command.StartsWith("download:", StringComparison.Ordinal) &&
                long.TryParse(command["download:".Length..], NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var size))
            {
                _dataExpected = size;
                _replies.Enqueue(Encoding.ASCII.GetBytes($"DATA{size:x8}"));
            }
            else if (answers.TryGetValue(command, out var answer))
            {
                _replies.Enqueue(Encoding.UTF8.GetBytes(answer));
            }
            else
            {
                _replies.Enqueue(Encoding.UTF8.GetBytes($"FAILno scripted answer for '{command}'"));
            }

            return refuse ? 0 : count;
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMs)
        {
            if (_replies.Count == 0)
            {
                return 0;
            }

            var reply = _replies.Dequeue();
            reply.CopyTo(buffer, offset);
            return reply.Length;
        }

        public Task<int> ReadAsync(byte[] buffer, int offset, int count, int timeoutMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(Read(buffer, offset, count, timeoutMs));

        public Task<long> WriteAsync(byte[] buffer, int offset, int count, int timeoutMs, CancellationToken cancellationToken = default) =>
            Task.FromResult(Write(buffer, offset, count, timeoutMs));

        public Stream GetStream() => throw new NotSupportedException();
    }
}