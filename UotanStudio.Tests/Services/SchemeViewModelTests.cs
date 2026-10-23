using UotanStudio.Projects;
using UotanStudio.Projects.Schemes;
using UotanStudio.Tests.TestInfrastructure;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Workflow.Execution;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Scheme operator and editor view models: generated form defaults and validation, stage ordering and status
/// projection, resume selection, and the editor persistence round trip.
/// <para>Scheme 操作员与编辑器视图模型：生成的表单默认值与校验、阶段顺序与状态投影、续跑选择，以及编辑器持久化往返。</para>
/// </summary>
public sealed class SchemeViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void SetPackage_InputDefaultsAndRequiredValidation_AreProjected()
    {
        var viewModel = CreateOperator((_, _, _) =>
            Task.FromResult(new SchemeRunResult { Status = ExecutionStatus.Succeeded }));
        viewModel.SetPackage(new SchemePackage
        {
            Id = "inputs",
            Name = "Inputs",
            Inputs =
            [
                new SchemeInputDefinition { Id = "mode", Name = "Mode", Type = SchemeInputType.Boolean, Default = "true" },
                new SchemeInputDefinition { Id = "image", Name = "Image", Type = SchemeInputType.Path, Required = true },
                new SchemeInputDefinition { Id = "count", Name = "Count", Type = SchemeInputType.Number, Default = "2" },
                new SchemeInputDefinition
                {
                    Id = "profile",
                    Name = "Profile",
                    Type = SchemeInputType.Choice,
                    Choices = ["fast", "safe"]
                }
            ],
            Stages = [Stage("prepare", "n1")]
        });

        Assert.Equal("true", viewModel.Inputs[0].EffectiveValue);
        Assert.True(viewModel.Inputs[0].BooleanValue);
        Assert.Equal("2", viewModel.Inputs[2].GetRunValue());
        Assert.True(viewModel.Inputs[1].IsMissingRequiredValue);
        Assert.True(viewModel.Inputs[1].HasError);
        Assert.Contains("image", viewModel.Inputs[1].ValidationMessage, StringComparison.Ordinal);

        viewModel.Inputs[1].ApplyPath("images/boot.img");

        Assert.False(viewModel.Inputs[1].HasError);
        Assert.Equal("images/boot.img", viewModel.Inputs[1].GetRunValue());
    }

    [Fact]
    public async Task RunAsync_StageOrderAndStatuses_FollowTheDeclaredOrder()
    {
        var result = new SchemeRunResult
        {
            Status = ExecutionStatus.Failed,
            FailedStageId = "apply",
            ErrorMessage = "expected failure",
            Stages =
            [
                new SchemeStageResult
                {
                    StageId = "prepare",
                    Status = SchemeStageStatus.Executed,
                    ExecutedNodes = 2,
                    ArtifactPaths = ["out/system.img"]
                },
                new SchemeStageResult
                {
                    StageId = "apply",
                    Status = SchemeStageStatus.Failed,
                    ErrorMessage = "expected failure"
                },
                new SchemeStageResult { StageId = "verify", Status = SchemeStageStatus.NotRun }
            ]
        };
        var viewModel = CreateOperator((_, _, _) => Task.FromResult(result));
        viewModel.SetPackage(new SchemePackage
        {
            Id = "stages",
            Name = "Stages",
            Stages = [Stage("prepare", "n1"), Stage("apply", "n2"), Stage("verify", "n3")]
        });

        var started = await viewModel.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(started);
        Assert.Equal(["prepare", "apply", "verify"], viewModel.Stages.Select(stage => stage.StageId));
        Assert.Equal(SchemeStageStatus.Executed, viewModel.Stages[0].Status);
        Assert.Equal("Node_Status_Succeeded", viewModel.Stages[0].StatusKey);
        Assert.Equal(2, viewModel.Stages[0].ExecutedNodes);
        Assert.Equal(SchemeStageStatus.Failed, viewModel.Stages[1].Status);
        Assert.Equal("Node_Status_Failed", viewModel.Stages[1].StatusKey);
        Assert.True(viewModel.Stages[1].HasError);
        Assert.Equal(SchemeStageStatus.NotRun, viewModel.Stages[2].Status);
        Assert.Equal("Node_Status_Idle", viewModel.Stages[2].StatusKey);
        Assert.Equal("apply", viewModel.FailedStageId);
        Assert.True(viewModel.HasFailedStage);
        Assert.Equal(ExecutionStatus.Failed, viewModel.RunStatus!.Value);
        Assert.Equal(1d, viewModel.Progress);
    }

    [Fact]
    public async Task ResumeOptions_RunAllThenStages_ForwardTheSelectedStage()
    {
        SchemeRunOptions? captured = null;
        var viewModel = CreateOperator((_, options, _) =>
        {
            captured = options;
            return Task.FromResult(new SchemeRunResult
            {
                Status = ExecutionStatus.Succeeded,
                Stages =
                [
                    new SchemeStageResult { StageId = "prepare", Status = SchemeStageStatus.Reused },
                    new SchemeStageResult { StageId = "apply", Status = SchemeStageStatus.Executed }
                ]
            });
        });
        viewModel.SetPackage(new SchemePackage
        {
            Id = "resume",
            Name = "Resume",
            Inputs = [new SchemeInputDefinition { Id = "mode", Name = "Mode", Default = "safe" }],
            Stages = [Stage("prepare", "n1"), Stage("apply", "n2")]
        });

        Assert.Null(viewModel.ResumeOptions[0].StageId);
        Assert.True(viewModel.ResumeOptions[0].IsRunAll);
        Assert.Equal(["prepare", "apply"],
            viewModel.ResumeOptions.Skip(1).Select(option => option.StageId ?? string.Empty));

        viewModel.SelectedResumeOption = viewModel.ResumeOptions[2];
        var started = await viewModel.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(started);
        Assert.NotNull(captured);
        Assert.Equal("apply", captured!.ResumeFromStageId);
        Assert.Equal("safe", captured.Inputs["mode"]);
        Assert.Equal("apply", viewModel.ResumeFromStageId);
        Assert.Equal(SchemeStageStatus.Reused, viewModel.Stages[0].Status);
    }

    [Fact]
    public async Task EditorSaveThenLoad_RoundTripsMetadataInputsAssetsAndStages()
    {
        var editor = CreateEditor();
        editor.Name = "Round trip";
        editor.Version = "3.2.1";
        editor.Description = "description";
        editor.Author = "author";

        var input = editor.AddInput();
        input.Id = "image";
        input.Name = "Image";
        input.Type = SchemeInputType.Path;
        input.Required = true;
        input.Sensitive = true;
        input.DefaultValue = "images/a.img";

        var choice = editor.AddInput();
        choice.Id = "mode";
        choice.Name = "Mode";
        choice.Type = SchemeInputType.Choice;
        choice.ChoicesText = "fast, safe";

        var asset = editor.AddAsset();
        asset.Id = "asset";
        asset.Name = "Asset";
        asset.RelativePath = "assets\\a.bin";
        asset.Sha256 = new string('a', 64);
        asset.LengthText = "128";

        var stage = editor.AddStage();
        stage.Id = "prepare";
        stage.Name = "Prepare";
        stage.Kind = SchemeStageKind.Patch;
        stage.FailurePolicy = SchemeStageFailurePolicy.Continue;
        stage.Embed(new WorkflowSnapshot
        {
            Nodes = [new NodeDto { InstanceId = "n1", TypeKey = "Test.Scheme.Echo" }]
        }, "workflow-a");

        var path = Path.Combine(_temp.Path, "roundtrip.uotanscheme");
        var saved = await editor.SaveAsync(path, TestContext.Current.CancellationToken);

        Assert.True(saved);
        Assert.False(editor.HasValidationMessages);
        Assert.True(File.Exists(path));

        var loaded = CreateEditor();
        var opened = await loaded.LoadAsync(path, TestContext.Current.CancellationToken);

        Assert.True(opened);
        Assert.Equal("Round trip", loaded.Name);
        Assert.Equal("3.2.1", loaded.Version);
        Assert.Equal("description", loaded.Description);
        Assert.Equal("author", loaded.Author);
        Assert.Equal(2, loaded.Inputs.Count);
        Assert.Equal(SchemeInputType.Path, loaded.Inputs[0].Type);
        Assert.True(loaded.Inputs[0].Required);
        Assert.True(loaded.Inputs[0].Sensitive);
        Assert.Equal("images/a.img", loaded.Inputs[0].DefaultValue);
        Assert.Equal(["fast", "safe"], loaded.Inputs[1].ToDefinition().Choices);
        var restoredAsset = Assert.Single(loaded.Assets).ToAsset();
        Assert.Equal("assets/a.bin", restoredAsset.RelativePath);
        Assert.Equal(128L, restoredAsset.Length);
        var restoredStage = Assert.Single(loaded.Stages).ToStage();
        Assert.Equal("prepare", restoredStage.Id);
        Assert.Equal(SchemeStageKind.Patch, restoredStage.Kind);
        Assert.Equal(SchemeStageFailurePolicy.Continue, restoredStage.FailurePolicy);
        Assert.Single(restoredStage.Graph.Nodes);
        Assert.Null(restoredStage.ProjectWorkflowName);

        var editedStage = loaded.AddStage();
        editedStage.Id = "verify";
        editedStage.Reference("workflow-a");
        Assert.True(editedStage.IsReference);
        Assert.Equal("workflow-a", editedStage.ToStage().ProjectWorkflowName);
        Assert.True(loaded.MoveStageDown(loaded.Stages[0]));
        Assert.Equal(["verify", "prepare"], loaded.Stages.Select(item => item.Id));
    }

    [Fact]
    public void CreateSingleStageFromCurrentCanvas_ReplacesStagesWithTheCapturedGraph()
    {
        var editor = CreateEditor(canvasSnapshot: selectionOnly => new WorkflowSnapshot
        {
            Nodes =
            [
                new NodeDto { InstanceId = "n1", TypeKey = "Test.Scheme.Echo" },
                new NodeDto { InstanceId = "n2", TypeKey = "Test.Scheme.Echo" }
            ]
        });
        editor.AddStage();
        editor.AddStage();

        var captured = editor.CreateSingleStageFromCurrentCanvas(selectionOnly: true);

        Assert.True(captured);
        var stage = Assert.Single(editor.Stages);
        Assert.Equal(2, stage.NodeCount);
        Assert.True(stage.HasEmbeddedGraph);
        Assert.False(editor.HasError);
    }

    private static SchemeOperatorViewModel CreateOperator(
        Func<SchemePackage, SchemeRunOptions, CancellationToken, Task<SchemeRunResult>> runner,
        Func<string, CancellationToken, Task<WorkflowSnapshot?>>? workflowLoader = null)
        => new(runner, workflowLoader ?? ((_, _) => Task.FromResult<WorkflowSnapshot?>(null)));

    private static SchemeEditorViewModel CreateEditor(
        Func<bool, WorkflowSnapshot?>? canvasSnapshot = null)
        => new(
            () => ["workflow-a"],
            (_, _) => Task.FromResult<WorkflowSnapshot?>(new WorkflowSnapshot
            {
                Nodes = [new NodeDto { InstanceId = "embedded", TypeKey = "Test.Scheme.Echo" }]
            }),
            canvasSnapshot ?? (_ => null));

    private static SchemeStage Stage(string id, string nodeId) => new()
    {
        Id = id,
        Kind = SchemeStageKind.Generic,
        Graph = new WorkflowSnapshot
        {
            Nodes = [new NodeDto { InstanceId = nodeId, TypeKey = "Test.Scheme.Echo" }]
        }
    };
}