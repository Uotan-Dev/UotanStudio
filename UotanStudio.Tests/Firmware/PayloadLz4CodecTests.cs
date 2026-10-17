using System.Security.Cryptography;
using UotanStudio.Compress.Codecs.Lz4;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks destination-size LZ4 encoding against native liblz4 1.9.4 vectors. <para>依据原生 liblz4 1.9.4 向量检查限定目标大小的 LZ4 编码。</para></summary>
public sealed class PayloadLz4CodecTests
{
    [Theory]
    [InlineData(0, 0, 12, 1, 0, 1, "6E340B9CFFB37A989CA544E6BB780A2C78901D3FB33738768511A30617AFA01D")]
    [InlineData(128, 6, 1, 100, 128, 84, "668FCFF8B02499706B677A96A2E9CA5A97CC64FCD7E9CF4C91726570749312CA")]
    [InlineData(128, 6, 2, 100, 128, 84, "668FCFF8B02499706B677A96A2E9CA5A97CC64FCD7E9CF4C91726570749312CA")]
    [InlineData(4096, 2, -1, 4096, 4079, 4096, "2F9279EC0A90DF83FF043A7D7D90B64203B93B2D3974576334AC85BB5CA80381")]
    [InlineData(4096, 2, 12, 4096, 4079, 4096, "2F9279EC0A90DF83FF043A7D7D90B64203B93B2D3974576334AC85BB5CA80381")]
    [InlineData(4096, 4, 9, 512, 985, 512, "E7D327E177A2E5B19D45DFAA20C24FB16B6C599FA253FF8944E91A24D5F294B6")]
    [InlineData(4096, 6, 10, 512, 2214, 512, "4570B7986CBFF4FFCE58FED8343000B4CD912232E0A51E20BA2B8B15AFB24D96")]
    [InlineData(65536, 0, -1, 1, 0, 1, "6E340B9CFFB37A989CA544E6BB780A2C78901D3FB33738768511A30617AFA01D")]
    [InlineData(65536, 0, 9, 100, 22974, 100, "86B2F7855925F3AEE52A061B4BF5351A3CD49CF89B0F5BFD2D5D70AD1615D77E")]
    [InlineData(65536, 1, -1, 100, 18655, 100, "D8162574C1838C82303C37A02FADEE30BDF02748744CDA2C06C3D81802303A58")]
    [InlineData(65547, 5, -1, 512, 1111, 512, "A5606BDD7DB6FAF4F9D6199FAEDB73FD0ADBB60E2F12B7E2A380DA06B7C81E93")]
    [InlineData(131072, 7, 12, 8192, 70347, 8192, "CEAB87397E2A2806005242897256FA13EBF8CC754CCF0B348331C1BC1002E110")]
    public void DestinationSize_MatchesNativeBytesAndConsumedLength(int size, int pattern, int level,
        int capacity, int expectedConsumed, int expectedLength, string expectedSha256)
    {
        var source = CreateSource(size, pattern);
        var guarded = Enumerable.Repeat((byte)0xa5, capacity + 32).ToArray();
        var encoded = Lz4DestinationCodec.Encode(source, guarded.AsSpan(16, capacity),
            level < 0 ? 1 : 2, level, out var consumed);
        Assert.Equal(expectedConsumed, consumed);
        Assert.Equal(expectedLength, encoded);
        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(guarded.AsSpan(16, encoded))));
        Assert.All(guarded[..16], value => Assert.Equal(0xa5, value));
        Assert.All(guarded[(16 + capacity)..], value => Assert.Equal(0xa5, value));
    }

    [Fact]
    public void DestinationSize_RejectsEmptyTarget()
        => Assert.Throws<InvalidDataException>(() => Lz4DestinationCodec.Encode([1], [], 1, 0, out _));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void DestinationSize_RejectsUnknownAlgorithm(int algorithm)
        => Assert.Throws<InvalidDataException>(() => Lz4DestinationCodec.Encode([1], new byte[10], algorithm, 0, out _));

    [Fact]
    public void DestinationSize_RejectsOverlappingBuffers()
    {
        var buffer = new byte[128];
        Assert.Throws<ArgumentException>(() => Lz4DestinationCodec.Encode(buffer, buffer.AsSpan(10), 1, 0, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DestinationSize_RepeatedCallsWithDirtyCache_ProduceIdenticalBlocks(int algorithm)
    {
        var source = CreateSource(65536, 3);
        var first = new byte[source.Length];
        var firstLength = Lz4DestinationCodec.Encode(source, first, algorithm, 9, out _);

        var noise = CreateSource(4096, 2);
        var noiseTarget = new byte[noise.Length];
        Assert.True(Lz4DestinationCodec.Encode(noise, noiseTarget, algorithm, 1, out _) > 0);

        var second = new byte[source.Length];
        var secondLength = Lz4DestinationCodec.Encode(source, second, algorithm, 9, out _);

        Assert.Equal(firstLength, secondLength);
        Assert.Equal(first.AsSpan(0, firstLength).ToArray(), second.AsSpan(0, secondLength).ToArray());
    }

    private static byte[] CreateSource(int size, int pattern)
    {
        var source = new byte[size];
        uint state = 0x12345678;
        var copyRemaining = 0;
        var distance = 0;
        for (var i = 0; i < source.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            if (pattern >= 4 && i > 64 && copyRemaining == 0 && state % 4 != 0)
            {
                distance = (int)(state % (uint)Math.Min(i, 65535)) + 1;
                copyRemaining = (int)((state >> 16) % (uint)(8 << (pattern - 4))) + 3;
            }
            source[i] = pattern switch
            {
                0 => 0,
                1 => (byte)(i % 17),
                2 => (byte)state,
                3 => i % 512 < 400 ? (byte)(i % 17) : (byte)state,
                _ => copyRemaining > 0 ? source[i - distance] : (byte)state
            };
            if (copyRemaining > 0) copyRemaining--;
        }
        return source;
    }
}