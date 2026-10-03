using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public class EdlSerialBundleServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlashSerialAsync_NumberedManifests_UsesOneSessionAndMatchingPatches(bool imagesDirectory)
    {
        using var dir = new TempDirectory();
        var prefix = imagesDirectory ? "images/" : "";
        dir.Write(prefix + "rawprogram1.xml", Manifest("second.img", 2));
        dir.Write(prefix + "rawprogram0.xml", Manifest("first.img", 1));
        dir.Write(prefix + "patch0.xml",
            """<data><patch filename="DISK" start_sector="1" byte_offset="0" size_in_bytes="4" value="0" SECTOR_SIZE_IN_BYTES="512"/></data>""");
        dir.WriteBytes(prefix + "first.img", new byte[512]);
        dir.WriteBytes(prefix + "second.img", new byte[512]);
        using var transport = Transport();
        var connects = 0;

        await FlashEdlBundleService.FlashSerialCoreAsync(async token =>
        {
            connects++;
            return await OpenAsync(transport, token);
        }, dir.Path, false, TestContext.Current.CancellationToken);

        Assert.Equal(1, connects);
        var commands = Commands(transport);
        Assert.Equal(new[] { "configure", "getstorageinfo", "getstorageinfo", "getstorageinfo", "program", "patch", "getstorageinfo", "program" },
            commands.Select(command => command.Name.LocalName));
        Assert.Equal(new[] { "1", "2" }, commands.Where(command => command.Name == "program")
            .Select(command => (string?)command.Attribute("start_sector")));
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task FlashSerialAsync_LaterManifestExceedsDevice_PreflightsAllBeforeFirstProgram()
    {
        using var dir = new TempDirectory();
        dir.Write("rawprogram0.xml", Manifest("first.img", 1));
        dir.Write("rawprogram1.xml", Manifest("first.img", int.MaxValue));
        var source = dir.WriteBytes("first.img", new byte[512]);
        using var transport = Transport();

        await Assert.ThrowsAsync<InvalidDataException>(() => FlashEdlBundleService.FlashSerialCoreAsync(
            token => OpenAsync(transport, token), dir.Path, false, TestContext.Current.CancellationToken));

        Assert.Equal(new[] { "configure", "getstorageinfo", "getstorageinfo" },
            Commands(transport).Select(command => command.Name.LocalName));
        Assert.Equal(1, transport.DisposeCount);
        using var exclusive = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task FlashSerialAsync_InvalidLaterSource_StopsBeforeFirstWrite()
    {
        using var dir = new TempDirectory();
        dir.Write("rawprogram0.xml", Manifest("first.img", 1));
        dir.Write("rawprogram1.xml", Manifest("second.img", 2));
        dir.WriteBytes("first.img", new byte[512]);
        dir.WriteBytes("second.img", []);
        using var transport = Transport();

        await Assert.ThrowsAsync<InvalidDataException>(() => FlashEdlBundleService.FlashSerialCoreAsync(
            token => OpenAsync(transport, token), dir.Path, false, TestContext.Current.CancellationToken));

        Assert.Equal("configure", Assert.Single(Commands(transport)).Name.LocalName);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlashSerialAsync_UfsFilePresent_RequiresExplicitOptIn(bool includeUfs)
    {
        using var dir = new TempDirectory();
        dir.Write("rawprogram0.xml", Manifest("first.img", 1));
        dir.WriteBytes("first.img", new byte[512]);
        dir.Write("ufs_provision.xml", "not valid XML");
        using var transport = Transport();

        var run = FlashEdlBundleService.FlashSerialCoreAsync(token => OpenAsync(transport, token),
            dir.Path, includeUfs, TestContext.Current.CancellationToken);
        if (includeUfs)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => run);
            Assert.Equal("configure", Assert.Single(Commands(transport)).Name.LocalName);
        }
        else
        {
            await run;
            Assert.Contains(Commands(transport), command => command.Name == "program");
            Assert.DoesNotContain(Commands(transport), command => command.Name == "ufs");
        }
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task FlashSerialAsync_FirstProgramRejected_DoesNotTrySecondManifest()
    {
        using var dir = new TempDirectory();
        dir.Write("rawprogram0.xml", Manifest("first.img", 1));
        dir.Write("rawprogram1.xml", Manifest("first.img", 2));
        dir.WriteBytes("first.img", new byte[512]);
        using var transport = Transport(rejectProgram: true);

        await Assert.ThrowsAsync<IOException>(() => FlashEdlBundleService.FlashSerialCoreAsync(
            token => OpenAsync(transport, token), dir.Path, false, TestContext.Current.CancellationToken));

        Assert.Single(Commands(transport), command => command.Name == "program");
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlashSerialAsync_MultipleManifests_ProvisionsOnceAfterAllPrograms(bool rejectProgram)
    {
        using var dir = new TempDirectory();
        dir.Write("rawprogram0.xml", Manifest("first.img", 1));
        dir.Write("rawprogram1.xml", Manifest("first.img", 2));
        dir.WriteBytes("first.img", new byte[512]);
        dir.Write("ufs_provision.xml", """
            <data>
              <ufs type="common" bNumberLU="1"/>
              <ufs type="body" LUNum="0"/>
              <ufs type="epilogue" commit="0"/>
            </data>
            """);
        using var transport = Transport(rejectProgram);

        var run = FlashEdlBundleService.FlashSerialCoreAsync(token => OpenAsync(transport, token),
            dir.Path, true, TestContext.Current.CancellationToken);
        if (rejectProgram)
        {
            await Assert.ThrowsAsync<IOException>(() => run);
            Assert.DoesNotContain(Commands(transport), command => command.Name == "ufs");
        }
        else
        {
            await run;
            Assert.Equal(new[] { "configure", "getstorageinfo", "getstorageinfo", "getstorageinfo", "program", "getstorageinfo", "program", "ufs", "ufs", "ufs" },
                Commands(transport).Select(command => command.Name.LocalName));
        }
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task FlashSerialAsync_InvalidProvisioningAtEnd_PreflightsBeforeEarlierPrograms()
    {
        using var dir = new TempDirectory();
        dir.Write("rawprogram0.xml", Manifest("first.img", 1));
        dir.Write("rawprogram1.xml", Manifest("first.img", 2));
        dir.WriteBytes("first.img", new byte[512]);
        dir.Write("ufs_provision.xml", "<data><ufs type='epilogue' commit='1'/></data>");
        using var transport = Transport();

        await Assert.ThrowsAsync<InvalidDataException>(() => FlashEdlBundleService.FlashSerialCoreAsync(
            token => OpenAsync(transport, token), dir.Path, true, TestContext.Current.CancellationToken));

        Assert.Equal("configure", Assert.Single(Commands(transport)).Name.LocalName);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData("missing-image")]
    [InlineData("no-manifest")]
    [InlineData("cancelled")]
    public async Task FlashSerialAsync_LocalRejection_DoesNotConnect(string failure)
    {
        using var dir = new TempDirectory();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (failure == "missing-image")
        {
            dir.Write("rawprogram0.xml", Manifest("missing.img", 1));
        }
        if (failure == "cancelled")
        {
            cancellation.Cancel();
        }
        var connects = 0;
        var run = FlashEdlBundleService.FlashSerialCoreAsync(_ =>
        {
            connects++;
            throw new InvalidOperationException();
        }, dir.Path, false, cancellation.Token);

        if (failure == "cancelled")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else if (failure == "missing-image")
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => run);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => run);
        }
        Assert.Equal(0, connects);
    }

    private static string Manifest(string file, int sector) =>
        $"""<data><program filename="{file}" start_sector="{sector}" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"/></data>""";

    [Fact]
    public async Task FlashSerialAsync_Tuning_AppliesSlotAndLeadingWipe()
    {
        using var dir = new TempDirectory();
        dir.Write("rawprogram0.xml", Manifest("first.img", 1));
        dir.WriteBytes("first.img", new byte[512]);
        using var transport = Transport();

        await FlashEdlBundleService.FlashSerialCoreAsync(token => OpenAsync(transport, token),
            dir.Path, false, TestContext.Current.CancellationToken, tuning: new EdlBundleTuning
            {
                Slot = 2,
                WipeFirstSectors = 1
            });

        var commands = Commands(transport);
        var program = Assert.Single(commands, command => command.Name == "program"
            && (string?)command.Attribute("filename") == "first.img");
        Assert.Equal("2", (string?)program.Attribute("slot"));
        Assert.Contains(commands, command => command.Name == "program"
            && (string?)command.Attribute("filename") == "Zeros16KB.bin");
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task FlashSerialAsync_TuningOmitted_KeepsDefaultSlotAndSkipsWipe()
    {
        using var dir = new TempDirectory();
        dir.Write("rawprogram0.xml", Manifest("first.img", 1));
        dir.WriteBytes("first.img", new byte[512]);
        using var transport = Transport();

        await FlashEdlBundleService.FlashSerialCoreAsync(token => OpenAsync(transport, token),
            dir.Path, false, TestContext.Current.CancellationToken);

        var commands = Commands(transport);
        var program = Assert.Single(commands, command => command.Name == "program");
        Assert.Null((string?)program.Attribute("slot"));
        Assert.DoesNotContain(commands, command => command.Name == "program"
            && (string?)command.Attribute("filename") == "Zeros16KB.bin");
    }

    [Fact]
    public async Task FlashSerialAsync_OverrideSectorSize_OverridesDisagreeingManifestDeclaration()
    {
        using var dir = new TempDirectory();
        dir.Write("rawprogram0.xml",
            """<data><program filename="first.img" start_sector="1" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="1024"/></data>""");
        dir.WriteBytes("first.img", new byte[512]);
        using var transport = Transport();

        await FlashEdlBundleService.FlashSerialCoreAsync(token => OpenAsync(transport, token),
            dir.Path, false, TestContext.Current.CancellationToken, tuning: new EdlBundleTuning
            {
                OverrideSectorSize = 512
            });

        var program = Assert.Single(Commands(transport), command => command.Name == "program");
        Assert.Equal("512", (string?)program.Attribute("SECTOR_SIZE_IN_BYTES"));
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task FlashSerialAsync_ManifestSectorSizeDisagreesWithoutOverride_StopsBeforeProgram()
    {
        using var dir = new TempDirectory();
        dir.Write("rawprogram0.xml",
            """<data><program filename="first.img" start_sector="1" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="1024"/></data>""");
        dir.WriteBytes("first.img", new byte[512]);
        using var transport = Transport();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            FlashEdlBundleService.FlashSerialCoreAsync(token => OpenAsync(transport, token),
                dir.Path, false, TestContext.Current.CancellationToken));

        Assert.Contains("sector size", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("configure", Assert.Single(Commands(transport)).Name.LocalName);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task FlashSerialAsync_CancelledAfterConnect_DisposesWithoutProgramming()
    {
        using var dir = new TempDirectory();
        dir.Write("rawprogram0.xml", Manifest("first.img", 1));
        dir.WriteBytes("first.img", new byte[512]);
        using var transport = Transport();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FlashEdlBundleService.FlashSerialCoreAsync(async token =>
        {
            var session = await OpenAsync(transport, token);
            cancellation.Cancel();
            return session;
        }, dir.Path, false, cancellation.Token));

        Assert.Equal("configure", Assert.Single(Commands(transport)).Name.LocalName);
        Assert.Equal(1, transport.DisposeCount);
    }

    private static async Task<EdlSession> OpenAsync(ScriptedCommTransport transport, CancellationToken cancellationToken)
    {
        var session = new EdlSession(transport,
            new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });
        await session.InitializeAsync(null, cancellationToken);
        return session;
    }

    private static XElement[] Commands(ScriptedCommTransport transport) => transport.Writes
        .Where(bytes => bytes.Length > 0 && bytes[0] == '<')
        .Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single()).ToArray();

    private static ScriptedCommTransport Transport(bool rejectProgram = false)
    {
        const string ack = "<data><response value='ACK' rawmode='false'/></data>";
        const string configureAck = """
            <data><response value='ACK' rawmode='false' MaxPayloadSizeToTargetInBytes='1048576' Erase='true' Patch='true' Wipe='true' UfsProvisioning='true'/></data>
            """;
        var transport = new ScriptedCommTransport();
        transport.Enqueue("<?xml version='1.0'?><data></data>".PadRight(48));
        transport.OnWrite = bytes =>
        {
            if (bytes[0] != '<')
            {
                return [Encoding.UTF8.GetBytes(ack)];
            }
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single().Name.LocalName;
            var response = command switch
            {
                "configure" => configureAck,
                "getstorageinfo" => """<data><log value='{"storage_info":{"block_size":512,"total_blocks":100}}'/><response value='ACK'/></data>""",
                "program" => rejectProgram ? "<data><response value='NAK'/></data>"
                    : "<data><response value='ACK' rawmode='true'/></data>",
                _ => ack
            };
            return [Encoding.UTF8.GetBytes(response)];
        };
        return transport;
    }
}