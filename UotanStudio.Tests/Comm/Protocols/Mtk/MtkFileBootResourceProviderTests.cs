using UotanStudio.Comm.Protocols.Mtk;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed class MtkFileBootResourceProviderTests : IDisposable
{
    private static readonly MtkDeviceInfo Device = new(0x766, 0x8A00, 0xCA01, 2, 0x17);

    private readonly string _directory = Directory.CreateTempSubdirectory("uotan-mtk-auth-").FullName;

    [Fact]
    public async Task OpenAsync_AuthenticationKind_ReturnsVerbatimFileBytesAsOwnedResource()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var path = Path.Combine(_directory, "auth.bin");
        File.WriteAllBytes(path, payload);
        var provider = new MtkFileBootResourceProvider(authenticationPath: path);

        using var resource = await provider.OpenAsync(Device, MtkBootResourceKind.Authentication,
            TestContext.Current.CancellationToken);

        Assert.Equal(payload.Length, resource.Length);
        var read = new byte[resource.Length];
        Assert.Equal(read.Length, await resource.Stream.ReadAtLeastAsync(read, read.Length, throwOnEndOfStream: false,
            TestContext.Current.CancellationToken));
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task OpenAsync_RootCertificateKind_UsesItsOwnFile()
    {
        var path = Path.Combine(_directory, "cert.bin");
        File.WriteAllBytes(path, new byte[] { 0xAA });
        var provider = new MtkFileBootResourceProvider(rootCertificatePath: path);

        using var resource = await provider.OpenAsync(Device, MtkBootResourceKind.RootCertificate,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, resource.Length);
    }

    [Fact]
    public async Task OpenAsync_KindWithoutSelection_Throws()
    {
        var provider = new MtkFileBootResourceProvider(authenticationPath:
            Path.Combine(_directory, "auth.bin"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAsync(
            Device, MtkBootResourceKind.RootCertificate, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenAsync_MissingFile_Throws()
    {
        var provider = new MtkFileBootResourceProvider(authenticationPath:
            Path.Combine(_directory, "absent.bin"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAsync(
            Device, MtkBootResourceKind.Authentication, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Constructor_NoPathSelected_Throws()
    {
        Assert.Throws<ArgumentException>(() => new MtkFileBootResourceProvider());
        Assert.Throws<ArgumentException>(() => new MtkFileBootResourceProvider(" ", null));
    }

    [Fact]
    public async Task OpenAsync_DisposedResource_ReleasesTheStream()
    {
        var path = Path.Combine(_directory, "auth.bin");
        File.WriteAllBytes(path, new byte[] { 0x01 });
        var provider = new MtkFileBootResourceProvider(authenticationPath: path);
        var resource = await provider.OpenAsync(Device, MtkBootResourceKind.Authentication,
            TestContext.Current.CancellationToken);

        resource.Dispose();
        resource.Dispose();

        Assert.Throws<ObjectDisposedException>(() => resource.Stream);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}