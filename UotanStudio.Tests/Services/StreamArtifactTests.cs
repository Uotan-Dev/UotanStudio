using System.Text;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Patching;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class StreamArtifactTests
{
    private const long SparseLength = 256L * 1024 * 1024;

    [Fact]
    public async Task FileStreamArtifact_Constructor_RejectsNullPath()
    {
        await Task.CompletedTask;

        Assert.Throws<ArgumentNullException>(() => new FileStreamArtifact(null!));
        Assert.Throws<ArgumentException>(() => new FileStreamArtifact("  "));
    }

    [Fact]
    public async Task FileStreamArtifact_OverExistingFile_ReportsLengthNameAndBytes()
    {
        var path = CreateProbeFilePath();
        var token = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllBytesAsync(path, [0x01, 0x02, 0x03, 0xFE], token);
            var artifact = new FileStreamArtifact(path);

            await using var stream = artifact.OpenRead();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, token);

            Assert.Equal(4L, artifact.Length);
            Assert.Equal(System.IO.Path.GetFileName(path), artifact.DisplayName);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0xFE }, copy.ToArray());
        }
        finally
        {
            DeleteProbe(path);
        }
    }

    [Fact]
    public async Task FileStreamArtifact_MultipleOpenings_EachStartsWithFirstByte()
    {
        var path = CreateProbeFilePath();
        var token = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(path, "payload", token);
            var artifact = new FileStreamArtifact(path);

            await using var first = artifact.OpenRead();
            var head = new byte[3];
            Assert.Equal(3, await first.ReadAsync(head, token));
            var tail = new byte[16];
            var tailCount = await first.ReadAsync(tail, token);

            await using var second = artifact.OpenRead();
            using var whole = new MemoryStream();
            await second.CopyToAsync(whole, token);

            Assert.Equal("pay", Encoding.UTF8.GetString(head));
            Assert.Equal("load", Encoding.UTF8.GetString(tail, 0, tailCount));
            Assert.Equal("payload", Encoding.UTF8.GetString(whole.ToArray()));
        }
        finally
        {
            DeleteProbe(path);
        }
    }

    [Fact]
    public async Task FileStreamArtifact_FileOpenedForSharedRead_AllowsConcurrentOpen()
    {
        var path = CreateProbeFilePath();
        var token = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(path, "shared read", token);
            var artifact = new FileStreamArtifact(path);
            await using var producer = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            await using var reader = artifact.OpenRead();
            using var copy = new MemoryStream();
            await reader.CopyToAsync(copy, token);

            Assert.Equal("shared read", Encoding.UTF8.GetString(copy.ToArray()));
        }
        finally
        {
            DeleteProbe(path);
        }
    }

    [Fact]
    public async Task FileStreamArtifact_ProducingWriterHoldsExclusiveAccess_OpenReadThrows()
    {
        var path = CreateProbeFilePath();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var artifact = new FileStreamArtifact(path);
            await using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await writer.WriteAsync(Encoding.UTF8.GetBytes("exclusive"), token);
            await writer.FlushAsync(token);

            Assert.ThrowsAny<IOException>(() => artifact.OpenRead());
        }
        finally
        {
            DeleteProbe(path);
        }
    }

    [Fact]
    public async Task FileStreamArtifact_MissingFile_ReportsNullLengthAndThrowsOnOpen()
    {
        var path = CreateProbeFilePath();
        await Task.CompletedTask;

        var artifact = new FileStreamArtifact(path);

        Assert.Null(artifact.Length);
        Assert.False(File.Exists(path));
        Assert.ThrowsAny<IOException>(() => artifact.OpenRead());
    }

    [Fact]
    public async Task FileStreamArtifact_DisposeAsync_KeepsFileReadableAndIsIdempotent()
    {
        var path = CreateProbeFilePath();
        var token = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(path, "survives", token);
            var artifact = new FileStreamArtifact(path);

            await artifact.DisposeAsync();
            await artifact.DisposeAsync();

            await using var stream = artifact.OpenRead();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal("survives", await reader.ReadToEndAsync(token));
            Assert.Equal(8L, artifact.Length);
        }
        finally
        {
            DeleteProbe(path);
        }
    }

    [Fact]
    public async Task MemoryStreamArtifact_EmptyPayload_ReportsZeroLengthAndEmptyStream()
    {
        var token = TestContext.Current.CancellationToken;

        var artifact = new MemoryStreamArtifact(ReadOnlyMemory<byte>.Empty);
        var stream = artifact.OpenRead();
        try
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, token);

            Assert.Equal(0L, artifact.Length);
            Assert.Null(artifact.DisplayName);
            Assert.Equal(0, copy.Length);
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    [Fact]
    public async Task MemoryStreamArtifact_MultipleOpenings_AreIndependentAndReadOnly()
    {
        var token = TestContext.Current.CancellationToken;
        var artifact = new MemoryStreamArtifact(new byte[] { 0x10, 0x20, 0x30 }, "probe.bin");

        await using var first = artifact.OpenRead();
        var head = new byte[1];
        Assert.Equal(1, await first.ReadAsync(head, token));
        await using var second = artifact.OpenRead();
        using var copy = new MemoryStream();
        await second.CopyToAsync(copy, token);

        Assert.Equal(3L, artifact.Length);
        Assert.Equal("probe.bin", artifact.DisplayName);
        Assert.False(first.CanWrite);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, copy.ToArray());
        Assert.Equal(1L, first.Position);
    }

    [Fact]
    public async Task NodeDataUtilOpenRead_StreamArtifact_OpensIndependentFreshStreams()
    {
        var token = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes("artifact payload");
        var artifact = new MemoryStreamArtifact(bytes, "payload.txt");

        var first = NodeDataUtil.OpenRead(artifact);
        var second = NodeDataUtil.OpenRead(artifact);
        try
        {
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotSame(first, second);
            using var firstCopy = new MemoryStream();
            await first.CopyToAsync(firstCopy, token);
            using var secondCopy = new MemoryStream();
            await second.CopyToAsync(secondCopy, token);
            Assert.Equal(bytes, firstCopy.ToArray());
            Assert.Equal(bytes, secondCopy.ToArray());
        }
        finally
        {
            if (first is not null)
            {
                await first.DisposeAsync();
            }
            if (second is not null)
            {
                await second.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task NodeDataUtilOpenRead_ValueKinds_ReturnExpectedStreamOrNull()
    {
        var path = CreateProbeFilePath();
        var token = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(path, "from path", token);
            var payload = new byte[] { 1, 2, 3 };

            Assert.Null(NodeDataUtil.OpenRead(null));
            Assert.Equal(payload, await ReadAllAsync(NodeDataUtil.OpenRead(payload), token));
            Assert.Equal(payload, await ReadAllAsync(NodeDataUtil.OpenRead(new ReadOnlyMemory<byte>(payload)), token));
            Assert.Equal("from path", Encoding.UTF8.GetString(await ReadAllAsync(NodeDataUtil.OpenRead(path), token)));
            Assert.Equal("from path", Encoding.UTF8.GetString(
                await ReadAllAsync(NodeDataUtil.OpenRead(new FileArtifact(path)), token)));
            Assert.Equal("42", Encoding.UTF8.GetString(await ReadAllAsync(NodeDataUtil.OpenRead(42), token)));
        }
        finally
        {
            DeleteProbe(path);
        }
    }

    [Fact]
    public async Task NodeDataUtilOpenRead_ReadableStream_ReturnsSameInstance()
    {
        var token = TestContext.Current.CancellationToken;
        var source = new MemoryStream(new byte[] { 9, 8, 7 }, writable: false);
        try
        {
            var opened = NodeDataUtil.OpenRead(source);

            Assert.Same(source, opened);
            using var copy = new MemoryStream();
            await opened!.CopyToAsync(copy, token);
            Assert.Equal(new byte[] { 9, 8, 7 }, copy.ToArray());
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task NodeDataUtilOpenRead_ArtifactAboveInMemoryLimit_StreamsBoundedWindow()
    {
        var path = CreateSparseFile(SparseLength);
        try
        {
            var artifact = new FileStreamArtifact(path);
            var token = TestContext.Current.CancellationToken;

            var stream = NodeDataUtil.OpenRead(artifact);
            try
            {
                Assert.NotNull(stream);
                var window = new byte[4096];
                var read = await stream.ReadAsync(window, token);

                Assert.Equal(4096, read);
                Assert.All(window, value => Assert.Equal((byte)0, value));
                Assert.Equal(artifact.Length, stream.Length);
            }
            finally
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync();
                }
            }

            Assert.Throws<InvalidDataException>(() => NodeDataUtil.ToBytes(artifact, token));
        }
        finally
        {
            DeleteProbe(path);
        }
    }

    [Fact]
    public async Task NodeDataUtilToBytes_SmallArtifacts_PreserveBytes()
    {
        var path = CreateProbeFilePath();
        var token = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllBytesAsync(path, [4, 5, 6], token);
            var inMemory = new MemoryStreamArtifact(new byte[] { 1, 2 }, "two.bin");

            Assert.Equal(new byte[] { 4, 5, 6 }, NodeDataUtil.ToBytes(new FileStreamArtifact(path), token));
            Assert.Equal(new byte[] { 1, 2 }, NodeDataUtil.ToBytes(inMemory, token));
        }
        finally
        {
            DeleteProbe(path);
        }
    }

    [Fact]
    public async Task NodeArtifactPublisher_MemoryStreamArtifact_WritesBytesAndReportsLength()
    {
        var directory = Directory.CreateTempSubdirectory("uotan-stream-publish-").FullName;
        var destination = Path.Combine(directory, "result.bin");
        var token = TestContext.Current.CancellationToken;
        try
        {
            var payload = Enumerable.Range(0, 300_000).Select(value => (byte)(value * 7)).ToArray();
            await File.WriteAllTextAsync(destination, "previous", token);

            var length = await NodeArtifactPublisher.PublishAsync(new MemoryStreamArtifact(payload), destination, token);

            Assert.Equal(payload.Length, length);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination, token));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NodeArtifactPublisher_FileStreamArtifact_CopiesWithoutReplacingDestinationOnCancellation()
    {
        var directory = Directory.CreateTempSubdirectory("uotan-stream-publish-").FullName;
        var source = Path.Combine(directory, "source.bin");
        var destination = Path.Combine(directory, "result.bin");
        var token = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            await File.WriteAllBytesAsync(source, [0xAB, 0xCD, 0xEF], token);
            await File.WriteAllTextAsync(destination, "previous", token);
            var artifact = new FileStreamArtifact(source);

            var published = await NodeArtifactPublisher.PublishAsync(artifact, destination, token);
            cancellation.Cancel();

            Assert.Equal(3L, published);
            Assert.Equal(new byte[] { 0xAB, 0xCD, 0xEF }, await File.ReadAllBytesAsync(destination, token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => NodeArtifactPublisher.PublishAsync(artifact, destination, cancellation.Token));
            Assert.Equal(new byte[] { 0xAB, 0xCD, 0xEF }, await File.ReadAllBytesAsync(destination, token));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NodeValueFormatter_StreamArtifacts_ShowNameAndLength()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-format", "probe.bin");
        await Task.CompletedTask;

        var file = new FileStreamArtifact(path);
        var named = new MemoryStreamArtifact(new byte[1500], "data.bin");
        var unnamed = new MemoryStreamArtifact(new byte[3]);

        Assert.Equal("probe.bin", NodeValueFormatter.Describe(file, null));
        Assert.Equal("data.bin (1,500 B)", NodeValueFormatter.Describe(named, null));
        Assert.Equal(" (3 B)", NodeValueFormatter.Describe(unnamed, null));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task NodeValueFormatter_NonArtifactValues_UseBoundedPreview()
    {
        await Task.CompletedTask;

        var edits = PatchImageEdits.Empty.Append(new PatchImageTextEdit("/build.prop", "text", "utf8", ""));
        var shortPath = "/" + new string('a', 200);
        var longEdits = PatchImageEdits.Empty.Append(new PatchImageTextEdit(shortPath, "text", "utf8", ""));
        var joined = string.Join(Environment.NewLine, edits.Items.Select(edit => edit.Path));

        Assert.Equal(string.Empty, NodeValueFormatter.Describe(null, null));
        Assert.Equal("plain text", NodeValueFormatter.Describe("plain text", null));
        Assert.Equal("1,024 B", NodeValueFormatter.Describe(new byte[1024], null));
        Assert.Equal("1.5", NodeValueFormatter.Describe(1.5, null));
        Assert.Equal(string.Empty, NodeValueFormatter.Describe(new object(), null));
        Assert.Equal(joined, NodeValueFormatter.Describe(edits, null));
        var truncated = NodeValueFormatter.Describe(longEdits, null);
        Assert.Equal(shortPath[..160], truncated);
        Assert.Equal(160, truncated.Length);
    }

    [Fact]
    public async Task NodeValueFormatter_OutputOverLimit_TruncatesWithEllipsis()
    {
        await Task.CompletedTask;
        var text = new string('x', 900);

        var described = NodeValueFormatter.Describe(text, null);
        var exact = NodeValueFormatter.Describe(new string('y', 800), null);
        var list = NodeValueFormatter.Describe(new[] { text, text }, null);
        var expectedList = text[..160] + Environment.NewLine + text[..160];

        Assert.Equal(801, described.Length);
        Assert.Equal(text[..800] + "…", described);
        Assert.Equal(800, exact.Length);
        Assert.Equal(expectedList, list);
        Assert.Equal(expectedList.Length, list.Length);
    }

    private static string CreateProbeFilePath()
        => Path.Combine(Path.GetTempPath(), "uotan-stream-artifact-" + Guid.NewGuid().ToString("N") + ".bin");

    private static string CreateSparseFile(long length)
    {
        var path = CreateProbeFilePath();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.SetLength(length);
        return path;
    }

    private static async Task<byte[]> ReadAllAsync(Stream? stream, CancellationToken cancellationToken)
    {
        Assert.NotNull(stream);
        await using var owned = stream;
        using var copy = new MemoryStream();
        await owned.CopyToAsync(copy, cancellationToken);
        return copy.ToArray();
    }

    private static void DeleteProbe(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}