using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using UotanStudio.Services.Delivery;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class DeliveryProjectSettingsTests(NodeEditorUiFixture fixture) : IDisposable
{
    private const string SuperSize = "8589934592";
    private const string PayloadSize = "1073741824";

    public void Dispose() => ProjectContextService.Instance.CloseProject();

    [Fact]
    public async Task SuperSource_TickUseProjectSettings_BackfillsEveryDisabledSuperField()
    {
        using var temp = new TempDirectory();
        OpenProject(temp.Path, new DeliverySourceContainerSettings
        {
            SourceName = "super.img",
            Kind = "super",
            DeviceName = "super",
            Storage = "RAW",
            DeviceType = "VAB",
            Size = ulong.Parse(SuperSize, CultureInfo.InvariantCulture),
            BlockSize = 4096,
            MetadataSlots = 3,
            MetadataMaxSize = 65536,
            GroupNames = ["qti_dynamic_partitions_a"]
        });
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new DeliveryView();
                view.FindName<CheckBox>("PackSuperBox").IsChecked = true;
                Assert.True(view.FindName<StackPanel>("SuperSizeField").IsEnabled);
                view.FindName<CheckBox>("UseProjectSuperSettingsBox").IsChecked = true;

                Assert.True(view.FindName<CheckBox>("UseProjectSuperSettingsBox").IsChecked);
                Assert.False(view.FindName<StackPanel>("SuperSizeField").IsEnabled);
                Assert.False(view.FindName<StackPanel>("SuperMetadataSizeField").IsEnabled);
                Assert.False(view.FindName<StackPanel>("SuperMetadataSlotsField").IsEnabled);
                Assert.False(view.FindName<StackPanel>("SuperBlockSizeField").IsEnabled);
                Assert.False(view.FindName<StackPanel>("SuperDeviceNameField").IsEnabled);
                Assert.False(view.FindName<StackPanel>("SuperGroupNameField").IsEnabled);
                Assert.False(view.FindName<StackPanel>("SuperDeviceTypeOptions").IsEnabled);
                Assert.Equal(SuperSize, view.FindName<TextBox>("SuperSizeBox").Text);
                Assert.Equal("65536", view.FindName<TextBox>("SuperMetadataSizeBox").Text);
                Assert.Equal("3", view.FindName<TextBox>("SuperMetadataSlotsBox").Text);
                Assert.Equal("4096", view.FindName<TextBox>("SuperBlockSizeBox").Text);
                Assert.Equal("super", view.FindName<TextBox>("SuperDeviceNameBox").Text);
                Assert.Equal("qti_dynamic_partitions_a", view.FindName<TextBox>("SuperGroupNameBox").Text);
                Assert.True(view.FindName<RadioButton>("SuperVabBox").IsChecked);
                Assert.True(view.FindName<CheckBox>("SuperZstdBox").IsEnabled);
                Assert.True(view.FindName<TextBox>("PayloadMinorVersionBox").IsEnabled);
                var plan = view.CaptureDeliveryPlan();
                Assert.Equal(ulong.Parse(SuperSize, CultureInfo.InvariantCulture), plan.Super.DeviceSize);
                Assert.Equal("qti_dynamic_partitions_a", plan.Super.Group);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PayloadSource_TickUseProjectSettings_ReleasesSuperGeometryAndKeepsGroupName()
    {
        using var temp = new TempDirectory();
        OpenProject(temp.Path, new DeliverySourceContainerSettings
        {
            SourceName = "payload.bin",
            Kind = "payload",
            DeviceType = "AB",
            BlockSize = 4096,
            MinorVersion = 0,
            MaxTimestamp = 1700000000,
            SecurityPatchLevel = "2024-01-05",
            BuildFingerprint = "Brand/Device/Device:14/UP1A/123:user/release-keys",
            Incremental = "123",
            SdkLevel = 34
        });
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new DeliveryView();
                view.FindName<CheckBox>("PackPayloadBox").IsChecked = true;
                var groupName = view.FindName<TextBox>("SuperGroupNameBox").Text;
                view.FindName<CheckBox>("UseProjectSuperSettingsBox").IsChecked = true;

                Assert.True(view.FindName<CheckBox>("UseProjectSuperSettingsBox").IsChecked);
                Assert.True(view.FindName<StackPanel>("SuperSizeField").IsEnabled);
                Assert.Equal(groupName, view.FindName<TextBox>("SuperGroupNameBox").Text);
                Assert.True(view.FindName<StackPanel>("SuperGroupNameField").IsEnabled);
                Assert.False(view.FindName<StackPanel>("SuperBlockSizeField").IsEnabled);
                Assert.Equal("4096", view.FindName<TextBox>("SuperBlockSizeBox").Text);
                Assert.True(view.FindName<TextBox>("PayloadVersionBox").IsEnabled);
                Assert.False(view.FindName<TextBox>("PayloadMinorVersionBox").IsEnabled);
                Assert.False(view.FindName<TextBox>("PayloadTimestampBox").IsEnabled);
                Assert.False(view.FindName<TextBox>("PayloadSecurityPatchBox").IsEnabled);
                Assert.False(view.FindName<TextBox>("PayloadBuildFingerprintBox").IsEnabled);
                Assert.False(view.FindName<TextBox>("PayloadIncrementalBox").IsEnabled);
                Assert.False(view.FindName<TextBox>("PayloadSdkLevelBox").IsEnabled);
                Assert.Equal("0", view.FindName<TextBox>("PayloadMinorVersionBox").Text);
                Assert.Equal("1700000000", view.FindName<TextBox>("PayloadTimestampBox").Text);
                Assert.Equal("2024-01-05", view.FindName<TextBox>("PayloadSecurityPatchBox").Text);
                Assert.Equal("123", view.FindName<TextBox>("PayloadIncrementalBox").Text);
                Assert.Equal("34", view.FindName<TextBox>("PayloadSdkLevelBox").Text);
                Assert.True(view.FindName<RadioButton>("SuperAbBox").IsChecked);

                view.FindName<TextBox>("SuperSizeBox").Text = PayloadSize;
                var plan = view.CaptureDeliveryPlan();
                Assert.Equal(ulong.Parse(PayloadSize, CultureInfo.InvariantCulture), plan.Super.DeviceSize);
                Assert.Equal(1700000000L, plan.Payload.MaxTimestamp);
                Assert.Equal((uint)4096, plan.Payload.BlockSize);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MissingSource_TickUseProjectSettings_ClearsTickAndKeepsFieldsEditable()
    {
        using var temp = new TempDirectory();
        OpenProject(temp.Path, null);
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new DeliveryView();
                view.FindName<CheckBox>("PackSuperBox").IsChecked = true;
                view.FindName<CheckBox>("UseProjectSuperSettingsBox").IsChecked = true;

                Assert.Null(view.FindName<ComboBox>("SuperSettingsProjectBox").SelectedItem);
                Assert.Equal(UotanStudio.Services.LanguageService.Instance.GetString("Delivery_NoProjectSourceSettings"),
                    view.FindName<UotanStudio.Views.Controls.DeliveryCompositionView>("Composition").Model.Status);
                Assert.False(view.FindName<CheckBox>("UseProjectSuperSettingsBox").IsChecked);
                Assert.True(view.FindName<StackPanel>("SuperSizeField").IsEnabled);
                Assert.True(view.FindName<StackPanel>("SuperBlockSizeField").IsEnabled);
                Assert.True(view.FindName<StackPanel>("SuperDeviceTypeOptions").IsEnabled);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SourceWithoutDeviceName_KeepsDeviceNameFieldEditable()
    {
        using var temp = new TempDirectory();
        OpenProject(temp.Path, new DeliverySourceContainerSettings
        {
            SourceName = "super.img",
            Kind = "super",
            DeviceName = "",
            DeviceType = "Unknown",
            Size = 0,
            BlockSize = 4096,
            MetadataSlots = 3,
            MetadataMaxSize = 65536,
            GroupNames = []
        });
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new DeliveryView();
                view.FindName<TextBox>("SuperSizeBox").Text = SuperSize;
                view.FindName<CheckBox>("PackSuperBox").IsChecked = true;
                view.FindName<CheckBox>("UseProjectSuperSettingsBox").IsChecked = true;

                Assert.True(view.FindName<CheckBox>("UseProjectSuperSettingsBox").IsChecked);
                Assert.True(view.FindName<StackPanel>("SuperDeviceNameField").IsEnabled);
                Assert.True(view.FindName<StackPanel>("SuperSizeField").IsEnabled);
                Assert.Equal(SuperSize, view.FindName<TextBox>("SuperSizeBox").Text);
                Assert.True(view.FindName<StackPanel>("SuperGroupNameField").IsEnabled);
                Assert.True(view.FindName<StackPanel>("SuperDeviceTypeOptions").IsEnabled);
                Assert.False(view.FindName<StackPanel>("SuperMetadataSizeField").IsEnabled);
                Assert.Equal("65536", view.FindName<TextBox>("SuperMetadataSizeBox").Text);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UntouchedDeliveryPanel_CapturesPlanWithRecordDefaults()
    {
        using var temp = new TempDirectory();
        OpenProject(temp.Path, null);
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(() =>
            {
                var view = new DeliveryView();
                var plan = view.CaptureDeliveryPlan();
                Assert.Equal(0UL, plan.Super.DeviceSize);
                Assert.Equal((uint)65536, plan.Super.MetadataSize);
                Assert.Equal((uint)3, plan.Super.Slots);
                Assert.Equal((uint)4096, plan.Super.BlockSize);
                Assert.Equal((uint)2, plan.Payload.Version);
                var captured = DeliveryPlanStore.CaptureElement(temp.Path, plan);
                Assert.Equal(JsonValueKind.Object, captured.ValueKind);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    private static void OpenProject(string root, DeliverySourceContainerSettings? settings)
    {
        var subRoot = Path.Combine(root, "sample");
        var metadataRoot = Path.Combine(subRoot, ".uotan");
        Directory.CreateDirectory(metadataRoot);
        File.WriteAllText(Path.Combine(subRoot, "sample.uotanfw"), "{}");
        if (settings is not null)
            File.WriteAllText(SourceContainerMetadataService.GetPath(subRoot, settings.Kind),
                JsonSerializer.Serialize(settings, SourceContainerJsonContext.Default.DeliverySourceContainerSettings));
        ProjectContextService.Instance.OpenSolution(Path.Combine(root, "workspace.uotan"));
    }
}

file static class DeliveryViewNamedControlExtensions
{
    public static T FindName<T>(this Control view, string name) where T : Control =>
        view.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing {typeof(T).Name} '{name}'.");
}