using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Auth;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

/// <summary>
/// Nothing project-verification credential tests built on literal token vectors. The digest input
/// layout, hexadecimal casing and nonce width are pinned to those literals.
/// <para>基于字面令牌向量的 Nothing 项目校验凭据测试。摘要输入布局、十六进制大小写与随机数宽度都锁定到这些字面量。</para>
/// </summary>
public sealed class EdlAuthProviderNothingTests
{
    private const string ReferenceToken1 =
        "512034500a07154561661e0f371f4a712a0b76074605724c640e301d632b3671";

    private const string ReferenceToken2 =
        "1ecd222465436eb8acc0cfc41e90d1e677165c184ea7d9631615014dac88c669";

    private const string ReferenceHashVerify =
        "16386b4035411a770b12507b2e30297c0c5471230b213e6a1e1e701c6a425150";

    private const ulong ReferenceSerial = 1729931115;

    private static EdlNothingProjectProfile ReferenceProfile() => new()
    {
        ProjectId = "22111",
        HashVerify = ReferenceHashVerify,
        Description = "Nothing Phone (2)",
    };

    [Fact]
    public async Task CreateCredentialsAsync_UpstreamReferenceVector_ProducesDocumentedTokens()
    {
        var provider = new EdlNothingProjectAuthProvider(ReferenceProfile(), ReferenceToken1);

        var credentials = await provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.NothingProject, "22111", ReferenceSerial, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(Encoding.ASCII.GetBytes(ReferenceToken1), credentials["token1"]);
        Assert.Equal(Encoding.ASCII.GetBytes(ReferenceToken2), credentials["token2"]);
        Assert.Equal(Encoding.ASCII.GetBytes(ReferenceHashVerify), credentials["token3"]);
    }

    [Fact]
    public async Task CreateCredentialsAsync_FreshNonce_ProducesCredentialGateWidthsAndCasing()
    {
        var provider = new EdlNothingProjectAuthProvider(ReferenceProfile());

        var credentials = await provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.NothingProject, "22111", ReferenceSerial, null,
            TestContext.Current.CancellationToken);

        var token1 = Encoding.ASCII.GetString(credentials["token1"]);
        var token2 = Encoding.ASCII.GetString(credentials["token2"]);
        Assert.Equal(32, token1.Length);
        Assert.Equal(64, token2.Length);
        Assert.All(token1, static ch => Assert.True(char.IsAsciiHexDigitLower(ch)));
        Assert.All(token2, static ch => Assert.True(char.IsAsciiHexDigitLower(ch)));
        Assert.Equal(ReferenceHashVerify, Encoding.ASCII.GetString(credentials["token3"]));
    }

    [Fact]
    public async Task CreateCredentialsAsync_SecondFreshExchange_UsesANewNonce()
    {
        var provider = new EdlNothingProjectAuthProvider(ReferenceProfile());

        var first = await provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.NothingProject, "22111", ReferenceSerial, null,
            TestContext.Current.CancellationToken);
        var second = await provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.NothingProject, "22111", ReferenceSerial, null,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(first["token1"], second["token1"]);
        Assert.NotEqual(first["token2"], second["token2"]);
    }

    [Fact]
    public async Task CreateCredentialsAsync_OnePlusKind_IsRefused()
    {
        var provider = new EdlNothingProjectAuthProvider(ReferenceProfile());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.OnePlusProject, "22111", ReferenceSerial, null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateCredentialsAsync_ProjectMismatchWithProfile_IsRefused()
    {
        var provider = new EdlNothingProjectAuthProvider(ReferenceProfile());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.NothingProject, "2211111", ReferenceSerial, null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateCredentialsAsync_ZeroSerial_IsRefused()
    {
        var provider = new EdlNothingProjectAuthProvider(ReferenceProfile());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.NothingProject, "22111", 0, null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateCredentialsAsync_AlreadyCancelledToken_IsRefused()
    {
        var provider = new EdlNothingProjectAuthProvider(ReferenceProfile());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.NothingProject, "22111", ReferenceSerial, null, cancelled.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefg")]
    [InlineData("16386B4035411A770B12507B2E30297C0C5471230B213E6A1E1E701C6A425150")]
    public void Constructor_MalformedHashVerify_IsRefused(string hashVerify)
    {
        var profile = new EdlNothingProjectProfile { ProjectId = "22111", HashVerify = hashVerify };

        Assert.Throws<ArgumentException>(() => new EdlNothingProjectAuthProvider(profile));
    }

    [Theory]
    [InlineData("")]
    [InlineData("512034500a07154561661e0f371f4a712a0b76074605724c640e301d632b367")]
    [InlineData("512034500A07154561661E0F371F4A712A0B76074605724C640E301D632B3671")]
    public void Constructor_MalformedFixedNonce_IsRefused(string fixedToken1)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new EdlNothingProjectAuthProvider(ReferenceProfile(), fixedToken1));
    }

    [Fact]
    public void TryResolve_ReferenceProjectId_ReturnsUpstreamDigest()
    {
        Assert.True(EdlNothingProjectCatalog.TryResolve("22111", out var profile));

        Assert.NotNull(profile);
        Assert.Equal("22111", profile!.ProjectId);
        Assert.Equal(ReferenceHashVerify, profile.HashVerify);
    }

    [Fact]
    public void TryResolve_UncataloguedProjectId_RefusesGuessing()
    {
        Assert.False(EdlNothingProjectCatalog.TryResolve("99999", out var profile));

        Assert.Null(profile);
    }

    [Fact]
    public async Task CreateCredentialsAsync_FixedNonceReused_Token1StableAcrossExchanges()
    {
        var provider = new EdlNothingProjectAuthProvider(ReferenceProfile(), ReferenceToken1);

        var first = await provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.NothingProject, "22111", ReferenceSerial, null,
            TestContext.Current.CancellationToken);
        var second = await provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.NothingProject, "22111", ReferenceSerial, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(first["token1"], second["token1"]);
        Assert.Equal(ReferenceToken2, Encoding.ASCII.GetString(first["token2"]));
    }
}