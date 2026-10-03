using UotanStudio.Comm.Protocols.Mtk;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

/// <summary>
/// Pins the Legacy stage-two patch sites to the reference pattern bytes: the searched instructions, the short
/// replacements, and the exact length and immediates of the command-handler stub, so a transcription error in
/// either table is visible.
/// <para>把 Legacy 第二阶段补丁站点锁定到参考模式字节：被搜索的指令、短替换序列，以及命令处理桩的确切长度与立即数，
/// 因此任一模式表的抄写错误都会显现。</para>
/// </summary>
public static class MtkLegacyDaPatcherTests
{
    private const int StubLength = 54;
    private const string SecurityPatternHex = "08B54FF45042";
    private const string SecurityPatchHex = "08B5002008BD";
    private const string CommandPatternHex = "30B585B003AB";

    [Fact]
    public static void Patch_SecurityPatternPresent_ReplacesSixInstructionBytes()
    {
        var image = Compose(Filler(10), Convert.FromHexString(SecurityPatternHex), Filler(20));

        var result = MtkLegacyDaPatcher.Patch(image);

        Assert.True(result.SecurityCheckPatched);
        Assert.False(result.CommandHandlerPatched);
        Assert.True(result.IsPatched);
        Assert.Equal(Convert.FromHexString(SecurityPatchHex), result.Image.AsSpan(10, 6).ToArray());
        Assert.Equal(image[..10], result.Image[..10]);
        Assert.Equal(image[16..], result.Image[16..]);
    }

    [Fact]
    public static void Patch_CommandHandlerPatternPresent_WritesReferenceStubAtKnownLength()
    {
        var image = Compose(Filler(4), Convert.FromHexString(CommandPatternHex), Filler(80));

        var result = MtkLegacyDaPatcher.Patch(image);

        Assert.True(result.CommandHandlerPatched);
        Assert.False(result.SecurityCheckPatched);
        var stub = result.Image.AsSpan(4, StubLength);
        Assert.Equal([0x70, 0xB5], stub[..2].ToArray());
        Assert.Equal([0x4A, 0xF2, 0xC8, 0x64], stub.Slice(2, 4).ToArray());
        Assert.Equal([0xC8, 0xF2, 0x04, 0x04], stub.Slice(6, 4).ToArray());
        Assert.Equal([0xBD, 0xE8, 0x70, 0x40], stub[^6..^2].ToArray());
        Assert.Equal([0x18, 0x47], stub[^2..].ToArray());
        Assert.Equal(Filler(80)[(StubLength - 6)..], result.Image[(4 + StubLength)..]);
        Assert.Equal(-1, result.Image.AsSpan().IndexOf(Convert.FromHexString(CommandPatternHex)));
    }

    [Fact]
    public static void Patch_BothSitesPresent_PatchesBothAndLeavesTheInputUnchanged()
    {
        var image = Compose(Convert.FromHexString(SecurityPatternHex), Filler(8),
            Convert.FromHexString(CommandPatternHex), Filler(60));
        var original = image.ToArray();

        var result = MtkLegacyDaPatcher.Patch(image);

        Assert.True(result.SecurityCheckPatched);
        Assert.True(result.CommandHandlerPatched);
        Assert.Equal(original, image);
        Assert.Equal(Convert.FromHexString(SecurityPatchHex), result.Image[..6]);
    }

    [Fact]
    public static void Patch_NoPatternPresent_ReportsUnpatchedImage()
    {
        var image = Filler(120, 0x40);

        var result = MtkLegacyDaPatcher.Patch(image);

        Assert.False(result.IsPatched);
        Assert.Equal(image, result.Image);
    }

    [Fact]
    public static void Patch_CommandSiteTooCloseToImageEnd_IsRejected()
    {
        var image = Compose(Filler(30), Convert.FromHexString(CommandPatternHex), Filler(10));

        Assert.Throws<ArgumentException>(() => MtkLegacyDaPatcher.Patch(image));
    }

    [Fact]
    public static void TryPatch_DigestSlotPresent_ReattestsStageOneWithThePatchedStageTwo()
    {
        var stageTwo = Compose(Filler(20), Convert.FromHexString(SecurityPatternHex), Filler(102));
        var stageOne = Compose(Filler(64), System.Security.Cryptography.SHA256.HashData(stageTwo[..96]), Filler(48));

        var pair = MtkLegacyDaPatcher.TryPatch(stageOne, stageTwo, 32, 32, isVersion6: false);

        Assert.NotNull(pair);
        Assert.True(pair.SecurityCheckPatched);
        Assert.Equal(Convert.FromHexString(SecurityPatchHex), pair.StageTwo.AsSpan(20, 6).ToArray());
        Assert.Equal(stageTwo[96..], pair.StageTwo[96..]);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(pair.StageTwo[..96]),
            pair.StageOne.AsSpan(64, 32).ToArray());
        Assert.Equal(stageOne[..64], pair.StageOne[..64]);
    }

    [Fact]
    public static void TryPatch_WithoutDigestSlotOrWithoutPatch_ReportsNoPair()
    {
        var stageTwo = Compose(Filler(20), Convert.FromHexString(SecurityPatternHex), Filler(102));
        var unattested = Filler(256, 0x40);

        Assert.Null(MtkLegacyDaPatcher.TryPatch(unattested, stageTwo, 32, 32, isVersion6: false));

        var stageOne = Compose(Filler(64), System.Security.Cryptography.SHA256.HashData(stageTwo[..96]), Filler(48));
        Assert.Null(MtkLegacyDaPatcher.TryPatch(stageOne, Filler(128, 0x60), 32, 32, isVersion6: false));
    }

    private static byte[] Filler(int count, int seed = 1) =>
        [.. Enumerable.Range(0, count).Select(index => (byte)((index + seed) % 0xE0 + 1))];

    private static byte[] Compose(params byte[][] parts)
    {
        var buffer = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(buffer, offset);
            offset += part.Length;
        }

        return buffer;
    }
}