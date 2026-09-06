using UotanStudio.Firmware.Ext4;
using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.ViewModels;

/// <summary>
/// <para>文件管理器文档编辑器测试：用 <see cref="Ext4Builder"/> 构建小镜像，
/// 断言 <see cref="FileManagerEditorViewModel.LoadFromImageAsync"/> 后的目录树结构、统计与刷新命令。</para>
/// File-manager document editor tests: build a small ext4 image with <see cref="Ext4Builder"/> and assert
/// the tree structure, statistics and refresh command after
/// <see cref="FileManagerEditorViewModel.LoadFromImageAsync"/>.
/// </summary>
public sealed class FileManagerEditorViewModelTests
{
    [Fact]
    public async Task LoadFromImageAsync_Ext4Image_BuildsTreeAndStatistics()
    {
        var root = CreateTempDirectory();
        var imagePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".img");
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.txt"), "hello ext4\n");
            File.WriteAllBytes(Path.Combine(root, "blob.bin"), BinaryBlob);
            Directory.CreateDirectory(Path.Combine(root, "subdir"));
            File.WriteAllText(Path.Combine(root, "subdir", "inner.txt"), "inner content\n");

            Ext4Builder.Build(root, imagePath);

            var vm = new FileManagerEditorViewModel();
            try
            {
                await vm.LoadFromImageAsync(imagePath);

                Assert.False(vm.HasError);
                Assert.False(vm.IsLoading);
                Assert.Equal("EXT4", vm.FormatName);
                Assert.Equal(new FileInfo(imagePath).Length, vm.ImageSize);
                Assert.Equal(imagePath, vm.ImagePath);

                // 文件 3 个（hello.txt / blob.bin / subdir/inner.txt），目录 1 个（subdir）。
                // 3 files (hello.txt / blob.bin / subdir/inner.txt) and 1 directory (subdir).
                Assert.Equal(3, vm.FileCount);
                Assert.Equal(1, vm.DirectoryCount);

                // 根层条目：blob.bin、hello.txt（文件，按名排序）与 subdir（目录优先）。
                // Root entries: blob.bin, hello.txt (files, name-sorted) and subdir (directories first).
                Assert.Equal(3, vm.Entries.Count);
                var subdir = Assert.Single(vm.Entries, static e => e.IsDirectory);
                Assert.Equal("subdir", subdir.Name);
                Assert.True(subdir.Size > 0);

                var hello = vm.Entries.FirstOrDefault(static e => e.Name == "hello.txt");
                Assert.NotNull(hello);
                Assert.Equal(FileEntryKind.File, hello.Kind);
                Assert.Equal("hello ext4\n".Length, hello.Size);

                var inner = Assert.Single(subdir.Children);
                Assert.Equal("inner.txt", inner.Name);
                Assert.Equal(FileEntryKind.File, inner.Kind);
                Assert.Equal("inner content\n".Length, inner.Size);
                Assert.Equal("subdir/inner.txt", inner.Path);

                var imageRoot = Assert.Single(vm.DirectoryRoots);
                Assert.Equal("/", vm.CurrentPath);
                Assert.Same(imageRoot, vm.CurrentDirectory);
                Assert.Equal(vm.Entries, vm.CurrentEntries);
                Assert.Same(subdir, Assert.Single(imageRoot.Directories));
                Assert.False(vm.NavigateUpCommand.CanExecute(null));
                Assert.False(vm.ExtractSelectedCommand.CanExecute(null));

                vm.OpenEntry(subdir);
                Assert.Equal("/subdir", vm.CurrentPath);
                Assert.Same(inner, Assert.Single(vm.CurrentEntries));
                Assert.True(vm.NavigateUpCommand.CanExecute(null));
                Assert.True(vm.ExtractSelectedCommand.CanExecute(null));
                Assert.True(imageRoot.IsExpanded);

                vm.OpenEntry(inner);
                Assert.Same(subdir, vm.CurrentDirectory);
                Assert.Same(inner, vm.SelectedEntry);

                vm.NavigateUpCommand.Execute(null);
                Assert.Same(imageRoot, vm.CurrentDirectory);
                Assert.Same(subdir, vm.SelectedEntry);

                // 符号链接未参与断言：Windows 下创建可能受开发者模式限制。
                // Symlinks are not asserted: creating them on Windows may require developer mode.
            }
            finally
            {
                vm.Cleanup();
            }
        }
        finally
        {
            DeleteRecursive(root);
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task RefreshCommand_ReloadsImage()
    {
        var root = CreateTempDirectory();
        var imagePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".img");
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "alpha");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "b.txt"), "beta");
            Ext4Builder.Build(root, imagePath);

            var vm = new FileManagerEditorViewModel();
            try
            {
                await vm.LoadFromImageAsync(imagePath);
                Assert.Equal(2, vm.FileCount);
                Assert.Equal(1, vm.DirectoryCount);

                vm.OpenEntry(Assert.Single(vm.Entries, static entry => entry.IsDirectory));
                vm.OpenEntry(Assert.Single(vm.CurrentEntries));

                // 刷新命令重新加载：状态被复位后重建。
                // The refresh command reloads: state is reset and rebuilt.
                await vm.RefreshCommand.ExecuteAsync(null);

                Assert.False(vm.HasError);
                Assert.False(vm.IsLoading);
                Assert.Equal("EXT4", vm.FormatName);
                Assert.Equal(2, vm.FileCount);
                Assert.Equal(1, vm.DirectoryCount);
                Assert.Contains(vm.Entries, static e => e.Name == "nested" && e.IsDirectory);
                Assert.Equal("/nested", vm.CurrentPath);
                Assert.Equal("nested/b.txt", vm.SelectedEntry?.Path);
                Assert.Same(vm.SelectedEntry, Assert.Single(vm.CurrentEntries));

                vm.Cleanup();
                Assert.Empty(vm.DirectoryRoots);
                Assert.Empty(vm.CurrentEntries);
                Assert.Null(vm.CurrentDirectory);
                Assert.Null(vm.SelectedEntry);
                Assert.False(vm.NavigateUpCommand.CanExecute(null));
                Assert.False(vm.ExtractSelectedCommand.CanExecute(null));
            }
            finally
            {
                vm.Cleanup();
            }
        }
        finally
        {
            DeleteRecursive(root);
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task LoadFromImageAsync_NonImageFile_ReportsError()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".img");
        try
        {
            // 全零内容：不命中任何文件系统魔数。
            // All-zero content: no filesystem magic matches.
            File.WriteAllBytes(imagePath, new byte[4096]);

            var vm = new FileManagerEditorViewModel();
            try
            {
                await vm.LoadFromImageAsync(imagePath);

                Assert.True(vm.HasError);
                Assert.NotEmpty(vm.ErrorMessage);
                Assert.Empty(vm.Entries);
                Assert.Empty(vm.DirectoryRoots);
                Assert.Empty(vm.CurrentEntries);
                Assert.False(vm.IsDirectoryEmpty);
                Assert.Equal(0, vm.FileCount);
                Assert.Equal(0, vm.DirectoryCount);
                Assert.False(vm.IsLoading);
            }
            finally
            {
                vm.Cleanup();
            }
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    private static readonly byte[] BinaryBlob = [0x00, 0x01, 0x02, 0xFE, 0xFF, 0x80, 0x7F, 0x10, 0x20, 0x30];

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-fm-" + Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}