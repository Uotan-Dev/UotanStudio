using System.Text.RegularExpressions;
using UotanStudio.Tests.Firmware.Architecture;
using Xunit;

namespace UotanStudio.Tests.Architecture;

/// <summary>
/// Guards the read-only install-location boundary required by MSIX packages and macOS app bundles:
/// production code never resolves the executable assembly location, and directory-sensitive path
/// derivations stay on the frozen allow lists reviewed for read-only semantics.
/// <para>守护 MSIX 包与 macOS .app 包要求的只读安装位置边界：生产代码不得解析可执行程序集位置，
/// 目录敏感的路径派生只能落在已审阅只读语义的冻结允许列表内。</para>
/// </summary>
/// <remarks>
/// <see cref="AppContext.BaseDirectory"/> entries are read-only probes (native library lookup, bundled asset
/// candidates, build-time tooling); working-directory entries keep CLI semantics or are guarded fallbacks.
/// Adding an entry requires updating the matching list deliberately, together with a review of whether the
/// new usage stays read-only under an immutable install directory.
/// <para><see cref="AppContext.BaseDirectory"/> 条目均为只读探测（原生库定位、随包资源候选、构建期工具）；
/// 工作目录条目保留 CLI 语义或为受控兜底。新增条目必须有意识地更新对应列表，并确认新用法在只读安装目录下依然成立。</para>
/// </remarks>
public sealed class InstallDirectoryBoundaryTests
{
    private static readonly Regex BaseDirectoryPattern = new(@"BaseDirectory", RegexOptions.CultureInvariant);

    private static readonly Regex CurrentDirectoryPattern = new(
        @"Environment\.CurrentDirectory|Directory\.GetCurrentDirectory\(|SetCurrentDirectory",
        RegexOptions.CultureInvariant);

    private static readonly Regex ExecutableLocationPattern = new(
        @"Assembly\.Location|MainModule\.FileName", RegexOptions.CultureInvariant);

    /// <summary>Files allowed to derive paths from <see cref="AppContext.BaseDirectory"/>; all uses are read-only.
    /// <para>允许从 <see cref="AppContext.BaseDirectory"/> 派生路径的文件；全部为只读用途。</para></summary>
    private static readonly IReadOnlyList<string> BaseDirectoryAllowList =
    [
        "UotanStudio.Comm/Transports/Usb/Backend/LibUsb/LibUsbNativeLoader.cs",
        "UotanStudio/Services/Scrcpy/ScrcpyServerService.cs",
        "UotanStudio/Services/FileAssociationService.cs",
        "UotanStudio.Firmware.MagicGen/Program.cs",
    ];

    /// <summary>Files allowed to depend on the process working directory (CLI semantics or guarded fallbacks).
    /// <para>允许依赖进程工作目录的文件（CLI 语义或受控兜底）。</para></summary>
    private static readonly IReadOnlyList<string> CurrentDirectoryAllowList =
    [
        "UotanStudio.Mcp/McpServerConfig.cs",
        "UotanStudio.Comm/Protocols/Hdc/HdcServerRemoteCommand.cs",
        "UotanStudio/ViewModels/Workspace/TerminalViewModel.cs",
        "UotanStudio.Firmware/OtaDelta/Payload/PayloadExtractor.cs",
        "UotanStudio.Firmware/VendorFirmware/NTPi/NtpiWriter.cs",
        "UotanStudio.Firmware.MagicGen/Program.cs",
    ];

    [Fact]
    public void ProductionSources_NeverResolveTheExecutableLocation()
    {
        var offenders = Scan(ExecutableLocationPattern);
        Assert.True(offenders.Count == 0,
            "Production code must not resolve the executable assembly location (empty under NativeAOT single-file); "
            + $"use AppContext.BaseDirectory inside the frozen read-only allow list instead: {Describe(offenders)}.");
    }

    [Fact]
    public void ProductionSources_ConfineBaseDirectory_ToTheFrozenReadOnlyAllowList()
    {
        var offenders = Scan(BaseDirectoryPattern)
            .Where(entry => !BaseDirectoryAllowList.Contains(FilePathOf(entry)))
            .ToArray();
        Assert.True(offenders.Length == 0,
            $"AppContext.BaseDirectory left the frozen read-only allow list; the install directory is immutable under "
            + $"MSIX and macOS bundles: {Describe(offenders)}.");
    }

    [Fact]
    public void ProductionSources_ConfineCurrentDirectory_ToTheFrozenAllowList()
    {
        var offenders = Scan(CurrentDirectoryPattern)
            .Where(entry => !CurrentDirectoryAllowList.Contains(FilePathOf(entry)))
            .ToArray();
        Assert.True(offenders.Length == 0,
            $"Working-directory derivations left the frozen allow list; a packaged desktop launch has no meaningful "
            + $"working directory: {Describe(offenders)}.");
    }

    private static IReadOnlyList<string> Scan(Regex pattern)
    {
        var offenders = new List<string>();
        foreach (var projectDirectory in RepositoryLayout.SolutionProjectDirectories())
        {
            if (projectDirectory is "UotanStudio.Tests" or "UotanStudio.Benchmarks")
            {
                continue;
            }

            foreach (var (relativePath, content) in RepositoryLayout.SourceFiles(projectDirectory))
            {
                offenders.AddRange(RepositoryLayout.Matches(relativePath, content, pattern));
            }
        }

        return offenders;
    }

    private static string FilePathOf(string entry) => entry[..entry.IndexOf(':')];

    private static string Describe(IReadOnlyList<string> offenders) => offenders.Count == 0 ? "<none>" : string.Join("; ", offenders);
}