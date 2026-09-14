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
    public void ResetPlanMaterializesAutoZeroCompanions()
    {
        // Restoring a persisted plan can assign the same AutoZero value already held, which never fires
        // OnAutoZeroChanged; Reset must still materialize the VAB B-slot companions.
        // 还原持久化计划时 AutoZero 值可能与当前值相同而不触发属性变化；Reset 仍须物化 VAB B 槽伴随条目。
        var vm = new DeliveryViewModel();
        var plan = new DeliveryPlan
        {
            OrganizeDirectory = true,
            PackSuper = true,
            Container = [Input("wind_a"), Input("vendor_a")],
            Super = new() { AutoZero = true, DeviceType = "VAB" }
        };
        vm.SetMode(true, super: true);
        vm.Reset(plan);
        Assert.True(vm.AutoZero);
        Assert.Contains(vm.Container, i => i.PartitionName == "wind_b" && i.SizeBytes == 0 && i.IsAutoZero);
        Assert.Contains(vm.Container, i => i.PartitionName == "vendor_b" && i.SizeBytes == 0 && i.IsAutoZero);
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
        var plan = new DeliveryPlan
        {
            OrganizeDirectory = false,
            PackSuper = true,
            ContainerConfirmed = true,
            Container = [empty],
            Super = new() { AutoZero = true, DeviceType = "VAB" }
        };
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
    public void AutoZeroWithSuperRenamesSuffixlessRowsAndMaterializesCompanion()
    {
        var vm = new DeliveryViewModel { AutoZero = true };
        vm.SetMode(true, super: true);
        var odm = Input("odm");
        vm.SelectSources([odm]);
        var row = Assert.Single(vm.Container, i => i.PartitionName == "odm_a");
        Assert.Equal(odm.SourcePath, row.FilePath);
        var companion = Assert.Single(vm.Container, i => i.PartitionName == "odm_b");
        Assert.Null(companion.FilePath);
        Assert.Equal(0L, companion.SizeBytes);
        Assert.True(companion.IsAutoZero);
    }

    [Fact]
    public void AutoZeroWithSuperKeepsSuffixlessRowWhenTargetExists()
    {
        var vm = new DeliveryViewModel { AutoZero = true };
        vm.SetMode(true, super: true);
        var existingA = Input("odm_a");
        var odm = Input("odm");
        vm.SelectSources([existingA, odm]);
        var row = Assert.Single(vm.Container, i => i.PartitionName == "odm");
        Assert.Equal(odm.SourcePath, row.FilePath);
        Assert.Single(vm.Container, i => i.PartitionName == "odm_a");
        // The pre-existing odm_a still yields its single odm_b; no duplicate companion is added.
        // 既有的 odm_a 仍生成唯一的 odm_b；不会追加重复的伴随条目。
        Assert.Single(vm.Container, i => i.PartitionName == "odm_b");
        Assert.Equal(3, vm.Container.Count);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AutoZeroOrSuperOffLeavesSuffixlessRowsUntouched(bool autoZero, bool superPacked)
    {
        var vm = new DeliveryViewModel { AutoZero = autoZero };
        vm.SetMode(true, super: superPacked);
        var odm = Input("odm");
        vm.SelectSources([odm]);
        // Non-container mode keeps the row in Output; either way it is never renamed and never gains a companion.
        // 非容器模式下该行留在 Output；无论哪种情况都不改名、不生成伴随条目。
        var target = vm.EditingContainer ? vm.Container : vm.Output;
        var row = Assert.Single(target, i => i.PartitionName == "odm");
        Assert.Equal(odm.SourcePath, row.FilePath);
        Assert.DoesNotContain(vm.Container.Concat(vm.Output), i => i.PartitionName == "odm_b");
    }

    [Fact]
    public void TogglingAutoZeroClearsTheCompositionList()
    {
        var vm = new DeliveryViewModel { AutoZero = true };
        vm.SetMode(true, super: true);
        var big = Input("my_bigball_a");
        var empty = Input("my_bigball_b", 0);
        vm.SelectSources([big, empty]);
        Assert.Contains(vm.Container, i => i.PartitionName == "my_bigball_a");
        // Toggling auto-zero clears the whole composition list per the new interaction contract.
        // 按新交互约定，切换自动零分区开关会清空整个编排列表。
        vm.AutoZero = false;
        Assert.Empty(vm.Container);
        // Toggling back on keeps the list empty; rows must be re-selected from the source list.
        // 重新打开后列表保持为空，需从源列表重新勾选。
        vm.AutoZero = true;
        Assert.Empty(vm.Container);
    }

    [Fact]
    public void PartitionNameEditRejectsInvalidAndDuplicateNames()
    {
        var vm = new DeliveryViewModel { AutoZero = false };
        vm.SetMode(true, super: true);
        var first = Input("boot");
        var second = Input("vendor");
        vm.SelectSources([first, second]);
        // Duplicate name rolls back to the previous value and reports a status hint.
        // 重名编辑回退到原值并给出状态提示。
        var vendorRow = Assert.Single(vm.Container, i => i.PartitionName == "vendor");
        vendorRow.PartitionName = "boot";
        Assert.Equal("vendor", vendorRow.PartitionName);
        Assert.Equal("Delivery_InvalidPartition", vm.Status);
        // Invalid characters roll back as well.
        // 非法字符同样回退。
        vendorRow.PartitionName = "bad name!";
        Assert.Equal("vendor", vendorRow.PartitionName);
        Assert.Equal("Delivery_InvalidPartition", vm.Status);
        // A valid unique edit sticks and clears nothing.
        // 合法且唯一的编辑正常生效。
        vendorRow.PartitionName = "vendor_b";
        Assert.Equal("vendor_b", vendorRow.PartitionName);
        Assert.Equal(2, vm.Container.Count);
    }
}