using System.Text;
using UotanStudio.Hash.Adler32;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class Net11HashTests
{
    [Theory]
    [InlineData("", 1u)]
    [InlineData("a", 0x00620062u)]
    [InlineData("abc", 0x024D0127u)]
    [InlineData("Wikipedia", 0x11E60398u)]
    public void Compute_KnownVector_ReturnsStandardAdler32(string text, uint expected)
    {
        var data = Encoding.ASCII.GetBytes(text);

        var actual = Adler32.Compute(data);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(95)]
    [InlineData(96)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(5503)]
    [InlineData(5504)]
    [InlineData(5505)]
    [InlineData(5551)]
    [InlineData(5552)]
    [InlineData(5553)]
    [InlineData(11007)]
    [InlineData(11008)]
    [InlineData(11009)]
    [InlineData(11103)]
    [InlineData(11104)]
    [InlineData(11105)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    public void Compute_BoundaryLengthWithNonZeroSeed_MatchesScalarReference(int length)
    {
        var prefix = BuildDeterministic(37);
        var seed = Adler32.ComputeScalar(prefix);
        var data = BuildDeterministic(length);
        var combined = new byte[prefix.Length + data.Length];
        prefix.CopyTo(combined, 0);
        data.CopyTo(combined, prefix.Length);

        var scalar = Adler32.ComputeScalar(data);
        var continuedScalar = Adler32.ComputeScalar(combined);

        Assert.NotEqual(1u, seed);
        Assert.Equal(scalar, Adler32.Compute(data));
        Assert.Equal(scalar, Adler32.ComputeVectorized(data));
        Assert.Equal(continuedScalar, Adler32.Compute(data, seed));
        if (length == 0)
        {
            Assert.Equal(seed, Adler32.Compute(data, seed));
        }
    }

    private static byte[] BuildDeterministic(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 131 + (i >> 5) * 17 + 29) & 0xFF);
        }

        return data;
    }
}