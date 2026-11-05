using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Sunxi;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class SunxiFlashTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "uotan-sunxi-flash-" + Guid.NewGuid().ToString("N"));

    public SunxiFlashTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Plan_Load_BuildsTablePartitionAndBootVariantSteps()
    {
        using var plan = SunxiFlashPlan.Load(StandardPackagePath(), TestContext.Current.CancellationToken);

        Assert.Equal(7, plan.Steps.Count);
        Assert.Equal(SunxiFlashStepKind.Mbr, plan.Steps[0].Kind);
        Assert.Equal(16384, plan.Steps[0].ByteCount);
        Assert.Equal(SunxiFesDataTag.Mbr, plan.Steps[0].Tag);
        Assert.Equal(SunxiFlashStepKind.Partition, plan.Steps[1].Kind);
        Assert.Equal("bootloader", plan.Steps[1].Name);
        Assert.Equal(0x1000u, plan.Steps[1].FirstSector);
        Assert.Equal(32768, plan.Steps[1].ByteCount);
        Assert.Equal(SunxiFesDataTag.Flash, plan.Steps[1].Tag);
        Assert.Equal("rootfs", plan.Steps[2].Name);
        Assert.Equal(0x2000u, plan.Steps[2].FirstSector);
        Assert.Equal(100 * 1024, plan.Steps[2].ByteCount);
        Assert.Equal(SunxiFlashStepKind.Boot1, plan.Steps[3].Kind);
        Assert.Equal(SunxiBootImageRole.Boot1Normal, plan.Steps[3].BootRole);
        Assert.Equal(new SunxiBootImageRole?[] { SunxiBootImageRole.Boot0NormalNand,
            SunxiBootImageRole.Boot0NormalNor },
            plan.Steps.Where(step => step.Kind == SunxiFlashStepKind.Boot0).Select(step => step.BootRole).ToArray());
        Assert.Equal(["private"], plan.MissingPartitions);
    }

    [Fact]
    public void Plan_Load_ResolvesTheDownloadFileFromTheVendorPartitionList()
    {
        var path = WriteImage(
            ("sunxi_mbr.fex", "12345678", "1234567890___MBR", BuildMbr(("system", 0x1000, 64))),
            ("sys_partition.fex", "COMMON", "SYS_CONFIG000000",
                Encoding.ASCII.GetBytes("[partition_start]\n[partition]\nname=system\ndownloadfile=system.img\n")),
            ("system.img", "RFSFAT16", "SYSTEM_IMG000000", new byte[4096]));

        using var plan = SunxiFlashPlan.Load(path, TestContext.Current.CancellationToken);

        var partition = Assert.Single(plan.Steps, step => step.Kind == SunxiFlashStepKind.Partition);
        Assert.Equal("system", partition.Name);
        Assert.Equal(4096, partition.ByteCount);
        Assert.Empty(plan.MissingPartitions);
    }

    [Fact]
    public async Task Plan_Load_ExpandsASparsePartitionToItsLogicalLength()
    {
        var raw = new byte[8192];
        raw.AsSpan().Fill(0x5A);
        var path = WriteImage(
            ("sunxi_mbr.fex", "12345678", "1234567890___MBR", BuildMbr(("system", 0x1000, 32))),
            ("system.fex", "RFSFAT16", "SYSTEM_IMG000000", BuildSparse(raw)));

        using var plan = SunxiFlashPlan.Load(path, TestContext.Current.CancellationToken);

        var partition = Assert.Single(plan.Steps, step => step.Kind == SunxiFlashStepKind.Partition);
        Assert.Equal(raw.Length, partition.ByteCount);
        var buffer = new byte[raw.Length];
        var written = await plan.ExtractSegmentAsync(partition, 0, buffer, TestContext.Current.CancellationToken);
        Assert.Equal(raw.Length, written);
        Assert.Equal(raw, buffer);
    }

    [Fact]
    public void Plan_Load_RejectsPackagesWithoutAnMbr()
    {
        var path = WriteImage(("bootloader.fex", "DATAFILE", "xxxxxxxxxxxxxxxx", new byte[512]));

        Assert.Throws<InvalidDataException>(() => SunxiFlashPlan.Load(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Plan_Load_RejectsDuplicateBootRoles()
    {
        var path = WriteImage(
            ("sunxi_mbr.fex", "12345678", "1234567890___MBR", BuildMbr()),
            ("boot0_spinor.fex", "12345678", "1234567890BNOR_0", new byte[512]),
            ("boot0_spinor2.fex", "12345678", "1234567890BNOR_0", new byte[512]),
            ("u-boot-efex.fex", "12345678", "UBOOT_0000000000", BuildUboot()));

        Assert.Throws<InvalidDataException>(() => SunxiFlashPlan.Load(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Plan_Load_RejectsPartitionPayloadAboveItsCapacity()
    {
        var path = WriteImage(
            ("sunxi_mbr.fex", "12345678", "1234567890___MBR", BuildMbr(("bootloader", 0x1000, 64))),
            ("bootloader.fex", "DATAFILE", "xxxxxxxxxxxxxxxx", new byte[65536]));

        Assert.Throws<InvalidDataException>(() => SunxiFlashPlan.Load(path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(SunxiFlashMode.FullErase, 0x12u)]
    [InlineData(SunxiFlashMode.PartitionErase, 0x1u)]
    [InlineData(SunxiFlashMode.KeepData, 0x0u)]
    [InlineData(SunxiFlashMode.Partition, 0x0u)]
    public void Options_EraseFlag_MapsTheSelectedMode(SunxiFlashMode mode, uint expected)
    {
        Assert.Equal(expected, (uint)new SunxiFlashOptions { Mode = mode }.EraseFlag);
    }

    [Fact]
    public async Task Flasher_Execute_RunsErasePartitionsBootAndToolMode()
    {
        var transport = new FakeTransport();
        QueueFullRun(transport);
        using var plan = SunxiFlashPlan.Load(StandardPackagePath(), TestContext.Current.CancellationToken);
        var progress = new ProgressLog();
        var flasher = new SunxiFlasher(plan, new SunxiFlashOptions { FlashConfirmation = "FLASH" });

        await flasher.ExecuteAsync(new SunxiFesSession(transport),
            progress, TestContext.Current.CancellationToken);

        var eraseFrame = transport.Writes.First(frame => frame.Length == 20 && Read16(frame) == 0x0206);
        Assert.Equal((uint)SunxiFesDataTag.Erase | SunxiEfexConstants.TransferFinishTag, Read32(eraseFrame, 12));
        Assert.Contains(transport.Writes, frame => frame.Length == 16 && Read32(frame, 0) == 0x12u);
        Assert.Contains(transport.Writes, frame => frame.Length == 20 && Read16(frame) == 0x020A);
        Assert.Contains(transport.Writes, frame => frame.Length == 20 && Read16(frame) == 0x020B);
        Assert.Contains(transport.Writes, frame => frame.Length == 20 && Read16(frame) == 0x0206
            && Read32(frame, 12) == ((uint)SunxiFesDataTag.Flash | SunxiEfexConstants.TransferFinishTag));
        Assert.Contains(transport.Writes, frame => frame.Length == 20 && Read16(frame) == 0x0206
            && Read32(frame, 12) == ((uint)SunxiFesDataTag.Boot1 | SunxiEfexConstants.TransferFinishTag));
        Assert.Contains(transport.Writes, frame => frame.Length == 20 && Read16(frame) == 0x0206
            && Read32(frame, 12) == ((uint)SunxiFesDataTag.Boot0 | SunxiEfexConstants.TransferFinishTag));
        var toolFrame = transport.Writes[^1];
        Assert.Equal(0x020F, Read16(toolFrame));
        Assert.Equal((uint)SunxiToolMode.Normal, Read32(toolFrame, 4));
        Assert.Equal((uint)SunxiToolMode.Reboot, Read32(toolFrame, 8));
        Assert.Equal(4, transport.Writes.Count(frame => frame.Length == 20 && Read16(frame) == 0x020D));
        Assert.Equal(2, transport.Writes.Count(frame => frame.Length == 20 && Read16(frame) == 0x020C));
        Assert.Contains(progress.Reports, report => report.Phase == "skip" && report.Target == "private");
        Assert.Contains(progress.Reports, report => report.Phase == "erase");
        Assert.Contains(progress.Reports, report => report.Phase == "done");
    }

    [Fact]
    public async Task Flasher_Execute_PartitionModeSkipsEraseAndFiltersPartitions()
    {
        var transport = new FakeTransport();
        QueueQueries(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueDownload(transport, 2);
        QueueVerifyValue(transport, 1, 1);
        QueueDownload(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        using var plan = SunxiFlashPlan.Load(StandardPackagePath(), TestContext.Current.CancellationToken);
        var progress = new ProgressLog();
        var options = new SunxiFlashOptions
        {
            FlashConfirmation = "FLASH",
            Mode = SunxiFlashMode.Partition,
            Partitions = ["rootfs"]
        };

        await new SunxiFlasher(plan, options).ExecuteAsync(new SunxiFesSession(transport),
            progress, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(transport.Writes, frame => frame.Length == 16 && Read32(frame, 0) == 0x12u);
        Assert.Contains(progress.Reports, report => report.Phase == "skip" && report.Target == "bootloader");
        Assert.Contains(progress.Reports, report => report.Phase == "download" && report.Target == "rootfs");
        Assert.Equal(1, transport.Writes.Count(frame => frame.Length == 20 && Read16(frame) == 0x020C));
    }

    [Fact]
    public async Task Flasher_Execute_KeepDataSkipsUserDataPartitions()
    {
        var path = WriteImage(
            ("sunxi_mbr.fex", "12345678", "1234567890___MBR",
                BuildMbr(("bootloader", 0x1000, 64), ("private", 0x2000, 64))),
            ("bootloader.fex", "DATAFILE", "xxxxxxxxxxxxxxxx", new byte[1024]),
            ("private.fex", "DATAFILE", "xxxxxxxxxxxxxxxx", new byte[1024]),
            ("u-boot-efex.fex", "12345678", "UBOOT_0000000000", BuildUboot()),
            ("boot0_spinor.fex", "12345678", "1234567890BNOR_0", new byte[512]));
        var transport = new FakeTransport();
        QueueQueries(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueDownload(transport);
        QueueVerifyValue(transport, 1, 1);
        QueueDownload(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        using var plan = SunxiFlashPlan.Load(path, TestContext.Current.CancellationToken);
        var progress = new ProgressLog();
        var options = new SunxiFlashOptions { FlashConfirmation = "FLASH", Mode = SunxiFlashMode.KeepData };

        await new SunxiFlasher(plan, options).ExecuteAsync(new SunxiFesSession(transport),
            progress, TestContext.Current.CancellationToken);

        Assert.Contains(transport.Writes, frame => frame.Length == 16 && Read32(frame, 0) == 0u);
        Assert.Contains(progress.Reports, report => report.Phase == "skip" && report.Target == "private");
        Assert.DoesNotContain(progress.Reports, report => report.Phase == "download" && report.Target == "private");
    }

    [Fact]
    public async Task Flasher_Execute_SelectsTheBootVariantForTheStorageAndBootMode()
    {
        var transport = new FakeTransport();
        QueueFullRun(transport, secure: 0, storage: 0);
        using var plan = SunxiFlashPlan.Load(StandardPackagePath(), TestContext.Current.CancellationToken);
        var progress = new ProgressLog();
        var flasher = new SunxiFlasher(plan, new SunxiFlashOptions { FlashConfirmation = "FLASH" });

        await flasher.ExecuteAsync(new SunxiFesSession(transport),
            progress, TestContext.Current.CancellationToken);

        Assert.Contains(progress.Reports, report => report.Phase == "download" && report.Target == "boot0_nand.fex");
        Assert.DoesNotContain(progress.Reports, report => report.Phase == "download" && report.Target == "boot0_spinor.fex");
        Assert.Contains(progress.Reports, report => report.Phase == "download" && report.Target == "u-boot-efex.fex");
    }

    [Fact]
    public async Task Flasher_Execute_FailsOnAnAuthoritativeCrcMismatch()
    {
        var transport = new FakeTransport();
        QueueQueries(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueDownload(transport);
        QueueVerifyValue(transport, 1, 2);
        using var plan = SunxiFlashPlan.Load(StandardPackagePath(), TestContext.Current.CancellationToken);
        var flasher = new SunxiFlasher(plan, new SunxiFlashOptions { FlashConfirmation = "FLASH" });

        await Assert.ThrowsAsync<SunxiEfexException>(() => flasher.ExecuteAsync(new SunxiFesSession(transport),
            null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Flasher_Execute_ReportsInconclusivePartitionVerificationAndContinues()
    {
        var transport = new FakeTransport();
        QueueFullRun(transport, partitionVerifyFlag: 0);
        using var plan = SunxiFlashPlan.Load(StandardPackagePath(), TestContext.Current.CancellationToken);
        var progress = new ProgressLog();
        var flasher = new SunxiFlasher(plan, new SunxiFlashOptions { FlashConfirmation = "FLASH" });

        await flasher.ExecuteAsync(new SunxiFesSession(transport),
            progress, TestContext.Current.CancellationToken);

        Assert.Contains(progress.Reports, report => report.Phase == "verify" && report.Target == "bootloader"
            && report.Message.Contains("did not return a valid CRC", StringComparison.Ordinal));
        Assert.Equal(0x020F, Read16(transport.Writes[^1]));
    }

    [Fact]
    public async Task Flasher_Execute_SkipsPartitionVerificationWhenDisabled()
    {
        var transport = new FakeTransport();
        QueueQueries(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueDownload(transport);
        QueueDownload(transport, 2);
        QueueDownload(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        using var plan = SunxiFlashPlan.Load(StandardPackagePath(), TestContext.Current.CancellationToken);
        var options = new SunxiFlashOptions { FlashConfirmation = "FLASH", VerifyAfter = false };

        await new SunxiFlasher(plan, options).ExecuteAsync(new SunxiFesSession(transport), null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, transport.Writes.Count(frame => frame.Length == 20 && Read16(frame) == 0x020C));
        Assert.Equal(0x020F, Read16(transport.Writes[^1]));
    }

    [Fact]
    public async Task Flasher_Execute_RejectsUnconfirmedOptionsBeforeAnyWireByte()
    {
        var transport = new FakeTransport();
        using var plan = SunxiFlashPlan.Load(StandardPackagePath(), TestContext.Current.CancellationToken);
        var flasher = new SunxiFlasher(plan, new SunxiFlashOptions { FlashConfirmation = "erase" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => flasher.ExecuteAsync(new SunxiFesSession(
            transport), null, TestContext.Current.CancellationToken));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task Flasher_Execute_RejectsAPackageWithoutTheMatchingBootImage()
    {
        var path = WriteImage(
            ("sunxi_mbr.fex", "12345678", "1234567890___MBR", BuildMbr()),
            ("boot0_spinor.fex", "12345678", "1234567890BNOR_0", new byte[512]));
        var transport = new FakeTransport();
        QueueQueries(transport);
        using var plan = SunxiFlashPlan.Load(path, TestContext.Current.CancellationToken);
        var flasher = new SunxiFlasher(plan, new SunxiFlashOptions { FlashConfirmation = "FLASH" });

        await Assert.ThrowsAsync<InvalidDataException>(() => flasher.ExecuteAsync(new SunxiFesSession(transport),
            null, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(SunxiBootFileMode.Normal, SunxiStorageKind.Spinor, SunxiBootImageRole.Boot0NormalNor)]
    [InlineData(SunxiBootFileMode.Normal, SunxiStorageKind.Nand, SunxiBootImageRole.Boot0NormalNand)]
    [InlineData(SunxiBootFileMode.Normal, SunxiStorageKind.Emmc, SunxiBootImageRole.Boot0NormalCard)]
    [InlineData(SunxiBootFileMode.Normal, SunxiStorageKind.Ufs, SunxiBootImageRole.Boot0NormalUfs)]
    [InlineData(SunxiBootFileMode.Toc, SunxiStorageKind.Spinor, SunxiBootImageRole.Boot0TocNor)]
    [InlineData(SunxiBootFileMode.Package, SunxiStorageKind.Emmc, SunxiBootImageRole.Boot0NormalCard)]
    public void BootSelector_MatchesTheBoot0Variant(SunxiBootFileMode mode, SunxiStorageKind storage,
        SunxiBootImageRole expected)
    {
        Assert.True(SunxiBootImageSelector.Matches(expected, mode, storage));
    }

    [Fact]
    public void BootSelector_TreatsAnUnreportedModeAsTocForBoot0()
    {
        Assert.True(SunxiBootImageSelector.Matches(SunxiBootImageRole.Boot0TocNor, null,
            SunxiStorageKind.Spinor));
        Assert.False(SunxiBootImageSelector.Matches(SunxiBootImageRole.Boot1Toc, null,
            SunxiStorageKind.Spinor));
    }

    [Theory]
    [InlineData(SunxiBootFileMode.Package, SunxiStorageKind.Emmc, SunxiBootImageRole.Boot1Package)]
    [InlineData(SunxiBootFileMode.Package, SunxiStorageKind.Spinor, SunxiBootImageRole.Boot1PackageNor)]
    [InlineData(SunxiBootFileMode.Toc, SunxiStorageKind.Nand, SunxiBootImageRole.Boot1Toc)]
    public void BootSelector_MatchesTheBoot1Variant(SunxiBootFileMode mode, SunxiStorageKind storage,
        SunxiBootImageRole expected)
    {
        Assert.True(SunxiBootImageSelector.Matches(expected, mode, storage));
    }

    private static void QueueFullRun(FakeTransport transport, uint secure = 0, uint storage = 3,
        uint partitionVerifyFlag = SunxiEfexConstants.CrcValidFlag)
    {
        QueueQueries(transport, secure, storage);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueDownload(transport);
        QueueVerifyValue(transport, 0x1111, 0x1111, partitionVerifyFlag);
        QueueDownload(transport, 2);
        QueueVerifyValue(transport, 0x2222, 0x2222, partitionVerifyFlag);
        QueueDownload(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
        QueueVerifyStatus(transport);
        QueueDownload(transport);
    }

    private static void QueueQueries(FakeTransport transport, uint secure = 0, uint storage = 3)
    {
        transport.QueueValue(secure);
        transport.QueueResponse();
        transport.QueueValue(storage);
        transport.QueueResponse();
    }

    private static void QueueDownload(FakeTransport transport, int frames = 1)
    {
        for (var index = 0; index < frames; index++)
        {
            transport.QueueResponse();
        }
    }

    private static void QueueVerifyStatus(FakeTransport transport) =>
        QueueVerifyValue(transport, 0, 0);

    private static void QueueVerifyValue(FakeTransport transport, int fesCrc, int mediaCrc,
        uint flag = SunxiEfexConstants.CrcValidFlag)
    {
        transport.QueueVerify(flag, fesCrc, mediaCrc);
        transport.QueueResponse();
    }

    private string StandardPackagePath() => WriteImage(
        ("sunxi_mbr.fex", "12345678", "1234567890___MBR", BuildMbr(
            ("bootloader", 0x1000, 64), ("rootfs", 0x2000, 4096), ("private", 0x2200, 128))),
        ("bootloader.fex", "DATAFILE", "xxxxxxxxxxxxxxxx", new byte[32768]),
        ("rootfs.fex", "DATAFILE", "xxxxxxxxxxxxxxxx", new byte[100 * 1024]),
        ("u-boot-efex.fex", "12345678", "UBOOT_0000000000", BuildUboot()),
        ("boot_package.fex", "12345678", "BOOTPKG-00000000", new byte[2048]),
        ("boot0_nand.fex", "BOOT", "BOOT0_0000000000", new byte[1536]),
        ("boot0_spinor.fex", "12345678", "1234567890BNOR_0", new byte[1024]));

    private static byte[] BuildUboot()
    {
        var image = new byte[4096];
        "u-boot\0\0"u8.CopyTo(image.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), (uint)image.Length);
        Encoding.ASCII.GetBytes("sun50iw9").CopyTo(image.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44), 0x4A000000);
        return image;
    }

    private static byte[] BuildSparse(byte[] raw)
    {
        var image = new byte[28 + 12 + raw.Length];
        Write32(image, 0, 0xED26FF3A);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(8), 28);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(10), 12);
        Write32(image, 12, 4096);
        Write32(image, 16, (uint)(raw.Length / 4096));
        Write32(image, 20, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(28), 0xCAC1);
        Write32(image, 32, (uint)(raw.Length / 4096));
        Write32(image, 36, (uint)(12 + raw.Length));
        raw.CopyTo(image, 40);
        return image;
    }

    private static byte[] BuildMbr(params (string Name, ulong FirstSector, ulong SectorCount)[] partitions)
    {
        var table = new byte[16384];
        "softw411"u8.CopyTo(table.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(24), (uint)partitions.Length);
        for (var index = 0; index < partitions.Length; index++)
        {
            var entry = 32 + (index * 128);
            var (name, firstSector, sectorCount) = partitions[index];
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entry), (uint)(firstSector >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entry + 4), (uint)firstSector);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entry + 8), (uint)(sectorCount >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entry + 12), (uint)sectorCount);
            Encoding.ASCII.GetBytes(name).CopyTo(table, entry + 32);
        }

        return table;
    }

    private string WriteImage(params (string Name, string MainType, string SubType, byte[] Payload)[] files)
    {
        const int tableOffset = 0x400;
        const int fileHeaderSize = 1024;
        var tableEnd = tableOffset + (files.Length * fileHeaderSize);
        var total = tableEnd + files.Sum(file => (long)file.Payload.Length);
        var image = new byte[total];
        "IMAGEWTY"u8.CopyTo(image);
        Write32(image, 8, 0x100);
        Write32(image, 12, 1024);
        Write32(image, 16, 0x2);
        Write32(image, 20, 0x100234);
        Write32(image, 24, (uint)total);
        Write32(image, 28, 0);
        Write32(image, 56, (uint)files.Length);
        Write32(image, 60, tableOffset);
        var payloadOffset = (long)tableEnd;
        for (var index = 0; index < files.Length; index++)
        {
            var record = tableOffset + (index * fileHeaderSize);
            var file = files[index];
            var nameBytes = Encoding.ASCII.GetBytes(file.Name);
            Write32(image, record, (uint)nameBytes.Length);
            Write32(image, record + 4, fileHeaderSize);
            Encoding.ASCII.GetBytes(file.MainType.PadRight(8)).CopyTo(image, record + 8);
            Encoding.ASCII.GetBytes(file.SubType.PadRight(16)).CopyTo(image, record + 16);
            Write32(image, record + 32, 0x1);
            Write32(image, record + 36, (uint)file.Payload.Length);
            Write32(image, record + 40, (uint)file.Payload.Length);
            Write32(image, record + 44, (uint)payloadOffset);
            Write32(image, record + 48, 0xDEADBEEF);
            nameBytes.CopyTo(image, record + 52);
            file.Payload.CopyTo(image, payloadOffset);
            payloadOffset += file.Payload.Length;
        }

        var path = Path.Combine(_root, $"package-{Guid.NewGuid():N}.img");
        File.WriteAllBytes(path, image);
        return path;
    }

    private static void Write32(byte[] image, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), value);

    private static ushort Read16(byte[] frame) => BinaryPrimitives.ReadUInt16LittleEndian(frame);

    private static uint Read32(byte[] frame, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(offset));

    private sealed class ProgressLog : IProgress<SunxiFlashProgress>
    {
        private readonly System.Threading.Lock _gate = new();
        private readonly List<SunxiFlashProgress> _reports = [];

        public IReadOnlyList<SunxiFlashProgress> Reports
        {
            get
            {
                lock (_gate)
                {
                    return _reports.ToArray();
                }
            }
        }

        public void Report(SunxiFlashProgress value)
        {
            lock (_gate)
            {
                _reports.Add(value);
            }
        }
    }

    private sealed class FakeTransport : ISunxiEfexTransport
    {
        private readonly Queue<byte[]> _reads = new();

        public List<byte[]> Writes { get; } = [];

        public void QueueResponse(byte status = 0)
        {
            var response = new byte[13];
            "AWUS"u8.CopyTo(response);
            response[12] = status;
            _reads.Enqueue(response);
        }

        public void QueueValue(uint value)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
            _reads.Enqueue(payload);
        }

        public void QueueVerify(uint flag, int fesCrc, int mediaCrc)
        {
            var payload = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, flag);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), fesCrc);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), mediaCrc);
            _reads.Enqueue(payload);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken,
            int? timeoutMilliseconds = null)
        {
            Writes.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken,
            int? timeoutMilliseconds = null)
        {
            Assert.NotEmpty(_reads);
            var next = _reads.Dequeue();
            Assert.Equal(buffer.Length, next.Length);
            next.CopyTo(buffer);
            return Task.FromResult(next.Length);
        }
    }
}