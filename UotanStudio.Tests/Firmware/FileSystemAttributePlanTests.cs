using UotanStudio.Firmware.FileSystem.Rebuilding;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class FileSystemAttributePlanTests
{
    [Fact]
    public void ApplyAttributes_Hardlink_UpdatesAllAliasesAndRejectsConflicts()
    {
        var plan = Create(false);
        plan.ApplyAttributes([new("a", 123, 456, 0x1ED)], TestContext.Current.CancellationToken);
        var alias = plan.Get(Path.Combine(plan.Root, "b"));
        Assert.Equal(123u, alias.Uid);
        Assert.Equal(456u, alias.Gid);
        Assert.Equal(0x81ED, alias.Mode);
        var conflict = Create(false);
        Assert.Throws<InvalidDataException>(() => conflict.ApplyAttributes(
            [new("a", 123, 456, 0x1ED), new("b", 999, 456, 0x1ED)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ApplyAttributes_MalformedAccessAcl_RejectsPermissionChanges()
    {
        var plan = Create(true);
        Assert.Throws<InvalidDataException>(() => plan.ApplyAttributes([new("a", 0, 0, 0x1ED)],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ApplyUserXattrs_Hardlink_UsesOriginalGuardsAndRejectsConflictingValues()
    {
        var plan = Create(false);
        plan.ApplyUserXattrs([new("a", "user.test", [1], true), new("b", "user.test", [1], true)],
            TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1 }, plan.Get(Path.Combine(plan.Root, "b")).Xattrs!["user.test"]);
        var conflict = Create(false);
        Assert.Throws<InvalidDataException>(() => conflict.ApplyUserXattrs(
            [new("a", "user.test", []), new("b", "user.test", null)], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("security.selinux")]
    [InlineData("system.posix_acl_access")]
    [InlineData("user.")]
    public void ApplyUserXattrs_UnsupportedName_Rejects(string name)
    {
        Assert.Throws<InvalidDataException>(() => Create(false).ApplyUserXattrs([new("a", name, [1])],
            TestContext.Current.CancellationToken));
    }

    private static FileSystemEditPlan Create(bool acl)
    {
        Dictionary<string, string> projection = new(StringComparer.Ordinal) { ["a"] = "file:identical", ["b"] = "file:identical" };
        var manifest = new FileSystemPreservationManifest
        {
            Projection = projection,
            ParsedEntries =
            [
                new() { Path = "", Mode = 0x41ED, Inode = 1 },
                new() { Path = "a", Mode = 0x81A4, Inode = 2, Xattrs = acl ? new() { ["system.posix_acl_access"] = [1] } : null },
                new() { Path = "b", Mode = 0x81A4, Inode = 2 }
            ]
        };
        return new FileSystemEditPlan(Path.Combine(Path.GetTempPath(), "attribute-plan"), manifest, projection);
    }
}