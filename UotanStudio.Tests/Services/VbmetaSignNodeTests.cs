using System.Security.Cryptography;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Behaviour of the AVB vbmeta signing node, including input rejection and a real RSA signing round trip.<para>AVB vbmeta 签名节点的行为，包括输入拒绝与真实 RSA 签名往返。</para></summary>
public sealed class VbmetaSignNodeTests
{
    [Fact]
    public void VbmetaSign_Descriptor_DeclaresPublishingSigner()
    {
        var descriptor = NodeFactoryRegistrar.Descriptors.Single(item => item.TypeKey == "Validation.Vbmeta.Sign");

        Assert.True(descriptor.Capabilities.HasFlag(NodeCapability.ProducesArtifact));
        Assert.True(descriptor.Capabilities.HasFlag(NodeCapability.PublishesFile));
        Assert.False(descriptor.Capabilities.HasFlag(NodeCapability.MutatesDevice));
    }

    [Fact]
    public void VbmetaSign_Descriptor_ExposesFlowPortsForOrdering()
    {
        var node = Factory();

        Assert.Contains(node.Input, port => port.PortType == NodePortType.Flow);
        Assert.Contains(node.Output, port => port.PortType == NodePortType.Flow);
    }

    [Fact]
    public async Task VbmetaSign_MissingInputs_FailWithoutWritingOutput()
    {
        var node = Factory();
        var output = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.img");
        Seed(node, "Output Path", output);

        var result = await RunAsync(node);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task VbmetaSign_UnloadableKey_FailsBeforeSigning()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var vbmeta = Path.Combine(directory, "vbmeta.img");
        var key = Path.Combine(directory, "bad.pem");
        var output = Path.Combine(directory, "signed.img");
        await File.WriteAllBytesAsync(vbmeta, "AVB0invalid"u8.ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(key, "not a key", TestContext.Current.CancellationToken);
        var node = Factory();
        try
        {
            Seed(node, "Vbmeta", vbmeta);
            Seed(node, "Private Key", key);
            Seed(node, "Output Path", output);

            var result = await RunAsync(node);

            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VbmetaSign_NonAvbImage_FailsWithSignerMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var vbmeta = Path.Combine(directory, "vbmeta.img");
        var keyPath = Path.Combine(directory, "key.pem");
        var output = Path.Combine(directory, "signed.img");
        await File.WriteAllBytesAsync(vbmeta, new byte[64], TestContext.Current.CancellationToken);
        using (var rsa = RSA.Create(2048))
        {
            await File.WriteAllTextAsync(keyPath, rsa.ExportRSAPrivateKeyPem(), TestContext.Current.CancellationToken);
        }
        var node = Factory();
        try
        {
            Seed(node, "Vbmeta", vbmeta);
            Seed(node, "Private Key", keyPath);
            Seed(node, "Output Path", output);

            var result = await RunAsync(node);

            Assert.Equal(ExecutionStatus.Failed, result.Status);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VbmetaSign_NodeRepository_RegistersExactlyOneSignKey()
    {
        var matches = NodeFactoryRegistrar.Descriptors
            .Where(item => item.TypeKey.StartsWith("Validation.", StringComparison.Ordinal))
            .Select(item => item.TypeKey)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Validation.Data.Hash", "Validation.Signature.Verify", "Validation.Vbmeta.Sign"], matches);
    }

    private static FlowNodeViewModel Factory()
        => NodeFactoryRegistrar.Descriptors.Single(item => item.TypeKey == "Validation.Vbmeta.Sign").Factory();

    private static void Seed(FlowNodeViewModel node, string inputPort, object? value)
        => node.Input.Single(port => port.Title == inputPort).InitialValue = value;

    private static Task<ExecutionResult> RunAsync(FlowNodeViewModel node)
        => new NodeExecutionEngine(new TestLog()).ExecuteAsync([node], [], TestContext.Current.CancellationToken);

    private sealed class TestLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }

        public void Write(LogLevel level, string source, string format, params object[] args) { }

        public void SetMinimumLevel(LogLevel level) { }
    }
}