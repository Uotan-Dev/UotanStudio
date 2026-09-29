using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Mcp;
using UotanStudio.Mcp.Tools;
using UotanStudio.Services.Firmware;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Guards the host and MCP surface of the sparsechunk restore/split capability: byte identity after a merge,
/// atomic publication, source deletion only after the output was published, and failures that leave no output.
/// <para>守护 sparsechunk 还原/拆分能力的宿主与 MCP 出口：合并后的字节一致性、原子发布、
/// 仅在输出发布后删除源片，以及失败时不产出目标文件。</para>
/// </summary>
public sealed class FirmwareSparseChunkHostTests
{
    [Fact]
    public async Task RestoreSparseChunksAsync_RawPieces_RestoresOriginalBytesAndReportsVariant()
    {
        using var temp = new TempDirectory();
        var raw = BuildRaw();
        var pieces = await SplitAsync(temp, raw);
        var output = Path.Combine(temp.Path, "restored.img");

        var result = await FirmwareService.Instance.RestoreSparseChunksAsync(pieces[1], output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(raw, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal("RawStream", result.Metadata["variant"]);
        Assert.Equal(pieces.Count, result.Entries.Count);
    }

    [Fact]
    public async Task RestoreSparseChunksAsync_DeleteSources_RemovesPiecesOnlyAfterPublish()
    {
        using var temp = new TempDirectory();
        var raw = BuildRaw();
        var pieces = await SplitAsync(temp, raw);
        var output = Path.Combine(temp.Path, "restored.img");

        var result = await FirmwareService.Instance.RestoreSparseChunksAsync(pieces[0], output,
            deleteSources: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(File.Exists(output));
        Assert.All(pieces, piece => Assert.False(File.Exists(piece)));
    }

    [Fact]
    public async Task RestoreSparseChunksAsync_MissingPiece_ReturnsFailureAndLeavesOutputAbsent()
    {
        using var temp = new TempDirectory();
        var raw = BuildRaw();
        var pieces = await SplitAsync(temp, raw);
        File.Delete(pieces[1]);
        var output = Path.Combine(temp.Path, "restored.img");

        var result = await FirmwareService.Instance.RestoreSparseChunksAsync(pieces[0], output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task SplitSparseChunksAsync_RawImage_ProducesContiguousPiecesThatReassemble()
    {
        using var temp = new TempDirectory();
        var raw = BuildRaw();
        var input = temp.WriteBytes("super.raw.img", raw);
        var basePath = Path.Combine(temp.Path, "super.img");

        var result = await FirmwareService.Instance.SplitSparseChunksAsync(input, basePath, 32_768,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(4, result.Entries.Count);
        var reassembled = result.Entries
            .Select(name => File.ReadAllBytes(Path.Combine(temp.Path, name)))
            .SelectMany(bytes => bytes)
            .ToArray();
        Assert.Equal(raw, reassembled);
    }

    [Fact]
    public async Task MergeSparseChunksAsync_McpHandler_PublishesBytesInsideSandbox()
    {
        using var temp = new TempDirectory();
        var raw = BuildRaw();
        var pieces = await SplitAsync(temp, raw);
        var output = Path.Combine(temp.Path, "merged.img");
        var sandbox = new PathSandbox(temp.Path);

        var toolResult = await FirmwareToolHandlers.MergeSparseChunksAsync(sandbox, pieces[2], output,
            TestContext.Current.CancellationToken);

        Assert.True(toolResult.Success);
        Assert.Equal(raw, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
    }

    private static byte[] BuildRaw()
    {
        var raw = new byte[100_000];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i * 7 + 1);
        }
        return raw;
    }

    private static async Task<IReadOnlyList<string>> SplitAsync(TempDirectory temp, byte[] raw)
    {
        var input = temp.WriteBytes("super.raw.img", raw);
        return await SparseChunkSplitter.SplitRawAsync(input, Path.Combine(temp.Path, "super.img"), 32_768,
            TestContext.Current.CancellationToken);
    }
}