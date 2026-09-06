using Avalonia.Controls;
using Avalonia.Media;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Material.Icons;
using Material.Icons.Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Devices;

namespace UotanStudio.Views.Workspace;

public class WorkspaceDockFactory : Factory
{
    private ProjectExplorerViewModel? _projectExplorer;
    private PropertiesViewModel? _properties;

    public override IRootDock CreateLayout()
    {
        var svc = LanguageService.Instance;

        var projectSetting = ProjectSettingViewModel.EnsureInstance();
        projectSetting.Id = "ProjectSetting";
        projectSetting.Title = LanguageService.Instance.GetString("Dock_ProjectSetting");
        projectSetting.CanClose = true;

        var projectExplorer = _projectExplorer = new ProjectExplorerViewModel
        {
            Id = "ProjectExplorer",
            Title = LanguageService.Instance.GetString("Dock_ProjectExplorer"),
            CanClose = true
        };

        var imagesView = ImageViewModel.EnsureInstance();
        imagesView.Id = "ImagesView";
        imagesView.Title = LanguageService.Instance.GetString("Dock_ImagesView");
        imagesView.CanClose = true;

        var properties = _properties = new PropertiesViewModel
        {
            Id = "Properties",
            Title = LanguageService.Instance.GetString("Dock_Properties"),
            CanClose = true
        };
        var output = new Tool { Id = "Output", Title = svc.GetString("Dock_Output") };
        var adbLog = new Tool { Id = "AdbLog", Title = svc.GetString("Dock_AdbLog") };
        var device = new WorkspaceDeviceTool { Id = "Device", Title = LanguageService.Instance.GetString("Dock_Device"), CanClose = true };

        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            Title = LanguageService.Instance.GetString("Dock_Documents"),
            IsCollapsable = false,
            Proportion = double.NaN,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>(),
            EmptyContent = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Vertical,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Spacing = 8,
                Children =
                {
                    new MaterialIcon { Kind = MaterialIconKind.FileDocumentOutline, Width = 48, Height = 48, Opacity = 0.4 },
                    new TextBlock
                    {
                        Text = svc.GetString("Dock_NoDocumentOpen"),
                        FontSize = 16,
                        Opacity = 0.6,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = svc.GetString("Dock_NoDocumentOpenHint"),
                        FontSize = 12,
                        Opacity = 0.4,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                    }
                }
            }
        };

        var rightToolsTop = new ToolDock
        {
            Id = "RightPaneTop",
            Title = "RightPaneTop",
            Proportion = double.NaN,
            ActiveDockable = imagesView,
            VisibleDockables = CreateList<IDockable>(imagesView)
        };

        var rightTools = new ToolDock
        {
            Id = "RightPane",
            Title = "RightPane",
            Proportion = 0.35,
            ActiveDockable = properties,
            VisibleDockables = CreateList<IDockable>(properties)
        };

        var leftTools = new ToolDock
        {
            Id = "LeftPane",
            Title = "LeftPane",
            Proportion = 0.15,
            ActiveDockable = projectExplorer,
            VisibleDockables = CreateList<IDockable>(projectExplorer, projectSetting)
        };

        var bottomTools = new ToolDock
        {
            Id = "BottomPane",
            Title = "BottomPane",
            Proportion = 0.25,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>(output, adbLog)
        };

        var mainLayout = new ProportionalDock
        {
            Orientation = Dock.Model.Core.Orientation.Horizontal,
            Proportion = double.NaN,
            VisibleDockables = CreateList<IDockable>(
                leftTools,
                new ProportionalDockSplitter(),
                new ProportionalDock
                {
                    Orientation = Dock.Model.Core.Orientation.Vertical,
                    Proportion = 0.7,
                    VisibleDockables = CreateList<IDockable>(
                        documentDock,
                        new ProportionalDockSplitter(),
                        bottomTools
                    )
                },
                new ProportionalDockSplitter(),
                new ProportionalDock
                {
                    Orientation = Dock.Model.Core.Orientation.Vertical,
                    Proportion = 0.15,
                    VisibleDockables = CreateList<IDockable>(
                        rightToolsTop,
                        new ProportionalDockSplitter(),
                        rightTools
                    )
                }
            )
        };

        var rootDock = CreateRootDock();
        rootDock.IsCollapsable = false;
        rootDock.ActiveDockable = mainLayout;
        rootDock.DefaultDockable = mainLayout;
        rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);

        // 把各工作区面板登记到面板服务，供「窗口」菜单显示/隐藏/聚焦。
        // Register each workspace panel with the panel service for the Window menu.
        var panels = WorkspacePanelService.Instance;
        panels.Register(WorkspacePanelService.ProjectExplorer, projectExplorer, leftTools, visible: true);
        panels.Register(WorkspacePanelService.ProjectSetting, projectSetting, leftTools, visible: true);
        panels.Register(WorkspacePanelService.Device, device, leftTools, visible: false);
        panels.Register(WorkspacePanelService.ImagesView, imagesView, rightToolsTop, visible: true);
        panels.Register(WorkspacePanelService.Properties, properties, rightTools, visible: true);

        return rootDock;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["ProjectExplorer"] = () => _projectExplorer ??= new ProjectExplorerViewModel { Id = "ProjectExplorer" },
            ["Properties"] = () => _properties ??= new PropertiesViewModel { Id = "Properties" },
            ["Device"] = () => DeviceViewModel.EnsureInstance()
        };

        // 创建启用 docking 的 HostWindow
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => CreateHostWindow(),
            ["ManagedHostWindow"] = () => CreateManagedHostWindow()
        };

        DefaultHostWindowLocator = () => CreateHostWindow();

        base.InitLayout(layout);
    }

    private static HostWindow CreateHostWindow()
    {
        var options = new Dock.Model.Core.DockManagerOptions { IsDockingEnabled = true };
        var hwnd = new HostWindow(options);
        // Floating document decorations are configured by the global HostWindow style.
        hwnd.ShowInTaskbar = true;
        hwnd.CanResize = true;
        return hwnd;
    }

    private static ManagedHostWindow CreateManagedHostWindow()
    {
        var options = new Dock.Model.Core.DockManagerOptions { IsDockingEnabled = true };
        return new ManagedHostWindow(options);
    }

    public override void CloseDockable(IDockable? dockable)
    {
        if (dockable == null)
        {
            return;
        }

        if (!dockable.CanClose)
        {
            return;
        }

        try
        {
            SafeRemoveDockable(dockable);
        }
        catch
        {
            // 关闭流程中的异常不阻断用户操作。
        }
    }

    private void SafeRemoveDockable(IDockable dockable)
    {
        try
        {
            if (dockable == null)
            {
                return;
            }

            IDock? ownerDock = null;
            if (dockable.Owner is IDock dock)
            {
                ownerDock = dock;
                if (dock.VisibleDockables != null)
                {
                    var list = dock.VisibleDockables.ToList();
                    if (list.Contains(dockable))
                    {
                        list.Remove(dockable);
                        dock.VisibleDockables = CreateList(list.ToArray());

                        if (dock.ActiveDockable == dockable)
                        {
                            dock.ActiveDockable = list.FirstOrDefault();
                        }
                    }
                }
            }

            dockable.Owner = null;

            // 用户经停靠区关闭按钮移除面板后，同步「窗口」菜单的可见性状态。
            // After a panel is removed via the dock's close button, sync the Window menu visibility.
            if (ownerDock is not null)
            {
                WorkspacePanelService.Instance.OnDockContentsChanged(ownerDock);
            }
        }
        catch
        {
            // 移除过程中的异常不阻断关闭流程。
        }

        // 文档关闭后立即释放大对象（解析模型/原始字节/树节点），避免内存占用保持不降。
        // Release large objects as soon as a document is closed so the memory footprint drops.
        if (dockable is UotanStudio.ViewModels.Compare.CompareViewModel compare)
        {
            compare.Cleanup();
        }
        else if (dockable is UotanStudio.ViewModels.Editors.DexEditorViewModel dexEditor)
        {
            try
            {
                dexEditor.Cleanup();
            }
            catch
            {
                // 清理失败不影响关闭流程
            }
        }
        else if (dockable is UotanStudio.ViewModels.Editors.FileManagerEditorViewModel fileManager)
        {
            try
            {
                fileManager.Cleanup();
            }
            catch
            {
                // 清理失败不影响关闭流程
            }
        }
    }
}
