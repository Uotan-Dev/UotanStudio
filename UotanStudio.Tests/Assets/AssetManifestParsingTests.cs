using System.Text;
using UotanStudio.Assets;
using Xunit;

namespace UotanStudio.Tests.Assets;

/// <summary>
/// Pins the manifest contract: one well-formed six-column row becomes exactly one bundle entry carrying the recorded
/// length, digest, encoding and origin, while every row the store cannot act on is rejected with
/// <see cref="InvalidDataException"/>.
/// <para>固定清单契约：一条合法的六列行恰好产出一个携带长度、摘要、编码与来源的条目，而存储层无法处理的任何行都以
/// <see cref="InvalidDataException"/> 拒绝。</para>
/// </summary>
public class AssetManifestParsingTests
{
    private const string Header = "# <bundle>\t<path>\t<length>\t<sha256>\t<encoding>\t<origin>";

    private const string Digest = "9c120f0a849bc0aa5048daae2a3c0f6eecd828b5b33fce682a9622833f5feea6";

    [Fact]
    public void Parse_SingleWellFormedRow_ReturnsOneBundleHoldingThatEntry()
    {
        var manifest = Utf8(Header, Row("fonts", "MiSans-Regular.ttf", "8122324", Digest));

        var bundles = AssetManifest.Parse(manifest);

        var pair = Assert.Single(bundles);
        Assert.Equal("fonts", pair.Key);
        var bundle = pair.Value;
        Assert.True(bundle.IsPresent);
        Assert.Equal("fonts", bundle.Key);
        var entry = Assert.Single(bundle.Entries);
        Assert.Equal("fonts", entry.Bundle);
        Assert.Equal("MiSans-Regular.ttf", entry.Path);
        Assert.Equal(8_122_324L, entry.Length);
        Assert.Equal(Digest, entry.Sha256);
        Assert.Equal(AssetEncoding.Zstd, entry.Encoding);
        Assert.Equal(64, bundle.Digest.Length);
    }

    [Fact]
    public void Parse_ThreeRowsAcrossTwoBundles_GroupsEntriesByBundleKeyInPathOrder()
    {
        var manifest = Utf8(
            Header,
            Row("fonts", "MiSans-Regular.ttf", "8122324", Digest),
            Row("fonts", "MiSans-Bold.ttf", "8009316", Digest),
            Row("delivery", "update-binary-arm64", "2002464", Digest));

        var bundles = AssetManifest.Parse(manifest);

        Assert.Equal(2, bundles.Count);
        Assert.Equal(["MiSans-Bold.ttf", "MiSans-Regular.ttf"], bundles["fonts"].Entries.Select(static entry => entry.Path).ToArray());
        Assert.Equal(["update-binary-arm64"], bundles["delivery"].Entries.Select(static entry => entry.Path).ToArray());
    }

    [Fact]
    public void Parse_CrLfSeparatedRows_IgnoresTheTrailingCarriageReturn()
    {
        var manifest = Encoding.UTF8.GetBytes(
            string.Join("\r\n", Header, Row("fonts", "MiSans-Bold.ttf", "8009316", Digest)));

        var bundles = AssetManifest.Parse(manifest);

        var entry = Assert.Single(bundles["fonts"].Entries);
        Assert.Equal("MiSans-Bold.ttf", entry.Path);
        Assert.Equal(8_009_316L, entry.Length);
    }

    [Fact]
    public void Parse_FiveColumns_ThrowsInvalidDataException()
    {
        AssertRejected(Utf8(Header, string.Join('\t', "fonts", "MiSans-Regular.ttf", "8122324", Digest, "zstd")));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("268435457")]
    public void Parse_UnusableLength_ThrowsInvalidDataException(string length)
    {
        AssertRejected(Utf8(Header, Row("fonts", "MiSans-Regular.ttf", length, Digest)));
    }

    [Theory]
    [InlineData("9C120F0A849BC0AA5048DAE2A3C0F6EECD828B5B33FCE682A9622833F5FEEA6")]
    [InlineData("9c120f0a849bc0aa5048daae2a3c0f6eecd828b5b33fce682a9622833f5f")]
    [InlineData("9c120f0a849bc0aa5048daae2a3c0f6eecd828b5b33fce682a9622833f5feea66")]
    [InlineData("g1120f0a849bc0aa5048daae2a3c0f6eecd828b5b33fce682a9622833f5feea6")]
    public void Parse_UnusableSha256_ThrowsInvalidDataException(string sha256)
    {
        AssertRejected(Utf8(Header, Row("fonts", "MiSans-Regular.ttf", "8122324", sha256)));
    }

    [Fact]
    public void Parse_RepeatedBundleAndPath_ThrowsInvalidDataException()
    {
        var row = Row("fonts", "MiSans-Bold.ttf", "8009316", Digest);

        AssertRejected(Utf8(Header, row, row));
    }

    [Fact]
    public void Parse_BundleWithTraversalSegment_ThrowsInvalidDataException()
    {
        AssertRejected(Utf8(Header, Row("ohos/../device", "agent.so", "16", Digest)));
    }

    [Fact]
    public void Parse_BundleWithBackslash_ThrowsInvalidDataException()
    {
        AssertRejected(Utf8(Header, Row("ohos\\device", "agent.so", "16", Digest)));
    }

    [Fact]
    public void Parse_AbsoluteBundleKey_ThrowsInvalidDataException()
    {
        AssertRejected(Utf8(Header, Row("/ohos/device", "agent.so", "16", Digest)));
    }

    [Fact]
    public void Parse_PathWithSeparator_ThrowsInvalidDataException()
    {
        AssertRejected(Utf8(Header, Row("ohos/device", "sub/agent.so", "16", Digest)));
    }

    [Fact]
    public void Parse_UnsupportedEncoding_ThrowsInvalidDataException()
    {
        AssertRejected(Utf8(Header, Row("fonts", "MiSans-Bold.ttf", "8009316", Digest, "gzip")));
    }

    [Fact]
    public void Parse_UnknownOrigin_ThrowsInvalidDataException()
    {
        AssertRejected(Utf8(Header, Row("fonts", "MiSans-Bold.ttf", "8009316", Digest, "zstd", "vendored")));
    }

    [Fact]
    public void Parse_EmptyManifest_ThrowsInvalidDataException()
    {
        AssertRejected([]);
    }

    [Fact]
    public void Parse_NonUtf8Bytes_ThrowsInvalidDataException()
    {
        AssertRejected([0x66, 0x6F, 0x6F, 0xC3, 0x28, 0x0A]);
    }

    private static void AssertRejected(byte[] manifest)
    {
        Assert.Throws<InvalidDataException>(() =>
        {
            AssetManifest.Parse(manifest);
        });
    }

    private static string Row(string bundle, string path, string length, string sha256, string encoding = "zstd",
        string origin = "source") => $"{bundle}\t{path}\t{length}\t{sha256}\t{encoding}\t{origin}";

    private static byte[] Utf8(params string[] lines) => Encoding.UTF8.GetBytes(string.Join('\n', lines));
}