using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.FlashBundle;

public class FlashBundleWipeGateTests
{
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

    private const string ZerosDocument =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<data>\n"
        + "<program SECTOR_SIZE_IN_BYTES=\"4096\" num_partition_sectors=\"16\" filename=\"Zeros16KB.bin\""
        + " physical_partition_number=\"0\" start_sector=\"32768\" />\n</data>\n";

    private const string RangedWipeManifest = """
        <?xml version="1.0" encoding="UTF-8"?>
        <data>
          <wipe SECTOR_SIZE_IN_BYTES="4096" num_partition_sectors="16" physical_partition_number="0"
                start_sector="32768" label="userdata-header" />
        </data>
        """;

    private const string EntireStorageWipeManifest = """
        <?xml version="1.0" encoding="UTF-8"?>
        <data>
          <wipe SECTOR_SIZE_IN_BYTES="4096" all="true" physical_partition_number="0" start_sector="0"
                label="userdata" />
        </data>
        """;

    [Fact]
    public void BuildWipe_ResolvedRange_EmitsExactProgramWithZerosDocument()
    {
        Assert.Equal(ZerosDocument, FirehoseXmlBuilder.BuildWipe(4096, 16, 0, 32768));
    }

    [Fact]
    public void BuildWipe_NegativeSectorCount_RefusesWithoutProducingText()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => FirehoseXmlBuilder.BuildWipe(4096, -1, 0, 0));
        Assert.Contains("numPartitionSectors", error.ParamName ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWipe_EndSectorOverflow_RefusesWithoutProducingText()
    {
        Assert.Throws<OverflowException>(() => FirehoseXmlBuilder.BuildWipe(4096, 4, 0, long.MaxValue - 2));
    }

    [Fact]
    public void WipeManifestParser_RangedAndEntireElements_ReadEveryAttribute()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("rawprogram0.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <data>
              <program SECTOR_SIZE_IN_BYTES="4096" filename="boot.img" physical_partition_number="0"
                       start_sector="0x8000" num_partition_sectors="4" label="boot" />
              <wipe SECTOR_SIZE_IN_BYTES="4096" num_partition_sectors="16" physical_partition_number="1"
                    start_sector="NUM_DISK_SECTORS-8." label="tail" zeros_filename="Zeros4KB.bin" slot="2" />
              <wipe all="1" physical_partition_number="0" label="userdata" />
            </data>
            """);

        var wipes = WipeManifestParser.Parse(path);

        Assert.Equal(2, wipes.Count);
        var tail = wipes[0];
        Assert.Equal("NUM_DISK_SECTORS-8.", tail.StartSector);
        Assert.Equal(16L, tail.NumPartitionSectors);
        Assert.Equal(1, tail.PhysicalPartitionNumber);
        Assert.Equal("Zeros4KB.bin", tail.ZeroFilename);
        Assert.Equal(2, tail.Slot);
        Assert.False(tail.EntireStorage);
        var entire = wipes[1];
        Assert.True(entire.EntireStorage);
        Assert.Equal(WipeManifestEntry.DefaultZeroFilename, entire.ZeroFilename);
        Assert.Null(entire.ToPlanRange(4096));
        Assert.Single(RawProgramParser.Parse(path));
    }

    [Fact]
    public void TryPrepare_MissingConfirmation_RefusesWithNoCommandDocument()
    {
        var (authorized, command, refusal) = Attempt(new WipeManifestEntry
        {
            NumPartitionSectors = 16,
            StartSector = "32768"
        }, Geometry(), new FlashBundleOptions());

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.MissingConfirmationReason, refusal);
    }

    [Fact]
    public void TryPrepare_LowercaseConfirmationPhrase_RefusesBecauseAuthorizationIsCaseSensitive()
    {
        var (authorized, command, refusal) = Attempt(new WipeManifestEntry
        {
            NumPartitionSectors = 16,
            StartSector = "32768"
        }, Geometry(), new FlashBundleOptions
        {
            WipeConfirmation = new WipeConfirmation("wipe")
        });

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.MissingConfirmationReason, refusal);
    }

    [Fact]
    public void TryPrepare_EntireStorageWithoutSecondFlag_RefusesAsAmbiguous()
    {
        var (authorized, command, refusal) = Attempt(new WipeManifestEntry
        {
            EntireStorage = true
        }, Geometry(), new FlashBundleOptions
        {
            WipeConfirmation = new WipeConfirmation("WIPE")
        });

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.AmbiguousEntireStorageReason, refusal);
    }

    [Fact]
    public void TryPrepare_WithoutReportedGeometry_Refuses()
    {
        var (authorized, command, refusal) = Attempt(new WipeManifestEntry
        {
            NumPartitionSectors = 16,
            StartSector = "32768"
        }, geometry: null, new FlashBundleOptions
        {
            WipeConfirmation = new WipeConfirmation("WIPE")
        });

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.MissingGeometryReason, refusal);
    }

    [Theory]
    [InlineData("1048570", 16)]
    [InlineData("0", 1048577)]
    public void TryPrepare_RegionBeyondReportedCapacity_Refuses(string startSector, long sectors)
    {
        var (authorized, command, refusal) = Attempt(new WipeManifestEntry
        {
            NumPartitionSectors = sectors,
            StartSector = startSector
        }, Geometry(), new FlashBundleOptions
        {
            WipeConfirmation = new WipeConfirmation("WIPE")
        });

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.UnresolvableRegionReason, refusal);
    }

    [Fact]
    public void TryPrepare_ConfirmedResolvableRegion_EmitsResolvedZerosDocument()
    {
        var (authorized, command, refusal) = Attempt(new WipeManifestEntry
        {
            NumPartitionSectors = 16,
            StartSector = "0x8000"
        }, Geometry(), new FlashBundleOptions
        {
            WipeConfirmation = new WipeConfirmation("WIPE")
        });

        Assert.True(authorized);
        Assert.Null(refusal);
        Assert.NotNull(command);
        Assert.Equal(32768L, command.StartSector);
        Assert.Equal(16L, command.SectorCount);
        Assert.Equal(65536L, command.ByteCount);
        Assert.Equal(ZerosDocument, command.CommandXml);
    }

    [Fact]
    public void TryPrepare_EntireStorageWithSecondFlag_CoversEveryReportedBlock()
    {
        var (authorized, command, refusal) = Attempt(new WipeManifestEntry
        {
            EntireStorage = true
        }, Geometry(), new FlashBundleOptions
        {
            WipeConfirmation = new WipeConfirmation("WIPE"),
            AllowEntireStorageWipe = true
        });

        Assert.True(authorized);
        Assert.Null(refusal);
        Assert.NotNull(command);
        Assert.Equal(0L, command.StartSector);
        Assert.Equal(1048576L, command.SectorCount);
        Assert.Equal(1048576L * 4096, command.ByteCount);
    }

    [Fact]
    public void Preview_AuthorizedRequest_ReportsRegionAndDocument()
    {
        var preview = FlashBundleWipeCommand.Preview(new WipeManifestEntry
        {
            NumPartitionSectors = 16,
            StartSector = "32768",
            Label = "userdata-header"
        }, new FlashBundleOptions
        {
            WipeConfirmation = new WipeConfirmation("WIPE")
        }, Geometry());

        Assert.True(preview.IsAuthorized);
        Assert.Equal("userdata-header", preview.Name);
        Assert.Equal(32768, preview.StartSector);
        Assert.Equal(16, preview.SectorCount);
        Assert.Equal(65536L, preview.BytesToOverwrite);
        Assert.Null(preview.RefusalReason);
    }

    [Fact]
    public async Task FlashBundleAsync_OfflineDryRun_ReportsRefusalAndWritesNothing()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", RangedWipeManifest);
        using var transport = CreateTransport();
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            DryRun = true,
            WipeConfirmation = new WipeConfirmation("WIPE")
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        var preview = Assert.Single(result.WipePreviews);
        Assert.False(preview.IsAuthorized);
        Assert.Equal(FlashBundleWipeCommand.MissingGeometryReason, preview.RefusalReason);
        Assert.Empty(transport.Writes);
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.Equal(0L, result.TotalBytesWritten);
    }

    [Fact]
    public async Task PreflightDeviceAsync_ConfirmedWipe_ReportsExactCommandWithoutProgramWrite()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", RangedWipeManifest);
        using var transport = CreateTransport();
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).PreflightDeviceAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            WipeConfirmation = new WipeConfirmation("WIPE")
        }, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        var preview = Assert.Single(result.WipePreviews);
        Assert.Equal(ZerosDocument, preview.CommandXml);
        Assert.Equal(65536L, preview.BytesToOverwrite);
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.Equal(0L, result.TotalBytesWritten);
        Assert.DoesNotContain("<program", Concatenated(transport), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlashBundleAsync_ConfirmedWipeEntry_SendsThePreviewedDocumentAndZeroPayload()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", RangedWipeManifest);
        using var transport = CreateTransport();
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            WipeConfirmation = new WipeConfirmation("WIPE")
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(1, result.WipeEntriesProcessed);
        Assert.Equal(65536L, result.TotalBytesWritten);
        var preview = Assert.Single(result.WipePreviews);
        var programXml = Encoding.UTF8.GetString(Assert.Single(transport.Writes,
            write => write.Length > 0 && write[0] == (byte)'<'
                && Encoding.UTF8.GetString(write).Contains("<program ", StringComparison.Ordinal)));
        Assert.Equal(preview.CommandXml, programXml);
        var payload = Assert.Single(transport.Writes, write => write.Length == 65536);
        Assert.True(payload.All(static value => value == 0));
        var evidence = Assert.Single(result.Items, item => item.Kind == FlashBundleItemKind.Wipe);
        Assert.Equal(ZerosDocument, evidence.CommandXml);
        Assert.Equal(32768L, evidence.Range!.StartLba);
    }

    [Fact]
    public async Task FlashBundleAsync_UnconfirmedWipeEntry_EmitsZeroWritesAndFails()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", RangedWipeManifest);
        using var transport = CreateTransport();
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.Equal(0L, result.TotalBytesWritten);
        Assert.DoesNotContain("<program", Concatenated(transport), StringComparison.Ordinal);
        Assert.DoesNotContain("<erase", Concatenated(transport), StringComparison.Ordinal);
        var preview = Assert.Single(result.WipePreviews);
        Assert.Equal(FlashBundleWipeCommand.MissingConfirmationReason, preview.RefusalReason);
        Assert.Contains(result.Errors, static error => error.Contains("WIPE confirmation", StringComparison.Ordinal));
        var evidence = Assert.Single(result.Items, item => item.Kind == FlashBundleItemKind.Wipe);
        Assert.Null(evidence.CommandXml);
        Assert.Equal(0L, evidence.AcknowledgedBytes);
    }

    [Fact]
    public async Task FlashBundleAsync_WholeStorageWipeWithoutSecondFlag_WritesNothing()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", EntireStorageWipeManifest);
        using var transport = CreateTransport();
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            WipeConfirmation = new WipeConfirmation("WIPE")
        }, progress: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("<program", Concatenated(transport), StringComparison.Ordinal);
        Assert.Equal(FlashBundleWipeCommand.AmbiguousEntireStorageReason,
            Assert.Single(result.WipePreviews).RefusalReason);
    }

    private static (bool Authorized, FlashBundleWipeCommand? Command, string? Refusal) Attempt(
        WipeManifestEntry entry, FirehoseStorageGeometry? geometry, FlashBundleOptions options)
    {
        var authorized = FlashBundleWipeCommand.TryPrepare(entry, options, geometry, out var command, out var refusal);
        return (authorized, command, refusal);
    }

    private static FirehoseStorageGeometry Geometry() => new(0, 4096, 1048576);

    private static string Concatenated(ScriptedCommTransport transport)
        => string.Concat(transport.Writes.Select(Encoding.UTF8.GetString));

    private static EdlSession CreateSession(ScriptedCommTransport transport)
        => new(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            AutoDetectSectorSize = false
        });

    private static ScriptedCommTransport CreateTransport() => new()
    {
        OnWrite = static bytes =>
        {
            if (bytes.Length == 0 || bytes[0] != (byte)'<')
            {
                return [];
            }

            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("<getstorageinfo", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(StorageInfoXml)];
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
                return
                [
                    Encoding.UTF8.GetBytes("<data><response value=\"ACK\" rawmode=\"true\"/></data>"),
                    Encoding.UTF8.GetBytes(AckXml)
                ];
            }

            return [Encoding.UTF8.GetBytes(AckXml)];
        }
    };
}