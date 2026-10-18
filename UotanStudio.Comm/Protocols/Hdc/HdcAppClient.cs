using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// Native daemon app transfer and bundle-manager completion messages.
/// <para>原生守护进程应用传输与 bundle-manager 完成消息。</para>
/// </summary>
/// <param name="connection">Established HDC connection used to open the app task channels. <para>用于打开应用任务通道的已建立 HDC 连接。</para></param>
public sealed class HdcAppClient(HdcConnection connection) : Contracts.IHdcAppClient
{
    /// <summary>
    /// Transfers and installs one HAP/HSP/APP package using AppCheck/Begin/Data/Finish.
    /// <para>使用 AppCheck/Begin/Data/Finish 传输并安装单个 HAP/HSP/APP 包。</para>
    /// </summary>
    /// <param name="path">Local package file, or a directory that is installed via <see cref="InstallDirectory"/>. <para>本地包文件；若为目录则经 <see cref="InstallDirectory"/> 安装。</para></param>
    /// <param name="cancellationToken">Cancels before any device exchange begins. <para>在任何设备交换开始前取消。</para></param>
    /// <returns>The bundle-manager completion text on success. <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException">The file extension is not <c>.hap</c>, <c>.hsp</c> or <c>.app</c>. <para>文件扩展名不是 <c>.hap</c>、<c>.hsp</c> 或 <c>.app</c>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="IOException">The daemon reported a failed install result. <para>守护进程报告安装结果失败。</para></exception>
    public string Install(string path, CancellationToken cancellationToken = default)
        => Install(path, "", cancellationToken);

    /// <summary>
    /// Installs a package with bundle-manager options such as -r (replace while preserving data).
    /// <para>安装带 bundle-manager 选项（如 -r：覆盖安装并保留数据）的包。</para>
    /// </summary>
    /// <param name="path">Local package file, or a directory that is installed via <see cref="InstallDirectory"/>. <para>本地包文件；若为目录则经 <see cref="InstallDirectory"/> 安装。</para></param>
    /// <param name="options">Bundle-manager option string; only ASCII letters, digits, space, <c>-</c>, <c>_</c> and <c>.</c> are accepted. <para>bundle-manager 选项串；仅接受 ASCII 字母、数字、空格、<c>-</c>、<c>_</c> 与 <c>.</c>。</para></param>
    /// <param name="cancellationToken">Cancels before any device exchange begins. <para>在任何设备交换开始前取消。</para></param>
    /// <returns>The bundle-manager completion text on success. <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException">The extension is unsupported or <paramref name="options"/> contains rejected characters. <para>扩展名不受支持，或 <paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>. <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="IOException">The daemon reported a failed install result. <para>守护进程报告安装结果失败。</para></exception>
    public string Install(string path, string options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);
        if (Directory.Exists(path)) return InstallDirectory(path, options, cancellationToken);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".hap" or ".hsp" or ".app")) throw new ArgumentException("Unsupported HDC package extension.", nameof(path));
        return InstallPackage(path, options, cancellationToken);
    }

    /// <summary>
    /// Installs distinct package paths in the supplied order, stopping at the first failure.
    /// <para>按给定顺序安装互不重复的包路径，遇到第一个失败即停止。</para>
    /// </summary>
    /// <param name="paths">Package files or directories; duplicates are collapsed by full path (case-insensitive on Windows) and the batch is capped at 256 entries. <para>包文件或目录；按完整路径去重（Windows 上不区分大小写），批次上限 256 项。</para></param>
    /// <param name="options">Bundle-manager option string validated exactly as in <see cref="Install(string, string, CancellationToken)"/>. <para>bundle-manager 选项串，校验规则与 <see cref="Install(string, string, CancellationToken)"/> 完全一致。</para></param>
    /// <param name="cancellationToken">Checked before each input is normalized and before each install. <para>在每个输入规范化之前以及每次安装之前检查。</para></param>
    /// <returns>One completion-text entry per installed package, in install order. <para>按安装顺序为每个已安装包返回一条完成文本。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is <see langword="null"/>. <para><paramref name="paths"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The batch is empty, exceeds 256 inputs, or <paramref name="options"/> contains rejected characters. <para>批次为空、输入超过 256 项，或 <paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="FileNotFoundException">An input path exists as neither file nor directory. <para>某个输入路径既不是文件也不是目录。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public IReadOnlyList<string> Install(IEnumerable<string> paths, string options = "", CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        ArgumentNullException.ThrowIfNull(paths);
        var packages = new List<string>();
        var inputCount = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inputCount > 256) throw new ArgumentException("HDC install batch exceeds 256 packages.", nameof(paths));
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("HDC install input not found.", full);
            if (!packages.Contains(full, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) packages.Add(full);
        }
        if (packages.Count == 0) throw new ArgumentException("HDC install batch is empty.", nameof(paths));
        return [.. packages.Select(path => Install(path, options, cancellationToken))];
    }

    /// <summary>
    /// Installs a bounded package directory as one upstream TAR task so related modules are submitted together.
    /// <para>将有界的包目录作为单个上游 TAR 任务安装，使相关模块一起提交。</para>
    /// </summary>
    /// <param name="path">Directory scanned recursively for <c>.hap</c>, <c>.hsp</c> and <c>.app</c> files; links are never followed. <para>递归扫描 <c>.hap</c>、<c>.hsp</c>、<c>.app</c> 文件的目录；绝不跟随链接。</para></param>
    /// <param name="options">Bundle-manager option string validated exactly as in <see cref="Install(string, string, CancellationToken)"/>. <para>bundle-manager 选项串，校验规则与 <see cref="Install(string, string, CancellationToken)"/> 完全一致。</para></param>
    /// <param name="cancellationToken">Checked during enumeration and TAR writing; the temporary TAR file is deleted either way. <para>在枚举与写入 TAR 期间检查；临时 TAR 文件无论如何都会删除。</para></param>
    /// <returns>The bundle-manager completion text for the single TAR transfer. <para>单个 TAR 传输的 bundle-manager 完成文本。</para></returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="path"/> is not an existing directory. <para><paramref name="path"/> 不是现有目录。</para></exception>
    /// <exception cref="IOException">A reparse point was hit, the 100000-entry, 256-package or 4 GiB budget was exceeded, no package was found, or the daemon reported failure. <para>遇到重分析点、超过 100000 条目 / 256 包 / 4 GiB 预算、未找到包，或守护进程报告失败。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> contains rejected characters. <para><paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public string InstallDirectory(string path, string options = "", CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var root = new DirectoryInfo(path);
        if (!root.Exists) throw new DirectoryNotFoundException(path);
        var pending = new Stack<DirectoryInfo>(); pending.Push(root);
        var packages = new List<FileInfo>();
        long size = 0;
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("HDC install does not follow links.");
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 100000) throw new IOException("HDC install directory entry limit exceeded.");
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("HDC install does not follow links.");
                if (entry is DirectoryInfo child) pending.Push(child);
                else if (entry is FileInfo file && file.Extension.ToLowerInvariant() is ".hap" or ".hsp" or ".app")
                {
                    size = checked(size + file.Length);
                    if (size > 4L * 1024 * 1024 * 1024 || packages.Count >= 256) throw new IOException("HDC install package budget exceeded.");
                    packages.Add(file);
                }
            }
        }
        if (packages.Count == 0) throw new IOException("HDC install directory contains no package.");
        var tar = Path.Combine(Path.GetTempPath(), "uotan-hdc-install-" + Guid.NewGuid().ToString("N") + ".tar");
        try
        {
            using (var output = new FileStream(tar, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new System.Formats.Tar.TarWriter(output, System.Formats.Tar.TarEntryFormat.Ustar))
                foreach (var package in packages.OrderBy(p => p.FullName, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var input = package.OpenRead();
                    if (input.Length != package.Length) throw new IOException("HDC package changed during preparation.");
                    var entry = new System.Formats.Tar.UstarTarEntry(System.Formats.Tar.TarEntryType.RegularFile,
                        Path.GetRelativePath(root.FullName, package.FullName).Replace('\\', '/'))
                    { DataStream = input };
                    writer.WriteEntry(entry);
                }
            cancellationToken.ThrowIfCancellationRequested();
            return InstallPackage(tar, options, cancellationToken);
        }
        finally { File.Delete(tar); }
    }

    private string InstallPackage(string path, string options, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        using var input = File.OpenRead(path);
        var config = new TransferConfig { FileSize = input.Length, OptionalName = Guid.NewGuid().ToString("N") + extension, HoldTimestamp = false, FunctionName = "install", Options = options };
        using var channel = connection.OpenChannel(HdcCommand.AppCheck, HdcTransferCodec.EncodeConfig(config));
        HdcSyncClient.Expect(channel, HdcCommand.AppBegin, cancellationToken);
        using var transfer = new HdcSyncClient(connection);
        transfer.SendData(channel, input, input.Length, HdcCommand.AppData, cancellationToken);
        return ReadResult(channel, 1, cancellationToken);
    }

    /// <summary>Installs the signed modules in an APP container that match a device type, without re-signing them.
    /// <para>安装 APP 容器中与目标设备类型匹配的已签名模块，不进行重新签名。</para></summary>
    /// <param name="path">APP (ZIP) container path opened read-only.
    /// <para>以只读方式打开的 APP（ZIP）容器路径。</para></param>
    /// <param name="deviceType">Device type matched against the <c>deviceType</c> arrays in <c>pack.info</c>.
    /// <para>与 <c>pack.info</c> 中各 <c>deviceType</c> 数组匹配的机型代号。</para></param>
    /// <param name="options">Bundle-manager option string validated exactly as in <see cref="Install(string, string, CancellationToken)"/>.
    /// <para>bundle-manager 选项串，校验规则与 <see cref="Install(string, string, CancellationToken)"/> 完全一致。</para></param>
    /// <param name="cancellationToken">Checked while extracting modules; the staging directory is deleted either way.
    /// <para>解压模块期间检查；暂存目录无论如何都会删除。</para></param>
    /// <returns>The bundle-manager completion text for the single-module install or the combined TAR transfer.
    /// <para>单模块安装或合并 TAR 传输的 bundle-manager 完成文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="deviceType"/> is blank or <paramref name="options"/> contains rejected characters.
    /// <para><paramref name="deviceType"/> 为空，或 <paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="InvalidDataException">The container, its <c>pack.info</c> metadata or a module entry violates the size, naming or uniqueness constraints.
    /// <para>容器、其 <c>pack.info</c> 元数据或模块条目违反大小、命名或唯一性约束。</para></exception>
    /// <exception cref="NotSupportedException">The container has no installable module for <paramref name="deviceType"/>.
    /// <para>容器中没有匹配 <paramref name="deviceType"/> 的可安装模块。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public string InstallAppBundle(string path, string deviceType, string options = "", CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipFile.OpenRead(path);
        var selected = SelectAppModules(archive, deviceType);
        var staging = Path.Combine(Path.GetTempPath(), "uotan-hdc-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var entry in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = entry.Open();
                using var output = File.Create(Path.Combine(staging, entry.Name));
                var buffer = new byte[65536];
                long copied = 0;
                int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    copied = checked(copied + read);
                    if (copied > entry.Length) throw new InvalidDataException("APP module exceeds its declared length.");
                    output.Write(buffer, 0, read);
                }
                if (copied != entry.Length) throw new InvalidDataException("Truncated APP module.");
            }
            if (selected.Count == 1) return InstallPackage(Path.Combine(staging, selected[0].Name), options, cancellationToken);
            var tar = Path.Combine(staging, "modules.tar");
            using (var output = File.Create(tar))
            using (var writer = new System.Formats.Tar.TarWriter(output, System.Formats.Tar.TarEntryFormat.Ustar))
                foreach (var entry in selected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteEntry(Path.Combine(staging, entry.Name), entry.Name);
                }
            return InstallPackage(tar, options, cancellationToken);
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    internal static List<ZipArchiveEntry> SelectAppModules(ZipArchive archive, string deviceType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceType);
        if (archive.Entries.Count > 1024) throw new InvalidDataException("Too many APP entries.");
        var metadata = archive.Entries.SingleOrDefault(e => e.FullName == "pack.info")
            ?? throw new InvalidDataException("APP package has no pack.info.");
        if (metadata.Length > 4 * 1024 * 1024) throw new InvalidDataException("APP metadata exceeds limit.");
        using var input = metadata.Open();
        var bytes = new byte[checked((int)metadata.Length)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("APP metadata exceeds declared size.");
        using var json = JsonDocument.Parse(bytes);
        var selected = new List<ZipArchiveEntry>();
        long total = 0;
        foreach (var package in json.RootElement.GetProperty("packages").EnumerateArray())
        {
            if (!package.GetProperty("deviceType").EnumerateArray().Any(t => t.GetString() == deviceType)) continue;
            if (package.TryGetProperty("deliveryWithInstall", out var delivery) && !delivery.GetBoolean()) continue;
            var name = package.GetProperty("name").GetString() ?? throw new InvalidDataException("Missing APP module name.");
            if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) || name.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe APP module name.");
            var entry = archive.Entries.SingleOrDefault(e => e.FullName == name + ".hap" || e.FullName == name + ".hsp")
                ?? throw new InvalidDataException("Missing APP module: " + name);
            total = checked(total + entry.Length);
            if (total > 4L * 1024 * 1024 * 1024 || selected.Count >= 256 || selected.Contains(entry))
                throw new InvalidDataException("APP module count, size or uniqueness constraint failed.");
            selected.Add(entry);
        }
        if (selected.Count == 0) throw new NotSupportedException("APP contains no installable module for device type " + deviceType);
        return selected;
    }

    private static void ValidateOptions(string options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')))
            throw new ArgumentException("Invalid bundle-manager options.", nameof(options));
    }

    /// <summary>
    /// Uninstalls the exact named bundle.
    /// <para>卸载指定精确名称的 bundle。</para>
    /// </summary>
    /// <param name="bundle">Bundle name; only ASCII letters, digits, <c>.</c> and <c>_</c> are accepted. <para>bundle 名称；仅接受 ASCII 字母、数字、<c>.</c> 与 <c>_</c>。</para></param>
    /// <param name="cancellationToken">Cancels before the uninstall task is opened. <para>在卸载任务打开前取消。</para></param>
    /// <returns>The bundle-manager completion text on success. <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="bundle"/> is blank or contains rejected characters. <para><paramref name="bundle"/> 为空或含被拒绝的字符。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="IOException">The daemon reported a failed uninstall result. <para>守护进程报告卸载结果失败。</para></exception>
    public string Uninstall(string bundle, CancellationToken cancellationToken = default)
        => Uninstall(bundle, "", cancellationToken);

    /// <summary>
    /// Uninstalls a bundle with options such as -k (keep data), without changing default behavior.
    /// <para>带选项（如 -k：保留数据）卸载 bundle，不改变默认行为。</para>
    /// </summary>
    /// <param name="bundle">Bundle name; only ASCII letters, digits, <c>.</c> and <c>_</c> are accepted. <para>bundle 名称；仅接受 ASCII 字母、数字、<c>.</c> 与 <c>_</c>。</para></param>
    /// <param name="options">Bundle-manager option string; when empty only the bundle name is sent. <para>bundle-manager 选项串；为空时仅发送 bundle 名称。</para></param>
    /// <param name="cancellationToken">Cancels before the uninstall task is opened. <para>在卸载任务打开前取消。</para></param>
    /// <returns>The bundle-manager completion text on success. <para>成功时返回 bundle-manager 的完成文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="bundle"/> or <paramref name="options"/> contains rejected characters. <para><paramref name="bundle"/> 或 <paramref name="options"/> 含被拒绝的字符。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="IOException">The daemon reported a failed uninstall result. <para>守护进程报告卸载结果失败。</para></exception>
    public string Uninstall(string bundle, string options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundle);
        if (bundle.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_')))
            throw new ArgumentException("Invalid bundle name.", nameof(bundle));
        using var channel = connection.OpenChannel(HdcCommand.AppUninstall, Encoding.UTF8.GetBytes(options.Length == 0 ? bundle : options + " " + bundle));
        return ReadResult(channel, 2, cancellationToken);
    }

    private static string ReadResult(HdcStream channel, byte expectedMode, CancellationToken ct)
    {
        var finish = HdcSyncClient.Expect(channel, HdcCommand.AppFinish, ct, timeoutMs: 300000);
        var payload = finish.Payload ?? [];
        if (payload.Length < 2) throw new InvalidDataException("Truncated HDC app result.");
        if (payload[0] != expectedMode) throw new InvalidDataException("Unexpected HDC app result mode.");
        var text = Encoding.UTF8.GetString(payload.AsSpan(2));
        var vendorSuccess = payload[1] == 0 && text.Trim().Equals(
            expectedMode == 1 ? "install bundle successfully." : "uninstall bundle successfully.", StringComparison.Ordinal);
        if ((payload[1] != 1 && !vendorSuccess) || text.TrimStart().StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"HDC application operation failed (status {payload[1]}): " + text);
        return text;
    }
}