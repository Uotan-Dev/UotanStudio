using System.IO.Compression;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class DatImageDiffTests
{
    // Independently generated with Python bz2 and zlib 1.3.1, following recovery
    // ac8c9e96627671e53a1aebdacaf9748b7241a3df/applypatch/imgdiff.cpp.
    // Fixtures are zlib-wrapped only to keep long RAW zero runs out of this source.
    private const string Source = "eJzLyy/KTcxRyM9JUUiqLEktfntiIy+DgADDgdA6cxGhj0Jal287lYbH9UltX381a8qc5Wc3K88tPmf5fA7xRO1+doZRMApGwSgYBaNgFIyCUTAKRsEoGAWjYBQMIAAAdRMzxw==";
    private const string Target = "eJzLyy/KTcxRyEstV0iqLEktfntiIyeDogDDhtA8872HnomIsCz/r7vP61TIvYrktW8ups1ds3nxnOJzls9JIP7WyTOMglEwCkbBKBgFo2AUjIJRMApGwSgYBaNg4AAAlb42kw==";
    private const string Patch = "eJzz9HV38XRzM2JmQAUCUJoTymBCEguD0n1QAQU/VJoNiDmA+OP///85oGpB5s/kZxgFo2AUjIJRMApGwSgYBaNgFIyCUTAKRsFAAqdg0DiAiYEmlK8HpWGdfqeoDEtDx0i14MjKfOM+BgamBAYHBg8GBQaDMzxfWc/pPekqWKjw6V66DELhV55UU6DOBww3QMMBCgsYFFUO8TCZsd4+emein4iKrbOkrwNItXiRq0XABGRH6EMtNYfSsJEFhNl+GgYtQEd8ATpCAIgZGBUYFBUXnDXJSO/eqTRHw0NdRMIJSfmG99u8GPjnP2A4wARxSkBag2+gya0JqSHNVxVeTSj1sAP7QDHxfs4UVDcBAE6PUCE=";

    [Fact]
    public void Imgdiff_NormalDeflateRawChunks_MatchNativeZlibBytes()
    {
        var source = Expand(Source); var target = Expand(Target); var patch = Expand(Patch);
        using var temp = new TempDirectory();
        var list = temp.Write("list", $"4\n1\n0\n0\nimgdiff 0 {patch.Length} {Hash(source)} {Hash(target)} 2,0,1 1 2,0,1\n");
        var output = Path.Combine(temp.Path, "target.img");
        DatReader.Unpack(list, temp.WriteBytes("new.dat", []), temp.WriteBytes("patch.dat", patch), temp.WriteBytes("old.img", source), output);
        Assert.Equal(target, File.ReadAllBytes(output));
    }

    [Fact]
    public void Imgdiff_OutputLimit_IsEnforced()
    {
        using var output = new MemoryStream();
        Assert.ThrowsAny<IOException>(() => ImageDeltaPatch.Apply(new MemoryStream(Expand(Source)), new MemoryStream(Expand(Patch)), output, 4095));
        Assert.True(output.Length <= 4095);
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA1.HashData(data));
    private static byte[] Expand(string fixture)
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(fixture));
        using var decoder = new ZLibStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream(); decoder.CopyTo(output); return output.ToArray();
    }
}