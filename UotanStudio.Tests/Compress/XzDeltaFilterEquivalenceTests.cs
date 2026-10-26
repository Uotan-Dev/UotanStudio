using UotanStudio.Compress.Codecs.Xz;
using Xunit;

namespace UotanStudio.Tests.Compress;

/// <summary>
/// Pins the vectorized inverse XZ delta filter against its byte-at-a-time reference: the in-place chain
/// <c>v[i] = buffer[i] + v[i - distance]</c> and the history-ring refresh from the last <c>distance</c> outputs must
/// reproduce the reference bytes and the cross-chunk ring state for every distance, length and chunk split.
/// <para>固定向量化逆 XZ 差分过滤器与其逐字节参考实现的一致性：就地链 <c>v[i] = buffer[i] + v[i - distance]</c>
/// 与用最后 <c>distance</c> 个输出回填 history 环的做法，必须在任意步长、长度与分块切分下复现参考字节与跨块
/// 环状态。</para>
/// </summary>
public class XzDeltaFilterEquivalenceTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 3)]
    [InlineData(1, 4096)]
    [InlineData(4, 1)]
    [InlineData(4, 3)]
    [InlineData(4, 63)]
    [InlineData(4, 257)]
    [InlineData(16, 1)]
    [InlineData(16, 65_537)]
    [InlineData(32, 300_000)]
    [InlineData(64, 129)]
    [InlineData(255, 5)]
    [InlineData(256, 4097)]
    [InlineData(256, 700_000)]
    public void Transform_AnyDistanceAndLength_MatchesScalarReference(int distance, int length)
    {
        var data = BuildDeterministic(length);
        var reference = new byte[length];
        data.CopyTo(reference);

        var vectorized = new XzDeltaFilter(distance);
        var scalar = new XzDeltaFilter(distance);
        vectorized.Transform(data, length, 0);
        scalar.TransformScalar(reference, length);

        Assert.Equal(reference, data);
    }

    [Theory]
    [InlineData(4, 1000)]
    [InlineData(64, 1000)]
    [InlineData(256, 1000)]
    [InlineData(3, 10_000)]
    public void Transform_SplitChunks_CarryRingStateLikeReference(int distance, int total)
    {
        var data = BuildDeterministic(total);
        var reference = new byte[total];
        data.CopyTo(reference);
        var vectorized = new XzDeltaFilter(distance);
        var scalar = new XzDeltaFilter(distance);
        int[] splits = [1, distance - 1, distance, distance + 1, total / 2, total];

        var position = 0;
        foreach (var split in splits)
        {
            var chunk = Math.Min(split, total - position);
            if (chunk <= 0)
            {
                continue;
            }

            vectorized.Transform(data.AsSpan(position, chunk), chunk, position);
            scalar.TransformScalar(reference.AsSpan(position, chunk), chunk);
            position += chunk;
        }

        if (position < total)
        {
            vectorized.Transform(data.AsSpan(position), total - position, position);
            scalar.TransformScalar(reference.AsSpan(position), total - position);
        }

        Assert.Equal(reference, data);

        var followUp = BuildDeterministic(total + 79_197);
        var followReference = new byte[followUp.Length];
        followUp.CopyTo(followReference);
        vectorized.Transform(followUp, followUp.Length, 0);
        scalar.TransformScalar(followReference, followReference.Length);
        Assert.Equal(followReference, followUp);
    }

    private static byte[] BuildDeterministic(int length)
        => [.. Enumerable.Range(0, length).Select(index => (byte)(index * 31 + (index >> 5) * 7 + 5))];
}