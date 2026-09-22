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
using UotanStudio.ViewModels.Shell;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Devices;

namespace UotanStudio.Views.Workspace;

public class WorkspaceDockFactory : Factory
{
    private readonly AiSettingsViewModel _aiSettings;
    private ProjectExplorerViewModel? _projectExplorer;
    internal ProjectExplorerViewModel? ProjectExplorer => _projectExplorer;
    private PropertiesViewModel? _properties;
    private TerminalDockService? _terminals;
    private readonly HashSet<IDockable> _closingDocuments = [];

    public WorkspaceDockFactory() : this(new AiSettingsViewModel()) { }

    public WorkspaceDockFactory(AiSettingsViewModel aiSettings) => _aiSettings = aiSettings;

    public override IRootDock CreateLayout()
    {
        var svc = LanguageService.Instance;

        var projectSetting = ProjectSettingViewModel.EnsureInstance();
        projectSetting.Id = "ProjectSetting";
        projectSetting.Title = LanguageService.Instance.GetString("Dock_ProjectSetting");
        projectSetting.CanClose = true;

        var pluginTool = new PluginToolViewModel
        {
            Id = WorkspacePanelService.PluginTool,
            Title = LanguageService.Instance.GetString("Dock_Plugins"),
            CanClose = true
        };

        _projectExplorer?.Dispose();
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

        var aiAssistant = AppFeatures.AiAssistantEnabled ? new AiAssistantViewModel(_aiSettings) : null;

        var properties = _properties = new PropertiesViewModel
        {
            Id = "Properties",
            Title = LanguageService.Instance.GetString("Dock_Properties"),
            CanClose = true
        };
        var output = new WorkspaceLogViewModel
        {
            Id = "Output",
            Title = svc.GetString("Dock_Output"),
            CanClose = true
        };
        var adbLog = new AdbLogViewModel
        {
            Id = "AdbLog",
            Title = svc.GetString("Dock_AdbLog"),
            CanClose = true
        };
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
        if (aiAssistant is not null)
            rightToolsTop.VisibleDockables.Add(aiAssistant);

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
            // 打包设置面板默认隐藏，经「窗口」菜单唤出，不进入初始可见列表。
            // The packing settings panel is hidden by default (invoked from the Window menu), so it
            // starts outside the visible list.
            VisibleDockables = CreateList<IDockable>(projectExplorer, pluginTool)
        };

        var bottomTools = new ToolDock
        {
            Id = "BottomPane",
            Title = "BottomPane",
            Proportion = 0.25,
            ActiveDockable = output,
            VisibleDockables = CreateList<IDockable>(output, adbLog)
        };

        var mainLayout = new ProportionalDock
        {
            Id = "WorkspaceMainLayout",
            Orientation = Dock.Model.Core.Orientation.Horizontal,
            Proportion = double.NaN,
            VisibleDockables = CreateList<IDockable>(
                leftTools,
                new ProportionalDockSplitter(),
                new ProportionalDock
                {
                    Id = "WorkspaceCenterColumn",
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
                    Id = "WorkspaceRightColumn",
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
        panels.Register(WorkspacePanelService.ProjectSetting, projectSetting, leftTools, visible: false);
        panels.Register(WorkspacePanelService.PluginTool, pluginTool, leftTools, visible: true);
        panels.Register(WorkspacePanelService.ImagesView, imagesView, rightToolsTop, visible: true);
        if (aiAssistant is not null)
            panels.Register(WorkspacePanelService.AiAssistant, aiAssistant, rightToolsTop, visible: true);
        panels.Register(WorkspacePanelService.Properties, properties, rightTools, visible: true);
        panels.Register(WorkspacePanelService.WorkspaceLog, output, bottomTools, visible: true);
        panels.Register(WorkspacePanelService.AdbLog, adbLog, bottomTools, visible: true);
        panels.RegisterTerminals(_terminals = new TerminalDockService(this, bottomTools));
        _terminals.CreateTerminal(activate: false);
        adbLog.Initialize();

        return rootDock;
    }

    /// <summary>Stops all terminal processes owned by this workspace. 停止工作区拥有的全部终端进程。</summary>
    public void DisposeTerminal() => _terminals?.Dispose();

    /// <summary>
    /// <para>关闭工作区全部文档型面板（文本/Hex/DTB/固件信息等，与工作区会话快照的文档口径一致）。
    /// 供工程切换前清理使用：脏文档已在会话操作门的确认阶段处理完毕，这里直接移除并复用
    /// <see cref="SafeRemoveDockable"/> 的释放逻辑，不再逐一询问。</para>
    /// Closes every document-kind dockable in the workspace (text/Hex/DTB/firmware info etc., matching
    /// the workspace session snapshot document scope). Used to clear documents before a project switch:
    /// dirty documents were already confirmed in the session gate, so this removes them directly and
    /// reuses the <see cref="SafeRemoveDockable"/> release logic without asking again.
    /// </summary>
    /// <param name="layout"><para>布局根（工作区 <c>Layout</c>）。</para>The layout root (workspace <c>Layout</c>).</param>
    public void CloseAllDocuments(IDockable? layout)
    {
        var documents = WorkspaceSessionCapture.Enumerate(layout)
            .Where(WorkspaceSessionCapture.IsDocument)
            .Distinct()
            .ToList();
        foreach (var document in documents)
        {
            _closingDocuments.Remove(document);
            SafeRemoveDockable(document);
        }
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

    public override async void CloseDockable(IDockable? dockable)
    {
        if (dockable == null)
        {
            return;
        }

        if (!dockable.CanClose)
        {
            return;
        }

        if (!_closingDocuments.Add(dockable)) return;
        try
        {
            if (!await DocumentSaveService.ConfirmCloseAsync(dockable)) return;
            SafeRemoveDockable(dockable);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Editor", $"Close failed: {ex.Message}");
        }
        finally { _closingDocuments.Remove(dockable); }
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
        if (dockable is TerminalViewModel terminal)
        {
            _terminals?.Remove(terminal);
        }
        else if (dockable is UotanStudio.ViewModels.Editors.LogEditorViewModel logEditor)
        {
            logEditor.Cleanup();
        }
        else if (dockable is UotanStudio.ViewModels.Compare.CompareViewModel compare)
        {
            compare.Cleanup();
        }
        else if (dockable is UotanStudio.ViewModels.Editors.FileViewModel fileVm)
        {
            try
            {
                fileVm.Cleanup();
            }
            catch
            {
                // 清理失败不影响关闭流程
            }
        }
        else if (dockable is UotanStudio.ViewModels.Editors.HexEditorViewModel hexEditor)
        {
            try
            {
                hexEditor.Cleanup();
            }
            catch
            {
                // 清理失败不影响关闭流程
            }
        }
        else if (dockable is UotanStudio.ViewModels.Editors.DtbEditorViewModel dtbEditor)
        {
            try
            {
                dtbEditor.Cleanup();
            }
            catch
            {
                // 清理失败不影响关闭流程
            }
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
        else if (dockable is UotanStudio.ViewModels.Editors.ManifestEditorViewModel manifestEditor)
        {
            try
            {
                manifestEditor.Cleanup();
            }
            catch
            {
                // 清理失败不影响关闭流程
            }
        }
        else if (dockable is UotanStudio.ViewModels.Editors.ArscEditorViewModel arscEditor)
        {
            try
            {
                arscEditor.Cleanup();
            }
            catch
            {
                // 清理失败不影响关闭流程
            }
        }
        else if (dockable is UotanStudio.ViewModels.Editors.ElfInfoViewModel elfInfo)
        {
            elfInfo.Cleanup();
        }
        else if (dockable is UotanStudio.ViewModels.Editors.FirmwareInfoViewModel firmwareInfo)
        {
            try
            {
                firmwareInfo.Cleanup();
            }
            catch
            {
                // 清理失败不影响关闭流程
            }
        }
        else if (dockable is UotanStudio.ViewModels.Editors.PartitionEditorViewModel partitionEditor)
        {
            try
            {
                partitionEditor.Cleanup();
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
