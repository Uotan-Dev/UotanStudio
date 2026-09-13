using System.Buffers.Binary;
using System.IO.Compression;
using UotanStudio.Services.Delivery;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class PayloadOtaZipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "payload-ota-" + Guid.NewGuid().ToString("N"));
    public PayloadOtaZipTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private sealed class NoTools : IDeliveryTools
    {
        public Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token)
            => Task.CompletedTask;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PayloadAlwaysPublishesUnsignedOtaWithActualStoredRanges(bool organize, bool recovery)
    {
        var source = Path.Combine(_root, "boot.img");
        File.WriteAllBytes(source, new byte[8192]);
        var plan = new DeliveryPlan
        {
            PackPayload = true, OrganizeDirectory = organize, Recovery = recovery, Txt = false,
            ContainerConfirmed = true, OutputDirectory = Path.Combine(_root, "output"),
            Container = [new() { SourcePath = source, FileName = "boot.img", Partition = "boot" }],
            Files = organize ? [new() { Generated = true, FileName = "ota.zip", Partition = "payload" }] : []
        };
        Assert.Empty(DeliveryScriptGenerator.Generate(plan));
        var service = new DeliveryOutputService(new NoTools());
        var paths = await service.ExportAsync(plan, token: TestContext.Current.CancellationToken);
        Assert.Contains(Path.Combine(plan.OutputDirectory, "ota.zip"), paths);
        Assert.DoesNotContain(paths, p => Path.GetFileName(p) is "payload.bin" or "payload_properties.txt" or "recovery.zip");
        var zipPath = Path.Combine(plan.OutputDirectory, "ota.zip");
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(new[] { "payload.bin", "payload_properties.txt", "META-INF/com/android/metadata" }, zip.Entries.Select(e => e.FullName));
        using var reader = new StreamReader(zip.GetEntry("META-INF/com/android/metadata")!.Open());
        var metadata = (await reader.ReadToEndAsync(TestContext.Current.CancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal("AB", metadata["ota-type"]);
        Assert.False(metadata.ContainsKey("post-build")); // Never invent a target build identity.
        var bytes = File.ReadAllBytes(zipPath);
        var ranges = new Dictionary<string, (long Offset, long Size)>();
        var position = 0;
        foreach (var entry in zip.Entries)
        {
            Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position)));
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 8)));
            var offset = position + 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 26))
                + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 28));
            ranges.Add(entry.Name, (offset, entry.Length));
            position = checked(offset + (int)entry.Length);
        }
        foreach (var name in new[] { "ota-streaming-property-files", "ota-property-files" })
            foreach (var item in metadata[name].TrimEnd().Split(','))
            {
                var fields = item.Split(':');
                var offset = long.Parse(fields[1]); var length = long.Parse(fields[2]);
                if (fields[0] == "payload_metadata.bin")
                {
                    var start = checked((int)ranges["payload.bin"].Offset);
                    Assert.Equal(start, offset);
                    Assert.Equal(24L + (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(start + 12))
                        + BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start + 20)), length);
                }
                else Assert.Equal(ranges[fields[0]], (offset, length));
            }
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2)));
        await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(plan, token: TestContext.Current.CancellationToken));
        Assert.Equal(bytes, File.ReadAllBytes(zipPath));
    }

    [Fact]
    public async Task LegacyPayloadPlanRestoresZipArtifactWithoutChangingContainerMembership()
    {
        var plan = new DeliveryPlan { PackPayload = true, ContainerConfirmed = true,
            Container = [new() { Partition = "boot", FileName = "boot.img" }],
            Files = [new() { Generated = true, FileName = "payload.bin", Partition = "payload" }] };
        var state = DeliveryPlanStore.CaptureElement(_root, plan);
        var restored = DeliveryPlanStore.LoadFromState(state, _root)!;
        Assert.Equal("ota.zip", Assert.Single(restored.Files).FileName);
        var model = new DeliveryViewModel();
        model.Reset(plan);
        Assert.Equal("ota.zip", Assert.Single(model.Output).FileName);
        Assert.Equal("boot", Assert.Single(model.Container).PartitionName);
    }
}
