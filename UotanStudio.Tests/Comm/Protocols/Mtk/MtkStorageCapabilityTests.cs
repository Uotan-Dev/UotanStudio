using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Storage;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Fact]
    public void FromXFlash_EmmcReport_MapsRpmbUserAndUnnamedRegions()
    {
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            CapabilityEmmcGeometry(4096, 1024), PacketLengths());

        var snapshot = MtkStorageCapabilitySnapshot.FromXFlash(storage, capturedAtUtc: CapabilityCapture);

        Assert.Equal(MtkStorageDiscoverySource.XFlash, snapshot.Source);
        Assert.Equal(MtkXFlashStorageKind.Emmc, snapshot.Kind);
        Assert.Equal(512, snapshot.BlockSizeBytes);
        Assert.Equal(8, snapshot.Regions.Count);
        Assert.Null(snapshot.StorageGeneration);
        Assert.Equal(1u, snapshot.ReportedDeviceType);
        Assert.Equal(4096, snapshot.WritePacketBytes);
        Assert.Equal(8192, snapshot.ReadPacketBytes);
        var rpmb = snapshot.FindRegion(MtkStorageRegionKind.Rpmb);
        Assert.NotNull(rpmb);
        Assert.Equal(3u, rpmb.PhysicalIdentifier);
        Assert.Equal("EMMC-RPMB", rpmb.CanonicalName);
        Assert.True(rpmb.AuthenticatedOnly);
        Assert.False(rpmb.IsOrdinaryWritable);
        Assert.Equal(FeatureSupport.Unsupported, rpmb.WriteSupport);
        Assert.Multiple(
            () => Assert.Equal(MtkStorageRpmbAccess.Reported, snapshot.RpmbAccess),
            () => Assert.Equal(4096, snapshot.FindRegion(MtkStorageRegionKind.EmmcUser)?.CapacityBytes),
            () => Assert.True(snapshot.FindRegion(MtkStorageRegionKind.EmmcUser)?.IsOrdinaryWritable),
            () => Assert.Equal(MtkStorageRegionKind.Unknown,
                snapshot.FindRegion("XFLASH-EMMC-PARTITION-1")?.Kind),
            () => Assert.Equal(7, snapshot.OrdinaryRegions.Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.FindRegion(MtkStorageRegionKind.Unknown));
    }

    [Fact]
    public void FromXFlash_UfsReport_KeepsLogicalUnitsAndReportsNoRpmb()
    {
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Ufs,
            CapabilityUfsGeometry(1024, 2048, 3072), PacketLengths());

        var snapshot = MtkStorageCapabilitySnapshot.FromXFlash(storage);

        Assert.Equal(MtkXFlashStorageKind.Ufs, snapshot.Kind);
        Assert.Equal(MtkStorageRpmbAccess.None, snapshot.RpmbAccess);
        Assert.Null(snapshot.FindRegion(MtkStorageRegionKind.Rpmb));
        Assert.All(snapshot.Regions, region =>
        {
            Assert.Equal(MtkStorageRegionKind.UfsLogicalUnit, region.Kind);
            Assert.StartsWith("XFLASH-UFS-PARTITION-", region.CanonicalName, StringComparison.Ordinal);
        });
        Assert.True(snapshot.FindRegion("XFLASH-UFS-PARTITION-2")?.IsOrdinaryWritable);
    }

    [Fact]
    public void FromXFlash_ZeroCapacityRegion_IsReportedButNotWritable()
    {
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            CapabilityEmmcGeometry(4096, 1024), PacketLengths());

        var snapshot = MtkStorageCapabilitySnapshot.FromXFlash(storage);

        var empty = snapshot.FindRegion("XFLASH-EMMC-PARTITION-4");
        Assert.NotNull(empty);
        Assert.Equal(0, empty.CapacityBytes);
        Assert.Equal(FeatureSupport.Unsupported, empty.WriteSupport);
        Assert.False(empty.IsOrdinaryWritable);
    }

    [Fact]
    public void FromXFlash_NandGeometry_PreservesPageSpareEccAndBadBlockSemantics()
    {
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Nand,
            CapabilityNandGeometry(2048, 131072, 64, 262144, 131072, 1), CapabilityNandPackets());

        var snapshot = MtkStorageCapabilitySnapshot.FromXFlash(storage);

        Assert.Equal(MtkXFlashStorageKind.Nand, snapshot.Kind);
        Assert.Null(snapshot.Nor);
        Assert.NotNull(snapshot.Nand);
        Assert.Equal(2048, snapshot.Nand.PageSizeBytes);
        Assert.Equal(131072, snapshot.Nand.EraseBlockSizeBytes);
        Assert.Equal(64, snapshot.Nand.SpareSizeBytes);
        Assert.Equal(262144, snapshot.Nand.TotalCapacityBytes);
        Assert.Equal(131072, snapshot.Nand.AvailableCapacityBytes);
        Assert.Equal(FeatureSupport.Supported, snapshot.Nand.BadBlockTableSupport);
        Assert.Equal(FeatureSupport.Supported, snapshot.Nand.SpareAccess);
        Assert.Equal(FeatureSupport.Supported, snapshot.Nand.EccHandling);
        var main = snapshot.FindRegion(MtkStorageRegionKind.NandMainArea);
        Assert.NotNull(main);
        Assert.Equal(131072, main.CapacityBytes);
        Assert.Equal(2048, main.AlignmentBytes);
        Assert.Throws<NotSupportedException>(() => storage.ValidateRange(8, 0, 2048));
    }

    [Fact]
    public void FromXFlash_NandGeometry_ZeroSpareAndAvailable_FailClosed()
    {
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Nand,
            CapabilityNandGeometry(2048, 131072, 0, 262144, 0, 0), CapabilityNandPackets());

        var snapshot = MtkStorageCapabilitySnapshot.FromXFlash(storage);

        Assert.Equal(FeatureSupport.Unsupported, snapshot.Nand?.SpareAccess);
        Assert.Equal(FeatureSupport.Unsupported, snapshot.Nand?.BadBlockTableSupport);
        Assert.Equal(0, snapshot.FindRegion(MtkStorageRegionKind.NandMainArea)?.CapacityBytes);
        Assert.False(snapshot.FindRegion(MtkStorageRegionKind.NandMainArea)?.IsOrdinaryWritable);
    }

    [Fact]
    public void FromXFlash_NorReport_StatesWriteGranularityAndUnknownEraseSector()
    {
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Nor,
            CapabilityNorGeometry(4096, 1048576), CapabilityPackets(4096, 8192));

        var snapshot = MtkStorageCapabilitySnapshot.FromXFlash(storage);

        Assert.NotNull(snapshot.Nor);
        Assert.Equal(1048576, snapshot.Nor.CapacityBytes);
        Assert.Equal(4096, snapshot.Nor.WriteGranularityBytes);
        Assert.Null(snapshot.Nor.EraseSectorSizeBytes);
        Assert.Equal(FeatureSupport.Unknown, snapshot.Nor.WriteProtection);
        Assert.Null(snapshot.Nor.FlashDeviceId);
        Assert.Null(snapshot.Nand);
        var whole = snapshot.FindRegion(MtkStorageRegionKind.NorWholeDevice);
        Assert.NotNull(whole);
        Assert.Equal(4096, whole.AlignmentBytes);
        Assert.True(whole.IsOrdinaryWritable);
        storage.ValidateRange(8, 4096, 4096);
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ValidateRange(8, 512, 4096));
    }

    [Fact]
    public void FromXml_EmmcDocument_MapsBootUserGeneralPurposeAndRpmbRoles()
    {
        var storage = MtkXmlStorageInfo.Parse(CapabilityXmlEmmc(0x1000));

        var snapshot = MtkStorageCapabilitySnapshot.FromXml(storage);

        Assert.Equal(MtkStorageDiscoverySource.Xml, snapshot.Source);
        Assert.Equal(MtkXFlashStorageKind.Emmc, snapshot.Kind);
        Assert.Equal("0011AABB", snapshot.CidHex);
        Assert.Equal(MtkStorageRegionKind.EmmcBoot1, snapshot.FindRegion("EMMC-BOOT1")?.Kind);
        Assert.Equal(MtkStorageRegionKind.EmmcBoot2, snapshot.FindRegion("EMMC-BOOT2")?.Kind);
        Assert.Equal(MtkStorageRegionKind.EmmcUser, snapshot.FindRegion("EMMC-USER")?.Kind);
        Assert.Equal(0x1000, snapshot.FindRegion("EMMC-USER")?.CapacityBytes);
        Assert.Equal(MtkStorageRegionKind.EmmcGeneralPurpose, snapshot.FindRegion("EMMC-GP1")?.Kind);
        var rpmb = snapshot.FindRegion("EMMC-RPMB");
        Assert.NotNull(rpmb);
        Assert.Equal(MtkStorageRegionKind.Rpmb, rpmb.Kind);
        Assert.True(rpmb.AuthenticatedOnly);
        Assert.False(rpmb.IsOrdinaryWritable);
        Assert.Equal(MtkStorageRpmbAccess.Reported, snapshot.RpmbAccess);
        Assert.Equal(7, snapshot.OrdinaryRegions.Count);
        Assert.NotEqual(MtkStorageRegionKind.EmmcUser, snapshot.FindRegion("EMMC-BOOT1")?.Kind);
    }

    [Fact]
    public void FromXml_UfsDocument_ReservesLastLogicalUnitAndReportsRpmb()
    {
        var storage = MtkXmlStorageInfo.Parse(CapabilityXmlUfs());

        var snapshot = MtkStorageCapabilitySnapshot.FromXml(storage);

        Assert.Equal(MtkXFlashStorageKind.Ufs, snapshot.Kind);
        Assert.Equal(MtkStorageRegionKind.UfsLogicalUnit, snapshot.FindRegion("UFS-LUA0")?.Kind);
        var protectedLu = snapshot.FindRegion("UFS-LUA3");
        Assert.NotNull(protectedLu);
        Assert.Equal(MtkStorageRegionKind.Rpmb, protectedLu.Kind);
        Assert.True(protectedLu.AuthenticatedOnly);
        Assert.False(protectedLu.IsOrdinaryWritable);
        Assert.Equal(MtkStorageRpmbAccess.Reported, snapshot.RpmbAccess);
        Assert.Throws<NotSupportedException>(() => storage.ValidateRange("UFS-LUA3", 0, 512));
    }

    [Fact]
    public void FromLegacy_EmmcInventory_MapsUserRpmbAndUnnamedRegions()
    {
        var storage = MtkLegacyStorageInfo.Parse(CapabilityLegacyEmmc(4096, 1024));

        var snapshot = MtkStorageCapabilitySnapshot.FromLegacy(storage);

        Assert.Equal(MtkStorageDiscoverySource.Legacy, snapshot.Source);
        Assert.Equal(MtkXFlashStorageKind.Emmc, snapshot.Kind);
        Assert.Equal(512, snapshot.BlockSizeBytes);
        Assert.Equal(4096, snapshot.FindRegion(MtkStorageRegionKind.EmmcUser)?.CapacityBytes);
        var rpmb = snapshot.FindRegion(MtkStorageRegionKind.Rpmb);
        Assert.NotNull(rpmb);
        Assert.True(rpmb.AuthenticatedOnly);
        Assert.False(rpmb.IsOrdinaryWritable);
        Assert.Equal(MtkStorageRegionKind.Unknown, snapshot.FindRegion("LEGACY-EMMC-PARTITION-1")?.Kind);
        Assert.Equal(32, snapshot.CidHex?.Length);
        Assert.Equal(16, snapshot.FirmwareRevisionHex?.Length);
        Assert.Equal(MtkStorageRpmbAccess.Reported, snapshot.RpmbAccess);
        Assert.Throws<NotSupportedException>(() => storage.ValidateRange(3, 0, 512));
    }

    [Fact]
    public void FromLegacy_NorInventory_ReportsGranularityFlashIdAndUnknownEraseSector()
    {
        var inventory = new byte[28];
        BinaryPrimitives.WriteUInt16BigEndian(inventory.AsSpan(6), 0xEF17);
        BinaryPrimitives.WriteUInt32BigEndian(inventory.AsSpan(8), 16777216);

        var snapshot = MtkStorageCapabilitySnapshot.FromLegacy(MtkLegacyStorageInfo.ParseNor(inventory));

        Assert.Equal(MtkXFlashStorageKind.Nor, snapshot.Kind);
        Assert.Null(snapshot.BlockSizeBytes);
        Assert.NotNull(snapshot.Nor);
        Assert.Equal(16777216, snapshot.Nor.CapacityBytes);
        Assert.Equal(512, snapshot.Nor.WriteGranularityBytes);
        Assert.Null(snapshot.Nor.EraseSectorSizeBytes);
        Assert.Equal((ushort)0xEF17, snapshot.Nor.FlashDeviceId);
        Assert.Equal(MtkStorageRegionKind.NorWholeDevice, snapshot.FindRegion("NOR-WHOLE")?.Kind);
        Assert.Equal(MtkStorageRpmbAccess.None, snapshot.RpmbAccess);
    }

    [Fact]
    public void FromLegacy_NandInventory_LeavesAvailableCapacityUnknown()
    {
        var inventory = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(inventory.AsSpan(7), 131072);
        BinaryPrimitives.WriteUInt16BigEndian(inventory.AsSpan(11), 1);
        var details = new byte[9];
        BinaryPrimitives.WriteUInt16BigEndian(details, 2048);
        BinaryPrimitives.WriteUInt16BigEndian(details.AsSpan(4), 32);

        var snapshot = MtkStorageCapabilitySnapshot.FromLegacy(MtkLegacyStorageInfo.ParseNand(
            inventory, [0x11, 0x22], details, false));

        Assert.Equal(MtkXFlashStorageKind.Nand, snapshot.Kind);
        Assert.Null(snapshot.BlockSizeBytes);
        Assert.NotNull(snapshot.Nand);
        Assert.Equal(2048, snapshot.Nand.PageSizeBytes);
        Assert.Equal(65536, snapshot.Nand.EraseBlockSizeBytes);
        Assert.Equal(0, snapshot.Nand.SpareSizeBytes);
        Assert.Equal(131072, snapshot.Nand.TotalCapacityBytes);
        Assert.Null(snapshot.Nand.AvailableCapacityBytes);
        Assert.Equal(FeatureSupport.Unsupported, snapshot.Nand.SpareAccess);
        Assert.Equal(FeatureSupport.Supported, snapshot.Nand.EccHandling);
        Assert.Equal(131072, snapshot.FindRegion(MtkStorageRegionKind.NandMainArea)?.CapacityBytes);
    }

    [Fact]
    public void RegionCapability_Defaults_FailClosedWithoutReportedPermissions()
    {
        var region = new MtkStorageRegionCapability
        {
            Kind = MtkStorageRegionKind.Unknown,
            CanonicalName = "UNREPORTED"
        };

        Assert.Null(region.CapacityBytes);
        Assert.Equal(FeatureSupport.Unknown, region.ReadSupport);
        Assert.Equal(FeatureSupport.Unknown, region.WriteSupport);
        Assert.Equal(FeatureSupport.Unknown, region.EraseSupport);
        Assert.False(region.AuthenticatedOnly);
        Assert.False(region.IsOrdinaryWritable);
    }

    [Theory]
    [InlineData("duplicate-name")]
    [InlineData("authenticated-flag")]
    [InlineData("rpmb-posture")]
    [InlineData("negative-capacity")]
    [InlineData("family-companion")]
    [InlineData("duplicate-identifier")]
    [InlineData("duplicate-identity")]
    public void Validate_ContradictoryReports_Throw(string contradiction)
    {
        var region = new MtkStorageRegionCapability
        {
            Kind = MtkStorageRegionKind.EmmcUser,
            CanonicalName = "EMMC-USER",
            CapacityBytes = 1024,
            WriteSupport = FeatureSupport.Supported
        };
        var rpmb = new MtkStorageRegionCapability
        {
            Kind = MtkStorageRegionKind.Rpmb,
            CanonicalName = "EMMC-RPMB",
            CapacityBytes = 512,
            AuthenticatedOnly = true
        };
        var snapshot = contradiction switch
        {
            "duplicate-name" => Manual(MtkXFlashStorageKind.Emmc, [region, region with { PhysicalIdentifier = null }]),
            "authenticated-flag" => Manual(MtkXFlashStorageKind.Emmc, [region with { AuthenticatedOnly = true }]),
            "rpmb-posture" => Manual(MtkXFlashStorageKind.Emmc, [rpmb], MtkStorageRpmbAccess.None),
            "negative-capacity" => Manual(MtkXFlashStorageKind.Emmc, [region with { CapacityBytes = -1 }]),
            "family-companion" => Manual(MtkXFlashStorageKind.Emmc, [region]) with
            {
                Nor = new MtkNorCapability { CapacityBytes = 1, WriteGranularityBytes = 1 }
            },
            "duplicate-identifier" => Manual(MtkXFlashStorageKind.Emmc, [region with { PhysicalIdentifier = 9 },
                region with { CanonicalName = "OTHER", PhysicalIdentifier = 9 }]),
            _ => Manual(MtkXFlashStorageKind.Emmc, [region with { CanonicalName = "A",
                Kind = MtkStorageRegionKind.EmmcBoot1, PhysicalIdentifier = null }, region with { CanonicalName = "B",
                Kind = MtkStorageRegionKind.EmmcBoot1, PhysicalIdentifier = null }])
        };

        Assert.Throws<InvalidOperationException>(snapshot.Validate);
    }

    [Fact]
    public void GeometryEquals_IgnoresGenerationAndCaptureInstant()
    {
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            CapabilityEmmcGeometry(4096, 1024), PacketLengths());

        var first = MtkStorageCapabilitySnapshot.FromXFlash(storage, 1, capturedAtUtc: CapabilityCapture);
        var second = MtkStorageCapabilitySnapshot.FromXFlash(storage, 2, capturedAtUtc: CapabilityCapture.AddMinutes(1));

        Assert.True(first.GeometryEquals(second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GeometryEquals_DetectsChangedUserCapacity()
    {
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            CapabilityEmmcGeometry(4096, 1024), PacketLengths());
        var changed = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            CapabilityEmmcGeometry(4608, 1024), PacketLengths());

        Assert.False(MtkStorageCapabilitySnapshot.FromXFlash(storage).GeometryEquals(
            MtkStorageCapabilitySnapshot.FromXFlash(changed)));
    }

    [Fact]
    public void ThreePaths_EmmcReports_ConvergeOnSnapshotTypeAndKeyFacts()
    {
        var xflash = MtkStorageCapabilitySnapshot.FromXFlash(MtkXFlashStorageInfo.Parse(
            MtkXFlashStorageKind.Emmc, CapabilityEmmcGeometry(4096, 1024), PacketLengths()));
        var xml = MtkStorageCapabilitySnapshot.FromXml(MtkXmlStorageInfo.Parse(CapabilityXmlEmmc(0x1000)));
        var legacy = MtkStorageCapabilitySnapshot.FromLegacy(
            MtkLegacyStorageInfo.Parse(CapabilityLegacyEmmc(4096, 1024)));

        Assert.IsType<MtkStorageCapabilitySnapshot>(xflash);
        Assert.IsType<MtkStorageCapabilitySnapshot>(xml);
        Assert.IsType<MtkStorageCapabilitySnapshot>(legacy);
        foreach (var snapshot in new[] { xflash, xml, legacy })
        {
            Assert.Equal(MtkXFlashStorageKind.Emmc, snapshot.Kind);
            Assert.Equal(512, snapshot.BlockSizeBytes);
            Assert.Equal(MtkStorageRpmbAccess.Reported, snapshot.RpmbAccess);
            Assert.Equal(4096, snapshot.FindRegion(MtkStorageRegionKind.EmmcUser)?.CapacityBytes);
            Assert.True(snapshot.FindRegion(MtkStorageRegionKind.EmmcUser)?.IsOrdinaryWritable);
            Assert.False(snapshot.FindRegion(MtkStorageRegionKind.Rpmb)?.IsOrdinaryWritable);
            Assert.True(snapshot.FindRegion(MtkStorageRegionKind.Rpmb)?.AuthenticatedOnly);
        }
        Assert.NotEqual(xflash.Source, xml.Source);
        Assert.NotEqual(xml.Source, legacy.Source);
    }

    [Fact]
    public void Capability_Int64MaxUserCapacity_RangeChecksStayOverflowSafe()
    {
        var capacity = long.MaxValue & ~511L;
        var storage = MtkXFlashStorageInfo.Parse(MtkXFlashStorageKind.Emmc,
            CapabilityEmmcGeometry(capacity, 1024), PacketLengths());

        var snapshot = MtkStorageCapabilitySnapshot.FromXFlash(storage);
        var user = snapshot.FindRegion(MtkStorageRegionKind.EmmcUser);

        Assert.NotNull(user);
        Assert.Equal(capacity, user.CapacityBytes);
        Assert.True(user.IsOrdinaryWritable);
        storage.ValidateRange(8, capacity - 512, 512);
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ValidateRange(8, capacity, 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.ValidateRange(8, 0, long.MaxValue));
    }

    [Fact]
    public async Task DiscoverXFlashStorageAsync_ValidGeometry_PublishesGenerationAndEvidence()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, CapabilityEmmcGeometry(4096, 1024));
        EnqueueControlReply(transport, PacketLengths());

        await session.DiscoverXFlashStorageAsync(MtkXFlashStorageKind.Emmc, TestContext.Current.CancellationToken);

        var capability = session.StorageCapability;
        Assert.NotNull(capability);
        Assert.Equal(MtkStorageDiscoverySource.XFlash, capability.Source);
        Assert.Equal(1ul, session.StorageGeneration);
        Assert.Equal(1ul, capability.StorageGeneration);
        Assert.Equal(4096, capability.FindRegion(MtkStorageRegionKind.EmmcUser)?.CapacityBytes);
        var evidence = session.Evidence[^1];
        Assert.Equal("storage-snapshot", evidence.Phase);
        Assert.Equal(CommandOutcomeKind.Completed, evidence.Outcome);
        Assert.Contains("generation=1", evidence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverXFlashStorageAsync_InvalidReply_LeavesCapabilityUnpublished()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, CapabilityEmmcGeometry(4096, 1024));
        EnqueueControlReply(transport, new byte[8]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.DiscoverXFlashStorageAsync(MtkXFlashStorageKind.Emmc, TestContext.Current.CancellationToken));

        Assert.Null(session.StorageCapability);
        Assert.Equal(0ul, session.StorageGeneration);
    }

    [Fact]
    public async Task ReadXFlashStorageAsync_InvalidDataFrameAfterPublish_ClearsCapability()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        Assert.NotNull(session.StorageCapability);
        EnqueueTransferStart(transport, read: true);
        EnqueueXFlashFrame(transport, new byte[] { 1, 0, 0, 0xC0 });
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashStorageAsync(8, 0, 512, destination,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(session.XFlashStorage);
        Assert.Null(session.StorageCapability);
        Assert.Equal(1ul, session.StorageGeneration);
    }

    [Fact]
    public async Task ExecuteXFlashPlanAsync_StorageGeometryChanged_OldPlanRejectedBeforeTraffic()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("raw.img", new byte[512]);
        using var staleTransport = new ScriptedCommTransport();
        using var staleSession = await CreateStageTwoSessionAsync(staleTransport);
        EnqueueControlReply(staleTransport, CapabilityEmmcGeometry(4096, 1024));
        EnqueueControlReply(staleTransport, PacketLengths());
        var staleStorage = (await staleSession.DiscoverXFlashStorageAsync(MtkXFlashStorageKind.Emmc,
            TestContext.Current.CancellationToken))!;
        using var plan = MtkFlashPlan.Prepare(staleStorage,
            [new(MtkFlashStepKind.Raw, 8, 0, 512, path)], TestContext.Current.CancellationToken);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, CapabilityEmmcGeometry(4608, 1024));
        EnqueueControlReply(transport, PacketLengths());
        await session.DiscoverXFlashStorageAsync(MtkXFlashStorageKind.Emmc, TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteXFlashPlanAsync(plan,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.False(plan.Capability.GeometryEquals(session.StorageCapability));
    }

    [Fact]
    public async Task ExecuteXFlashPlanAsync_BoundPlan_CapabilityMatchesPublishedSnapshot()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("raw.img", new byte[512]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueControlReply(transport, CapabilityEmmcGeometry(4096, 1024));
        EnqueueControlReply(transport, PacketLengths());
        var storage = (await session.DiscoverXFlashStorageAsync(MtkXFlashStorageKind.Emmc,
            TestContext.Current.CancellationToken))!;

        using var plan = MtkFlashPlan.Prepare(storage,
            [new(MtkFlashStepKind.Raw, 8, 0, 512, path)], TestContext.Current.CancellationToken);

        Assert.Equal(MtkStorageDiscoverySource.XFlash, plan.Capability.Source);
        Assert.Null(plan.Capability.StorageGeneration);
        Assert.True(plan.Capability.GeometryEquals(session.StorageCapability));
    }

    [Fact]
    public async Task DiscoverXmlStorageAsync_ValidDocument_PublishesXmlCapability()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        EnqueueXmlStorage(transport, XmlHardwareDocument(false));

        await session.DiscoverXmlStorageAsync(cancellationToken: TestContext.Current.CancellationToken);

        var capability = session.StorageCapability;
        Assert.NotNull(capability);
        Assert.Equal(MtkStorageDiscoverySource.Xml, capability.Source);
        Assert.Equal(1ul, capability.StorageGeneration);
        Assert.Equal(0x10000, capability.FindRegion(MtkStorageRegionKind.EmmcUser)?.CapacityBytes);
        Assert.False(capability.FindRegion(MtkStorageRegionKind.Rpmb)?.IsOrdinaryWritable);
        Assert.Equal(MtkStorageRegionKind.EmmcBoot1, capability.FindRegion("EMMC-BOOT1")?.Kind);
    }

    [Fact]
    public async Task DiscoverXmlStorageAsync_FaultAfterPublish_ClearsCapability()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        Assert.NotNull(session.StorageCapability);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlReadRequest(512).ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
        EnqueueXmlText(transport, "OK@0x0");
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlStorageAsync("EMMC-USER", 0, 512,
            destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(session.XmlStorage);
        Assert.Null(session.StorageCapability);
    }

    [Fact]
    public async Task InitializeLegacyAgentAsync_PublishesLegacyCapability_FaultClearsIt()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);

        var capability = session.StorageCapability;
        Assert.NotNull(capability);
        Assert.Equal(MtkStorageDiscoverySource.Legacy, capability.Source);
        Assert.Equal(1ul, capability.StorageGeneration);
        Assert.Equal(8192, capability.FindRegion(MtkStorageRegionKind.EmmcUser)?.CapacityBytes);
        Assert.False(capability.FindRegion(MtkStorageRegionKind.Rpmb)?.IsOrdinaryWritable);
        transport.Enqueue(Convert.FromHexString("5A015A5A5A"));
        transport.Enqueue(Enumerable.Repeat((byte)1, 512).ToArray());
        transport.Enqueue([0, 0]);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadLegacyStorageAsync(8, 0, 512, destination,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(session.LegacyStorage);
        Assert.Null(session.StorageCapability);
        Assert.Equal(1ul, session.StorageGeneration);
    }

    private static MtkStorageCapabilitySnapshot Manual(MtkXFlashStorageKind kind,
        IReadOnlyList<MtkStorageRegionCapability> regions,
        MtkStorageRpmbAccess rpmb = MtkStorageRpmbAccess.None) => new()
        {
            Kind = kind,
            Source = MtkStorageDiscoverySource.Xml,
            Regions = regions,
            RpmbAccess = rpmb,
            CapturedAtUtc = CapabilityCapture,
            Evidence = []
        };

    private static byte[] CapabilityEmmcGeometry(long userCapacity, long rpmbCapacity)
    {
        var geometry = new byte[96];
        BinaryPrimitives.WriteUInt32LittleEndian(geometry, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), 512);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8 + 2 * 8), (ulong)rpmbCapacity);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8 + 7 * 8), (ulong)userCapacity);
        return geometry;
    }

    private static byte[] CapabilityUfsGeometry(long first, long second, long third)
    {
        var geometry = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(geometry, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), 512);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8), (ulong)second);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(16), (ulong)first);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(24), (ulong)third);
        return geometry;
    }

    private static byte[] CapabilityNorGeometry(int blockSize, long capacity)
    {
        var geometry = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(geometry, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), (uint)blockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(8), (ulong)capacity);
        return geometry;
    }

    private static byte[] CapabilityNandGeometry(int page, int block, int spare, long total, long available,
        byte badBlockTable)
    {
        var geometry = new byte[45];
        BinaryPrimitives.WriteUInt32LittleEndian(geometry, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(4), (uint)page);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(8), (uint)block);
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(12), (uint)spare);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(16), (ulong)total);
        BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(24), (ulong)available);
        geometry[32] = badBlockTable;
        return geometry;
    }

    private static byte[] CapabilityPackets(int writeLength, int readLength)
    {
        var packets = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(packets, (uint)writeLength);
        BinaryPrimitives.WriteUInt32LittleEndian(packets.AsSpan(4), (uint)readLength);
        return packets;
    }

    private static byte[] CapabilityNandPackets() => CapabilityPackets(4096, 8192);

    private static byte[] CapabilityLegacyEmmc(long userCapacity, long rpmbCapacity)
    {
        var bytes = new byte[92];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(4 + 2 * 8), (ulong)rpmbCapacity);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(4 + 7 * 8), (ulong)userCapacity);
        for (var index = 0; index < 16; index++)
        {
            bytes[68 + index] = (byte)index;
        }
        return bytes;
    }

    private static string CapabilityXmlEmmc(long userSize) =>
        "<da_hw_info><version>1.2</version><storage>EMMC</storage><emmc><block_size>0x200</block_size>" +
        "<boot1_size>0x400</boot1_size><boot2_size>0x800</boot2_size><rpmb_size>0x400</rpmb_size>" +
        $"<user_size>0x{userSize:x}</user_size><gp1_size>0</gp1_size><gp2_size>0</gp2_size>" +
        "<gp3_size>0</gp3_size><gp4_size>0</gp4_size><id>0011aabb</id></emmc></da_hw_info>";

    private static string CapabilityXmlUfs() =>
        "<da_hw_info><version>1.2</version><storage>UFS</storage><ufs><block_size>0x200</block_size>" +
        "<lua0_size>0x10000</lua0_size><lua1_size>0x8000</lua1_size><lua2_size>0x4000</lua2_size>" +
        "<lua3_size>0x800</lua3_size></ufs></da_hw_info>";

    private static readonly DateTimeOffset CapabilityCapture = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
}