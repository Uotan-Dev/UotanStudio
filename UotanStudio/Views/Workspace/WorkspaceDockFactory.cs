using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Devices;
using System;
using System.Collections.Generic;
using System.Linq;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;
using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Views.Workspace;

public class WorkspaceDockFactory : Factory
{
    public override IRootDock CreateLayout()
    {
        var svc = LanguageService.Instance;

        var projectSetting = ProjectSettingViewModel.EnsureInstance();
        projectSetting.Id = "ProjectSetting";
        projectSetting.Title = LanguageService.Instance.GetString("Dock_ProjectSetting");
        projectSetting.CanClose = true;

        var projectExplorer = new ProjectExplorerViewModel
        {
            Id = "ProjectExplorer",
            Title = LanguageService.Instance.GetString("Dock_ProjectExplorer"),
            CanClose = true
        };

        var imagesView = ImageViewModel.EnsureInstance();
        imagesView.Id = "ImagesView";
        imagesView.Title = LanguageService.Instance.GetString("Dock_ImagesView");
        imagesView.CanClose = true;

        var properties = new PropertiesViewModel
        {
            Id = "Properties",
            Title = LanguageService.Instance.GetString("Dock_Properties"),
            CanClose = true
        };
        var device = new WorkspaceDeviceTool { Id = "Device", Title = LanguageService.Instance.GetString("Dock_Device"), CanClose = true };

        var documentDock = new DocumentDock
        {
            Id = "DocumentsPane",
            Title = LanguageService.Instance.GetString("Dock_Documents"),
            IsCollapsable = false,
            Proportion = double.NaN,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>()
        };

        var rightToolsTop = new ToolDock
        {
            Id = "RightPaneTop",
            Title = "RightPaneTop",
            Proportion = 0.25,
            ActiveDockable = imagesView,
            VisibleDockables = CreateList<IDockable>(imagesView)
        };

        var rightTools = new ToolDock
        {
            Id = "RightPane",
            Title = "RightPane",
            Proportion = 0.45,
            ActiveDockable = properties,
            VisibleDockables = CreateList<IDockable>(properties)
        };

        var leftTools = new ToolDock
        {
            Id = "LeftPane",
            Title = "LeftPane",
            Proportion = 0.25,
            ActiveDockable = projectExplorer,
            VisibleDockables = CreateList<IDockable>(projectExplorer, projectSetting, device)
        };

        var bottomTools = new ToolDock
        {
            Id = "BottomPane",
            Title = "BottomPane",
            Proportion = 0.25,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>()
        };

        var mainLayout = new ProportionalDock
        {
            Orientation = Orientation.Horizontal,
            Proportion = 0.2,
            VisibleDockables = CreateList<IDockable>(
                leftTools,
                new ProportionalDockSplitter(),
                new ProportionalDock
                {
                    Orientation = Orientation.Vertical,
                    Proportion = 0.8,
                    VisibleDockables = CreateList<IDockable>(
                        documentDock,
                        new ProportionalDockSplitter(),
                        bottomTools
                    )
                },
                new ProportionalDockSplitter(),
                new ProportionalDock
                {
                    Orientation = Orientation.Vertical,
                    Proportion = 0.5,
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

        return rootDock;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["ProjectExplorer"] = () => LanguageService.Instance.GetString("Dock_ProjectExplorer"),
            ["Properties"] = () => LanguageService.Instance.GetString("Dock_Properties"),
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
        // 由主题自绘窗体装饰（WindowDrawnDecorations 标题栏 + 最小化/最大化/关闭按钮），
        // 与主窗口的自绘窗体样式保持一致；不要启用原生 WindowDecorations，否则会叠加一整套外框。
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
            // 忽略任何异常，确保不崩溃
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

            if (dockable.Owner is IDock dock)
            {
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
        }
        catch
        {
            // 忽略任何移除过程中的错误
        }
    }
}