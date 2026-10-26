using System.Globalization;
using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Protocols.SpacemiT;
using UotanStudio.Comm.Transports.Abstractions;
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