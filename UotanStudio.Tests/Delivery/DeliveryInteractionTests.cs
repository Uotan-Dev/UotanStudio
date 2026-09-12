using UotanStudio.Services.Delivery;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Delivery;

public sealed class DeliveryInteractionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "delivery-interaction-" + Guid.NewGuid().ToString("N"));
    public DeliveryInteractionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private DeliveryFile Input(string name, int size = 8192)
    {
        var path = Path.Combine(_root, name + ".img");
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(i => (byte)(i * 37)).ToArray());
        return new() { SourcePath = path, FileName = name + ".img", Partition = name, SourceLength = size };
    }

    [Fact]
    public void DeselectRemovesRowFromBothLists()
    {
        var vm = new DeliveryViewModel();
        var boot = Input("boot");
        var vendor = Input("vendor");
        vm.SelectSources([boot, vendor]);
        Assert.Equal(2, vm.Output.Count);
        vm.SelectSources([boot]);
        Assert.Single(vm.Output);
        Assert.Equal("boot", vm.Output[0].PartitionName);

        // Rows moved into the container must also disappear on deselection.
        // 进入容器的行在取消勾选后同样要被移除。
        vm.SetMode(true, super: true);
        Assert.True(vm.EditingContainer);
        var system = Input("system");
        vm.SelectSources([boot, system]);
        Assert.Contains(vm.Container, i => i.PartitionName == "system");
        vm.SelectSources([system]);
        Assert.DoesNotContain(vm.Container, i => i.PartitionName == "boot");
        Assert.Contains(vm.Container, i => i.PartitionName == "system");
    }

    [Fact]
    public void OutputRowsPreserveExplicitAlternativeSelection()
    {
        var vm = new DeliveryViewModel();
        var boot = Input("boot") with { Partition = "init_boot", Optional = true, Default = true };
        var alt = Input("boot_ksu") with { Partition = "init_boot", Optional = true };
        var system = Input("system");
        vm.SelectSources([boot]);
        Assert.True(vm.Output[0].IsOptional);
        vm.SelectSources([boot, alt]);
        var rows = vm.Output.Where(i => i.PartitionName == "init_boot").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.True(r.IsOptional));
        Assert.Equal(1, rows.Count(r => r.IsDefault));

        // Same name and partition stays mandatory.
        // 同名同分区保持必选。
        vm.SelectSources([system]);
        Assert.Contains(vm.Output, i => i.PartitionName == "system" && !i.IsOptional);
    }

    [Fact]
    public void VabSplitAddsZeroSizeCompanionOnce()
    {
        var wind = Input("wind");
        var options = new DeliverySuperOptions { AutoZero = true, DeviceType = "VAB" };
        var partitions = DeliverySuperBuilder.ExpandPartitions([wind], options);
        Assert.Equal(wind.SourcePath, Assert.Single(partitions, i => i.Partition == "wind_a").SourcePath);
        Assert.Empty(Assert.Single(partitions, i => i.Partition == "wind_b").SourcePath);
        var expanded = DeliverySuperBuilder.ExpandPartitions([.. partitions, Input("vendor")], options);
        Assert.Single(expanded, i => i.Partition == "wind_b");
        Assert.Contains(expanded, i => i.Partition == "vendor_a");
    }

    [Fact]
    public void AOnlyKeepsOriginalPartitionName()
    {
        // The view disables AutoZero for A-only devices before passing options to the builder.
        var partitions = DeliverySuperBuilder.ExpandPartitions([Input("wind")], new() { AutoZero = false, DeviceType = "AOnly" });
        Assert.Equal("wind", Assert.Single(partitions).Partition);
    }

    [Fact]
    public void FindProjectsWithSettingsListsOnlyConfiguredProjects()
    {
        var with = Path.Combine(_root, "alpha");
        var without = Path.Combine(_root, "beta");
        Directory.CreateDirectory(Path.Combine(with, ".uotan"));
        Directory.CreateDirectory(without);
        File.WriteAllText(Path.Combine(with, ".uotan", "source-super.json"), "{}");
        var found = DeliverySourceSettings.FindProjectsWithSettings(_root);
        var names = found.Select(f => Path.GetFileName(f.ProjectRoot)).ToArray();
        Assert.Contains("alpha", names);
        Assert.DoesNotContain("beta", names);
        Assert.Empty(DeliverySourceSettings.FindProjectsWithSettings(Path.Combine(_root, "empty-missing")));
    }

    [Fact]
    public void EmptyBSourcePassesValidationOnlyWithAutoZero()
    {
        // VAB unpacking produces 0-byte B-slot images; with AutoZero they are legitimate zero-size partitions.
        // VAB 解包产生 0 字节 B 槽镜像；勾选自动零分区时它们是合法的零大小分区。
        var empty = Input("my_bigball_b", 0);
        var plan = new DeliveryPlan { OrganizeDirectory = false, PackSuper = true, ContainerConfirmed = true,
            Container = [empty], Super = new() { AutoZero = true, DeviceType = "VAB" } };
        DeliveryValidation.Validate(plan);
        Assert.Throws<InvalidDataException>(() => DeliveryValidation.Validate(plan with { Super = plan.Super with { AutoZero = false } }));
        Assert.Throws<InvalidDataException>(() => DeliveryValidation.Validate(plan with { Container = [Input("my_bigball_a", 0)] }));
    }

    [Fact]
    public void AutoZeroMaterializesReadOnlyBSlotCompanion()
    {
        var vm = new DeliveryViewModel { AutoZero = true };
        vm.SetMode(true, super: true);
        vm.SelectSources([Input("my_bigball_a")]);
        var companion = Assert.Single(vm.Container, i => i.PartitionName == "my_bigball_b");
        Assert.True(companion.IsAutoZero);
        Assert.Null(companion.FilePath);
        Assert.Equal(0L, companion.SizeBytes);
        Assert.False(companion.IsZeroSize);
        Assert.Equal(2, vm.Container.Count);
    }

    [Fact]
    public void AutoZeroMatchesSelectedEmptyImageByFileName()
    {
        var vm = new DeliveryViewModel { AutoZero = true };
        vm.SetMode(true, super: true);
        var big = Input("my_bigball_a");
        var empty = Input("my_bigball_b", 0);
        vm.SelectSources([big, empty]);
        var row = Assert.Single(vm.Container, i => i.PartitionName == "my_bigball_b");
        Assert.Equal(empty.SourcePath, row.FilePath);
        Assert.True(row.IsAutoZero);
        Assert.Equal(2, vm.Container.Count);
        // Deselecting the empty image falls back to a generated read-only zero-size entry.
        // 取消勾选空镜像后回退为自动生成的只读零大小条目。
        vm.SelectSources([big]);
        var generated = Assert.Single(vm.Container, i => i.PartitionName == "my_bigball_b");
        Assert.Null(generated.FilePath);
        Assert.True(generated.IsAutoZero);
    }

    [Fact]
    public void DisablingAutoZeroRemovesCompanionAndUnlocksEmptyRows()
    {
        var vm = new DeliveryViewModel { AutoZero = true };
        vm.SetMode(true, super: true);
        var big = Input("my_bigball_a");
        var empty = Input("my_bigball_b", 0);
        vm.SelectSources([big, empty]);
        vm.AutoZero = false;
        var row = Assert.Single(vm.Container, i => i.PartitionName == "my_bigball_b");
        Assert.Equal(empty.SourcePath, row.FilePath);
        Assert.False(row.IsAutoZero);
        vm.SelectSources([big]);
        Assert.DoesNotContain(vm.Container, i => i.PartitionName == "my_bigball_b");
    }
}
