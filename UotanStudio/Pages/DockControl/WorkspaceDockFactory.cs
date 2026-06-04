using System;
using System.Collections.Generic;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace UotanStudio.Pages.DockControl
{
    public class WorkspaceDockFactory : Factory
    {
        public override IRootDock CreateLayout()
        {
            var document1 = new Document { Id = "Document1", Title = "build.prop" };
            var document2 = new Document { Id = "Document2", Title = "init.rc" };

            var projectExplorer = new Tool { Id = "ProjectExplorer", Title = "Project Explorer" };
            var properties = new Tool { Id = "Properties", Title = "Properties" };
            var output = new Tool { Id = "Output", Title = "Output" };
            var adbLog = new Tool { Id = "AdbLog", Title = "ADB Log" };

            var documentDock = new DocumentDock
            {
                Id = "DocumentsPane",
                Title = "Documents",
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
            ContextLocator = new Dictionary<string, Func<object>>
            {
                ["Document1"] = () => "using System;\n\nnamespace App {\n}",
                ["Document2"] = () => "<Window>\n</Window>",
                ["SolutionExplorer"] = () => "Solution Tree",
                ["Properties"] = () => "Properties View",
                ["Output"] = () => "Build succeeded.",
                ["ErrorList"] = () => "0 Errors, 0 Warnings"
            };

            HostWindowLocator = new Dictionary<string, Func<IHostWindow>>
            {
                [nameof(IDockWindow)] = () => new HostWindow()
            };

            base.InitLayout(layout);
        }
    }
}