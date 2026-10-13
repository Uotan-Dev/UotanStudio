using Avalonia.Controls;
using Avalonia.Threading;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class FlashViewEdlStageTierTests(NodeEditorUiFixture fixture)
{
    private const string FirehoseStageLine =
        "Observed stage: Firehose, with a programmer already active.";

    private const string NoStageObservedLine =
        "No EDL protocol stage has been observed yet, so only stage-independent actions are enabled.";

    private static readonly string[] ByNameStorageNames =
    [
        "EdlReadbackButton", "EdlEraseButton", "EdlFactoryResetButton", "EdlRepairGptButton",
        "EdlActiveSlotBox", "EdlSetActiveSlotButton"
    ];

    private static readonly string[] LoaderNames =
    [
        "EdlChooseProgrammerButton", "EdlAllowStreamDloadBox", "EdlChooseProgrammerDirButton"
    ];

    private static readonly string[] StageIndependentNames =
    [
        "EdlReadStorageInfoButton", "EdlResetButton", "EdlPowerOffButton", "EdlResetToEdlButton"
    ];

    private static readonly string[] MtkDownloadAgentNames = ["MtkRawReadGroup"];

    private static readonly string[] MtkRpmbNames =
    [
        "MtkRpmbModeBox", "MtkRpmbOffsetBox", "MtkRpmbLengthBox", "MtkRpmbReadButton", "MtkRpmbCapacityButton"
    ];

    private static readonly string[] ExposedNames =
    [
        "EdlRegionGroup", "EdlStreamingNandGroup", "EdlReadbackButton", "EdlEraseButton",
        "EdlFactoryResetButton", "EdlRepairGptButton", "EdlActiveSlotBox", "EdlSetActiveSlotButton",
        "EdlChooseProgrammerButton", "EdlProgrammerPickBox", "EdlAllowStreamDloadBox",
        "EdlReadStorageInfoButton", "EdlResetButton", "EdlPowerOffButton", "EdlResetToEdlButton",
        "EdlSkipResponseBox", "EdlGenerateXmlBox", "EdlGeneralizedXmlBox",
        "MtkRawReadGroup", "MtkLegacyParamsGroup",
        "MtkPreloaderPane", "MtkUsbOnlyRow",
        "EdlChooseProgrammerDirButton", "EdlProgrammerDirText"
    ];

    [Fact]
    public async Task UpdateModeVisibility_EdlFirehoseStageWithExpertSwitchOn_ExposesTheByNameStorageControls()
    {
        await WithFlashViewAsync((view, model) =>
        {
            SetExpert(view, true);
            SelectEdl(model, EdlProtocolState.Firehose);
            foreach (var name in ByNameStorageNames)
            {
                Assert.True(Find(view, name).IsVisible, name);
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task UpdateModeVisibility_EdlStageShortOfFirehose_HidesEveryByNameStorageControl()
    {
        await WithFlashViewAsync((view, model) =>
        {
            SetExpert(view, true);
            foreach (var stage in new[] { EdlProtocolState.Sahara, EdlProtocolState.Unknown })
            {
                SelectEdl(model, stage);
                foreach (var name in ByNameStorageNames)
                {
                    Assert.False(Find(view, name).IsVisible, $"{name} at {stage}");
                }
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task UpdateModeVisibility_EdlLoaderAlreadyUp_HidesTheProgrammerAndAuthenticationRow()
    {
        await WithFlashViewAsync((view, model) =>
        {
            SetExpert(view, false);
            foreach (var (stage, expected) in new[]
            {
                (EdlProtocolState.Sahara, true),
                (EdlProtocolState.Unknown, true),
                (EdlProtocolState.Firehose, false),
                (EdlProtocolState.Streaming, false)
            })
            {
                SelectEdl(model, stage);
                foreach (var name in LoaderNames)
                {
                    Assert.Equal(expected, Find(view, name).IsVisible);
                }
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EdlProgrammerPickBox_StagePastCommandMode_HidesTheCandidateList()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var picker = Find(view, "EdlProgrammerPickBox");
            view.ProgrammerCandidates = ["prog_firehose_256kb.elf", "prog_ufs_firehose_sm8250.elf"];
            foreach (var (stage, expected) in new[]
            {
                (EdlProtocolState.Sahara, true),
                (EdlProtocolState.Unknown, true),
                (EdlProtocolState.Firehose, false),
                (EdlProtocolState.Streaming, false)
            })
            {
                SelectEdl(model, stage);
                Assert.Equal(expected, picker.IsVisible);
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task UpdateModeVisibility_EdlStageUnobserved_KeepsTheStageIndependentSetAndExplainsTheStageLine()
    {
        await WithFlashViewAsync((view, model) =>
        {
            view.ObservedSaharaMode = _ => null;
            SetExpert(view, true);
            var stageText = view.FindControl<TextBlock>("EdlStageText")!;
            foreach (var stage in new[]
            {
                EdlProtocolState.Unknown, EdlProtocolState.Sahara, EdlProtocolState.Firehose,
                EdlProtocolState.Streaming
            })
            {
                SelectEdl(model, stage);
                foreach (var name in StageIndependentNames)
                {
                    var button = view.FindControl<Button>(name)!;
                    Assert.True(button.IsVisible, $"{name} at {stage}");
                    Assert.True(button.IsEnabled, $"{name} at {stage}");
                }
            }

            SelectEdl(model, EdlProtocolState.Firehose);
            Assert.Equal(FirehoseStageLine, stageText.Text);

            SelectEdl(model, EdlProtocolState.Unknown);
            Assert.Equal(NoStageObservedLine, stageText.Text);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EdlStreamingNandGroup_StreamingStageWithExpertSwitchOn_AppearsOnThatBackendOnly()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var group = Find(view, "EdlStreamingNandGroup");
            SetExpert(view, false);
            SelectEdl(model, EdlProtocolState.Streaming);
            Assert.False(group.IsVisible);

            SetExpert(view, true);
            SelectEdl(model, EdlProtocolState.Streaming);
            Assert.True(group.IsVisible);

            SelectEdl(model, EdlProtocolState.Firehose);
            Assert.False(group.IsVisible);

            SelectEdl(model, EdlProtocolState.Sahara);
            Assert.False(group.IsVisible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EdlReadbackManifestBoxes_FollowTheReadbackEntryStage()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var generate = Find(view, "EdlGenerateXmlBox");
            var generalized = Find(view, "EdlGeneralizedXmlBox");
            foreach (var (stage, expected) in new[]
            {
                (EdlProtocolState.Firehose, true),
                (EdlProtocolState.Sahara, false),
                (EdlProtocolState.Unknown, false),
                (EdlProtocolState.Streaming, false)
            })
            {
                SelectEdl(model, stage);
                Assert.Equal(expected, generate.IsVisible);
                Assert.Equal(expected, generalized.IsVisible);
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MtkDownloadAgentGroups_StageShortOfTheAgentOrExpertSwitchOff_HideEveryAgentGroup()
    {
        await WithFlashViewAsync((view, model) =>
        {
            SetExpert(view, true);
            SelectMtk(model, "da");
            foreach (var name in MtkDownloadAgentNames)
            {
                Assert.True(Find(view, name).IsVisible, name);
            }

            foreach (var stage in new[] { "brom", "preloader", "logging", string.Empty })
            {
                SelectMtk(model, stage);
                foreach (var name in MtkDownloadAgentNames)
                {
                    Assert.False(Find(view, name).IsVisible, $"{name} at {stage}");
                }
            }

            SetExpert(view, false);
            SelectMtk(model, "da");
            foreach (var name in MtkDownloadAgentNames)
            {
                Assert.False(Find(view, name).IsVisible, name);
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MtkRawReadGroup_StageShortOfTheAgent_HidesTheSectorAndAreaControls()
    {
        await WithFlashViewAsync((view, model) =>
        {
            var group = Find(view, "MtkRawReadGroup");
            SetExpert(view, true);
            SelectMtk(model, "da");
            Assert.True(group.IsVisible);

            foreach (var stage in new[] { "brom", "preloader", "logging", string.Empty })
            {
                SelectMtk(model, stage);
                Assert.False(group.IsVisible, $"MtkRawReadGroup at {stage}");
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MtkRpmbControls_EnabledOnlyOnTheBootStages()
    {
        await WithFlashViewAsync((view, model) =>
        {
            SetExpert(view, true);
            foreach (var stage in new[] { "brom", "preloader" })
            {
                SelectMtk(model, stage);
                Assert.True(Find(view, "MtkUsbOnlyRow").IsVisible, $"MtkUsbOnlyRow at {stage}");
                foreach (var name in MtkRpmbNames)
                {
                    Assert.True(Find(view, name).IsEnabled, $"{name} at {stage}");
                }
            }

            foreach (var stage in new[] { "da", "logging", string.Empty })
            {
                SelectMtk(model, stage);
                Assert.False(Find(view, "MtkUsbOnlyRow").IsVisible, $"MtkUsbOnlyRow at {stage}");
                foreach (var name in MtkRpmbNames)
                {
                    Assert.False(Find(view, name).IsEnabled, $"{name} at {stage}");
                }
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MtkRawReadGroup_ExpertSwitchOff_HidesTheSectorAndAreaControls()
    {
        await WithFlashViewAsync((view, model) =>
        {
            SetExpert(view, false);
            SelectMtk(model, "da");
            Assert.False(Find(view, "MtkRawReadGroup").IsVisible);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ExpertModeSwitch_FlippedTwiceOnTheSameStage_ReturnsEveryGroupToItsPriorVisibility()
    {
        await WithFlashViewAsync((view, model) =>
        {
            AssertFlipLeavesNoStickyExposure(view, model, DeviceProtocolKind.Edl, "edl", EdlProtocolState.Firehose);
            AssertFlipLeavesNoStickyExposure(view, model, DeviceProtocolKind.Edl, "edl", EdlProtocolState.Streaming);
            AssertFlipLeavesNoStickyExposure(view, model, DeviceProtocolKind.Mtk, "da", EdlProtocolState.Unknown);
            return Task.CompletedTask;
        });
    }

    private static void AssertFlipLeavesNoStickyExposure(
        FlashView view,
        DeviceViewModel model,
        DeviceProtocolKind protocol,
        string state,
        EdlProtocolState stage)
    {
        SetExpert(view, false);
        Select(model, protocol, state, stage);
        var closed = VisibleExposure(view);

        SetExpert(view, true);
        Select(model, protocol, state, stage);
        var widened = VisibleExposure(view);

        SetExpert(view, false);
        Select(model, protocol, state, stage);
        Assert.NotEqual(closed, widened);
        Assert.Equal(closed, VisibleExposure(view));
    }

    private static string VisibleExposure(FlashView view)
        => string.Join(", ", ExposedNames.Where(name => Find(view, name).IsVisible));

    private static Control Find(FlashView view, string name) => view.FindControl<Control>(name)!;

    private static void SetExpert(FlashView view, bool value)
        => view.FindControl<CheckBox>("FlashExpertModeBox")!.IsChecked = value;

    private static void SelectEdl(DeviceViewModel model, EdlProtocolState stage)
        => Select(model, DeviceProtocolKind.Edl, "edl", stage);

    private static void SelectMtk(DeviceViewModel model, string state)
        => Select(model, DeviceProtocolKind.Mtk, state, EdlProtocolState.Unknown);

    private static void Select(
        DeviceViewModel model,
        DeviceProtocolKind protocol,
        string state,
        EdlProtocolState stage)
    {
        model.SelectedDevice = new DeviceInfo(new DetectedDevice
        {
            Key = $"{protocol}-{state}-{stage}-{model.SelectedDevice?.Device.Key?.Length ?? 0}",
            Serial = "edl-stage-tier",
            Protocol = protocol,
            Transport = DeviceTransportKind.Usb,
            State = state,
            EdlProtocol = stage
        });
        Dispatcher.UIThread.RunJobs();
    }

    private async Task WithFlashViewAsync(Func<FlashView, DeviceViewModel, Task> body)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                LanguageService.Instance.ApplyLanguage("en-US");
                var model = DeviceViewModel.EnsureInstance();
                var view = new FlashView();
                var window = new Window { Content = view, Width = 960, Height = 800 };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    ((FlashViewModel)view.DataContext!).FlushPendingLog();
                    return body(view, model);
                }
                finally
                {
                    model.SelectedDevice = null;
                    window.Close();
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }
}