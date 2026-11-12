using System.Text;
using UotanStudio.Comm.Protocols.Edl.Programmer;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Covers the authentication refusal marker a programmer carries and the descriptor declaration the folder
/// scan derives from it.
/// <para>覆盖 programmer 携带的认证拒绝标记，以及目录扫描据此写入的描述符声明。</para>
/// </summary>
public sealed class EdlProgrammerAuthMarkerTests
{
    private const string Refusal =
        "ERROR: Only nop and sig tag can be recevied before authentication.";

    [Fact]
    public void Detect_MarkerInsideLeadingBytes_ReturnsTrue()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes($"padding {Refusal} padding"));

        Assert.True(EdlProgrammerAuthMarker.Detect(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Detect_MarkerSplitAcrossChunkBoundary_ReturnsTrue()
    {
        var marker = EdlProgrammerAuthMarker.Marker.ToArray();
        var bytes = new byte[64 * 1024 + marker.Length];
        marker.CopyTo(bytes, bytes.Length - marker.Length);

        using var stream = new MemoryStream(bytes);

        Assert.True(EdlProgrammerAuthMarker.Detect(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Detect_OrdinaryBytes_ReturnsFalse()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("ELF firehose programmer without the gate"));

        Assert.False(EdlProgrammerAuthMarker.Detect(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Scan_ProgrammerCarryingMarker_DeclaresVendorAuthentication()
    {
        var result = ScanFolder(payload => payload + Refusal);

        var descriptor = Assert.Single(result.Descriptors);
        Assert.True(descriptor.RequiresVendorAuthentication);
    }

    [Fact]
    public void Scan_ProgrammerWithoutMarker_LeavesTheDeclarationUnset()
    {
        var result = ScanFolder(payload => payload);

        var descriptor = Assert.Single(result.Descriptors);
        Assert.Null(descriptor.RequiresVendorAuthentication);
    }

    private static EdlProgrammerFolderScanResult ScanFolder(Func<string, string> compose)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"uotan-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var name = "0011aabbccddeeff_0a0b0c0d0e0f1011_ddr.elf";
            File.WriteAllBytes(Path.Combine(directory, name),
                Encoding.ASCII.GetBytes(compose("ELF firehose programmer body ")));

            return EdlProgrammerFolderScanner.Scan(directory, 16, 8 * 1024 * 1024,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}