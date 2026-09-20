using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using UotanStudio.Firmware.Infrastructure.Downloads;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Services.Delivery;

/// <summary><para>钉住的工具发布产物：zip 归档或原始单文件二进制。</para>Pinned tool artifact: a zip archive or a raw single-file binary.</summary>
internal sealed record DeliveryToolPackage(string Name, string Url, string Sha256, string Prefix, bool Raw = false, string? Member = null);

/// <summary><para>把钉住的工具发布产物下载到可校验的归档缓存，并把选中的平台物化到 staging 目录。
/// 关键工具的来源与 SHA-256 在下方静态字段逐一钉住；Windows 使用官方 win64 包，
/// Linux/macOS 部分二进制使用 aspect-build 的 musl 静态链接单文件发布。</para>
/// Downloads pinned tool releases into a verified archive cache and materializes the selected platforms into staging.
/// Pinned sources and SHA-256 checksums are declared per package below; Windows uses the official win64 bundle, while
/// some Linux/macOS binaries use aspect-build's musl static single-file releases.</summary>
internal sealed class DeliveryToolService(HttpClient client, string cacheDirectory) : IDeliveryTools
{
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    internal static readonly DeliveryToolPackage Windows = new("platform-tools-36.0.2-win",
        "https://dl.google.com/android/repository/platform-tools_r36.0.2-win.zip",
        "B024D4F319D6AD3004DE1BA7B96A5C7C5F3512E8B14126308D598B4AB93DCEAD", "platform-tools/");
    internal static readonly DeliveryToolPackage Linux = new("platform-tools-36.0.2-linux",
        "https://dl.google.com/android/repository/platform-tools_r36.0.2-linux.zip",
        "3AFDEA91441815AB41254193DF0343D92C1B1C0D0237165C3A345C8AF8891C31", "platform-tools/");
    internal static readonly DeliveryToolPackage MacOS = new("platform-tools-36.0.2-darwin",
        "https://dl.google.com/android/repository/platform-tools_r36.0.2-darwin.zip",
        "106A5D31FAD8C1C0C5A180D06F5779767D129D7D5EDBE629005C11A85EEC5B4B", "platform-tools/");

    internal static readonly DeliveryToolPackage ZstdWindows = new("zstd-1.5.7-win64",
        "https://github.com/facebook/zstd/releases/download/v1.5.7/zstd-v1.5.7-win64.zip",
        "ACB4E8111511749DC7A3EBEDCA9B04190E37A17AFEB73F55D4425DBF0B90FAD9", "zstd-v1.5.7-win64/", Member: "zstd.exe");
    internal static readonly DeliveryToolPackage ZstdLinux = new("zstd-1.5.6-linux-amd64",
        "https://github.com/aspect-build/zstd-prebuilt/releases/download/v1.5.6-bcr2/zstd_linux_amd64",
        "190C196B46883A234238D9C52A211C1494DF79BB170FBE30C6FC22639D5D4731", "", Raw: true);
    internal static readonly DeliveryToolPackage ZstdLinuxArm64 = new("zstd-1.5.6-linux-arm64",
        "https://github.com/aspect-build/zstd-prebuilt/releases/download/v1.5.6-bcr2/zstd_linux_arm64",
        "4FA2386A975C2120F91F3103BD4268A19A341CB9063417F3829018E94F28C8C6", "", Raw: true);
    internal static readonly DeliveryToolPackage ZstdLinuxLoong64 = new("zstd-1.5.6-linux-loong64",
        "https://github.com/wojiushixiaobai/zstd-loongarch64/releases/download/v1.5.6/zstd-v1.5.6-linux-loongarch64",
        "A9760DB4F501F5CAAD6935BD105CA691A2AD2D05DFA96DD2B58209C4ACCCF326", "", Raw: true);
    internal static readonly DeliveryToolPackage ZstdMacArm = new("zstd-1.5.6-darwin-arm64",
        "https://github.com/aspect-build/zstd-prebuilt/releases/download/v1.5.6-bcr2/zstd_darwin_arm64",
        "9848858F7332EFFB071818F282B3AC3052085E3DAFA9AE09F0060B7E211A80DC", "", Raw: true);
    internal static readonly DeliveryToolPackage ZstdMacAmd64 = new("zstd-1.5.6-darwin-amd64",
        "https://github.com/aspect-build/zstd-prebuilt/releases/download/v1.5.6-bcr2/zstd_darwin_amd64",
        "D81BE8618553D7D658E09DA669FFD68EC76D8095161A590C9B8A1A99FCE67CA8", "", Raw: true);

    /// <summary>
    /// <para>为启用的平台准备 platform-tools 与（选择压缩镜像时）zstd，裁掉不用的二进制，
    /// 并按 Recovery/组织式 Super 的约定把所需 Linux 静态 zstd 别名物化到 bin/android。</para>
    /// Prepares platform-tools and, when compressed images are selected, zstd for each enabled platform, trims unused
    /// binaries, and materializes the Linux static zstd aliases into bin/android per the Recovery/organized Super convention.
    /// </summary>
    public async Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token)
    {
        var platforms = new List<(string Name, DeliveryToolPackage Package)>();
        if (plan.Windows) platforms.Add(("Windows", Windows));
        if (plan.Linux) platforms.Add(("Linux", Linux));
        if (plan.MacOS) platforms.Add(("macOS", MacOS));
        if (plan.PackSuper && plan.OrganizeDirectory && plan.Super.Zstd && !plan.Linux)
            platforms.Add(("Linux", Linux));
        foreach (var (name, package) in platforms)
        {
            progress?.Report(new(75, "Preparing tools", Stage: "Delivery_ProgressTools", Detail: name));
            var target = Path.Combine(directory, "bin", name);
            var archive = await AcquireAsync(package, progress, token).ConfigureAwait(false);
            await ExtractAsync(archive, package.Prefix, target, token).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(target, name == "Windows" ? "fastboot.exe" : "fastboot")))
                throw new InvalidDataException("Tool archive does not contain fastboot.");
            if (plan.Files.Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)))
                await PrepareZstdAsync(name, target, plan, progress, token).ConfigureAwait(false);
            TrimPlatformTools(name, target);
        }
        if (plan.Recovery && !plan.PackPayload && !plan.PackSuper
            && plan.Files.Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
            && !platforms.Any(p => p.Name == "Linux"))
            platforms.Add(("Linux", Linux));
        if ((plan.PackSuper && plan.OrganizeDirectory && plan.Super.Zstd)
            || (plan.Recovery && !plan.PackPayload && !plan.PackSuper
                && plan.Files.Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))))
        {
            var mappings = new[] { (Source: "zstd-arm64", Name: "zstd"), (Source: "zstd-arm64", Name: "zstd-arm64"),
                (Source: "zstd", Name: "zstd-x86_64"), (Source: "zstd-loong64", Name: "zstd-loong64") };
            var linuxDirectory = Path.Combine(directory, "bin", "Linux");
            if (mappings.Any(mapping => File.Exists(Path.Combine(linuxDirectory, mapping.Source))))
            {
                var androidDirectory = Path.Combine(directory, "bin", "android");
                Directory.CreateDirectory(androidDirectory);
                foreach (var mapping in mappings)
                {
                    var source = Path.Combine(linuxDirectory, mapping.Source);
                    if (File.Exists(source))
                        await DeliveryOutputService.CopyFileAsync(source, Path.Combine(androidDirectory, mapping.Name), token).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary><para>删除 platform-tools 包内交付脚本用不到的二进制，仅保留 fastboot 及其运行依赖。</para>
    /// Removes binaries the delivery scripts never use, keeping only fastboot and its runtime dependencies.</summary>
    internal static void TrimPlatformTools(string name, string target)
    {
        var unused = name == "Windows"
            ? new[] { "adb.exe", "etc1tool.exe", "hprof-conv.exe", "make_f2fs.exe", "make_f2fs_casefold.exe", "mke2fs.exe", "mke2fs.conf", "sqlite3.exe" }
            : new[] { "adb", "etc1tool", "hprof-conv", "make_f2fs", "make_f2fs_casefold", "mke2fs", "mke2fs.conf", "sqlite3" };
        foreach (var file in unused)
        {
            var path = Path.Combine(target, file);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary><para>为单个平台准备 zstd 解压工具：优先使用计划中的本地便携路径，否则下载钉住版本。</para>
    /// Prepares zstd for one platform: prefers the plan's local portable path, otherwise downloads the pinned release.</summary>
    private async Task PrepareZstdAsync(string name, string target, DeliveryPlan plan, IProgress<DeliveryProgress>? progress, CancellationToken token)
    {
        var local = plan.LocalZstd.GetValueOrDefault(name, "");
        var exe = name == "Windows" ? "zstd.exe" : "zstd";
        if (!string.IsNullOrWhiteSpace(local))
        {
            await DeliveryOutputService.CopyFileAsync(local, Path.Combine(target, exe), token).ConfigureAwait(false);
            SetExecutable(Path.Combine(target, exe));
            return;
        }
        progress?.Report(new(75, "Preparing tools", Stage: "Delivery_ProgressTools", Detail: name + " zstd"));
        if (name == "Windows")
        {
            var archive = await AcquireAsync(ZstdWindows, progress, token).ConfigureAwait(false);
            await ExtractAsync(archive, ZstdWindows.Prefix, target, token, ZstdWindows.Member).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(target, exe))) throw new InvalidDataException("zstd package does not contain zstd.exe.");
        }
        else if (name == "Linux")
        {
            await MaterializeRawAsync(ZstdLinux, target, exe, progress, token).ConfigureAwait(false);
            await MaterializeRawAsync(ZstdLinuxArm64, target, "zstd-arm64", progress, token).ConfigureAwait(false);
            await MaterializeRawAsync(ZstdLinuxLoong64, target, "zstd-loong64", progress, token).ConfigureAwait(false);
        }
        else
        {
            await MaterializeRawAsync(ZstdMacArm, target, exe, progress, token).ConfigureAwait(false);
            await MaterializeRawAsync(ZstdMacAmd64, target, "zstd-x86_64", progress, token).ConfigureAwait(false);
        }
    }

    private async Task MaterializeRawAsync(DeliveryToolPackage package, string target, string fileName, IProgress<DeliveryProgress>? progress, CancellationToken token)
    {
        var cached = await AcquireAsync(package, progress, token).ConfigureAwait(false);
        await DeliveryOutputService.CopyFileAsync(cached, Path.Combine(target, fileName), token).ConfigureAwait(false);
        SetExecutable(Path.Combine(target, fileName));
    }

    /// <summary>
    /// <para>取得（必要时下载并校验 SHA-256）指定工具包在缓存中的路径。源的选择与测速由
    /// Firmware.SharedKernel 的 <c>HttpDownload</c> 负责，从快到慢依次尝试，SHA-256 校验兜底。</para>
    /// Returns the cached path for a tool package, downloading and verifying its SHA-256 when needed. Source selection
    /// and speed probing are handled by <c>HttpDownload</c> in Firmware.SharedKernel; sources are tried fastest-first
    /// with a SHA-256 check as the final guard.
    /// </summary>
    internal async Task<string> AcquireAsync(DeliveryToolPackage package, IProgress<DeliveryProgress>? progress, CancellationToken token)
    {
        await CacheGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var cached = Path.Combine(cacheDirectory, package.Name + (package.Raw ? ".bin" : ".zip"));
            if (await MatchesAsync(cached, package.Sha256, token).ConfigureAwait(false)) return cached;
            var sources = await HttpDownload.SelectSourcesBySpeedAsync(new Uri(package.Url), client, token).ConfigureAwait(false);
            var errors = new List<string>();
            foreach (var source in sources)
            {
                var url = source.ToString();
                token.ThrowIfCancellationRequested();
                var temporary = cached + ".download-" + Guid.NewGuid().ToString("N");
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var length = response.Content.Headers.ContentLength;
                    if (length is > 256 * 1024 * 1024) throw new InvalidDataException("Tool archive exceeds 256 MiB.");
                    await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        var buffer = new byte[65536];
                        long readTotal = 0, last = 0;
                        int count;
                        while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                        {
                            readTotal += count;
                            if (readTotal > 256 * 1024 * 1024) throw new InvalidDataException("Tool archive exceeds 256 MiB.");
                            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                            if (Environment.TickCount64 - last >= 100)
                            {
                                progress?.Report(new(80, "Downloading tools", (readTotal, length ?? 0), "Delivery_ProgressDownload", Detail: package.Name));
                                last = Environment.TickCount64;
                            }
                        }
                        if (length is { } expected && readTotal != expected) throw new EndOfStreamException("Tool download was truncated.");
                    }
                    if (!await MatchesAsync(temporary, package.Sha256, token).ConfigureAwait(false)) throw new InvalidDataException("Tool checksum mismatch.");
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, cached, true);
                    return cached;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                {
                    errors.Add(new Uri(url).Host + ": " + ex.Message);
                }
                finally { File.Delete(temporary); }
            }
            throw new IOException("无法获取工具 / Tool download failed: " + package.Name + "\n" + string.Join("\n", errors));
        }
        finally { CacheGate.Release(); }
    }

    private static async Task<bool> MatchesAsync(string path, string hash, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        await using var input = File.OpenRead(path);
        if (input.Length > 256 * 1024 * 1024) return false;
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task ExtractAsync(string archive, string prefix, string target, CancellationToken token, string? member = null)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 10000) throw new InvalidDataException("Too many tool archive entries.");
        long total = 0;
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("Linked tool archive entry.");
            var relative = entry.FullName[prefix.Length..].Replace('\\', '/');
            if (relative.Length == 0 || relative.EndsWith('/')) continue;
            if (member is not null && !relative.Equals(member, StringComparison.OrdinalIgnoreCase)) continue;
            if (relative.Contains(':')) throw new InvalidDataException("Invalid tool path.");
            var path = Path.GetFullPath(Path.Combine(target, relative));
            if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Tool archive path escapes its directory.");
            total = checked(total + entry.Length);
            if (total > 256 * 1024 * 1024 || entry.Length > 64 * 1024 * 1024) throw new InvalidDataException("Expanded tool archive exceeds limits.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            var buffer = new byte[65536];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                written += count;
                if (written > entry.Length) throw new InvalidDataException("Tool entry length mismatch.");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (written != entry.Length) throw new EndOfStreamException("Tool entry truncated.");
            SetExecutable(path);
        }
    }

    private static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}