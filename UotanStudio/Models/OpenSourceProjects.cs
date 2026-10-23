namespace UotanStudio.Models;

/// <summary>
/// Catalog of open-source project entries, static and read-only.
/// <para>开源项目条目清单，静态且只读。</para>
/// </summary>
public static class OpenSourceProjects
{
    /// <summary>
    /// Gets the open-source project catalog.
    /// <para>获取开源项目清单。</para>
    /// </summary>
    /// <value>The open-source project entries.<para>开源项目条目列表。</para></value>
    public static IReadOnlyList<OpenSourceProject> All { get; } =
    [
        new OpenSourceProject
        {
            Name = "Avalonia",
            DescriptionKey = "About_Project_Avalonia",
            RepositoryUrl = "https://github.com/AvaloniaUI/Avalonia",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md",
            AuthorName = "AvaloniaUI",
            AuthorGitHubUrl = "https://github.com/AvaloniaUI",
            AuthorAvatarUrl = "https://github.com/AvaloniaUI.png"
        },
        new OpenSourceProject
        {
            Name = "AvaloniaEdit",
            DescriptionKey = "About_Project_AvaloniaEdit",
            RepositoryUrl = "https://github.com/AvaloniaUI/AvaloniaEdit",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/AvaloniaUI/AvaloniaEdit/blob/master/license.txt",
            AuthorName = "AvaloniaUI",
            AuthorGitHubUrl = "https://github.com/AvaloniaUI",
            AuthorAvatarUrl = "https://github.com/AvaloniaUI.png"
        },
        new OpenSourceProject
        {
            Name = "Dock",
            DescriptionKey = "About_Project_Dock",
            RepositoryUrl = "https://github.com/wieslawsoltes/Dock",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/wieslawsoltes/Dock/blob/master/LICENSE.TXT",
            AuthorName = "Wiesław Šoltés",
            AuthorGitHubUrl = "https://github.com/wieslawsoltes",
            AuthorAvatarUrl = "https://github.com/wieslawsoltes.png"
        },
        new OpenSourceProject
        {
            Name = "Semi.Avalonia",
            DescriptionKey = "About_Project_SemiAvalonia",
            RepositoryUrl = "https://github.com/irihitech/Semi.Avalonia",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/irihitech/Semi.Avalonia/blob/main/LICENSE",
            AuthorName = "IRIHI Technology Co., Ltd.",
            AuthorGitHubUrl = "https://github.com/irihitech",
            AuthorAvatarUrl = "https://github.com/irihitech.png"
        },
        new OpenSourceProject
        {
            Name = "Semi.Avalonia.DataGrid",
            DescriptionKey = "About_Project_SemiAvaloniaDataGrid",
            RepositoryUrl = "https://github.com/irihitech/Semi.Avalonia",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/irihitech/Semi.Avalonia/blob/main/LICENSE",
            AuthorName = "IRIHI Technology Co., Ltd.",
            AuthorGitHubUrl = "https://github.com/irihitech",
            AuthorAvatarUrl = "https://github.com/irihitech.png"
        },
        new OpenSourceProject
        {
            Name = "Semi.Avalonia.AvaloniaEdit",
            DescriptionKey = "About_Project_SemiAvaloniaAvaloniaEdit",
            RepositoryUrl = "https://www.nuget.org/packages/Semi.Avalonia.AvaloniaEdit/12.0.0",
            LicenseName = "Proprietary (IRIHI EULA)",
            LicenseUrl = "https://www.nuget.org/packages/Semi.Avalonia.AvaloniaEdit/12.0.0",
            AuthorName = "IRIHI Technology Co., Ltd.",
            AuthorGitHubUrl = "https://github.com/irihitech",
            AuthorAvatarUrl = "https://github.com/irihitech.png"
        },
        new OpenSourceProject
        {
            Name = "Semi.Avalonia.Dock",
            DescriptionKey = "About_Project_SemiAvaloniaDock",
            RepositoryUrl = "https://www.nuget.org/packages/Semi.Avalonia.Dock/12.0.0.2",
            LicenseName = "Proprietary (IRIHI EULA)",
            LicenseUrl = "https://www.nuget.org/packages/Semi.Avalonia.Dock/12.0.0.2",
            AuthorName = "IRIHI Technology Co., Ltd.",
            AuthorGitHubUrl = "https://github.com/irihitech",
            AuthorAvatarUrl = "https://github.com/irihitech.png"
        },
        new OpenSourceProject
        {
            Name = "Irihi.Ursa",
            DescriptionKey = "About_Project_IrihiUrsa",
            RepositoryUrl = "https://github.com/irihitech/Ursa.Avalonia",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/irihitech/Ursa.Avalonia/blob/main/LICENSE",
            AuthorName = "IRIHI Technology Co., Ltd.",
            AuthorGitHubUrl = "https://github.com/irihitech",
            AuthorAvatarUrl = "https://github.com/irihitech.png"
        },
        new OpenSourceProject
        {
            Name = "Irihi.Iconica.TDesign",
            DescriptionKey = "About_Project_IrihiIconicaTDesign",
            RepositoryUrl = "https://www.nuget.org/packages/Irihi.Iconica.TDesign/1.0.0",
            LicenseName = "Apache-2.0",
            LicenseUrl = "https://licenses.nuget.org/Apache-2.0",
            AuthorName = "IRIHI Technology Co., Ltd.",
            AuthorGitHubUrl = "https://github.com/irihitech",
            AuthorAvatarUrl = "https://github.com/irihitech.png"
        },
        new OpenSourceProject
        {
            Name = "NodifyM.Avalonia",
            DescriptionKey = "About_Project_NodifyMAvalonia",
            RepositoryUrl = "https://github.com/MakesYT/NodifyM.Avalonia",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/MakesYT/NodifyM.Avalonia/blob/master/LICENSE",
            AuthorName = "Maklith (MakesYT)",
            AuthorGitHubUrl = "https://github.com/Maklith",
            AuthorAvatarUrl = "https://github.com/Maklith.png"
        },
        new OpenSourceProject
        {
            Name = "Iciclecreek.Avalonia.Terminal",
            DescriptionKey = "About_Project_IciclecreekTerminal",
            RepositoryUrl = "https://github.com/tomlm/Iciclecreek.Avalonia.Terminal",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/blob/main/LICENSE",
            AuthorName = "Tom Laird-McConnell",
            AuthorGitHubUrl = "https://github.com/tomlm",
            AuthorAvatarUrl = "https://github.com/tomlm.png"
        },
        new OpenSourceProject
        {
            Name = "XTerm.NET",
            DescriptionKey = "About_Project_XTermNet",
            RepositoryUrl = "https://github.com/tomlm/XTerm.NET",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/tomlm/XTerm.NET/blob/main/LICENSE",
            AuthorName = "Tom Laird-McConnell",
            AuthorGitHubUrl = "https://github.com/tomlm",
            AuthorAvatarUrl = "https://github.com/tomlm.png"
        },
        new OpenSourceProject
        {
            Name = "Porta.Pty",
            DescriptionKey = "About_Project_PortaPty",
            RepositoryUrl = "https://github.com/tomlm/Porta.Pty",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/tomlm/Porta.Pty/blob/main/LICENSE",
            AuthorName = "Tom Laird-McConnell",
            AuthorGitHubUrl = "https://github.com/tomlm",
            AuthorAvatarUrl = "https://github.com/tomlm.png"
        },
        new OpenSourceProject
        {
            Name = "CommunityToolkit.Mvvm",
            DescriptionKey = "About_Project_CommunityToolkitMvvm",
            RepositoryUrl = "https://github.com/CommunityToolkit/dotnet",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/CommunityToolkit/dotnet/blob/main/LICENSE",
            AuthorName = ".NET Community Toolkit",
            AuthorGitHubUrl = "https://github.com/CommunityToolkit",
            AuthorAvatarUrl = "https://github.com/CommunityToolkit.png"
        },
        new OpenSourceProject
        {
            Name = "Irihi.Lingua",
            DescriptionKey = "About_Project_IrihiLingua",
            RepositoryUrl = "https://github.com/irihitech/Irihi.Lingua",
            LicenseName = "MIT",
            LicenseUrl = "https://licenses.nuget.org/MIT",
            AuthorName = "IRIHI Technology Co., Ltd.",
            AuthorGitHubUrl = "https://github.com/irihitech",
            AuthorAvatarUrl = "https://github.com/irihitech.png"
        },
        new OpenSourceProject
        {
            Name = "LuaCSharp",
            DescriptionKey = "About_Project_LuaCSharp",
            RepositoryUrl = "https://github.com/nuskey8/Lua-CSharp",
            LicenseName = "MIT",
            LicenseUrl = "https://licenses.nuget.org/MIT",
            AuthorName = "Yusuke Nakada (nuskey8)",
            AuthorGitHubUrl = "https://github.com/nuskey8",
            AuthorAvatarUrl = "https://github.com/nuskey8.png"
        },
        new OpenSourceProject
        {
            Name = "QRCoder",
            DescriptionKey = "About_Project_QrCoder",
            RepositoryUrl = "https://github.com/Shane32/QRCoder",
            LicenseName = "MIT",
            LicenseUrl = "https://licenses.nuget.org/MIT",
            AuthorName = "Raffael Herrmann / Shane Krueger",
            AuthorGitHubUrl = "https://github.com/Shane32",
            AuthorAvatarUrl = "https://github.com/Shane32.png"
        },
        new OpenSourceProject
        {
            Name = "LibUsbDotNet",
            DescriptionKey = "About_Project_LibUsbDotNet",
            RepositoryUrl = "https://github.com/LibUsbDotNet/LibUsbDotNet",
            LicenseName = "LGPL-3.0-or-later",
            LicenseUrl = "https://licenses.nuget.org/LGPL-3.0-or-later",
            AuthorName = "LibUsbDotNet contributors",
            AuthorGitHubUrl = "https://github.com/LibUsbDotNet",
            AuthorAvatarUrl = "https://github.com/LibUsbDotNet.png"
        },
        new OpenSourceProject
        {
            Name = "libusb",
            DescriptionKey = "About_Project_Libusb",
            RepositoryUrl = "https://github.com/libusb/libusb",
            LicenseName = "LGPL-2.1-or-later",
            LicenseUrl = "https://github.com/libusb/libusb/blob/master/COPYING",
            AuthorName = "libusb contributors",
            AuthorGitHubUrl = "https://github.com/libusb",
            AuthorAvatarUrl = "https://github.com/libusb.png"
        },
        new OpenSourceProject
        {
            Name = "Makaretu.Dns.Multicast",
            DescriptionKey = "About_Project_MakaretuDnsMulticast",
            RepositoryUrl = "https://github.com/richardschneider/net-mdns",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/richardschneider/net-mdns/blob/master/LICENSE",
            AuthorName = "Richard Schneider",
            AuthorGitHubUrl = "https://github.com/richardschneider",
            AuthorAvatarUrl = "https://github.com/richardschneider.png"
        },
        new OpenSourceProject
        {
            Name = "BouncyCastle.Cryptography",
            DescriptionKey = "About_Project_BouncyCastleCryptography",
            RepositoryUrl = "https://github.com/bcgit/bc-csharp",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/bcgit/bc-csharp/blob/master/LICENSE.md",
            AuthorName = "The Legion of the Bouncy Castle",
            AuthorGitHubUrl = "https://github.com/bcgit",
            AuthorAvatarUrl = "https://github.com/bcgit.png"
        },
        new OpenSourceProject
        {
            Name = "SharpCompress",
            DescriptionKey = "About_Project_SharpCompress",
            RepositoryUrl = "https://github.com/adamhathcock/sharpcompress",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/adamhathcock/sharpcompress/blob/master/LICENSE.txt",
            AuthorName = "Adam Hathcock",
            AuthorGitHubUrl = "https://github.com/adamhathcock",
            AuthorAvatarUrl = "https://github.com/adamhathcock.png"
        },
        new OpenSourceProject
        {
            Name = "K4os.Compression.LZ4",
            DescriptionKey = "About_Project_K4osCompressionLz4",
            RepositoryUrl = "https://github.com/MiloszKrajewski/K4os.Compression.LZ4",
            LicenseName = "MIT / BSD-2-Clause (liblz4)",
            LicenseUrl = "https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/master/LICENSE",
            AuthorName = "Milosz Krajewski / Yann Collet",
            AuthorGitHubUrl = "https://github.com/MiloszKrajewski",
            AuthorAvatarUrl = "https://github.com/MiloszKrajewski.png"
        },
        new OpenSourceProject
        {
            Name = "ZstdSharp.Port",
            DescriptionKey = "About_Project_ZstdSharpPort",
            RepositoryUrl = "https://github.com/oleg-st/ZstdSharp",
            LicenseName = "MIT / BSD-3-Clause (zstd)",
            LicenseUrl = "https://github.com/oleg-st/ZstdSharp/blob/master/LICENSE",
            AuthorName = "Oleg Stepanischev / Meta Platforms",
            AuthorGitHubUrl = "https://github.com/oleg-st",
            AuthorAvatarUrl = "https://github.com/oleg-st.png"
        },
        new OpenSourceProject
        {
            Name = "LZMA-SDK (.NET)",
            DescriptionKey = "About_Project_LzmaSdk",
            RepositoryUrl = "https://github.com/monemihir/LZMA-SDK",
            LicenseName = "MIT (wrapper) / Public Domain (7-Zip SDK)",
            LicenseUrl = "https://licenses.nuget.org/MIT",
            AuthorName = "Mihir Mone / Igor Pavlov",
            AuthorGitHubUrl = "https://github.com/monemihir",
            AuthorAvatarUrl = "https://github.com/monemihir.png"
        },
        new OpenSourceProject
        {
            Name = "Google.Protobuf",
            DescriptionKey = "About_Project_GoogleProtobuf",
            RepositoryUrl = "https://github.com/protocolbuffers/protobuf",
            LicenseName = "BSD-3-Clause",
            LicenseUrl = "https://github.com/protocolbuffers/protobuf/blob/main/LICENSE",
            AuthorName = "Google",
            AuthorGitHubUrl = "https://github.com/protocolbuffers",
            AuthorAvatarUrl = "https://github.com/protocolbuffers.png"
        },
        new OpenSourceProject
        {
            Name = "BsDiff (.NET port)",
            DescriptionKey = "About_Project_BsDiff",
            RepositoryUrl = "https://github.com/LogosBible/bsdiff.net",
            LicenseName = "MIT / BSD-2-Clause (bsdiff)",
            LicenseUrl = "https://licenses.nuget.org/MIT",
            AuthorName = "Logos Bible Software / Colin Percival",
            AuthorGitHubUrl = "https://github.com/LogosBible",
            AuthorAvatarUrl = "https://github.com/LogosBible.png"
        },
        new OpenSourceProject
        {
            Name = "YamlDotNet",
            DescriptionKey = "About_Project_YamlDotNet",
            RepositoryUrl = "https://github.com/aaubry/YamlDotNet",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/aaubry/YamlDotNet/blob/master/LICENSE.txt",
            AuthorName = "Antoine Aubry",
            AuthorGitHubUrl = "https://github.com/aaubry",
            AuthorAvatarUrl = "https://github.com/aaubry.png"
        },
        new OpenSourceProject
        {
            Name = "DiffPlex",
            DescriptionKey = "About_Project_DiffPlex",
            RepositoryUrl = "https://github.com/mmanela/diffplex",
            LicenseName = "Apache-2.0",
            LicenseUrl = "https://github.com/mmanela/diffplex/blob/master/License.txt",
            AuthorName = "Matthew Manela",
            AuthorGitHubUrl = "https://github.com/mmanela",
            AuthorAvatarUrl = "https://github.com/mmanela.png"
        },
        new OpenSourceProject
        {
            Name = "DiscUtils (LTRData fork)",
            DescriptionKey = "About_Project_DiscUtils",
            RepositoryUrl = "https://github.com/LTRData/DiscUtils",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/LTRData/DiscUtils/blob/master/LICENSE",
            AuthorName = "LTRData (Kenneth Bell / original DiscUtils)",
            AuthorGitHubUrl = "https://github.com/LTRData",
            AuthorAvatarUrl = "https://github.com/LTRData.png"
        },
        new OpenSourceProject
        {
            Name = "ModelContextProtocol C# SDK",
            DescriptionKey = "About_Project_ModelContextProtocolSdk",
            RepositoryUrl = "https://github.com/modelcontextprotocol/csharp-sdk",
            LicenseName = "Apache-2.0",
            LicenseUrl = "https://licenses.nuget.org/Apache-2.0",
            AuthorName = "Model Context Protocol / LF Projects",
            AuthorGitHubUrl = "https://github.com/modelcontextprotocol",
            AuthorAvatarUrl = "https://github.com/modelcontextprotocol.png"
        },
        new OpenSourceProject
        {
            Name = ".NET Runtime",
            DescriptionKey = "About_Project_DotNetRuntime",
            RepositoryUrl = "https://github.com/dotnet/runtime",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT",
            AuthorName = ".NET Foundation",
            AuthorGitHubUrl = "https://github.com/dotnet",
            AuthorAvatarUrl = "https://github.com/dotnet.png"
        },
        new OpenSourceProject
        {
            Name = "SkiaSharp",
            DescriptionKey = "About_Project_SkiaSharp",
            RepositoryUrl = "https://github.com/mono/SkiaSharp",
            LicenseName = "MIT / BSD-3-Clause (Skia)",
            LicenseUrl = "https://github.com/mono/SkiaSharp/blob/main/LICENSE.md",
            AuthorName = "Microsoft / Skia",
            AuthorGitHubUrl = "https://github.com/mono",
            AuthorAvatarUrl = "https://github.com/mono.png"
        },
        new OpenSourceProject
        {
            Name = "HarfBuzzSharp",
            DescriptionKey = "About_Project_HarfBuzzSharp",
            RepositoryUrl = "https://github.com/mono/SkiaSharp",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/mono/SkiaSharp/blob/main/LICENSE.md",
            AuthorName = "Microsoft / HarfBuzz contributors",
            AuthorGitHubUrl = "https://github.com/mono",
            AuthorAvatarUrl = "https://github.com/mono.png"
        },
        new OpenSourceProject
        {
            Name = "ANGLE",
            DescriptionKey = "About_Project_Angle",
            RepositoryUrl = "https://github.com/google/angle",
            LicenseName = "BSD-3-Clause",
            LicenseUrl = "https://github.com/google/angle/blob/main/LICENSE",
            AuthorName = "The ANGLE Project Authors",
            AuthorGitHubUrl = "https://github.com/google",
            AuthorAvatarUrl = "https://github.com/google.png"
        },
        new OpenSourceProject
        {
            Name = "Oniguruma (Onigwrap)",
            DescriptionKey = "About_Project_Oniguruma",
            RepositoryUrl = "https://github.com/kkos/oniguruma",
            LicenseName = "BSD-2-Clause",
            LicenseUrl = "https://github.com/kkos/oniguruma/blob/master/COPYING",
            AuthorName = "K.Kosako",
            AuthorGitHubUrl = "https://github.com/kkos",
            AuthorAvatarUrl = "https://github.com/kkos.png"
        },
        new OpenSourceProject
        {
            Name = "Microsoft Terminal (ConPTY)",
            DescriptionKey = "About_Project_MicrosoftTerminal",
            RepositoryUrl = "https://github.com/microsoft/terminal",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/microsoft/terminal/blob/main/LICENSE",
            AuthorName = "Microsoft",
            AuthorGitHubUrl = "https://github.com/microsoft",
            AuthorAvatarUrl = "https://github.com/microsoft.png"
        },
        new OpenSourceProject
        {
            Name = "MiSans 字体",
            DescriptionKey = "About_Project_MiSansFont",
            RepositoryUrl = "https://hyperos.mi.com/font/",
            LicenseName = "Proprietary (MiSans Fonts IP License)",
            LicenseUrl = "https://hyperos.mi.com/font/zh/download/",
            AuthorName = "Xiaomi Inc.",
            AuthorGitHubUrl = "https://hyperos.mi.com/font/",
            AuthorAvatarUrl = "https://github.com/Xiaomi.png"
        },
        new OpenSourceProject
        {
            Name = "Android update-binary (AOSP)",
            DescriptionKey = "About_Project_AndroidUpdateBinary",
            RepositoryUrl = "https://android.googlesource.com/platform/bootable/recovery",
            LicenseName = "Apache-2.0",
            LicenseUrl = "https://www.apache.org/licenses/LICENSE-2.0",
            AuthorName = "The Android Open Source Project",
            AuthorGitHubUrl = "https://source.android.com/",
            AuthorAvatarUrl = "https://github.com/aosp-mirror.png"
        },
        new OpenSourceProject
        {
            Name = "xunit",
            DescriptionKey = "About_Project_Xunit",
            RepositoryUrl = "https://github.com/xunit/xunit",
            LicenseName = "Apache-2.0",
            LicenseUrl = "https://github.com/xunit/xunit/blob/main/LICENSE",
            AuthorName = ".NET Foundation",
            AuthorGitHubUrl = "https://github.com/xunit",
            AuthorAvatarUrl = "https://github.com/xunit.png"
        },
        new OpenSourceProject
        {
            Name = "BenchmarkDotNet",
            DescriptionKey = "About_Project_BenchmarkDotNet",
            RepositoryUrl = "https://github.com/dotnet/BenchmarkDotNet",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/dotnet/BenchmarkDotNet/blob/master/LICENSE.md",
            AuthorName = ".NET Foundation",
            AuthorGitHubUrl = "https://github.com/dotnet",
            AuthorAvatarUrl = "https://github.com/dotnet.png"
        }
    ];
}