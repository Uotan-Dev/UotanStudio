using UotanStudio.Comm.Protocols.Mtk;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed class MtkDirectoryKeyStoreTests : IDisposable
{
    private static readonly MtkDeviceInfo Device = new(0x766, 0x8A00, 0xCA01, 2, 0x17);

    private readonly string _directory = Directory.CreateTempSubdirectory("uotan-mtk-keys-").FullName;

    [Fact]
    public async Task OpenAsync_MatchingResourceFile_ReturnsOwnedCopyOfTheFileBytes()
    {
        var expected = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        File.WriteAllBytes(Path.Combine(_directory, "daa_auth_0766.bin"), expected);
        var store = new MtkDirectoryKeyStore(_directory);

        using var resource = await store.OpenAsync(Device, MtkBootResourceKind.Authentication,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected.Length, resource.Length);
        var actual = new byte[resource.Length];
        Assert.Equal(expected.Length, await resource.Stream.ReadAsync(actual, TestContext.Current.CancellationToken));
        Assert.Equal(expected, actual);
        File.Delete(Path.Combine(_directory, "daa_auth_0766.bin"));
        Assert.Equal(expected.Length, resource.Length);
    }

    [Fact]
    public async Task OpenAsync_AbsentResourceFile_ThrowsWithHardwareCode()
    {
        var store = new MtkDirectoryKeyStore(_directory);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenAsync(
            Device, MtkBootResourceKind.RootCertificate, TestContext.Current.CancellationToken));

        Assert.Contains("0x0766", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAsync_UnsupportedKind_RejectsBeforeTouchingTheDirectory()
    {
        var store = new MtkDirectoryKeyStore(_directory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.OpenAsync(
            Device, (MtkBootResourceKind)9, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}