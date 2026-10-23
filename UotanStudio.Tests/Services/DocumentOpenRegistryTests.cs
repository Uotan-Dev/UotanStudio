using UotanStudio.Messages;
using UotanStudio.Services.Ui.Workspace.Documents;
using UotanStudio.Services.Workspace.Documents;
using UotanStudio.UiAbstractions;
using UotanStudio.ViewModels.Compare;
using UotanStudio.ViewModels.Editors;
using Xunit;

namespace UotanStudio.Tests.Services;

public class DocumentOpenRegistryTests
{
    [Fact]
    public void DocumentScopeKinds_MatchCurrentSessionCaptureSet()
    {
        var expected = new[]
        {
            "Text", "Firmware", "FileManager", "Dtb", "Dex", "Elf", "Qualcomm",
            "Manifest", "Arsc", "Hex", "Apk", "Partition", "Compare"
        };

        var actual = DocumentOpenRegistry.All
            .Where(d => d.IsDocument)
            .Select(d => d.Kind)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected.OrderBy(k => k, StringComparer.Ordinal), actual);
    }

    [Fact]
    public void ByMode_ExplicitModes_MapToRestoreSwitchKinds()
    {
        var expected = new (EditorOpenMode Mode, string Kind)[]
        {
            (EditorOpenMode.Text, "Text"),
            (EditorOpenMode.Elf, "Elf"),
            (EditorOpenMode.Qualcomm, "Qualcomm"),
            (EditorOpenMode.Qcn, "Qcn"),
            (EditorOpenMode.Partition, "Partition"),
            (EditorOpenMode.Log, "Log"),
            (EditorOpenMode.Hex, "Hex"),
            (EditorOpenMode.Dtb, "Dtb"),
            (EditorOpenMode.Dex, "Dex"),
            (EditorOpenMode.Axml, "Manifest"),
            (EditorOpenMode.Arsc, "Arsc"),
            (EditorOpenMode.Apk, "Apk"),
            (EditorOpenMode.Firmware, "Firmware"),
            (EditorOpenMode.FileSystem, "FileManager")
        };

        foreach (var (mode, kind) in expected)
        {
            var descriptor = DocumentOpenRegistry.ByMode(mode);
            Assert.NotNull(descriptor);
            Assert.Equal(kind, descriptor!.Kind);
        }
    }

    [Fact]
    public void MenuEntries_Order_MatchesExplorerContextMenuSequence()
    {
        var expected = new[]
        {
            EditorOpenMode.Elf, EditorOpenMode.Qualcomm, EditorOpenMode.Qcn,
            EditorOpenMode.Partition, EditorOpenMode.Text, EditorOpenMode.Log,
            EditorOpenMode.Hex, EditorOpenMode.Dtb, EditorOpenMode.Dex,
            EditorOpenMode.Axml, EditorOpenMode.Arsc, EditorOpenMode.Apk,
            EditorOpenMode.Firmware, EditorOpenMode.FileSystem
        };

        var actual = DocumentOpenRegistry.MenuEntries.Select(d => d.Mode!.Value).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MenuEntries_LocaleKeys_MatchExplorerOpenAsResources()
    {
        var expected = new[]
        {
            "Explorer_OpenAsElf", "Explorer_OpenAsQualcomm", "Explorer_OpenAsQcn",
            "Explorer_OpenAsPartition", "Explorer_OpenAsText", "Explorer_OpenAsLog",
            "Explorer_OpenAsHex", "Explorer_OpenAsDtb", "Explorer_OpenAsDex",
            "Explorer_OpenAsAxml", "Explorer_OpenAsArsc", "Explorer_OpenAsApk",
            "Explorer_OpenAsFirmware", "Explorer_OpenAsFileSystem"
        };

        var actual = DocumentOpenRegistry.MenuEntries.Select(d => d.LocaleKey).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ByViewModelType_CaptureTypes_MapToCurrentKindPairing()
    {
        var expected = new (Type ViewModelType, string Kind)[]
        {
            (typeof(FileViewModel), "Text"),
            (typeof(FirmwareInfoViewModel), "Firmware"),
            (typeof(FileManagerEditorViewModel), "FileManager"),
            (typeof(DtbEditorViewModel), "Dtb"),
            (typeof(DexEditorViewModel), "Dex"),
            (typeof(ElfInfoViewModel), "Elf"),
            (typeof(QualcommImageViewModel), "Qualcomm"),
            (typeof(ManifestEditorViewModel), "Manifest"),
            (typeof(ArscEditorViewModel), "Arsc"),
            (typeof(HexEditorViewModel), "Hex"),
            (typeof(ApkEditorViewModel), "Apk"),
            (typeof(PartitionEditorViewModel), "Partition"),
            (typeof(CompareViewModel), "Compare")
        };

        foreach (var (viewModelType, kind) in expected)
        {
            var descriptor = DocumentOpenRegistry.ByViewModelType(viewModelType);
            Assert.NotNull(descriptor);
            Assert.Equal(kind, descriptor!.Kind);
            Assert.True(descriptor.IsDocument);
        }
    }

    [Fact]
    public void ByKind_UnknownKind_ReturnsNull()
    {
        Assert.Null(DocumentOpenRegistry.ByKind("NoSuchKind"));
        Assert.Null(DocumentOpenRegistry.ByMode(EditorOpenMode.Auto));
    }

    [Fact]
    public void NonDocumentDescriptors_OutsideCaptureScope_MarkUndocumented()
    {
        foreach (var kind in new[] { "Qcn", "Log", "ScreenMirror" })
        {
            var descriptor = DocumentOpenRegistry.ByKind(kind);
            Assert.NotNull(descriptor);
            Assert.False(descriptor!.IsDocument);
        }
    }

    [Fact]
    public void OpenDocumentRequestMessage_PositionalCtor_RoundTripsPathAndMode()
    {
        var explicitMode = new OpenDocumentRequestMessage("x", EditorOpenMode.Dtb);
        Assert.Equal("x", explicitMode.Path);
        Assert.Equal(EditorOpenMode.Dtb, explicitMode.Mode);

        var defaulted = new OpenDocumentRequestMessage("y");
        Assert.Equal("y", defaulted.Path);
        Assert.Equal(EditorOpenMode.Auto, defaulted.Mode);
    }

    [Theory]
    [InlineData("boot.txt", EditorOpenMode.Text)]
    [InlineData("boot.dts", EditorOpenMode.Text)]
    [InlineData("boot.img", EditorOpenMode.Firmware)]
    [InlineData("boot.dtb", EditorOpenMode.Dtb)]
    [InlineData("classes.dex", EditorOpenMode.Dex)]
    [InlineData("lib.jar", EditorOpenMode.Dex)]
    [InlineData("resources.arsc", EditorOpenMode.Arsc)]
    [InlineData("app.apk", EditorOpenMode.Apk)]
    [InlineData("backup.qcn", EditorOpenMode.Qcn)]
    [InlineData("BACKUP.QCN", EditorOpenMode.Qcn)]
    [InlineData("console.log", EditorOpenMode.Log)]
    [InlineData("blob.dat", EditorOpenMode.Firmware)]
    [InlineData("archive.xyz", EditorOpenMode.Auto)]
    public void ResolveMode_PathOnlyRoutes_MatchesDelegatedDocumentFormatPolicy(string path, EditorOpenMode expected)
    {
        Assert.Equal(expected, DocumentOpenRegistry.ResolveMode(path));
        Assert.Equal(DocumentOpenRegistry.ResolveMode(path), DocumentFormatPolicy.ResolveMode(path));
    }

    [Fact]
    public void OrderByApplicability_QcnFile_PlacesQcnEntryFirst()
    {
        var (entries, applicable) = DocumentOpenRegistry.OrderByApplicability(
            DocumentOpenRegistry.MenuEntries, "backup.qcn", null);

        Assert.True(applicable > 0);
        Assert.Equal(EditorOpenMode.Qcn, entries[0].Mode);
        Assert.Equal(DocumentOpenRegistry.MenuEntries.Count, entries.Count);
    }

    [Fact]
    public void OrderByApplicability_PlainTextFile_KeepsRegistryOrderWithoutSeparatorBoundary()
    {
        var (entries, applicable) = DocumentOpenRegistry.OrderByApplicability(
            DocumentOpenRegistry.MenuEntries, "notes.txt", null);

        Assert.Equal(0, applicable);
        Assert.Equal(DocumentOpenRegistry.MenuEntries.Select(d => d.Kind), entries.Select(d => d.Kind));
    }

    [Fact]
    public void OrderByApplicability_SuppliedHeader_OverridesDiskContent()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-order-" + Guid.NewGuid().ToString("N") + ".bin");
        byte[] dtb = [0xD0, 0x0D, 0xFE, 0xED, 0x00, 0x00, 0x00, 0x28];
        var blank = new byte[8];
        try
        {
            File.WriteAllBytes(path, dtb);

            var (fromDisk, fromDiskApplicable) = DocumentOpenRegistry.OrderByApplicability(
                DocumentOpenRegistry.MenuEntries, path, null);
            var (fromBlankHeader, blankApplicable) = DocumentOpenRegistry.OrderByApplicability(
                DocumentOpenRegistry.MenuEntries, path, blank);

            Assert.Contains(fromDisk.Take(fromDiskApplicable), entry => entry.Mode == EditorOpenMode.Dtb);
            Assert.DoesNotContain(fromBlankHeader.Take(blankApplicable), entry => entry.Mode == EditorOpenMode.Dtb);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}