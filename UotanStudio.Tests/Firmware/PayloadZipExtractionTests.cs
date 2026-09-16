using System.IO.Compression;
using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Firmware.SharedKernel;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// ZIP-wrapped payload extraction tests: the embedded payload is always materialized to a temporary
/// file before extraction, and byte progress spans the whole job smoothly.
/// <para>ZIP 包裹载荷提取测试：内嵌 payload 始终在提取前物化到临时文件，字节进度平滑覆盖整个作业。</para>
/// </summary>
public class PayloadZipExtractionTests
{
    private const uint BlockSize = 4;

    [Fact]
    public async Task StoredZip_ExtractAsync_YieldsCorrectImages()
    {
        // Arrange
        var (payload, expected) = BuildFullPayload();
        var zipPath = CreateZip(Path.Combine(Path.GetTempPath(), $"uotan-payload-stored-{Guid.NewGuid():N}.zip"), "payload.bin", payload, CompressionLevel.NoCompression);
        var outputDir = Path.Combine(Path.GetTempPath(), $"uotan-payload-out-{Guid.NewGuid():N}");
        try
        {
            // Act
            await PayloadParser.ExtractAsync(zipPath, null, outputDir, TestContext.Current.CancellationToken);

            // Assert
            foreach (var (name, data) in expected)
            {
                Assert.Equal(data, File.ReadAllBytes(Path.Combine(outputDir, name + ".img")));
            }
        }
        finally
        {
            File.Delete(zipPath);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task DeflatedZip_ExtractAsync_YieldsCorrectImages_AndDeletesTempPayload()
    {
        // Arrange
        var (payload, expected) = BuildFullPayload();
        var zipPath = CreateZip(Path.Combine(Path.GetTempPath(), $"uotan-payload-deflated-{Guid.NewGuid():N}.zip"), "payload.bin", payload, CompressionLevel.Fastest);
        var outputDir = Path.Combine(Path.GetTempPath(), $"uotan-payload-out-{Guid.NewGuid():N}");
        try
        {
            // Act
            await PayloadParser.ExtractAsync(zipPath, null, outputDir, TestContext.Current.CancellationToken);

            // Assert
            foreach (var (name, data) in expected)
            {
                Assert.Equal(data, File.ReadAllBytes(Path.Combine(outputDir, name + ".img")));
            }
        }
        finally
        {
            File.Delete(zipPath);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task StoredZip_ByteProgress_IsMonotonicAndReachesTotal()
    {
        // Arrange
        var (payload, _) = BuildFullPayload();
        var zipPath = CreateZip(Path.Combine(Path.GetTempPath(), $"uotan-payload-stored-{Guid.NewGuid():N}.zip"), "payload.bin", payload, CompressionLevel.NoCompression);
        var outputDir = Path.Combine(Path.GetTempPath(), $"uotan-payload-out-{Guid.NewGuid():N}");
        var reports = new List<(long Processed, long Total)>();
        try
        {
            // Act
            await PayloadParser.ExtractAsync(zipPath, null, outputDir, TestContext.Current.CancellationToken,
                byteProgress: new CollectingProgress<(long Processed, long Total)>(reports));
        }
        finally
        {
            File.Delete(zipPath);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }

        // Assert: 单调不回退，且最终达到 (total, total)。
        // Monotonic, non-regressing, and finally reaching (total, total).
        Assert.NotEmpty(reports);
        // STORED 原址直读路径不应把容器（payload.bin）字节算入总进度，Total 即分区提取字节总量(16+20)。
        // The STORED in-place path must not inflate the total with the container bytes; Total equals the
        // partition extraction bytes (16+20).
        Assert.All(reports, r => Assert.Equal(36, r.Total));
        long? last = null;
        foreach (var (processed, total) in reports)
        {
            Assert.True(total > 0);
            Assert.True(processed <= total);
            if (last.HasValue) Assert.True(processed >= last.Value, "progress must be monotonic");
            last = processed;
        }

        Assert.Equal(reports[^1].Total, reports[^1].Processed);
    }

    [Fact]
    public async Task Handler_UnpackAsync_ReportsMonotonicPercentTo100()
    {
        // Arrange
        var (payload, _) = BuildFullPayload();
        var zipPath = CreateZip(Path.Combine(Path.GetTempPath(), $"uotan-payload-stored-{Guid.NewGuid():N}.zip"), "payload.bin", payload, CompressionLevel.NoCompression);
        var outputDir = Path.Combine(Path.GetTempPath(), $"uotan-payload-out-{Guid.NewGuid():N}");
        var captured = new List<(double Percent, string Message)>();
        var options = new UnpackOptions { Progress = new CapturingFirmwareProgress(captured) };
        try
        {
            // Act
            var result = await new PayloadFormatHandler().UnpackAsync(zipPath, outputDir, options, TestContext.Current.CancellationToken);

            // Assert
            Assert.True(result.Success, result.Summary);
            Assert.Equal(100.0, captured[^1].Percent, precision: 3);
            for (var i = 1; i < captured.Count; i++)
            {
                Assert.True(captured[i].Percent >= captured[i - 1].Percent, "percent must be monotonic");
            }
        }
        finally
        {
            File.Delete(zipPath);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>
    /// Builds a two-partition full payload and the expected output images.
    /// <para>构建包含两个分区的完整载荷与期望输出镜像。</para>
    /// </summary>
    private static (byte[] Payload, Dictionary<string, byte[]> Expected) BuildFullPayload()
    {
        var system = new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var vendor = new byte[20] { 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 9, 8, 7 };

        var manifest = new DeltaArchiveManifest { BlockSize = BlockSize, MinorVersion = 9 };
        manifest.Partitions.Add(PartitionWithReplace("system", system));
        manifest.Partitions.Add(PartitionWithReplace("vendor", vendor));

        var data = new Dictionary<InstallOperation, byte[]>
        {
            [manifest.Partitions[0].Operations[0]] = system,
            [manifest.Partitions[1].Operations[0]] = vendor
        };

        var payload = PayloadDeltaTests.BuildPayload(manifest, data);
        return (payload, new Dictionary<string, byte[]>
        {
            ["system"] = system,
            ["vendor"] = vendor
        });
    }

    private static PartitionUpdate PartitionWithReplace(string name, byte[] data)
    {
        var numBlocks = checked((uint)(data.Length / BlockSize));
        var op = new InstallOperation { Type = InstallOperation.Types.Type.Replace };
        op.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = numBlocks });
        var partition = new PartitionUpdate
        {
            PartitionName = name,
            NewPartitionInfo = new PartitionInfo { Size = (ulong)data.Length, Hash = ByteString.CopyFrom(SHA256.HashData(data)) }
        };
        partition.Operations.Add(op);
        return partition;
    }

    /// <summary>
    /// Creates a ZIP at <paramref name="zipPath"/> holding a single entry written with the given level.
    /// <para>在 <paramref name="zipPath"/> 创建一个含单个条目的 ZIP，并以指定压缩级别写入。</para>
    /// </summary>
    private static string CreateZip(string zipPath, string entryName, byte[] content, CompressionLevel level)
    {
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(entryName, level);
            using (var writer = entry.Open())
            {
                writer.Write(content, 0, content.Length);
            }
        }

        return zipPath;
    }

    /// <summary>
    /// A synchronous <see cref="IProgress{T}"/> that appends every report to a list, so progress can
    /// be asserted without relying on async SynchronizationContext delivery.
    /// <para>一个同步的 <see cref="IProgress{T}"/>，把每次上报追加到列表，从而无需依赖异步
    /// SynchronizationContext 即可对进度进行断言。</para>
    /// </summary>
    private sealed class CollectingProgress<T>(List<T> sink) : IProgress<T>
    {
        public void Report(T value) => sink.Add(value);
    }

    /// <summary>
    /// A synchronous <see cref="IFirmwareProgress"/> that records percent/message pairs.
    /// <para>一个同步的 <see cref="IFirmwareProgress"/>，记录百分比/消息对。</para>
    /// </summary>
    private sealed class CapturingFirmwareProgress(List<(double Percent, string Message)> sink) : IFirmwareProgress
    {
        public void Report(double percent, string message) => sink.Add((percent, message));
    }
}