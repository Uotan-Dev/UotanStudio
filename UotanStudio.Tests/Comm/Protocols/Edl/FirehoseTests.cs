using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

/// <summary>
/// Firehose XML command construction, response parsing, and protocol command flows
/// (configure negotiation, program/erase/patch/wipe, sector-size auto-detection, sparse).
/// <para>Firehose XML 命令构建、响应解析与协议命令流程（configure 协商、program/erase/patch/wipe、
/// 扇区大小自动检测、sparse）。</para>
/// </summary>
public class FirehoseTests
{
    private const string RawAckXml = "<data><response value=\"ACK\" rawmode=\"true\"/></data>";
    private const string AckXml = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <data>
        <response value="ACK" rawmode="false"/>
        </data>
        """;

    [Fact]
    public void BuildConfigure_EmitsAllNegotiatedAttributes()
    {
        var xml = FirehoseXmlBuilder.BuildConfigure(new FirehoseConfiguration
        {
            StorageType = FirehoseStorageType.Ufs,
            Verbose = 1,
            AlwaysValidate = 1,
            MaxDigestTableSizeInBytes = 16384,
            MaxPayloadSizeToTarget = 524288,
            MaxPayloadSizeToTargetSupported = 1048576,
            MaxPayloadSizeFromTarget = 8192,
            ZlpAwareHost = true,
            SkipStorageInit = 1,
            SkipWrite = 1,
            SkipSettingMinClocks = 1,
            SkipSettingMaxClocks = 1,
            TargetName = "a<b>c",
            Slot = 2,
            StorageSlot = 1
        });

        Assert.StartsWith("<?xml", xml);
        Assert.Contains("<configure MemoryName=\"ufs\"", xml, StringComparison.Ordinal);
        Assert.Contains("Verbose=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("AlwaysValidate=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("MaxDigestTableSizeInBytes=\"16384\"", xml, StringComparison.Ordinal);
        Assert.Contains("MaxPayloadSizeToTargetInBytes=\"524288\"", xml, StringComparison.Ordinal);
        Assert.Contains("MaxPayloadSizeToTargetInBytesSupported=\"1048576\"", xml, StringComparison.Ordinal);
        Assert.Contains("MaxPayloadSizeFromTargetInBytes=\"8192\"", xml, StringComparison.Ordinal);
        Assert.Contains("ZlpAwareHost=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("SkipStorageInit=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("SkipWrite=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("SkipSettingMinClocks=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("SkipSettingMaxClocks=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("TargetName=\"a&lt;b&gt;c\"", xml, StringComparison.Ordinal);
        Assert.Contains("slot=\"2\"", xml, StringComparison.Ordinal);
        Assert.Contains("StorageSlot=\"1\"", xml, StringComparison.Ordinal);
        Assert.EndsWith("</data>\n", xml);
    }

    [Fact]
    public void BuildConfigure_OmitsOptionalZeroAttributes()
    {
        var xml = FirehoseXmlBuilder.BuildConfigure(new FirehoseConfiguration
        {
            StorageType = FirehoseStorageType.Emmc,
            AutoDetectSectorSize = false,
            SectorSize = 4096
        });

        Assert.DoesNotContain("SkipStorageInit", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("slot=", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageSlot", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetName", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProgram_EmitsFullAttributeSet()
    {
        var xml = FirehoseXmlBuilder.BuildProgram(4096, 64, "boot.img", 0, 0x8000, "boot", 1);

        Assert.Contains("<program SECTOR_SIZE_IN_BYTES=\"4096\"", xml, StringComparison.Ordinal);
        Assert.Contains("num_partition_sectors=\"64\"", xml, StringComparison.Ordinal);
        Assert.Contains("filename=\"boot.img\"", xml, StringComparison.Ordinal);
        Assert.Contains("physical_partition_number=\"0\"", xml, StringComparison.Ordinal);
        Assert.Contains("start_sector=\"32768\"", xml, StringComparison.Ordinal);
        Assert.Contains("label=\"boot\"", xml, StringComparison.Ordinal);
        Assert.Contains("slot=\"1\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProgram_OmitsLabelAndSlotWhenNull()
    {
        var xml = FirehoseXmlBuilder.BuildProgram(512, 8, "x.img", 1, 0);

        Assert.DoesNotContain("label=", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("slot=", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildReadAndErase_EmitTheirElementNames()
    {
        var read = FirehoseXmlBuilder.BuildRead(512, 4, "boot.img", 0, 0x10, "boot");
        Assert.Contains("<read SECTOR_SIZE_IN_BYTES=\"512\"", read, StringComparison.Ordinal);
        Assert.Contains("filename=\"boot.img\"", read, StringComparison.Ordinal);

        var erase = FirehoseXmlBuilder.BuildErase(512, 4, 0, 0x10);
        Assert.Contains("<erase SECTOR_SIZE_IN_BYTES=\"512\"", erase, StringComparison.Ordinal);
        Assert.Contains("num_partition_sectors=\"4\"", erase, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPatch_DiskTarget_EmitsPhysicalStoragePatch()
    {
        Assert.Contains("filename=\"DISK\"", FirehoseXmlBuilder.BuildPatch(512, "DISK", 0, 0, 0, "00", 1), StringComparison.Ordinal);
        Assert.Contains("filename=\"disk\"", FirehoseXmlBuilder.BuildPatch(512, "disk", 0, 0, 0, "00", 1), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPatch_EmitsByteOffsetAndValue()
    {
        var xml = FirehoseXmlBuilder.BuildPatch(512, "boot.img", 0, 0x20, 4, "deadbeef", 4);

        Assert.Contains("<patch SECTOR_SIZE_IN_BYTES=\"512\"", xml, StringComparison.Ordinal);
        Assert.Contains("filename=\"boot.img\"", xml, StringComparison.Ordinal);
        Assert.Contains("start_sector=\"32\"", xml, StringComparison.Ordinal);
        Assert.Contains("byte_offset=\"4\"", xml, StringComparison.Ordinal);
        Assert.Contains("value=\"deadbeef\"", xml, StringComparison.Ordinal);
        Assert.Contains("size_in_bytes=\"4\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildNop_UsesLowercaseElement()
    {
        var xml = FirehoseXmlBuilder.BuildNop();
        Assert.Contains("<nop value=\"ping\" />", xml, StringComparison.Ordinal);

        var custom = FirehoseXmlBuilder.BuildNop("hello");
        Assert.Contains("<nop value=\"hello\" />", custom, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RebootOption.Reset, "reset")]
    [InlineData(RebootOption.PowerOff, "off")]
    [InlineData(RebootOption.ResetToEdl, "reset_to_edl")]
    public void BuildPower_MapsOptionsToValues(RebootOption option, string expected)
    {
        var xml = FirehoseXmlBuilder.BuildPower(option);
        Assert.Contains($"<power value=\"{expected}\" />", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("DelayInSeconds", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWipe_EmitsProgramWithZerosFilename()
    {
        var xml = FirehoseXmlBuilder.BuildWipe(4096, 16, 0, 0);
        Assert.Contains("<program SECTOR_SIZE_IN_BYTES=\"4096\"", xml, StringComparison.Ordinal);
        Assert.Contains("filename=\"Zeros16KB.bin\"", xml, StringComparison.Ordinal);
        Assert.Contains("num_partition_sectors=\"16\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSetActivePartition_ThrowsOnEmptySlot()
    {
        Assert.Throws<ArgumentException>(() => FirehoseXmlBuilder.BuildSetActivePartition(""));
        Assert.Throws<ArgumentException>(() => FirehoseXmlBuilder.BuildSetActivePartition(null!));
    }

    [Fact]
    public void BuildMemoryAndStorageCommands_EmitExpectedElements()
    {
        Assert.Contains("<setbootablestoragedrive value=\"1\" />", FirehoseXmlBuilder.BuildSetBootableStorageDrive(1), StringComparison.Ordinal);
        Assert.Contains("<getstorageinfo physical_partition_number=\"0\" />", FirehoseXmlBuilder.BuildGetStorageInfo(0), StringComparison.Ordinal);
        Assert.Contains("<setactivepartition slot=\"a\" />", FirehoseXmlBuilder.BuildSetActivePartition("a"), StringComparison.Ordinal);
        Assert.Contains("<peek size_in_bytes=\"4\" address=\"0x4000\" />", FirehoseXmlBuilder.BuildPeek(4, "0x4000"), StringComparison.Ordinal);
    }

    [Fact]
    public void FirehoseResponseParser_ParsesAck()
    {
        var response = FirehoseResponseParser.Parse("""
            <data>
            <response value="ACK" rawmode="false" MaxPayloadSizeToTargetInBytes="8388608"/>
            </data>
            """);

        Assert.True(response.IsAck);
        Assert.False(response.IsNak);
        Assert.Equal("ACK", response.RawValue);
        Assert.Equal("8388608", response["MaxPayloadSizeToTargetInBytes"]);
        Assert.Equal(8388608L, response.GetInt64("MaxPayloadSizeToTargetInBytes"));
        Assert.Equal("false", response["rawmode"]);
    }

    [Fact]
    public void FirehoseResponseParser_ParsesNak()
    {
        var response = FirehoseResponseParser.Parse("""
            <data>
            <response value="NAK" MaxPayloadSizeToTargetInBytes="4096"/>
            </data>
            """);

        Assert.True(response.IsNak);
        Assert.False(response.IsAck);
        Assert.Equal("NAK", response.RawValue);
        Assert.Equal(4096L, response.GetInt64("MaxPayloadSizeToTargetInBytes"));
    }

    [Fact]
    public void FirehoseResponseParser_ParsesRawMode()
    {
        var response = FirehoseResponseParser.Parse("""
            <data>
            <response value="ACK" rawmode="true"/>
            </data>
            """);

        Assert.True(response.IsRawMode);
        Assert.True(response.IsAck);
    }

    [Fact]
    public void FirehoseResponseParser_ParsesLogOnlyBlock()
    {
        var response = FirehoseResponseParser.Parse("""
            <data>
            <log value="Programmer started"/>
            </data>
            """);

        Assert.True(response.IsLog);
        Assert.Equal("Programmer started", response.LogValue);
        Assert.Equal("Programmer started", response.RawValue);
    }

    [Fact]
    public void FirehoseResponseParser_ParseDataBlock_MixedLogsAndResponse()
    {
        var block = FirehoseResponseParser.ParseDataBlock("""
            <data>
            <log value="hello"/>
            <log value="world"/>
            <response value="ACK"/>
            </data>
            """);

        Assert.Equal(["hello", "world"], block.Logs);
        Assert.NotNull(block.Response);
        Assert.True(block.Response.IsAck);
    }

    [Fact]
    public void FirehoseResponseParser_EmptyXml_Throws()
    {
        Assert.Throws<FirehoseException>(() => FirehoseResponseParser.Parse(""));
        Assert.Throws<FirehoseException>(() => FirehoseResponseParser.Parse("   "));
    }

    [Fact]
    public void FirehoseResponseParser_MalformedXml_Throws()
    {
        Assert.Throws<FirehoseException>(() => FirehoseResponseParser.Parse("<data><response value=\"ACK\""));
    }

    private static FirehoseProtocol CreateProtocol(ScriptedCommTransport transport, FirehoseConfiguration? config = null)
        => new(transport, config ?? new FirehoseConfiguration
        {
            SectorSize = 4096,
            AutoDetectSectorSize = false
        });

    [Fact]
    public async Task ConfigureAsync_WritesConfigureXmlAndNegotiatesPayloadSizes()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("""
            <?xml version="1.0" encoding="UTF-8" ?>
            <data>
            <response value="ACK" MaxPayloadSizeToTargetInBytes="8388608" MaxPayloadSizeFromTargetInBytes="16384"/>
            </data>
            """);

        var protocol = CreateProtocol(transport);
        var response = await protocol.ConfigureAsync(maxRetries: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(8388608L, protocol.Configuration.MaxPayloadSizeToTarget);
        Assert.Equal(16384L, protocol.Configuration.MaxPayloadSizeFromTarget);

        var configureXml = Encoding.UTF8.GetString(Assert.Single(transport.Writes));
        Assert.Contains("<configure", configureXml, StringComparison.Ordinal);
        Assert.Contains("ZlpAwareHost=\"1\"", configureXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureAsync_RenegotiatesOnNakWithSupportedPayloadSize()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("""
            <?xml version="1.0" encoding="UTF-8" ?>
            <data>
            <response value="NAK" MaxPayloadSizeToTargetInBytes="8192"/>
            </data>
            """);
        transport.Enqueue(AckXml);

        var protocol = CreateProtocol(transport);
        var response = await protocol.ConfigureAsync(maxRetries: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(8192L, protocol.Configuration.MaxPayloadSizeToTarget);
        Assert.Equal(2, transport.Writes.Count);
        var retried = Encoding.UTF8.GetString(transport.Writes[1]);
        Assert.Contains("MaxPayloadSizeToTargetInBytes=\"8192\"", retried, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureAsync_AckCarriesMemoryName_AdoptsReportedStorageType()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("""
            <?xml version="1.0" encoding="UTF-8" ?>
            <data>
            <response value="ACK" MemoryName="UFS"/>
            </data>
            """);

        var protocol = CreateProtocol(transport);

        await protocol.ConfigureAsync(maxRetries: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(FirehoseStorageType.Ufs, protocol.Configuration.StorageType);
    }

    [Fact]
    public async Task ConfigureAsync_NakNamesDifferentStorageType_RetriesWithMatchingMemoryName()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("""
            <?xml version="1.0" encoding="UTF-8" ?>
            <data>
            <response value="NAK" MemoryName="ufs"/>
            </data>
            """);
        transport.Enqueue(AckXml);
        var protocol = CreateProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            AutoDetectSectorSize = false,
            ConfigureRetryIntervalMs = 0
        });

        await protocol.ConfigureAsync(maxRetries: 3, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(FirehoseStorageType.Ufs, protocol.Configuration.StorageType);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Contains("<configure MemoryName=\"eMMC\"", Encoding.UTF8.GetString(transport.Writes[0]), StringComparison.Ordinal);
        Assert.Contains("<configure MemoryName=\"ufs\"", Encoding.UTF8.GetString(transport.Writes[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureAsync_NakLogTextNamesDifferentStorageType_RetriesWithMatchingMemoryName()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("""
            <?xml version="1.0" encoding="UTF-8" ?>
            <data>
            <log value="storage init failed: expected UFS"/>
            <response value="NAK"/>
            </data>
            """);
        transport.Enqueue(AckXml);
        var protocol = CreateProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            AutoDetectSectorSize = false,
            ConfigureRetryIntervalMs = 0
        });

        await protocol.ConfigureAsync(maxRetries: 3, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(FirehoseStorageType.Ufs, protocol.Configuration.StorageType);
        Assert.Contains("<configure MemoryName=\"ufs\"", Encoding.UTF8.GetString(transport.Writes[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureAsync_NakNamesCurrentStorageType_KeepsSendingSameMemoryName()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("""
            <?xml version="1.0" encoding="UTF-8" ?>
            <data>
            <response value="NAK" MemoryName="eMMC"/>
            </data>
            """);
        transport.Enqueue("""
            <?xml version="1.0" encoding="UTF-8" ?>
            <data>
            <response value="NAK" MemoryName="eMMC"/>
            </data>
            """);
        var protocol = CreateProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            AutoDetectSectorSize = false,
            ConfigureRetryIntervalMs = 0
        });

        var exception = await Assert.ThrowsAsync<EdlException>(
            () => protocol.ConfigureAsync(maxRetries: 2, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.ConfigureFailed, exception.ErrorCode);
        Assert.Equal(FirehoseStorageType.Emmc, protocol.Configuration.StorageType);
        Assert.Equal(2, transport.Writes.Count);
        Assert.All(transport.Writes, write =>
            Assert.Contains("<configure MemoryName=\"eMMC\"", Encoding.UTF8.GetString(write), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgramAsync_RawModeAcknowledged_WritesPayloadThenReadsCompletion()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(RawAckXml);
        transport.Enqueue(AckXml);

        var protocol = CreateProtocol(transport);
        var image = new byte[4096 * 4];
        Random.Shared.NextBytes(image);
        using var stream = new MemoryStream(image);

        var response = await protocol.ProgramAsync(stream, 0x100, 4, "boot.img", 0, TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(2, transport.Writes.Count);

        var programXml = Encoding.UTF8.GetString(transport.Writes[0]);
        Assert.Contains("<program", programXml, StringComparison.Ordinal);
        Assert.Contains("filename=\"boot.img\"", programXml, StringComparison.Ordinal);
        Assert.Contains("num_partition_sectors=\"4\"", programXml, StringComparison.Ordinal);

        Assert.Equal(image, transport.Writes[1]);
    }

    [Fact]
    public async Task ProgramAsync_NakThrowsProgramFailed()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("""
            <?xml version="1.0" encoding="UTF-8" ?>
            <data>
            <response value="NAK"/>
            </data>
            """);

        var protocol = CreateProtocol(transport);
        using var stream = new MemoryStream(new byte[4096]);

        var exception = await Assert.ThrowsAsync<EdlException>(
            () => protocol.ProgramAsync(stream, 0, 1, "boot.img", 0, TestContext.Current.CancellationToken));
        Assert.Equal(EdlErrorCode.ProgramFailed, exception.ErrorCode);
        Assert.Single(transport.Writes);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task EraseAsync_IssuesEraseXml()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(AckXml);

        var protocol = CreateProtocol(transport);
        var response = await protocol.EraseAsync(0x100, 8, 0, TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        var eraseXml = Encoding.UTF8.GetString(Assert.Single(transport.Writes));
        Assert.Contains("<erase", eraseXml, StringComparison.Ordinal);
        Assert.Contains("num_partition_sectors=\"8\"", eraseXml, StringComparison.Ordinal);
        Assert.Contains("start_sector=\"256\"", eraseXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WipeAsync_WritesZerosPayload()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(RawAckXml);
        transport.Enqueue(AckXml);

        var protocol = CreateProtocol(transport);
        var response = await protocol.WipeAsync(0, 8, 0, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(2, transport.Writes.Count);
        var wipeXml = Encoding.UTF8.GetString(transport.Writes[0]);
        Assert.Contains("filename=\"Zeros16KB.bin\"", wipeXml, StringComparison.Ordinal);
        Assert.Equal(32768, transport.Writes[1].Length);
        Assert.All(transport.Writes[1], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task AutoDetectSectorSize_UsesGetStorageInfo()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue("""
            <?xml version="1.0" encoding="UTF-8" ?>
            <data>
            <response value="ACK" sector_size="4096"/>
            </data>
            """);

        var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            StorageType = FirehoseStorageType.Emmc,
            AutoDetectSectorSize = false
        });
        await protocol.AutoDetectSectorSizeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4096L, protocol.Configuration.SectorSize);
        var xml = Encoding.UTF8.GetString(Assert.Single(transport.Writes));
        Assert.Contains("<getstorageinfo", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoDetectSectorSize_FallsBackToStorageTypeDefault()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(AckXml);

        var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            StorageType = FirehoseStorageType.Ufs,
            AutoDetectSectorSize = false
        });
        await protocol.AutoDetectSectorSizeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4096L, protocol.Configuration.SectorSize);
    }

    [Fact]
    public async Task AutoDetectSectorSize_SkipsForNand()
    {
        var transport = new ScriptedCommTransport();
        var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            StorageType = FirehoseStorageType.Nand,
            AutoDetectSectorSize = false
        });
        await protocol.AutoDetectSectorSizeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0L, protocol.Configuration.SectorSize);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ProgramSparseAsync_ExpandsRawChunkAndProgramsSectors()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(RawAckXml);
        transport.Enqueue(AckXml);

        var protocol = CreateProtocol(transport);
        const int blockSize = 4096;
        const int blockCount = 2;
        var chunkData = new byte[blockSize * blockCount];
        for (var i = 0; i < chunkData.Length; i++)
        {
            chunkData[i] = (byte)(i * 3);
        }

        var sparse = BuildSparseImage(blockSize, chunkData);
        using var stream = new MemoryStream(sparse);

        var response = await protocol.ProgramSparseAsync(stream, 0x100, "sparse.img", 0, TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(2, transport.Writes.Count);
        var programXml = Encoding.UTF8.GetString(transport.Writes[0]);
        Assert.Contains("filename=\"sparse.img\"", programXml, StringComparison.Ordinal);
        Assert.Contains("num_partition_sectors=\"2\"", programXml, StringComparison.Ordinal);
        Assert.Equal(chunkData, transport.Writes[1]);
    }

    [Fact]
    public async Task NopAsync_SendsPing()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(AckXml);

        var protocol = CreateProtocol(transport);
        var response = await protocol.NopAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        var xml = Encoding.UTF8.GetString(Assert.Single(transport.Writes));
        Assert.Contains("<nop value=\"ping\" />", xml, StringComparison.Ordinal);
    }

    /// <summary>Builds a minimal Android sparse image: header + one raw chunk.</summary>
    private static byte[] BuildSparseImage(int blockSize, byte[] chunkData)
    {
        const uint magic = 0xED26FF3A;
        const ushort chunkHeaderSize = 12;
        const ushort chunkTypeRaw = 0xCAC1;
        var outBlocks = (uint)(chunkData.Length / blockSize);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(magic);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)28);
        writer.Write(chunkHeaderSize);
        writer.Write((uint)blockSize);
        writer.Write(outBlocks);
        writer.Write(1u);
        writer.Write(0u);
        writer.Write(chunkTypeRaw);
        writer.Write((ushort)0);
        writer.Write(outBlocks);
        writer.Write((uint)(chunkHeaderSize + chunkData.Length));
        writer.Write(chunkData);
        writer.Flush();
        return ms.ToArray();
    }
}