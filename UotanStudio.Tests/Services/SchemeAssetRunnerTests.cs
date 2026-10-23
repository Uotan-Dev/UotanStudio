using System.Security.Cryptography;
using UotanStudio.Projects;
using UotanStudio.Projects.Schemes;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.Workflow.Execution;
using UotanStudio.Workflow.Nodes;
using UotanStudio.Workflow.Registry;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Scheme runner asset handling: materialization before stages, length and digest failures before execution,
/// absolute-path injection into input bindings, literal and prior-output precedence, and run-directory cleanup.
/// <para>Scheme 运行器资产处理：阶段前物化、执行前的长度与摘要失败、把绝对路径注入输入绑定、
/// 字面量与先前输出优先，以及运行目录清理。</para>
/// </summary>
[Collection("Node editor UI")]
public sealed class SchemeAssetRunnerTests : IDisposable
{
    private const string EchoTypeKey = "Test.Scheme.AssetEcho";

    private static int _runs;

    private readonly TempDirectory _temp = new();

    public SchemeAssetRunnerTests()
    {
        NodeFactoryRegistrar.RegisterAll();
        NodeTypeRegistry.Register(EchoTypeKey, () => new EchoNode());
    }

    public void Dispose()
    {
        NodeTypeRegistry.Unregister(EchoTypeKey);
        _temp.Dispose();
    }

    [Fact]
    public async Task RunAsync_AssetIdMatchingBinding_InjectsTheMaterializedAbsolutePath()
    {
        var token = TestContext.Current.CancellationToken;
        var content = "asset-bytes"u8.ToArray();
        var asset = WriteAsset("payload.bin", content, out var assetDirectory);
        var node = new EchoNode();
        var stage = Stage("apply", node, "text");
        stage.Inputs.Add(new SchemeStageInputBinding
        {
            InputId = "payload",
            NodeInstanceId = node.InstanceId,
            PortId = "in"
        });
        var package = new SchemePackage
        {
            Id = "asset-run",
            Name = "Asset run",
            Assets = [asset],
            Stages = [stage]
        };
        var runsParent = Path.Combine(_temp.Path, "runs");
        _runs = 0;

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, new SchemeRunOptions
        {
            AssetDirectory = assetDirectory,
            TemporaryDirectory = runsParent
        }, token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(1, _runs);
        var injected = Assert.IsType<string>(result.Stages[0].Outputs["text"]);
        Assert.True(Path.IsPathRooted(injected));
        Assert.EndsWith("payload.bin", injected, StringComparison.Ordinal);
        Assert.StartsWith(Path.GetFullPath(runsParent), injected, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(injected));
        Assert.True(!Directory.Exists(runsParent) || Directory.GetDirectories(runsParent).Length == 0);
    }

    [Fact]
    public async Task RunAsync_LiteralBinding_WinsOverTheAssetPath()
    {
        var token = TestContext.Current.CancellationToken;
        var asset = WriteAsset("payload.bin", "literal-loss"u8.ToArray(), out var assetDirectory);
        var node = new EchoNode();
        var stage = Stage("apply", node, "text");
        stage.Inputs.Add(new SchemeStageInputBinding
        {
            InputId = "payload",
            Literal = "literal-value",
            NodeInstanceId = node.InstanceId,
            PortId = "in"
        });
        var package = new SchemePackage
        {
            Id = "literal",
            Name = "Literal",
            Assets = [asset],
            Stages = [stage]
        };

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, new SchemeRunOptions
        {
            AssetDirectory = assetDirectory,
            TemporaryDirectory = Path.Combine(_temp.Path, "runs")
        }, token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("literal-value", result.Stages[0].Outputs["text"]);
    }

    [Fact]
    public async Task RunAsync_PreviousStageOutput_WinsOverTheAssetPath()
    {
        var token = TestContext.Current.CancellationToken;
        var asset = WriteAsset("payload.bin", "asset-loss"u8.ToArray(), out var assetDirectory);
        var prepareNode = new EchoNode();
        var prepare = Stage("prepare", prepareNode, "text");
        prepare.Inputs.Add(new SchemeStageInputBinding
        {
            InputId = "greeting",
            NodeInstanceId = prepareNode.InstanceId,
            PortId = "in"
        });
        var applyNode = new EchoNode();
        var apply = Stage("apply", applyNode, "text");
        apply.Inputs.Add(new SchemeStageInputBinding
        {
            InputId = "payload",
            SourceStageId = "prepare",
            SourceOutput = "text",
            NodeInstanceId = applyNode.InstanceId,
            PortId = "in"
        });
        var package = new SchemePackage
        {
            Id = "previous-output",
            Name = "Previous output",
            Inputs = [new SchemeInputDefinition { Id = "greeting", Name = "Greeting", Default = "hello" }],
            Assets = [asset],
            Stages = [prepare, apply]
        };

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, new SchemeRunOptions
        {
            AssetDirectory = assetDirectory,
            TemporaryDirectory = Path.Combine(_temp.Path, "runs")
        }, token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("hello", result.Stages[1].Outputs["text"]);
    }

    [Fact]
    public async Task RunAsync_MissingAsset_FailsBeforeAnyStageRuns()
    {
        var token = TestContext.Current.CancellationToken;
        var assetDirectory = Path.Combine(_temp.Path, "empty-assets");
        Directory.CreateDirectory(assetDirectory);
        var asset = new SchemeAsset
        {
            Id = "absent",
            Name = "Absent",
            RelativePath = "absent.bin"
        };
        var package = new SchemePackage
        {
            Id = "missing-asset",
            Name = "Missing asset",
            Assets = [asset],
            Stages = [Stage("apply", new EchoNode(), "text")]
        };
        var runsParent = Path.Combine(_temp.Path, "runs");
        _runs = 0;

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, new SchemeRunOptions
        {
            AssetDirectory = assetDirectory,
            TemporaryDirectory = runsParent
        }, token);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("absent", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Empty(result.Stages);
        Assert.Equal(0, _runs);
        Assert.True(!Directory.Exists(runsParent) || Directory.GetDirectories(runsParent).Length == 0);
    }

    [Fact]
    public async Task RunAsync_DigestMismatch_FailsBeforeAnyStageRuns()
    {
        var token = TestContext.Current.CancellationToken;
        var asset = WriteAsset("payload.bin", "tampered"u8.ToArray(), out var assetDirectory);
        asset.Sha256 = new string('0', 64);
        var package = new SchemePackage
        {
            Id = "asset-hash",
            Name = "Asset hash",
            Assets = [asset],
            Stages = [Stage("apply", new EchoNode(), "text")]
        };
        _runs = 0;

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, new SchemeRunOptions
        {
            AssetDirectory = assetDirectory,
            TemporaryDirectory = Path.Combine(_temp.Path, "runs")
        }, token);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("SHA-256", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _runs);
    }

    [Fact]
    public async Task RunAsync_ExplicitAssetPaths_AreValidatedAndInjected()
    {
        var token = TestContext.Current.CancellationToken;
        var asset = WriteAsset("payload.bin", "explicit"u8.ToArray(), out var assetDirectory);
        var path = Path.Combine(assetDirectory, "payload.bin");
        var node = new EchoNode();
        var stage = Stage("apply", node, "text");
        stage.Inputs.Add(new SchemeStageInputBinding
        {
            InputId = "payload",
            NodeInstanceId = node.InstanceId,
            PortId = "in"
        });
        var package = new SchemePackage
        {
            Id = "explicit-paths",
            Name = "Explicit paths",
            Assets = [asset],
            Stages = [stage]
        };

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, new SchemeRunOptions
        {
            AssetPaths = new Dictionary<string, string>(StringComparer.Ordinal) { [asset.Id] = path }
        }, token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(Path.GetFullPath(path), result.Stages[0].Outputs["text"]);
    }

    [Fact]
    public async Task RunAsync_ExplicitPathWithWrongDigest_FailsBeforeAnyStageRuns()
    {
        var token = TestContext.Current.CancellationToken;
        var asset = WriteAsset("payload.bin", "explicit"u8.ToArray(), out var assetDirectory);
        asset.Sha256 = new string('1', 64);
        var package = new SchemePackage
        {
            Id = "explicit-bad",
            Name = "Explicit bad",
            Assets = [asset],
            Stages = [Stage("apply", new EchoNode(), "text")]
        };
        _runs = 0;

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package, new SchemeRunOptions
        {
            AssetPaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [asset.Id] = Path.Combine(assetDirectory, "payload.bin")
            }
        }, token);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(0, _runs);
    }

    [Fact]
    public async Task RunAsync_DeclaredAssetWithoutAnySource_FailsBeforeAnyStageRuns()
    {
        var token = TestContext.Current.CancellationToken;
        var package = new SchemePackage
        {
            Id = "unsourced",
            Name = "Unsourced",
            Assets = [new SchemeAsset { Id = "payload", Name = "Payload", RelativePath = "payload.bin" }],
            Stages = [Stage("apply", new EchoNode(), "text")]
        };
        _runs = 0;

        var result = await new SchemeRunner(new MemoryLog()).RunAsync(package,
            cancellationToken: token);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("not materialized", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(0, _runs);
    }

    private SchemeAsset WriteAsset(string relativePath, byte[] content, out string directory)
    {
        directory = Path.Combine(_temp.Path, "assets");
        var full = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return new SchemeAsset
        {
            Id = "payload",
            Name = "Payload",
            RelativePath = relativePath,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            Length = content.Length
        };
    }

    private static SchemeStage Stage(string id, FlowNodeViewModel node, string outputId)
    {
        var stage = new SchemeStage
        {
            Id = id,
            Kind = SchemeStageKind.Generic,
            Graph = new WorkflowSnapshot { Nodes = WorkflowPersistenceService.BuildNodeDtos([node]) }
        };
        stage.Outputs.Add(new SchemeStageOutputBinding
        {
            OutputId = outputId,
            NodeInstanceId = node.InstanceId,
            PortId = "out"
        });
        return stage;
    }

    private sealed class EchoNode : FlowNodeViewModel
    {
        public EchoNode() : base("Echo", NodeCategory.Data)
        {
            TypeKey = EchoTypeKey;
            Input.Add(new NodeConnectorViewModel
            {
                Title = "In",
                PortId = "in",
                PortType = NodePortType.Any,
                MaxConnections = 1
            });
            Output.Add(new NodeConnectorViewModel { Title = "Out", PortId = "out", PortType = NodePortType.Any });
        }

        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            _runs++;
            WriteOutput("Out", ReadInput("In"));
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryLog : IUnifiedLog
    {
        public void Write(LogLevel level, string source, string message) { }

        public void Write(LogLevel level, string source, string format, params object[] args) { }

        public void SetMinimumLevel(LogLevel level) { }
    }
}