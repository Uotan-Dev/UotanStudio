using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class UsbTreeWindowTests(NodeEditorUiFixture fixture)
{
    private const string HostControllerInstanceId = @"PCI\VEN_8086&DEV_9D2D\3&11583659&0&A0";
    private const string RootHubInstanceId = @"USB\ROOT_HUB30\4&2B8C7D1&0&0";
    private const string AndroidDeviceInstanceId = @"USB\VID_18D1&PID_4EE7\0123456789ABCDEF";
    private const string AdbFunctionInstanceId = @"USB\VID_18D1&PID_4EE7&MI_01\6&1A2B3C4D&0&0001";
    private const string WebcamInstanceId = @"USB\VID_0C45&PID_6362\1";

    [Fact]
    public async Task Tree_LoadedTopology_RealizesContainersAndShowsDriverInf()
    {
        var token = TestContext.Current.CancellationToken;
        await DispatchAsync(async () =>
        {
            using var model = CreateModel(Topology());
            var window = new UsbTreeWindow(model);
            try
            {
                window.Show();
                await WaitForAsync(() => model.Roots.Count > 0 && !model.IsBusy, token);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var tree = window.FindControl<TreeView>("UsbTopologyTreeView")!;
                Assert.NotNull(tree.ContainerFromItem(model.Roots[0]));
                Assert.NotEmpty(window.GetVisualDescendants().OfType<TreeViewItem>());
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.IsEffectivelyVisible && text.Text?.Contains("oem42.inf") == true);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.IsEffectivelyVisible && text.Text == "adb");
                Assert.Contains(LanguageService.Instance.GetString("UsbTree_NodeCount", 5), model.StatusText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task OnlyAndroid_Enabled_HidesNonAndroidDeviceAndKeepsAncestors()
    {
        await DispatchAsync(async () =>
        {
            using var model = CreateModel(Topology());
            await model.RefreshAsync();
            Assert.Equal(HostControllerInstanceId, model.Roots[0].Node.InstanceId);
            Assert.Equal(4, InstanceIds(model.Roots[0]).Count);
            Assert.Contains(WebcamInstanceId, InstanceIds(model.Roots[0]));
            Assert.Contains(AndroidDeviceInstanceId, InstanceIds(model.Roots[0]));
            model.OnlyAndroid = true;
            Assert.Single(model.Roots);
            var kept = InstanceIds(model.Roots[0]);
            Assert.DoesNotContain(WebcamInstanceId, kept);
            Assert.Contains(RootHubInstanceId, kept);
            Assert.Contains(AndroidDeviceInstanceId, kept);
            Assert.Contains(AdbFunctionInstanceId, kept);
            Assert.Contains(LanguageService.Instance.GetString("UsbTree_NodeCount", 4), model.StatusText);
            model.OnlyAndroid = false;
            Assert.Contains(WebcamInstanceId, InstanceIds(model.Roots[0]));
        });
    }

    [Fact]
    public async Task DeviceNode_WithAdbFunction_AggregatesDescendantProtocolBadge()
    {
        await DispatchAsync(async () =>
        {
            using var model = CreateModel(Topology());
            await model.RefreshAsync();
            var device = Descendants(model.Roots[0])
                .Single(node => node.Node.InstanceId == AndroidDeviceInstanceId);
            Assert.Equal("adb", device.ProtocolText);
            Assert.True(device.HasProtocol);
            Assert.Equal("18D1:4EE7", device.IdentityText);
            Assert.Equal("Google", device.VendorText);
            var function = Assert.Single(device.Children);
            Assert.Equal("MI_01 ff/42/01", function.InterfaceText);
            Assert.False(function.HasAdbWarning);
        });
    }

    [Fact]
    public async Task BackendWithoutDriverInfo_RendersNotProvidedAndSuppressesAdbWarning()
    {
        var capabilities = new UsbTopologyCapabilities
        {
            Backend = UsbTopologyBackendKind.LinuxSysfs,
            SupportsDriverInfo = false,
            SupportsInterfaceNodes = true,
            SupportsHostControllerNodes = true,
            SupportsDeviceInterfaceGuids = false,
            SupportsProblemCodes = false
        };
        await DispatchAsync(async () =>
        {
            using var model = CreateModel(Topology(capabilities));
            await model.RefreshAsync();
            var notProvided = LanguageService.Instance.GetString("UsbTree_NotProvided");
            var nodes = Descendants(model.Roots[0]);
            var device = nodes.Single(node => node.Node.InstanceId == AndroidDeviceInstanceId);
            Assert.Equal(notProvided, device.DriverText);
            Assert.Equal(notProvided, device.DriverInfText);
            Assert.Equal(notProvided, device.ClassNameText);
            Assert.Equal(notProvided, device.ProblemCodeText);
            Assert.Equal(notProvided, model.Roots[0].DeviceInterfaceGuidsText);
            Assert.Equal(notProvided, model.Roots[0].DiagnosticsText);
            Assert.DoesNotContain(nodes, node => node.HasAdbWarning);
        });
    }

    [Fact]
    public async Task AvailableBackends_Empty_ShowsUnsupportedStatusWithoutTree()
    {
        var token = TestContext.Current.CancellationToken;
        await DispatchAsync(async () =>
        {
            using var model = CreateModel(Topology(), []);
            var window = new UsbTreeWindow(model);
            try
            {
                window.Show();
                await WaitForAsync(() => !model.IsBusy && model.Roots.Count == 0, token);
                window.UpdateLayout();
                var expected = LanguageService.Instance.GetString("UsbTree_NoBackend");
                Assert.False(model.IsTopologySupported);
                Assert.Equal(expected, model.StatusText);
                Assert.Equal(expected, model.UnsupportedMessage);
                Assert.Empty(model.Roots);
                Assert.Empty(window.GetVisualDescendants().OfType<TreeViewItem>());
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.IsEffectivelyVisible && text.Text == expected);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Dispose_DeviceChangeNotificationAfterDispose_SkipsFurtherTopologyReads()
    {
        var token = TestContext.Current.CancellationToken;
        var reads = 0;
        await DispatchAsync(async () =>
        {
            var model = new UsbTreeViewModel(monitorDeviceChanges: false,
                topologySource: _ =>
                {
                    Interlocked.Increment(ref reads);
                    return Topology();
                },
                backends: [UsbTopologyBackendKind.WindowsPnp],
                capabilitiesSource: _ => DriverCapable());
            await model.RefreshAsync();
            Assert.Equal(1, Volatile.Read(ref reads));
            model.NotifyDeviceChanged();
            await WaitForAsync(() => Volatile.Read(ref reads) > 1, token);
            Assert.Equal(2, Volatile.Read(ref reads));
            model.Dispose();
            model.NotifyDeviceChanged();
            await Task.Delay(600, token);
            await model.RefreshAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, Volatile.Read(ref reads));
        });
    }

    [Fact]
    public async Task BackendSelector_KeepsAutomaticEntryAndDescribesBackendCapabilities()
    {
        await DispatchAsync(async () =>
        {
            using var model = CreateModel(Topology());
            Assert.Equal(2, model.Backends.Count);
            Assert.Null(model.Backends[0].Backend);
            Assert.Equal(LanguageService.Instance.GetString("UsbTree_BackendAuto"), model.Backends[0].Display);
            Assert.Equal(UsbTopologyBackendKind.WindowsPnp, model.Backends[1].Backend);
            Assert.Contains(LanguageService.Instance.GetString("UsbTree_CapDriver_Yes"),
                model.Backends[1].Description);
            Assert.Same(model.Backends[0], model.SelectedBackend);
        });
    }

    private static UsbTreeViewModel CreateModel(UsbTopologyResult result,
        IReadOnlyList<UsbTopologyBackendKind>? backends = null)
        => new(monitorDeviceChanges: false,
            topologySource: _ => result,
            backends: backends ?? [UsbTopologyBackendKind.WindowsPnp],
            capabilitiesSource: _ => DriverCapable());

    private static UsbTopologyCapabilities DriverCapable() => new()
    {
        Backend = UsbTopologyBackendKind.WindowsPnp,
        SupportsDriverInfo = true,
        SupportsInterfaceNodes = true,
        SupportsHostControllerNodes = true,
        SupportsDeviceInterfaceGuids = true,
        SupportsProblemCodes = true
    };

    private static UsbTopologyResult Topology(UsbTopologyCapabilities? capabilities = null)
    {
        var nodes = new List<UsbTopologyNode>
        {
            new()
            {
                InstanceId = HostControllerInstanceId,
                Kind = UsbTopologyNodeKind.HostController,
                Description = "Intel(R) USB 3.0 eXtensible Host Controller",
                ClassName = "USB",
                DriverService = "usbxhci",
                DriverInfPath = "usbhub3.inf"
            },
            new()
            {
                InstanceId = RootHubInstanceId,
                ParentInstanceId = HostControllerInstanceId,
                Kind = UsbTopologyNodeKind.RootHub,
                Description = "USB Root Hub (USB 3.0)",
                ClassName = "USB",
                DriverService = "usbhub",
                DriverInfPath = "usbhub3.inf",
                Speed = UsbDeviceSpeed.High
            },
            new()
            {
                InstanceId = AndroidDeviceInstanceId,
                ParentInstanceId = RootHubInstanceId,
                Kind = UsbTopologyNodeKind.Device,
                Description = "Pixel 6",
                FriendlyName = "Android Device",
                ManufacturerName = "Google",
                ClassName = "AndroidUsbDeviceClass",
                ClassGuid = UsbTopologyNodes.AdbInterfaceClass,
                DriverService = "WinUSB",
                DriverInfPath = "oem42.inf",
                VendorId = 0x18D1,
                ProductId = 0x4EE7,
                Revision = "0100",
                SerialNumber = "0123456789ABCDEF",
                Speed = UsbDeviceSpeed.High,
                ContainerId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                HardwareIds = ["USB\\VID_18D1&PID_4EE7\\0123456789ABCDEF"],
                CompatibleIds = ["USB\\Class_EF&SubClass_06&Prot_01"],
                LocationPaths = ["PCIROOT(0)#USB(1)#USB(2)"],
                DeviceInterfaceGuids = [UsbTopologyNodes.AdbInterfaceClass],
                ProblemCode = 0
            },
            new()
            {
                InstanceId = AdbFunctionInstanceId,
                ParentInstanceId = AndroidDeviceInstanceId,
                Kind = UsbTopologyNodeKind.Function,
                Description = "ADB Interface",
                InterfaceNumber = 1,
                InterfaceClass = 0xFF,
                InterfaceSubClass = 0x42,
                InterfaceProtocol = 0x01,
                Protocol = "adb",
                DriverService = "WinUSB",
                DriverInfPath = "oem42.inf",
                DeviceInterfaceGuids = [UsbTopologyNodes.AdbInterfaceClass]
            },
            new()
            {
                InstanceId = WebcamInstanceId,
                ParentInstanceId = RootHubInstanceId,
                Kind = UsbTopologyNodeKind.Device,
                Description = "USB Camera",
                VendorId = 0x0C45,
                ProductId = 0x6362,
                Speed = UsbDeviceSpeed.High,
                DriverService = "usbvideo",
                DriverInfPath = "usbvideo.inf"
            }
        };

        var reported = capabilities ?? DriverCapable();
        return new UsbTopologyResult(reported, UsbTopologyTree.Build(nodes), nodes,
            TimeSpan.FromMilliseconds(12));
    }

    private static IReadOnlyList<UsbTreeNodeViewModel> Descendants(UsbTreeNodeViewModel node)
    {
        var nodes = new List<UsbTreeNodeViewModel>();
        foreach (var child in node.Children)
        {
            nodes.Add(child);
            nodes.AddRange(Descendants(child));
        }

        return nodes;
    }

    private static IReadOnlyList<string> InstanceIds(UsbTreeNodeViewModel root)
        => Descendants(root).Select(node => node.Node.InstanceId).ToArray();

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken token)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, token);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private async Task DispatchAsync(Func<Task> action)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(async () =>
            {
                var previousLanguage = LanguageService.Instance.CurrentLanguage;
                try
                {
                    LanguageService.Instance.ApplyLanguage("en-US");
                    await action();
                }
                finally
                {
                    LanguageService.Instance.ApplyLanguage(previousLanguage);
                }
            }, TestContext.Current.CancellationToken);
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }
}