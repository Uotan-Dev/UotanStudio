using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

/// <summary>
/// EDL flash-bundle tests: rawprogram/patch XML parsing tolerance and
/// <see cref="FlashBundleOrchestrator"/> orchestration (program/erase/patch order asserted
/// through the scripted transport write sequence; the device is treated as already running a
/// Firehose programmer, so no programmer image is uploaded).
/// <para>EDL 刷写包测试：rawprogram/patch XML 解析容错与 <see cref="FlashBundleOrchestrator"/>
/// 编排（通过脚本化传输写入序列断言 program/erase/patch 顺序；设备视为已在运行 Firehose
/// programmer，因此不上传 programmer 镜像）。</para>
/// </summary>
public class FlashBundleOrchestratorTests
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
        <response value="ACK" rawmode="false" MaxPayloadSizeToTargetInBytes="1048576"/>
        </data>
        """;

    private const string NopResponseXml = """
        <data><log value="booted"/></data><data><response value="ACK"/></data>
        """;

    // ============== RawProgramParser / PatchParser ==============

    [Fact]
    public void RawProgramParser_ParsesHexAndDecimalStartSectors()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("rawprogram0.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <data>
              <program SECTOR_SIZE_IN_BYTES="4096" file_sector_offset="1" filename="boot.img"
                       physical_partition_number="0" start_sector="0x8000" num_partition_sectors="4"
                       label="boot" sparse="true" UNSPARSE_FILE_SIZE="1048576" PAGES_PER_BLOCK="64"
                       last_sector="100" slot="2" />
              <program filename="system.img" start_sector="4096" />
            </data>
            """);

        var entries = RawProgramParser.Parse(path);

        Assert.Equal(2, entries.Count);
        var boot = entries[0];
        Assert.Equal("boot", boot.Label);
        Assert.Equal("boot.img", boot.Filename);
        Assert.Equal("0x8000", boot.StartSector);
        Assert.Equal(4, boot.NumPartitionSectors);
        Assert.Equal(4096, boot.SectorSizeInBytes);
        Assert.Equal(1, boot.FileSectorOffset);
        Assert.True(boot.IsSparse);
        Assert.Equal(1048576, boot.UnsparseFileSize);
        Assert.Equal(64, boot.PagesPerBlock);
        Assert.Equal(100, boot.LastSector);
        Assert.Equal(2, boot.Slot);

        // Missing optional attributes must default gracefully.
        // 缺失的可选属性必须优雅地使用默认值。
        var system = entries[1];
        Assert.Equal("system.img", system.Filename);
        Assert.Equal("4096", system.StartSector);
        Assert.Empty(system.Label);
        Assert.False(system.IsSparse);
        Assert.Null(system.Slot);
        Assert.Equal(0, system.NumPartitionSectors);
    }

    [Fact]
    public void RawProgramParser_MalformedFile_ThrowsEdlException()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("rawprogram0.xml", "<data><program");
        var exception = Assert.Throws<EdlException>(() => RawProgramParser.Parse(path));
        Assert.Equal(EdlErrorCode.XmlParseError, exception.ErrorCode);
    }

    [Fact]
    public void PatchParser_ParsesEntriesAndDecodesHexValues()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("patch0.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <data>
              <patch SECTOR_SIZE_IN_BYTES="512" filename="boot.img" physical_partition_number="0"
                     start_sector="0x20" byte_offset="4" value="0xdeadbeef" size_in_bytes="4" />
              <patch filename="system.img" start_sector="0x100" byte_offset="0" value="00ff" size_in_bytes="2" />
            </data>
            """);

        var entries = PatchParser.Parse(path);

        Assert.Equal(2, entries.Count);
        Assert.Equal("boot.img", entries[0].Filename);
        Assert.Equal(0x20, ParseStartSector(entries[0].StartSector));
        Assert.Equal(4, entries[0].ByteOffset);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, entries[0].GetDecodedValue());
        // Odd-length hex is left-padded with a zero nibble.
        // 奇数长度十六进制以 0 半字节左补齐。
        Assert.Equal(new byte[] { 0x00, 0xFF }, entries[1].GetDecodedValue());
    }

    [Fact]
    public void PatchEntry_InvalidHex_Throws()
    {
        Assert.Throws<EdlException>(() => new PatchEntry { Value = "zz" }.GetDecodedValue());
        Assert.Throws<EdlException>(() => new PatchEntry { Value = "" }.GetDecodedValue());
    }

    // ============== FlashBundleOrchestrator ==============

    private const string TwoPartitionRawProgram = """
        <?xml version="1.0" encoding="UTF-8"?>
        <data>
          <program SECTOR_SIZE_IN_BYTES="4096" file_sector_offset="0" filename="boot.img"
                   physical_partition_number="0" start_sector="0x8000" num_partition_sectors="4"
                   label="boot" sparse="false" />
          <program SECTOR_SIZE_IN_BYTES="4096" file_sector_offset="0" filename="system.img"
                   physical_partition_number="0" start_sector="0x9000" num_partition_sectors="8"
                   label="system" sparse="false" />
        </data>
        """;

    [Fact]
    public async Task FlashBundleAsync_SinglePartition_ProgramsInOrder()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", SinglePartitionRawProgram("boot.img", 4));
        var bootImage = new byte[4096 * 4];
        Random.Shared.NextBytes(bootImage);
        dir.WriteBytes("boot.img", bootImage);

        var transport = CreateBundleTransport(commandAckCount: 1);
        using var session = CreateSession(transport);
        var orchestrator = new FlashBundleOrchestrator(session);

        var result = await orchestrator.FlashBundleAsync(rawPath, patchPath: null, dir.Path, progress: null, CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(1, result.ProgramEntriesProcessed);

        // [0] nop probe, [1] configure, [2] program XML, [3] payload.
        Assert.Equal(5, transport.Writes.Count);
        var programXml = Encoding.UTF8.GetString(transport.Writes[3]);
        Assert.Contains("<program", programXml, StringComparison.Ordinal);
        Assert.Contains("filename=\"boot.img\"", programXml, StringComparison.Ordinal);
        Assert.Contains("start_sector=\"32768\"", programXml, StringComparison.Ordinal);
        Assert.Contains("num_partition_sectors=\"4\"", programXml, StringComparison.Ordinal);
        Assert.Equal(bootImage, transport.Writes[4]);

        // The configure handshake precedes the program command.
        // configure 握手先于 program 命令。
        Assert.Contains("<configure", Encoding.UTF8.GetString(transport.Writes[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlashBundleAsync_MultiPartition_ProgramsInRawprogramOrder()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", TwoPartitionRawProgram);
        var bootImage = new byte[4096 * 4];
        var systemImage = new byte[4096 * 8];
        Random.Shared.NextBytes(bootImage);
        Random.Shared.NextBytes(systemImage);
        dir.WriteBytes("boot.img", bootImage);
        dir.WriteBytes("system.img", systemImage);

        var transport = CreateBundleTransport(commandAckCount: 2);
        using var session = CreateSession(transport);
        var orchestrator = new FlashBundleOrchestrator(session);

        var result = await orchestrator.FlashBundleAsync(rawPath, patchPath: null, dir.Path, progress: null, CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(2, result.ProgramEntriesProcessed);
        Assert.Equal(4096 * 4 + 4096 * 8, result.TotalBytesWritten);

        // [2] boot program XML, [3] boot payload, [4] system program XML, [5] system payload.
        Assert.Contains("filename=\"boot.img\"", Encoding.UTF8.GetString(transport.Writes[3]), StringComparison.Ordinal);
        Assert.Equal(bootImage, transport.Writes[4]);
        Assert.Contains("filename=\"system.img\"", Encoding.UTF8.GetString(transport.Writes[5]), StringComparison.Ordinal);
        Assert.Equal(systemImage, transport.Writes[6]);
    }

    [Fact]
    public async Task FlashBundleAsync_EraseEntryWithoutFilename_IssuesErase()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <data>
              <program SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="1"
                       start_sector="0x10" num_partition_sectors="32" />
            </data>
            """);

        var transport = CreateBundleTransport(commandAckCount: 1);
        using var session = CreateSession(transport);
        var orchestrator = new FlashBundleOrchestrator(session);

        var result = await orchestrator.FlashBundleAsync(rawPath, patchPath: null, dir.Path, progress: null, CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(1, result.EraseEntriesProcessed);

        // [0] nop probe, [1] configure, [2] erase XML.
        Assert.Contains("<erase", Encoding.UTF8.GetString(transport.Writes[3]), StringComparison.Ordinal);
        Assert.Contains("physical_partition_number=\"1\"", Encoding.UTF8.GetString(transport.Writes[3]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlashBundleAsync_PatchesAfterProgram()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", SinglePartitionRawProgram("boot.img", 4));
        var patchPath = dir.Write("patch0.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <data>
              <patch SECTOR_SIZE_IN_BYTES="4096" filename="boot.img" physical_partition_number="0"
                     start_sector="0x20" byte_offset="4" value="deadbeef" size_in_bytes="4" />
            </data>
            """);
        dir.WriteBytes("boot.img", new byte[4096 * 4]);

        var transport = CreateBundleTransport(commandAckCount: 2); // program ACK + patch ACK
        using var session = CreateSession(transport);
        var orchestrator = new FlashBundleOrchestrator(session);

        var result = await orchestrator.FlashBundleAsync(rawPath, patchPath, dir.Path, progress: null, CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(1, result.ProgramEntriesProcessed);
        Assert.Equal(1, result.PatchEntriesProcessed);

        // [2] geometry, [3] program XML, [4] payload, [5] patch XML.
        Assert.Contains("filename=\"boot.img\"", Encoding.UTF8.GetString(transport.Writes[3]), StringComparison.Ordinal);
        Assert.Contains("<patch", Encoding.UTF8.GetString(transport.Writes[5]), StringComparison.Ordinal);
        Assert.Contains("value=\"deadbeef\"", Encoding.UTF8.GetString(transport.Writes[5]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlashBundleAsync_DryRun_ParsesWithoutDeviceWrites()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", TwoPartitionRawProgram);
        dir.WriteBytes("boot.img", new byte[4096 * 4]);
        dir.WriteBytes("system.img", new byte[4096 * 8]);

        var transport = CreateBundleTransport(commandAckCount: 0);
        using var session = CreateSession(transport);
        var orchestrator = new FlashBundleOrchestrator(session);

        var result = await orchestrator.FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            DryRun = true
        }, progress: null, CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(2, result.ProgramEntriesSkipped);

        Assert.Empty(transport.Writes);
        var allWrites = string.Concat(transport.Writes.Select(w => Encoding.UTF8.GetString(w)));
        Assert.DoesNotContain("<program", allWrites, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlashBundleAsync_LabelFilter_ProgramsOnlyMatchingPartitions()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", TwoPartitionRawProgram);
        dir.WriteBytes("boot.img", new byte[4096 * 4]);
        dir.WriteBytes("system.img", new byte[4096 * 8]);

        var transport = CreateBundleTransport(commandAckCount: 1);
        using var session = CreateSession(transport);
        var orchestrator = new FlashBundleOrchestrator(session);

        var result = await orchestrator.FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = dir.Path,
            Labels = ["system"]
        }, progress: null, CancellationToken.None);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(1, result.ProgramEntriesProcessed);

        var allWrites = string.Concat(transport.Writes.Select(w => Encoding.UTF8.GetString(w)));
        Assert.Contains("system.img", allWrites, StringComparison.Ordinal);
        Assert.DoesNotContain("boot.img", allWrites, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlashBundleAsync_MissingImageFile_ReportsError()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", SinglePartitionRawProgram("missing.img", 4));

        var transport = CreateBundleTransport(commandAckCount: 0);
        using var session = CreateSession(transport);
        var orchestrator = new FlashBundleOrchestrator(session);

        var result = await orchestrator.FlashBundleAsync(rawPath, patchPath: null, dir.Path, progress: null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        Assert.Equal(0, result.ProgramEntriesProcessed);
        Assert.Equal(0, result.ProgramEntriesSkipped);
        // The program never reached the device (image file missing).
        // 镜像文件缺失，program 从未发送到设备。
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task FlashBundleAsync_InvalidRawProgramPath_ReturnsFatalError()
    {
        using var session = CreateSession(new ScriptedCommTransport());
        var orchestrator = new FlashBundleOrchestrator(session);

        var result = await orchestrator.FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = "does-not-exist.xml",
            ImageDirectory = "."
        }, progress: null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, e => e.Contains("rawprogram", StringComparison.OrdinalIgnoreCase));
    }

    // ============== helpers ==============

    [Fact]
    public async Task FlashBundleAsync_FirstProgramRejected_DoesNotContinueOrPatch()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", TwoPartitionRawProgram);
        var patchPath = dir.Write("patch0.xml",
            "<data><patch filename=\"DISK\" start_sector=\"0\" byte_offset=\"0\" value=\"0\" size_in_bytes=\"4\"/></data>");
        dir.WriteBytes("boot.img", new byte[4096 * 4]);
        dir.WriteBytes("system.img", new byte[4096 * 8]);
        var transport = CreateBundleTransport(0);
        var respond = transport.OnWrite!;
        transport.OnWrite = bytes => Encoding.UTF8.GetString(bytes).Contains("<program ", StringComparison.Ordinal)
            ? [Encoding.UTF8.GetBytes("<data><response value=\"NAK\"/></data>")]
            : respond(bytes);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            rawPath, patchPath, dir.Path, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        Assert.Equal(0, result.ProgramEntriesProcessed);
        Assert.Equal(4, transport.Writes.Count);
        Assert.Contains("boot.img", Encoding.UTF8.GetString(transport.Writes[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlashBundleAsync_MissingLaterImage_RejectsBeforeAnyDeviceIo()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", TwoPartitionRawProgram);
        dir.WriteBytes("boot.img", new byte[4096 * 4]);
        using var transport = CreateBundleTransport(1);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            rawPath, null, dir.Path, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task FlashBundleAsync_Cancelled_PropagatesWithoutDeviceIo()
    {
        using var transport = CreateBundleTransport(0);
        using var session = CreateSession(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions(), null, cancellation.Token));

        Assert.Empty(transport.Writes);
    }

    [Theory]
    [InlineData("../outside.img")]
    [InlineData("images/../../outside.img")]
    [InlineData("C:/outside.img")]
    [InlineData("/outside.img")]
    [InlineData("boot.img:stream")]
    public void ParseAll_ImagePathEscapesFirmwareRoot_Rejects(string filename)
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", SinglePartitionRawProgram(filename, 4));

        Assert.Throws<EdlException>(() => RawProgramParser.ParseAll([rawPath], dir.Path));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(long.MaxValue)]
    public async Task FlashBundleAsync_InvalidSourceOffset_FailsWithoutProgramCommand(long offset)
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", new byte[4096]);
        var rawPath = dir.Write("rawprogram0.xml", $"""
            <data><program filename="boot.img" start_sector="0" num_partition_sectors="1"
              SECTOR_SIZE_IN_BYTES="4096" file_sector_offset="{offset}"/></data>
            """);
        using var transport = CreateBundleTransport(0);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            rawPath, null, dir.Path, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        Assert.DoesNotContain(transport.Writes, bytes => Encoding.UTF8.GetString(bytes).Contains("<program ", StringComparison.Ordinal));
    }

    private static string SinglePartitionRawProgram(string filename, int sectors)
        => $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <data>
              <program SECTOR_SIZE_IN_BYTES="4096" file_sector_offset="0" filename="{{filename}}"
                       physical_partition_number="0" start_sector="0x8000" num_partition_sectors="{{sectors}}"
                       label="{{System.IO.Path.GetFileNameWithoutExtension(filename)}}" sparse="false" />
            </data>
            """;

    private static EdlSession CreateSession(ScriptedCommTransport transport)
        => new(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            AutoDetectSectorSize = false
        });

    private static ScriptedCommTransport CreateBundleTransport(int commandAckCount)
    {
        // 响应由写入序列驱动（而非预入队）：Sahara 被动 HELLO 探测在队列为空时立即超时，
        // 随后 <nop> 探测 / <configure> 握手 / 各命令按写入内容获得对应应答，避免预入队
        // 响应被被动探测提前消费导致后续命令空等满 120s 响应超时。
        // Responses are driven by the write sequence (not pre-enqueued): the passive Sahara HELLO
        // probe times out immediately on an empty queue, then the <nop> probe / <configure>
        // handshake / each command receive the matching reply by write content, so pre-enqueued
        // responses are never consumed by the passive probe and later commands do not idle for the
        // full 120s response timeout.
        var acksRemaining = commandAckCount;
        var transport = new ScriptedCommTransport
        {
            OnWrite = bytes =>
            {
                var text = Encoding.UTF8.GetString(bytes);
                if (text.Contains("<getstorageinfo", StringComparison.Ordinal))
                {
                    return [Encoding.UTF8.GetBytes("""
                        <data><log value='{"storage_info":{"block_size":4096,"total_blocks":1048576}}'/><response value="ACK"/></data>
                        """)];
                }
                // A running Firehose programmer answers the probe <nop> with logs + ACK.
                // 运行中的 Firehose programmer 以日志 + ACK 应答探测 <nop>。
                if (text.Contains("<nop", StringComparison.Ordinal))
                {
                    return [Encoding.UTF8.GetBytes(NopResponseXml)];
                }

                // The configure handshake is answered with the payload-negotiating ACK.
                // configure 握手以携带负载协商的 ACK 应答。
                if (text.Contains("<configure", StringComparison.Ordinal))
                {
                    return [Encoding.UTF8.GetBytes(ConfigureAckXml)];
                }

                // program/erase/patch 命令以普通 ACK 应答，数量受 commandAckCount 限制。
                // Commands (program/erase/patch) are answered with a plain ACK, bounded by commandAckCount.
                if (acksRemaining > 0 && (text.Contains("<program", StringComparison.Ordinal)
                    || text.Contains("<erase", StringComparison.Ordinal)
                    || text.Contains("<patch", StringComparison.Ordinal)))
                {
                    acksRemaining--;
                    return text.Contains("<program", StringComparison.Ordinal)
                        ? [Encoding.UTF8.GetBytes("<data><response value=\"ACK\" rawmode=\"true\"/></data>"), Encoding.UTF8.GetBytes(AckXml)]
                        : [Encoding.UTF8.GetBytes(AckXml)];
                }

                return [];
            }
        };
        return transport;
    }

    private static long ParseStartSector(string value)
    {
        // NumberStyles.HexNumber 不接受 "0x" 前缀，先剥除再按十六进制解析。
        // NumberStyles.HexNumber rejects the "0x" prefix; strip it before hex parsing.
        var hexText = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return long.TryParse(hexText, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hex)
            ? hex
            : long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}