using System.Globalization;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class EdlGptFolderXmlInferenceTests
{
    private static readonly Guid PartitionType = new("e8857b27-652f-4a5f-9c8f-4d0e6b5a1c3d");

    [Fact]
    public async Task TryBuildAsync_QualcommFlatFolder_GeneratesPerLunPackagesWithBoundImages()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"gpt-infer-{Environment.TickCount64:X}");
        Directory.CreateDirectory(folder);
        try
        {
            var ablBytes = new byte[] { 1, 2, 3, 4 };
            var xblBytes = new byte[] { 5, 6, 7, 8 };
            File.WriteAllBytes(Path.Combine(folder, "abl.bin"), ablBytes);
            File.WriteAllBytes(Path.Combine(folder, "xbl.img"), xblBytes);
            GptFixtureImage.Write(Path.Combine(folder, "gpt_main0.bin"), 16 * 1024 * 1024,
            [
                new GptFixtureEntry(PartitionType, Guid.NewGuid(), 2048, 4095, "abl"),
                new GptFixtureEntry(PartitionType, Guid.NewGuid(), 4096, 8191, "missing_partition"),
            ]);
            GptFixtureImage.Write(Path.Combine(folder, "gpt_main3.bin"), 8 * 1024 * 1024,
            [
                new GptFixtureEntry(PartitionType, Guid.NewGuid(), 1024, 2047, "xbl"),
            ]);

            Assert.True(EdlGptFolderXmlInference.HasInferableLayout(folder));
            var packageSet = await EdlGptFolderXmlInference.TryBuildAsync(folder, TestContext.Current.CancellationToken);
            Assert.NotNull(packageSet);
            Assert.Equal(2, packageSet!.Packages.Count);
            Assert.Equal(folder, packageSet.DirectoryPath);
            Assert.Equal([0, 3], [.. packageSet.Packages.Select(package => package.Layout.Lun)]);

            foreach (var package in packageSet.Packages)
            {
                Assert.True(File.Exists(package.RawProgramPath));
                Assert.True(File.Exists(package.PatchPath));
                Assert.True(File.Exists(package.PartitionPath));
            }

            var rawProgram0 = File.ReadAllText(packageSet.Packages[0].RawProgramPath);
            Assert.Contains("abl.bin", rawProgram0, StringComparison.Ordinal);
            Assert.Contains("gpt_main0.bin", rawProgram0, StringComparison.Ordinal);
            Assert.Contains("gpt_backup0.bin", rawProgram0, StringComparison.Ordinal);
            Assert.Contains("filename=\"\" label=\"missing_partition\"", rawProgram0, StringComparison.Ordinal);

            var rawProgram3 = File.ReadAllText(packageSet.Packages[1].RawProgramPath);
            Assert.Contains("xbl.img", rawProgram3, StringComparison.Ordinal);
            Assert.Contains("gpt_main3.bin", rawProgram3, StringComparison.Ordinal);

            var partition0 = File.ReadAllText(packageSet.Packages[0].PartitionPath);
            Assert.Contains("abl.bin", partition0, StringComparison.Ordinal);
            Assert.Contains("SECTOR_SIZE_IN_BYTES = 512", partition0, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task TryBuildAsync_GrowPlaceholderAndSlotSuffixedImages_BindsBaseImageAndKeepsZeroRow()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"gpt-infer-grow-{Environment.TickCount64:X}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "system.bin"), [1, 2, 3]);
            GptFixtureImage.Write(Path.Combine(folder, "gpt_main0.bin"), 16 * 1024 * 1024,
            [
                new GptFixtureEntry(PartitionType, Guid.NewGuid(), 2048, 4095, "system_a"),
                new GptFixtureEntry(PartitionType, Guid.NewGuid(), 4096, 8191, "system_b"),
                new GptFixtureEntry(Guid.Empty, Guid.NewGuid(), 8192, 8191, "last_parti")
            ]);

            var packageSet = await EdlGptFolderXmlInference.TryBuildAsync(folder, TestContext.Current.CancellationToken);
            Assert.NotNull(packageSet);
            var package = Assert.Single(packageSet!.Packages);
            Assert.Equal(3, package.Layout.Partitions.Count);

            var rawProgram = File.ReadAllText(package.RawProgramPath);
            Assert.Contains("filename=\"system.bin\" label=\"system_a\"", rawProgram, StringComparison.Ordinal);
            Assert.Contains("filename=\"system.bin\" label=\"system_b\"", rawProgram, StringComparison.Ordinal);
            Assert.Contains(
                "filename=\"\" label=\"last_parti\" num_partition_sectors=\"0\"",
                rawProgram,
                StringComparison.Ordinal);

            var patch = File.ReadAllText(package.PatchPath);
            Assert.Contains("Update last partition 3 'last_parti'", patch, StringComparison.Ordinal);
            Assert.Contains("filename=\"gpt_main0.bin\"", patch, StringComparison.Ordinal);

            var partition = File.ReadAllText(package.PartitionPath);
            Assert.DoesNotContain("last_parti", partition, StringComparison.Ordinal);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(partition, "<partition ").Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void HasInferableLayout_WithoutSignatureOrTable_IsFalse()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"gpt-infer-none-{Environment.TickCount64:X}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "boot.img"), [1]);
            Assert.False(EdlGptFolderXmlInference.HasInferableLayout(folder));
            File.WriteAllBytes(Path.Combine(folder, "gpt_main0.bin"), [1, 2, 3, 4]);
            Assert.False(EdlGptFolderXmlInference.HasInferableLayout(folder));
            File.WriteAllBytes(Path.Combine(folder, "abl.bin"), [1]);
            Assert.True(EdlGptFolderXmlInference.HasInferableLayout(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}