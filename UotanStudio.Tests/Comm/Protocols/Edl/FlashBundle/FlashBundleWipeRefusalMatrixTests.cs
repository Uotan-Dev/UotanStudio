using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.FlashBundle;

public sealed class FlashBundleWipeRefusalMatrixTests
{
    private const long SectorSize = 4096;
    private const long ReportedSectorCount = 1048576;

    private const string AuthorizedDocument =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<data>\n"
        + "<program SECTOR_SIZE_IN_BYTES=\"4096\" num_partition_sectors=\"16\" filename=\"Zeros4KB.bin\""
        + " physical_partition_number=\"1\" start_sector=\"32768\" slot=\"2\" />\n</data>\n";

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("wipe")]
    [InlineData("Wipe")]
    [InlineData("WIPE ")]
    [InlineData(" WIPE")]
    [InlineData("WIPEX")]
    [InlineData("YES")]
    public void TryPrepare_ConfirmationPhraseNotExactlyWipe_RefusesWithMissingConfirmationReason(string phrase)
    {
        var (authorized, command, refusal) = Attempt(
            Ranged(), Geometry(), new FlashBundleOptions { WipeConfirmation = new WipeConfirmation(phrase) });

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.MissingConfirmationReason, refusal);
    }

    [Fact]
    public void TryPrepare_ConfirmationGateRunsBeforeGeometryAndRegionGates_RefusesWithMissingConfirmationReason()
    {
        var entry = new WipeManifestEntry
        {
            EntireStorage = true,
            PhysicalPartitionNumber = -1,
            SectorSizeInBytes = 512,
            StartSector = "bogus",
            NumPartitionSectors = 0
        };

        var (authorized, command, refusal) = Attempt(entry, geometry: null, new FlashBundleOptions
        {
            AllowEntireStorageWipe = true
        });

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.MissingConfirmationReason, refusal);
    }

    [Fact]
    public void TryPrepare_EntireStorageWithoutSecondFlag_RefusesBeforeReportedGeometryIsRequired()
    {
        var (authorized, command, refusal) = Attempt(
            new WipeManifestEntry { EntireStorage = true, Label = "userdata" },
            geometry: null,
            Authorized());

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.AmbiguousEntireStorageReason, refusal);
    }

    [Fact]
    public void TryPrepare_EntireStorageWithNegativePartition_RefusedByTheCommandBuildGate()
    {
        var (authorized, command, refusal) = Attempt(
            new WipeManifestEntry { EntireStorage = true, PhysicalPartitionNumber = -1 },
            Geometry(),
            new FlashBundleOptions
            {
                WipeConfirmation = new WipeConfirmation("WIPE"),
                AllowEntireStorageWipe = true
            });

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.CommandBuildReason, refusal);
    }

    [Fact]
    public void TryPrepare_DeclaredNegativeSlot_RefusedByTheCommandBuildGate()
    {
        var (authorized, command, refusal) = Attempt(
            Ranged(slot: -1), Geometry(), Authorized());

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.CommandBuildReason, refusal);
    }

    [Fact]
    public void TryPrepare_ManifestSectorSizeContradictsReportedGeometry_RefusesWithSectorSizeConflictReason()
    {
        var (authorized, command, refusal) = Attempt(
            Ranged(sectorSize: 512), Geometry(), Authorized());

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.SectorSizeConflictReason, refusal);
    }

    [Fact]
    public void TryPrepare_ManifestDeclaresNoSectorSize_ResolvesAgainstTheReportedSectorSize()
    {
        var (authorized, command, refusal) = Attempt(
            Ranged(sectorSize: 0), Geometry(), Authorized());

        Assert.True(authorized);
        Assert.Null(refusal);
        Assert.NotNull(command);
        Assert.Equal(SectorSize, command!.SectorSize);
        Assert.Equal(65536L, command.ByteCount);
        Assert.Contains("SECTOR_SIZE_IN_BYTES=\"4096\"", command.CommandXml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0x")]
    [InlineData("-1")]
    [InlineData("16x")]
    [InlineData("NUM_DISK_SECTORS-0.")]
    [InlineData("NUM_DISK_SECTORS-16x.")]
    public void TryPrepare_StartSectorOutsideTheBoundedAddressGrammar_RefusesWithUnresolvableRegionReason(
        string startSector)
    {
        var (authorized, command, refusal) = Attempt(
            Ranged(start: startSector), Geometry(), Authorized());

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.UnresolvableRegionReason, refusal);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-16L)]
    public void TryPrepare_NonPositiveSectorCount_RefusesWithUnresolvableRegionReason(long sectors)
    {
        var (authorized, command, refusal) = Attempt(
            Ranged(sectors: sectors), Geometry(), Authorized());

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.UnresolvableRegionReason, refusal);
    }

    [Fact]
    public void TryPrepare_RelativeRangeWiderThanItsOwnOffset_RefusesWithUnresolvableRegionReason()
    {
        var (authorized, command, refusal) = Attempt(
            Ranged(sectors: 8, start: "NUM_DISK_SECTORS-4."), Geometry(), Authorized());

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.UnresolvableRegionReason, refusal);
    }

    [Fact]
    public void TryPrepare_RelativeOffsetThatOverflowsTheReportedCapacity_RefusesWithUnresolvableRegionReason()
    {
        var (authorized, command, refusal) = Attempt(
            Ranged(sectors: 1, start: "NUM_DISK_SECTORS-9223372036854775807."), Geometry(), Authorized());

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.UnresolvableRegionReason, refusal);
    }

    [Fact]
    public void TryPrepare_AbsoluteStartBeyondReportedCapacity_RefusesWithUnresolvableRegionReason()
    {
        var (authorized, command, refusal) = Attempt(
            Ranged(sectors: 16, start: "9223372036854775807"), Geometry(), Authorized());

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.UnresolvableRegionReason, refusal);
    }

    [Fact]
    public void TryPrepare_RangeEndingExactlyAtReportedCapacity_IsAuthorized()
    {
        var (authorized, command, refusal) = Attempt(
            Ranged(sectors: 16, start: "1048560"), Geometry(), Authorized());

        Assert.True(authorized);
        Assert.Null(refusal);
        Assert.NotNull(command);
        Assert.Equal(1048560L, command!.StartSector);
        Assert.Equal(16L, command.SectorCount);
        Assert.Equal(65536L, command.ByteCount);
    }

    [Fact]
    public void TryPrepare_RangeEndingOneSectorBeyondReportedCapacity_Refuses()
    {
        var (authorized, command, refusal) = Attempt(
            Ranged(sectors: 16, start: "1048561"), Geometry(), Authorized());

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.UnresolvableRegionReason, refusal);
    }

    [Fact]
    public void TryPrepare_AuthorizedRegionOnReportedPartitionAndSlot_EmitsUpstreamShapedZerosProgramDocument()
    {
        var (authorized, command, refusal) = Attempt(
            Ranged(partition: 1, start: "0x8000", slot: 2, zeros: "Zeros4KB.bin", label: "userdata-header"),
            Geometry(1), Authorized());

        Assert.True(authorized);
        Assert.Null(refusal);
        Assert.NotNull(command);
        Assert.Equal(32768L, command!.StartSector);
        Assert.Equal(16L, command.SectorCount);
        Assert.Equal(1, command.PhysicalPartitionNumber);
        Assert.Equal(SectorSize, command.SectorSize);
        Assert.Equal(2, command.Slot);
        Assert.Equal("Zeros4KB.bin", command.ZeroFilename);
        Assert.Equal(65536L, command.ByteCount);
        Assert.Equal(AuthorizedDocument, command.CommandXml);
    }

    [Fact]
    public void Preview_RefusedRequest_ReportsTheGateReasonWithoutAnyRegionFields()
    {
        var preview = FlashBundleWipeCommand.Preview(
            Ranged(sectorSize: 512), Authorized(), Geometry());

        Assert.False(preview.IsAuthorized);
        Assert.Null(preview.CommandXml);
        Assert.Null(preview.StartSector);
        Assert.Null(preview.SectorCount);
        Assert.Null(preview.BytesToOverwrite);
        Assert.Equal(FlashBundleWipeCommand.SectorSizeConflictReason, preview.RefusalReason);
        Assert.Equal("userdata-header", preview.Name);
        Assert.Equal(0, preview.PhysicalPartitionNumber);
    }

    [Fact]
    public void Preview_AuthorizedRequest_ReportsTheSameCommandTryPrepareBuilt()
    {
        var entry = Ranged(partition: 1, start: "0x8000", slot: 2, zeros: "Zeros4KB.bin");
        var options = Authorized();
        var geometry = Geometry(1);
        var preview = FlashBundleWipeCommand.Preview(entry, options, geometry);
        var (_, command, _) = Attempt(entry, geometry, options);

        Assert.True(preview.IsAuthorized);
        Assert.NotNull(command);
        Assert.Equal(command!.CommandXml, preview.CommandXml);
        Assert.Equal(command.StartSector, preview.StartSector);
        Assert.Equal(command.SectorCount, preview.SectorCount);
        Assert.Equal(command.ByteCount, preview.BytesToOverwrite);
        Assert.Equal(1, preview.PhysicalPartitionNumber);
        Assert.Null(preview.RefusalReason);
    }

    [Fact]
    public void Preview_NamelessRequest_FallsBackToTheZerosPayloadFilename()
    {
        var preview = FlashBundleWipeCommand.Preview(
            Ranged(label: "", zeros: "Zeros8KB.bin"), Authorized(), Geometry());

        Assert.Equal("Zeros8KB.bin", preview.Name);
        Assert.True(preview.IsAuthorized);
    }

    [Fact]
    public void RefusalReasonConstants_EveryGateStatesThatNothingWasWritten()
    {
        var reasons = new[]
        {
            FlashBundleWipeCommand.MissingConfirmationReason,
            FlashBundleWipeCommand.AmbiguousEntireStorageReason,
            FlashBundleWipeCommand.MissingGeometryReason,
            FlashBundleWipeCommand.UnresolvableRegionReason,
            FlashBundleWipeCommand.SectorSizeConflictReason,
            FlashBundleWipeCommand.CommandBuildReason
        };

        Assert.Equal(6, reasons.Distinct(StringComparer.Ordinal).Count());
        foreach (var reason in reasons)
        {
            Assert.EndsWith("nothing was written.", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TryPrepare_EntireStorageWithConflictingSectorSize_RefusesAsAmbiguousBeforeTheSectorSizeGate()
    {
        var (authorized, command, refusal) = Attempt(
            new WipeManifestEntry { EntireStorage = true, SectorSizeInBytes = 512 }, Geometry(), Authorized());

        Assert.False(authorized);
        Assert.Null(command);
        Assert.Equal(FlashBundleWipeCommand.AmbiguousEntireStorageReason, refusal);
    }

    private static FlashBundleOptions Authorized() => new()
    {
        WipeConfirmation = new WipeConfirmation("WIPE")
    };

    private static WipeManifestEntry Ranged(long sectors = 16, string start = "32768", int partition = 0,
        long sectorSize = SectorSize, int? slot = null, string zeros = "Zeros4KB.bin",
        string label = "userdata-header") => new()
        {
            SectorSizeInBytes = sectorSize,
            NumPartitionSectors = sectors,
            PhysicalPartitionNumber = partition,
            StartSector = start,
            Label = label,
            ZeroFilename = zeros,
            Slot = slot
        };

    private static FirehoseStorageGeometry Geometry(int partition = 0)
        => new(partition, SectorSize, ReportedSectorCount);

    private static (bool Authorized, FlashBundleWipeCommand? Command, string? Refusal) Attempt(
        WipeManifestEntry entry, FirehoseStorageGeometry? geometry, FlashBundleOptions options)
    {
        var authorized = FlashBundleWipeCommand.TryPrepare(entry, options, geometry, out var command, out var refusal);
        return (authorized, command, refusal);
    }
}