using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

/// <summary>
/// The file cell of a partition row: only an operation that actually takes an image invites the user to pick one, so a
/// row that erases, reboots, switches a slot or merely names a partition keeps its file cell blank.
/// <para>分区行的文件列：只有真正需要镜像的操作才提示用户选择文件，因此擦除、重启、切换槽位或仅列出分区的行保持空白。</para>
/// </summary>
public sealed class FlashPartitionEntryFilePromptTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("flash", true)]
    [InlineData("FLASH", true)]
    [InlineData("Flash", true)]
    [InlineData("wipe_super", true)]
    [InlineData("WIPE_SUPER", true)]
    [InlineData("erase", false)]
    [InlineData("delete", false)]
    [InlineData("create", false)]
    [InlineData("set_active", false)]
    [InlineData("reboot", false)]
    public void CommandTakesFile_OnlyTheWriteCommandsTakeAnImage(string? command, bool expected) =>
        Assert.Equal(expected, FlashPartitionEntry.CommandTakesFile(command));

    [Fact]
    public void CanChooseFile_RowWithoutOperationFile_OffersThePrompt()
    {
        var row = new FlashPartitionEntry { Name = "boot", AcceptsFile = true };

        Assert.False(row.HasFileName);
        Assert.True(row.CanChooseFile);
    }

    [Fact]
    public void CanChooseFile_RowThatAlreadyNamesItsFile_KeepsThePromptHidden()
    {
        var row = new FlashPartitionEntry { Name = "boot", AcceptsFile = true, FileName = "boot.img" };

        Assert.True(row.HasFileName);
        Assert.False(row.CanChooseFile);
    }

    [Fact]
    public void CanChooseFile_RowWhoseOperationTakesNoFile_NeverOffersThePrompt()
    {
        var row = new FlashPartitionEntry { Name = "metadata", Command = "erase", AcceptsFile = false };

        Assert.False(row.CanChooseFile);

        // Choosing a file on such a row would promise a write the operation ignores, so the cell stays blank even after
        // the name is cleared again.
        // 这类行上选择文件会承诺一次操作并不执行的写入，因此即使名称再次被清空，单元格也保持空白。
        row.FileName = "metadata.img";
        Assert.False(row.CanChooseFile);
        row.FileName = string.Empty;
        Assert.False(row.CanChooseFile);
    }

    [Fact]
    public void CanChooseFile_ChangingFileNameOrAcceptsFile_NotifiesTheCell()
    {
        var row = new FlashPartitionEntry { Name = "boot", AcceptsFile = true };
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        row.FileName = "boot.img";
        row.AcceptsFile = false;

        Assert.Contains(nameof(FlashPartitionEntry.CanChooseFile), changed);
        Assert.Contains(nameof(FlashPartitionEntry.HasFileName), changed);
        Assert.Equal(2, changed.Count(name => name == nameof(FlashPartitionEntry.CanChooseFile)));
    }

    [Fact]
    public void NewRow_AcceptsAFileByDefault_SoPlainImageListsKeepThePrompt()
    {
        var row = new FlashPartitionEntry { Name = "boot" };

        Assert.True(row.AcceptsFile);
        Assert.True(row.CanChooseFile);
    }
}