using System.Text;
using UotanStudio.Services;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>End-to-end behavior of the collection and byte primitives added for batch processing.<para>为批量处理新增的集合与字节原语的端到端行为。</para></summary>
public sealed class DataPrimitiveNodeTests
{
    [Fact]
    public async Task ListFromText_NewlineSeparated_ProducesTrimmedItemsAndCount()
    {
        var node = Factory("Data.List.FromText");
        Seed(node, "Text", " boot = 64M \n dtbo=8M\n\n vendor_boot = 96M ");

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var list = Assert.IsAssignableFrom<IReadOnlyList<object?>>(Read(node, "List"));
        Assert.Equal(["boot = 64M", "dtbo=8M", "vendor_boot = 96M"], list.Cast<string>());
        Assert.Equal(3d, Read(node, "Count"));
    }

    [Fact]
    public async Task ListFromText_CustomSeparator_SplitsOnEveryMatch()
    {
        var node = Factory("Data.List.FromText");
        Seed(node, "Text", "a;b;;c");
        Seed(node, "Separator", ";");

        await RunAsync(node);

        Assert.Equal(["a", "b", "c"], ReadList(node, "List").Cast<string>());
    }

    [Fact]
    public async Task ListElementAt_NameValueItem_SplitsIntoNameAndField()
    {
        var node = Factory("Data.List.ElementAt");
        Seed(node, "List", new object[] { "system=4096", "boot=64" });
        Seed(node, "Index", 1d);

        await RunAsync(node);

        Assert.Equal("boot=64", Read(node, "Value"));
        Assert.Equal("boot", Read(node, "Name"));
        Assert.Equal("64", Read(node, "Field"));
        Assert.Equal(1d, Read(node, "Index"));
        Assert.Equal(2d, Read(node, "Count"));
    }

    [Fact]
    public async Task ListElementAt_IndexOutOfRange_FailsWithoutWritingOutputs()
    {
        var node = Factory("Data.List.ElementAt");
        Seed(node, "List", new object[] { "only" });
        Seed(node, "Index", 5d);

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Null(Read(node, "Value"));
    }

    [Fact]
    public async Task ListElementAt_EmptyList_Fails()
    {
        var node = Factory("Data.List.ElementAt");
        Seed(node, "List", Array.Empty<object>());

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task ListCount_NonListValue_ReportsZeroInsteadOfThrowing()
    {
        var node = Factory("Data.List.Count");
        Seed(node, "List", "not a list");

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(0d, Read(node, "Count"));
    }

    [Fact]
    public async Task Graph_TableTextToListToElement_SelectsRequestedPartition()
    {
        var split = Factory("Data.List.FromText");
        var element = Factory("Data.List.ElementAt");
        Seed(split, "Text", "boot=67108864\nvendor_boot=100663296");
        Seed(element, "Index", 1d);

        Connect(split, "List", element, "List");
        var result = await RunAsync(new NodeExecutionEngine(new TestLog()), [split, element]);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("vendor_boot", Read(element, "Name"));
        Assert.Equal("100663296", Read(element, "Field"));
    }

    [Fact]
    public async Task HashNode_TextInput_EncodesWithDeclaredEncoding()
    {
        var node = Factory("Validation.Data.Hash");
        Seed(node, "Text", "abc");

        var result = await RunAsync(node);

        Assert.True(result.Status == ExecutionStatus.Succeeded, result.ErrorMessage);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Read(node, "Hash"));
    }

    [Fact]
    public async Task HashNode_Utf16Encoding_HashesUtf16Bytes()
    {
        var node = Factory("Validation.Data.Hash");
        Seed(node, "Text", "abc");
        Seed(node, "Encoding", "utf16le");

        await RunAsync(node);

        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.Unicode.GetBytes("abc")));
        Assert.Equal(expected, Read(node, "Hash"));
    }

    [Fact]
    public async Task HashNode_ConnectedData_KeepsFileBytesAsAuthority()
    {
        var read = Factory("File.Binary.Read");
        var hash = Factory("Validation.Data.Hash");
        var temp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(temp, [1, 2, 3], TestContext.Current.CancellationToken);
        Seed(read, "Path", temp);
        Seed(hash, "Text", "ignored");

        Connect(read, "Data", hash, "Data");
        var result = await RunAsync(new NodeExecutionEngine(new TestLog()), [read, hash]);

        try
        {
            Assert.Equal(ExecutionStatus.Succeeded, result.Status);
            var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([1, 2, 3]));
            Assert.Equal(expected, Read(hash, "Hash"));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public async Task FileBytesWrite_BytesToPath_PublishesFileAndReportsLength()
    {
        var node = Factory("File.Bytes.Write");
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var target = Path.Combine(directory, "out.bin");
        Seed(node, "Data", new byte[] { 9, 8, 7, 6 });
        Seed(node, "Path", target);

        await RunAsync(node);

        try
        {
            Assert.True(File.Exists(target));
            Assert.Equal([9, 8, 7, 6], await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
            Assert.Equal(target, Read(node, "Path"));
            Assert.Equal(4d, Read(node, "Bytes"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FileBytesWrite_MissingData_FailsWithoutCreatingTarget()
    {
        var node = Factory("File.Bytes.Write");
        var target = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        Seed(node, "Path", target);

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task CompressDecompress_ZstdRoundTrip_RestoresOriginalBytes()
    {
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("uotan-node-primitive-", 64)));
        var compress = Factory("Data.Compression.Compress");
        var decompress = Factory("Data.Compression.Decompress");
        Seed(compress, "Data", payload);
        Seed(compress, "Algorithm", "zstd");
        Seed(decompress, "Algorithm", "zstd");

        Connect(compress, "Compressed", decompress, "Data");
        var result = await RunAsync(new NodeExecutionEngine(new TestLog()), [compress, decompress]);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(payload, Assert.IsType<byte[]>(Read(decompress, "Data")));
    }

    [Fact]
    public void ResolveCompressionFormat_Zopfli_RejectedAsCompressionTarget()
    {
        Assert.Throws<InvalidDataException>(() => ExtendedDataNodeViewModel.ResolveCompressionFormat("zopfli"));
        Assert.Equal(UotanStudio.Compress.CompressionFormat.Zstd,
            ExtendedDataNodeViewModel.ResolveCompressionFormat("zstd"));
    }

    private static FlowNodeViewModel Factory(string typeKey)
        => NodeFactoryRegistrar.Descriptors.Single(descriptor => descriptor.TypeKey == typeKey).Factory();

    /// <summary>Seeds an input through the port initial value, because the engine resets runtime data before every run.<para>通过端口初值注入输入，因为引擎在每次运行前会重置运行期数据。</para></summary>
    /// <param name="node">Target node. <para>目标节点。</para></param>
    /// <param name="inputPort">Input port title. <para>输入端口标题。</para></param>
    /// <param name="value">Seed value. <para>注入值。</para></param>
    private static void Seed(FlowNodeViewModel node, string inputPort, object? value)
        => node.Input.Single(port => port.Title == inputPort).InitialValue = value;

    private static void Connect(FlowNodeViewModel source, string sourcePort, FlowNodeViewModel target, string targetPort)
        => Edges.Add(new()
        {
            SourceInstanceId = source.InstanceId,
            SourceConnector = sourcePort,
            TargetInstanceId = target.InstanceId,
            TargetConnector = targetPort
        });

    private static object? Read(FlowNodeViewModel node, string title)
        => node.Output.Single(port => port.Title == title).Data;

    private static IReadOnlyList<object?> ReadList(FlowNodeViewModel node, string title)
        => Assert.IsAssignableFrom<IReadOnlyList<object?>>(Read(node, title));

    private static Task<ExecutionResult> RunAsync(FlowNodeViewModel node)
    {
        Edges.Clear();
        return new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [], TestContext.Current.CancellationToken);
    }

    private static Task<ExecutionResult> RunAsync(NodeExecutionEngine engine, params FlowNodeViewModel[] nodes)
    {
        var edges = Edges.ToArray();
        Edges.Clear();
        return engine.ExecuteAsync(nodes, edges, TestContext.Current.CancellationToken);
    }

    private static readonly List<NodeConnection> Edges = [];

    private sealed class TestLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }

        public void Write(LogLevel level, string source, string format, params object[] args) { }

        public void SetMinimumLevel(LogLevel level) { }
    }
}