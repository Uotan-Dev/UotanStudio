using UotanStudio.Services.Keys;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class KeyManagerPurposeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-key-purpose-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PurposeEntries_ExposeJarAndApkInTheExpectedOrder()
    {
        var model = new KeyManagerViewModel(new KeyLibraryService(Path.Combine(_root, "library")));

        Assert.Equal(["ADB", "HDC", "Payload", "JAR", "APK"], model.Kinds);

        model.KindIndex = 3;
        Assert.True(model.IsJar);
        Assert.True(model.IsLibraryPurpose);
        Assert.False(model.IsApkCertificate);
        Assert.Equal([2048, 4096], model.PayloadSizes);

        model.KindIndex = 4;
        Assert.False(model.IsJar);
        Assert.False(model.IsLibraryPurpose);
        Assert.True(model.IsApkCertificate);
    }

    [Fact]
    public async Task JarPurpose_GeneratesManagedJarKey()
    {
        var library = new KeyLibraryService(Path.Combine(_root, "library"));
        var model = new KeyManagerViewModel(library)
        {
            KindIndex = 3,
            Name = "jar-signing",
            PayloadBits = 2048,
        };

        await model.GenerateCommand.ExecuteAsync(null);

        var key = Assert.Single(library.List());
        Assert.Equal(ManagedKeyKind.Jar, key.Kind);
        Assert.Equal("jar-signing", key.Name);
    }

    [Fact]
    public async Task ApkPurpose_UsesDedicatedCertificateGenerator()
    {
        var library = new KeyLibraryService(Path.Combine(_root, "library"));
        var output = Path.Combine(_root, "exports");
        var model = new KeyManagerViewModel(library)
        {
            KindIndex = 4,
            Name = "apk-signing",
            CertificateAlias = "apk-signing",
            FormatIndex = (int)ApkCertificateExportFormat.Pem,
            OutputDirectory = output,
        };

        await model.GenerateCommand.ExecuteAsync(null);

        var key = Assert.Single(library.List());
        Assert.Equal(ManagedKeyKind.Apk, key.Kind);
        Assert.True(File.Exists(Path.Combine(output, "apk-signing.pk8")), model.Status);
        Assert.True(File.Exists(Path.Combine(output, "apk-signing.pem")), model.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
