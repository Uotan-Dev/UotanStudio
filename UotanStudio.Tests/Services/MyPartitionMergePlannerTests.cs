using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class MyPartitionMergePlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-mymargeplanner-" + Guid.NewGuid().ToString("N"));

    public MyPartitionMergePlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void DerivePartitionName_ContainerEntries_UsesNameVerbatim()
    {
        Assert.Equal("my_product", MyPartitionMergePlanner.DerivePartitionName("my_product", containerPartitionEntry: true));
        Assert.Equal("System", MyPartitionMergePlanner.DerivePartitionName("System", containerPartitionEntry: true));
        Assert.Null(MyPartitionMergePlanner.DerivePartitionName("payload.bin", containerPartitionEntry: true));
        Assert.Null(MyPartitionMergePlanner.DerivePartitionName("super.img", containerPartitionEntry: true));
    }

    [Fact]
    public void DerivePartitionName_FileEntries_StripsPathAndExtension()
    {
        Assert.Equal("my_product", MyPartitionMergePlanner.DerivePartitionName("firmware-update/my_product.img", containerPartitionEntry: false));
        Assert.Equal("my_bigball", MyPartitionMergePlanner.DerivePartitionName("my_bigball.br", containerPartitionEntry: false));
        Assert.Equal("system", MyPartitionMergePlanner.DerivePartitionName("system.img", containerPartitionEntry: false));
        Assert.Equal("my_region", MyPartitionMergePlanner.DerivePartitionName(@"archive\my_region.img", containerPartitionEntry: false));
        Assert.Null(MyPartitionMergePlanner.DerivePartitionName("system.transfer.list", containerPartitionEntry: false));
        Assert.Null(MyPartitionMergePlanner.DerivePartitionName("", containerPartitionEntry: false));
    }

    [Fact]
    public void ShouldOffer_BothUnpackOptionsWithSystemAndMyPartition_IsTrue()
    {
        Assert.True(MyPartitionMergePlanner.ShouldOffer(true, true, ["system", "my_bigball"]));
        Assert.True(MyPartitionMergePlanner.ShouldOffer(true, true, ["my_product", "vendor", "System"]));
        Assert.True(MyPartitionMergePlanner.ShouldOffer(true, true, ["SYSTEM", "MY_CARRIER"]));
    }

    [Fact]
    public void ShouldOffer_MissingRequirement_IsFalse()
    {
        Assert.False(MyPartitionMergePlanner.ShouldOffer(false, true, ["system", "my_bigball"]));
        Assert.False(MyPartitionMergePlanner.ShouldOffer(true, false, ["system", "my_bigball"]));
        Assert.False(MyPartitionMergePlanner.ShouldOffer(true, true, ["my_bigball"]));
        Assert.False(MyPartitionMergePlanner.ShouldOffer(true, true, ["system", "vendor", "product"]));
        Assert.False(MyPartitionMergePlanner.ShouldOffer(true, true, []));
    }

    [Fact]
    public void ShouldOffer_SlotSuffixedNames_PairsSystemWithSameSlotMyPartition()
    {
        Assert.True(MyPartitionMergePlanner.ShouldOffer(true, true, ["system_a", "my_product_a"]));
        Assert.True(MyPartitionMergePlanner.ShouldOffer(true, true, ["System_B", "vendor_b", "my_carrier_b"]));
        Assert.True(MyPartitionMergePlanner.ShouldOffer(true, true, ["system", "my_product_a"]));
        Assert.True(MyPartitionMergePlanner.ShouldOffer(true, true, ["system_a", "my_engineering"]));
    }

    [Fact]
    public void ShouldOffer_OtherSlotMyPartition_IsFalse()
    {
        Assert.False(MyPartitionMergePlanner.ShouldOffer(true, true, ["system_a", "my_product_b"]));
        Assert.False(MyPartitionMergePlanner.ShouldOffer(true, true, ["system_a", "vendor_a", "my_region_b"]));
    }

    [Fact]
    public void ShouldOffer_SuffixedSystemVariantIsNotTheSystemPartition_IsFalse()
    {
        Assert.False(MyPartitionMergePlanner.ShouldOffer(true, true, ["system_ext_a", "my_product_a"]));
        Assert.False(MyPartitionMergePlanner.ShouldOffer(true, true, ["system_dlkm", "my_product"]));
    }

    [Fact]
    public void DiscoverTrees_MixedLayout_FindsSystemAndSortedMyTrees()
    {
        Directory.CreateDirectory(Path.Combine(_root, "system"));
        Directory.CreateDirectory(Path.Combine(_root, "my_product"));
        Directory.CreateDirectory(Path.Combine(_root, "my_carrier"));
        Directory.CreateDirectory(Path.Combine(_root, "vendor"));
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        Directory.CreateDirectory(Path.Combine(_root, ".uotan"));

        var (systemTree, myTrees) = MyPartitionMergePlanner.DiscoverTrees(_root);

        Assert.Equal(Path.Combine(_root, "system"), systemTree);
        Assert.Equal(
            [Path.Combine(_root, "my_carrier"), Path.Combine(_root, "my_product")],
            myTrees);
    }

    [Fact]
    public void DiscoverTrees_MissingSystem_ReturnsNullWithMyTrees()
    {
        Directory.CreateDirectory(Path.Combine(_root, "my_region"));

        var (systemTree, myTrees) = MyPartitionMergePlanner.DiscoverTrees(_root);

        Assert.Null(systemTree);
        Assert.Equal([Path.Combine(_root, "my_region")], myTrees);
    }

    [Fact]
    public void DiscoverTrees_SlotLayout_KeepsOnlySameSlotMyTrees()
    {
        Directory.CreateDirectory(Path.Combine(_root, "system_a"));
        Directory.CreateDirectory(Path.Combine(_root, "my_product_a"));
        Directory.CreateDirectory(Path.Combine(_root, "my_product_b"));
        Directory.CreateDirectory(Path.Combine(_root, "my_carrier"));
        Directory.CreateDirectory(Path.Combine(_root, "vendor_a"));

        var (systemTree, myTrees) = MyPartitionMergePlanner.DiscoverTrees(_root);

        Assert.Equal(Path.Combine(_root, "system_a"), systemTree);
        Assert.Equal(
            [Path.Combine(_root, "my_carrier"), Path.Combine(_root, "my_product_a")],
            myTrees);
    }

    [Fact]
    public void DiscoverTrees_BothSlotsUnpacked_TargetsFirstSystemByName()
    {
        Directory.CreateDirectory(Path.Combine(_root, "system_b"));
        Directory.CreateDirectory(Path.Combine(_root, "system_a"));
        Directory.CreateDirectory(Path.Combine(_root, "my_product_a"));
        Directory.CreateDirectory(Path.Combine(_root, "my_product_b"));

        var (systemTree, myTrees) = MyPartitionMergePlanner.DiscoverTrees(_root);

        Assert.Equal(Path.Combine(_root, "system_a"), systemTree);
        Assert.Equal([Path.Combine(_root, "my_product_a")], myTrees);
    }

    [Fact]
    public void FindPartitionImageFiles_MatchesBaseNameAcrossExtensions()
    {
        var images = Directory.CreateDirectory(Path.Combine(_root, "images")).FullName;
        File.WriteAllText(Path.Combine(images, "my_product.img"), "x");
        File.WriteAllText(Path.Combine(images, "my_product.br"), "x");
        File.WriteAllText(Path.Combine(images, "my_product.new.dat"), "x");
        File.WriteAllText(Path.Combine(images, "vendor.img"), "x");

        var files = MyPartitionMergePlanner.FindPartitionImageFiles(images, "my_product");

        Assert.Equal(
            [Path.Combine(images, "my_product.br"), Path.Combine(images, "my_product.img")],
            files);
    }

    [Fact]
    public void FindPartitionImageFiles_MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(MyPartitionMergePlanner.FindPartitionImageFiles(Path.Combine(_root, "missing"), "my_product"));
    }
}