using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Projects;

public sealed class AtomicJsonPerformanceTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(100_000)]
    public async Task WriteIfChangedAsync_UnicodeAndBom_PreservesUnchangedFile(int size)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "state.json");
        var value = new string('中', size) + "😀";
        var json = JsonSerializer.Serialize(value, AtomicTestJsonContext.Default.String);
        await File.WriteAllTextAsync(path, json, Encoding.Unicode, TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        Assert.False(await AtomicJsonFile.WriteIfChangedAsync(path, value, AtomicTestJsonContext.Default.String, TestContext.Current.CancellationToken));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(100_000)]
    public async Task WriteIfChangedAsync_ChangedSnapshot_PublishesExactUtf8(int size)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("state.json", "previous"u8.ToArray());
        var value = new string('x', size);

        Assert.True(await AtomicJsonFile.WriteIfChangedAsync(path, value, AtomicTestJsonContext.Default.String, TestContext.Current.CancellationToken));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(value, AtomicTestJsonContext.Default.String),
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteIfChangedAsync_FailureAfterSpill_PreservesTargetAndAllowsRetry(bool cancel)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("state.json", "previous"u8.ToArray());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        IEnumerable<string> Values()
        {
            yield return new string('a', 100_000);
            if (cancel)
            {
                cancellation.Cancel();
            }
            else
            {
                throw new InvalidOperationException("Injected serialization failure");
            }
            yield return new string('b', 100_000);
        }
        var error = await Record.ExceptionAsync(() => AtomicJsonFile.WriteIfChangedAsync(path, Values(),
            AtomicTestJsonContext.Default.IEnumerableString, cancellation.Token));
        Assert.NotNull(error);
        if (cancel)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(error);
        }
        Assert.Equal("previous", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
        Assert.True(await AtomicJsonFile.WriteIfChangedAsync(path, "retry", AtomicTestJsonContext.Default.String, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteIfChangedAsync_OverLimit_PreservesTarget()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("state.json", "previous"u8.ToArray());
        var values = Enumerable.Repeat(new string('x', 1024 * 1024), 33);
        await Assert.ThrowsAsync<InvalidDataException>(() => AtomicJsonFile.WriteIfChangedAsync(path, values,
            AtomicTestJsonContext.Default.IEnumerableString, TestContext.Current.CancellationToken));
        Assert.Equal("previous", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(IEnumerable<string>))]
internal partial class AtomicTestJsonContext : JsonSerializerContext;