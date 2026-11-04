using UotanStudio.Comm.Protocols.Edl.Programmer;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Cases for the file names the programmer folder scan admits: a newer programmer ships as a multi-ELF
/// <c>.melf</c> container, so the naming convention has to admit that extension as well.
/// <para>programmer 目录扫描接纳的文件名用例：较新的 programmer 以多 ELF 的 <c>.melf</c> 容器发布，因此命名约定也必须
/// 接纳该扩展名。</para>
/// </summary>
public sealed class EdlProgrammerNameTests
{
    private const string HardwareId = "0011aabbccddeeff";
    private const string HashPrefix = "0a0b0c0d0e0f1011";

    [Theory]
    [InlineData(".bin")]
    [InlineData(".mbn")]
    [InlineData(".elf")]
    [InlineData(".melf")]
    public void TryParseProgrammerName_AdmitsEveryProgrammerExtension(string extension)
    {
        var accepted = EdlProgrammerFolderScanner.TryParseProgrammerName(
            $"{HardwareId}_{HashPrefix}_ddr{extension}", out var identity, out var refusal);

        Assert.True(accepted, refusal);
        Assert.NotNull(identity);
    }

    [Fact]
    public void TryParseProgrammerName_RejectsAnExtensionThatIsNotAProgrammer()
    {
        var accepted = EdlProgrammerFolderScanner.TryParseProgrammerName(
            $"{HardwareId}_{HashPrefix}_ddr.img", out var identity, out var refusal);

        Assert.False(accepted);
        Assert.Null(identity);
        Assert.Contains(".melf", refusal, StringComparison.Ordinal);
    }
}