using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio.Pages.DockControl
{
    public class WorkspaceDockFactory : Factory
    {
        /// <summary>
        /// 关联的主 DockControl，用于共享 DockManagerOptions 并在浮动窗口拖拽时参与命中测试。
        /// Set by WorkspaceView after the DockControl is loaded; null before that point.
        /// </summary>
        public Dock.Avalonia.Controls.DockControl? DockControl { get; set; }

        public override IRootDock CreateLayout()
        {
            var svc = LanguageService.Instance;
            var document1 = new FileViewModel { Id = "Document1", Title = svc.GetString("Document_BuildProp"), CanClose = true };
            var document2 = new FileViewModel { Id = "Document2", Title = svc.GetString("Document_InitRc"), CanClose = true };
            var projectSetting = new ProjectSettingViewModel { Id = "ProjectSetting", Title = LanguageService.Instance.GetString("Dock_ProjectSetting"), CanClose = true };
            var projectExplorer = new ProjectExplorerViewModel { Id = "ProjectExplorer", Title = LanguageService.Instance.GetString("Dock_ProjectExplorer"), CanClose = true };
            var imagesview = new ImageViewModel { Id = "ImagesView", Title = LanguageService.Instance.GetString("Dock_ImagesView"), CanClose = true };
            var properties = new PropertiesViewModel { Id = "Properties", Title = LanguageService.Instance.GetString("Dock_Properties"), CanClose = true };
            var output = new OutputViewModel { Id = "Output", Title = LanguageService.Instance.GetString("Dock_Output"), CanClose = true };
            var adbLog = new Tool { Id = "AdbLog", Title = LanguageService.Instance.GetString("Dock_AdbLog"), CanClose = true };

            var documentDock = new DocumentDock
            {
                Id = "DocumentsPane",
                Title = LanguageService.Instance.GetString("Dock_Documents"),
                IsCollapsable = false,
                Proportion = double.NaN,
                ActiveDockable = document1,
                VisibleDockables = CreateList<IDockable>(document1, document2)
            };

            var rightToolsTop = new ToolDock
            {
                Id = "RightPaneTop",
                Title = "RightPaneTop",
                Proportion = 0.25,
                ActiveDockable = imagesview,
                VisibleDockables = CreateList<IDockable>(imagesview)
            };

            var rightTools = new ToolDock
            {
                Id = "RightPane",
                Title = "RightPane",
                Proportion = 0.25,
                ActiveDockable = properties,
                VisibleDockables = CreateList<IDockable>(properties)
            };

            var leftTools = new ToolDock
            {
                Id = "LeftPane",
                Title = "LeftPane",
                Proportion = 0.25,
                ActiveDockable = projectExplorer,
                VisibleDockables = CreateList<IDockable>(projectExplorer, projectSetting)
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
                        Proportion = 0.3,
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
                ["Document1"] = () => "using System;\n\nnamespace App {\n}",
                ["Document2"] = () => "<Window>\n</Window>",
                ["ProjectExplorer"] = () => LanguageService.Instance.GetString("Dock_ProjectExplorer"),
                ["Properties"] = () => LanguageService.Instance.GetString("Dock_Properties"),
                ["Output"] = () => LanguageService.Instance.GetString("Dock_Output_Log"),
                ["AdbLog"] = () => LanguageService.Instance.GetString("Dock_AdbLog_Output")
            };

            HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
            {
                [nameof(IDockWindow)] = () => new HostWindow(DockControl?.DockManagerOptions),
                ["ManagedHostWindow"] = () => new ManagedHostWindow(DockControl?.DockManagerOptions)
            };

            DefaultHostWindowLocator = () => new HostWindow(DockControl?.DockManagerOptions);

            base.InitLayout(layout);
        }

        public override void CloseDockable(IDockable dockable)
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
}
