using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Infrastructure.Downloads;

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
    /// <summary>
    /// One download gate per pinned package name, held across the cache check, the source speed probe, the download,
    /// the post-download digest check and the atomic publish so two builds racing for the same package never both
    /// fetch and publish it. The dictionary is static because <see cref="DeliveryService"/> hands each build a fresh
    /// <see cref="DeliveryToolService"/> over the same shared cache directory, and the pinned package set is finite,
    /// so the key space stays bounded. Two instances pointing at different cache directories contend on one key only
    /// because they name the same package, which costs an extra wait and never corrupts either cache.
    /// <para>按钉住的包名各配一把下载门，并在缓存校验、源站测速、下载、下载后摘要校验与原子发布期间一直持有，
    /// 因此两个并发构建不会重复下载并发布同一个包。字典是静态的，因为 <see cref="DeliveryService"/> 每次构建都会新建一个
    /// 指向同一份共享缓存目录的 <see cref="DeliveryToolService"/>；包名集合有限，键空间不会无界增长。两个实例即便指向不同的
    /// 缓存目录，也只因同名的包而共用一把门，代价只是多等一次，不会损坏任何一份缓存。</para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Ceiling on how many platforms <see cref="PrepareAsync"/> stages at once. Two is enough to overlap one platform's
    /// download with another platform's archive extraction while keeping the shared staging tree's disk access bounded;
    /// each platform still extracts, prepares zstd and trims sequentially into its own <c>bin/&lt;name&gt;</c> directory.
    /// <para><see cref="PrepareAsync"/> 同时暂存的平台数上限。取 2 足以让一个平台的下载与另一个平台的解压重叠，
    /// 又能约束共享暂存树的磁盘访问；每个平台仍是顺序解压、准备 zstd 并裁剪，写入自己的 <c>bin/&lt;name&gt;</c> 目录。</para>
    /// </summary>
    private const int MaximumConcurrentPlatforms = 2;

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
    /// 并按 Recovery/组织式 Super 的约定把所需 Linux 静态 zstd 别名物化到 bin/android。平台条目按
    /// <see cref="MaximumConcurrentPlatforms"/> 有界并发暂存，每个条目内部的下载、解压、zstd 准备与裁剪仍按顺序执行。</para>
    /// Prepares platform-tools and, when compressed images are selected, zstd for each enabled platform, trims unused
    /// binaries, and materializes the Linux static zstd aliases into bin/android per the Recovery/organized Super
    /// convention. Platform entries are staged concurrently up to <see cref="MaximumConcurrentPlatforms"/> while the
    /// download, extraction, zstd preparation and trimming inside one entry stay sequential.
    /// </summary>
    /// <param name="plan">
    /// Plan selecting the enabled platforms and deciding whether zstd is needed: files ending in
    /// <c>.zst</c>, an organized Zstd Super, or a Recovery-only delivery with ZST files also stage the
    /// Linux toolset.
    /// <para>决定启用平台的交付计划；以 <c>.zst</c> 结尾的文件、组织式 Zstd Super，或带 ZST 文件的纯 Recovery
    /// 都会额外暂存 Linux 工具集并准备 zstd。</para>
    /// </param>
    /// <param name="directory">
    /// Staging root; each platform lands in <c>bin/Windows</c>, <c>bin/Linux</c> or <c>bin/macOS</c> and the
    /// Linux zstd aliases in <c>bin/android</c>.
    /// <para>暂存根目录；各平台分别落到 <c>bin/Windows</c>、<c>bin/Linux</c> 或 <c>bin/macOS</c>，
    /// Linux 的 zstd 别名落到 <c>bin/android</c>。</para>
    /// </param>
    /// <param name="progress">
    /// Sink for the <c>Delivery_ProgressTools</c> stage reports at 75 percent, one per platform and per
    /// zstd download; <see langword="null"/> skips reporting.
    /// <para>接收 75% 的 <c>Delivery_ProgressTools</c> 阶段上报，每个平台与每次 zstd 下载各一条；
    /// 为 <see langword="null"/> 时不上报。</para>
    /// </param>
    /// <param name="token">
    /// Cancellation token threaded through the downloads, archive extraction and file copies.
    /// <para>贯穿下载、归档解压与文件复制的取消令牌。</para>
    /// </param>
    public async Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token)
    {
        var platforms = new List<(string Name, DeliveryToolPackage Package)>();
        if (plan.Windows) platforms.Add(("Windows", Windows));
        if (plan.Linux) platforms.Add(("Linux", Linux));
        if (plan.MacOS) platforms.Add(("macOS", MacOS));
        if (plan.PackSuper && plan.OrganizeDirectory && plan.Super.Zstd && !plan.Linux)
            platforms.Add(("Linux", Linux));
        await PreparePlatformsAsync(platforms, directory, plan, progress, token).ConfigureAwait(false);
        if (plan.Recovery && !plan.PackPayload && !plan.PackSuper
            && plan.Files.Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
            && !platforms.Any(p => p.Name == "Linux"))
            platforms.Add(("Linux", Linux));
        if ((plan.PackSuper && plan.OrganizeDirectory && plan.Super.Zstd)
            || (plan.Recovery && !plan.PackPayload && !plan.PackSuper
                && plan.Files.Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))))
        {
            (string Source, string Name)[] mappings = [("zstd-arm64", "zstd"), ("zstd-arm64", "zstd-arm64"),
                ("zstd", "zstd-x86_64"), ("zstd-loong64", "zstd-loong64")];
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

    /// <summary>
    /// Stages the resolved platform entries concurrently, bounded by <see cref="MaximumConcurrentPlatforms"/>. Entries
    /// land in distinct <c>bin/&lt;name&gt;</c> directories, so extraction, zstd staging and trimming never write the
    /// same file from two entries at once; the per-package download gates keep the shared cache safe. Every entry is
    /// awaited before this returns, so the Linux zstd alias mapping performed afterwards sees a complete
    /// <c>bin/Linux</c>. A failing entry does not cancel its siblings: they finish, and the first failure then escapes
    /// as the aggregate exception, which the caller's staging cleanup already treats as an aborted delivery.
    /// <para>按 <see cref="MaximumConcurrentPlatforms"/> 有界并发暂存已解析出的平台条目。各条目写入互不相同的
    /// <c>bin/&lt;name&gt;</c> 目录，因此解压、zstd 暂存与裁剪不会有两个条目同时写同一份文件；共享缓存的安全仍由按包名
    /// 分开的下载门保证。返回前会等待所有条目，因此随后执行的 Linux zstd 别名映射看到的 <c>bin/Linux</c> 是完整的。
    /// 某个条目失败不会取消其余条目：它们会跑完，随后第一个失败以聚合异常抛出，而调用方的暂存清理本就按中止交付处理。</para>
    /// </summary>
    /// <param name="platforms">
    /// Platform entries to stage, each with the directory name under <c>bin/</c> and the pinned package to fetch.
    /// <para>待暂存的平台条目，各自带有 <c>bin/</c> 下的目录名与需要取回的钉住工具包。</para>
    /// </param>
    /// <param name="directory">Staging root holding the per-platform <c>bin/</c> layout.
    /// <para>持有各平台 <c>bin/</c> 布局的暂存根目录。</para></param>
    /// <param name="plan">Plan deciding whether zstd is staged alongside platform-tools.
    /// <para>决定是否随 platform-tools 一起暂存 zstd 的交付计划。</para></param>
    /// <param name="progress">Sink receiving one <c>Delivery_ProgressTools</c> report per platform when that platform
    /// starts, so interleaved reports still name exactly one platform in <c>Detail</c>.
    /// <para>每个平台开始启动时收到一条 <c>Delivery_ProgressTools</c> 上报，交错的上报里 <c>Detail</c> 仍只点名一个平台。</para></param>
    /// <param name="token">Cancellation token threaded through every platform entry.
    /// <para>贯穿每个平台条目的取消令牌。</para></param>
    internal async Task PreparePlatformsAsync(IReadOnlyList<(string Name, DeliveryToolPackage Package)> platforms,
        string directory, DeliveryPlan plan, IProgress<DeliveryProgress>? progress, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(MaximumConcurrentPlatforms, MaximumConcurrentPlatforms);
        var entries = new Task[platforms.Count];
        for (var index = 0; index < platforms.Count; index++)
        {
            entries[index] = PreparePlatformAsync(platforms[index].Name, platforms[index].Package, directory, plan, progress, gate, token);
        }

        await Task.WhenAll(entries).ConfigureAwait(false);
    }

    private async Task PreparePlatformAsync(string name, DeliveryToolPackage package, string directory, DeliveryPlan plan,
        IProgress<DeliveryProgress>? progress, SemaphoreSlim gate, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
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
        finally { gate.Release(); }
    }

    /// <summary>
    /// <para>删除 platform-tools 包内交付脚本用不到的二进制，仅保留 fastboot 及其运行依赖。</para>
    /// Removes binaries the delivery scripts never use, keeping only fastboot and its runtime dependencies.</summary>
    internal static void TrimPlatformTools(string name, string target)
    {
        string[] unused = name == "Windows"
            ? ["adb.exe", "etc1tool.exe", "hprof-conv.exe", "make_f2fs.exe", "make_f2fs_casefold.exe", "mke2fs.exe", "mke2fs.conf", "sqlite3.exe"]
            : ["adb", "etc1tool", "hprof-conv", "make_f2fs", "make_f2fs_casefold", "mke2fs", "mke2fs.conf", "sqlite3"];
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
            DeliveryUnixPermissions.SetExecutable(Path.Combine(target, exe));
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
        DeliveryUnixPermissions.SetExecutable(Path.Combine(target, fileName));
    }

    /// <summary>
    /// <para>取得（必要时下载并校验 SHA-256）指定工具包在缓存中的路径。源的选择与测速由
    /// Firmware.Core 的 <c>HttpDownload</c> 负责，从快到慢依次尝试，SHA-256 校验兜底。同一包名的并发调用被
    /// <see cref="CacheGates"/> 串行，不同包名互不阻塞。</para>
    /// Returns the cached path for a tool package, downloading and verifying its SHA-256 when needed. Source selection
    /// and speed probing are handled by <c>HttpDownload</c> in Firmware.Core; sources are tried fastest-first with a
    /// SHA-256 check as the final guard. Concurrent calls for one package name are serialized by
    /// <see cref="CacheGates"/> while different package names never wait on each other.
    /// </summary>
    internal async Task<string> AcquireAsync(DeliveryToolPackage package, IProgress<DeliveryProgress>? progress, CancellationToken token)
    {
        var gate = CacheGates.GetOrAdd(package.Name, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
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
                    AtomicPublish.Publish(temporary, cached);
                    return cached;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                {
                    errors.Add(new Uri(url).Host + ": " + ex.Message);
                }
                finally { File.Delete(temporary); }
            }
            throw new IOException("Tool download failed: " + package.Name + "\n" + string.Join("\n", errors));
        }
        finally { gate.Release(); }
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
            DeliveryUnixPermissions.SetExecutable(path);
        }
    }
}