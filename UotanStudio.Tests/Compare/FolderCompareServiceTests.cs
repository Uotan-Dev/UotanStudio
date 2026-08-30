using UotanStudio.Services.Compare;
using Xunit;

namespace UotanStudio.Tests.Compare;

/// <summary>
/// FolderCompareService tests: verify the compare classification (Equal/Different/LeftOnly/RightOnly),
/// recursive subdirectory comparison, byte-exact content matching, and copy/delete operations by
/// relative path, all exercised against real temporary directories.
/// <para>FolderCompareService 测试：验证比较分类（Equal/Different/LeftOnly/RightOnly）、子目录递归比较、
/// 逐字节内容匹配，以及基于相对路径的复制/删除操作，均针对真实临时目录进行。</para>
/// </summary>
public sealed class FolderCompareServiceTests
{
    [Fact]
    public async Task CompareAsync_ClassifiesEqualDifferentAndOrphans()
    {
        var root = CreateTempDirectory();
        try
        {
            var left = Path.Combine(root, "left");
            var right = Path.Combine(root, "right");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);

            File.WriteAllText(Path.Combine(left, "same.txt"), "identical content");
            File.WriteAllText(Path.Combine(right, "same.txt"), "identical content");
            File.WriteAllText(Path.Combine(left, "diff.txt"), "left content");
            File.WriteAllText(Path.Combine(right, "diff.txt"), "right content");
            File.WriteAllText(Path.Combine(left, "left_only.txt"), "only on the left");
            File.WriteAllText(Path.Combine(right, "right_only.txt"), "only on the right");

            var result = await FolderCompareService.Instance.CompareAsync(left, right, TestContext.Current.CancellationToken);

            // 四个文件条目：equal / different / left-only / right-only 各一。
            // Four file entries: one each of equal / different / left-only / right-only.
            Assert.Equal(4, result.Items.Count);
            Assert.Equal(1, result.EqualCount);
            Assert.Equal(1, result.DifferentCount);
            Assert.Equal(1, result.LeftOnlyCount);
            Assert.Equal(1, result.RightOnlyCount);

            var same = Assert.Single(result.Items, static i => i.RelativePath == "same.txt");
            Assert.False(same.IsDirectory);
            Assert.Equal(FolderItemStatus.Equal, same.Status);

            var diff = Assert.Single(result.Items, static i => i.RelativePath == "diff.txt");
            Assert.False(diff.IsDirectory);
            Assert.Equal(FolderItemStatus.Different, diff.Status);

            // 孤儿条目：缺失侧尺寸为 -1。
            // Orphan entries report -1 for the missing side.
            var leftOnly = Assert.Single(result.Items, static i => i.RelativePath == "left_only.txt");
            Assert.Equal(FolderItemStatus.LeftOnly, leftOnly.Status);
            Assert.True(leftOnly.LeftSize > 0);
            Assert.Equal(-1, leftOnly.RightSize);

            var rightOnly = Assert.Single(result.Items, static i => i.RelativePath == "right_only.txt");
            Assert.Equal(FolderItemStatus.RightOnly, rightOnly.Status);
            Assert.Equal(-1, rightOnly.LeftSize);
            Assert.True(rightOnly.RightSize > 0);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public async Task CompareAsync_SubdirectoriesAreCompared()
    {
        var root = CreateTempDirectory();
        try
        {
            var left = Path.Combine(root, "left");
            var right = Path.Combine(root, "right");
            Directory.CreateDirectory(Path.Combine(left, "sub"));
            Directory.CreateDirectory(Path.Combine(right, "sub"));

            File.WriteAllText(Path.Combine(left, "sub", "same.txt"), "shared content");
            File.WriteAllText(Path.Combine(right, "sub", "same.txt"), "shared content");
            File.WriteAllText(Path.Combine(left, "sub", "diff.txt"), "left content");
            File.WriteAllText(Path.Combine(right, "sub", "diff.txt"), "right content");

            var result = await FolderCompareService.Instance.CompareAsync(left, right, TestContext.Current.CancellationToken);

            // 子目录条目本身按目录报告，其内部文件用 '/' 拼接的相对路径分类。
            // The subdirectory entry is reported as a directory; files inside use '/' joined paths.
            Assert.Equal(3, result.Items.Count);

            var subDir = Assert.Single(result.Items, static i => i.RelativePath == "sub");
            Assert.True(subDir.IsDirectory);

            var same = Assert.Single(result.Items, static i => i.RelativePath == "sub/same.txt");
            Assert.False(same.IsDirectory);
            Assert.Equal(FolderItemStatus.Equal, same.Status);

            var diff = Assert.Single(result.Items, static i => i.RelativePath == "sub/diff.txt");
            Assert.False(diff.IsDirectory);
            Assert.Equal(FolderItemStatus.Different, diff.Status);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public async Task CompareAsync_SameSizeDifferentContentIsDifferent()
    {
        var root = CreateTempDirectory();
        try
        {
            var left = Path.Combine(root, "left");
            var right = Path.Combine(root, "right");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);

            // 等长（各 4 字节）但内容不同，不能只靠尺寸判定。
            // Same length (4 bytes each) but different content — size alone must not classify as equal.
            File.WriteAllText(Path.Combine(left, "payload.bin"), "AAAA");
            File.WriteAllText(Path.Combine(right, "payload.bin"), "ABAA");

            var result = await FolderCompareService.Instance.CompareAsync(left, right, TestContext.Current.CancellationToken);

            var item = Assert.Single(result.Items);
            Assert.Equal("payload.bin", item.RelativePath);
            Assert.Equal(FolderItemStatus.Different, item.Status);
            Assert.Equal(4, item.LeftSize);
            Assert.Equal(4, item.RightSize);
            Assert.Equal(1, result.DifferentCount);
            Assert.Equal(0, result.EqualCount);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public async Task CompareAsync_EmptyDirectories()
    {
        var root = CreateTempDirectory();
        try
        {
            var left = Path.Combine(root, "left");
            var right = Path.Combine(root, "right");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);

            var result = await FolderCompareService.Instance.CompareAsync(left, right, TestContext.Current.CancellationToken);

            // 两个空目录：无任何可比较条目。
            // Two empty directories: nothing to compare.
            Assert.Empty(result.Items);
            Assert.Equal(0, result.EqualCount);
            Assert.Equal(0, result.DifferentCount);
            Assert.Equal(0, result.LeftOnlyCount);
            Assert.Equal(0, result.RightOnlyCount);
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public async Task CopyAsync_CopiesFileAndCreatesDirectories()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            Directory.CreateDirectory(target);

            const string payload = "nested payload";
            File.WriteAllText(Path.Combine(source, "sub", "nested.txt"), payload);

            await FolderCompareService.Instance.CopyAsync(source, target, "sub/nested.txt", TestContext.Current.CancellationToken);

            // 目标侧缺失的 sub 目录应由 CopyAsync 自动创建。
            // The missing sub directory on the target side must be created by CopyAsync.
            var copied = Path.Combine(target, "sub", "nested.txt");
            Assert.True(File.Exists(copied));
            Assert.Equal(payload, File.ReadAllText(copied));
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public async Task DeleteAsync_DeletesRelativeFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var target = Path.Combine(root, "dir");
            Directory.CreateDirectory(target);
            var file = Path.Combine(target, "to-delete.txt");
            File.WriteAllText(file, "delete me");

            await FolderCompareService.Instance.DeleteAsync(target, "to-delete.txt", TestContext.Current.CancellationToken);

            Assert.False(File.Exists(file));
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    [Fact]
    public async Task CompareAsync_Cancellation()
    {
        var root = CreateTempDirectory();
        try
        {
            var left = Path.Combine(root, "left");
            var right = Path.Combine(root, "right");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);

            // 已取消的令牌：比较必须立即以取消异常失败。
            // A pre-cancelled token: the comparison must fail fast with a cancellation exception.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => FolderCompareService.Instance.CompareAsync(left, right, new CancellationToken(canceled: true)));
        }
        finally
        {
            DeleteRecursive(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-folder-compare-" + Path.GetRandomFileName());
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
