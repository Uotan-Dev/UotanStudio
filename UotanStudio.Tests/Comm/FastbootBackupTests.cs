using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Core.Backup;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm;

/// <summary>
/// Fastboot backup/restore tests: the fetched partition bytes are written to disk verbatim
/// (sparse preserved as-is) and the restore issues the flash sequence.
/// <para>Fastboot 备份/还原测试：fetch 的分区字节原样落盘（sparse 保留），还原走 flash 序列。</para>
/// </summary>
public class FastbootBackupTests
{
    private const long PartitionSize = 0x20000; // 128 KiB

    [Fact]
    public void BackupFastbootPartition_WritesExactDevicePayload()
    {
        var partitionBytes = new byte[PartitionSize];
        Random.Shared.NextBytes(partitionBytes);

        var transport = new FakeCommTransport();
        transport.Enqueue("OKAY0x00020000");        // getvar:partition-size:boot (128 KiB)
        transport.Enqueue("OKAYno");                // getvar:has-slot:boot
        transport.Enqueue("OKAY");                  // getvar:max-fetch-size (empty -> -1)
        transport.Enqueue("DATA00020000");          // fetch:boot:0:20000 -> DATA 0x20000 bytes
        transport.Enqueue(partitionBytes);
        transport.Enqueue("OKAY");

        var session = new FastbootSession(transport);
        using var dir = new TempDirectory();
        var outPath = Path.Combine(dir.Path, "boot-backup.img");

        try
        {
            var result = BackupService.BackupFastbootPartition(session, "boot", outPath, progress: null, CancellationToken.None);

            Assert.True(result.Success, result.Summary);
            Assert.Equal(PartitionSize, result.Bytes);

            var written = File.ReadAllBytes(outPath);
            Assert.Equal(partitionBytes, written);
            Assert.Equal(Sha256Hex(partitionBytes), Sha256Hex(written));

            var commands = transport.Writes.Select(w => Encoding.UTF8.GetString(w)).ToList();
            Assert.Contains("getvar:has-slot:boot", commands);
            Assert.Contains("getvar:partition-size:boot", commands);
            Assert.Contains("getvar:max-fetch-size", commands);
            Assert.Contains("fetch:boot:0:20000", commands);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public void RestoreFastbootPartition_IssuesDownloadAndFlash()
    {
        var imageBytes = new byte[16384];
        Random.Shared.NextBytes(imageBytes);

        var transport = new FakeCommTransport();
        // DownloadData: max-download-size query, download command -> DATA, trailing OKAY.
        transport.Enqueue("OKAY");                    // getvar:max-download-size (empty -> fallback)
        transport.Enqueue($"DATA{imageBytes.Length:x8}"); // download:00004000
        transport.Enqueue("OKAY");                    // payload accepted
        transport.Enqueue("OKAY");                    // flash:boot

        var session = new FastbootSession(transport);
        using var dir = new TempDirectory();
        var imagePath = dir.WriteBytes("boot.img", imageBytes);

        try
        {
            var result = BackupService.RestoreFastbootPartition(session, "boot", imagePath, progress: null, CancellationToken.None);

            Assert.True(result.Success, result.Summary);
            var commands = transport.Writes.Select(w => Encoding.UTF8.GetString(w)).ToList();
            Assert.Contains(commands, c => c.StartsWith("download:", StringComparison.Ordinal));
            Assert.Contains("flash:boot", commands);
        }
        finally
        {
            session.Dispose();
        }
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
