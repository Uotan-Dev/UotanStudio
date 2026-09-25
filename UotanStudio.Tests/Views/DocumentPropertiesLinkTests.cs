using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Containers;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services;
using UotanStudio.Services.Workspace;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class DocumentPropertiesLinkTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task Publish_SelectionsAndBackgroundChanges_FollowsOnlyDisplayedDocument()
    {
        await RunAsync(async () =>
        {
            using var temp = new TempDirectory();
            var properties = new PropertiesViewModel();
            var elf = new ElfInfoViewModel();
            var log = new LogEditorViewModel();
            var dex = new DexEditorViewModel();
            try
            {
                DocumentNotifications.Publish(elf);
                await elf.LoadAsync(temp.WriteBytes("sample.elf", ElfFixture.Create()), TestContext.Current.CancellationToken);
                Assert.Same(elf, DocumentNotifications.Create(elf).ActiveDocument);
                Assert.Contains(properties.Properties, row => row.Value == "0x123456789ABCDEF0");
                elf.SelectedPageIndex = 2;
                elf.SelectedSection = elf.Sections[2];
                Assert.Contains(properties.Properties, row => row.Value == ".text");
                elf.SelectedPageIndex = 1;
                elf.SelectedSegment = elf.Segments[0];
                Assert.DoesNotContain(properties.Properties, row => row.Value == ".text");
                Assert.Contains(properties.Properties, row => row.Value == elf.Segments[0].VirtualAddress);

                var partition = new PartitionEditorViewModel();
                var first = new PartitionItemViewModel(new PartitionEntry { Name = "system" }, 0, "main");
                var second = new PartitionItemViewModel(new PartitionEntry { Name = "vendor" }, 1, "main");
                partition.Partitions.Add(first);
                partition.Partitions.Add(second);
                DocumentNotifications.Publish(partition);
                Assert.Same(partition, DocumentNotifications.Create(partition).ActiveDocument);
                partition.SelectedPartition = first;
                Assert.Contains(properties.Properties, row => row.Value == "system");
                first.Name = "system_a";
                Assert.Contains(properties.Properties, row => row.Value == "system_a");
                partition.SelectedPartition = second;
                first.Name = "old_selection";
                Assert.DoesNotContain(properties.Properties, row => row.Value == "old_selection");
                Assert.Contains(properties.Properties, row => row.Value == "vendor");

                DocumentNotifications.Publish(dex);
                dex.SelectedResource = new JarEntryInfo("assets/example.txt", 42, JarEntryKind.Resource);
                Assert.Contains(properties.Properties, row => row.Value == "assets/example.txt");
                DocumentNotifications.Publish(log);
                Assert.Same(log, DocumentNotifications.Create(log).ActiveDocument);
                var path = temp.WriteBytes("test.log", "alpha\nbeta\nalpha again\n"u8.ToArray());
                await log.LoadAsync(path);
                log.SearchText = "alpha";
                Assert.Equal(2, log.Entries.Count);
                Assert.Contains(properties.Properties, row => row.Value == "alpha");
                Assert.Contains(properties.Properties, row => row.Value == "2");
                second.Name = "background_change";
                Assert.DoesNotContain(properties.Properties, row => row.Value == "background_change");
                DocumentNotifications.Publish(null);
                log.SearchText = "beta";
                Assert.Empty(properties.Properties);
            }
            finally
            {
                DocumentNotifications.Publish(null);
                WeakReferenceMessenger.Default.UnregisterAll(properties);
                elf.Cleanup();
                log.Cleanup();
                dex.Cleanup();
            }
        });
    }

    [Fact]
    public async Task ViewLocator_ClickExistingDocument_RestoresPropertiesAndCachesHandler()
    {
        await RunAsync(() =>
        {
            var properties = new PropertiesViewModel();
            var elf = new ElfInfoViewModel { Title = "ELF click target" };
            var locator = new ViewLocator();
            var view = Assert.IsAssignableFrom<Control>(locator.Build(elf));
            view.DataContext = elf;
            var window = new Window { Content = view, Width = 800, Height = 600 };
            try
            {
                Assert.Same(view, locator.Build(elf));
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                DocumentNotifications.Publish(new PartitionEditorViewModel());
                window.MouseDown(new Point(30, 25), MouseButton.Left);
                window.MouseUp(new Point(30, 25), MouseButton.Left);
                Assert.Contains(properties.Properties, row => row.Value == "ELF click target");
            }
            finally
            {
                DocumentNotifications.Publish(null);
                WeakReferenceMessenger.Default.UnregisterAll(properties);
                elf.Cleanup();
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Publish_DeviceTreeSelection_RestoresSelectedNodeAfterDocumentSwitch()
    {
        await RunAsync(() =>
        {
            var properties = new PropertiesViewModel();
            var dtb = new DtbEditorViewModel
            {
                SelectedNode = new DtbNodeViewModel(new DeviceTreeNode("soc@1234"))
            };
            try
            {
                DocumentNotifications.Publish(dtb);
                Assert.Contains(properties.Properties, row => row.Value == "soc@1234");
                DocumentNotifications.Publish(new PartitionEditorViewModel());
                DocumentNotifications.Publish(dtb);
                Assert.Contains(properties.Properties, row => row.Value == "soc@1234");
            }
            finally
            {
                DocumentNotifications.Publish(null);
                WeakReferenceMessenger.Default.UnregisterAll(properties);
                dtb.Cleanup();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task OpenWith_ContextMenu_ListsEveryModeAndDispatchesContextTarget()
    {
        await RunAsync(() =>
        {
            var model = new ProjectExplorerViewModel();
            var workspace = Path.Combine(Path.GetTempPath(), "uotan-docprops-" + Guid.NewGuid().ToString("N"));
            var target = new ExplorerNode(Path.Combine(workspace, "context.elf"), false);
            var other = new ExplorerNode(Path.Combine(workspace, "selected.bin"), false);
            model.TreeViewContent.Add(target);
            model.TreeViewContent.Add(other);
            var view = new ProjectExplorerView { DataContext = model };
            var window = new Window { Content = view, Width = 600, Height = 600 };
            var recipient = new object();
            var requests = new List<OpenDocumentRequestMessage>();
            WeakReferenceMessenger.Default.Register<OpenDocumentRequestMessage>(recipient, (_, message) => requests.Add(message));
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var tree = view.FindControl<TreeView>("ProjectTree")!;
                tree.SelectedItem = other;
                var container = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(target));
                var menu = Assert.IsType<ContextMenu>(container.ContextMenu);
                menu.DataContext = target;
                menu.Open(container);
                var modes = menu.Items.OfType<MenuItem>().SelectMany(item => item.Items.OfType<MenuItem>())
                    .Where(item => item.Tag is EditorOpenMode).ToArray();
                Assert.Equal(Enum.GetValues<EditorOpenMode>().Order(), modes.Select(item => (EditorOpenMode)item.Tag!).Order());
                foreach (var item in modes)
                {
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.Equal((EditorOpenMode)item.Tag!, requests[^1].Mode);
                    Assert.Equal(target.Path, requests[^1].Path);
                }
                Assert.Equal(modes.Length, requests.Count);
                menu.Close();
            }
            finally
            {
                WeakReferenceMessenger.Default.UnregisterAll(recipient);
                window.Close();
                model.Dispose();
            }
            return Task.CompletedTask;
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.Session.Dispatch(action, TestContext.Current.CancellationToken);
        }
        await dispatch.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
    }
}