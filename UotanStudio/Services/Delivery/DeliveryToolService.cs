using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace UotanStudio.Services.Delivery;

internal sealed record DeliveryToolPackage(string Name, string Url, string Sha256, string Prefix);

/// <summary>Downloads pinned tools into a verified archive cache and materializes selected platforms.</summary>
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

    internal static IEnumerable<string> Sources(string url)
    {
        yield return url;
        if (!url.StartsWith("https://github.com/", StringComparison.Ordinal)) yield break;
        yield return "https://ghfast.top/" + url;
        yield return "https://gh-proxy.org/" + url;
        yield return "https://ghproxy.net/" + url;
    }

    public async Task PrepareAsync(DeliveryPlan plan, string directory, IProgress<DeliveryProgress>? progress, CancellationToken token)
    {
        var platforms = new List<(string Name, DeliveryToolPackage Package)>();
        if (plan.Windows) platforms.Add(("Windows", Windows));
        if (plan.Linux) platforms.Add(("Linux", Linux));
        if (plan.MacOS) platforms.Add(("macOS", MacOS));
        foreach (var (name, package) in platforms)
        {
            progress?.Report(new(75, "Preparing tools", Stage: "Delivery_ProgressTools", Detail: name));
            var target = Path.Combine(directory, "bin", name);
            var archive = await AcquireAsync(package, progress, token).ConfigureAwait(false);
            await ExtractAsync(archive, package.Prefix, target, token).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(target, name == "Windows" ? "fastboot.exe" : "fastboot")))
                throw new InvalidDataException("Tool archive does not contain fastboot.");
            if (plan.Files.Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)))
            {
                var local = plan.LocalZstd.GetValueOrDefault(name, "");
                if (string.IsNullOrWhiteSpace(local))
                    throw new FileNotFoundException($"请指定 {name} 的便携 zstd 工具 / Select a portable {name} zstd executable.");
                await DeliveryOutputService.CopyFileAsync(local, Path.Combine(target, name == "Windows" ? "zstd.exe" : "zstd"), token).ConfigureAwait(false);
                SetExecutable(Path.Combine(target, name == "Windows" ? "zstd.exe" : "zstd"));
            }
        }
    }

    internal async Task<string> AcquireAsync(DeliveryToolPackage package, IProgress<DeliveryProgress>? progress, CancellationToken token)
    {
        await CacheGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var cached = Path.Combine(cacheDirectory, package.Name + ".zip");
            if (await MatchesAsync(cached, package.Sha256, token).ConfigureAwait(false)) return cached;
            var errors = new List<string>();
            foreach (var url in Sources(package.Url))
            {
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

    internal static async Task ExtractAsync(string archive, string prefix, string target, CancellationToken token)
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
