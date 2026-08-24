using UotanStudio.Core.Inspector;
using UotanStudio.Firmware.Cpio;
using UotanStudio.Firmware.Cpio.Model;
using UotanStudio.Firmware.Payload;
using Xunit;

namespace UotanStudio.Core.Tests;

/// <summary>
/// Unit tests for <see cref="FirmwareInspector"/>.
/// <para><see cref="FirmwareInspector"/> 的单元测试。</para>
/// </summary>
public class FirmwareInspectorTests
{
    private static string TempDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "UotanStudioCoreTests", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// A minimal cpio archive should be inspected into a node whose children list its entries.
    /// <para>最小 cpio 归档应被解析为子节点即条目的结构节点。</para>
    /// </summary>
    [Fact]
    public async Task InspectAsync_CpioArchive_ReturnsEntries()
    {
        var dir = TempDir("cpio");
        var archivePath = Path.Combine(dir, "boot.cpio");

        using (var fs = File.Create(archivePath))
        {
            var archive = new CpioArchive();
            archive.SaveAll(fs,
            [
                new ArchiveEntry
                {
                    Name = "init",
                    Data = [0x01, 0x02],
                    Metadata = new ArchiveEntryMetadata
                    {
                        FileType = CpioFileType.Regular,
                        UnixPermissions = 0x1A4,
                        LinkCount = 1
                    }
                },
                new ArchiveEntry
                {
                    Name = "system/bin/sh",
                    Data = [0x03, 0x04, 0x05],
                    Metadata = new ArchiveEntryMetadata
                    {
                        FileType = CpioFileType.Regular,
                        UnixPermissions = 0x1A4,
                        LinkCount = 1
                    }
                }
            ], ArchiveFormat.NewAscii);
        }

        var node = Assert.IsType<InspectorNode>(await FirmwareInspector.InspectAsync(archivePath));

        Assert.Contains(node.Children, c => c.Name == "init");
        Assert.Contains(node.Children, c => c.Name == "system/bin/sh");
    }

    /// <summary>
    /// A minimal payload built by <see cref="PayloadBuilder"/> should be inspected into
    /// a node whose children list its partitions.
    /// <para>由 <see cref="PayloadBuilder"/> 构建的最小 payload 应被解析为子节点即分区的结构节点。</para>
    /// </summary>
    [Fact]
    public async Task InspectAsync_Payload_ReturnsPartitions()
    {
        var dir = TempDir("payload");
        var payloadPath = Path.Combine(dir, "payload.bin");

        using (var builder = new PayloadBuilder())
        using (var bootImage = new MemoryStream(new byte[4096]))
        using (var systemImage = new MemoryStream(new byte[4096]))
        {
            builder.AddPartition("boot", bootImage);
            builder.AddPartition("system", systemImage);
            await builder.BuildAsync(payloadPath);
        }

        var node = Assert.IsType<InspectorNode>(await FirmwareInspector.InspectAsync(payloadPath));

        Assert.Contains(node.Children, c => c.Name == "boot");
        Assert.Contains(node.Children, c => c.Name == "system");
    }

    /// <summary>
    /// An unrecognized file (unknown extension and magic) should be inspected into null.
    /// <para>无法识别的文件（未知扩展名与魔数）应被解析为 null。</para>
    /// </summary>
    [Fact]
    public async Task InspectAsync_Unrecognized_ReturnsNull()
    {
        var dir = TempDir("unknown");
        var path = Path.Combine(dir, "random.dat");
        await File.WriteAllBytesAsync(path, [0xDE, 0xAD, 0xBE, 0xEF]);

        var node = await FirmwareInspector.InspectAsync(path);

        Assert.Null(node);
    }

    /// <summary>
    /// A missing file should be inspected into null.
    /// <para>不存在的文件应被解析为 null。</para>
    /// </summary>
    [Fact]
    public async Task InspectAsync_MissingFile_ReturnsNull()
    {
        var node = await FirmwareInspector.InspectAsync(Path.Combine(Path.GetTempPath(), "definitely_missing_12345.bin"));
        Assert.Null(node);
    }
}