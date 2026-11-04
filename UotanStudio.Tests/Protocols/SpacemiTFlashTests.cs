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

        Assert.NotNull(package.ResolveFile("factory/FSBL.bin"));
        Assert.NotNull(package.ResolveFile("u-boot.itb"));
        Assert.Null(package.ResolveFile("/partition_.+\\.json$/"));
        Assert.Null(package.ResolveFile("missing.bin"));
        Assert.Null(package.ResolveFile("../outside.bin"));
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

    [Fact]
    public void Flasher_RejectsRegexImageReferenceInsteadOfSilentlySkipping()
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

        var transport = new ScriptedTransport(new Dictionary<string, string>
        {
            ["getvar:mtd-size"] = "OKAY4M",
            ["getvar:max-download-size"] = "OKAY0x00100000",
        });
        var session = new FastbootSession(transport);
        var package = SpacemiTFlashPackage.Load(_root);
        var ex = Assert.Throws<FastbootException>(() =>
            new SpacemiTFlasher(package, ConfirmedOptions()).Execute(
                session, _ => session, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("regex", ex.Message, StringComparison.OrdinalIgnoreCase);
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
        const long maxDownload = 2L * 1024 * 1024;
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

        var payload = new byte[4L * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        File.WriteAllBytes(Path.Combine(_root, "big.bin"), payload);

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

    private static SpacemiTFlashOptions ConfirmedOptions() => new()
    {
        FlashConfirmation = SpacemiTFlashOptions.RequiredConfirmation,
        ReEnumerateSettleMs = 0,
    };

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
    /// the scripted table (or by the DATA/OKAY download dance), so flasher decisions such as
    /// skip_when guards and multi_flash table selection can be asserted end to end.
    /// </summary>
    private sealed class ScriptedTransport(Dictionary<string, string> answers) : ICommTransport
    {
        private readonly Queue<byte[]> _replies = [];
        private readonly List<byte> _payload = [];
        private readonly HashSet<string> _failOnce = [];
        private long _dataExpected;

        public List<string> Commands { get; } = [];

        public List<byte[]> Downloads { get; } = [];

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
            else if (answers.TryGetValue(command, out var answer))
            {
                _replies.Enqueue(Encoding.UTF8.GetBytes(answer));
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
}