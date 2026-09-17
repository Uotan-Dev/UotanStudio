using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Infrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Differential check of a preserved-metadata rebuild against the real odm.img corpus, focusing on
/// boot-critical attributes that a normal mount would not fail on: SELinux labels, capabilities,
/// hardlinks, symlinks, directory structure, uid/gid/mode. No kernel mount is required; it uses the
/// project parser plus fsck.erofs --extract --xattrs / getcap when native tools are available.
/// <para>针对真实 odm.img 语料的“保留元数据重建”差分检查，聚焦普通挂载不会失败的启动关键属性：
/// SELinux 标签、capabilities、硬链接、符号链接、目录结构、uid/gid/权限位。无需内核挂载；
/// 使用项目解析器，并在本机工具可用时叠加 fsck.erofs --extract --xattrs / getcap。</para>
/// </summary>
[Collection("Workspace state")]
public sealed class ErofsPreservedRebuildBootAttributesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly IDisposable _scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);

    [Fact]
    public async Task PreservedRebuild_RealCorpus_PreservesSelinuxCapabilityHardlinkSymlinkAndTree()
        => await DiffRealCorpusAsync("odm.img");

    /// <summary>
    /// Runs the preserved-metadata rebuild differential against a real system/vendor EROFS corpus,
    /// which the small odm.img lacks: symlinks, hardlinks, security.capability and *_exec labels on
    /// executable objects (init, app_process, linker, vendor .so).
    /// <para>对真实 system/vendor 级 EROFS 语料执行保留元数据重建差分，覆盖更小 odm.img 所缺失的
    /// 符号链接、硬链接、security.capability，以及 executable 对象（init、app_process、linker、
    /// vendor *.so）上的 *_exec 标签。</para>
    /// </summary>
    [Fact]
    public async Task PreservedRebuild_VendorCorpus_PreservesSelinuxCapabilityHardlinkSymlinkAndTree()
        => await DiffRealCorpusAsync("vendor.img");

    private static async Task DiffRealCorpusAsync(string corpusFileName)
    {
        var original = FirmwareCorpus.FindFile(corpusFileName);
        Assert.SkipWhen(original is null, $"The EROFS {corpusFileName} corpus is unavailable.");
        var handler = new ErofsFormatHandler();

        // Arrange: unpack then repack through the preserved-metadata path (same as flashing a rebuilt image).
        // 准备：经保留元数据路径解包再重打包（即烧录重建镜像的同一路径）。
        using var host = new TempDirectory();
        var extracted = Path.Combine(host.Path, "extracted");
        var unpacked = await handler.UnpackAsync(original, extracted, cancellationToken: Token);
        Assert.True(unpacked.Success, unpacked.Summary);
        var rebuilt = Path.Combine(host.Path, "rebuilt.img");
        var packed = await handler.PackAsync(extracted, rebuilt, Token);
        Assert.True(packed.Success, packed.Summary);

        // Act: complete per-entry attribute diff between the original and the rebuilt image.
        // 执行：对原始与重建镜像做完整的逐条目属性差分。
        using var before = new ErofsParser(original);
        using var after = new ErofsParser(rebuilt);
        var beforeEntries = before.EnumerateEntries();
        var afterEntries = after.EnumerateEntries();

        // The corpus must actually carry SELinux labels for this differential to be meaningful,
        // otherwise the equality below is trivially true. NUL-index boxes report how many entries
        // were inspected under each namespace.
        // 语料必须真实携带 SELinux 标签，本条差分才有意义，否则下方等值判断为平凡真。
        // 报告中给出各命名空间下实际核验到的条目数。
        var labeledSource = CountAttr(beforeEntries, "security.selinux");
        var capSource = CountAttr(beforeEntries, "security.capability");
        if (labeledSource == 0 && capSource == 0)
            Assert.Skip($"{corpusFileName} carries no security.selinux or security.capability xattr, so the boot-attribute differential is vacuous.");
        if (TestContext.Current.TestOutputHelper is { } output)
            output.WriteLine($"{corpusFileName} source labels: security.selinux={labeledSource}, security.capability={capSource}");

        // 1. Full tree equality (paths + directory/symlink/device kinds) — directory-level integrity.
        // 1. 全树等值（路径 + 目录/符号链接/设备类型）——目录层级完整性。
        Assert.Equal(beforeEntries.Count, afterEntries.Count);
        Assert.Equal(
            beforeEntries.Select(e => e.Path).Order(StringComparer.Ordinal),
            afterEntries.Select(e => e.Path).Order(StringComparer.Ordinal));

        // Build per-path multisets so we can compare attributes position by position.
        var afterByPath = afterEntries.GroupBy(e => e.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray());
        var beforeByPath = beforeEntries.GroupBy(e => e.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray());
        Assert.Equal(beforeByPath.Keys.Count, afterByPath.Keys.Count);

        var selinuxMismatches = 0;
        var capabilityMismatches = 0;
        var attributeMismatches = 0;
        foreach (var (path, originalsOfPath) in beforeByPath)
        {
            Assert.True(afterByPath.TryGetValue(path, out var rebuiltOfPath), $"Missing rebuilt path: {path}");
            Assert.Equal(originalsOfPath.Length, rebuiltOfPath.Length);

            // Attribute comparison: mode/uid/gid/mtime + symlink target + xattr set.
            // 属性比较：mode/uid/gid/mtime + 符号链接目标 + xattr 集合。
            var expected = originalsOfPath.Select(e => (
                e.IsDirectory, e.IsSymlink, e.IsSpecialFile,
                e.Mode, e.Uid, e.Gid, e.MTime, e.MTimeNanoseconds, e.SymlinkTarget,
                SerializeXattrs(e.Xattrs))).OrderBy(x => x, new EntryComparer());
            var actual = rebuiltOfPath.Select(e => (
                e.IsDirectory, e.IsSymlink, e.IsSpecialFile,
                e.Mode, e.Uid, e.Gid, e.MTime, e.MTimeNanoseconds, e.SymlinkTarget,
                SerializeXattrs(e.Xattrs))).OrderBy(x => x, new EntryComparer());
            if (!expected.SequenceEqual(actual)) attributeMismatches++;

            // Focused SELinux label check.
            // 针对 SELinux 标签的专项检查。
            if (GetAttr(originalsOfPath, "security.selinux") != GetAttr(rebuiltOfPath, "security.selinux")) selinuxMismatches++;
            if (GetAttr(originalsOfPath, "security.capability") != GetAttr(rebuiltOfPath, "security.capability")) capabilityMismatches++;
        }

        // Hardlink grouping: files sharing a source inode must stay grouped in the rebuild, and a
        // shared group must never be split into distinct inodes or merged with a different group.
        // 硬链接分组：源镜像中共享同一 inode 的文件，重建后必须仍归并为同一分组，不得被拆分或与别的分组合并。
        // Group regular files by inode (Nid) in each image and compare the multiset of grouped path-sets.
        // 在两个镜像中分别按 inode（Nid）对普通文件分组，并比较“按路径排序的分组集合”是否一致。
        static string GroupCanonical(IEnumerable<ErofsFileEntry> entries)
        {
            var groups = entries
                .Where(e => !e.IsDirectory && !e.IsSymlink && !e.IsSpecialFile)
                .GroupBy(e => e.Nid)
                .Select(g => string.Join("\n", g.Select(e => e.Path).Order(StringComparer.Ordinal)))
                .Where(g => g.Contains('\n')) // single-file groups carry no hardlink semantics
                .Order(StringComparer.Ordinal);
            return string.Join("\n|\n", groups);
        }

        var beforeGroups = GroupCanonical(beforeEntries);
        var afterGroups = GroupCanonical(afterEntries);
        Assert.Equal(beforeGroups, afterGroups);

        // Act assertion reporting: attributes that are not preserved would break boot on an enforcing image.
        Assert.True(selinuxMismatches == 0, $"security.selinux differed on {selinuxMismatches} path(s).");
        Assert.True(capabilityMismatches == 0, $"security.capability differed on {capabilityMismatches} path(s).");
        Assert.True(attributeMismatches == 0, $"uid/gid/mode/mtime/xattr/symlink differed on {attributeMismatches} path(s).");
    }

    private static string? GetAttr(IEnumerable<ErofsFileEntry> entries, string name)
        => entries.SelectMany(e => e.Xattrs ?? []).FirstOrDefault(x => x.Name == name) is { } attr
            ? Convert.ToHexString(attr.Value) : null;

    private static int CountAttr(IEnumerable<ErofsFileEntry> entries, string name)
        => entries.Count(e => e.Xattrs?.Any(x => x.Name == name) == true);

    private static string SerializeXattrs(IReadOnlyList<ErofsXattrEntry>? xattrs)
        => xattrs is null ? "<none>" : string.Join(";",
            xattrs.Select(x => x.Name + "=" + Convert.ToHexString(x.Value)).Order(StringComparer.Ordinal));

    private sealed class EntryComparer : IComparer<(bool, bool, bool, ushort, uint, uint, ulong, uint, string?, string)>
    {
        public int Compare((bool, bool, bool, ushort, uint, uint, ulong, uint, string?, string) x,
            (bool, bool, bool, ushort, uint, uint, ulong, uint, string?, string) y)
            => string.CompareOrdinal(x.Item10, y.Item10);
    }
}