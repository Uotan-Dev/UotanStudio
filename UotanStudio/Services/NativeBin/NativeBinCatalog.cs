using System.Runtime.InteropServices;

namespace UotanStudio.Services.NativeBin;

/// <summary>
/// One URL the provisioner can attempt in order; mirrors are marked
/// <see cref="IsMirror"/> so callers can filter them out when
/// <c>nativeBin.useMirrorList=false</c>.
/// <para>下载器按顺序尝试的单个 URL；镜像通过 <see cref="IsMirror"/> 标记，
/// 便于在 <c>nativeBin.useMirrorList=false</c> 时过滤。</para>
/// </summary>
/// <param name="Url">Absolute HTTP(S) URL to fetch.
/// <para>用于下载的完整 HTTP(S) 地址。</para></param>
/// <param name="IsMirror"><c>true</c> for a non-official mirror of the official release file.
/// <para>为 <c>true</c> 时表示非官方镜像。</para></param>
public sealed record NativeBinUrl(string Url, bool IsMirror);

/// <summary>Catalog entry describing one supported libusb runtime for a .NET RID: expected SHA-256 and the ordered list of URLs to attempt.
/// <para>描述某个 .NET RID 支持的 libusb 运行时的清单条目：预期 SHA-256 及按顺序尝试的 URL 列表。</para></summary>
/// <param name="Rid">The target runtime identifier, for instance win-x64.
/// <para>目标运行时标识，例如 win-x64。</para></param>
/// <param name="FileName">File name of the shared library to fetch.<para>要获取的共享库文件名。</para></param>
/// <param name="ExpectedSha256">Lowercase 64-char SHA-256 hex digest of the artifact.
/// <para>产物小写 64 字符 SHA-256 十六进制摘要。</para></param>
/// <param name="Urls">Ordered URLs to try; first entry is the canonical official release.
/// <para>按顺序尝试的 URL 列表；首项为官方 Release。</para></param>
public sealed record NativeBinEntry(string Rid, string FileName, string ExpectedSha256, IReadOnlyList<NativeBinUrl> Urls);

/// <summary>
/// Static catalog of libusb runtime artifacts for each supported runtime identifier.
/// <para>各受支持运行时标识的 libusb 运行库静态清单。</para>
/// </summary>
public static class NativeBinCatalog
{
    private const string LibUsbVersion = "1.0.30";
    private const string OfficialBase = "https://github.com/libusb/libusb/releases/download";
    private const string OfficialTag = $"v{LibUsbVersion}";

    private static readonly IReadOnlyList<NativeBinEntry> AllEntries = BuildEntries();

    /// <summary>
    /// Returns all supported libusb entries, one per RID, as a stable snapshot that
    /// must not be mutated.
    /// <para>返回全部受支持的 libusb 条目（每个 RID 一条），为不可修改的稳定快照。</para>
    /// </summary>
    /// <value>The complete catalog entries.<para>完整的清单条目。</para></value>
    public static IReadOnlyList<NativeBinEntry> All => AllEntries;

    /// <summary>
    /// Looks up the catalog entry for a RID; <c>null</c> when the RID is not supported.
    /// <para>按 RID 查询清单条目；不受支持的 RID 返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="rid">RID to look up. <para>要查询的 RID。</para></param>
    /// <returns>The matching entry, or null for an unsupported RID.<para>匹配的条目，RID 不受支持时为空。</para></returns>
    public static NativeBinEntry? ByRid(string rid)
    {
        if (string.IsNullOrEmpty(rid))
        {
            return null;
        }

        foreach (var entry in AllEntries)
        {
            if (string.Equals(entry.Rid, rid, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the current process RID as understood by the catalog; throws
    /// <see cref="PlatformNotSupportedException"/> when the process RID is not in
    /// <see cref="All"/>.
    /// <para>返回当前进程 RID（在清单中可识别的形式）；若不在 <see cref="All"/> 中
    /// 则抛出 <see cref="PlatformNotSupportedException"/>。</para>
    /// </summary>
    /// <returns>The supported runtime identifier of this process.<para>当前进程受支持的运行时标识。</para></returns>
    /// <exception cref="PlatformNotSupportedException">
    /// The process RID is not supported by this catalog.
    /// <para>当前进程 RID 不受本清单支持。</para></exception>
    public static string CurrentRid()
    {
        var raw = RuntimeInformation.RuntimeIdentifier;
        if (ByRid(raw) is not null)
        {
            return raw;
        }

        foreach (var entry in AllEntries)
        {
            if (raw.StartsWith(entry.Rid, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Rid;
            }
        }

        throw new PlatformNotSupportedException(
            $"No bundled libusb entry for runtime identifier '{raw}'. " +
            $"Supported RIDs: {string.Join(", ", AllEntries.Select(e => e.Rid))}.");
    }

    private static IReadOnlyList<NativeBinEntry> BuildEntries()
    {

        return new NativeBinEntry[]
        {
            new("win-x64", "libusb-1.0.dll",
                "5bd409849825009b6fe25861a6147f76d256aab248f07049d78387e3bff12d94",
                BuildUrls($"{OfficialBase}/{OfficialTag}/libusb-{LibUsbVersion}-bin-win-x64.zip", "libusb-1.0.dll")),
            new("win-arm64", "libusb-1.0.dll",
                "8d0753f3167516efb000a811eaf7feed9e6635d6223408e8c608c560f67464e5",
                BuildUrls($"{OfficialBase}/{OfficialTag}/libusb-{LibUsbVersion}-bin-win-arm64.zip", "libusb-1.0.dll")),
            new("osx-arm64", "libusb-1.0.dylib",
                "c7b2c2bcc0b6ba8dae266b4d43669021e86d178f841ea89d2141012f995ec2fd",
                BuildUrls($"{OfficialBase}/{OfficialTag}/libusb-{LibUsbVersion}-bin-macos-arm64.zip", "libusb-1.0.dylib")),
            new("osx-x64", "libusb-1.0.dylib",
                "53e6b188649a4302ba099673e2840c4d09c0475b59bfa967270175d171e2d4fc",
                BuildUrls($"{OfficialBase}/{OfficialTag}/libusb-{LibUsbVersion}-bin-macos-x64.zip", "libusb-1.0.dylib")),
            new("linux-x64", "libusb-1.0.so",
                "387f0a81419995f2fb5fa704685e10d62dd328d7041f53faaa19d423f155fcfc",
                BuildUrls($"{OfficialBase}/{OfficialTag}/libusb-{LibUsbVersion}-bin-linux-x64.tar.gz", "libusb-1.0.so")),
            new("linux-arm64", "libusb-1.0.so",
                "35af666de298c6ca1682f70f94e98604d64f02b0f44f826dafd745c8ebaa05f1",
                BuildUrls($"{OfficialBase}/{OfficialTag}/libusb-{LibUsbVersion}-bin-linux-arm64.tar.gz", "libusb-1.0.so")),
            new("linux-riscv64", "libusb-1.0.so",
                "aa2edaa881324bb9c1fdd09ea3e7df5010c2443a91805cefafe8c401c7be15c9",
                BuildUrls($"https://github.com/libusb/libusb/releases/latest/download/libusb-{LibUsbVersion}-bin-linux-riscv64.tar.gz", "libusb-1.0.so")),
            new("linux-loong64", "libusb-1.0.so",
                "8743ac1e3ec85e8d4285e8f34c94c5668a9afed20051f7aea54968febb2cb897",
                BuildUrls($"https://github.com/libusb/libusb/releases/latest/download/libusb-{LibUsbVersion}-bin-linux-loong64.tar.gz", "libusb-1.0.so")),
        };
    }

    private static IReadOnlyList<NativeBinUrl> BuildUrls(string official, string fileName)
    {
        var proxies = new[]
        {
            "https://gh-proxy.com/",
            "https://mirror.ghproxy.com/",
            "https://ghfast.top/",
            "https://ghproxy.net/",
        };
        var urls = new List<NativeBinUrl>(proxies.Length + 1)
        {
            new(official, IsMirror: false),
        };
        foreach (var proxy in proxies)
        {
            urls.Add(new(NormalizeMirrorUrl(proxy, official, fileName), IsMirror: true));
        }
        return urls;
    }

    private static string NormalizeMirrorUrl(string proxyBase, string official, string fileName)
    {
        const string githubHost = "https://github.com/";
        var relativePath = official.StartsWith(githubHost, StringComparison.OrdinalIgnoreCase)
            ? official[githubHost.Length..]
            : official;
        return proxyBase + githubHost + relativePath;
    }
}