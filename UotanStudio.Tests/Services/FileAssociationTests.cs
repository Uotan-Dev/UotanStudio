using System.Xml.Linq;
using UotanStudio.Services;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Verifies the platform-neutral parts of file-association wiring: the built-in formats are always kept,
/// the persisted selection is normalized and deduplicated, and the generated Linux/macOS manifests carry
/// every selected extension. No real registry, LaunchServices or XDG database is touched.
/// <para>验证文件关联接线的平台无关部分：内置格式始终保留、持久化选择被规范化与去重、生成的 Linux/macOS
/// 清单包含所有选定扩展名。不触碰真实注册表、LaunchServices 或 XDG 数据库。</para>
/// </summary>
public sealed class FileAssociationTests
{
    [Fact]
    public void BuildSpecs_NullSelection_ContainsOnlyBuiltInFormats()
    {
        var specs = FileAssociationService.BuildSpecs(null);

        Assert.Equal(3, specs.Count);
        Assert.Contains(specs, s => s.Extension == ".uotan" && s.ProgId == "UotanStudio.uotan");
        Assert.Contains(specs, s => s.Extension == ".uotanfw" && s.ProgId == "UotanStudio.uotanfw");
        Assert.Contains(specs, s => s.Extension == ".uotannode" && s.ProgId == "UotanStudio.uotannode");
    }

    [Fact]
    public void BuildSpecs_Selection_AddsNormalizedExtensions()
    {
        var specs = FileAssociationService.BuildSpecs(["IMG", ".dtb", ".pac"]);

        Assert.Equal(6, specs.Count);
        Assert.Contains(specs, s => s.Extension == ".img" && s.ProgId == "UotanStudio.img");
        Assert.Contains(specs, s => s.Extension == ".dtb" && s.ProgId == "UotanStudio.dtb");
        Assert.Contains(specs, s => s.Extension == ".pac" && s.ProgId == "UotanStudio.pac");
    }

    [Fact]
    public void BuildSpecs_Deduplicates_BuiltInsAndCaseVariants()
    {
        var specs = FileAssociationService.BuildSpecs([".uotan", ".IMG", "img", ".img"]);

        Assert.Equal(4, specs.Count);
        Assert.Single(specs, s => s.Extension == ".uotan");
        Assert.Single(specs, s => s.Extension == ".img");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("IMG", ".img")]
    [InlineData(".DTBO", ".dtbo")]
    [InlineData(" .pac ", ".pac")]
    [InlineData("bad ext", "")]
    public void NormalizeExtension_HandlesUserInput(string? raw, string expected) =>
        Assert.Equal(expected, FileAssociationService.NormalizeExtension(raw));

    [Fact]
    public void MimeTypeFor_UsesDottedApplicationNamespace()
    {
        Assert.Equal("application/x-uotanstudio-img", FileAssociationService.MimeTypeFor(".img"));
        Assert.Equal("application/x-uotanstudio-logcat", FileAssociationService.MimeTypeFor("LOGCAT"));
    }

    [Fact]
    public void BuildMimeXml_DeclaresEverySpec_WithWellFormedXml()
    {
        var specs = FileAssociationService.BuildSpecs([".img", ".logcat"]);

        var xml = FileAssociationService.BuildMimeXml(specs);
        var document = XDocument.Parse(xml);
        var namespaceName = "http://www.freedesktop.org/standards/shared-mime-info";
        var mimeTypes = document.Descendants(XName.Get("mime-type", namespaceName)).ToList();

        Assert.Equal(specs.Count, mimeTypes.Count);
        Assert.All(specs, spec =>
        {
            var mime = FileAssociationService.MimeTypeFor(spec.Extension);
            Assert.Contains(mimeTypes, element => (string?)element.Attribute("type") == mime);
            Assert.Contains(mimeTypes, element =>
                element.Elements(XName.Get("glob", namespaceName))
                    .Any(glob => (string?)glob.Attribute("pattern") == "*" + spec.Extension));
        });
    }

    [Fact]
    public void BuildDesktopEntry_ListsEveryMimeType_AndQuotesExecutable()
    {
        var specs = FileAssociationService.BuildSpecs([".img", ".dtb"]);
        var executable = @"""C:\Program Files\UotanStudio\UotanStudio.Desktop.exe""";

        var entry = FileAssociationService.BuildDesktopEntry(specs, executable);

        Assert.Contains("[Desktop Entry]", entry, StringComparison.Ordinal);
        Assert.Contains("MimeType=application/x-uotanstudio-uotan;application/x-uotanstudio-uotanfw;", entry, StringComparison.Ordinal);
        Assert.Contains("MimeType=", entry, StringComparison.Ordinal);
        Assert.Contains("application/x-uotanstudio-img;", entry, StringComparison.Ordinal);
        Assert.Contains("application/x-uotanstudio-dtb;", entry, StringComparison.Ordinal);
        Assert.Contains("Exec=", entry, StringComparison.Ordinal);
        Assert.Contains("%f", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMacDocumentTypes_UsesTheBundleIdentifier_ForEverySpec()
    {
        var specs = FileAssociationService.BuildSpecs([".img", ".logcat"]);

        var types = FileAssociationService.BuildMacDocumentTypes(specs);

        Assert.Equal(specs.Count, types.Count);
        Assert.All(types, type =>
            Assert.Equal("club.openatom.uotanstudio.desktop", type.BundleIdentifier));
        Assert.Contains(types, type => type.Extension == ".img");
        Assert.Contains(types, type => type.Extension == ".logcat");
    }

    [Fact]
    public void BuildMacDocumentTypes_PreservesSpecOrder()
    {
        var specs = FileAssociationService.BuildSpecs([".pac", ".img"]);

        var types = FileAssociationService.BuildMacDocumentTypes(specs);

        Assert.Equal(specs.Select(s => s.Extension), types.Select(t => t.Extension));
    }

    [Fact]
    public void IsPermissionFailure_UnauthorizedAccess_IsTrue() =>
        Assert.True(FileAssociationService.IsPermissionFailure(new UnauthorizedAccessException("denied")));

    [Fact]
    public void IsPermissionFailure_OtherFailure_IsFalse() =>
        Assert.False(FileAssociationService.IsPermissionFailure(new InvalidOperationException("other")));

    [Theory]
    [InlineData(5)]
    [InlineData(740)]
    public void IsPermissionFailure_AccessDeniedWin32Code_IsTrue(int nativeCode) =>
        Assert.True(FileAssociationService.IsPermissionFailure(
            new System.ComponentModel.Win32Exception(nativeCode, "denied")));

    [Fact]
    public void IsPermissionFailure_UnrelatedWin32Code_IsFalse() =>
        Assert.False(FileAssociationService.IsPermissionFailure(
            new System.ComponentModel.Win32Exception(2, "not found")));

    [Fact]
    public void FileAssociationResult_Factories_CarryStatusAndReason()
    {
        Assert.Equal(FileAssociationStatus.Succeeded, FileAssociationResult.Succeeded(4).Status);
        Assert.Equal(4, FileAssociationResult.Succeeded(4).AssociationCount);
        Assert.Equal(FileAssociationStatus.ElevationRequired, FileAssociationResult.ElevationRequired("uac").Status);
        Assert.Equal("uac", FileAssociationResult.ElevationRequired("uac").Message);
        Assert.Equal(FileAssociationStatus.Failed, FileAssociationResult.Failed("boom").Status);
        Assert.Equal("boom", FileAssociationResult.Failed("boom").Message);
        Assert.Equal(FileAssociationStatus.Unsupported, FileAssociationResult.Unsupported().Status);
    }

    [Fact]
    public void ElevatedCommand_RoundTripsResultFile()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "result.txt");
        var expected = new FileAssociationResult(FileAssociationStatus.Failed, "registry access denied", 0);
        File.WriteAllText(path, $"{expected.Status}\n{expected.AssociationCount}\n{expected.Message}");

        var actual = FileAssociationElevatedCommand.ReadResult(path);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ElevatedCommand_ParsesExtensionsAfterResultPair()
    {
        var args = new[]
        {
            FileAssociationElevatedCommand.RegisterSwitch,
            FileAssociationElevatedCommand.ResultSwitch, "C:\\temp\\r.txt",
            ".img", ".dtb",
        };

        Assert.True(FileAssociationElevatedCommand.IsRequested(args));
        Assert.Equal("C:\\temp\\r.txt", FileAssociationElevatedCommand.FindValue(args, FileAssociationElevatedCommand.ResultSwitch));
        Assert.Equal([".img", ".dtb"], FileAssociationElevatedCommand.ReadExtensions(args));
    }
}