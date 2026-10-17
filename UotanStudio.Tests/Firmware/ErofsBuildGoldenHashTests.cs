using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsBuildGoldenHashTests
{
    private const int BlockSize = 4096;
    private const ulong BuildTime = 1_700_000_000UL;

    [Theory]
    [InlineData("flat", "3ED9CE53589E15E5AE4B21D9CF06F9FD792FE5E5094D3ED218344CFC7A25D193")]
    [InlineData("compacted", "059975E21DE2125A024DDC865ECF00150FBD3302100335AAB775BFDC82356D32")]
    [InlineData("full-index", "B852351D7DAE0E2797E0937FA9A14D0BD041B9E13336B489E990D40D0894B3E9")]
    [InlineData("big-pcluster", "11D9E80D174F409C61306E450E744234867FA3AF1EC05B6EE77AB5CE25FA2E5E")]
    [InlineData("ztailpacked", "1C1A983144F85B7410DA7C044ACBDFBCE520E1410C65F6E4B2FBD3BE579AB285")]
    [InlineData("fragments", "FC0DE8B7A055234B35923CBCF1F3A6DFEF090B258725B69C2032EB18452B2006")]
    [InlineData("metabox", "F6C5D0A117DFB8EED537F37F7361BB929FD2426973BCD252AE234B3B690F38F4")]
    [InlineData("links", "D27E7F6BF9DDBFFC3221459EC051E76CAA93934DAE46E3FD942DEC252511E50D")]
    [InlineData("wide-ownership", "5B09C3FCC95A1DF6ECDDBDCE77165F9EA9464FF695CB7F3D586069B2C2E204E7")]
    public void Build_FixtureImage_MatchesPinnedHashAndIsDeterministic(string fixture, string expected)
    {
        using var source = new TempDirectory();
        WriteFixture(source, fixture);

        var image = new ErofsBuilder().Build(source.Path, OptionsFor(fixture));
        var again = new ErofsBuilder().Build(source.Path, OptionsFor(fixture));

        var actual = Convert.ToHexString(SHA256.HashData(image));
        Assert.True(actual == expected, $"fixture={fixture} expected={expected} actual={actual}");
        var repeat = Convert.ToHexString(SHA256.HashData(again));
        Assert.True(repeat == actual, $"fixture={fixture} nondeterministic actual={actual} repeat={repeat}");
    }

    [Fact]
    public void Fixtures_ClassifyFeatureProductsAgainstPinnedDefaultProducts()
    {
        Assert.False(OptionsFor("flat").Compress);
        Assert.False(OptionsFor("compacted").Fragments);
        Assert.False(OptionsFor("compacted").Metabox);
        Assert.False(OptionsFor("compacted").CompressedTailPacking);
        Assert.False(OptionsFor("full-index").Fragments);
        Assert.False(OptionsFor("big-pcluster").Fragments);
        Assert.False(OptionsFor("links").Fragments);
        Assert.False(OptionsFor("wide-ownership").Compress);

        Assert.True(OptionsFor("fragments").Fragments);
        Assert.True(OptionsFor("fragments").AllFilesAsFragments);
        Assert.True(OptionsFor("metabox").Metabox);
        Assert.True(OptionsFor("ztailpacked").CompressedTailPacking);
        Assert.True(OptionsFor("big-pcluster").MaxPclusterSize > BlockSize);
    }

    private static ErofsOptions OptionsFor(string fixture)
    {
        var options = fixture switch
        {
            "flat" => new ErofsOptions { Compress = false },
            "compacted" => new ErofsOptions { Compress = true, UseCompactedIndexes = true },
            "full-index" => new ErofsOptions { Compress = true, UseCompactedIndexes = false },
            "big-pcluster" => new ErofsOptions { Compress = true, MaxPclusterSize = 1 << 20 },
            "ztailpacked" => new ErofsOptions { Compress = true, CompressedTailPacking = true },
            "fragments" => new ErofsOptions { Compress = true, Fragments = true, AllFilesAsFragments = true },
            "metabox" => new ErofsOptions { Compress = true, Metabox = true },
            "links" => new ErofsOptions { Compress = true },
            "wide-ownership" => new ErofsOptions
            {
                Compress = false,
                FsConfig = new Dictionary<string, FsConfigEntry>(StringComparer.Ordinal)
                {
                    ["data/multiblock.bin"] = new FsConfigEntry(70_000, 70_001, 0x81A4),
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(fixture), fixture, "Unknown EROFS golden fixture."),
        };
        options.BlockSize = BlockSize;
        options.Uuid = new byte[16];
        options.BuildTime = BuildTime;
        return options;
    }

    private static void WriteFixture(TempDirectory source, string fixture)
    {
        source.WriteBytes("empty.bin", []);
        source.Write("text/readme.txt", "hello erofs golden fixture\n");
        source.WriteBytes("data/block.bin", Deterministic(BlockSize));
        source.WriteBytes("data/multiblock.bin", Deterministic(3 * BlockSize));
        source.Write("compressible.txt", Compressible(96 * 1024));
        source.Write("dir/sub/inner.txt", "inner\n");
        source.WriteBytes("dir/one-block.bin", Deterministic(BlockSize));
        source.Write("tiny/one.txt", "t\n");
        Directory.CreateDirectory(Path.Combine(source.Path, "emptydir"));

        if (fixture != "links")
        {
            return;
        }

        File.CreateSymbolicLink(
            Path.Combine(source.Path, "link.txt"),
            Path.Combine("text", "readme.txt"));
        var linked = CreateHardLink(
            Path.Combine(source.Path, "hard-block.bin"),
            Path.Combine(source.Path, "data", "block.bin"),
            IntPtr.Zero);
        Assert.True(linked, $"CreateHardLink failed with error {Marshal.GetLastWin32Error()}.");
    }

    private static byte[] Deterministic(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 31 + i / 7) & 0xFF);
        }

        return payload;
    }

    private static string Compressible(int length)
    {
        var builder = new System.Text.StringBuilder(length);
        while (builder.Length < length)
        {
            builder.Append("the quick brown fox jumps over the lazy dog 0123456789\n");
        }

        return builder.ToString(0, length);
    }

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}