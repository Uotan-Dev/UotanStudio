using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

public sealed class NewProjectSelectionTests
{
    [Fact]
    public void SplitSelectedEntries_UpdateBin_RoutesComponentsToPartitions()
    {
        var container = new NewProjectImageEntry(@"C:\rom\update.bin", "update.bin");
        NewProjectImageEntry[] selected =
        [
            container,
            new("system", "img", container.FilePath!, false),
            new("board_list", "list", container.FilePath!, false)
        ];

        var (zipMembers, partitions) = NewProjectWindow.SplitSelectedEntries(selected,
            ContainerKind.UpdateBin);

        Assert.Empty(zipMembers);
        Assert.Equal(["system", "board_list"], partitions);
    }

    [Fact]
    public void SplitSelectedEntries_Zip_RoutesMembersAndPayloadPartitionsSeparately()
    {
        var container = new NewProjectImageEntry(@"C:\rom\ota.zip", "zip");
        NewProjectImageEntry[] selected =
        [
            container,
            new("images/system.img", "img", container.FilePath!, false),
            new("boot", "payload", container.FilePath!, true, "boot", "payload.bin")
        ];

        var (zipMembers, partitions) = NewProjectWindow.SplitSelectedEntries(selected,
            ContainerKind.Zip);

        Assert.Equal(["images/system.img"], zipMembers);
        Assert.Equal(["boot"], partitions);
    }

    [Fact]
    public void SplitSelectedEntries_Super_RoutesEveryEntryToPartitions()
    {
        var container = new NewProjectImageEntry(@"C:\rom\super.img", "super");
        NewProjectImageEntry[] selected =
        [
            container,
            new("system", "super", container.FilePath!, false)
        ];

        var (zipMembers, partitions) = NewProjectWindow.SplitSelectedEntries(selected,
            ContainerKind.Super);

        Assert.Empty(zipMembers);
        Assert.Equal(["system"], partitions);
    }
}