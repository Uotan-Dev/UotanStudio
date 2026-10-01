using System.Net;
using System.Net.Http.Headers;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashRemotePayloadServiceTests
{
    [Fact]
    public async Task ExtractAsync_RangePayload_PublishesOnlySelectedImage()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var boot = new InstallOperation { Type = InstallOperation.Types.Type.Replace };
            boot.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = 1 });
            var system = new InstallOperation { Type = InstallOperation.Types.Type.Replace };
            system.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = 2 });
            var manifest = new DeltaArchiveManifest { BlockSize = 4 };
            manifest.Partitions.Add(new PartitionUpdate { PartitionName = "boot", Operations = { boot } });
            manifest.Partitions.Add(new PartitionUpdate { PartitionName = "system", Operations = { system } });
            var bytes = PayloadDeltaTests.BuildPayload(manifest, new Dictionary<InstallOperation, byte[]>
            {
                [boot] = "boot"u8.ToArray(),
                [system] = "sys!sys!"u8.ToArray()
            });
            using var client = new HttpClient(new RangeHandler(bytes));
            var service = new FlashRemotePayloadService(client);
            var address = "https://example.test/payload.bin";

            var listed = await service.ListAsync(address, TestContext.Current.CancellationToken);
            Assert.Equal(2, listed.Count);
            var output = await service.ExtractAsync(address, ["boot"], root, TestContext.Current.CancellationToken);

            Assert.Equal("boot"u8.ToArray(), File.ReadAllBytes(Path.Combine(output, "boot.img")));
            Assert.False(File.Exists(Path.Combine(output, "system.img")));
            Assert.Empty(Directory.GetDirectories(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ListAsync_NonPayloadUrl_DoesNotOfferPartitions()
    {
        using var client = new HttpClient(new RangeHandler("ordinary file"u8.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new FlashRemotePayloadService(client).ListAsync("https://example.test/ordinary.bin",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExtractAsync_RangeTransferFails_DoesNotPublishPartialDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var operation = new InstallOperation { Type = InstallOperation.Types.Type.Replace };
            operation.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = 1 });
            var manifest = new DeltaArchiveManifest { BlockSize = 4 };
            manifest.Partitions.Add(new PartitionUpdate { PartitionName = "boot", Operations = { operation } });
            var bytes = PayloadDeltaTests.BuildPayload(manifest,
                new Dictionary<InstallOperation, byte[]> { [operation] = "boot"u8.ToArray() });
            using var client = new HttpClient(new RangeHandler(bytes, failAfterRequests: 2));

            await Assert.ThrowsAnyAsync<Exception>(() => new FlashRemotePayloadService(client)
                .ExtractAsync("https://example.test/payload.bin", ["boot"], root,
                    TestContext.Current.CancellationToken));

            Assert.Empty(Directory.GetDirectories(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RangeHandler(byte[] data, int failAfterRequests = int.MaxValue) : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) > failAfterRequests)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var range = request.Headers.Range?.Ranges.Single();
            var from = checked((int)(range?.From ?? 0));
            var to = Math.Min(data.Length - 1, checked((int)(range?.To ?? data.Length - 1)));
            if (from >= data.Length)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data.AsSpan(from, to - from + 1).ToArray())
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, data.Length);
            return Task.FromResult(response);
        }
    }
}