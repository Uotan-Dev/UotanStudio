using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using System;
using System.Collections.Generic;
using UotanStudio.Localization;

namespace UotanStudio.Pages.DockControl
{
    /// <summary>
    /// <para>节点编辑器的 Dock 布局工厂，创建包含工具箱、画布、属性面板和执行日志的布局。</para>
    /// Dock layout factory for the node editor, creating a layout with toolbox, canvas, properties panel, and execution log.
    /// </summary>
    public class NodeDockFactory : Factory
    {
        /// <summary>
        /// <para>创建节点编辑器的 Dock 布局。</para>
        /// Creates the dock layout for the node editor.
        /// </summary>
        /// <returns>根 Dock 布局。</returns>
        public override IRootDock CreateLayout()
        {
            var nodeToolbox = new Tool
            {
                Id = "NodeToolbox",
                Title = LocalizationService.Instance["Node_Toolbox"],
                CanClose = false
            };

            var nodeProperties = new Tool
            {
                Id = "NodeProperties",
                Title = LocalizationService.Instance["Node_Properties"],
                CanClose = false
            };

            var executionLog = new Tool
            {
                Id = "ExecutionLog",
                Title = LocalizationService.Instance["Node_ExecLog"],
                CanClose = false
            };

            var nodeCanvas = new Document
            {
                Id = "NodeCanvas",
                Title = LocalizationService.Instance["Node_Canvas"],
                CanClose = false
            };

            var documentDock = new DocumentDock
            {
                Id = "NodeCanvasPane",
                Title = LocalizationService.Instance["Node_Canvas"],
                IsCollapsable = false,
                Proportion = double.NaN,
                ActiveDockable = nodeCanvas,
                VisibleDockables = CreateList<IDockable>(nodeCanvas)
            };

            var leftTools = new ToolDock
            {
                Id = "NodeLeftPane",
                Title = "NodeLeftPane",
                Proportion = 0.2,
                ActiveDockable = nodeToolbox,
                VisibleDockables = CreateList<IDockable>(nodeToolbox)
            };

            var rightTools = new ToolDock
            {
                Id = "NodeRightPane",
                Title = "NodeRightPane",
                Proportion = 0.2,
                ActiveDockable = nodeProperties,
                VisibleDockables = CreateList<IDockable>(nodeProperties)
            };

            var bottomTools = new ToolDock
            {
                Id = "NodeBottomPane",
                Title = "NodeBottomPane",
                Proportion = 0.25,
                ActiveDockable = executionLog,
                VisibleDockables = CreateList<IDockable>(executionLog)
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
                        Proportion = 0.6,
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

        /// <summary>
        /// <para>初始化 Dock 布局，注册上下文定位器和宿主窗口定位器。</para>
        /// Initializes the dock layout, registering context locators and host window locators.
        /// </summary>
        /// <param name="layout">
        /// <para>要初始化的布局。</para>
        /// The layout to initialize.
        /// </param>
        public override void InitLayout(IDockable layout)
        {
            ContextLocator = new Dictionary<string, Func<object?>>
            {
                ["NodeToolbox"] = () => LocalizationService.Instance["Node_Toolbox"],
                ["NodeProperties"] = () => LocalizationService.Instance["Node_Properties"],
                ["ExecutionLog"] = () => LocalizationService.Instance["Node_ExecLog"],
                ["NodeCanvas"] = () => LocalizationService.Instance["Node_Canvas"]
            };

            HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
            {
                [nameof(IDockWindow)] = () => new HostWindow(),
                ["ManagedHostWindow"] = () => new ManagedHostWindow()
            };

            DefaultHostWindowLocator = () => new HostWindow();

            base.InitLayout(layout);
        }

        /// <summary>
        /// <para>关闭可停靠面板，安全移除。</para>
        /// Closes a dockable panel with safe removal.
        /// </summary>
        /// <param name="dockable">
        /// <para>要关闭的可停靠面板。</para>
        /// The dockable to close.
        /// </param>
        public override void CloseDockable(IDockable dockable)
        {
            if (dockable == null || !dockable.CanClose)
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
                if (dockable?.Owner is IDock dock && dock.VisibleDockables != null)
                {
                    var list = new List<IDockable>(dock.VisibleDockables);
                    if (list.Contains(dockable))
                    {
                        list.Remove(dockable);
                        dock.VisibleDockables = CreateList(list.ToArray());

                        if (dock.ActiveDockable == dockable)
                        {
                            dock.ActiveDockable = list.Count > 0 ? list[0] : null;
                        }
                    }
                }

                if (dockable != null)
                {
                    dockable.Owner = null;
                }
            }
            catch
            {
                // 忽略任何移除过程中的错误
            }
        }
    }
}
