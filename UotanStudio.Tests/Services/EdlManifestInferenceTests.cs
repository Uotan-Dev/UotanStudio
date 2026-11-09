using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class EdlManifestInferenceTests
{
    private static readonly (uint ImageId, string FileName)[] KaanapaliEightPieceSet =
    [
        (36, "multi_image_qti.mbn"),
        (37, "multi_image.mbn"),
        (21, "xbl_sc.elf"),
        (60, "signed_firmware_soc_view.elf"),
        (59, "sequencer_ram.elf"),
        (61, "tme_config.elf"),
        (13, "prog_firehose_ddr.elf"),
        (38, "xbl_config_devprg.elf")
    ];

    private static string CreateTempFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "edl-infer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteProgrammer(string folder, string fileName, long length)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5a, (int)length).ToArray());
        return path;
    }

    private static string WriteVendorManifest(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var (_, fileName) in KaanapaliEightPieceSet)
        {
            File.WriteAllBytes(Path.Combine(folder, fileName), [0x11]);
        }

        var images = string.Join(Environment.NewLine, KaanapaliEightPieceSet.Select(image =>
            $"""    <image image_id="{image.ImageId}" image_path="{image.FileName}"/>"""));
        var path = Path.Combine(folder, "qsahara_device_programmer.xml");
        File.WriteAllText(path,
            $"""<sahara_config><chipset>kaanapali</chipset><images>{Environment.NewLine}{images}{Environment.NewLine}</images></sahara_config>""");
        return path;
    }

    [Fact]
    public void TryInfer_MissingOrBlankDirectory_ReturnsNull()
    {
        Assert.Null(EdlManifestInference.TryInfer(string.Empty, TestContext.Current.CancellationToken));
        Assert.Null(EdlManifestInference.TryInfer(
            Path.Combine(Path.GetTempPath(), "edl-infer-missing-" + Guid.NewGuid().ToString("N")),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TryInfer_FirehoseFileWithoutAuxiliaryMembers_ReturnsNull()
    {
        var root = CreateTempFolder();
        try
        {
            WriteProgrammer(root, "prog_firehose_ddr.elf", 8);

            var inferred = EdlManifestInference.TryInfer(root, TestContext.Current.CancellationToken);

            Assert.Null(inferred);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryInfer_AuxiliaryMembersWithoutFirehoseFile_ReturnsNull()
    {
        var root = CreateTempFolder();
        try
        {
            WriteProgrammer(root, "multi_image_qti.mbn", 8);
            WriteProgrammer(root, "multi_image.mbn", 8);
            WriteProgrammer(root, "xbl_sc.elf", 8);

            var inferred = EdlManifestInference.TryInfer(root, TestContext.Current.CancellationToken);

            Assert.Null(inferred);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryInfer_KaanapaliEightPieceSet_RecoversVendorImageIdsInManifestOrder()
    {
        var root = CreateTempFolder();
        try
        {
            foreach (var (imageId, fileName) in KaanapaliEightPieceSet)
            {
                WriteProgrammer(root, fileName, imageId);
            }

            var inferred = EdlManifestInference.TryInfer(root, TestContext.Current.CancellationToken);

            Assert.NotNull(inferred);
            Assert.Equal(Path.GetFullPath(root), inferred!.Directory);
            Assert.Equal(
                KaanapaliEightPieceSet.Select(image => (long)image.ImageId),
                inferred.Images.Select(image => (long)image.ImageId));
            Assert.Equal(
                KaanapaliEightPieceSet.Select(image => Path.GetFullPath(Path.Combine(root, image.FileName))),
                inferred.Images.Select(image => image.Path));
            Assert.Equal(
                KaanapaliEightPieceSet.Select(image => (long)image.ImageId),
                inferred.Images.Select(image => image.LengthBytes));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryInfer_VariantSuffixes_GeneralizesByStemPrefixWithoutSwallowingSiblingStems()
    {
        var root = CreateTempFolder();
        try
        {
            WriteProgrammer(root, "multi_image_qti_v2.mbn", 1);
            WriteProgrammer(root, "multi_image_v3.mbn", 2);
            WriteProgrammer(root, "xbl_sc_2024.elf", 3);
            WriteProgrammer(root, "prog_firehose_lite.elf", 4);
            WriteProgrammer(root, "xbl_config_v1.elf", 5);

            var inferred = EdlManifestInference.TryInfer(root, TestContext.Current.CancellationToken);

            Assert.NotNull(inferred);
            Assert.Equal(
                [36, 37, 21, 13, 38],
                inferred!.Images.Select(image => (long)image.ImageId));
            Assert.Equal("multi_image_qti_v2.mbn", Path.GetFileName(inferred.Images[0].Path));
            Assert.Equal("multi_image_v3.mbn", Path.GetFileName(inferred.Images[1].Path));
            Assert.Equal("xbl_sc_2024.elf", Path.GetFileName(inferred.Images[2].Path));
            Assert.Equal("prog_firehose_lite.elf", Path.GetFileName(inferred.Images[3].Path));
            Assert.Equal("xbl_config_v1.elf", Path.GetFileName(inferred.Images[4].Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryInfer_AmbiguousFirehoseNames_ClaimsFirstCandidateDeterministically()
    {
        var root = CreateTempFolder();
        try
        {
            WriteProgrammer(root, "prog_firehose_a.elf", 1);
            WriteProgrammer(root, "prog_firehose_b.elf", 2);
            WriteProgrammer(root, "multi_image.mbn", 3);

            var inferred = EdlManifestInference.TryInfer(root, TestContext.Current.CancellationToken);

            Assert.NotNull(inferred);
            Assert.Equal(
                [37, 13],
                inferred!.Images.Select(image => (long)image.ImageId));
            Assert.Equal("prog_firehose_a.elf", Path.GetFileName(inferred.Images[1].Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryInfer_CancelledToken_ThrowsOperationCanceled()
    {
        var root = CreateTempFolder();
        try
        {
            WriteProgrammer(root, "prog_firehose_ddr.elf", 1);
            WriteProgrammer(root, "multi_image.mbn", 1);

            Assert.Throws<OperationCanceledException>(() =>
                EdlManifestInference.TryInfer(root, new CancellationToken(canceled: true)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_FolderWithValidProgrammerManifest_ReturnsOnlyTheManifestAsCandidate()
    {
        var root = CreateTempFolder();
        try
        {
            var manifestPath = WriteVendorManifest(root);
            WriteProgrammer(root, "prog_firehose_ddr_late.elf", 9);

            var package = FlashBinPackage.Read(root);

            Assert.Equal(manifestPath, package.Programmer);
            Assert.Equal([manifestPath], package.Programmers);
            Assert.Null(package.InferredProgrammer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_RawprogramXmlIsNotAProgrammerManifest_KeepsFirehoseImageCandidates()
    {
        var root = CreateTempFolder();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "system.bin"), [1]);
            File.WriteAllBytes(Path.Combine(root, "prog_ufs_firehose_test.elf"), [3]);
            File.WriteAllText(Path.Combine(root, "rawprogram0.xml"),
                """<data><program filename="system.bin" label="system_a" start_sector="8" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="4096" physical_partition_number="0"/></data>""");

            var package = FlashBinPackage.Read(root);

            Assert.Equal(
                ["prog_ufs_firehose_test.elf"],
                package.Programmers.Select(Path.GetFileName));
            Assert.Null(package.InferredProgrammer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Read_MultiImageScatterWithoutManifest_ReportsInferredMappingAndKeepsFirehoseCandidate()
    {
        var root = CreateTempFolder();
        try
        {
            foreach (var (_, fileName) in KaanapaliEightPieceSet)
            {
                File.WriteAllBytes(Path.Combine(root, fileName), [0x22]);
            }

            var package = FlashBinPackage.Read(root);

            Assert.NotNull(package.InferredProgrammer);
            Assert.Equal(8, package.InferredProgrammer!.Images.Count);
            var firehoseImages = package.InferredProgrammer.Images
                .Where(image => image.ImageId == (uint)SaharaImageId.Firehose).ToArray();
            Assert.Single(firehoseImages);
            Assert.Equal("prog_firehose_ddr.elf", Path.GetFileName(firehoseImages[0].Path));
            Assert.Equal(
                KaanapaliEightPieceSet.Select(image => image.ImageId),
                package.InferredProgrammer.Images.Select(image => image.ImageId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnumerateProgrammers_ManifestReferencingMissingImage_IsRejectedAndScatterIsInferred()
    {
        var root = CreateTempFolder();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "qsahara_device_programmer.xml"),
                """<sahara_config><images><image image_id="13" image_path="absent_programmer.elf"/></images></sahara_config>""");
            WriteProgrammer(root, "prog_firehose_ddr.elf", 1);
            WriteProgrammer(root, "multi_image.mbn", 1);

            var programmers = FlashBinPackage.EnumerateProgrammers(root);

            Assert.Equal(
                ["prog_firehose_ddr.elf"],
                programmers.Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}