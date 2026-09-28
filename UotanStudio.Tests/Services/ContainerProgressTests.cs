using System.IO.Compression;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;
using UotanStudio.Services.Delivery;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ContainerProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "container-progress-" + Guid.NewGuid().ToString("N"));
    public ContainerProgressTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private sealed class Capture<T> : IProgress<T>
    {
        internal List<T> Values { get; } = [];
        public void Report(T value) { lock (Values) Values.Add(value); }
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("super")]
    [InlineData("sparse-super")]
    [InlineData("payload")]
    [InlineData("zip-payload")]
    public async Task SelectedEntriesReportAggregateBytesAndFinishAtOneHundred(string kind)
    {
        var token = TestContext.Current.CancellationToken;
        var first = Enumerable.Range(0, 8192).Select(i => (byte)(i * 31)).ToArray();
        var second = Enumerable.Repeat((byte)0x39, 16384).ToArray();
        var one = Path.Combine(_root, "system.img"); var two = Path.Combine(_root, "vendor.img");
        File.WriteAllBytes(one, first); File.WriteAllBytes(two, second);
        var container = Path.Combine(_root, "input.bin");
        string[] names = ["system", "vendor"];
        if (kind is "super" or "sparse-super")
            await DeliverySuperBuilder.BuildAsync(
                [new() { SourcePath = one, Partition = "system" }, new() { SourcePath = two, Partition = "vendor" }],
                new() { DeviceSize = 4 * 1024 * 1024, Sparse = kind == "sparse-super" }, container, token: token);
        else if (kind is "payload" or "zip-payload")
        {
            using var builder = new PayloadBuilder(); using var a = File.OpenRead(one); using var b = File.OpenRead(two);
            using (var output = File.Create(container)) builder.AddPartition("system", a).AddPartition("vendor", b).Build(output);
            if (kind == "zip-payload")
            {
                var zipPath = container + ".zip";
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                    zip.CreateEntryFromFile(container, "payload.bin", CompressionLevel.NoCompression);
                container = zipPath;
            }
        }
        else
        {
            names = ["system.img", "vendor.img"];
            using var zip = ZipFile.Open(container, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(one, names[0]); zip.CreateEntryFromFile(two, names[1]);
        }
        var bytes = new Capture<(long Processed, long Total)>(); var percent = new Capture<double>();
        var result = await ContainerEntryExtractor.ExtractAsync(container, names, fromPayloadOnly: kind == "zip-payload",
            cancellationToken: token, outputDirectory: Path.Combine(_root, "output"), progress: percent, byteProgress: bytes);
        Assert.Equal(2, result.OutputPaths.Count);
        Assert.Equal(first, File.ReadAllBytes(result.OutputPaths[0])); Assert.Equal(second, File.ReadAllBytes(result.OutputPaths[1]));
        Assert.Equal((0L, 24576L), bytes.Values[0]); Assert.Equal((24576L, 24576L), bytes.Values[^1]);
        Assert.All(bytes.Values, v => { Assert.Equal(24576L, v.Total); Assert.InRange(v.Processed, 0, v.Total); });
        Assert.Equal(bytes.Values.Select(v => v.Processed).Order(), bytes.Values.Select(v => v.Processed));
        Assert.Equal(100, percent.Values[^1]);
    }
}