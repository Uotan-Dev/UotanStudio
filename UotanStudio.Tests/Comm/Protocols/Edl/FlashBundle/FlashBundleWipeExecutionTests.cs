using System.Text;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.FlashBundle;

public sealed class FlashBundleWipeExecutionTests
{
    private const int SectorSize = 4096;
    private const long ReportedSectorCount = 1048576;
    private const int PayloadBytes = 16 * SectorSize;

    private const string AckXml = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <data>
        <response value="ACK" rawmode="false"/>
        </data>
        """;

    private const string ConfigureAckXml = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <data>
        <response value="ACK" rawmode="false" MaxPayloadSizeToTargetInBytes="1048576" Program="true" Erase="true" Patch="true" Wipe="true"/>
        </data>
        """;

    private const string NopResponseXml = """
        <data><log value="booted"/></data><data><response value="ACK"/></data>
        """;

    private const string StorageInfoXml = """
        <data><log value='{"storage_info":{"block_size":4096,"total_blocks":1048576}}'/><response value="ACK"/></data>
        """;

    private const string TinyStorageInfoXml = """
        <data><log value='{"storage_info":{"block_size":4096,"total_blocks":8}}'/><response value="ACK"/></data>
        """;

    private const string NakXml = "<data><response value=\"NAK\" rawmode=\"false\"/></data>";

    private const string ExpectedZerosProgramXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<data>\n"
        + "<program SECTOR_SIZE_IN_BYTES=\"4096\" num_partition_sectors=\"16\" filename=\"Zeros4KB.bin\""
        + " physical_partition_number=\"1\" start_sector=\"32768\" />\n</data>\n";

    private const string SecondLunWipeManifest = """
        <?xml version="1.0" encoding="UTF-8"?>
        <data>
          <wipe SECTOR_SIZE_IN_BYTES="4096" num_partition_sectors="16" physical_partition_number="1"
                start_sector="0x8000" label="userdata-header" zeros_filename="Zeros4KB.bin" />
        </data>
        """;

    private const string RangedWipeManifest = """
        <?xml version="1.0" encoding="UTF-8"?>
        <data>
          <wipe SECTOR_SIZE_IN_BYTES="4096" num_partition_sectors="16" physical_partition_number="0"
                start_sector="32768" label="userdata-header" />
        </data>
        """;

    private const string ConflictingSectorSizeManifest = """
        <?xml version="1.0" encoding="UTF-8"?>
        <data>
          <wipe SECTOR_SIZE_IN_BYTES="512" num_partition_sectors="16" physical_partition_number="0"
                start_sector="32768" label="userdata-header" />
        </data>
        """;

    private const string TwoLunWipeManifest = """
        <?xml version="1.0" encoding="UTF-8"?>
        <data>
          <wipe SECTOR_SIZE_IN_BYTES="4096" num_partition_sectors="16" physical_partition_number="0"
                start_sector="32768" label="leading-blocks" />
          <wipe SECTOR_SIZE_IN_BYTES="4096" num_partition_sectors="8" physical_partition_number="1"
                start_sector="NUM_DISK_SECTORS-8." label="tail-blocks" />
        </data>
        """;

    [Fact]
    public async Task FlashBundleAsync_UnconfirmedWipeInOfflineDryRun_RefusesTheWipeAndSendsNoDeviceBytesAtAll()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", RangedWipeManifest);
        using var transport = CreateTransport(StorageInfoXml, nakProgram: false);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            DryRun = true
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Empty(transport.Writes);
        var preview = Assert.Single(result.WipePreviews);
        Assert.Equal(FlashBundleWipeCommand.MissingConfirmationReason, preview.RefusalReason);
        Assert.Null(preview.CommandXml);
        Assert.False(preview.IsAuthorized);
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.Equal(0L, result.TotalBytesWritten);
    }

    [Fact]
    public async Task FlashBundleAsync_ConfirmedWipesInOfflineDryRun_RecordOnePreviewPerRequestAndSendNoDeviceBytes()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", TwoLunWipeManifest);
        using var transport = CreateTransport(StorageInfoXml, nakProgram: false);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            DryRun = true,
            WipeConfirmation = new WipeConfirmation("WIPE")
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Empty(transport.Writes);
        Assert.Equal(2, result.WipePreviews.Count);
        Assert.Equal("leading-blocks", result.WipePreviews[0].Name);
        Assert.Equal(0, result.WipePreviews[0].PhysicalPartitionNumber);
        Assert.Equal("tail-blocks", result.WipePreviews[1].Name);
        Assert.Equal(1, result.WipePreviews[1].PhysicalPartitionNumber);
        Assert.All(result.WipePreviews, static preview =>
        {
            Assert.Equal(FlashBundleWipeCommand.MissingGeometryReason, preview.RefusalReason);
            Assert.Null(preview.CommandXml);
        });
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.Equal(0L, result.TotalBytesWritten);
    }

    [Fact]
    public async Task PreflightDeviceAsync_TwoWipeRequestsOnTwoLuns_ReportsBothRegionsAndProgramsNeither()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", TwoLunWipeManifest);
        using var transport = CreateTransport(StorageInfoXml, nakProgram: false);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).PreflightDeviceAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            WipeConfirmation = new WipeConfirmation("WIPE")
        }, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(2, result.WipePreviews.Count);
        Assert.DoesNotContain("<program", Concatenated(transport), StringComparison.Ordinal);
        var leading = result.WipePreviews[0];
        Assert.True(leading.IsAuthorized);
        Assert.Equal(32768L, leading.StartSector);
        Assert.Equal(16L, leading.SectorCount);
        Assert.Equal(65536L, leading.BytesToOverwrite);
        var tail = result.WipePreviews[1];
        Assert.True(tail.IsAuthorized);
        Assert.Equal(ReportedSectorCount - 8, tail.StartSector);
        Assert.Equal(8L, tail.SectorCount);
        Assert.Equal(8L * SectorSize, tail.BytesToOverwrite);
        Assert.Contains("start_sector=\"1048568\"", tail.CommandXml!, StringComparison.Ordinal);
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.Equal(0L, result.TotalBytesWritten);
    }

    [Fact]
    public async Task FlashBundleAsync_ConfirmedWipeOnSecondLun_EmitsUpstreamShapedZerosProgramAndAllZeroPayload()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", SecondLunWipeManifest);
        using var transport = CreateTransport(StorageInfoXml, nakProgram: false);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            WipeConfirmation = new WipeConfirmation("WIPE")
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        var programXml = Assert.Single(ProgramDocuments(transport));
        Assert.Equal(ExpectedZerosProgramXml, programXml);
        Assert.Equal(programXml, Assert.Single(result.WipePreviews).CommandXml!);
        Assert.Contains("<getstorageinfo physical_partition_number=\"1\"", Concatenated(transport),
            StringComparison.Ordinal);
        var payload = Assert.Single(transport.Writes, static write => write.Length == PayloadBytes);
        Assert.True(payload.All(static value => value == 0));
        Assert.Equal(1, result.WipeEntriesProcessed);
        Assert.Equal((long)PayloadBytes, result.TotalBytesWritten);
        var evidence = Assert.Single(result.Items, static item => item.Kind == FlashBundleItemKind.Wipe);
        Assert.Equal(CommandOutcomeKind.Completed, evidence.Outcome);
        Assert.Equal((long)PayloadBytes, evidence.AcknowledgedBytes);
        Assert.Equal(ExpectedZerosProgramXml, evidence.CommandXml);
        Assert.Equal(1, evidence.Range!.PhysicalPartitionNumber);
        Assert.Equal(32768L, evidence.Range.StartLba);
        Assert.Equal(16L, evidence.Range.BlockCount);
    }

    [Fact]
    public async Task FlashBundleAsync_WipeCommandNakedByTheTarget_FailsTheWipeWithoutCountingIt()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", SecondLunWipeManifest);
        using var transport = CreateTransport(StorageInfoXml, nakProgram: true);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            WipeConfirmation = new WipeConfirmation("WIPE")
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.Equal(0L, result.TotalBytesWritten);
        Assert.DoesNotContain(transport.Writes, static write => write.Length == PayloadBytes);
        Assert.Contains(result.Errors, static error => error.Contains("Wipe failed for", StringComparison.Ordinal));
        var preview = Assert.Single(result.WipePreviews);
        Assert.True(preview.IsAuthorized);
        var evidence = Assert.Single(result.Items, static item => item.Kind == FlashBundleItemKind.Wipe);
        Assert.NotEqual(CommandOutcomeKind.Completed, evidence.Outcome);
        Assert.Equal(0L, evidence.AcknowledgedBytes);
    }

    [Fact]
    public async Task FlashBundleAsync_WipeRangeBeyondTargetReportedCapacity_StopsBeforeAnyProgramCommand()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", RangedWipeManifest);
        using var transport = CreateTransport(TinyStorageInfoXml, nakProgram: false);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            WipeConfirmation = new WipeConfirmation("WIPE")
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("<program", Concatenated(transport), StringComparison.Ordinal);
        Assert.Empty(result.WipePreviews);
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.Equal(0L, result.TotalBytesWritten);
        Assert.Contains(result.Errors, static error =>
            error.Contains("exceeds the reported physical storage capacity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlashBundleAsync_ManifestWipeDeclaresSectorSizeTheTargetDoesNotReport_RefusesWithoutWriting()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", ConflictingSectorSizeManifest);
        using var transport = CreateTransport(StorageInfoXml, nakProgram: false);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            WipeConfirmation = new WipeConfirmation("WIPE")
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("<program", Concatenated(transport), StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Writes, static write => write.Length == PayloadBytes);
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.Equal(0L, result.TotalBytesWritten);
        var preview = Assert.Single(result.WipePreviews);
        Assert.Equal(FlashBundleWipeCommand.SectorSizeConflictReason, preview.RefusalReason);
        Assert.Null(preview.CommandXml);
        Assert.Contains(result.Errors, static error => error.Contains("Wipe refused for", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlashBundleAsync_SkipWipe_DropsTheManifestWipeAndReportsNoWipeWork()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", RangedWipeManifest);
        using var transport = CreateTransport(StorageInfoXml, nakProgram: false);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            SkipWipe = true,
            WipeConfirmation = new WipeConfirmation("WIPE"),
            AllowEntireStorageWipe = true
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Empty(result.WipePreviews);
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.Equal(0L, result.TotalBytesWritten);
        Assert.DoesNotContain("<program", Concatenated(transport), StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Writes, static write => write.Length == PayloadBytes);
    }

    [Fact]
    public async Task FlashBundleAsync_SkipWipeWithAnOutOfRangeWipeRequest_RunsWithoutValidatingTheWipe()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <data>
              <wipe SECTOR_SIZE_IN_BYTES="4096" num_partition_sectors="16" physical_partition_number="0"
                    start_sector="9223372036854775807" label="impossible" />
            </data>
            """);
        using var transport = CreateTransport(StorageInfoXml, nakProgram: false);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            SkipWipe = true,
            WipeConfirmation = new WipeConfirmation("WIPE")
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Empty(result.WipePreviews);
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.DoesNotContain("<program", Concatenated(transport), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlashBundleAsync_UnconfirmedWipeAgainstAReportingTarget_RefusesBeforeAnyZerosProgramByte()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", SecondLunWipeManifest);
        using var transport = CreateTransport(StorageInfoXml, nakProgram: false);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("<program", Concatenated(transport), StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Writes, static write => write.Length == PayloadBytes);
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.Equal(0L, result.TotalBytesWritten);
        var preview = Assert.Single(result.WipePreviews);
        Assert.Equal(FlashBundleWipeCommand.MissingConfirmationReason, preview.RefusalReason);
        Assert.Null(preview.CommandXml);
        Assert.Null(preview.StartSector);
        Assert.Null(preview.BytesToOverwrite);
        var evidence = Assert.Single(result.Items, static item => item.Kind == FlashBundleItemKind.Wipe);
        Assert.Null(evidence.CommandXml);
        Assert.Equal(0L, evidence.AcknowledgedBytes);
    }

    private static ScriptedCommTransport CreateTransport(string storageInfoXml, bool nakProgram) => new()
    {
        OnWrite = bytes =>
        {
            if (bytes.Length == 0 || bytes[0] != (byte)'<')
            {
                return [];
            }

            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("<getstorageinfo", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(storageInfoXml)];
            }

            if (text.Contains("<nop", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(NopResponseXml)];
            }

            if (text.Contains("<configure", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(ConfigureAckXml)];
            }

            if (text.Contains("<program ", StringComparison.Ordinal))
            {
                if (nakProgram)
                {
                    return [Encoding.UTF8.GetBytes(NakXml)];
                }

                return
                [
                    Encoding.UTF8.GetBytes("<data><response value=\"ACK\" rawmode=\"true\"/></data>"),
                    Encoding.UTF8.GetBytes(AckXml)
                ];
            }

            return [Encoding.UTF8.GetBytes(AckXml)];
        }
    };

    private static EdlSession CreateSession(ScriptedCommTransport transport) => new(transport, new FirehoseConfiguration
    {
        SectorSize = SectorSize,
        AutoDetectSectorSize = false
    });

    private static string Concatenated(ScriptedCommTransport transport)
        => string.Concat(transport.Writes.Select(static write => Encoding.UTF8.GetString(write)));

    private static List<string> ProgramDocuments(ScriptedCommTransport transport) => [.. transport.Writes
        .Where(static write => write.Length > 0 && write[0] == (byte)'<')
        .Select(static write => Encoding.UTF8.GetString(write))
        .Where(static text => text.Contains("<program ", StringComparison.Ordinal))];
}