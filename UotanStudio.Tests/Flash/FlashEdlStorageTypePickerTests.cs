using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.ViewModels.Flash;
using Xunit;

namespace UotanStudio.Tests.Flash;

public sealed class FlashEdlStorageTypePickerTests
{
    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, FirehoseStorageType.Emmc)]
    [InlineData(1, FirehoseStorageType.Ufs)]
    [InlineData(2, null)]
    public void StorageTypeForSelection_EachEntry_MapsToItsMedia(int selectedIndex, FirehoseStorageType? expected)
        => Assert.Equal(expected, FlashViewModel.StorageTypeForSelection(selectedIndex));

    [Fact]
    public void QualcommSection_DeclaresTheStorageTypePickerTheViewModelReads()
    {
        var root = RepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "UotanStudio", "Views", "Shell", "FlashView.axaml"));
        var operations = File.ReadAllText(Path.Combine(
            root, "UotanStudio", "ViewModels", "Flash", "FlashViewModel.Operations.cs"));
        var viewBridge = File.ReadAllText(Path.Combine(
            root, "UotanStudio", "ViewModels", "Flash", "FlashViewModel.ViewBridge.cs"));

        Assert.Contains("Name=\"EdlStorageTypeBox\"", view, StringComparison.Ordinal);
        Assert.Contains("Text=\"{DynamicResource Flash_EdlStorageType}\"", view, StringComparison.Ordinal);
        Assert.Contains("Field<ComboBox>(\"EdlStorageTypeBox\")", operations, StringComparison.Ordinal);
        Assert.Contains("StorageTypeForSelection(Field<ComboBox>(\"EdlStorageTypeBox\").SelectedIndex)",
            operations, StringComparison.Ordinal);
        Assert.Contains("Field<ComboBox>(\"EdlStorageTypeBox\").ItemsSource", viewBridge, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageTypeEntries_KeepTheOrderAndDefaultTheSelectionMappingAssumes()
    {
        var root = RepositoryRoot();
        var operations = File.ReadAllText(Path.Combine(
            root, "UotanStudio", "ViewModels", "Flash", "FlashViewModel.Operations.cs"));
        const string declaration = "private readonly LocalizedOptionSet _edlStorageTypeOptions";
        var start = operations.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, "the storage-media option set is missing");
        var block = operations[start..operations.IndexOf(");", start, StringComparison.Ordinal)];

        var emmc = block.IndexOf("LocalizedOption.FromText(\"eMMC\")", StringComparison.Ordinal);
        var ufs = block.IndexOf("LocalizedOption.FromText(\"UFS\")", StringComparison.Ordinal);
        Assert.True(emmc >= 0 && emmc < ufs,
            "the picker order must stay eMMC, UFS to match StorageTypeForSelection");

        var viewBridge = File.ReadAllText(Path.Combine(
            root, "UotanStudio", "ViewModels", "Flash", "FlashViewModel.ViewBridge.cs"));
        Assert.Contains("Field<ComboBox>(\"EdlStorageTypeBox\").SelectedIndex = 1", viewBridge,
            StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UotanStudio.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}