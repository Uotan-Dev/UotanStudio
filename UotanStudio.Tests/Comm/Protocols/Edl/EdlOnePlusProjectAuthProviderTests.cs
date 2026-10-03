using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Auth;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

/// <summary>
/// Project-verification credential tests against tokens emitted by the reference OnePlus tool implementation,
/// so the hash layout, AES key composition, payload padding and hex casing are pinned to upstream behaviour
/// rather than to this repository's own reading of it.
/// <para>项目校验凭据测试对照参考 OnePlus 工具实现产出的令牌，因此摘要布局、AES 密钥组成、明文填充与十六进制
/// 大小写都锁定为上游行为，而不是本仓库对上游的解读。</para>
/// </summary>
internal static class EdlOnePlusReferenceVectors
{
    internal const string FixedKey = "FreeProgDontSale";

    internal const string ProjectModelGuacamolesToken =
        "7942235D387272C712AD28FF72834A714A3841AB5EFD42E7CAB1D749309BE96246965430A334C7A7B8BF6834D557890CB2F48D521D4D1F47E8DAE22422C14DBE77DA8CB692DCB772FA8E4F8AD5C58FFE2F97E0B6A945B5D3F34EFAD030F392A09A7D4CC386C4412A40F1FFE3BE5046BAD5C4594B582757CC4F681B012CB9097F6163819637A662A6311055A1F2F66877620BB3FC7D9F25CC71166372A31706388FC0A19D9900B471275B141187A6EC1B955AC6040813E6FF79D6DA7FC3817256C310ECBCDF55660D98582FC2CDE1AA957BE849B64625A408628C078A06EC806BE99A0E2B91D5EBB253846A3B0E0DB76D8FA40A93BEE511F40F1C6125A873E032";

    internal const string ProjectModelGuacamolesCommandToken =
        "5E70712F348AC9CFCA6613D2728D9F17157C5886F497A944AA6C7351B40CA2B532FFE306950DC989CA0339044AB3D5B6CF21E8290A807F1B4C724EF8A1B7BEB8B338C92CA2C9CE2306B85DF15BD836E11B71053BBD09EAF3DA4AAD0BAAD9E0E7E94E33EC13D1E0C782C21C09AA6C3DD047CB6E18AADAE2DF0A8209A4496A884D1A925D73C9EFC3B8CC33CEB62A1B7B2C2B7EB8A79E3A123B02FD351CD6B82E44FC4FAC75D886598AB12F44ED41997E497FB4661F3AC1063C873FF2BE4E71F185E70C97575286F29289FCF19BD717C9D301B40495C75CDB7D2C0DEB09ACB7706B45C4F376DE2009E552CFE04D5E5709AB0AACAAD450EDB38C0D551B6BD78F178F";

    internal const string ProjectModelLemonadeToken =
        "C2444F775DA91BADE9EA4E6F7DE84599D0CF05F5F1B42F0A953EF993FCB78E222D675C7763003AD96B70AC70301FDB3DF1008D5108486D1492D9B6A04D5F521AD6F4123C22E2F7EED71CFE7569368D23FF1BA36EE068385094DEA0F8508902E0B592042021272883F2FF143111477AA0DA036B5E529C7BD4A31DC4B4A07E72741EB737F54C2A4FC45B8D4BBD901442ADF5E894B44A302AABCA6E47DFEC7B8EC1B6CB67F7520F538BEF35DAC6DA1353FE5CC04023EAE3ADB9566C0A8D6F042AB1BEB02B62B71EDAA87DD35DB42DB94C1F690AB30F881122BD1F48CE86E6740FE0E2EA2D10763BCD92887477C1B027026634F641E2F89D130EB6F63D46CD1E62C9";

    internal const string SoftwareBillie8Token =
        "7516F41D3959A5BDC073CA4D96BE2E8361EB84688AF273D57BF517F88DEA69C89F163470AE26D31F3A90C181C60449065553E8AFBE30F80775C62D1E51F6810846CF79CE54ACBFE86382A64640546E34A8ECD83653E7A27AACEFAEE76B8DB3BEB30D2ADD762D6DE3EDCEA2AE26CA13AC645D3D9491C40A775CE03829706982AE8391DCDC1E25D17FA6FB9E4077DDCB1394AB621955FFBBB5E22B73FB0DCF0458EF466AF015A81EB2D92B57188BA1550B3E20CF147EFE5F66F257D368F5FE67F059B2F4FEF123C316C2E61CED113FAD22412451EE648A484BCD05D282B39FF31EF487F8BC16BBC5EFA0A5EA40E8A393D3491ABF007B397F98763950035706A55D00FC5FCF012CEDB8220C09E92C2537D8E35AE7953E349DC6320461E6BA75F10884D9BAAF8B69F4A835270F0F2EFBA88CC382BC47FA174C0A85424477EAB9D47E9FA22D9A779C82C88CBD2676F701A459D2B3E6F0A0F2BBBE7E7623E57D5B56B6D6A943A18A1D72A1F03809DC213F61BDE7E959A8194E8FF36CAF9B9992169834D34952EF806F8BDAFEC23668BC92C7832B0AD3B14D1BC3374993629AB23E107639DFA00BB0F9F9FF0CA32CB1B8A33A601BDC2B159B77BA1A9073647DEDC039341CBE8D4279C6C8AE23F97E1EC46E6EA86E626B7E7E97A4A5477137C5E9A7EC0BF1629B6BB9C46F532F639FAED2777A4CA3774CDBB3DC9B1033F7E7FCEDE8B98B";

    internal const string SoftwareBillie8CommandToken =
        "3B3118BC78701429AAC6699AC7CA5C8754B1007F876FE3D1DCCB380E1C89D3FB3A324157201B2D0EEB89F13749F2EA88BCFE53687F9A57D51405AB546DF6812F7A63FC89970808A151A813EAE3D4F0CE74ABDD033DE7C22685931008B3C270889F43330819B4B6B4C0ED515346BA16775490E89847D035994EEAEA8CB18F0765E7A2354765F2C8128D00F409D16E1C6D1887BCABA524BE1C282AFDC505B2B5F3B31413C724947E2E9E0090FB7CCF3C0536BE4BD66D2874C2C2010FFC2911FBE044985E9728F7BE542B4EAE29A0DED9D2AB58DF22B953D7CCD05B61BA6747D701466B376437D097EE987FDCC07331DA16C6AA72160726AE91C09EA9839154024BDCF7AB24571B1BB9A56A042E91B6DC333275620F186FDE358A87F7C6FA7B0E1FC71807E5A9C36F03142143B3D7B9199A52E0C3232C4365FC4172F5BA922B41E015D23976480C1F96858507604B260A900EDBE76041966059B99376256E6149C50866162292323F5CB94EF1459B1702F800428943704592E8A156A57DF0D6B6DF0BA4DE18511F2D8046FF9BC4B6A7E3E3BC8773C2F099FF5D7E626C28540CF3EC9C330BD1DEDC8FE1B76AA64776F02F569AE0DA073FDD51C6D014136A1D181B6DA1A0354E895502ACCDAA8D84F51D95EA2CB237B83F9D6A15D60C3CD52F497D5C8DF476B83B5748B92615300057ECD445D627EFE413E5BD23CC72819CFA70C55D";

    internal const string SoftwareBillie8tCustomFlagsToken =
        "FC47CFB34CB14161EE5700D6E8D3C3D45AAEA43A2AB30EA8CA88EF7D9B1FB45A9B09908F60C3EA7310BE3DB6F9C9EEA4525A67CF8A4B3FA51B27FE8C1B95F2862C516DD0201A3FA04434AC456A7BA14CC3DE771D1E18B9643145B7F6A57F9AA3388C908A2CB2A26A91E89A9818901630791178EAC7AD904D8C5443653B3B7633B7986ED8D7723D09E458D9B8621A6DF2438BEC352B593845361F1550FBF1936A6F2C0BEB1F3EFB09EAE532CBFF2CF88C63955208EAD892364F908D2425EDB4F6681B3E8BF5EDCC97B1DF5125C82A9A352BCEC22703C2CBC395EDE83A03EFDC7780BF25B594336BBC6549F8AD90D70E6EF851244170371841108C958298253AB3EDA816B73E49902ED2943C8E08B8A2D7421733D73A1CA45D8EA80B302BFD45A56403548E64BED24F6CAE8CF86D0E988805B1B393B3491CCD6435E45F096AE1C14842BF220CAD3E2AA6A2EA2F8D52ACFF2051FADC765A4B672FF46C0E1E13DF59CA3E0866136AB9839790049312BB89211D6172F3451052435525E3CF7D3F702AC1598897F8808240E800820F1967A6A53C7A685F936A81B74E015555DAD51721871EFC443C2C272FD1F7D5FD411712ACC1D8C395933A2AC8CD704A7CC35956815D8DB925B32FD14E77A0C9DBEE103E434DEAF9CC6F3D75053E337AB00B5497320E997236F150F873C4646FC7BB2B7FFF5E6236075A9674A44B33E6CE177B3926";
}

public sealed class EdlOnePlusProjectAuthProviderTests
{
    private const string FixedKey = EdlOnePlusReferenceVectors.FixedKey;

    private static EdlOnePlusProjectProfile ProjectModel(string projectId, string productionKey)
        => new()
        {
            ProjectId = projectId,
            ProductionKey = productionKey,
            RandomPostfix = "0iyFR00pPnoqjVNL",
            VersionString = "guacamoles_21_O.22_191107",
            Family = EdlOnePlusProjectFamily.ProjectModel,
            Description = projectId,
        };

    private static EdlOnePlusProjectProfile SoftwareModel(string projectId, int atoBuildState, int flashMode)
        => new()
        {
            ProjectId = projectId,
            ProductionKey = "7016147d58e8c038",
            RandomPostfix = "c75oVnz8yUgLZObh",
            VersionString = "billie8_14_E.01_201028",
            Family = EdlOnePlusProjectFamily.SoftwareProjectModel,
            AtoBuildState = atoBuildState,
            FlashMode = flashMode,
        };

    private static string Token(IReadOnlyDictionary<string, byte[]> credentials, string field)
        => Encoding.ASCII.GetString(credentials[field]);

    [Theory]
    [InlineData("18825", "b2fad511325185e5", 123456UL, EdlOnePlusReferenceVectors.ProjectModelGuacamolesToken)]
    [InlineData("0898dcd6", "7016147d58e8c038", 987654321UL, EdlOnePlusReferenceVectors.ProjectModelLemonadeToken)]
    public async Task CreateCredentialsAsync_ProjectModelProfile_ProducesReferenceToken(
        string projectId, string productionKey, ulong serial, string expected)
    {
        var provider = new EdlOnePlusProjectAuthProvider(
            ProjectModel(projectId, productionKey), FixedKey);

        var credentials = await provider.CreateCredentialsAsync(EdlProjectAuthenticationKind.OnePlusProject,
            projectId, serial, null, TestContext.Current.CancellationToken);

        Assert.Equal(["pk", "token"], credentials.Keys.Order().ToArray());
        Assert.Equal(FixedKey, Token(credentials, "pk"));
        Assert.Equal(expected, Token(credentials, "token"));
        Assert.Equal(512, Token(credentials, "token").Length);
    }

    [Fact]
    public async Task CreateCommandTokenAsync_ProjectModelProfile_ProducesReferenceToken()
    {
        var provider = new EdlOnePlusProjectAuthProvider(
            ProjectModel("18825", "b2fad511325185e5"), FixedKey);

        var credentials = await provider.CreateCommandTokenAsync(EdlProjectAuthenticationKind.OnePlusProject,
            "18825", 123456UL, null, "program", TestContext.Current.CancellationToken);

        Assert.Equal(FixedKey, Token(credentials, "pk"));
        Assert.Equal(EdlOnePlusReferenceVectors.ProjectModelGuacamolesCommandToken, Token(credentials, "token"));
    }

    [Fact]
    public async Task CreateCredentialsAsync_SoftwareProfileWithDeviceTimestamp_ProducesReferenceToken()
    {
        var provider = new EdlOnePlusProjectAuthProvider(SoftwareModel("f2056ae1", 0, 0), FixedKey);

        var credentials = await provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.OnePlusSoftwareProject, "f2056ae1", 123456UL, 1729390368L,
            TestContext.Current.CancellationToken);

        Assert.Equal(FixedKey, Token(credentials, "pk"));
        Assert.Equal(EdlOnePlusReferenceVectors.SoftwareBillie8Token, Token(credentials, "token"));
        Assert.Equal(1024, Token(credentials, "token").Length);
    }

    [Fact]
    public async Task CreateCommandTokenAsync_SoftwareProfile_ProducesReferenceToken()
    {
        var provider = new EdlOnePlusProjectAuthProvider(SoftwareModel("f2056ae1", 0, 0), FixedKey);

        var credentials = await provider.CreateCommandTokenAsync(
            EdlProjectAuthenticationKind.OnePlusSoftwareProject, "f2056ae1", 123456UL, 1729390368L, "patch",
            TestContext.Current.CancellationToken);

        Assert.Equal(EdlOnePlusReferenceVectors.SoftwareBillie8CommandToken, Token(credentials, "token"));
    }

    [Fact]
    public async Task CreateCredentialsAsync_NonDefaultSoftwareFlags_ProduceReferenceToken()
    {
        var provider = new EdlOnePlusProjectAuthProvider(SoftwareModel("3a403a71", 1, 2), FixedKey);

        var credentials = await provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.OnePlusSoftwareProject, "3a403a71", 555001234UL, 1604949411L,
            TestContext.Current.CancellationToken);

        Assert.Equal(EdlOnePlusReferenceVectors.SoftwareBillie8tCustomFlagsToken, Token(credentials, "token"));
    }

    [Theory]
    [InlineData("18825", "b2fad511325185e5", "18825", EdlOnePlusProjectFamily.ProjectModel)]
    [InlineData("19825", "7016147d58e8c038", "0898dcd6", EdlOnePlusProjectFamily.ProjectModel)]
    [InlineData("20889", "7016147d58e8c038", "f2056ae1", EdlOnePlusProjectFamily.SoftwareProjectModel)]
    public void TryResolve_ReportedProjectIdentifier_SelectsTheReferenceProfile(
        string projectId, string productionKey, string tokenName, EdlOnePlusProjectFamily family)
    {
        Assert.True(EdlOnePlusProjectCatalog.TryResolve(projectId, out var profile));

        Assert.NotNull(profile);
        Assert.Equal(tokenName, profile.ProjectId);
        Assert.Equal(productionKey, profile.ProductionKey);
        Assert.Equal(family, profile.Family);
        Assert.Equal(EdlOnePlusProjectProfile.ReferenceTimestampDefault, profile.ReferenceTimestamp);
        Assert.Equal(EdlOnePlusProjectCatalog.Describe(projectId), profile.Description);
        Assert.False(string.IsNullOrWhiteSpace(profile.Description));
    }

    [Fact]
    public void TryResolve_UnknownProject_ReportsNoProfile()
    {
        Assert.False(EdlOnePlusProjectCatalog.TryResolve("00000", out var profile));
        Assert.Null(profile);
        Assert.Equal(string.Empty, EdlOnePlusProjectCatalog.Describe("00000"));
        Assert.DoesNotContain("00000", EdlOnePlusProjectCatalog.ProjectIds);
    }

    [Fact]
    public void ProjectIds_CatalogEntries_AreDistinctAndSorted()
    {
        var identifiers = EdlOnePlusProjectCatalog.ProjectIds;

        Assert.Equal(identifiers.Distinct(StringComparer.Ordinal).ToArray(), identifiers);
        Assert.Equal(identifiers.Order(StringComparer.Ordinal).ToArray(), identifiers);
        Assert.True(identifiers.Count >= 40);
    }

    [Fact]
    public async Task CreateCredentialsAsync_ProjectNameDiffersFromProfile_IsRefused()
    {
        var provider = new EdlOnePlusProjectAuthProvider(
            ProjectModel("18825", "b2fad511325185e5"), FixedKey);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.OnePlusProject, "18821", 123456UL, null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateCredentialsAsync_KindDiffersFromProfileFamily_IsRefused()
    {
        var provider = new EdlOnePlusProjectAuthProvider(SoftwareModel("f2056ae1", 0, 0), FixedKey);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.OnePlusProject, "f2056ae1", 123456UL, 1729390368L,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateCredentialsAsync_SoftwareProfileWithoutDeviceTimestamp_IsRefused()
    {
        var provider = new EdlOnePlusProjectAuthProvider(SoftwareModel("f2056ae1", 0, 0), FixedKey);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.OnePlusSoftwareProject, "f2056ae1", 123456UL, null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateCommandTokenAsync_UnsupportedCommand_IsRefused()
    {
        var provider = new EdlOnePlusProjectAuthProvider(
            ProjectModel("18825", "b2fad511325185e5"), FixedKey);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.CreateCommandTokenAsync(
            EdlProjectAuthenticationKind.OnePlusProject, "18825", 123456UL, null, "erase",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateCredentialsAsync_GeneratedProjectKey_IsReusedByTheCommandToken()
    {
        var provider = new EdlOnePlusProjectAuthProvider(
            ProjectModel("18825", "b2fad511325185e5"));

        var exchange = await provider.CreateCredentialsAsync(EdlProjectAuthenticationKind.OnePlusProject,
            "18825", 123456UL, null, TestContext.Current.CancellationToken);
        var command = await provider.CreateCommandTokenAsync(EdlProjectAuthenticationKind.OnePlusProject,
            "18825", 123456UL, null, "program", TestContext.Current.CancellationToken);

        var key = Token(exchange, "pk");
        Assert.Equal(16, key.Length);
        Assert.Equal(key, Token(command, "pk"));
        Assert.Equal(512, Token(exchange, "token").Length);
        Assert.All(Token(exchange, "token"), character => Assert.True(char.IsAsciiHexDigitUpper(character)));
    }

    [Fact]
    public void Constructor_ProfileWithNonHexSoftwareProject_IsRefused()
    {
        var profile = new EdlOnePlusProjectProfile
        {
            ProjectId = "18825Z",
            ProductionKey = "b2fad511325185e5",
            RandomPostfix = "c75oVnz8yUgLZObh",
            VersionString = "billie8_14_E.01_201028",
            Family = EdlOnePlusProjectFamily.SoftwareProjectModel,
        };

        Assert.Throws<ArgumentException>(() => new EdlOnePlusProjectAuthProvider(profile));
    }

    [Fact]
    public async Task CreateCredentialsAsync_ProfileFieldsExceedingPayloadCapacity_IsRefused()
    {
        var profile = new EdlOnePlusProjectProfile
        {
            ProjectId = "18825",
            ProductionKey = "b2fad511325185e5",
            RandomPostfix = "0iyFR00pPnoqjVNL",
            VersionString = "v" + new string('x', 240),
            Family = EdlOnePlusProjectFamily.ProjectModel,
        };

        var provider = new EdlOnePlusProjectAuthProvider(profile, FixedKey);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.CreateCredentialsAsync(
            EdlProjectAuthenticationKind.OnePlusProject, "18825", 123456UL, null,
            TestContext.Current.CancellationToken));
    }
}