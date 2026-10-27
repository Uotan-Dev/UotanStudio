using Xunit;

using RegisterCrc32 = UotanStudio.Hash.Crc32Register.Crc32Register;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Drives concurrent register-form CRC-32 continuations whose zero-run matrix lengths differ, so a torn cache
/// would pair one caller's length with another caller's matrix and silently corrupt the result. Every thread
/// repeats its own length long enough for the cache to be rewritten mid-flight, and each computed value must
/// still match the single-threaded slice-by-8 reference for the same input.
/// <para>并发驱动零串矩阵长度互不相同的寄存器形式 CRC-32 续算，撕裂的缓存会把一个调用方的长度配上另一个
/// 调用方的矩阵并静默算错结果。每个线程以足够多的次数重复自己的长度，使缓存在运行中被反复改写，而每次
/// 计算结果仍必须与相同输入的单线程 slice-by-8 参考值逐位一致。</para>
/// </summary>
public class Crc32RegisterConcurrentZeroMatrixTests
{
    [Fact]
    public void Compute_ConcurrentDistinctLengths_MatchesPortableTableWalk()
    {
        var payload = new byte[300_000];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 71 + 29);
        }

        var lengths = new[] { 128 * 1024, 131_072 + 512, 200_000, 262_144, 299_999 };
        var references = new uint[lengths.Length];
        for (var i = 0; i < lengths.Length; i++)
        {
            references[i] = RegisterCrc32.ComputeSoftware(payload.AsSpan(0, lengths[i]), 0xDEAD_BEEFu);
        }

        var failures = 0L;
        var iterations = new int[lengths.Length];
        Parallel.For(0, lengths.Length, new ParallelOptions { MaxDegreeOfParallelism = lengths.Length }, i =>
        {
            var data = payload.AsSpan(0, lengths[i]);
            var expected = references[i];
            while (Interlocked.Read(ref failures) == 0L)
            {
                var actual = RegisterCrc32.Compute(data, 0xDEAD_BEEFu);
                if (actual != expected)
                {
                    Interlocked.Increment(ref failures);
                    return;
                }

                iterations[i]++;
                if (iterations[i] >= 20_000)
                {
                    return;
                }
            }
        });

        Assert.Equal(0, Interlocked.Read(ref failures));
    }
}