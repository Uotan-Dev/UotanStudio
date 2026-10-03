using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Hash algorithm a download agent uses to attest its stage-two image.
/// <para>下载代理用于担保第二阶段镜像的摘要算法。</para></summary>
public enum MtkDaHashMode
{
    /// <summary>SHA-1 digest, twenty bytes. <para>SHA-1 摘要，二十字节。</para></summary>
    Sha1 = 1,

    /// <summary>SHA-256 digest, thirty-two bytes. <para>SHA-256 摘要，三十二字节。</para></summary>
    Sha256 = 2
}

/// <summary>Where a stage-one download agent attests the stage-two image and how many bytes are covered.
/// <para>第一阶段下载代理对第二阶段镜像的担保位置，以及被覆盖的字节数。</para></summary>
/// <param name="Position">Byte offset of the stored digest inside stage one.
/// <para>第一阶段内已存储摘要的字节偏移。</para></param>
/// <param name="Mode">Digest algorithm. <para>摘要算法。</para></param>
/// <param name="HashedLength">Stage-two byte count covered by the digest.
/// <para>摘要覆盖的第二阶段字节数。</para></param>
public readonly record struct MtkDaHashLocation(int Position, MtkDaHashMode Mode, int HashedLength);

/// <summary>
/// Locates and repairs the stage-two digest embedded in a stage-one MediaTek download agent.
/// <para>定位并修复联发科第一阶段下载代理内嵌的第二阶段摘要。</para>
/// </summary>
/// <remarks>
/// <para>A download agent verifies its stage-two image against a digest stored inside stage one. Whenever the
/// uploaded stage-two bytes change, that stored digest must be recomputed, so this is the shared step behind
/// every patched-agent startup. Location follows the reference order exactly: hash of stage two without its
/// signature suffix, then hash of the whole stage-two image, then the v5 anchor text, then the v6 tail slot.
/// The anchor and tail heuristics only count as a hit when the candidate digest ends with four zero bytes,
/// which is how the reference tool distinguishes a real digest slot from arbitrary data. When nothing matches,
/// callers get <see langword="null"/> instead of a guessed position.</para>
/// <para>下载代理会用第一阶段内保存的摘要校验第二阶段镜像；只要上传的第二阶段字节发生变化，该摘要就必须重新计算，
/// 因此这是所有修补代理启动流程的共同步骤。定位顺序与参考实现完全一致：先对去掉签名尾缀的第二阶段求摘要，再对
/// 完整第二阶段求摘要，然后按 v5 锚点文本查找，最后按 v6 尾部槽位查找。锚点与尾部启发式只在候选摘要末尾为四个
/// 零字节时才算命中，这是参考工具区分真实摘要槽与任意数据的方式；都不匹配时调用方得到 <see langword="null"/>，
/// 而不会得到猜测的位置。</para>
/// </remarks>
public static class MtkDaHashRepair
{
    private static readonly byte[] StageTwoAnchor = "MMU MAP: VA"u8.ToArray();
    private const int TailDigestLength = 0x30;

    /// <summary>Finds the digest slot that covers <paramref name="stageTwo"/> inside
    /// <paramref name="stageOne"/>.
    /// <para>在 <paramref name="stageOne"/> 中查找覆盖 <paramref name="stageTwo"/> 的摘要槽位。</para></summary>
    /// <param name="stageOne">The stage-one image as it will be uploaded.
    /// <para>将要上传的第一阶段镜像。</para></param>
    /// <param name="stageTwo">The stage-two image as it will be uploaded.
    /// <para>将要上传的第二阶段镜像。</para></param>
    /// <param name="stageOneSignatureLength">Trailing signature length of stage one, used by the v6 tail rule.
    /// <para>第一阶段的尾部签名长度，供 v6 尾部规则使用。</para></param>
    /// <param name="stageTwoSignatureLength">Trailing signature length of stage two, excluded from the first
    /// candidate digest. <para>第二阶段的尾部签名长度，第一个候选摘要会排除该段。</para></param>
    /// <param name="isVersion6">Whether the loader container reports the v6 DA layout.
    /// <para>加载器容器是否声明 v6 DA 布局。</para></param>
    /// <returns>The located slot, or <see langword="null"/> when no candidate is attested.
    /// <para>定位到的槽位；无任何担保候选时为 <see langword="null"/>。</para></returns>
    public static MtkDaHashLocation? Locate(ReadOnlySpan<byte> stageOne, ReadOnlySpan<byte> stageTwo,
        int stageOneSignatureLength, int stageTwoSignatureLength, bool isVersion6)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stageOneSignatureLength);
        ArgumentOutOfRangeException.ThrowIfNegative(stageTwoSignatureLength);
        var hashedLength = stageTwo.Length - stageTwoSignatureLength;
        if (hashedLength < 0)
        {
            throw new ArgumentException("The stage-two signature suffix is longer than the image.",
                nameof(stageTwoSignatureLength));
        }

        if (FindDigest(stageOne, stageTwo[..hashedLength]) is (int position, MtkDaHashMode mode))
        {
            return new MtkDaHashLocation(position, mode, hashedLength);
        }

        hashedLength = stageTwo.Length;
        if (FindDigest(stageOne, stageTwo) is (int fullPosition, MtkDaHashMode fullMode))
        {
            return new MtkDaHashLocation(fullPosition, fullMode, hashedLength);
        }

        if (!isVersion6)
        {
            hashedLength = stageTwo.Length - stageTwoSignatureLength;
            return FindVersion5Slot(stageOne) is (int anchorPosition, MtkDaHashMode anchorMode)
                ? new MtkDaHashLocation(anchorPosition, anchorMode, hashedLength)
                : null;
        }

        hashedLength = stageTwo.Length - stageTwoSignatureLength;
        return FindVersion6Slot(stageOne, stageOneSignatureLength) is (int tailPosition, MtkDaHashMode tailMode)
            ? new MtkDaHashLocation(tailPosition, tailMode, hashedLength)
            : null;
    }

    /// <summary>Returns a copy of <paramref name="stageOne"/> whose digest slot covers
    /// <paramref name="stageTwo"/>.
    /// <para>返回 <paramref name="stageOne"/> 的副本，其摘要槽位改为担保 <paramref name="stageTwo"/>。</para></summary>
    /// <param name="stageOne">The unmodified stage-one image. <para>未被修改的第一阶段镜像。</para></param>
    /// <param name="stageTwo">The stage-two image to attest. <para>需要担保的第二阶段镜像。</para></param>
    /// <param name="location">The slot produced by <see cref="Locate"/>.
    /// <para>由 <see cref="Locate"/> 得到的槽位。</para></param>
    /// <returns>A newly allocated stage-one image with the digest rewritten.
    /// <para>重写了摘要的新分配第一阶段镜像。</para></returns>
    /// <exception cref="ArgumentException">The slot does not fit the images it describes.
    /// <para>该槽位与它所描述的镜像不匹配。</para></exception>
    public static byte[] Repair(ReadOnlySpan<byte> stageOne, ReadOnlySpan<byte> stageTwo,
        MtkDaHashLocation location)
    {
        var digestLength = DigestLength(location.Mode);
        if (location.Position < 0 || location.HashedLength < 0 || location.HashedLength > stageTwo.Length
            || location.Position > stageOne.Length - digestLength)
        {
            throw new ArgumentException("The hash slot does not fit the supplied images.", nameof(location));
        }

        var repaired = stageOne.ToArray();
        var digest = ComputeDigest(stageTwo[..location.HashedLength], location.Mode);
        digest.CopyTo(repaired.AsSpan(location.Position));
        return repaired;
    }

    private static (int Position, MtkDaHashMode Mode)? FindDigest(ReadOnlySpan<byte> stageOne,
        ReadOnlySpan<byte> covered)
    {
        var sha1 = SHA1.HashData(covered);
        var index = stageOne.IndexOf((ReadOnlySpan<byte>)sha1);
        if (index >= 0)
        {
            return (index, MtkDaHashMode.Sha1);
        }

        var sha256 = SHA256.HashData(covered);
        index = stageOne.IndexOf((ReadOnlySpan<byte>)sha256);
        return index >= 0 ? (index, MtkDaHashMode.Sha256) : null;
    }

    private static (int Position, MtkDaHashMode Mode)? FindVersion5Slot(ReadOnlySpan<byte> stageOne)
    {
        var anchor = stageOne.IndexOf((ReadOnlySpan<byte>)StageTwoAnchor);
        if (anchor < 0)
        {
            return null;
        }

        if (anchor >= TailDigestLength && HasZeroTail(stageOne[(anchor - TailDigestLength)..anchor]))
        {
            return (anchor - TailDigestLength, MtkDaHashMode.Sha256);
        }

        var digestLength = DigestLength(MtkDaHashMode.Sha1);
        return anchor >= digestLength && HasZeroTail(stageOne[(anchor - digestLength)..anchor])
            ? (anchor - digestLength, MtkDaHashMode.Sha1)
            : null;
    }

    private static (int Position, MtkDaHashMode Mode)? FindVersion6Slot(ReadOnlySpan<byte> stageOne,
        int stageOneSignatureLength)
    {
        var position = stageOne.Length - stageOneSignatureLength - TailDigestLength;
        if (position < 0)
        {
            return null;
        }

        return HasZeroTail(stageOne.Slice(position, TailDigestLength))
            ? (position, MtkDaHashMode.Sha256)
            : null;
    }

    private static bool HasZeroTail(ReadOnlySpan<byte> candidate) =>
        candidate[^4..].SequenceEqual((ReadOnlySpan<byte>)[0, 0, 0, 0]);

    private static byte[] ComputeDigest(ReadOnlySpan<byte> covered, MtkDaHashMode mode) =>
        mode == MtkDaHashMode.Sha1 ? SHA1.HashData(covered) : SHA256.HashData(covered);

    private static int DigestLength(MtkDaHashMode mode) => mode == MtkDaHashMode.Sha1 ? 20 : 32;
}