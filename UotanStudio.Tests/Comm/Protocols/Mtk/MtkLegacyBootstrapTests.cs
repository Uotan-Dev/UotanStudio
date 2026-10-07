using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Firmware.VendorFirmware.MediaTek;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyBootstrapAsync_CompleteScript_UsesBigEndianAndRetainsSignature(bool nand64)
    {
        using var dir = new TempDirectory();
        var options = LegacyBootstrapOptions(dir) with { Legacy = LegacyProfile(nand64) };
        using var transport = new ScriptedCommTransport();
        EnqueueLegacyBootstrap(transport, nand64);
        transport.Enqueue([0x5A, 0x5A]);

        using var session = await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy, options,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
        Assert.True(session.IsConnected);
        Assert.NotNull(session.LegacyStorage);
        Assert.Null(session.XFlashStorage);
        Assert.Null(session.XmlStorage);
        Assert.Equal(8, session.LegacyStorage.Regions.Count);
        Assert.Equal(8192, session.LegacyStorage.Regions[7].CapacityBytes);
        Assert.Equal("000102030405060708090A0B0C0D0E0F", session.LegacyStorage.CidHex);
        Assert.Contains(transport.Writes, bytes => bytes.SequenceEqual(Convert.FromHexString("05FE0008007007FFFF010000000002010200")));
        Assert.Contains(transport.Writes, bytes => bytes.SequenceEqual(Convert.FromHexString("400000000000001000000004")));
        Assert.Contains(transport.Writes, bytes => bytes.SequenceEqual(new byte[] { 0xEE, 0xEE, 0xEE, 0xEE }));
        Assert.Equal(4, transport.Writes.Count(bytes => bytes.SequenceEqual(new byte[] { 0xFE })));
        Assert.Equal(2, transport.Writes.Count(bytes => bytes.SequenceEqual(new byte[] { 0xFF })));
        using var exclusive = File.Open(options.LoaderPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await session.ShutdownLegacyAsync(MtkLegacyShutdownMode.PowerOff, TestContext.Current.CancellationToken);
        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
    }

    [Theory]
    [InlineData("missing-profile")]
    [InlineData("wrong-protocol")]
    [InlineData("signature")]
    [InlineData("ufs")]
    [InlineData("response")]
    [InlineData("packet")]
    [InlineData("bmt")]
    [InlineData("nor-cfg")]
    [InlineData("iot")]
    public async Task LegacyBootstrapAsync_InvalidPolicy_RejectsBeforeDeviceIo(string failure)
    {
        using var dir = new TempDirectory();
        var options = LegacyBootstrapOptions(dir);
        var protocol = MtkDaProtocol.Legacy;
        switch (failure)
        {
            case "missing-profile": options = options with { Legacy = null }; break;
            case "wrong-protocol": protocol = MtkDaProtocol.XFlash; break;
            case "signature": options = options with { StageTwoContent = MtkDaRegionContent.WithoutSignatureSuffix }; break;
            case "ufs": options = options with { StorageKind = MtkXFlashStorageKind.Ufs }; break;
            case "response": options = options with { Legacy = LegacyProfile() with { StageOneResponseHex = "ZZZZZZ" } }; break;
            case "packet": options = options with { Legacy = LegacyProfile() with { StoragePacketLength = 511 } }; break;
            case "bmt": options = options with { Legacy = LegacyProfile() with { NandBmtFlag = 3 } }; break;
            case "nor-cfg": options = options with { Legacy = LegacyProfile() with { NorChipSelect = 0x0009 } }; break;
            case "iot":
                var bytes = BootstrapContainer();
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(110), 0x6261);
                dir.WriteBytes("loader.bin", bytes);
                break;
        }
        using var transport = new ScriptedCommTransport();

        await Assert.ThrowsAnyAsync<Exception>(() => MtkBootstrap.InitializeAsync(transport, protocol, options,
            TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("nand-count")]
    [InlineData("response")]
    [InlineData("runtime")]
    [InlineData("da2-header")]
    [InlineData("da2-packet")]
    [InlineData("da2-final")]
    [InlineData("geometry")]
    [InlineData("completion")]
    [InlineData("soc")]
    public async Task LegacyBootstrapAsync_RejectedStage_DisposesWithoutFallback(string failure)
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueLegacyBootstrap(transport, false, failure);

        await Assert.ThrowsAsync<MtkProtocolException>(() => MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy,
            LegacyBootstrapOptions(dir), TestContext.Current.CancellationToken));

        Assert.Equal(1, transport.DisposeCount);
        Assert.DoesNotContain(transport.Writes, bytes => Encoding.UTF8.GetString(bytes).Contains("<command>", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.Writes, bytes => bytes.SequenceEqual(new byte[] { 0xC0 }));
    }

    [Fact]
    public async Task LegacyBootstrapAsync_UnassertedDa1Response_StartsWithAnyReportedAckBytes()
    {
        using var dir = new TempDirectory();
        var options = LegacyBootstrapOptions(dir) with { Legacy = LegacyProfile() with { StageOneResponseHex = null } };
        using var transport = new ScriptedCommTransport();
        EnqueueLegacyBootstrap(transport, false, "response");
        transport.Enqueue([0x5A, 0x5A]);

        using var session = await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy, options,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
        var evidence = session.Evidence.Single(entry => entry.Phase == "legacy-da-profile");
        Assert.Contains("da1-response=CF0000", evidence.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("asserted", evidence.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyBootstrapAsync_AssertedDa1ResponseMismatch_ReportsObservedAndAssertedBytes()
    {
        using var dir = new TempDirectory();
        var options = LegacyBootstrapOptions(dir) with { Legacy = LegacyProfile() with { StageOneResponseHex = "C10000" } };
        using var transport = new ScriptedCommTransport();
        EnqueueLegacyBootstrap(transport, false, "response");

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => MtkBootstrap.InitializeAsync(transport,
            MtkDaProtocol.Legacy, options, TestContext.Current.CancellationToken));

        Assert.Contains("the agent sent CF0000 and the profile asserts C10000", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(20)]
    [InlineData(21)]
    public async Task LegacyBootstrapAsync_DramRequest_UsesOnlySelectedResourceAndConsumesStatus(int version)
    {
        using var dir = new TempDirectory();
        var bytes = Encoding.ASCII.GetBytes($"MTK_BLOADER_INFO_v{version:D2}parameters");
        using var source = new MemoryStream(bytes);
        var emi = MtkEmiReader.ReadBlock(source, 0, bytes.Length, MtkEmiPayloadLayout.BootloaderInfo,
            TestContext.Current.CancellationToken);
        var options = LegacyBootstrapOptions(dir) with { EmiParameters = emi };
        using var transport = new ScriptedCommTransport();
        EnqueueLegacyBootstrap(transport, false, emi: emi);

        using var session = await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy, options,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
        Assert.Equal((ushort)0x1234, session.LegacyDramReportedChecksum);
        var expected = bytes.ToArray();
        if (version is 12 or 13)
        {
            expected[0] = 0;
            expected[1] = 0;
            expected[2] = 1;
            expected[3] = 0;
        }
        Assert.Contains(transport.Writes, payload => payload.SequenceEqual(expected));
        using var unchanged = emi.OpenRead();
        var retained = new byte[emi.Length];
        await unchanged.ReadExactlyAsync(retained, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, retained);
    }

    [Fact]
    public async Task LegacyBootstrapAsync_MissingRequestedDram_StopsBeforeDa2()
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueLegacyBootstrap(transport, false, "missing-dram");

        await Assert.ThrowsAsync<InvalidOperationException>(() => MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy,
            LegacyBootstrapOptions(dir), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(transport.Writes, bytes => bytes.Length == 12 && bytes[0] == 0x40);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData(0x6592, 22)]
    [InlineData(0x6580, 42)]
    [InlineData(0x8163, 42)]
    [InlineData(0x8127, 46)]
    [InlineData(0x6583, 22)]
    [InlineData(0x6589, 22)]
    [InlineData(0x6582, 22)]
    [InlineData(0x0766, 18)]
    public void LegacyRuntime_ChipSpecificFields_UsesDocumentedBigEndianLayout(ushort hardware, int length)
    {
        var bytes = LegacyProfile().BuildRuntime(hardware, new(5, 0xFE));

        Assert.Equal(length, bytes.Length);
        Assert.Equal(5, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        Assert.Equal(hardware == 0x6583 ? 0 : 1, bytes[15]);
        if (hardware is 0x6589 or 0x6582)
        {
            Assert.Equal(new byte[] { 0, 0, 0, 1 }, bytes[18..]);
        }
        if (hardware is 0x6580 or 0x8163 or 0x8127)
        {
            Assert.Equal(Convert.FromHexString("0000000146460000000000000000000000000000FF000000"), bytes[^24..]);
        }
    }

    [Fact]
    public void LegacyRuntime_DefaultStartupProfile_TransmitsReferenceNorCfgTimingBmtAndChargeBytes()
    {
        var bytes = LegacyProfile().BuildRuntime(0x0766, new(5, 0xFE));

        Assert.Equal(Convert.FromHexString("05FE0008007007FFFF010000000002010200"), bytes);
        Assert.Equal(0x0008, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2)));
        Assert.Equal(0x01, bytes[9]);
        Assert.Equal(0x02, bytes[14]);
    }

    [Fact]
    public void LegacyRuntime_SelectedStartupParameters_LandOnTheirDocumentedWireBytes()
    {
        var options = LegacyProfile() with
        {
            NorChipSelect = 0x0000,
            NandChipSelect = 0x01,
            NandAccessTiming = 0x7007FF00,
            NandBmtFlag = 2,
            NandBmtPoolBytes = 0x1500000,
            ForceCharging = true
        };

        var bytes = options.BuildRuntime(0x6582, new(5, 0xFE));

        Assert.Equal(Convert.FromHexString("05FE0000017007FF0002015000000101020000000001"), bytes);
        Assert.Equal(0x02, bytes[9]);
        Assert.Equal(0x01500000u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(10)));
        Assert.Equal(0x01, bytes[14]);
        Assert.Equal(0x00, bytes[17]);
    }

    [Fact]
    public async Task LegacyBootstrapAsync_SelectedStartupParameters_TransportsExactRuntimePayloadAfterStageOneAck()
    {
        using var dir = new TempDirectory();
        var options = LegacyBootstrapOptions(dir) with
        {
            Legacy = LegacyProfile() with
            {
                NorChipSelect = 0x0000,
                NandChipSelect = 0x01,
                NandAccessTiming = 0x7007FF00,
                NandBmtFlag = 2,
                NandBmtPoolBytes = 0x1500000,
                ForceCharging = true
            }
        };
        using var transport = new ScriptedCommTransport();
        EnqueueLegacyBootstrap(transport, false);

        using var session = await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy, options,
            TestContext.Current.CancellationToken);

        Assert.Contains(transport.Writes, bytes => bytes.SequenceEqual(Convert.FromHexString("05FE0000017007FF00020150000001010200")));
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData(false, 0x02)]
    [InlineData(true, 0x01)]
    public void LegacyRuntime_ForceChargingSwitch_SelectsAutoOrOnChargeByte(bool forceCharging, byte expected)
    {
        var bytes = (LegacyProfile() with { ForceCharging = forceCharging })
            .BuildRuntime(0x0766, new(5, 0xFE));

        Assert.Equal(expected, bytes[14]);
    }

    [Theory]
    [InlineData((byte)0, 0u)]
    [InlineData((byte)1, 0u)]
    [InlineData((byte)0, 0x00A00000u)]
    [InlineData((byte)2, 0x01500000u)]
    public void LegacyRuntime_ReferenceFlagAndPoolCombinations_TransmitBothBytesWithoutCoupling(byte flag, uint poolBytes)
    {
        var bytes = (LegacyProfile() with { NandBmtFlag = flag, NandBmtPoolBytes = poolBytes })
            .BuildRuntime(0x0766, new(5, 0xFE));

        Assert.Equal(flag, bytes[9]);
        Assert.Equal(poolBytes, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(10)));
    }

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x0009)]
    [InlineData(ushort.MaxValue)]
    public void LegacyRuntime_UndocumentedNorCfgWord_ThrowsWithTheOffendingParameter(ushort value)
    {
        var options = LegacyProfile() with { NorChipSelect = value };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Equal(nameof(MtkLegacyOptions.NorChipSelect), error.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.BuildRuntime(0x0766, new(5, 0xFE)));
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)255)]
    public void LegacyRuntime_UndocumentedBmtFlag_ThrowsWithTheOffendingParameter(byte flag)
    {
        var options = LegacyProfile() with { NandBmtFlag = flag };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Equal(nameof(MtkLegacyOptions.NandBmtFlag), error.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.BuildRuntime(0x0766, new(5, 0xFE)));
    }

    private static MtkLegacyOptions LegacyProfile(bool nand64 = false) => new()
    {
        StageOneResponseHex = "C10000",
        NandSize64Bit = nand64,
        HostOs = MtkXmlHostOs.Linux,
        AgentPacketLength = 4,
        StoragePacketLength = 512
    };

    private static MtkBootstrapOptions LegacyBootstrapOptions(TempDirectory dir) => BootstrapOptions(dir) with
    {
        StageTwoContent = MtkDaRegionContent.Complete,
        Legacy = LegacyProfile()
    };

    private static void EnqueueLegacyBootstrap(ScriptedCommTransport transport, bool nand64, string? failure = null,
        MtkEmiParameters? emi = null, ushort hardwareCode = 0x0766, byte[]? norInventory = null,
        byte[]? emmcInventory = null, byte[]? nandInventory = null, byte[]? nandDetails = null,
        byte[]? nandIds = null, byte[]? completionResponse = null)
    {
        transport.Enqueue(
        [
            0x5F, 0xF5, 0xAF, 0xFA, 0xFD, (byte)(hardwareCode >> 8), (byte)hardwareCode, 0, 0,
            0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0, 0xD8, 0, 0, 0, 0x17, 0, 0
        ]);
        transport.Enqueue([0, 0, 0, 0]);
        transport.Enqueue([0xFE, 0x05]);
        transport.Enqueue(Convert.FromHexString("D7002000000000000800000002000008000000D5002000000000"));
        transport.Enqueue([failure == "sync" ? (byte)0xCF : (byte)0xC0]);
        transport.Enqueue(Convert.FromHexString("00000BC4"));
        transport.Enqueue(failure == "nand-count" ? [1, 1] : [0, 1]);
        transport.Enqueue([0xAA, 0xBB]);
        transport.Enqueue(new byte[20]);
        transport.Enqueue(failure == "response" ? [0xCF, 0, 0] : [0xC1, 0, 0]);
        var needsDram = emi is not null || failure == "missing-dram";
        transport.Enqueue(needsDram ? Convert.FromHexString("00000BC3") : failure == "runtime" ? [0, 0, 1, 0] : new byte[4]);
        if (needsDram)
        {
            transport.Enqueue(new byte[20]);
            transport.Enqueue(Convert.FromHexString("00000BC40000"));
            if (emi is not null)
            {
                transport.Enqueue([0x5A]);
                if (emi.Version is 10 or 11)
                {
                    transport.Enqueue(new byte[16]);
                }
                var requested = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(requested, (uint)emi.Length);
                transport.Enqueue(requested);
                transport.Enqueue(Convert.FromHexString("12340000000002000000000080000000"));
                if (emi.Version == 13)
                {
                    transport.Enqueue(new byte[20]);
                }
            }
        }
        else if (hardwareCode == 0x6592)
        {
            transport.Enqueue(new byte[20]);
        }
        transport.Enqueue([failure == "da2-header" ? (byte)0xA5 : (byte)0x5A]);
        for (var index = 0; index < 4; index++)
        {
            transport.Enqueue([failure == "da2-packet" && index == 1 ? (byte)0xA5 : (byte)0x5A]);
        }
        transport.Enqueue([failure == "da2-final" ? (byte)0xA5 : (byte)0x5A]);
        transport.Enqueue(norInventory ?? new byte[28]);
        var nand = nandInventory ?? new byte[nand64 ? 17 : 13];
        if (nandInventory is null)
        {
            nand[^1] = 1;
        }
        transport.Enqueue(nand);
        transport.Enqueue(nandIds ?? [0x11, 0x22]);
        transport.Enqueue(nandDetails ?? new byte[9]);
        var geometry = emmcInventory ?? LegacyEmmcGeometry();
        if (failure == "geometry")
        {
            geometry[67] = 1;
        }
        transport.Enqueue(geometry);
        transport.Enqueue(new byte[28]);
        transport.Enqueue(new byte[38]);
        if (hardwareCode is 0x8127 or 0x8163)
        {
            transport.Enqueue(new byte[4]);
        }
        var completion = new byte[10];
        completion[0] = 0x5A;
        completion[4] = failure == "completion" ? (byte)1 : (byte)0;
        completion[9] = failure == "soc" ? (byte)0xCF : (byte)0xC1;
        transport.Enqueue(completionResponse ?? completion);
    }

    private static byte[] LegacyEmmcGeometry()
    {
        var bytes = new byte[92];
        for (var index = 0; index < 8; index++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(4 + index * 8), (ulong)(index + 1) * 1024);
        }
        for (var index = 0; index < 16; index++)
        {
            bytes[68 + index] = (byte)index;
        }
        return bytes;
    }

    private static async Task<MtkSession> CreateLegacyStorageSessionAsync(ScriptedCommTransport transport)
    {
        using var dir = new TempDirectory();
        EnqueueLegacyBootstrap(transport, false);
        return await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy, LegacyBootstrapOptions(dir),
            TestContext.Current.CancellationToken);
    }
}