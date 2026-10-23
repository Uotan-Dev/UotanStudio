using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsBuildGoldenHashTests
{
    private const int BlockSize = 4096;
    private const ulong BuildTime = 1_700_000_000UL;

    [Theory]
    [InlineData("flat", "3ED9CE53589E15E5AE4B21D9CF06F9FD792FE5E5094D3ED218344CFC7A25D193")]
    [InlineData("compacted", "FB71BB64245443D595622D940F9A06182293AB357AB3E6420A9EADB534609BF3")]
    [InlineData("full-index", "FEE33E501B5F4AD01E6F6A6952549194C5FF9B77C473A103F15670BCF41FCCE3")]
    [InlineData("big-pcluster", "955CD9D40684194B8B70694850DDA7419F28C5F7A3E4B688BA3C178A5DE8DCE0")]
    [InlineData("ztailpacked", "F998D5E0EB1C8194EAD54692F9834D04C85068D7CDDC19B4D23BDDD7416FB4AF")]
    [InlineData("fragments", "4610F77F6F5A82615B7341D7FE74376430CE86A6BE19D582420BE4F812830571")]
    [InlineData("metabox", "6EF4F2BD801F8096440991553F9A375FD5B0D42CF13E503FA5796344AB839909")]
    [InlineData("links", "D245C513ADAC5F4A982880E20B37E6C91AA3180908EA09F282A76274867D92C3")]
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

        if (fixture == "links")
        {
            File.CreateSymbolicLink(
                Path.Combine(source.Path, "link.txt"),
                Path.Combine("text", "readme.txt"));
            var linked = CreateHardLink(
                Path.Combine(source.Path, "hard-block.bin"),
                Path.Combine(source.Path, "data", "block.bin"),
                IntPtr.Zero);
            Assert.True(linked, $"CreateHardLink failed with error {Marshal.GetLastWin32Error()}.");
        }

        var pinned = DateTimeOffset.FromUnixTimeSeconds((long)BuildTime).UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(source.Path, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, pinned);
        }

        foreach (var directory in Directory.EnumerateDirectories(source.Path, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            Directory.SetLastWriteTimeUtc(directory, pinned);
        }

        Directory.SetLastWriteTimeUtc(source.Path, pinned);
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