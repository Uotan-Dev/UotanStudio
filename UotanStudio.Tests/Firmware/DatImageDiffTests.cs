using System.IO.Compression;
using System.Security.Cryptography;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Firmware.OtaDelta.Diff;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class DatImageDiffTests
{
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
        DatReader.Unpack(list, temp.WriteBytes("new.dat", []), temp.WriteBytes("patch.dat", patch), temp.WriteBytes("old.img", source), output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(target, File.ReadAllBytes(output));
    }

    [Fact]
    public void Imgdiff_OutputLimit_IsEnforced()
    {
        using var output = new MemoryStream();
        Assert.ThrowsAny<IOException>(() => ImageDeltaPatch.Apply(new MemoryStream(Expand(Source)), new MemoryStream(Expand(Patch)), output, 4095));
        Assert.True(output.Length <= 4095);
    }

    [Fact]
    public void Create_Apply_NormalOnly_RoundTrips()
    {
        var source = Block(2, 0x11); var target = Block(2, 0x22);
        AssertRoundTrip(source, target);
    }

    [Fact]
    public void Create_Apply_NormalPlusRaw_RoundTrips()
    {
        var source = Block(1, 0x33); var target = Block(3, 0x44);
        AssertRoundTrip(source, target);
    }

    [Fact]
    public void Create_Apply_RawOnlyWhenEmptySource_RoundTrips()
    {
        AssertRoundTrip([], Block(2, 0x55));
    }

    [Fact]
    public void Create_ThroughDatReader_RebuildsTarget()
    {
        var source = Block(1, 0x66); var target = Block(3, 0x77);
        var patch = BuildPatch(source, target);
        using var temp = new TempDirectory();
        var list = temp.Write("list", $"4\n3\n0\n0\nimgdiff 0 {patch.Length} {Hash(source)} {Hash(target)} 2,0,3 1 2,0,1\n");
        var output = Path.Combine(temp.Path, "out.img");
        DatReader.Unpack(list, temp.WriteBytes("new.dat", []), temp.WriteBytes("patch.dat", patch), temp.WriteBytes("old.img", source), output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(target, File.ReadAllBytes(output));
    }

    private static void AssertRoundTrip(byte[] source, byte[] target)
    {
        var patch = BuildPatch(source, target);
        using var output = new MemoryStream();
        ImageDeltaPatch.Apply(new MemoryStream(source), new MemoryStream(patch), output, target.LongLength);
        Assert.Equal(target, output.ToArray());
    }

    private static byte[] BuildPatch(byte[] source, byte[] target)
    {
        using var patch = new MemoryStream();
        ImageDeltaPatch.Create(new MemoryStream(source), new MemoryStream(target), patch);
        return patch.ToArray();
    }

    private static byte[] Block(int count, byte fill)
    {
        var data = new byte[count * 4096];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(fill ^ i);
        return data;
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA1.HashData(data));
    private static byte[] Expand(string fixture)
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(fixture));
        using var decoder = new ZLibStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream(); decoder.CopyTo(output); return output.ToArray();
    }
}