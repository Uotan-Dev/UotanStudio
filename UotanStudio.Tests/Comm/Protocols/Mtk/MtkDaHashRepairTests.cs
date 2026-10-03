using UotanStudio.Comm.Protocols.Mtk;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

/// <summary>
/// Pins the stage-one digest slot rules of the MediaTek download agent to the reference tool's order: the
/// digest of stage two without its signature suffix, then the digest of the whole image, then the v5 anchor
/// text, then the v6 tail slot. Digests come from an out-of-band SHA implementation over fixed fixtures, so the
/// assertions do not restate this repository's own reading of the format.
/// <para>把联发科第一阶段摘要槽位规则锁定到参考实现的查找顺序：先对去掉签名尾缀的第二阶段求摘要，再对完整镜像
/// 求摘要，然后按 v5 锚点文本、v6 尾部槽位查找。摘要取自对固定样例独立运行的 SHA 实现，因此断言不是在复述本仓库
/// 自己对该格式的理解。</para>
/// </summary>
public static class MtkDaHashRepairTests
{
    private const int StageTwoSignatureLength = 32;
    private const string Sha1OfUnsignedTail = "96D281BA44EB21ECFB1663C8AC5752C48686A927";
    private const string Sha256OfUnsignedTail = "08359B108FA567F5DCF319FA3434DA6ABBC1D595F426372666447F09CC5A87DC";
    private const string Sha1OfWholeImage = "E6434BC401F98603D7EDA504790C98C67385D535";
    private const string Sha256OfWholeImage = "471FB943AA23C511F6F72F8D1652D9C880CFA392AD80503120547703E56A2BE5";
    private static readonly byte[] Anchor = "MMU MAP: VA"u8.ToArray();

    [Fact]
    public static void Locate_StageTwoDigestStoredAtOffset_ReportsSignatureExcludedLength()
    {
        var stageOne = Compose(Pattern(64), FromHex(Sha256OfUnsignedTail), Pattern(48));

        var location = MtkDaHashRepair.Locate(stageOne, StageTwo(), 32, StageTwoSignatureLength, false);

        Assert.Equal(new MtkDaHashLocation(64, MtkDaHashMode.Sha256, 96), location);
    }

    [Fact]
    public static void Locate_OnlyWholeImageDigestPresent_ReportsWholeCoveredLength()
    {
        var stageOne = Compose(Pattern(16), FromHex(Sha1OfWholeImage), Pattern(8));

        var location = MtkDaHashRepair.Locate(stageOne, StageTwo(), 32, StageTwoSignatureLength, false);

        Assert.Equal(new MtkDaHashLocation(16, MtkDaHashMode.Sha1, 128), location);
    }

    [Fact]
    public static void Locate_NoDigestButVersionFiveAnchorWithWideSlot_ReportsSha256Position()
    {
        var stageOne = Compose(Pattern(32), ZeroTailedSlot(0x20, 28), Anchor, Pattern(16));

        var location = MtkDaHashRepair.Locate(stageOne, StageTwo(), 32, StageTwoSignatureLength, false);

        Assert.Equal(new MtkDaHashLocation(12, MtkDaHashMode.Sha256, 96), location);
    }

    [Fact]
    public static void Locate_AnchorTooCloseToStartForAWideSlot_ReportsSha1Position()
    {
        var stageOne = Compose(Pattern(8), ZeroTailedSlot(0x30, 16), Anchor);

        var location = MtkDaHashRepair.Locate(stageOne, StageTwo(), 32, StageTwoSignatureLength, false);

        Assert.Equal(new MtkDaHashLocation(4, MtkDaHashMode.Sha1, 96), location);
    }

    [Fact]
    public static void Locate_VersionSixTailSlotBehindSignature_ReportsTailPosition()
    {
        var stageOne = Compose(Pattern(120), ZeroTailedSlot(0x40, 0x2C), Pattern(32));

        var location = MtkDaHashRepair.Locate(stageOne, StageTwo(), 32, StageTwoSignatureLength, true);

        Assert.Equal(new MtkDaHashLocation(116, MtkDaHashMode.Sha256, 96), location);
    }

    [Fact]
    public static void Locate_NoAttestedSlot_ReturnsNullInsteadOfGuessing()
    {
        var stageOne = Pattern(256, 0x50);

        Assert.Null(MtkDaHashRepair.Locate(stageOne, StageTwo(), 32, StageTwoSignatureLength, false));
        Assert.Null(MtkDaHashRepair.Locate(stageOne, StageTwo(), 32, StageTwoSignatureLength, true));
    }

    [Fact]
    public static void Repair_RewritesOnlyTheSlotAndLeavesTheSourceImageIntact()
    {
        var stageOne = Compose(Pattern(64), Pattern(32, 0x60), Pattern(40));
        var source = stageOne.ToArray();
        var location = new MtkDaHashLocation(64, MtkDaHashMode.Sha256, 128);

        var repaired = MtkDaHashRepair.Repair(source, StageTwo(), location);

        Assert.Equal(Sha256OfWholeImage, Convert.ToHexString(repaired.AsSpan(64, 32)));
        Assert.Equal(source[..64], repaired[..64]);
        Assert.Equal(source[96..], repaired[96..]);
        Assert.Equal(source, stageOne);
        Assert.NotEqual(source, repaired);
    }

    [Fact]
    public static void Repair_SlotOutsideTheImages_IsRejected()
    {
        var stageOne = Pattern(16);

        Assert.Throws<ArgumentException>(() => MtkDaHashRepair.Repair(stageOne, StageTwo(),
            new MtkDaHashLocation(12, MtkDaHashMode.Sha256, 32)));
        Assert.Throws<ArgumentException>(() => MtkDaHashRepair.Repair(stageOne, StageTwo(),
            new MtkDaHashLocation(0, MtkDaHashMode.Sha1, 256)));
    }

    [Fact]
    public static void Locate_SignatureSuffixLongerThanTheImage_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => MtkDaHashRepair.Locate(Pattern(64), StageTwo(), 0, 200, false));
    }

    private static byte[] StageTwo() => [.. Enumerable.Range(0, 128).Select(index => (byte)index)];

    private static byte[] Pattern(int count, int seed = 1) =>
        [.. Enumerable.Range(0, count).Select(index => (byte)((index + seed) % 0xE0 + 1))];

    private static byte[] ZeroTailedSlot(int seed, int length)
    {
        var slot = Pattern(length, seed);
        slot.AsSpan()[^4..].Clear();
        return slot;
    }

    private static byte[] FromHex(string value) => Convert.FromHexString(value);

    private static byte[] Compose(params byte[][] parts)
    {
        var total = parts.Sum(part => part.Length);
        var buffer = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(buffer, offset);
            offset += part.Length;
        }

        return buffer;
    }
}