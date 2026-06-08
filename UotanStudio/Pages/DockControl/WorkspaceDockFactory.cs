using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using UotanStudio.Localization;

namespace UotanStudio.Pages.DockControl
{
    public class WorkspaceDockFactory : Factory
    {
        public override IRootDock CreateLayout()
        {
            var document1 = new Document { Id = "Document1", Title = "build.prop", CanClose = true };
            var document2 = new Document { Id = "Document2", Title = "init.rc", CanClose = true };

            var projectExplorer = new Tool { Id = "ProjectExplorer", Title = LocalizationService.Instance["Dock_ProjectExplorer"], CanClose = true };
            var properties = new Tool { Id = "Properties", Title = LocalizationService.Instance["Dock_Properties"], CanClose = true };
            var output = new Tool { Id = "Output", Title = LocalizationService.Instance["Dock_Output"], CanClose = true };
            var adbLog = new Tool { Id = "AdbLog", Title = LocalizationService.Instance["Dock_AdbLog"], CanClose = true };

            var documentDock = new DocumentDock
            {
                Id = "DocumentsPane",
                Title = LocalizationService.Instance["Dock_Documents"],
                IsCollapsable = false,
                Proportion = double.NaN,
                ActiveDockable = document1,
                VisibleDockables = CreateList<IDockable>(document1, document2)
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
                VisibleDockables = CreateList<IDockable>(projectExplorer)
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
                Proportion = double.NaN,
                VisibleDockables = CreateList<IDockable>(
                    leftTools,
                    new ProportionalDockSplitter(),
                    new ProportionalDock
                    {
                        Orientation = Orientation.Vertical,
                        Proportion = 0.5,
                        VisibleDockables = CreateList<IDockable>(
                            documentDock,
                            new ProportionalDockSplitter(),
                            bottomTools
                        )
                    },
                    new ProportionalDockSplitter(),
                    rightTools
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
                ["ProjectExplorer"] = () => LocalizationService.Instance["Dock_ProjectExplorer"],
                ["Properties"] = () => LocalizationService.Instance["Dock_Properties"],
                ["Output"] = () => "Build succeeded.",
                ["AdbLog"] = () => "ADB Log Output"
            };

            HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
            {
                [nameof(IDockWindow)] = () => new HostWindow(),
                ["ManagedHostWindow"] = () => new ManagedHostWindow()
            };

            DefaultHostWindowLocator = () => new HostWindow();

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