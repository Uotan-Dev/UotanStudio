namespace UotanStudio.Models;

/// <summary>
/// <para>本应用使用或参考的开源项目清单（关于对话框的开源项目区块数据源，静态只读，AOT 友好）。</para>
/// Catalog of the open-source projects used by or referenced in this application (data source for the
/// open-source section of the About dialog; static and read-only, AOT friendly).
/// </summary>
public static class OpenSourceProjects
{
    /// <summary>
    /// <para>获取开源项目清单。</para>
    /// Gets the open-source project catalog.
    /// </summary>
    public static IReadOnlyList<OpenSourceProject> All { get; } =
    [
        new OpenSourceProject
        {
            Name = "Iciclecreek.Avalonia.Terminal",
            Description = "跨平台终端控件，提供终端绘制、文字选择与交互输入",
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
            Description = "终端 VT/ANSI 协议解析与屏幕缓冲区",
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
            Description = "Windows ConPTY 与 macOS/Linux PTY 进程通信",
            RepositoryUrl = "https://github.com/tomlm/Porta.Pty",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/tomlm/Porta.Pty/blob/main/LICENSE",
            AuthorName = "Tom Laird-McConnell",
            AuthorGitHubUrl = "https://github.com/tomlm",
            AuthorAvatarUrl = "https://github.com/tomlm.png"
        },
        new OpenSourceProject
        {
            Name = "Avalonia",
            Description = "跨平台 UI 框架，本项目桌面端 UI 基于 Avalonia 构建",
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
            Description = "Avalonia 文本编辑器控件，用于代码/文本文件查看与编辑",
            RepositoryUrl = "https://github.com/AvaloniaUI/AvaloniaEdit",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/AvaloniaUI/AvaloniaEdit/blob/master/license.txt",
            AuthorName = "AvaloniaUI",
            AuthorGitHubUrl = "https://github.com/AvaloniaUI",
            AuthorAvatarUrl = "https://github.com/AvaloniaUI.png"
        },
        new OpenSourceProject
        {
            Name = "Semi.Avalonia",
            Description = "Semantic UI 风格的 Avalonia 主题库（本项目的主题与样式层）",
            RepositoryUrl = "https://github.com/irihitech/Semi.Avalonia",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/irihitech/Semi.Avalonia/blob/main/LICENSE",
            AuthorName = "IRIHI Technology",
            AuthorGitHubUrl = "https://github.com/irihitech",
            AuthorAvatarUrl = "https://github.com/irihitech.png"
        },
        new OpenSourceProject
        {
            Name = "Irihi.Ursa",
            Description = "Avalonia 交互控件库（对话框、Toast 等高级控件）",
            RepositoryUrl = "https://github.com/irihitech/Ursa.Avalonia",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/irihitech/Ursa.Avalonia/blob/main/LICENSE",
            AuthorName = "IRIHI Technology",
            AuthorGitHubUrl = "https://github.com/irihitech",
            AuthorAvatarUrl = "https://github.com/irihitech.png"
        },
        new OpenSourceProject
        {
            Name = "Dock",
            Description = "Avalonia 停靠布局系统（工作区面板 Dock 布局）",
            RepositoryUrl = "https://github.com/wieslawsoltes/Dock",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/wieslawsoltes/Dock/blob/master/LICENSE.TXT",
            AuthorName = "Wiesław Šoltés",
            AuthorGitHubUrl = "https://github.com/wieslawsoltes",
            AuthorAvatarUrl = "https://github.com/wieslawsoltes.png"
        },
        new OpenSourceProject
        {
            Name = "NodifyM.Avalonia",
            Description = "节点编辑器控件库（可视化编程画布）",
            RepositoryUrl = "https://github.com/Maklith/NodifyM.Avalonia",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/Maklith/NodifyM.Avalonia/blob/master/LICENSE",
            AuthorName = "MakesYT",
            AuthorGitHubUrl = "https://github.com/Maklith",
            AuthorAvatarUrl = "https://github.com/Maklith.png"
        },
        new OpenSourceProject
        {
            Name = "Material.Icons",
            Description = "Material 设计图标集合（工具栏/菜单图标）",
            RepositoryUrl = "https://github.com/SkankyDev/Material.Icons",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/SkankyDev/Material.Icons/blob/main/LICENSE",
            AuthorName = "SkankyDev",
            AuthorGitHubUrl = "https://github.com/SkankyDev",
            AuthorAvatarUrl = "https://github.com/SkankyDev.png"
        },
        new OpenSourceProject
        {
            Name = "CommunityToolkit.Mvvm",
            Description = "MVVM 工具包（ObservableObject / RelayCommand / WeakReferenceMessenger）",
            RepositoryUrl = "https://github.com/CommunityToolkit/dotnet",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/CommunityToolkit/dotnet/blob/main/LICENSE",
            AuthorName = ".NET Community Toolkit",
            AuthorGitHubUrl = "https://github.com/CommunityToolkit",
            AuthorAvatarUrl = "https://github.com/CommunityToolkit.png"
        },
        new OpenSourceProject
        {
            Name = "LibUsbDotNet",
            Description = ".NET USB 访问库（WinUSB/LibUsb 后端，USB 设备通信）",
            RepositoryUrl = "https://github.com/LibUsbDotNet/LibUsbDotNet",
            LicenseName = "LGPL-3.0",
            LicenseUrl = "https://github.com/LibUsbDotNet/LibUsbDotNet/blob/master/LICENSE",
            AuthorName = "LibUsbDotNet 团队",
            AuthorGitHubUrl = "https://github.com/LibUsbDotNet",
            AuthorAvatarUrl = "https://github.com/LibUsbDotNet.png"
        },
        new OpenSourceProject
        {
            Name = "SharpCompress",
            Description = "压缩/归档库（zip 等格式读写）",
            RepositoryUrl = "https://github.com/adamhathcock/sharpcompress",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/adamhathcock/sharpcompress/blob/master/LICENSE.txt",
            AuthorName = "Adam Hathcock",
            AuthorGitHubUrl = "https://github.com/adamhathcock",
            AuthorAvatarUrl = "https://github.com/adamhathcock.png"
        },
        new OpenSourceProject
        {
            Name = "BouncyCastle.Cryptography",
            Description = "加密算法库（AVB RSASSA-PSS / PKCS#1 v1.5 签名校验）",
            RepositoryUrl = "https://github.com/bcgit/bc-csharp",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/bcgit/bc-csharp/blob/master/LICENSE.md",
            AuthorName = "The Legion of the Bouncy Castle",
            AuthorGitHubUrl = "https://github.com/bcgit",
            AuthorAvatarUrl = "https://github.com/bcgit.png"
        },
        new OpenSourceProject
        {
            Name = "K4os.Compression.LZ4",
            Description = "LZ4 压缩库（LZ4 数据解压）",
            RepositoryUrl = "https://github.com/MiloszKrajewski/K4os.Compression.LZ4",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/master/LICENSE",
            AuthorName = "Milosz Krajewski",
            AuthorGitHubUrl = "https://github.com/MiloszKrajewski",
            AuthorAvatarUrl = "https://github.com/MiloszKrajewski.png"
        },
        new OpenSourceProject
        {
            Name = "LZMA-SDK",
            Description = "LZMA 压缩算法 SDK（LZMA/LZMA2 解压）",
            RepositoryUrl = "https://www.7-zip.org/sdk.html",
            LicenseName = "公有领域",
            LicenseUrl = "https://www.7-zip.org/sdk.html",
            AuthorName = "Igor Pavlov",
            AuthorGitHubUrl = "https://github.com/ip7z",
            AuthorAvatarUrl = "https://github.com/ip7z.png"
        },
        new OpenSourceProject
        {
            Name = "Google.Protobuf",
            Description = "Protocol Buffers 序列化库（OTA payload 等 protobuf 解析）",
            RepositoryUrl = "https://github.com/protocolbuffers/protobuf",
            LicenseName = "BSD-3-Clause",
            LicenseUrl = "https://github.com/protocolbuffers/protobuf/blob/main/LICENSE",
            AuthorName = "Google",
            AuthorGitHubUrl = "https://github.com/protocolbuffers",
            AuthorAvatarUrl = "https://github.com/protocolbuffers.png"
        },
        new OpenSourceProject
        {
            Name = "Makaretu.Dns.Multicast",
            Description = "mDNS/DNS-SD 库（本地网络设备发现）",
            RepositoryUrl = "https://github.com/richardschneider/net-mdns",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/richardschneider/net-mdns/blob/master/LICENSE",
            AuthorName = "Richard Schneider",
            AuthorGitHubUrl = "https://github.com/richardschneider",
            AuthorAvatarUrl = "https://github.com/richardschneider.png"
        },
        new OpenSourceProject
        {
            Name = "xunit",
            Description = "单元测试框架（xunit.v3，项目测试套件）",
            RepositoryUrl = "https://github.com/xunit/xunit",
            LicenseName = "Apache-2.0",
            LicenseUrl = "https://github.com/xunit/xunit/blob/main/LICENSE",
            AuthorName = ".NET Foundation",
            AuthorGitHubUrl = "https://github.com/xunit",
            AuthorAvatarUrl = "https://github.com/xunit.png"
        },
        new OpenSourceProject
        {
            Name = ".NET Runtime",
            Description = ".NET 基础类库与运行时（System.IO.Ports / System.IO.Hashing 等）",
            RepositoryUrl = "https://github.com/dotnet/runtime",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT",
            AuthorName = ".NET Foundation",
            AuthorGitHubUrl = "https://github.com/dotnet",
            AuthorAvatarUrl = "https://github.com/dotnet.png"
        },
        new OpenSourceProject
        {
            Name = "YamlDotNet",
            Description = "YAML 读写库（设备树/配置 YAML 解析）",
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
            Description = "文本差异比较库（文件对比服务的行级 diff）",
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
            Description = "纯托管 FAT12/16/32 与 exFAT 卷实现（FAT 分区读写）",
            RepositoryUrl = "https://github.com/LTRData/DiscUtils",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/LTRData/DiscUtils/blob/master/LICENSE",
            AuthorName = "LTRData (Kenneth Bell / original DiscUtils)",
            AuthorGitHubUrl = "https://github.com/LTRData",
            AuthorAvatarUrl = "https://github.com/LTRData.png"
        },
        new OpenSourceProject
        {
            Name = "Model Context Protocol C# SDK",
            Description = "官方 MCP C# SDK（内嵌 MCP 服务器）",
            RepositoryUrl = "https://github.com/modelcontextprotocol/csharp-sdk",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/modelcontextprotocol/csharp-sdk/blob/main/LICENSE",
            AuthorName = "Model Context Protocol",
            AuthorGitHubUrl = "https://github.com/modelcontextprotocol",
            AuthorAvatarUrl = "https://github.com/modelcontextprotocol.png"
        },
        new OpenSourceProject
        {
            Name = "BenchmarkDotNet",
            Description = "性能基准测试框架（基准测试项目）",
            RepositoryUrl = "https://github.com/dotnet/BenchmarkDotNet",
            LicenseName = "MIT",
            LicenseUrl = "https://github.com/dotnet/BenchmarkDotNet/blob/master/LICENSE.md",
            AuthorName = ".NET Foundation",
            AuthorGitHubUrl = "https://github.com/dotnet",
            AuthorAvatarUrl = "https://github.com/dotnet.png"
        }
    ];
}