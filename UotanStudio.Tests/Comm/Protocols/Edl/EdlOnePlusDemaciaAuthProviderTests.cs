using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Auth;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

/// <summary>
/// Demacia credential tests against vectors produced independently with the documented fixed key material
/// (SHA-256 plus AES-256-CBC over a zero-padded 256-byte block).
/// <para>demacia 凭据测试，对照使用公开固定密钥材料独立生成的向量（SHA-256 后对零填充到 256 字节的块做 AES-256-CBC）。</para>
/// </summary>
public sealed class EdlOnePlusDemaciaAuthProviderTests
{
    private const string FixedKey = "0123456789abcdef";

    private const string TokenForFullSerial =
        "53E1EBD1449CC006A0CFBB28E6E2181A2F7DC334EFA936F9E97D6C77866DA65247CE82183883FF00AC046835DD0361D1CDB3AE46CAF74B7051A57ED86A8A14FDFE305A8B5C6513D8441395C553562EAE3B1D00E0E898E5E5A7AF00EA1CAD8FD39F2B48C97CA8ED1EC9DCF1C825EB37835C2F4CD0C1A6233A922BC7C60203D2A4309CD03BBAEF19A31F0311DACC899FA8DDD7C86EC899F87DCCD6CDD828069630B07CB1D52A4EEE10A0ED7FBB32F6AADAF364B56E288122F28A0AF648A4EDCA8FD87026ED7063E868B9E7D47AEB0B5C499EE4F62583AD5F0FADBB47A3403691220361D7AD3210AB438F9DAC821063035E598BE05EE8B17F88D2707A4F23CF0C3C";

    private const string TokenForPaddedSerial =
        "53E1EBD1449CC006A0CFBB28E6E2181A098EEBADA0316F4AAFD3AE8D79213B5393AE13B81F595B0C3BC584E5D7E152E7486B1AC1D0CB7E6E1BC56AC3CB79983C33912D694D05C33C8DCB1171257DF25738F57798635EC89726265E48C8CBCE09E24AFBB38D787F0F8CC229BCD1DC2019AC4F3AF19379790041D47039B54F850B4F4CA5C02BC528744550C07B45B7EF4FE109062456EAA1C8616217624F4AED62EFA0F687CB5BDD2C1C8B101ED5718465A2030C136B01116C43F6616970128EEA1D59006B10C8C98A9AF6658D9BCF750CE45C742B7A3FE0549869509D61BD051CEFC02F492BDE7A582C260246E2F1E729489C0447634DB1D5200228AF191C31C9";

    [Theory]
    [InlineData(1234567890UL, TokenForFullSerial)]
    [InlineData(42UL, TokenForPaddedSerial)]
    public async Task CreateCredentialsAsync_SerialAndFixedKey_ProducesReferenceToken(ulong serial, string expected)
    {
        var provider = new EdlOnePlusDemaciaAuthProvider(FixedKey);

        var credentials = await provider.CreateCredentialsAsync(EdlProjectAuthenticationKind.OnePlusDemacia,
            "LE2123", serial, null, TestContext.Current.CancellationToken);

        Assert.Equal(["pk", "token"], credentials.Keys.Order());
        Assert.Equal(FixedKey, Encoding.ASCII.GetString(credentials["pk"]));
        Assert.Equal(expected, Encoding.ASCII.GetString(credentials["token"]));
    }

    [Fact]
    public async Task CreateCredentialsAsync_GeneratedKey_ProducesAcceptedFieldShapes()
    {
        var provider = new EdlOnePlusDemaciaAuthProvider();

        var credentials = await provider.CreateCredentialsAsync(EdlProjectAuthenticationKind.OnePlusDemacia,
            "LE2123", 9876543210UL, null, TestContext.Current.CancellationToken);

        var key = Encoding.ASCII.GetString(credentials["pk"]);
        var token = Encoding.ASCII.GetString(credentials["token"]);
        Assert.Equal(16, key.Length);
        Assert.All(key, character => Assert.True(char.IsAsciiLetterOrDigit(character)));
        Assert.Equal(512, token.Length);
        Assert.All(token, character => Assert.True(char.IsAsciiHexDigitUpper(character)));
    }

    [Fact]
    public async Task CreateCredentialsAsync_SoftwareProjectKind_IsRefusedWithoutDeviceIo()
    {
        var provider = new EdlOnePlusDemaciaAuthProvider(FixedKey);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.OnePlusSoftwareProject, "LE2123", 42UL, 1700000000L,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public Task CreateCredentialsAsync_ZeroSerial_IsRefused()
    {
        var provider = new EdlOnePlusDemaciaAuthProvider(FixedKey);

        return Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.OnePlusDemacia, "LE2123", 0UL, null, TestContext.Current.CancellationToken));
    }
}