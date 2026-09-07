using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class DiffCompatibilityTests
{
    // AOSP BSDF2: independently generated with native Brotli and Python bz2.
    // Control and extra streams use Brotli (2); the byte-difference stream uses Bzip2 (1).
    private const string Patch = "QlNERjICAQIOAAAAAAAAADkAAAAAAAAAGAAAAAAAAAAbFwAAJAEUHEoKSgI5BEJaaDkxQVkmU1lIEHRqAAAEZBiIgQCAEAAAAgQAJckgACKYAGhAAAvKiofbwADhdyRThQkEgQdGoIsEgHBsdXMgZXh0cmED";

    [Fact]
    public async Task TruncatedBrotliAfterLastOutputByte_IsRejectedAtomically()
    {
        using var temp = new TempDirectory(); var output = temp.WriteBytes("new.bin", "existing"u8.ToArray());
        var result = await DeltaEngine.ApplyDeltaAsync(temp.WriteBytes("old.bin", "original bytes"u8.ToArray()),
            temp.WriteBytes("patch", Convert.FromBase64String(Patch)[..^1]), output, TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task Bsdf2_IndependentMixedCodecFixture_Applies()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "new.bin");
        var result = await DeltaEngine.ApplyDeltaAsync(temp.WriteBytes("old.bin", "original bytes"u8.ToArray()),
            temp.WriteBytes("patch", Convert.FromBase64String(Patch)), output, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("changed bytes plus extra"u8.ToArray(), File.ReadAllBytes(output));
    }

    [Fact]
    public async Task InvalidPatch_PreservesDestinationAndRemovesTemporaryFile()
    {
        using var temp = new TempDirectory(); var output = temp.WriteBytes("new.bin", "existing"u8.ToArray());
        var result = await DeltaEngine.ApplyDeltaAsync(temp.WriteBytes("old.bin", []), temp.WriteBytes("patch", new byte[32]), output, TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task CancelledGeneration_PreservesExistingPatch()
    {
        using var temp = new TempDirectory(); var patch = temp.WriteBytes("patch", "existing"u8.ToArray());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeltaEngine.GenerateDeltaAsync(
            temp.WriteBytes("old.bin", [1]), temp.WriteBytes("new.bin", [2]), patch, cancellation.Token));
        Assert.Equal("existing"u8.ToArray(), File.ReadAllBytes(patch));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task GeneratedPatch_IsAcceptedByIndependentBsdiff()
    {
        using var temp = new TempDirectory(); var oldData = new byte[512 * 1024]; new Random(7).NextBytes(oldData);
        var newData = oldData.Concat("new suffix"u8.ToArray()).ToArray();
        for (var i = 0; i < newData.Length; i += 997) newData[i] ^= 0x77;
        var patch = Path.Combine(temp.Path, "patch");
        var result = await DeltaEngine.GenerateDeltaAsync(temp.WriteBytes("old.bin", oldData), temp.WriteBytes("new.bin", newData), patch, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ErrorMessage);
        using var output = new MemoryStream();
        BsDiff.BinaryPatch.Apply(new MemoryStream(oldData), () => File.OpenRead(patch), output);
        Assert.Equal(newData, output.ToArray());
    }
}